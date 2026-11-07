using Google.Protobuf;
using IConnect.Core;
using IConnect.Protocol;
using System.Collections.Concurrent;

namespace IConnect.Storage.Windows;

public sealed record IncomingBatchFileProgress(string Batch, uint Index, string Name, long Size, long Offset, UploadPhase Phase,
    PayloadRateEstimate? Estimate, IncomingFinalizationProgress? Finalization);

public sealed partial class WindowsBatchPlans
{
    private sealed class FileReceiver(WindowsUploads uploads, string batch, uint index, UploadBatchPlanPhase phase)
    { internal WindowsUploads Uploads { get; } = uploads; internal string Batch { get; } = batch; internal uint Index { get; } = index; internal UploadBatchPlanPhase Phase { get; } = phase; internal string WorkScope { get; set; } = ""; }
    private readonly ConcurrentDictionary<string, FileReceiver> fileReceivers = new();

    public IReadOnlyList<IncomingBatchFileProgress> ReadFileProgress(IncomingBatchPlan plan, long limit = 0) =>
        plan.Phase != UploadBatchPlanPhase.UploadPlanReady ? [] : fileReceivers.Values
            .Where(x => x.Batch == plan.Id && x.Phase == plan.Phase)
            .Select(x => x.Uploads.ReadScopedProgress(x.Batch, x.Index, limit)).OfType<IncomingBatchFileProgress>()
            .OrderBy(x => x.Index).ToArray();
    public bool HasFileProgress => fileReceivers.Values.Any(x => x.Uploads.HasPayloadProgress || x.Uploads.HasFinalizationProgress);
    public void ClearFilePayloadRates()
    {
        foreach (var receiver in fileReceivers.Values) receiver.Uploads.ClearPayloadRates();
    }
    public void PauseFileVerification()
    { WindowsUploads[] snapshot; lock (gate) snapshot = fileReceivers.Values.Select(x => x.Uploads).ToArray(); foreach (var uploads in snapshot) uploads.PauseVerification(); }
    public Task WhenFilesIdleAsync(string? plan = null)
    { WindowsUploads[] snapshot; lock (gate) snapshot = fileReceivers.Values.Where(x => plan is null || x.Batch == plan).Select(x => x.Uploads).ToArray(); return Task.WhenAll(snapshot.Select(x => x.WhenIdleAsync())); }
    private bool IsFileItem(Row row, FileRequest request) => UploadBatchRules.HasManifest(row.Phase) && request.HasBatchItemIndex &&
        request.BatchItemIndex < Header(row).ItemCount && !AcceptedManifest(row, out _).Items[(int)request.BatchItemIndex].IsDirectory;
    private static UploadDestination LogicalFileTarget(Row row, UploadBatchManifest manifest, DirectoryChild child, Dictionary<uint, DirectoryChild> children)
    {
        var names = new Stack<string>(); var parent = manifest.Items[(int)child.Index].Parent;
        while (parent >= 0)
        {
            if (!children.TryGetValue((uint)parent, out var ancestor) || !ancestor.IsDirectory || ancestor.Phase != UploadBatchItemPhase.BatchItemSucceeded) throw new InvalidDataException("A file child lost its confirmed parent receipt.");
            names.Push(ancestor.Name); parent = manifest.Items[parent].Parent;
        }
        return row.Target with { Path = [.. row.Target.Path, .. names], Identity = child.ParentIdentity };
    }
    private WindowsScopedUpload FileScope(Row row, UploadBatchManifest manifest, DirectoryChild child, UploadDestination target, bool fresh)
    {
        var h = Header(row); var item = manifest.Items[(int)child.Index];
        var descriptor = new UploadInfo { Id = child.Id, Name = item.Name, Size = item.Size, Sha256 = item.Sha256, Collision = manifest.Collision };
        var name = WindowsBatchUploadJournals.Name(h.Id, child.Index);
        void Current()
        {
            lock (gate)
            {
                Ready();
                if (!records.TryGetValue(h.Id, out var retained) || retained.Owner != row.Owner || retained.Peer != row.Peer || retained.Pairing != row.Pairing ||
                    retained.Phase != row.Phase || retained.Phase is not (UploadBatchPlanPhase.UploadPlanReady or UploadBatchPlanPhase.UploadPlanStopping) || !Header(retained).Equals(h) || !SameTarget(retained.Target, row.Target))
                    throw new StorageException(FileStatus.StaleReference, "The parent batch no longer owns this file child.");
                Authorize(retained, row.Peer, row.Pairing);
                var binding = ReadDirectoryChild(retained, AcceptedManifest(retained, out _), child.Index);
                if (binding != child) throw new StorageException(FileStatus.Conflict, "The retained file-child binding changed.");
                fileJournals.Check(name);
            }
        }
        SecureWindowsDirectory Open()
        {
            lock (gate)
            {
                Current(); using var lease = OpenDirectoryParent(row, AcceptedManifest(row, out _), child.Index);
                if (lease.Directory.Info.Identity != child.ParentIdentity || !lease.Path.SequenceEqual(target.Path)) throw new StorageException(FileStatus.StaleReference, "The file child's original parent changed.");
                return lease.Detach();
            }
        }
        // The very first Authorize occurs before this child's journal exists. The host has
        // already committed its separate binding/attempt marker, and no constructor creates bytes.
        var firstCommit = fresh;
        void AuthorizeScope()
        {
            if (!firstCommit) { Current(); return; }
            lock (gate)
            {
                Authorize(row, row.Peer, row.Pairing); fileJournals.Check(name, true);
                if (ReadDirectoryChild(row, AcceptedManifest(row, out _), child.Index) != child) throw new StorageException(FileStatus.Conflict, "The file-child binding changed before preparation.");
            }
        }
        SecureWindowsDirectory OpenScope()
        {
            if (!firstCommit) return Open();
            lock (gate)
            {
                AuthorizeScope(); using var lease = OpenDirectoryParent(row, AcceptedManifest(row, out _), child.Index);
                if (lease.Directory.Info.Identity != child.ParentIdentity || !lease.Path.SequenceEqual(target.Path)) throw new StorageException(FileStatus.StaleReference, "The initial file parent changed.");
                return lease.Detach();
            }
        }
        return new(Path.Combine(directory, name), child.Id, row.Peer, descriptor, target, fresh, AuthorizeScope, OpenScope,
            (bytes, progressOnly) => { AuthorizeScope(); fileJournals.Write(name, bytes, firstCommit, progressOnly); firstCommit = false; }, row.Phase == UploadBatchPlanPhase.UploadPlanStopping);
    }
    private void MakeReceiverRoom()
    {
        if (fileReceivers.Count < 2) return;
        var idle = fileReceivers.FirstOrDefault(x => x.Value.Uploads.IsIdle);
        if (idle.Value is not null) fileReceivers.TryRemove(idle.Key, out _);
    }
    private FileResponse HandleBatchFile(Row row, string peer, string pairing, string session, FileRequest request, CancellationToken ct)
    {
        var h = Header(row);
        if (!request.HasBatchItemIndex || request.BatchItemIndex >= h.ItemCount) throw new InvalidDataException("Choose a valid batch file index.");
        if (row.Phase != UploadBatchPlanPhase.UploadPlanReady && !(row.Phase == UploadBatchPlanPhase.UploadPlanStopping && request.Operation is FileOperation.CancelBatchFile or FileOperation.UploadBatchItemStatus)) throw new StorageException(FileStatus.Conflict, "This batch cannot start or continue file effects.");
        var manifest = AcceptedManifest(row, out var encoded); var index = request.BatchItemIndex; var item = manifest.Items[(int)index];
        if (item.IsDirectory || item.Error.Length != 0) throw new StorageException(FileStatus.Unsupported, "This planned item is not a usable file.");
        if (request.DestinationId.Length != 0 || request.NodeId.Length != 0 || request.Name.Length != 0 || request.BatchHeader is not null && !request.BatchHeader.Equals(h))
            throw new InvalidDataException("The accepted manifest owns the file destination and name.");
        var expected = new UploadInfo { Id = UploadBatchRules.ChildId(peer, h, (int)index, false), Name = item.Name, Size = item.Size, Sha256 = item.Sha256, Collision = manifest.Collision };
        if (request.Upload is not null && !request.Upload.Equals(expected) || request.Operation != FileOperation.WriteBatchFile && (request.Offset != 0 || request.Content.Length != 0))
            throw new InvalidDataException("Use the original manifest file descriptor and operation fields.");
        using var parent = OpenDirectoryParent(row, manifest, index);
        var target = row.Target with { Path = parent.Path, Identity = parent.Directory.Info.Identity };
        grants.ValidateAccess(grants.Get(target.Grant, peer), [.. target.Path, item.Name]);
        var attempts = ReadAttempts(row); var child = ReadDirectoryChild(row, manifest, index); var fresh = child is null;
        if (row.EffectsStarted && attempts is null || fresh && attempts is not null && Attempted(attempts, index))
        { fileJournals.Block(); throw new StorageException(FileStatus.Conflict, "A started batch file lost its ownership metadata. Existing copies were kept."); }
        if (fresh)
        {
            if (row.Phase != UploadBatchPlanPhase.UploadPlanReady) throw new StorageException(FileStatus.NotFound, "The stopped batch has no retained file child.");
            if (request.Operation != FileOperation.PrepareBatchFile) throw new StorageException(FileStatus.NotFound, "No retained file receipt exists. Status and retry do not create another copy.");
            MakeReceiverRoom(); if (fileReceivers.Count >= 2) throw new StorageException(FileStatus.ResourceExhausted, "Two batch files are busy. Retry preparation after one becomes idle.");
            fileJournals.Check(WindowsBatchUploadJournals.Name(h.Id, index), true); fileJournals.Reserve(h.Id);
            if (directoryBytes.GetValueOrDefault(h.Id) > ChildMetadataLimit - ChildRecordLimit) throw new StorageException(FileStatus.ResourceExhausted, "Batch child binding storage is full.");
            if (!row.EffectsStarted)
            {
                var bits = new byte[(h.ItemCount + 7) / 8]; attempts ??= new(1, h.Id, Convert.ToHexStringLower(h.Sha256.Span), bits, AttemptsChecksum(h, bits));
                if (attempts.Bits.Any(x => x != 0)) throw new InvalidDataException("The batch attempt state changed before its first effect.");
                Save(AttemptsName(h.Id), attempts, 4096); row = row with { EffectsStarted = true }; Put(row, encoded);
            }
            child = new(1, index, UploadBatchRules.ChildId(peer, h, (int)index, false), item.Name, target.Identity, "", UploadBatchItemPhase.BatchItemPending, "The file child belongs to its original accepted plan.", false);
            SaveDirectoryChild(row, child);
            attempts!.Bits[index / 8] |= (byte)(1 << (int)(index % 8)); attempts = attempts with { Checksum = AttemptsChecksum(h, attempts.Bits) }; Save(AttemptsName(h.Id), attempts, 4096);
        }
        else
        {
            if (child!.IsDirectory || child.ParentIdentity != target.Identity) throw new StorageException(FileStatus.StaleReference, "The file child's original parent changed.");
            if (attempts is null || !Attempted(attempts, index)) { fileJournals.Block(); throw new StorageException(FileStatus.Conflict, "The file child lost its attempt marker."); }
            fileJournals.Check(WindowsBatchUploadJournals.Name(h.Id, index));
        }
        var binding = FileScope(row, manifest, child!, target, fresh);
        if (fileReceivers.TryGetValue(child!.Id, out var old) && old.Phase != row.Phase)
        {
            if (!old.Uploads.IsIdle) { old.Uploads.PauseVerification(); throw new StorageException(FileStatus.Conflict, "The earlier file verification is stopping. Retry after it becomes idle."); }
            fileReceivers.TryRemove(child.Id, out _);
        }
        if (!fileReceivers.TryGetValue(child!.Id, out var receiver))
        {
            MakeReceiverRoom();
            if (fileReceivers.Count >= 2 && request.Operation != FileOperation.UploadBatchItemStatus) throw new StorageException(FileStatus.ResourceExhausted, "Two batch files are busy. Retry after a verification finishes.");
            var uploads = new WindowsUploads(grants, Path.GetDirectoryName(directory)!, binding);
            if (uploads.RecoveryIssues.Count != 0) { fileJournals.Block(); throw new StorageException(FileStatus.Conflict, "The retained batch file journal needs local review."); }
            receiver = new(uploads, h.Id, index, row.Phase);
            if (fileReceivers.Count < 2) fileReceivers[child.Id] = receiver;
        }
        var operation = request.Operation switch { FileOperation.PrepareBatchFile => FileOperation.PrepareUpload, FileOperation.WriteBatchFile => FileOperation.UploadWrite,
            FileOperation.FinishBatchFile => FileOperation.FinishUpload, FileOperation.CancelBatchFile => FileOperation.CancelUpload, FileOperation.UploadBatchItemStatus => FileOperation.UploadStatus,
            _ => throw new StorageException(FileStatus.Unsupported, "Unsupported batch file operation.") };
        if (operation == FileOperation.FinishUpload && receiver.Uploads.IsIdle) receiver.WorkScope = session;
        var inner = new FileRequest { RequestId = request.RequestId, Operation = operation, TransferId = child.Id, MutationEpoch = receiver.Uploads.Epoch,
            Offset = request.Offset, Content = request.Content };
        if (operation == FileOperation.PrepareUpload) inner.Upload = binding.Descriptor;
        var response = receiver.Uploads.Execute(peer, inner, () => target, ct, session); var info = response.Upload;
        response.BatchItem = new() { Header = h, Index = index, Id = child.Id, IsDirectory = false, Phase = FilePhase(info.Phase),
            ResultingName = info.Phase == UploadPhase.UploadCompleted ? info.FinalName : "", Message = info.Message };
        return response;
    }
    private static UploadBatchItemPhase FilePhase(UploadPhase phase) => phase switch {
        UploadPhase.UploadCompleted => UploadBatchItemPhase.BatchItemSucceeded, UploadPhase.UploadSkipped => UploadBatchItemPhase.BatchItemSkipped,
        UploadPhase.UploadCanceled => UploadBatchItemPhase.BatchItemFailed, UploadPhase.UploadReview => UploadBatchItemPhase.BatchItemReview, _ => UploadBatchItemPhase.BatchItemPending };
}
