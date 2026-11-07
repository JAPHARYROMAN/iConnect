using IConnect.Core;
using IConnect.Protocol;

namespace IConnect.Storage.Windows;

public sealed partial class WindowsBatchPlans
{
    private static readonly TimeSpan PublishedBatchCheckLifetime = TimeSpan.FromSeconds(30);
    private sealed class PublishedBatchCheckWork(Row row, string epoch, string scope, string challenge,
        CancellationTokenSource cancellation, IDisposable slot)
    {
        internal Row Row { get; } = row;
        internal string Epoch { get; } = epoch;
        internal string Scope { get; } = scope;
        internal string Challenge { get; } = challenge;
        internal CancellationTokenSource Cancellation { get; } = cancellation;
        internal CancellationToken Token { get; } = cancellation.Token;
        internal IDisposable Slot { get; } = slot;
        internal TaskCompletionSource Collected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Task { get; set; } = Task.CompletedTask;
        internal long? FinishedAt { get; set; }
        internal Action? Validate { get; set; }
    }
    private readonly Dictionary<string, PublishedBatchCheckWork> publishedBatchChecks = [];
    private readonly Dictionary<string, PublishedBatchCheckWork> publishedBatchWork = [];

    private FileResponse VerifyPublishedBatch(Row row, string scope, FileRequest request, FileResponse response, CancellationToken ct)
    {
        var header = Header(row);
        if (scope.Length == 0 || !UploadBatchRules.CanonicalToken(request.MutationId) || request.BatchHeader is null || !request.BatchHeader.Equals(header) ||
            request.HasBatchItemIndex || request.Upload is not null || request.Content.Length != 0 || request.Offset != 0 || request.Length != 0 ||
            request.NodeId.Length != 0 || request.DestinationId.Length != 0 || request.Name.Length != 0 || request.ExpectedVersion.Length != 0 ||
            request.Continuation.Length != 0 || request.Query.Length != 0 || request.DeletedId.Length != 0)
            throw new StorageException(FileStatus.InvalidRequest, "Use a fresh challenge and the original whole-batch intent.");
        if (row.Phase != UploadBatchPlanPhase.UploadPlanCompleted || row.Retiring || row.Retired ||
            row.Outcomes is not { Length: > 0 } || row.Outcomes.Any(x => x != 1))
            throw new StorageException(FileStatus.Conflict, "Choose a fully successful completed batch with retained item history.");

        foreach (var pair in publishedBatchChecks.Where(x => x.Value.FinishedAt is { } end &&
                     grants.Clock.GetElapsedTime(end) >= PublishedBatchCheckLifetime).ToArray())
        { publishedBatchChecks.Remove(pair.Key); ReleasePublishedBatchCheck(pair.Value); }
        if (publishedBatchChecks.TryGetValue(header.Id, out var previous))
        {
            if (previous.Scope != scope || previous.Challenge != request.MutationId || previous.Row != row || previous.Epoch != context.Epoch)
                throw new StorageException(FileStatus.Conflict, "A different check for this batch is awaiting completion or collection.");
            if (previous.FinishedAt is not null)
            {
                try
                {
                    if (previous.Validate is null || previous.Token.IsCancellationRequested)
                        throw new StorageException(FileStatus.Conflict, "The saved batch could not be verified. Its copies and receipts were kept.");
                    try { previous.Validate(); }
                    catch (Exception ex) when (StateRecovery.IsRecordFailure(ex))
                    { throw new StorageException(FileStatus.Conflict, "The saved batch changed before its check was collected. Its copies were kept."); }
                    response.PublishedBatchCheck = new() { Challenge = previous.Challenge, Verified = true };
                    return response;
                }
                finally { publishedBatchChecks.Remove(header.Id); ReleasePublishedBatchCheck(previous); }
            }
        }
        else
        {
            if (publishedBatchWork.ContainsKey(header.Id))
                throw new StorageException(FileStatus.Conflict, "The previous batch check is still releasing its resources.");
            var slot = grants.ReserveIncomingVerification() ??
                throw new StorageException(FileStatus.ResourceExhausted, "Incoming verification is busy. Retry shortly.");
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cancellation.CancelAfter(TimeSpan.FromMinutes(10) + PublishedBatchCheckLifetime);
            var check = new PublishedBatchCheckWork(row, context.Epoch, scope, request.MutationId, cancellation, slot);
            publishedBatchChecks.Add(header.Id, check); publishedBatchWork.Add(header.Id, check);
            check.Task = Task.Run(() => CheckPublishedBatchAsync(check));
        }
        response.PublishedBatchCheck = new() { Challenge = request.MutationId };
        return response;
    }

    private async Task CheckPublishedBatchAsync(PublishedBatchCheckWork check)
    {
        var row = check.Row; var id = Header(row).Id; var token = check.Token;
        var opened = new List<SecureWindowsDirectory>();
        try
        {
            var snapshot = Current(() =>
            {
                RequirePublishedBatchContext();
                var inventory = Inventory(row);
                var names = new string[inventory.Manifest.Items.Count];
                var identities = new string[names.Length];
                for (var index = 0; index < names.Length; index++)
                {
                    token.ThrowIfCancellationRequested();
                    if (!inventory.Children.TryGetValue((uint)index, out var child)) throw new IOException("A completed child receipt is missing.");
                    if (child.IsDirectory)
                    {
                        if (child.Phase != UploadBatchItemPhase.BatchItemSucceeded) throw new IOException("A directory did not succeed.");
                        names[index] = child.Name; identities[index] = child.Identity;
                    }
                    else
                    {
                        var binding = FileInventory(row, inventory.Manifest, child, inventory.Children).Scope;
                        var saved = WindowsUploads.InspectScopedPublished(binding, context.Owner);
                        names[index] = saved.Info.FinalName; identities[index] = saved.Identity;
                    }
                }
                return (inventory.Manifest, inventory.Children, Names: names, Identities: identities, Files: RetirementFiles(row));
            });
            using var destination = Current(() => SecureWindowsDirectory.OpenPath(row.Target.CanonicalRoot, row.Target.RootIdentity, row.Target.Path, true));
            if (destination.Info.Identity != row.Target.Identity) throw new IOException("The destination changed.");
            var paths = new string[snapshot.Names.Length][];
            for (var index = 0; index < snapshot.Names.Length; index++)
            {
                var item = snapshot.Manifest.Items[index];
                var parent = item.Parent < 0 ? destination : opened[item.Parent];
                paths[index] = item.Parent < 0 ? [.. row.Target.Path, snapshot.Names[index]] : [.. paths[item.Parent], snapshot.Names[index]];
                var entry = Current(() =>
                {
                    grants.ValidateAccess(grants.Get(row.Target.Grant, row.Peer), paths[index]);
                    if (parent.Info.Identity != snapshot.Children[(uint)index].ParentIdentity) throw new IOException("A recorded parent changed.");
                    return parent.OpenChild(snapshot.Names[index], item.IsDirectory);
                });
                opened.Add(entry);
                if (entry.Info.Identity != snapshot.Identities[index]) throw new IOException("A saved batch item was replaced.");
                if (!item.IsDirectory)
                    WindowsDownloadManager.VerifyFile(entry, item.Size, Convert.ToHexStringLower(item.Sha256.Span), token);
            }
            var children = snapshot.Manifest.Items.Select((item, index) => (item, index)).ToLookup(x => x.item.Parent);
            void Validate()
            {
                RequireCurrent(); RequirePublishedBatchContext();
                if (!RetirementFiles(row).SequenceEqual(snapshot.Files)) throw new IOException("The saved item receipts changed.");
                var grant = grants.Get(row.Target.Grant, row.Peer);
                for (var index = 0; index < opened.Count; index++)
                {
                    token.ThrowIfCancellationRequested(); grants.ValidateAccess(grant, paths[index]);
                    var item = snapshot.Manifest.Items[index];
                    var current = SecureWindowsDirectory.Inspect(opened[index].Handle);
                    if (current.Identity != snapshot.Identities[index] || current.IsDirectory != item.IsDirectory ||
                        (current.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
                        throw new IOException("A saved item's identity or directory metadata changed before collection.");
                    if (!snapshot.Manifest.Items[index].IsDirectory)
                    {
                        if (current.Size != item.Size || current.Links != 1)
                            throw new IOException("A saved file's metadata changed before collection.");
                        continue;
                    }
                    var expected = children[index].Select(x => (snapshot.Names[x.index], x.item.IsDirectory)).ToHashSet();
                    foreach (var entry in opened[index].EnumerateNames(token).Take(expected.Count + 1))
                        if (!expected.Remove((entry.Name, entry.IsDirectory))) throw new IOException("A saved folder has an unexpected entry.");
                    if (expected.Count != 0) throw new IOException("A saved folder has a missing entry.");
                }
            }
            Current(() => { Validate(); check.Validate = Validate; check.FinishedAt = grants.Clock.GetTimestamp(); return true; });
            // All saved files remain pinned against writes/deletion until collection or expiry.
            // Directories are checked again at collection; the destination root may contain unrelated entries.
            await check.Collected.Task.WaitAsync(PublishedBatchCheckLifetime, token);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException || StateRecovery.IsRecordFailure(ex))
        {
            lock (gate)
            {
                if (publishedBatchChecks.GetValueOrDefault(id) == check)
                {
                    if (check.Validate is not null) publishedBatchChecks.Remove(id);
                    else check.FinishedAt = grants.Clock.GetTimestamp();
                }
            }
        }
        finally
        {
            for (var index = opened.Count - 1; index >= 0; index--) opened[index].Dispose();
            lock (gate) { publishedBatchWork.Remove(id); check.Slot.Dispose(); check.Cancellation.Dispose(); }
        }

        T Current<T>(Func<T> action) => grants.WithAuthorization(() => { lock (gate) { RequireCurrent(); return action(); } });
        void RequireCurrent()
        {
            token.ThrowIfCancellationRequested();
            if (publishedBatchChecks.GetValueOrDefault(id) != check || context.Epoch != check.Epoch || records.GetValueOrDefault(id) != row ||
                row.Retiring || row.Retired || context.Owner != grants.OwnerScope)
                throw new IOException("The batch check context changed.");
            Authorize(row, row.Peer, row.Pairing);
        }
        void RequirePublishedBatchContext()
        {
            foreach (var name in new[] { "context.json.new", "context.json.bak" })
                if (File.Exists(Path.Combine(directory, name)) || Directory.Exists(Path.Combine(directory, name))) throw new IOException("Batch history needs review.");
            if (StateRecovery.Read<Context>(Path.Combine(directory, "context.json"), 4096) != context) throw new IOException("Batch history changed.");
        }
    }

    private static void ReleasePublishedBatchCheck(PublishedBatchCheckWork check)
    { check.Collected.TrySetResult(); }
    private void InvalidatePublishedBatchChecks() { lock (gate) CancelPublishedBatchChecks(null); }
    private void CancelPublishedBatchChecks(string? scope)
    {
        foreach (var pair in publishedBatchChecks.Where(x => scope is null || x.Value.Scope == scope).ToArray())
        {
            publishedBatchChecks.Remove(pair.Key); ReleasePublishedBatchCheck(pair.Value);
            if (publishedBatchWork.ContainsKey(pair.Key)) pair.Value.Cancellation.Cancel();
        }
    }
}
