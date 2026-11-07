using IConnect.Core;
using IConnect.Protocol;
using System.Security.Cryptography;
using System.Text.Json;

namespace IConnect.Storage.Windows;

// Written before child adoption. Either descriptor can survive a crash between the two journals.
public sealed record BatchSourceApproval(string Child, string OriginalVersion, string PreviousVersion, string ApprovedVersion,
    string Hash, string RootIdentity, string TemporaryIdentity);
public sealed record BatchDownloadSourceReview(string Id, string Batch, int Item, string Path, DownloadSourceReview Source);

public sealed partial class WindowsBatchDownloads
{
    internal sealed class SourceAuthority(WindowsBatchDownloads owner, string batch, int item, string stamp)
    {
        internal bool Invalidated, Confirming;
        internal string Stamp = stamp;
        internal void Require(DownloadJob child)
        {
            if (!ReferenceEquals(owner.gate, owner.files.OwnershipGate)) throw new IOException("The original batch owner is unavailable.");
            lock (owner.gate)
            {
                var parent = owner.RequireSourceParent(batch);
                if (Invalidated || BatchStamp(parent) != Stamp || item < 0 || item >= parent.Items.Length)
                    throw new IOException("The original batch changed. Review its source again.");
                var row = parent.Items[item];
                if (row.Source.Directory || row.Source.Error.Length != 0 || row.DownloadId != child.Id || parent.Items.Count(x => x.DownloadId == child.Id) != 1 ||
                    child.BatchId != batch || child.PeerId != parent.PeerId || child.Name != row.Source.Name || child.Size != row.Source.Size ||
                    child.Collision != parent.Collision || !MatchesSource(row, child)) throw new IOException("The receiving file does not belong to this original batch item.");
                using var destination = OpenParent(parent, row.Source.Parent);
                if (destination.Info.Identity != child.RootIdentity) throw new IOException("The retained batch destination changed. Existing copies were kept.");
            }
        }
    }
    private sealed record PendingBatchSource(BatchDownloadSourceReview Value, SourceAuthority Authority, Func<bool> Current);
    private sealed class SourceWork(CancellationTokenSource cancellation)
    {
        internal readonly CancellationTokenSource Cancellation = cancellation;
        internal readonly TaskCompletionSource Fence = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly Dictionary<string, PendingBatchSource> sourceReviews = [];
    private readonly Dictionary<string, SourceWork> sourceWork = [];
    private readonly CancellationTokenSource sourceLifetime = new();
    private static string BatchStamp(BatchDownload row) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(row)));
    private BatchDownload RequireSourceParent(string id)
    {
        ObjectDisposedException.ThrowIf(disposed, this); recovery.RequireHealthy();
        if (localCopy || !jobs.TryGetValue(id, out var row) || running.ContainsKey(id) || canceling.ContainsKey(id) || row.Phase is not (BatchPhase.Paused or BatchPhase.Interrupted))
            throw new IOException("Use an idle, unfinished remote batch with its original saved plan.");
        return row;
    }
    // An empty part identity means no part existed at approval; its first native allocation is still owned by this child/root.
    private static bool MatchesSource(BatchItem item, DownloadJob child) => child.SourceVersion == item.Source.Version || item.SourceApproval is { } receipt &&
        receipt.Child == child.Id && receipt.OriginalVersion == item.Source.Version && receipt.Hash == child.Hash &&
        receipt.RootIdentity == child.RootIdentity && (receipt.TemporaryIdentity.Length == 0 || receipt.TemporaryIdentity == child.TemporaryIdentity) &&
        (receipt.PreviousVersion == child.SourceVersion || receipt.ApprovedVersion == child.SourceVersion);
    private static void ValidateSourceApproval(BatchItem item)
    {
        if (item.SourceApproval is not { } row) return;
        static bool Hash(string value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);
        if (item.Source.Directory || item.Source.Error.Length != 0 || row.Child != item.DownloadId || !ProtocolRules.TokenPattern().IsMatch(row.Child) ||
            row.OriginalVersion != item.Source.Version || !Hash(row.PreviousVersion) || !Hash(row.ApprovedVersion) || !Hash(row.Hash) ||
            row.RootIdentity is not { Length: > 0 and <= 256 } || row.TemporaryIdentity is not { Length: <= 256 })
            throw new InvalidDataException("Invalid batch source-approval binding. Files and original progress were kept.");
    }
    private SourceWork BeginSourceWork(string batch, CancellationToken ct)
    {
        if (sourceWork.ContainsKey(batch)) throw new IOException("Finish this batch's earlier source preparation first.");
        var value = new SourceWork(CancellationTokenSource.CreateLinkedTokenSource(ct, sourceLifetime.Token)); sourceWork.Add(batch, value); return value;
    }
    private void EndSourceWork(string batch, SourceWork work)
    { lock (gate) { sourceWork.Remove(batch); work.Cancellation.Dispose(); } work.Fence.TrySetResult(); }

    public async Task<BatchDownloadSourceReview> PrepareSourceReviewAsync(string batch, int index, string peer, FileEntry selected,
        TransferManifests.Request request, Func<bool> current, CancellationToken ct = default)
    {
        SourceAuthority authority; SourceWork work; DownloadJob child; string path;
        lock (gate)
        {
            var parent = RequireSourceParent(batch);
            if (parent.PeerId != peer || index < 0 || index >= parent.Items.Length || sourceReviews.Values.Any(x => x.Value.Batch == batch))
                throw new IOException("Choose a current source for one unfinished item in this device's original batch.");
            var row = parent.Items[index];
            child = files.Jobs.SingleOrDefault(x => x.Id == row.DownloadId) ?? throw new IOException("This item has no retained receiving checkpoint to renew.");
            authority = new(this, batch, index, BatchStamp(parent)); authority.Require(child);
            path = string.Join(" / ", row.Source.SourcePath); work = BeginSourceWork(batch, ct);
        }
        DownloadSourceReview? prepared = null; var retained = false;
        bool Current() { lock (gate) return !authority.Invalidated && !disposed && jobs.TryGetValue(batch, out var row) && BatchStamp(row) == authority.Stamp && current(); }
        try
        {
            prepared = await files.PrepareBatchSourceReviewAsync(child.Id, peer, selected, (value, token) => request(value, token), Current, authority, work.Cancellation.Token);
            lock (gate)
            {
                work.Cancellation.Token.ThrowIfCancellationRequested(); authority.Require(files.Jobs.Single(x => x.Id == child.Id));
                if (!Current()) throw new IOException("The source connection ended. Review again.");
                var value = new BatchDownloadSourceReview(prepared.Id, batch, index, path, prepared);
                sourceReviews.Add(value.Id, new(value, authority, current)); retained = true; return value;
            }
        }
        finally { if (!retained && prepared is not null) await files.CancelSourceReviewAsync(prepared.Id); EndSourceWork(batch, work); }
    }
    public async Task ConfirmSourceReviewAsync(BatchDownloadSourceReview value, CancellationToken ct = default)
    {
        PendingBatchSource pending; SourceWork work;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!sourceReviews.TryGetValue(value.Id, out pending!) || pending.Value != value) throw new IOException("Review this batch source again.");
            work = BeginSourceWork(value.Batch, ct); sourceReviews.Remove(value.Id);
        }
        try
        {
            lock (gate)
            {
                work.Cancellation.Token.ThrowIfCancellationRequested();
                if (!pending.Current()) throw new IOException("The paired connection changed. Earlier progress was kept.");
                pending.Authority.Require(files.Jobs.Single(x => x.Id == value.Source.Job)); pending.Authority.Confirming = true;
            }
            await files.ConfirmBatchSourceReviewAsync(value.Source, child =>
            {
                // This callback runs under the shared owner lock after every remote/record check and before child persistence.
                pending.Authority.Require(child);
                var parent = jobs[value.Batch]; var items = parent.Items.ToArray(); var item = items[value.Item];
                items[value.Item] = item with { SourceApproval = new(child.Id, item.Source.Version, child.SourceVersion, value.Source.NewVersion,
                    value.Source.Hash, child.RootIdentity, child.TemporaryIdentity) };
                var next = parent with { Items = items }; Validate(next);
                Persist(next); jobs[next.Id] = next; pending.Authority.Stamp = BatchStamp(next);
            }, work.Cancellation.Token);
            WindowsActivity.Publish(Activity, new(WindowsActivityKind.DownloadBatch, WindowsActivityOutcome.Accepted, Get(value.Batch).PeerId));
        }
        finally
        {
            pending.Authority.Invalidated = true;
            try { await files.CancelSourceReviewAsync(value.Source.Id); }
            finally { EndSourceWork(value.Batch, work); NotifySourceReview(); }
        }
    }
    public async Task CancelSourceReviewAsync(string id)
    {
        PendingBatchSource? pending;
        lock (gate) { sourceReviews.Remove(id, out pending); if (pending is not null) pending.Authority.Invalidated = true; }
        if (pending is not null) await files.CancelSourceReviewAsync(pending.Value.Source.Id);
    }
    private async Task CancelSourceRecoveryAsync(string batch)
    {
        Task[] pendingWork; string[] pendingReviews;
        lock (gate) { if (sourceWork.TryGetValue(batch, out var work)) work.Cancellation.Cancel();
            pendingWork = sourceWork.Where(x => x.Key == batch).Select(x => x.Value.Fence.Task).ToArray();
            pendingReviews = sourceReviews.Values.Where(x => x.Value.Batch == batch).Select(x => x.Value.Id).ToArray(); }
        foreach (var id in pendingReviews) await CancelSourceReviewAsync(id);
        await Task.WhenAll(pendingWork);
    }
    private void NotifySourceReview()
    { if (Changed is { } changed) foreach (Action handler in changed.GetInvocationList()) try { handler(); } catch (Exception) { } }
}
