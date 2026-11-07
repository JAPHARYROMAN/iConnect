using IConnect.Core;
using IConnect.Protocol;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace IConnect.Storage.Windows;

public enum DownloadPhase { Preparing, Downloading, Paused, Interrupted, Verifying, Finalizing, Completed, Failed, Canceled, Skipped }
public enum DownloadCollision { Fail, KeepBoth, Skip }
public sealed record DownloadJob(string Id, string PeerId, string Name, string SourceNode, string SourceVersion,
    long Size, string RootPath, string RootIdentity, string DisplayPath, DownloadCollision Collision,
    string Ticket = "", string Hash = "", long Offset = 0, string PrefixHash = "", string TemporaryIdentity = "",
    string FinalName = "", DownloadPhase Phase = DownloadPhase.Preparing, string Error = "", string BatchId = "", int SecurityPolicy = 0, long SourceExpiresAt = 0);

/// <summary>Durable receiving queue. Only explicitly selected local destinations are writable.</summary>
public sealed partial class WindowsDownloadManager : IAsyncDisposable
{
    public delegate Task<FileResponse> Request(FileRequest request, CancellationToken ct);
    private sealed class Work(CancellationTokenSource cancellation, bool publishedOnly = false)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Task { get; set; } = Task.CompletedTask;
        public bool Cancel { get; set; }
        public bool PublishedOnly { get; } = publishedOnly;
        public volatile IncomingFinalizationTracker? Finalization;
    }
    private readonly object gate;
    internal object OwnershipGate => gate;
    private readonly bool localCopy;
    private readonly bool batchOnly;
    private readonly string directory;
    private readonly string partRecoveryDirectoryIdentity;
    private readonly Dictionary<string, DownloadJob> jobs = [];
    private readonly Dictionary<string, Work> running = [];
    private readonly Dictionary<string, Request> requesters = [];
    private readonly Dictionary<string, long> lastNotified = [];
    private readonly SemaphoreSlim concurrency = new(2);
    private bool disposed;
    private readonly StateRecovery recovery;
    private readonly IAttachmentChecks attachmentChecks;
    private readonly TimeProvider clock;
    private readonly long finalizationOrigin;
    private long FinalizationNow => clock.GetElapsedTime(finalizationOrigin).Ticks / TimeSpan.TicksPerMillisecond;
    public IReadOnlyList<StateRecoveryIssue> RecoveryIssues { get { lock (gate) return recovery.Issues; } }
    internal void RequireHealthy() { lock (gate) recovery.RequireHealthy(); }
    public int PreservedRecords { get { lock (gate) return recovery.ArchivedCount; } }
    public void ClearPreservedRecords() { lock (gate) recovery.ClearArchives(); }
    public void ArchiveRecovery(string id) { lock (gate) { if (running.Count != 0) throw new IOException("Pause active downloads before reviewing saved records."); recovery.Archive(id); } }
    public event Action<DownloadJob>? Changed;
    public event Action<DownloadJob>? MetadataChanged;
    public Action<WindowsActivityEvent>? Activity { get; set; }
    public IReadOnlyList<DownloadJob> Jobs { get { lock (gate) return jobs.Values.Reverse().ToArray(); } }
    public DownloadJob? FindJob(string id) { lock (gate) return jobs.GetValueOrDefault(id); }
    private IncomingFinalizationTracker? ActiveFinalization(string id) =>
        running.TryGetValue(id, out var work) && !work.Cancellation.IsCancellationRequested &&
        jobs.TryGetValue(id, out var row) && row.Phase is not (DownloadPhase.Completed or DownloadPhase.Canceled or DownloadPhase.Skipped or DownloadPhase.Failed)
            ? work.Finalization : null;
    public bool HasFinalizationProgress { get { lock (gate) return running.Keys.Any(id => ActiveFinalization(id) is not null); } }
    public IncomingFinalizationProgress? ReadFinalization(string id)
    {
        IncomingFinalizationTracker? progress;
        lock (gate) progress = ActiveFinalization(id);
        return progress?.Read();
    }
    public bool HasCompletedAttachmentCheck(string id) { lock (gate) return jobs.TryGetValue(id, out var row) && row.Phase == DownloadPhase.Completed && row.SecurityPolicy == ReceivedFileSecurity.PolicyVersion; }
    public WindowsDownloadManager(string directory) : this(directory, new WindowsAttachmentChecks()) { }
    internal WindowsDownloadManager(string directory, IAttachmentChecks attachmentChecks, bool localCopy = false, object? ownerGate = null, bool batchOnly = false, TimeProvider? clock = null)
    {
        gate = ownerGate ?? new(); this.localCopy = localCopy; this.batchOnly = batchOnly;
        this.clock = clock ?? TimeProvider.System;
        finalizationOrigin = this.clock.GetTimestamp();
        this.attachmentChecks = attachmentChecks;
        this.directory = Path.Combine(directory, "downloads"); Directory.CreateDirectory(this.directory);
        using (var root = SecureWindowsDirectory.OpenRoot(this.directory)) partRecoveryDirectoryIdentity = root.Info.Identity;
        recovery = new(this.directory);
        var paths = StateRecovery.Journals(this.directory);
        if (paths.Length > 1024) recovery.Block("More than 1,024 progress records exist. Review the private queue folder and restart the app.");
        foreach (var path in paths.Take(1024))
        {
            try
            {
                if (jobs.Count == 128) throw new InvalidDataException("The saved queue exceeds its record limit.");
                var job = StateRecovery.Read<DownloadJob>(path, 256 * 1024); Validate(job);
                if (Path.GetFileNameWithoutExtension(path) != job.Id) throw new InvalidDataException("Invalid download record owner.");
                if (job.Phase is DownloadPhase.Preparing or DownloadPhase.Downloading or DownloadPhase.Verifying or DownloadPhase.Finalizing)
                    job = job with { Phase = DownloadPhase.Interrupted, Error = "The application stopped. Resume to verify and continue." };
                jobs.Add(job.Id, job);
            }
            catch (Exception ex) when (StateRecovery.IsRecordFailure(ex))
            { recovery.Add(path, "This file's saved progress could not be validated. Its destination and partial files were left untouched."); }
        }
    }
    public string Start(string peer, FileEntry file, string destination, DownloadCollision collision, Request request,
        string? id = null, string? destinationIdentity = null, string batchId = "")
    {
        if (localCopy ? peer.Length != 0 : peer.Length != 64 || peer.Any(c => !char.IsAsciiHexDigitLower(c)))
            throw new InvalidDataException("Invalid receiving owner.");
        StorageRules.ValidateName(file.Name);
        if (file.IsDirectory || file.Size is < 0 or > StorageRules.MaxTransferSize) throw new InvalidDataException("Choose a supported file.");
        var full = Path.GetFullPath(destination);
        if (!Path.IsPathFullyQualified(full) || full.StartsWith("\\\\", StringComparison.Ordinal)) throw new InvalidDataException("Choose a local destination folder.");
        using var root = StorageGrantStore.OpenLocalDestination(full);
        if (destinationIdentity is not null && root.Info.Identity != destinationIdentity) throw new IOException("The batch destination folder changed.");
        id ??= ProtocolRules.NewToken();
        if (!ProtocolRules.TokenPattern().IsMatch(id) || batchId.Length != 0 && !ProtocolRules.TokenPattern().IsMatch(batchId))
            throw new InvalidDataException("Invalid local transfer ownership.");
        var job = new DownloadJob(id, peer, file.Name, file.Id, file.Version, file.Size,
            root.Info.CanonicalPath, root.Info.Identity, full, collision, BatchId: batchId);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            recovery.RequireHealthy();
            if (jobs.Count >= 128 || jobs.ContainsKey(id)) throw new InvalidDataException("Remove an old download record before adding another.");
            Persist(job); jobs.Add(job.Id, job); Launch(job.Id, request);
        }
        return job.Id;
    }
    public Task Resume(string id, Request? request) => ResumeCore(id, request, "");
    public Task CheckPublishedAsync(string id)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this); recovery.RequireHealthy(); var job = jobs[id];
            if (running.ContainsKey(id) || job.FinalName.Length == 0 || job.TemporaryIdentity.Length == 0 || job.Offset != job.Size || job.Hash.Length != 64)
                throw new IOException("Choose an idle, fully received copy with a recorded final identity.");
            // This local action has no requester and cannot fetch or create a new copy.
            Set(job with { Phase = DownloadPhase.Interrupted, SecurityPolicy = 0, Error = "Owner requested another Windows attachment check of the recorded copy." });
            Launch(id, null, publishedOnly: true); return running[id].Task;
        }
    }
    internal Task ResumeBatchFile(string id, Request? request, string batchId) => ResumeCore(id, request, batchId);
    private Task ResumeCore(string id, Request? request, string batchId)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            recovery.RequireHealthy();
            var job = jobs[id];
            if (job.BatchId != batchId) throw new InvalidOperationException("Resume this file through its original batch. A missing batch cannot authorize a standalone transfer.");
            if (job.Phase is DownloadPhase.Completed or DownloadPhase.Canceled or DownloadPhase.Skipped) return Task.CompletedTask;
            if (running.TryGetValue(id, out var current)) return current.Task;
            if (request is null && (job.Hash.Length != 64 || job.Offset != job.Size)) throw new InvalidOperationException("Reconnect to the source device before resuming.");
            Launch(id, request); return running[id].Task;
        }
    }
    public void Pause(string id) { lock (gate) { if (running.TryGetValue(id, out var work)) work.Cancellation.Cancel(); } }
    public async Task Cancel(string id)
    {
        Task? active = null;
        lock (gate) if (running.TryGetValue(id, out var work)) { work.Cancel = true; work.Cancellation.Cancel(); active = work.Task; }
        if (active is not null) await active;
        lock (gate)
        {
            var job = jobs[id];
            if (job.Phase == DownloadPhase.Completed) return;
            Set(job with { Phase = DownloadPhase.Canceled, Error = "Canceled. Partial-file cleanup is pending." });
            var cleanupError = "";
            var finalized = false;
            if (job.TemporaryIdentity.Length != 0)
            {
                try
                {
                    using var root = OpenRoot(job);
                    try { using var file = root.OpenTransferFile(job.Id, false); RequireIdentity(job, file); file.Delete(); }
                    catch (StorageException ex) when (ex.Status == FileStatus.NotFound && job.FinalName.Length != 0 && job.Offset == job.Size)
                    {
                        using var final = root.OpenChild(job.FinalName); RequireIdentity(job, final); VerifyFile(final, job, CancellationToken.None);
                        finalized = true;
                    }
                    catch (StorageException ex) when (ex.Status == FileStatus.NotFound) { }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    cleanupError = "Canceled. The partial file could not be safely removed; review the destination locally.";
                }
            }
            Set(job with { Phase = finalized && job.SecurityPolicy == ReceivedFileSecurity.PolicyVersion ? DownloadPhase.Completed : finalized ? DownloadPhase.Interrupted : DownloadPhase.Canceled,
                Error = finalized && job.SecurityPolicy != ReceivedFileSecurity.PolicyVersion ? "The published file was kept untrusted. Resume explicitly to finish Windows attachment checks." : cleanupError });
        }
        Request? requester; DownloadJob canceled;
        lock (gate) { requesters.TryGetValue(id, out requester); canceled = jobs[id]; }
        await ReleaseAsync(canceled, requester);
    }
    public Task WaitAsync(string id) { lock (gate) return running.TryGetValue(id, out var work) ? work.Task : Task.CompletedTask; }
    public void RemoveRecord(string id)
    {
        lock (gate)
        {
            var job = jobs[id];
            if (running.ContainsKey(id) || job.Phase is not (DownloadPhase.Completed or DownloadPhase.Canceled or DownloadPhase.Skipped) ||
                job.Phase == DownloadPhase.Canceled && job.Error.Length != 0)
                throw new InvalidOperationException("Finish or safely cancel the download before removing its record.");
            File.Delete(Path.Combine(directory, id + ".json"));
            jobs.Remove(id); requesters.Remove(id); lastNotified.Remove(id);
        }
    }
    private void Launch(string id, Request? request, bool publishedOnly = false)
    {
        if (request is not null) requesters[id] = request;
        var work = new Work(new CancellationTokenSource(), publishedOnly); running.Add(id, work);
        work.Task = Task.Run(() => RunAsync(id, request, work));
    }
    private async Task RunAsync(string id, Request? request, Work work)
    {
        var entered = false; SecureWindowsDirectory? temporary = null; DownloadJob job;
        lock (gate) job = jobs[id];
        var checkpoint = job;
        var chain = new byte[32]; var offset = job.Offset; var lastSaved = job.Offset;
        var canCheckpoint = false; var finalizing = false;
        IncomingFinalizationTracker BeginFinalization(IncomingFinalizationStage stage)
        {
            var progress = work.Finalization ?? new(id, job.Size, () => FinalizationNow);
            progress.Begin(stage);
            work.Finalization = progress;
            return progress;
        }
        Task CheckPublished(SecureWindowsDirectory root, CancellationToken ct)
        {
            var progress = BeginFinalization(IncomingFinalizationStage.AttachmentCheck);
            return ReceivedFileSecurity.VerifyPublishedAsync(root, job.FinalName, job.TemporaryIdentity, job.Size, job.Hash, attachmentChecks, () => { }, ct,
                () => progress.Begin(IncomingFinalizationStage.CheckingPublishedBytes), progress.Advance);
        }
        try
        {
            await concurrency.WaitAsync(work.Cancellation.Token); entered = true;
            var ct = work.Cancellation.Token;
            if (work.PublishedOnly)
            {
                using var local = OpenRoot(job);
                using (var file = local.OpenChild(job.FinalName))
                {
                    RequireIdentity(job, file);
                    VerifyFile(file, job, ct, BeginFinalization(IncomingFinalizationStage.CheckingPublishedBytes).Advance);
                }
                finalizing = true; job = Update(job with { Phase = DownloadPhase.Finalizing, Error = "Owner requested Windows attachment checks. The file remains untrusted." });
                await CheckPublished(local, ct);
                Update(job with { Phase = DownloadPhase.Completed, Error = "", SecurityPolicy = ReceivedFileSecurity.PolicyVersion }); return;
            }
            if (job.Hash.Length == 0 || job.Offset != job.Size)
            {
                if (request is null) throw new IOException("Reconnect to the source device.");
                job = Update(job with { Phase = DownloadPhase.Preparing, Error = "" });
                var response = await Checked(request, job.Ticket.Length == 0
                    ? new FileRequest { Operation = FileOperation.PrepareDownload, NodeId = job.SourceNode, ExpectedVersion = job.SourceVersion }
                    : new FileRequest { Operation = FileOperation.DownloadStatus, TransferId = job.Ticket }, ct);
                var info = response.Download ?? throw new InvalidDataException("The source did not provide a download descriptor.");
                if (info.Name != job.Name || info.Size != job.Size || info.Version != job.SourceVersion ||
                    (job.Ticket.Length != 0 && info.Id != job.Ticket)) throw new InvalidDataException("The source descriptor changed.");
                job = Update(job with { Ticket = info.Id, SourceExpiresAt = job.SourceExpiresAt == 0 ? info.ExpiresAt : info.ExpiresAt == 0 ? job.SourceExpiresAt : Math.Min(job.SourceExpiresAt, info.ExpiresAt) });
                checkpoint = checkpoint with { SourceExpiresAt = job.SourceExpiresAt };
                while (info.State == DownloadState.DownloadPreparing)
                {
                    if (!localCopy && job.SourceExpiresAt != 0 && job.SourceExpiresAt <= clock.GetUtcNow().ToUnixTimeMilliseconds())
                        throw new IOException("The source approval expired. Review a fresh source before resuming.");
                    ct.ThrowIfCancellationRequested(); await Task.Delay(300, ct);
                    info = (await Checked(request, new FileRequest { Operation = FileOperation.DownloadStatus, TransferId = job.Ticket }, ct)).Download
                        ?? throw new InvalidDataException("The source descriptor is missing.");
                    if (info.Id != job.Ticket || info.Name != job.Name || info.Size != job.Size || info.Version != job.SourceVersion)
                        throw new IOException("The preparing source descriptor changed. Earlier progress was kept.");
                    var expiry = job.SourceExpiresAt == 0 ? info.ExpiresAt : info.ExpiresAt == 0 ? job.SourceExpiresAt : Math.Min(job.SourceExpiresAt, info.ExpiresAt);
                    if (expiry != job.SourceExpiresAt) { job = Update(job with { SourceExpiresAt = expiry }); checkpoint = checkpoint with { SourceExpiresAt = expiry }; }
                }
                if (info.State != DownloadState.DownloadReady || info.Sha256.Length != 32 || info.Id != job.Ticket ||
                    info.Size != job.Size || info.Version != job.SourceVersion || info.Name != job.Name)
                    throw new IOException("The source could not prepare this file. Refresh the source and start again.");
                var hash = Convert.ToHexStringLower(info.Sha256.Span);
                if (job.Hash.Length != 0 && job.Hash != hash) throw new InvalidDataException("The source content changed.");
                job = Update(job with { Hash = hash });
            }
            using var root = OpenRoot(job);
            try { temporary = root.OpenTransferFile(job.Id, job.TemporaryIdentity.Length == 0); }
            catch (StorageException ex) when (ex.Status == FileStatus.NotFound && job.FinalName.Length != 0 && job.Offset == job.Size)
            {
                using (var finalized = root.OpenChild(job.FinalName))
                {
                    RequireIdentity(job, finalized);
                    VerifyFile(finalized, job, ct, BeginFinalization(IncomingFinalizationStage.CheckingPublishedBytes).Advance);
                }
                finalizing = true;
                job = Update(job with { Phase = DownloadPhase.Finalizing, Error = "Checking Windows attachment policy. This file remains untrusted." });
                await CheckPublished(root, ct);
                Update(job with { Phase = DownloadPhase.Completed, Error = "", SecurityPolicy = ReceivedFileSecurity.PolicyVersion }); return;
            }
            if (job.TemporaryIdentity.Length == 0) job = Update(job with { TemporaryIdentity = temporary.Info.Identity });
            RequireIdentity(job, temporary);
            temporary.MarkUntrusted();
            if (temporary.Info.Size < job.Offset) throw new InvalidDataException("The partial file is shorter than its saved checkpoint.");
            temporary.SetLength(job.Offset); // Discard bytes that were never durably acknowledged locally.
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var prefix = new byte[StorageRules.MaxReadRange]; var verified = 0L;
            while (verified < job.Offset)
            {
                ct.ThrowIfCancellationRequested(); var length = (int)Math.Min(prefix.Length, job.Offset - verified);
                ReadExactly(temporary, verified, prefix.AsSpan(0, length));
                digest.AppendData(prefix, 0, length); chain = Chain(chain, verified, prefix.AsSpan(0, length)); verified += length;
            }
            if (job.Offset != 0 && !CryptographicOperations.FixedTimeEquals(chain, Convert.FromHexString(job.PrefixHash)))
                throw new InvalidDataException("The saved partial file changed. Start a new download.");
            canCheckpoint = true;
            job = Update(job with { Phase = DownloadPhase.Downloading, Error = "" }); checkpoint = job;
            while (offset < job.Size)
            {
                ct.ThrowIfCancellationRequested();
                if (!localCopy && job.SourceExpiresAt != 0 && clock.GetUtcNow().ToUnixTimeMilliseconds() >= job.SourceExpiresAt)
                    throw new IOException("The recorded source approval expired. Review a fresh source before continuing; verified progress was kept.");
                var batch = new List<(long Offset, int Length, Task<FileResponse> Response)>();
                for (var position = offset; position < job.Size && batch.Count < 4; position += StorageRules.MaxReadRange)
                {
                    var length = (int)Math.Min(StorageRules.MaxReadRange, job.Size - position);
                    batch.Add((position, length, Checked(request!, new FileRequest { Operation = FileOperation.DownloadRead,
                        TransferId = job.Ticket, Offset = position, Length = (uint)length }, ct)));
                }
                try
                {
                    foreach (var chunk in batch)
                    {
                        var response = await chunk.Response;
                        if (response.Content.Length != chunk.Length || response.EndOfFile != (chunk.Offset + chunk.Length == job.Size))
                            throw new InvalidDataException("The source returned an invalid download range.");
                        ct.ThrowIfCancellationRequested(); temporary.Write(offset, response.Content.Span);
                        digest.AppendData(response.Content.Span); chain = Chain(chain, offset, response.Content.Span); offset += chunk.Length;
                    }
                }
                finally { foreach (var chunk in batch) { try { await chunk.Response; } catch { /* Observe every bounded in-flight request. */ } } }
                job = job with { Offset = offset, PrefixHash = Convert.ToHexStringLower(chain) };
                if (offset - lastSaved >= 1024 * 1024 || offset == job.Size)
                { temporary.Flush(); job = Update(job); checkpoint = job; lastSaved = offset; }
                else Publish(job);
            }
            temporary.Flush(); job = Update(job with { Offset = offset, PrefixHash = Convert.ToHexStringLower(chain), Phase = DownloadPhase.Verifying }); checkpoint = job;
            if (!CryptographicOperations.FixedTimeEquals(digest.GetHashAndReset(), Convert.FromHexString(job.Hash)))
                throw new InvalidDataException("The received file did not match the source SHA-256 checksum.");
            // Re-read the flushed destination before publication, independently of the network digest.
            VerifyFile(temporary, job, ct, BeginFinalization(IncomingFinalizationStage.CheckingSavedBytes).Advance);
            ct.ThrowIfCancellationRequested();
            BeginFinalization(IncomingFinalizationStage.Publishing);
            for (var number = 1; number <= 10_000; number++)
            {
                finalizing = true;
                var name = CollisionName(job.Name, number);
                job = Update(job with { Phase = DownloadPhase.Finalizing, FinalName = name });
                try { temporary.RenameTo(root, name); break; }
                catch (StorageException ex) when (ex.NativeError is 80 or 183 && job.Collision == DownloadCollision.KeepBoth && number < 10_000) { continue; }
                catch (StorageException ex) when (ex.NativeError is 80 or 183 && job.Collision == DownloadCollision.Skip)
                { temporary.Delete(); Update(job with { Phase = DownloadPhase.Skipped, Error = "An existing destination was kept." }); return; }
            }
            temporary.Dispose(); temporary = null;
            job = Update(job with { Phase = DownloadPhase.Finalizing, Error = "Checking Windows attachment policy. This file remains untrusted." });
            await CheckPublished(root, ct);
            Update(job with { Phase = DownloadPhase.Completed, Error = "", SecurityPolicy = ReceivedFileSecurity.PolicyVersion });
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (temporary is not null && canCheckpoint && !finalizing)
                { temporary.Flush(); checkpoint = job with { Offset = offset, PrefixHash = Convert.ToHexStringLower(chain) }; }
                if (finalizing) { lock (gate) checkpoint = jobs[id]; }
                Update(checkpoint with { Phase = DownloadPhase.Paused, Error = work.Cancel ? "Canceling…" : "Paused. Resume after reconnecting if needed." });
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            { Publish(checkpoint with { Phase = DownloadPhase.Interrupted, Error = "Progress could not be saved. The earlier checkpoint will be verified on resume." }); }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException)
        {
            // Keep the last durable checkpoint, including any finalization intent.
            if (temporary is not null && canCheckpoint && !finalizing && ex is not InvalidDataException)
            {
                try { temporary.Flush(); checkpoint = job with { Offset = offset, PrefixHash = Convert.ToHexStringLower(chain) }; }
                catch (IOException) { /* Retain the earlier durable checkpoint. */ }
            }
            if (finalizing) { lock (gate) checkpoint = jobs[id]; }
            var failed = checkpoint with { Phase = ex is InvalidDataException or FormatException ? DownloadPhase.Failed : DownloadPhase.Interrupted, Error = SafeError(ex.Message) };
            try { Update(failed); }
            catch (Exception saveFailure) when (saveFailure is IOException or UnauthorizedAccessException)
            { Publish(failed with { Error = "Progress could not be saved. The earlier checkpoint will be verified when resuming." }); }
        }
        finally
        {
            temporary?.Dispose(); if (entered) concurrency.Release();
            DownloadJob finished; lock (gate) finished = jobs[id];
            if (finished.Phase is DownloadPhase.Completed or DownloadPhase.Skipped) await ReleaseAsync(finished, request);
            lock (gate) { running.Remove(id); work.Cancellation.Dispose(); }
        }
    }
    private static async Task<FileResponse> Checked(Request request, FileRequest value, CancellationToken ct)
    {
        var result = await request(value, ct); StorageRules.ValidateResponse(result);
        if (result.Status != FileStatus.Ok) throw new IOException(result.Message.Length != 0 ? result.Message : "The source denied this download.");
        return result;
    }
    private static SecureWindowsDirectory OpenRoot(DownloadJob job)
    {
        var root = SecureWindowsDirectory.OpenRoot(job.RootPath);
        if (root.Info.Identity != job.RootIdentity || !root.Info.CanonicalPath.Equals(job.RootPath, StringComparison.OrdinalIgnoreCase))
        { root.Dispose(); throw new IOException("The destination location changed."); }
        return root;
    }
    private static void RequireIdentity(DownloadJob job, SecureWindowsDirectory file)
    {
        if (file.Info.Identity != job.TemporaryIdentity) throw new InvalidDataException("The destination temporary file was replaced.");
    }
    internal static void VerifyFile(SecureWindowsDirectory file, DownloadJob job, CancellationToken ct, Action<long>? progress = null) =>
        VerifyFile(file, job.Size, job.Hash, ct, progress);
    internal static void VerifyFile(SecureWindowsDirectory file, long size, string hash, CancellationToken ct, Action<long>? progress = null)
    {
        var snapshot = SecureWindowsDirectory.Inspect(file.Handle);
        if (snapshot.Size != size || snapshot.Links != 1 || (snapshot.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The destination has unexpected metadata.");
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[StorageRules.MaxReadRange]; var offset = 0L;
        while (offset < size)
        {
            ct.ThrowIfCancellationRequested(); var length = (int)Math.Min(buffer.Length, size - offset);
            ReadExactly(file, offset, buffer.AsSpan(0, length)); digest.AppendData(buffer, 0, length); offset += length;
            progress?.Invoke(offset);
        }
        if (!CryptographicOperations.FixedTimeEquals(digest.GetHashAndReset(), Convert.FromHexString(hash))) throw new InvalidDataException("Destination SHA-256 verification failed.");
    }
    private static void ReadExactly(SecureWindowsDirectory file, long offset, Span<byte> bytes)
    {
        var count = 0;
        while (count < bytes.Length) { var read = RandomAccess.Read(file.Handle, bytes[count..], offset + count); if (read == 0) throw new IOException("The partial file ended unexpectedly."); count += read; }
    }
    private static byte[] Chain(byte[] previous, long offset, ReadOnlySpan<byte> data)
    {
        Span<byte> input = stackalloc byte[72]; previous.CopyTo(input); BinaryPrimitives.WriteInt64LittleEndian(input[32..], offset); SHA256.HashData(data, input[40..]); return SHA256.HashData(input);
    }
    private static string CollisionName(string name, int number)
    {
        if (number == 1) return name;
        var extension = Path.GetExtension(name); var suffix = $" ({number})";
        var stem = Path.GetFileNameWithoutExtension(name); var maximum = 255 - extension.Length - suffix.Length;
        if (maximum < 1) throw new IOException("Choose a shorter destination name.");
        if (stem.Length > maximum) stem = stem[..maximum].TrimEnd();
        if (stem.Length > 0 && char.IsHighSurrogate(stem[^1])) stem = stem[..^1];
        var result = stem + suffix + extension; StorageRules.ValidateName(result); return result;
    }
    private DownloadJob Update(DownloadJob job)
    {
        bool notify, progressOnly;
        lock (gate)
        {
            var previous = jobs.GetValueOrDefault(job.Id);
            progressOnly = IsByteProgress(previous, job); notify = ShouldNotify(job, progressOnly);
            Persist(job); jobs[job.Id] = job; Observe(previous?.Phase, job);
        }
        if (notify) NotifyJob(job, progressOnly);
        return job;
    }
    private void Set(DownloadJob job)
    {
        var previous = jobs.GetValueOrDefault(job.Id)?.Phase;
        Persist(job); jobs[job.Id] = job; Observe(previous, job); NotifyJob(job, false);
    }
    private void Publish(DownloadJob job)
    {
        bool notify, progressOnly;
        lock (gate)
        {
            var previous = jobs.GetValueOrDefault(job.Id);
            progressOnly = IsByteProgress(previous, job); notify = ShouldNotify(job, progressOnly);
            jobs[job.Id] = job;
            if (previous?.Phase != job.Phase && job.Phase is DownloadPhase.Interrupted or DownloadPhase.Failed)
                WindowsActivity.Publish(Activity, new(localCopy ? WindowsActivityKind.LocalCopy : job.BatchId.Length == 0 ? WindowsActivityKind.Download : WindowsActivityKind.DownloadBatchFile, WindowsActivityOutcome.Review, job.PeerId));
        }
        if (notify) NotifyJob(job, progressOnly);
    }
    private void NotifyJob(DownloadJob job, bool progressOnly)
    {
        Changed?.Invoke(job);
        if (!progressOnly) MetadataChanged?.Invoke(job);
    }
    private void Observe(DownloadPhase? previous, DownloadJob job)
    {
        if (previous == job.Phase) return;
        WindowsActivityOutcome? outcome = job.Phase switch { DownloadPhase.Completed => WindowsActivityOutcome.Completed, DownloadPhase.Skipped => WindowsActivityOutcome.Skipped,
            DownloadPhase.Canceled => WindowsActivityOutcome.Canceled, DownloadPhase.Paused => WindowsActivityOutcome.Paused,
            DownloadPhase.Interrupted => job.FinalName.Length == 0 ? WindowsActivityOutcome.Paused : WindowsActivityOutcome.Review,
            DownloadPhase.Failed => job.FinalName.Length == 0 ? WindowsActivityOutcome.Failed : WindowsActivityOutcome.Review, _ => null };
        if (outcome is { } result) WindowsActivity.Publish(Activity, new(localCopy ? WindowsActivityKind.LocalCopy : job.BatchId.Length == 0 ? WindowsActivityKind.Download : WindowsActivityKind.DownloadBatchFile,
            result, job.PeerId, job.Phase == DownloadPhase.Completed ? job.Size : -1));
    }
    private static bool IsByteProgress(DownloadJob? previous, DownloadJob job) =>
        previous is not null && job.Phase == DownloadPhase.Downloading && job.Offset > previous.Offset && job.Offset < job.Size &&
        previous == (job with { Offset = previous.Offset, PrefixHash = previous.PrefixHash });

    private bool ShouldNotify(DownloadJob job, bool progressOnly)
    {
        var now = Environment.TickCount64;
        if (progressOnly && lastNotified.TryGetValue(job.Id, out var previous) && now - previous < 100) return false;
        lastNotified[job.Id] = now; return true;
    }
    private static Task ReleaseAsync(DownloadJob job, Request? request) => request is null ? Task.CompletedTask : ReleaseSourceAsync(job.Ticket, request);
    private void Persist(DownloadJob job)
    {
        var path = Path.Combine(directory, job.Id + ".json");
        using (var stream = new FileStream(path + ".new", FileMode.Create, FileAccess.Write, FileShare.None)) { JsonSerializer.Serialize(stream, job); stream.Flush(true); }
        AtomicStateFile.Commit(path + ".new", path);
    }
    private void Validate(DownloadJob job)
    {
        static bool Hash(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigitLower);
        if (!ProtocolRules.TokenPattern().IsMatch(job.Id) || (localCopy ? job.PeerId.Length != 0 : !Hash(job.PeerId)) || !Hash(job.SourceVersion) ||
            !ProtocolRules.TokenPattern().IsMatch(job.SourceNode) || job.Size is < 0 or > StorageRules.MaxTransferSize || job.Offset < 0 || job.Offset > job.Size ||
            !Enum.IsDefined(job.Phase) || !Enum.IsDefined(job.Collision) || job.Hash.Length != 0 && !Hash(job.Hash) || job.PrefixHash.Length != 0 && !Hash(job.PrefixHash) ||
            job.Ticket.Length != 0 && !ProtocolRules.TokenPattern().IsMatch(job.Ticket) || batchOnly && job.BatchId.Length == 0 || job.BatchId.Length != 0 && !ProtocolRules.TokenPattern().IsMatch(job.BatchId) ||
            !SecureWindowsDirectory.IsLocalVolumePath(job.RootPath) || job.RootIdentity.Length is < 1 or > 256 || job.DisplayPath.Length is < 3 or > 8192 ||
            job.TemporaryIdentity.Length > 256 || job.Error.Length > 512 || job.SourceExpiresAt is < 0 or > StorageRules.MaxTimestamp || job.SecurityPolicy is < 0 or > ReceivedFileSecurity.PolicyVersion || job.Phase == DownloadPhase.Completed && (job.Offset != job.Size || !Hash(job.Hash) || job.FinalName.Length == 0))
            throw new InvalidDataException("Invalid download queue record.");
        StorageRules.ValidateName(job.Name); if (job.FinalName.Length != 0) StorageRules.ValidateName(job.FinalName);
    }
    private static string SafeError(string value) => new(value.Where(c => !char.IsControl(c)).Take(512).ToArray());
    public async ValueTask DisposeAsync()
    {
        Task[] tasks;
        PendingSource[] pendingSources;
        lock (gate) { if (disposed) return; disposed = true; sourceRecoveryLifetime.Cancel(); foreach (var work in running.Values) work.Cancellation.Cancel(); tasks = running.Values.Select(x => x.Task).Concat(sourcePreparations.Values.Select(x => x.Task)).ToArray(); pendingSources = sourceReviews.Values.ToArray(); sourceReviews.Clear(); }
        try { await Task.WhenAll(tasks); }
        finally { foreach (var source in pendingSources) await ReleaseSourceAsync(source.Info.Id, source.Request); sourceRecoveryLifetime.Dispose(); concurrency.Dispose(); }
    }
}
