using IConnect.Core;
using IConnect.Protocol;
using System.Text.Json;

namespace IConnect.Storage.Windows;

public enum BatchPhase { Running, Paused, Interrupted, Completed, CompletedWithIssues, Canceled }
public enum BatchItemPhase { Pending, Creating, Downloading, Completed, Skipped, Failed, Canceled, Interrupted }
public sealed record BatchItem(TransferItem Source, BatchItemPhase Phase = BatchItemPhase.Pending, string LocalName = "",
    string DirectoryIdentity = "", string DownloadId = "", string Hash = "", string Error = "", BatchSourceApproval? SourceApproval = null,
    string FileIdentity = "");
public sealed record BatchDownload(string Id, string PeerId, string RootPath, string RootIdentity, string DisplayPath,
    DownloadCollision Collision, BatchItem[] Items, BatchPhase Phase = BatchPhase.Running, string Error = "", string MoveName = "")
{
    public int Completed => Items.Count(x => x.Phase == BatchItemPhase.Completed);
    public int Issues => Items.Count(x => x.Phase is BatchItemPhase.Failed or BatchItemPhase.Skipped);
    public long VerifiedBytes => Items.Where(x => !x.Source.Directory && x.Phase == BatchItemPhase.Completed).Sum(x => x.Source.Size);
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasBoundDestination => Phase == BatchPhase.Completed &&
        Items.All(x => x.Phase == BatchItemPhase.Completed && (x.Source.Directory || x.FileIdentity.Length != 0));
}

/// <summary>Resumable local copy orchestration. File bytes use the existing integrity-checked receiver.</summary>
public sealed partial class WindowsBatchDownloads : IAsyncDisposable
{
    private sealed class Work
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public Task Task { get; set; } = Task.CompletedTask;
        public string Child { get; set; } = "";
    }
    private readonly object gate;
    private readonly bool localCopy;
    private string StoppedError => localCopy ? "The app stopped. Review the local destination and resume." : "The app stopped. Reconnect to the same device and resume.";
    private readonly string directory;
    private readonly string partRecoveryDirectoryIdentity;
    private readonly WindowsDownloadManager files;
    private readonly Dictionary<string, BatchDownload> jobs = [];
    private readonly Dictionary<string, Work> running = [];
    private readonly Dictionary<string, Task> canceling = [];
    private readonly SemaphoreSlim concurrency = new(2);
    private bool disposed;
    private readonly StateRecovery recovery;
    public IReadOnlyList<StateRecoveryIssue> RecoveryIssues { get { lock (gate) return recovery.Issues; } }
    public int PreservedRecords { get { lock (gate) return recovery.ArchivedCount; } }
    public void ClearPreservedRecords() { lock (gate) recovery.ClearArchives(); }
    public void ArchiveRecovery(string id) { lock (gate) { if (running.Count != 0 || canceling.Count != 0 || sourceWork.Count != 0 || sourceReviews.Count != 0) throw new IOException("Pause batch work and close source reviews before reviewing saved records."); recovery.Archive(id); } Changed?.Invoke(); }
    public event Action? Changed;
    public Action<WindowsActivityEvent>? Activity { get; set; }
    public IReadOnlyList<BatchDownload> Jobs { get { lock (gate) return jobs.Values.Reverse().ToArray(); } }
    public WindowsBatchDownloads(string stateDirectory, WindowsDownloadManager files) : this(stateDirectory, files, false, null) { }
    internal WindowsBatchDownloads(string stateDirectory, WindowsDownloadManager files, bool localCopy, object? ownerGate)
    {
        gate = ownerGate ?? files.OwnershipGate; this.localCopy = localCopy;
        this.files = files; directory = Path.Combine(stateDirectory, "batches"); Directory.CreateDirectory(directory);
        using (var root = SecureWindowsDirectory.OpenRoot(directory)) partRecoveryDirectoryIdentity = root.Info.Identity;
        recovery = new(directory); var paths = StateRecovery.Journals(directory);
        if (paths.Length > 1024) recovery.Block("More than 1,024 batch records exist. Review the private queue folder and restart the app.");
        foreach (var path in paths.Take(1024))
        {
            try
            {
                if (jobs.Count == 8) throw new InvalidDataException("The saved batch queue exceeds its record limit.");
                var job = StateRecovery.Read<BatchDownload>(path, 8 * 1024 * 1024); Validate(job);
                if (Path.GetFileNameWithoutExtension(path) != job.Id || !SecureWindowsDirectory.IsLocalVolumePath(job.RootPath)) throw new InvalidDataException("Invalid batch record owner or destination.");
                if (job.Phase == BatchPhase.Running) job = job with { Phase = BatchPhase.Interrupted, Error = StoppedError };
                jobs.Add(job.Id, job);
            }
            catch (Exception ex) when (StateRecovery.IsRecordFailure(ex))
            { recovery.Add(path, "This batch's saved progress could not be validated. Copied files, folders and private partials were kept."); }
        }
        files.MetadataChanged += FileChanged;
    }
    private void FileChanged(DownloadJob file) { if (file.BatchId.Length != 0) Changed?.Invoke(); }
    public string Start(string peer, TransferManifest manifest, string destination, DownloadCollision collision, TransferManifests.Request request, string? id = null)
        => StartCore(peer, manifest, destination, collision, request, id, null, "");

    internal string StartFolderMove(TransferManifest manifest, LocalMoveDestination target, string name, TransferManifests.Request request, string id)
        => StartCore("", manifest, target.Directory, DownloadCollision.Fail, request, id, target, name);

    private string StartCore(string peer, TransferManifest manifest, string destination, DownloadCollision collision,
        TransferManifests.Request request, string? id, LocalMoveDestination? target, string moveName)
    {
        TransferManifests.Validate(manifest);
        using var root = StorageGrantStore.OpenLocalDestination(destination);
        if (target is not null && (root.Info.Identity != target.Identity || root.Info.CanonicalPath != target.CanonicalPath))
            throw new IOException("The reviewed destination folder changed.");
        var items = manifest.Items.Select(x => new BatchItem(x with { SourcePath = x.SourcePath.ToArray() },
            x.Error.Length == 0 ? BatchItemPhase.Pending : BatchItemPhase.Failed, Error: x.Error)).ToArray();
        var job = new BatchDownload(id ?? ProtocolRules.NewToken(), peer, root.Info.CanonicalPath, root.Info.Identity,
            Path.GetFullPath(destination), collision, items, MoveName: moveName);
        if (target is not null) Validate(job);
        Validate(job);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            recovery.RequireHealthy();
            files.RequireHealthy();
            if (jobs.ContainsKey(job.Id)) throw new IOException("This batch already has saved progress.");
            if (jobs.Count == 8) throw new InvalidOperationException("Remove an old batch record before adding another.");
            Persist(job); jobs.Add(job.Id, job); Launch(job.Id, request);
        }
        Changed?.Invoke(); return job.Id;
    }
    public Task WaitAsync(string id) { lock (gate) return running.TryGetValue(id, out var work) ? work.Task : Task.CompletedTask; }
    public void Pause(string id) { lock (gate) { if (running.TryGetValue(id, out var work)) work.Cancellation.Cancel(); } }
    public Task Resume(string id, TransferManifests.Request request)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            recovery.RequireHealthy();
            files.RequireHealthy();
            if (canceling.ContainsKey(id)) throw new InvalidOperationException("Wait for batch cancellation and cleanup to finish.");
            if (sourceWork.ContainsKey(id) || sourceReviews.Values.Any(x => x.Value.Batch == id)) throw new IOException("Close this batch's source review before resuming it.");
            if (running.TryGetValue(id, out var work)) return work.Task;
            if (jobs[id].Phase is not (BatchPhase.Paused or BatchPhase.Interrupted)) return Task.CompletedTask;
            Launch(id, request); return running[id].Task;
        }
    }
    public Task Cancel(string id)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!jobs.ContainsKey(id)) throw new InvalidOperationException("This batch record is no longer available.");
            if (canceling.TryGetValue(id, out var existing)) return existing;
            // Register cleanup before CancelCore can invoke synchronous cancellation
            // callbacks. Reentrant observers must receive the same cleanup task.
            var task = Task.Run(async () =>
            {
                try { await CancelCore(id); }
                finally { lock (gate) canceling.Remove(id); Changed?.Invoke(); }
            });
            canceling.Add(id, task); return task;
        }
    }
    private async Task CancelCore(string id)
    {
        await CancelSourceRecoveryAsync(id);
        Pause(id); await WaitAsync(id);
        var job = Get(id);
        foreach (var child in files.Jobs.Where(x => x.BatchId == id))
        {
            await files.Cancel(child.Id);
            var result = files.Jobs.Single(x => x.Id == child.Id);
            var index = Array.FindIndex(job.Items, x => x.DownloadId == child.Id);
            if (index >= 0) job = SetItem(job, index, Outcome(job.Items[index], result));
            if (result.Phase is DownloadPhase.Completed or DownloadPhase.Skipped || result.Phase == DownloadPhase.Canceled && result.Error.Length == 0)
                files.RemoveRecord(result.Id);
        }
        job = Get(id);
        var pending = job.Items.Select(x => x.Phase is BatchItemPhase.Completed or BatchItemPhase.Skipped ? x : x with
            { Phase = BatchItemPhase.Canceled, Error = x.Error.Length == 0 ? "Canceled. Already saved files and folders were kept." : x.Error }).ToArray();
        Set(job with { Items = pending, Phase = BatchPhase.Canceled,
            Error = files.Jobs.Any(x => x.BatchId == id) ? "Some private partial files need cleanup. Retry cancel before removing this record." : "Saved files and created folders were kept." });
    }
    public bool CanRemove(string id) => !IsRunning(id) && (Get(id).Phase is BatchPhase.Completed or BatchPhase.CompletedWithIssues or BatchPhase.Canceled) && !files.Jobs.Any(x => x.BatchId == id);
    public void Remove(string id)
    {
        var childrenRemain = files.Jobs.Any(x => x.BatchId == id);
        lock (gate)
        {
            if (childrenRemain || running.ContainsKey(id) || canceling.ContainsKey(id) ||
                jobs[id].Phase is not (BatchPhase.Completed or BatchPhase.CompletedWithIssues or BatchPhase.Canceled))
                throw new InvalidOperationException("Cancel unfinished file cleanup before removing the batch record.");
            File.Delete(Path.Combine(directory, id + ".json")); jobs.Remove(id);
        }
        Changed?.Invoke();
    }
    private bool IsRunning(string id) { lock (gate) return running.ContainsKey(id) || canceling.ContainsKey(id) || sourceWork.ContainsKey(id) || sourceReviews.Values.Any(x => x.Value.Batch == id); }
    internal bool Active(string id) => IsRunning(id);
    private BatchDownload Get(string id) { lock (gate) return jobs[id]; }
    private void Launch(string id, TransferManifests.Request request)
    {
        var work = new Work(); running.Add(id, work); work.Task = Task.Run(() => Run(id, request, work));
    }
    private async Task Run(string id, TransferManifests.Request request, Work work)
    {
        var entered = false; var ct = work.Cancellation.Token; var currentItem = -1;
        try
        {
            await concurrency.WaitAsync(ct); entered = true;
            var job = Set(Get(id) with { Phase = BatchPhase.Running, Error = "" });
            var resolver = new ManifestSourceResolver(request);
            for (var i = 0; i < job.Items.Length; i++)
            {
                ct.ThrowIfCancellationRequested(); job = Get(id); var item = job.Items[i]; currentItem = i;
                if (item.Phase is BatchItemPhase.Completed or BatchItemPhase.Skipped or BatchItemPhase.Failed or BatchItemPhase.Canceled) continue;
                if (item.Source.Parent >= 0 && job.Items[item.Source.Parent].Phase != BatchItemPhase.Completed)
                { SetItem(job, i, item with { Phase = BatchItemPhase.Skipped, Error = "The parent folder was not copied." }); continue; }
                using var parent = OpenParent(job, item.Source.Parent);
                if (item.Source.Directory)
                {
                    if (item.Phase == BatchItemPhase.Creating)
                        throw new IOException("Folder creation was interrupted before its identity was saved. Review the destination; start a new copy to retry this folder.");
                    _ = await resolver.ResolveAsync(item.Source, ct);
                    for (var number = 1; number <= 10_000; number++)
                    {
                        var name = i == 0 && job.MoveName.Length != 0 ? job.MoveName : DirectoryName(item.Source.Name, number);
                        job = SetItem(Get(id), i, item with { Phase = BatchItemPhase.Creating, LocalName = name, Error = "" });
                        try
                        {
                            using var created = parent.CreateDirectory(name);
                            SetItem(job, i, job.Items[i] with { Phase = BatchItemPhase.Completed, DirectoryIdentity = created.Info.Identity }); break;
                        }
                        catch (StorageException ex) when (ex.NativeError is 80 or 183)
                        {
                            if (job.Collision == DownloadCollision.KeepBoth && number < 10_000) continue;
                            SetItem(job, i, job.Items[i] with { Phase = job.Collision == DownloadCollision.Skip ? BatchItemPhase.Skipped : BatchItemPhase.Failed,
                                Error = "An existing destination was kept. Folders are never merged or replaced." }); break;
                        }
                    }
                    continue;
                }
                work.Child = item.DownloadId.Length == 0 ? ProtocolRules.NewToken() : item.DownloadId;
                job = SetItem(job, i, item with { Phase = BatchItemPhase.Downloading, DownloadId = work.Child, Error = "" });
                var child = files.Jobs.SingleOrDefault(x => x.Id == work.Child);
                if (child is null)
                {
                    var source = await resolver.ResolveAsync(item.Source, ct);
                    ct.ThrowIfCancellationRequested();
                    files.Start(job.PeerId, source, DisplayParent(job, item.Source.Parent), job.Collision,
                        (value, token) => request(value, token), work.Child, parent.Info.Identity, id);
                }
                else
                {
                    if (child.BatchId != id || child.PeerId != job.PeerId || child.Name != item.Source.Name || !MatchesSource(item, child) ||
                        child.Size != item.Source.Size || child.RootIdentity != parent.Info.Identity)
                        throw new InvalidDataException("The saved child transfer does not belong to this batch destination.");
                    if (child.Phase is not (DownloadPhase.Completed or DownloadPhase.Canceled or DownloadPhase.Skipped))
                        _ = files.ResumeBatchFile(child.Id, (value, token) => request(value, token), id);
                }
                await files.WaitAsync(work.Child).WaitAsync(ct);
                child = files.Jobs.Single(x => x.Id == work.Child);
                job = SetItem(Get(id), i, Outcome(Get(id).Items[i], child)); work.Child = "";
                if (child.Phase is DownloadPhase.Completed or DownloadPhase.Skipped || child.Phase == DownloadPhase.Canceled && child.Error.Length == 0)
                    files.RemoveRecord(child.Id);
                if (child.Phase is DownloadPhase.Interrupted or DownloadPhase.Paused)
                    throw new IOException(child.Error.Length == 0 ? "The file transfer stopped. Reconnect and resume." : child.Error);
            }
            job = Get(id);
            Set(job with { Phase = job.Items.All(x => x.Phase == BatchItemPhase.Completed) ? BatchPhase.Completed : BatchPhase.CompletedWithIssues,
                Error = job.Items.All(x => x.Phase == BatchItemPhase.Completed) ? "" : "Some items were skipped or failed. Review the item results." });
        }
        catch (OperationCanceledException) { TrySet(InterruptedItem(Get(id), currentItem, localCopy ? "Paused. Saved local items were kept; resume when ready." : "Paused. Saved items were kept; resume after reconnecting.") with { Phase = BatchPhase.Paused }); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        { TrySet(InterruptedItem(Get(id), currentItem, TransferManifests.SafeError(ex.Message))); }
        finally
        {
            if (work.Child.Length != 0 && files.Jobs.Any(x => x.Id == work.Child))
            { files.Pause(work.Child); await files.WaitAsync(work.Child); }
            if (entered) concurrency.Release();
            lock (gate) { running.Remove(id); work.Cancellation.Dispose(); }
            Changed?.Invoke();
        }
    }
    private static BatchDownload InterruptedItem(BatchDownload job, int index, string error)
    {
        if (index >= 0 && job.Items[index].Phase is not (BatchItemPhase.Completed or BatchItemPhase.Skipped or BatchItemPhase.Failed or BatchItemPhase.Canceled))
        {
            var items = job.Items.ToArray(); var old = items[index];
            items[index] = old with { Phase = old.Phase == BatchItemPhase.Creating ? old.Phase : BatchItemPhase.Interrupted, Error = error };
            job = job with { Items = items };
        }
        return job with { Phase = BatchPhase.Interrupted, Error = error };
    }
    private static BatchItem Outcome(BatchItem item, DownloadJob child) => item with
    {
        Phase = child.Phase switch { DownloadPhase.Completed => BatchItemPhase.Completed, DownloadPhase.Skipped => BatchItemPhase.Skipped,
            DownloadPhase.Failed => BatchItemPhase.Failed, DownloadPhase.Canceled => BatchItemPhase.Canceled, _ => BatchItemPhase.Downloading },
        LocalName = child.FinalName, Hash = child.Hash, Error = child.Error,
        FileIdentity = child.Phase == DownloadPhase.Completed ? child.TemporaryIdentity : ""
    };
    private static List<int> Parents(BatchDownload job, int parent)
    {
        var chain = new List<int>();
        while (parent >= 0) { chain.Add(parent); parent = job.Items[parent].Source.Parent; }
        chain.Reverse(); return chain;
    }
    private static SecureWindowsDirectory OpenParent(BatchDownload job, int parent) => SecureWindowsDirectory.OpenBoundPath(job.RootPath, job.RootIdentity,
        Parents(job, parent).Select(i => (job.Items[i].LocalName, job.Items[i].DirectoryIdentity)).ToArray());
    private static string DisplayParent(BatchDownload job, int parent) => Parents(job, parent).Aggregate(job.DisplayPath, (path, i) => Path.Combine(path, job.Items[i].LocalName));
    private static string DirectoryName(string name, int number)
    {
        if (number == 1) return name;
        var suffix = $" ({number})"; var stem = name[..Math.Min(name.Length, 255 - suffix.Length)].TrimEnd(' ', '.');
        if (stem.Length > 0 && char.IsHighSurrogate(stem[^1])) stem = stem[..^1];
        var result = stem + suffix; StorageRules.ValidateName(result); return result;
    }
    private BatchDownload SetItem(BatchDownload job, int index, BatchItem item)
    {
        var items = job.Items.ToArray(); items[index] = item; return Set(job with { Items = items });
    }
    private BatchDownload Set(BatchDownload job)
    {
        lock (gate) {
            var previous = jobs.GetValueOrDefault(job.Id)?.Phase; Persist(job); jobs[job.Id] = job;
            WindowsActivityOutcome? outcome = job.Phase switch { BatchPhase.Completed => WindowsActivityOutcome.Completed, BatchPhase.CompletedWithIssues => WindowsActivityOutcome.CompletedWithIssues,
                BatchPhase.Paused or BatchPhase.Interrupted => WindowsActivityOutcome.Paused, BatchPhase.Canceled => WindowsActivityOutcome.Canceled, _ => null };
            if (previous != job.Phase && outcome is { } result) WindowsActivity.Publish(Activity, new(localCopy ? WindowsActivityKind.LocalBatchCopy : WindowsActivityKind.DownloadBatch, result, job.PeerId,
                job.Phase is BatchPhase.Completed or BatchPhase.CompletedWithIssues ? job.VerifiedBytes : -1));
        } Changed?.Invoke(); return job;
    }
    private void TrySet(BatchDownload job)
    {
        try { Set(job); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            lock (gate) jobs[job.Id] = job with { Phase = BatchPhase.Interrupted, Error = "Progress could not be saved. The previous durable state will be checked on resume." };
            WindowsActivity.Publish(Activity, new(localCopy ? WindowsActivityKind.LocalBatchCopy : WindowsActivityKind.DownloadBatch, WindowsActivityOutcome.Review, job.PeerId));
        }
    }
    private void Persist(BatchDownload job)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(job);
        if (bytes.Length > 8 * 1024 * 1024) throw new InvalidDataException("The batch record exceeds its storage limit.");
        var path = Path.Combine(directory, job.Id + ".json");
        using (var stream = new FileStream(path + ".new", FileMode.Create, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
        AtomicStateFile.Commit(path + ".new", path);
    }
    private void Validate(BatchDownload job)
    {
        if (!ProtocolRules.TokenPattern().IsMatch(job.Id) || (localCopy ? job.PeerId.Length != 0 : job.PeerId.Length != 64 || job.PeerId.Any(c => !char.IsAsciiHexDigitLower(c))) ||
            !Enum.IsDefined(job.Phase) || !Enum.IsDefined(job.Collision) || job.RootPath.Length is < 3 or > 8192 || job.RootIdentity.Length is < 1 or > 256 ||
            job.DisplayPath.Length is < 3 or > 8192 || job.Error.Length > 512 || job.Items is null)
            throw new InvalidDataException("Invalid batch state.");
        TransferManifests.Validate(new(job.Items.Select(x => x.Source).ToArray()));
        if (job.MoveName.Length != 0)
        {
            StorageRules.ValidateName(job.MoveName);
            if (!localCopy || job.Collision != DownloadCollision.Fail || !job.Items[0].Source.Directory ||
                job.Items.Count(x => x.Source.Parent == -1) != 1)
                throw new InvalidDataException("Invalid folder move destination.");
        }
        foreach (var item in job.Items)
        {
            if (!Enum.IsDefined(item.Phase) || item.Error.Length > 512 || item.DirectoryIdentity.Length > 256 ||
                item.FileIdentity.Length > 256 || item.FileIdentity.Any(char.IsControl) ||
                item.FileIdentity.Length != 0 && (item.Source.Directory || item.Phase != BatchItemPhase.Completed) ||
                item.DownloadId.Length != 0 && !ProtocolRules.TokenPattern().IsMatch(item.DownloadId) ||
                item.Hash.Length is not (0 or 64) || item.Hash.Any(c => !char.IsAsciiHexDigitLower(c))) throw new InvalidDataException("Invalid batch item state.");
            if (item.LocalName.Length != 0) StorageRules.ValidateName(item.LocalName);
            if (item.Phase == BatchItemPhase.Completed && (item.Source.Directory ? item.DirectoryIdentity.Length == 0 || item.LocalName.Length == 0 : item.Hash.Length != 64 || item.LocalName.Length == 0))
                throw new InvalidDataException("A completed batch item has no durable identity or receipt.");
            if (item.Source.Error.Length != 0 && item.Phase is not (BatchItemPhase.Failed or BatchItemPhase.Canceled)) throw new InvalidDataException("Unsupported items cannot become approved transfers.");
            ValidateSourceApproval(item);
        }
    }
    public async ValueTask DisposeAsync()
    {
        Task[] tasks;
        PendingBatchSource[] reviews;
        lock (gate) { if (disposed) return; disposed = true; sourceLifetime.Cancel(); foreach (var work in running.Values) work.Cancellation.Cancel();
            reviews = sourceReviews.Values.ToArray(); sourceReviews.Clear(); foreach (var review in reviews) review.Authority.Invalidated = true;
            tasks = running.Values.Select(x => x.Task).Concat(canceling.Values).Concat(sourceWork.Values.Select(x => x.Fence.Task)).ToArray(); }
        try { await Task.WhenAll(tasks); }
        finally { foreach (var review in reviews) await files.CancelSourceReviewAsync(review.Value.Source.Id); files.MetadataChanged -= FileChanged; concurrency.Dispose(); sourceLifetime.Dispose(); }
    }
}
