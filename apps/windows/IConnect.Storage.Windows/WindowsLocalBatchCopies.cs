using IConnect.Core;
using IConnect.Protocol;
using System.Text.Json;

namespace IConnect.Storage.Windows;

public sealed record LocalBatchCopyJob(string Id, string[] Sources, int Items, int Files, int Directories, long Bytes, int Issues, BatchDownload? Transfer,
    bool HasSource = true, bool CopyMayExist = false, string MoveDestination = "");
public sealed record LocalBatchCopyItem(int Index, string Location, bool Directory, long Bytes, string Error);

/// <summary>Immutable local selection plans and verified file/folder receiving. Never a peer host.</summary>
public sealed partial class WindowsLocalBatchCopies : IAsyncDisposable
{
    private sealed record SourceRoot(string Id, string Canonical, string Identity, string Version, string Name, string Display, bool FileOnly);
    private sealed record Node(string Id, int Root, int Parent, string[] Path, string Name, bool Directory, long Size, string Identity, string Version, string Error = "");
    private sealed record Plan(int Schema, string Id, SourceRoot[] Roots, Node[] Nodes, FolderMoveTarget? Move = null);
    private readonly object gate = new();
    private readonly SecureWindowsDirectory root, lease, sourceRoot, receivingRoot, filesRoot, batchRoot, decisionRoot;
    private readonly WindowsDownloadManager files;
    private readonly WindowsBatchDownloads batches;
    private readonly StateRecovery recovery;
    private readonly Dictionary<string, Plan> plans = [];
    private readonly Dictionary<string, string> versions = [];
    private readonly HashSet<Task> planning = [];
    private readonly CancellationTokenSource lifetime = new();
    private bool closed;
    public event Action? Changed;
    public event Action<DownloadJob>? PayloadChanged;
    public DownloadJob? FileProgress(string id) => files.FindJob(id);
    public DownloadJob? ActiveFileProgress(string batch) =>
        files.Jobs.FirstOrDefault(x => x.BatchId == batch && !files.WaitAsync(x.Id).IsCompleted);
    public bool HasFinalizationProgress => files.HasFinalizationProgress;
    public IncomingFinalizationProgress? ReadFinalization(string id) => files.ReadFinalization(id);
    public Action<WindowsActivityEvent>? Activity { get; set; }
    public bool Planning { get { lock (gate) return planning.Count != 0; } }
    public WindowsLocalBatchCopies(string profile) : this(profile, new WindowsAttachmentChecks()) { }
    internal WindowsLocalBatchCopies(string profile, IAttachmentChecks checks)
    {
        var directory = Path.Combine(profile, "local-batch-copies"); Directory.CreateDirectory(directory); root = SecureWindowsDirectory.OpenRoot(directory);
        try
        {
            try { lease = root.CreateFile("owner.lock"); }
            catch (StorageException ex) when (ex.NativeError is 80 or 183) { lease = root.OpenWritableMetadataChild("owner.lock"); }
            sourceRoot = Folder(root, "sources"); receivingRoot = Folder(root, "receiving"); filesRoot = Folder(receivingRoot, "downloads"); batchRoot = Folder(receivingRoot, "batches");
            decisionRoot = Folder(root, "copy-decisions");
            files = new(Path.Combine(directory, "receiving"), checks, localCopy: true, ownerGate: gate, batchOnly: true);
            batches = new(Path.Combine(directory, "receiving"), files, localCopy: true, ownerGate: gate);
            recovery = new(Path.Combine(directory, "sources")); var journals = StateRecovery.Journals(Path.Combine(directory, "sources"));
            if (journals.Length > 8) recovery.Block("More than eight local batch plans exist. Review private progress storage and restart.");
            foreach (var journal in journals.Take(8)) try
            {
                using var record = sourceRoot.OpenChild(Path.GetFileName(journal)); var plan = StateRecovery.Read<Plan>(journal, 8 * 1024 * 1024); Validate(plan);
                if (Path.GetFileName(journal) != plan.Id + ".json" || plans.ContainsKey(plan.Id)) throw new IOException("Invalid plan ownership.");
                plans.Add(plan.Id, plan); versions.Add(plan.Id, record.Info.Version);
            }
            catch (Exception ex) when (StateRecovery.IsRecordFailure(ex)) { recovery.Add(journal, "This local source plan could not be validated. Preserve its metadata before starting new work; sources, partials and completed copies are kept."); }
            LoadDecisions();
            foreach (var id in batches.Jobs.Select(x => x.Id).Concat(files.Jobs.Select(x => x.BatchId)).Where(x => x.Length != 0).Distinct()) EnsureDecision(id);
            batches.Changed += Notify; files.Changed += NotifyPayload; files.MetadataChanged += _ => Notify();
            files.Activity = value => WindowsActivity.Publish(Activity, value); batches.Activity = value => WindowsActivity.Publish(Activity, value);
        }
        catch { decisionRoot?.Dispose(); batchRoot?.Dispose(); filesRoot?.Dispose(); receivingRoot?.Dispose(); sourceRoot?.Dispose(); lease?.Dispose(); root.Dispose(); throw; }
    }
    private static SecureWindowsDirectory Folder(SecureWindowsDirectory parent, string name)
    {
        try { return parent.CreateDirectory(name); }
        catch (StorageException ex) when (ex.NativeError is 80 or 183) { return parent.OpenChild(name, true); }
    }
    private void NotifyPayload(DownloadJob row)
    {
        if (closed || PayloadChanged is not { } listeners) return;
        foreach (Action<DownloadJob> listener in listeners.GetInvocationList()) try { listener(row); } catch (Exception) { }
    }
    private static TransferManifest Manifest(Plan plan) => new(plan.Nodes.Select(x => new TransferItem(x.Parent, plan.Roots[x.Root].Id, x.Path.ToArray(), x.Name, x.Directory, x.Size, x.Version, x.Error)).ToArray());
    public IReadOnlyList<LocalBatchCopyJob> Jobs
    {
        get { lock (gate)
        {
            var saved = batches.Jobs.ToDictionary(x => x.Id);
            var receipts = files.Jobs;
            return plans.Keys.Concat(saved.Keys).Concat(decisions.Keys).Concat(readyDecisions.Keys).Concat(receipts.Select(x => x.BatchId).Where(x => x.Length != 0)).Distinct().Select(id =>
            {
                plans.TryGetValue(id, out var plan); saved.TryGetValue(id, out var transfer); var loose = receipts.Where(x => x.BatchId == id).ToArray();
                return new LocalBatchCopyJob(id, plan?.Roots.Select(x => x.Display).ToArray() ?? ["Source plan unavailable"], plan?.Nodes.Length ?? transfer?.Items.Length ?? loose.Length,
                    plan?.Nodes.Count(x => !x.Directory) ?? transfer?.Items.Count(x => !x.Source.Directory) ?? loose.Length,
                    plan?.Nodes.Count(x => x.Directory) ?? transfer?.Items.Count(x => x.Source.Directory) ?? 0,
                    plan is not null ? Manifest(plan).Bytes : transfer?.Items.Where(x => !x.Source.Directory && x.Source.Error.Length == 0).Sum(x => x.Source.Size) ?? loose.Sum(x => x.Size),
                    plan?.Nodes.Count(x => x.Error.Length != 0) ?? transfer?.Issues ?? 0, Clone(transfer), plan is not null,
                    plan?.Schema != 3 || !readyDecisions.ContainsKey(id) || decisions.ContainsKey(id) || transfer is not null || loose.Length != 0,
                    plan?.Move is { } move ? Path.Combine(move.Destination.Directory, move.Name) : "");
            }).Reverse().ToArray();
        } }
    }
    private static BatchDownload? Clone(BatchDownload? row) => row is null ? null : row with { Items = row.Items.Select(x => x with { Source = x.Source with { SourcePath = x.Source.SourcePath.ToArray() } }).ToArray() };
    public IReadOnlyList<LocalBatchCopyItem> Page(string id, int page)
    {
        lock (gate)
        {
            if (page < 0) throw new ArgumentOutOfRangeException(nameof(page));
            var plan = plans[id]; return plan.Nodes.Select((x, i) => new LocalBatchCopyItem(i, OutputPath(plan.Nodes, i), x.Directory, x.Size, x.Error)).Skip(checked(page * 20)).Take(20).ToArray();
        }
    }
    private static string OutputPath(Node[] nodes, int index)
    { var path = new List<string>(); for (var i = index; i >= 0; i = nodes[i].Parent) path.Add(nodes[i].Name); path.Reverse(); return string.Join(" / ", path); }
    public IReadOnlyList<StateRecoveryIssue> RecoveryIssues { get { lock (gate) return recovery.Issues.Concat(batches.RecoveryIssues).Concat(files.RecoveryIssues).ToArray(); } }
    public int PreservedRecords { get { lock (gate) return recovery.ArchivedCount + batches.PreservedRecords + files.PreservedRecords; } }
    public bool Active(string id) { lock (gate) return batches.Active(id) || Verifying(id); }
    private void Healthy() { ObjectDisposedException.ThrowIf(closed, this); recovery.RequireHealthy(); RequireDecisions(); files.RequireHealthy(); if (batches.RecoveryIssues.Count != 0) throw new IOException("Preserve damaged local receiving plans before continuing."); }
    public Task<string> PrepareAsync(IReadOnlyList<LocalCopySelection> selected, CancellationToken ct = default) =>
        PrepareAsync(selected, null, ct);

    private Task<string> PrepareAsync(IReadOnlyList<LocalCopySelection> selected, FolderMoveTarget? move, CancellationToken ct)
    {
        lock (gate)
        {
            Healthy(); if (selected.Count is < 1 or > 128) throw new IOException("Select between one and 128 files or folders.");
            if (planning.Count != 0) throw new IOException("Wait for or cancel the current local planning operation.");
            if (Jobs.Count >= 8) throw new IOException("Review or remove old local batch progress first. Eight source or receiving contexts are retained.");
            var selections = selected.ToArray(); var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
            var work = Task.Run(() =>
            {
                var plan = Scan(selections, cancellation.Token) with { Move = move };
                if (move is not null) ValidateMove(plan, move);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(plan);
                if (bytes.Length > 8 * 1024 * 1024) throw new IOException("The local source plan exceeds its storage limit.");
                lock (gate)
                {
                    Healthy(); cancellation.Token.ThrowIfCancellationRequested();
                    if (Jobs.Count >= 8 || plans.Values.Sum(x => Manifest(x).Bytes) > 16L * 1024 * 1024 * 1024 - Manifest(plan).Bytes) throw new IOException("Retained local plans exceed eight contexts or 16 GiB of source sizes.");
                    using var record = sourceRoot.CreateFile(plan.Id + ".json.new");
                    try { record.Write(0, bytes); record.Flush(); record.RenameTo(sourceRoot, plan.Id + ".json"); CreateReady(plan.Id); plans.Add(plan.Id, plan); versions.Add(plan.Id, SecureWindowsDirectory.Inspect(record.Handle).Version); }
                    catch { if (!plans.ContainsKey(plan.Id)) record.Delete(); throw; }
                    WindowsActivity.Publish(Activity, new(WindowsActivityKind.LocalBatchPlan, WindowsActivityOutcome.Accepted, Bytes: Manifest(plan).Bytes));
                    return plan.Id;
                }
            }, cancellation.Token);
            planning.Add(work); _ = work.ContinueWith(_ => { lock (gate) { planning.Remove(work); cancellation.Dispose(); } Notify(); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            Notify(); return work;
        }
    }
    public string ChooseDestination(string id, string destination, DownloadCollision collision)
        => StartCopy(id, destination, collision, null);

    private string StartCopy(string id, string destination, DownloadCollision collision, FolderMoveTarget? move)
    {
        lock (gate)
        {
            Healthy(); if (!Enum.IsDefined(collision)) throw new IOException("Choose a supported collision policy.");
            RequireDecisions(); if (Jobs.Single(x => x.Id == id).CopyMayExist) throw new IOException("This source may already have produced copies. Resume its original batch, or review and remove missing progress before making a fresh source selection.");
            var plan = plans[id]; RequireRecord(plan);
            if (plan.Move != move) throw new IOException("Use the destination bound to this folder move.");
            using var target = StorageGrantStore.OpenLocalDestination(destination);
            if (plan.Roots.Any(x => !x.FileOnly && SecureWindowsDirectory.IsWithin(target.Info.CanonicalPath, x.Canonical)))
                throw new IOException("Choose a destination outside the selected source folders.");
            if (move is not null && (target.Info.CanonicalPath != move.Destination.CanonicalPath || target.Info.Identity != move.Destination.Identity))
                throw new IOException("The reviewed destination folder changed.");
            EnsureDecision(id);
            var result = move is null ? batches.Start("", Manifest(plan), destination, collision, Reader(plan), id) :
                batches.StartFolderMove(Manifest(plan), move.Destination, move.Name, Reader(plan), id);
            NotifyWhenIdle(id); return result;
        }
    }
    public Task Resume(string id)
    {
        lock (gate)
        {
            Healthy(); TransferManifests.Request request;
            RequireContextIdle(id);
            if (plans.TryGetValue(id, out var plan)) { RequireRecord(plan); request = Reader(plan); }
            else request = (_, _) => throw new IOException("The source plan is missing. Completed files are kept; select sources as a new plan if further bytes are needed.");
            var work = batches.Resume(id, request); NotifyWhenIdle(id); return work;
        }
    }
    public void Pause(string id) { lock (gate) batches.Pause(id); }
    public Task WaitAsync(string id) => batches.WaitAsync(id);
    public Task Cancel(string id)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            RequireContextIdle(id);
            if (batches.Jobs.All(x => x.Id != id))
            { if (Jobs.Single(x => x.Id == id).CopyMayExist) throw new IOException("Review unattached receipts and missing batch progress before removing this context."); RequireDecisions(); RemovePlan(id); RemoveReady(id); Notify(); return Task.CompletedTask; }
            var work = batches.Cancel(id); NotifyWhenIdle(id, work); return work;
        }
    }
    public bool CanRemove(string id) { lock (gate) return batches.Jobs.Any(x => x.Id == id) && batches.CanRemove(id); }
    public void Remove(string id) { lock (gate) { ObjectDisposedException.ThrowIf(closed, this); RequireContextIdle(id); ReceivingStamp(); RequireDecisions(); batches.Remove(id); RemovePlan(id); RemoveReady(id); RemoveDecision(id); folderMoveApprovals.Remove(id); Notify(); } }
    public void Preserve(string id)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closed, this); if (Planning || orphanWork.Count != 0 || batches.Jobs.Any(x => Active(x.Id))) throw new IOException("Pause local batch work before preserving metadata.");
            if (recovery.Issues.Any(x => x.Id == id)) recovery.Archive(id); else if (batches.RecoveryIssues.Any(x => x.Id == id)) batches.ArchiveRecovery(id); else files.ArchiveRecovery(id); Notify();
        }
    }
    public void ClearPreserved()
    { lock (gate) { ObjectDisposedException.ThrowIf(closed, this); if (Planning || orphanWork.Count != 0 || batches.Jobs.Any(x => Active(x.Id))) throw new IOException("Pause local batch work first."); recovery.ClearArchives(); batches.ClearPreservedRecords(); files.ClearPreservedRecords(); Notify(); } }
    private void RequireRecord(Plan plan)
    { using var record = sourceRoot.OpenChild(plan.Id + ".json"); if (record.Info.Version != versions[plan.Id]) throw new IOException("The saved local plan changed. Restart and review its metadata."); }
    private void RemovePlan(string id)
    {
        if (!plans.TryGetValue(id, out var plan)) return; RequireRecord(plan);
        using var record = sourceRoot.OpenMutableChild(id + ".json"); if (record.Info.Version != versions[id]) throw new IOException("The saved plan changed.");
        record.Delete(); plans.Remove(id); versions.Remove(id);
    }
    internal T WithReceivingPartProtection<T>(Func<ReceivingPartProtection, T> operation)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            if (orphanWork.Count != 0) throw new IOException("Wait for local receipt verification or cleanup before reviewing partial files.");
            return batches.WithReceivingPartProtection(batch => files.WithReceivingPartProtection(file =>
            { using var merged = new ReceivingPartProtection(); merged.Merge(batch); merged.Merge(file); return operation(merged); }));
        }
    }
    private void NotifyWhenIdle(string id, Task? task = null) => _ = (task ?? batches.WaitAsync(id)).ContinueWith(done => { _ = done.Exception; Notify(); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    private void Notify() { if (closed || Changed is not { } listeners) return; foreach (Action listener in listeners.GetInvocationList()) try { listener(); } catch (Exception) { } }
    public async ValueTask DisposeAsync()
    {
        Task[] scans, controls; Task? checking;
        lock (gate)
        {
            if (closed) return;
            closed = true; lifetime.Cancel();
            foreach (var job in batches.Jobs) batches.Pause(job.Id);
            foreach (var id in orphanWork.Keys) files.Pause(id);
            scans = planning.ToArray(); controls = orphanWork.Values.Select(x => x.Task).ToArray(); checking = verification?.Task;
        }
        try
        {
            try { await Task.WhenAll(scans); } catch (Exception ex) when (ex is OperationCanceledException || StateRecovery.IsRecordFailure(ex)) { }
            if (checking is not null)
                try { await checking; } catch (Exception ex) when (ex is OperationCanceledException || StateRecovery.IsRecordFailure(ex)) { }
            try { await batches.DisposeAsync(); } finally { try { await Task.WhenAll(controls); } finally { await files.DisposeAsync(); } }
        }
        finally { lifetime.Dispose(); decisionRoot.Dispose(); batchRoot.Dispose(); filesRoot.Dispose(); receivingRoot.Dispose(); sourceRoot.Dispose(); lease.Dispose(); root.Dispose(); }
    }
}
