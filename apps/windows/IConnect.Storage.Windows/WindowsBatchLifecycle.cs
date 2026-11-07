using Google.Protobuf;
using IConnect.Core;
using IConnect.Protocol;

namespace IConnect.Storage.Windows;

public sealed partial class WindowsBatchPlans
{
    private (UploadBatchManifest Manifest, byte[] Encoded, Dictionary<uint, DirectoryChild> Children) Inventory(Row row)
    {
        var manifest = AcceptedManifest(row, out var encoded); var attempts = ReadAttempts(row);
        if (row.EffectsStarted && attempts is null) throw new StorageException(FileStatus.Conflict, "The batch attempt inventory needs local review.");
        var children = new Dictionary<uint, DirectoryChild>();
        for (uint index = 0; index < manifest.Items.Count; index++)
        {
            var child = ReadDirectoryChild(row, manifest, index);
            if (child is not null) children[index] = child;
            if (attempts is not null && Attempted(attempts, index) != (child is not null))
                throw new StorageException(FileStatus.Conflict, "A batch item lost its attempt or ownership receipt. Copies were kept.");
        }
        return (manifest, encoded, children);
    }
    private (WindowsScopedUpload Scope, UploadInfo Info) FileInventory(Row row, UploadBatchManifest manifest, DirectoryChild child, Dictionary<uint, DirectoryChild> children)
    {
        var name = WindowsBatchUploadJournals.Name(Header(row).Id, child.Index); fileJournals.Check(name);
        var binding = FileScope(row, manifest, child, LogicalFileTarget(row, manifest, child, children), false);
        return (binding, WindowsUploads.InspectScopedJournal(binding, context.Owner));
    }
    private byte[] Outcomes(Row row, UploadBatchManifest manifest, Dictionary<uint, DirectoryChild> children, bool requireComplete, bool inspectParts)
    {
        var result = new byte[manifest.Items.Count]; var blocked = new bool[result.Length];
        for (var index = 0; index < result.Length; index++)
        {
            var item = manifest.Items[index]; children.TryGetValue((uint)index, out var child);
            var suppressed = item.Parent >= 0 && blocked[item.Parent];
            if ((item.Error.Length != 0 || suppressed) && child is not null)
                throw new StorageException(FileStatus.Conflict, "An excluded batch branch has unexpected ownership metadata.");
            UploadBatchItemPhase phase;
            if (item.Error.Length != 0) phase = UploadBatchItemPhase.BatchItemFailed;
            else if (suppressed) phase = UploadBatchItemPhase.BatchItemSkipped;
            else if (child is null) phase = UploadBatchItemPhase.BatchItemPending;
            else if (child.IsDirectory) phase = child.Phase == UploadBatchItemPhase.BatchItemPending ? UploadBatchItemPhase.BatchItemReview : child.Phase;
            else
            {
                var file = FileInventory(row, manifest, child, children);
                if (inspectParts && WindowsUploads.ScopedPartialExists(file.Scope, context.Owner))
                    throw new StorageException(FileStatus.Conflict, "The batch still owns private file staging.");
                phase = FilePhase(file.Info.Phase);
            }
            if (requireComplete && phase is UploadBatchItemPhase.BatchItemPending or UploadBatchItemPhase.BatchItemReview)
                throw new StorageException(FileStatus.Conflict, "Every planned item needs a final receipt before completing the batch.");
            result[index] = (byte)phase;
            blocked[index] = item.IsDirectory && phase != UploadBatchItemPhase.BatchItemSucceeded;
        }
        return result;
    }
    private Row EndPlan(Row row, FileRequest request, string scope, CancellationToken ct)
    {
        if (request.HasBatchItemIndex || request.Content.Length != 0 || request.Offset != 0 || request.Upload is not null ||
            request.BatchHeader is not null && !request.BatchHeader.Equals(Header(row)) || request.NodeId.Length != 0 || request.DestinationId.Length != 0 || request.Name.Length != 0)
            throw new InvalidDataException("Use the original batch identifier and history namespace to finish or stop it.");
        if (UploadBatchRules.IsTerminal(row.Phase))
        {
            if (request.Operation == FileOperation.FinishUploadBatch && row.Phase != UploadBatchPlanPhase.UploadPlanCompleted)
                throw new StorageException(FileStatus.Conflict, "A stopped or canceled batch cannot become completed.");
            return row;
        }
        var id = Header(row).Id;
        if (request.Operation == FileOperation.StopUploadBatch && !row.EffectsStarted)
        { EndAssembly(id); row = row with { Phase = UploadBatchPlanPhase.UploadPlanCanceled }; Put(row); return row; }
        if (!UploadBatchRules.HasManifest(row.Phase)) throw new StorageException(FileStatus.Conflict, "Accept the original complete manifest before finishing a batch.");
        if (request.Operation == FileOperation.FinishUploadBatch)
        {
            if (row.Phase != UploadBatchPlanPhase.UploadPlanReady || fileReceivers.Values.Any(x => x.Batch == id && !x.Uploads.IsIdle))
                throw new StorageException(FileStatus.Conflict, "Stop or wait for batch file work before completing this plan.");
            var complete = Inventory(row); var outcomes = Outcomes(row, complete.Manifest, complete.Children, true, true);
            row = row with { Phase = UploadBatchPlanPhase.UploadPlanCompleted, Outcomes = outcomes }; Put(row, complete.Encoded); return row;
        }
        var manifest = AcceptedManifest(row, out var encoded);
        // Commit the barrier before canceling workers. An old worker's scope cannot authorize
        // publication or progress after this transition. Never wait while holding the parent lock.
        if (row.Phase == UploadBatchPlanPhase.UploadPlanReady)
        { row = row with { Phase = UploadBatchPlanPhase.UploadPlanStopping }; Put(row, encoded); }
        foreach (var receiver in fileReceivers.Values.Where(x => x.Batch == id)) receiver.Uploads.PauseVerification();
        if (fileReceivers.Values.Any(x => x.Batch == id && !x.Uploads.IsIdle)) return row;
        foreach (var key in fileReceivers.Where(x => x.Value.Batch == id).Select(x => x.Key).ToArray()) fileReceivers.TryRemove(key, out _);
        var cleaned = 0; var scanned = 0; var cursor = row.StopCursor; var attempts = ReadAttempts(row); var bindings = new Dictionary<uint, DirectoryChild>();
        if (row.EffectsStarted && attempts is null) throw new StorageException(FileStatus.Conflict, "The stopped batch lost its attempt inventory.");
        DirectoryChild? Binding(uint index)
        {
            if (bindings.TryGetValue(index, out var found)) return found;
            var value = ReadDirectoryChild(row, manifest, index);
            if (attempts is not null && Attempted(attempts, index) != (value is not null)) throw new StorageException(FileStatus.Conflict, "A stopped item lost its ownership receipt.");
            if (value is not null) bindings[index] = value;
            return value;
        }
        while (cursor < Header(row).ItemCount && scanned++ < 256)
        {
            ct.ThrowIfCancellationRequested(); var child = Binding(cursor);
            if (child is { IsDirectory: false })
            {
                var parent = manifest.Items[(int)cursor].Parent;
                while (parent >= 0) { _ = Binding((uint)parent); parent = manifest.Items[parent].Parent; }
                var file = FileInventory(row, manifest, child, bindings);
                if (!UploadRules.IsTerminal(file.Info.Phase) || WindowsUploads.ScopedPartialExists(file.Scope, context.Owner))
                {
                    if (cleaned++ == 16) break;
                    var receiver = new WindowsUploads(grants, Path.GetDirectoryName(directory)!, file.Scope);
                    if (receiver.RecoveryIssues.Count != 0) throw new StorageException(FileStatus.Conflict, "Stopped file ownership needs local review.");
                    receiver.StopScopedPartial();
                }
            }
            cursor++;
        }
        row = row with { StopCursor = cursor }; Put(row, encoded);
        if (cursor < Header(row).ItemCount) return row;
        var inventory = Inventory(row); var stopped = Outcomes(row, inventory.Manifest, inventory.Children, false, true);
        row = row with { Phase = UploadBatchPlanPhase.UploadPlanStopped, Outcomes = stopped }; Put(row, inventory.Encoded); return row;
    }
    private void TerminalItem(Row row, FileRequest request, FileResponse response)
    {
        if (request.Content.Length != 0 || request.Offset != 0 || request.Upload is not null || request.BatchHeader is not null && !request.BatchHeader.Equals(Header(row)) ||
            request.NodeId.Length != 0 || request.DestinationId.Length != 0 || request.Name.Length != 0) throw new InvalidDataException("Use the retained batch item binding for status.");
        if (!UploadBatchRules.HasManifest(row.Phase) || !request.HasBatchItemIndex || request.BatchItemIndex >= Header(row).ItemCount)
            throw new StorageException(FileStatus.NotFound, "This terminal plan has no retained child receipt.");
        var inventory = Inventory(row);
        if (!inventory.Children.TryGetValue(request.BatchItemIndex, out var child))
            throw new StorageException(FileStatus.NotFound, "This item has no destination effect receipt.");
        if (child.IsDirectory)
        { response.BatchItem = ChildInfo(row, child); response.BatchItem.Phase = (UploadBatchItemPhase)row.Outcomes![(int)child.Index]; }
        else
        {
            var file = FileInventory(row, inventory.Manifest, child, inventory.Children); response.Upload = file.Info;
            response.BatchItem = new() { Header = Header(row), Index = child.Index, Id = child.Id, IsDirectory = false,
                Phase = FilePhase(file.Info.Phase), ResultingName = file.Info.Phase == UploadPhase.UploadCompleted ? file.Info.FinalName : "", Message = file.Info.Message };
        }
    }
}
