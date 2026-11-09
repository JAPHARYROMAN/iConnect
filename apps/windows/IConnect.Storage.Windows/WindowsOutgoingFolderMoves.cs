using System.Diagnostics;
using System.Text.Json;
using IConnect.Core;
using IConnect.Protocol;

namespace IConnect.Storage.Windows;

public sealed record OutgoingFolderMoveSource(string DisplayPath, string ParentPath, string ParentIdentity, string Name,
    string Identity, string Version);
public sealed record OutgoingFolderMoveReview(string Token, string BatchId, string Source, string Destination,
    string FinalName, int Files, int Folders, long Bytes);

public sealed partial class WindowsBatchSender
{
    private sealed record OriginalNode(string Identity, string Version);
    private sealed record FolderMoveApproval(OutgoingFolderMoveReview Review, OutgoingBatchSend Copy,
        OutgoingFolderMoveSource Source, OriginalNode[] Nodes, long Expires);
    private readonly Dictionary<string, FolderMoveApproval> folderMoveReviews = [];

    private static Envelope ReadEnvelope(string path)
    {
        var json = StateRecovery.Read<JsonElement>(path, RecordLimit);
        if (!json.TryGetProperty("Schema", out var schema) || schema.ValueKind != JsonValueKind.Number ||
            !schema.TryGetInt32(out var version) || !json.TryGetProperty("Row", out var row) || row.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Outgoing batch ownership is missing or malformed.");
        if (version == 3 && (!row.TryGetProperty(nameof(OutgoingBatchSend.MoveSource), out var source) || source.ValueKind is not (JsonValueKind.Null or JsonValueKind.Object) ||
            !row.TryGetProperty(nameof(OutgoingBatchSend.SourceRecoveryId), out var recovery) || recovery.ValueKind != JsonValueKind.String ||
            !row.TryGetProperty(nameof(OutgoingBatchSend.SourceRecovered), out var recovered) || recovered.ValueKind is not (JsonValueKind.True or JsonValueKind.False)))
            throw new InvalidDataException("Folder move history is missing or malformed.");
        return json.Deserialize<Envelope>() ?? throw new InvalidDataException("Outgoing batch ownership is empty.");
    }

    private static void ValidateFolderMove(OutgoingBatchSend row)
    {
        if (row.SourceRecoveryId is null || row.MoveSource is null && (row.SourceRecoveryId.Length != 0 || row.SourceRecovered) ||
            row.MoveSource is not null && (row.SourceRecoveryId.Length == 0 || !ProtocolRules.TokenPattern().IsMatch(row.SourceRecoveryId) ||
                MutationRules.CreatedAt(row.SourceRecoveryId) is < 0 or > StorageRules.MaxTimestamp || row.Phase != BatchSendingPhase.Completed ||
                row.ReceiverOutcomes is not { Length: > 0 } || row.ReceiverOutcomes.Any(x => x != 1)))
            throw new InvalidDataException("Invalid folder source-recovery transaction.");
        if (row.MoveSource is not { } source) return;
        if (new[] { source.DisplayPath, source.ParentPath, source.ParentIdentity, source.Name, source.Identity, source.Version }.Any(x => x is null))
            throw new InvalidDataException("Original folder ownership is missing.");
        StorageRules.ValidateName(source.Name);
        if (source.DisplayPath.Length is < 4 or > 8192 || !char.IsAsciiLetter(source.DisplayPath[0]) || source.DisplayPath[1] != ':' ||
            source.DisplayPath[2] != '\\' || source.DisplayPath.Any(char.IsControl) || Path.GetFullPath(source.DisplayPath) != source.DisplayPath ||
            Path.GetFileName(source.DisplayPath) != source.Name || !SecureWindowsDirectory.IsLocalVolumePath(source.ParentPath) ||
            source.ParentIdentity.Length is < 1 or > 256 || source.Identity.Length is < 1 or > 256 ||
            source.ParentIdentity.Any(char.IsControl) || source.Identity.Any(char.IsControl) || !Hex(source.Version))
            throw new InvalidDataException("Invalid original folder binding for a move.");
    }

    private OutgoingBatchSend FolderMoveReady(string id)
    {
        Ready(); var row = rows[id]; Authorize(row);
        if (work.ContainsKey(id) || row.Archived || row.Purging || row.SourceRecovered || row.Phase != BatchSendingPhase.Completed ||
            row.Header is not { Length: > 0 } || row.ReceiverOutcomes is not { Length: > 0 } || row.ReceiverOutcomes.Any(x => x != 1))
            throw new IOException("Choose an idle, fully copied completed folder batch with retained history.");
        RequireDurableFolderMove(row); return row;
    }

    private UploadBatchManifest FolderMoveManifest(OutgoingBatchSend row)
    {
        var manifest = Source(row);
        if (!manifest.Items[0].IsDirectory || manifest.Items[0].Parent != -1 ||
            manifest.Items.Skip(1).Any(x => x.Parent < 0) || manifest.Items.Any(x => x.Error.Length != 0))
            throw new IOException("A folder move requires one completely copied folder tree without skipped or unavailable items.");
        return manifest;
    }

    private void RequireDurableFolderMove(OutgoingBatchSend row)
    {
        lock (gate)
        {
            Ready();
            var path = Path.Combine(directory, row.Id + ".json");
            if (File.Exists(path + ".new") || Directory.Exists(path + ".new") || File.Exists(path + ".bak") || Directory.Exists(path + ".bak"))
                throw new IOException("The saved folder move has uncommitted progress. Reopen and review it.");
            var saved = ReadEnvelope(path);
            if (saved.Schema is not (1 or 2 or 3) ||
                !JsonSerializer.SerializeToUtf8Bytes(saved.Row, JsonOptions).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(row, JsonOptions)))
                throw new IOException("The completed batch changed. Review its current receipt.");
        }
    }

    private void RequirePublicOriginal(string canonicalPath)
    {
        var profile = recoveryProfile.Info.CanonicalPath;
        var segments = canonicalPath[49..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (SecureWindowsDirectory.IsWithin(canonicalPath, profile) || SecureWindowsDirectory.IsWithin(profile, canonicalPath) ||
            StorageGrantStore.IsSensitiveSegments(segments) || StorageGrantStore.IsSystemTree(segments))
            throw new IOException("Choose an original outside private and operating-system locations, without containing the iConnect profile.");
    }

    public OutgoingFolderMoveReview ReviewFolderMove(string id, string selectedFolder, WindowsLocalManagement management)
    {
        OutgoingBatchSend row;
        lock (gate) row = FolderMoveReady(id);
        if (row.SourceRecoveryId.Length != 0 && management.HasSourceRecovery(row.SourceRecoveryId))
            throw new IOException("This move already has a source recovery record. Review it under local saved items.");
        lock (gate)
        {
            if (FolderMoveReady(id) != row) throw new IOException("The completed folder batch changed. Review it again.");
            var manifest = FolderMoveManifest(row);
            var display = Path.GetFullPath(selectedFolder).TrimEnd(Path.DirectorySeparatorChar);
            var drive = Path.GetPathRoot(display)!;
            if (drive.Length != 3 || drive[1] != ':' || !char.IsAsciiLetter(drive[0]) || display.Length <= 3 || display.Length > 8192)
                throw new IOException("Select a named original folder on a normal local drive.");
            var segments = display[3..].Split('\\');
            foreach (var segment in segments) StorageRules.ValidateName(segment);
            if (segments[^1] != manifest.Items[0].Name) throw new IOException("Select the original folder with the prepared root name.");
            using var volume = SecureWindowsDirectory.OpenRoot(drive);
            using var parent = SecureWindowsDirectory.OpenPath(volume.Info.CanonicalPath, volume.Info.Identity, segments[..^1], true);
            using var original = parent.OpenChild(segments[^1], true); RequirePublicOriginal(original.Info.CanonicalPath);
            var source = new OutgoingFolderMoveSource(display, parent.Info.CanonicalPath, parent.Info.Identity, segments[^1], original.Info.Identity, original.Info.Version);
            if (row.MoveSource is not null && row.MoveSource != source with { Version = row.MoveSource.Version })
                throw new IOException("The pending recovery transaction belongs to a different original folder.");
            using var tree = OriginalTree.Open(original, manifest, null, default);
            foreach (var old in folderMoveReviews.Where(x => x.Value.Expires < Environment.TickCount64).Select(x => x.Key).ToArray()) folderMoveReviews.Remove(old);
            if (!folderMoveReviews.ContainsKey(id) && folderMoveReviews.Count >= 2)
                throw new IOException("Two folder-move reviews are already open. Finish one or wait for its expiry.");
            var value = new OutgoingFolderMoveReview(ProtocolRules.NewToken(), id, display, row.Destination, ItemResult(id, 0).Name,
                manifest.Items.Count(x => !x.IsDirectory), manifest.Items.Count(x => x.IsDirectory), row.Bytes);
            folderMoveReviews[id] = new(value, row, source, tree.Nodes, Environment.TickCount64 + 5 * 60_000);
            return value;
        }
    }

    public void CancelFolderMoveReview(OutgoingFolderMoveReview value)
    {
        lock (gate)
            if (folderMoveReviews.TryGetValue(value.BatchId, out var saved) && saved.Review == value) folderMoveReviews.Remove(value.BatchId);
    }

    public Task<MutationInfo> FinishFolderMoveAsync(OutgoingFolderMoveReview value, WindowsLocalManagement management,
        WindowsUploadSender.Request request, Func<bool> sessionCurrent, bool cancelReceiver, CancellationToken ct = default)
    {
        lock (gate)
        {
            if (!folderMoveReviews.Remove(value.BatchId, out var approval) || approval.Review != value || approval.Expires < Environment.TickCount64)
                throw new IOException("This folder-move review expired or was used. Select and review the original again.");
            var row = FolderMoveReady(value.BatchId);
            if (row != approval.Copy || work.Count >= 2) throw new IOException("The completed batch changed or two outgoing batch controls are active.");
            var manifest = FolderMoveManifest(row);
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct); var active = new Work(cancellation, folderMove: true);
            var result = new TaskCompletionSource<MutationInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
            work.Add(row.Id, active);
            active.Task = Task.Run(async () =>
            {
                try
                {
                    var token = cancellation.Token;
                    Current(row);
                    if (row.SourceRecoveryId.Length != 0 && management.HasSourceRecovery(row.SourceRecoveryId))
                        throw new IOException("This move already has a source recovery record. Review local saved items.");
                    using var original = SecureWindowsDirectory.OpenPath(approval.Source.ParentPath, approval.Source.ParentIdentity,
                        [approval.Source.Name], true, mutation: true);
                    RequirePublicOriginal(original.Info.CanonicalPath);
                    if (original.Info.Identity != approval.Source.Identity || original.Info.Version != approval.Source.Version)
                        throw new IOException("The reviewed original folder changed. Its contents were kept.");
                    using var tree = OriginalTree.Open(original, manifest, approval.Nodes, token);
                    tree.VerifyFiles(token); Current(row);
                    await RequirePublishedBatchProof(row, request, token, cancelReceiver);
                    var checkedAt = Stopwatch.GetTimestamp(); Current(row);
                    // Windows requires child handles to close before the ancestor rename.
                    // The root stays pinned; a separate full check follows the recovery move.
                    tree.Dispose();
                    var pending = Put(row with { MoveSource = approval.Source,
                        SourceRecoveryId = MutationRules.NewId(),
                        Message = "Folder source recovery was requested. Review local saved items for its outcome." });
                    var sourceSaved = false;
                    var receipt = management.RecoverVerifiedOutgoingFolder(approval.Source, original, pending.SourceRecoveryId,
                        saved => { using var current = OriginalTree.Open(saved, manifest, approval.Nodes, token); },
                        saved => { sourceSaved = true; using var current = OriginalTree.Open(saved, manifest, approval.Nodes, token); current.VerifyFiles(token); }, () =>
                        {
                            Current(pending);
                            if (!sourceSaved && Stopwatch.GetElapsedTime(checkedAt) >= TimeSpan.FromSeconds(10))
                                throw new IOException("The receiving folder check expired before source recovery.");
                        }, token);
                    if (receipt.Phase == MutationPhase.ChangeSucceeded)
                        Put(pending with { SourceRecovered = true, Message = "The receiving folder was verified and the original was saved in local recovery. Review local saved items for its current location." });
                    result.TrySetResult(receipt);
                }
                catch (OperationCanceledException) { result.TrySetCanceled(cancellation.Token); }
                catch (Exception ex) { result.TrySetException(ex); }
                finally { lock (gate) work.Remove(row.Id); cancellation.Dispose(); NotifyChanged(); }
                void Current(OutgoingBatchSend expected)
                {
                    cancellation.Token.ThrowIfCancellationRequested(); Authorize(expected); RequireDurableFolderMove(expected);
                    if (!sessionCurrent()) throw new IOException("The authenticated receiving session changed. Copies were kept.");
                }
            });
            NotifyChanged(); return Observe();
            async Task<MutationInfo> Observe() { await active.Task; return await result.Task; }
        }
    }

    private sealed class OriginalTree : IDisposable
    {
        private readonly SecureWindowsDirectory[] opened;
        private readonly UploadBatchManifest manifest;
        public OriginalNode[] Nodes { get; }
        private OriginalTree(SecureWindowsDirectory[] opened, UploadBatchManifest manifest, OriginalNode[] nodes)
        { this.opened = opened; this.manifest = manifest; Nodes = nodes; }
        public static OriginalTree Open(SecureWindowsDirectory root, UploadBatchManifest manifest, OriginalNode[]? expected, CancellationToken ct)
        {
            var opened = new List<SecureWindowsDirectory> { root }; var nodes = new List<OriginalNode>();
            try
            {
                for (var index = 0; index < manifest.Items.Count; index++)
                {
                    ct.ThrowIfCancellationRequested(); var item = manifest.Items[index];
                    var entry = index == 0 ? root : opened[item.Parent].OpenChild(item.Name, item.IsDirectory);
                    if (index != 0) opened.Add(entry);
                    if (entry.Info.IsDirectory != item.IsDirectory || !item.IsDirectory && (entry.Info.Size != item.Size || entry.Info.Links != 1))
                        throw new IOException("An original folder item does not match the completed batch.");
                    var node = new OriginalNode(entry.Info.Identity, entry.Info.Version); nodes.Add(node);
                    if (expected is not null && (expected.Length != manifest.Items.Count || expected[index] != node))
                        throw new IOException("An original folder item changed after review.");
                }
                var tree = new OriginalTree(opened.ToArray(), manifest, nodes.ToArray()); tree.VerifyAt(root, ct); return tree;
            }
            catch { for (var index = opened.Count - 1; index > 0; index--) opened[index].Dispose(); throw; }
        }
        public void VerifyFiles(CancellationToken ct)
        {
            for (var index = 0; index < manifest.Items.Count; index++)
                if (!manifest.Items[index].IsDirectory)
                    WindowsDownloadManager.VerifyFile(opened[index], manifest.Items[index].Size,
                        Convert.ToHexStringLower(manifest.Items[index].Sha256.Span), ct);
            VerifyAt(opened[0], ct);
        }
        public void VerifyAt(SecureWindowsDirectory root, CancellationToken ct)
        {
            var children = manifest.Items.Select((item, index) => (item, index)).ToLookup(x => x.item.Parent);
            for (var index = 0; index < opened.Length; index++)
            {
                ct.ThrowIfCancellationRequested(); var entry = index == 0 ? root : opened[index];
                var info = SecureWindowsDirectory.Inspect(entry.Handle); var item = manifest.Items[index];
                if (info.Identity != Nodes[index].Identity || info.IsDirectory != item.IsDirectory ||
                    (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0 || !item.IsDirectory && (info.Version != Nodes[index].Version || info.Links != 1))
                    throw new IOException("The original tree changed while checking its receiving copy.");
                if (index != 0)
                {
                    using var named = opened[item.Parent].OpenChild(item.Name, item.IsDirectory);
                    if (named.Info.Identity != Nodes[index].Identity || !item.IsDirectory && named.Info.Version != Nodes[index].Version)
                        throw new IOException("A named original item was replaced while checking its receiving copy.");
                }
                if (!item.IsDirectory) continue;
                var expected = children[index].Select(x => (x.item.Name, x.item.IsDirectory)).ToHashSet();
                var actual = entry.EnumerateNames(ct).Take(expected.Count + 1).Select(x => (x.Name, x.IsDirectory)).ToArray();
                if (actual.Length != expected.Count || !actual.ToHashSet().SetEquals(expected))
                    throw new IOException("The original folder has missing or unexpected children.");
            }
        }
        public void Dispose() { for (var index = opened.Length - 1; index > 0; index--) opened[index].Dispose(); }
    }
}
