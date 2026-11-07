using Google.Protobuf;
using IConnect.Core;
using IConnect.Protocol;
using System.Security.Cryptography;
using System.Text.Json;

namespace IConnect.Storage.Windows;

public sealed record UploadAuthority(string Owner, string OwnerEpoch, string Peer, string Pairing);
public enum SendingPhase { Preparing, Ready, Sending, Paused, Verifying, Completed, Review, Failed }
public sealed record OutgoingMoveSource(string DisplayPath, string ParentPath, string ParentIdentity, string Name, string Identity, string Version);
public sealed record OutgoingCopy(string Id, UploadAuthority Authority, string Name, long Size, string Identity = "", string Hash = "", long Offset = 0,
    SendingPhase Phase = SendingPhase.Preparing, string RemoteId = "", string RemoteEpoch = "", string Destination = "", string FinalName = "",
    string Message = "Preparing a private snapshot.", OutgoingMoveSource? MoveSource = null, string SourceRecoveryId = "", bool SourceRecovered = false);
public sealed record OutgoingPayloadProgress(string Id, string Run, string ReceiverId, string ReceiverEpoch, long Size, long Offset, bool Active);

/// <summary>Explicit owner-selected snapshots. A snapshot never grants remote read access to its source.</summary>
public sealed partial class WindowsUploadSender : IAsyncDisposable
{
    public delegate Task<FileResponse> Request(FileRequest request, CancellationToken ct);
    private sealed class Work(CancellationTokenSource cancellation)
    { public CancellationTokenSource Cancellation { get; } = cancellation; public Task Task { get; set; } = Task.CompletedTask; }
    private readonly object gate = new();
    private readonly string directory, path;
    private readonly Func<UploadAuthority, bool> current;
    private readonly Func<bool> ownerReady;
    private readonly Dictionary<string, OutgoingCopy> records = [];
    private readonly Dictionary<string, Work> work = [];
    private readonly Dictionary<string, OutgoingPayloadProgress> payload = [];
    private readonly FileStream lease;
    private readonly StateRecovery recovery;
    private readonly OutgoingCopyResources.Participant inventory;
    private bool disposed;
    public event Action? Changed;
    public event Action? MetadataChanged;
    private void NotifyChanged(bool progressOnly = false)
    {
        Changed?.Invoke();
        if (!progressOnly) MetadataChanged?.Invoke();
    }
    public event Action<OutgoingPayloadProgress>? PayloadChanged;
    public bool CopyingPayload { get { lock (gate) return payload.Count != 0; } }
    public bool HasPreparationProgress { get { lock (gate) return work.Keys.Any(id => records[id].Phase == SendingPhase.Preparing); } }
    public OutgoingPayloadProgress? Payload(string id) { lock (gate) return payload.GetValueOrDefault(id); }
    private void PublishPayload(OutgoingCopy row, string run, bool active)
    {
        var progress = new OutgoingPayloadProgress(row.Id, run, row.RemoteId, row.RemoteEpoch, row.Size, row.Offset, active && row.Offset < row.Size);
        lock (gate) { if (progress.Active) payload[row.Id] = progress; else payload.Remove(row.Id); }
        if (PayloadChanged is not { } listeners) return;
        foreach (Action<OutgoingPayloadProgress> listener in listeners.GetInvocationList()) try { listener(progress); } catch (Exception) { }
    }
    public Action<WindowsActivityEvent>? Activity { get; set; }
    public IReadOnlyList<OutgoingCopy> All { get { lock (gate) return records.Values.Reverse().ToArray(); } }
    public OutgoingCopy? FindCopy(string id) { lock (gate) return records.GetValueOrDefault(id); }
    public bool Active(string id) { lock (gate) return work.ContainsKey(id); }
    public IReadOnlyList<StateRecoveryIssue> RecoveryIssues { get { lock (gate) return recovery.Issues; } }
    public int PreservedRecords { get { lock (gate) return recovery.ArchivedCount; } }
    public WindowsUploadSender(string profile, Func<UploadAuthority, bool> current, Func<bool>? ownerReady = null, OutgoingCopyResources? resources = null)
    {
        this.current = current; this.ownerReady = ownerReady ?? (() => true);
        directory = Path.Combine(profile, "outgoing-uploads"); Directory.CreateDirectory(directory); path = Path.Combine(directory, "journal.json");
        lease = new FileStream(Path.Combine(directory, "owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); recovery = new(directory);
        try { inventory = (resources ?? OutgoingCopyResources.Isolated(OutgoingCopyResources.Single)).Open(OutgoingCopyResources.Single, profile); }
        catch { lease.Dispose(); throw; }
        try
        {
            if (File.Exists(path))
            {
                var loaded = StateRecovery.Read<OutgoingCopy[]>(path, 2 * 1024 * 1024);
                if (loaded.Length > 8 || loaded.Select(x => x.Id).Distinct().Count() != loaded.Length) throw new InvalidDataException("Outgoing snapshots exceed their limit.");
                foreach (var row in loaded)
                {
                    Validate(row); records.Add(row.Id, row.Phase switch {
                        SendingPhase.Preparing => row with { Phase = SendingPhase.Failed, Message = "Preparation stopped. Keep or remove the partial and choose the source again." },
                        SendingPhase.Sending or SendingPhase.Verifying => row with { Phase = SendingPhase.Paused, Message = "Sending stopped. Reconnect to the same pairing and explicitly check its receipt." }, _ => row });
                }
            }
            else if (File.Exists(path + ".new") || File.Exists(path + ".bak")) throw new InvalidDataException("Uncommitted outgoing progress needs review.");
        }
        catch (Exception ex) when (StateRecovery.IsRecordFailure(ex))
        { records.Clear(); recovery.Add(path, "Outgoing-copy metadata needs review. Private snapshots and original files were preserved."); }
        SyncInventory();
    }
    private static IEnumerable<OutgoingCopyResources.Reservation> Reservations(IEnumerable<OutgoingCopy> rows) => rows.Select(row => new OutgoingCopyResources.Reservation(row.Id, row.Authority.Peer, row.Size, row.Phase == SendingPhase.Preparing ? row.Size : 0));
    private void SyncInventory()
    {
        try { using var root = OpenDirectory(); inventory.Recover(Reservations(records.Values), recovery.Needed || PrivateFiles(root).Any(x => !records.ContainsKey(x.Name[..^4]))); }
        catch (Exception ex) when (StateRecovery.IsRecordFailure(ex)) { recovery.Block("Private outgoing inventory needs review. Existing copies were kept."); inventory.Recover(Reservations(records.Values), true); }
    }
    private void RequireReady() { ObjectDisposedException.ThrowIf(disposed, this); recovery.RequireHealthy(); if (!ownerReady()) throw new InvalidOperationException("Review the current Windows identity and approvals first."); }
    private void RequireAuthority(OutgoingCopy row) { RequireReady(); if (!current(row.Authority)) throw new InvalidOperationException("This snapshot belongs to an earlier identity or pairing. Prepare a new copy for the current device."); }
    private static bool Hex(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigitLower);
    private static void Validate(OutgoingCopy row)
    {
        StorageRules.ValidateName(row.Name);
        if (!UploadBatchRules.CanonicalToken(row.Id) || !Hex(row.Authority.Owner) || !Hex(row.Authority.Peer) || !ProtocolRules.TokenPattern().IsMatch(row.Authority.OwnerEpoch) ||
            !ProtocolRules.TokenPattern().IsMatch(row.Authority.Pairing) || row.Size is < 0 or > UploadRules.PeerQuota || row.Identity.Length > 256 || row.Hash.Length != 0 && !Hex(row.Hash) ||
            row.Offset < 0 || row.Offset > row.Size || !Enum.IsDefined(row.Phase) || row.Message.Length > 512 || row.Message.Any(char.IsControl) || row.Destination.Length > 255 ||
            (row.RemoteId.Length == 0 ? row.RemoteEpoch.Length != 0 : !ProtocolRules.TokenPattern().IsMatch(row.RemoteId) || !ProtocolRules.TokenPattern().IsMatch(row.RemoteEpoch)) ||
            row.Phase is not (SendingPhase.Preparing or SendingPhase.Failed) && (row.Hash.Length == 0 || row.Identity.Length == 0)) throw new InvalidDataException("Invalid outgoing snapshot.");
        if (row.RemoteId.Length != 0 && MutationRules.CreatedAt(row.RemoteId) is < 0 or > StorageRules.MaxTimestamp) throw new InvalidDataException("Invalid outgoing receipt identifier.");
        if (row.FinalName.Length != 0) StorageRules.ValidateName(row.FinalName);
        ValidateMove(row);
    }
    private void Save(Dictionary<string, OutgoingCopy> updated)
    {
        foreach (var row in updated.Values) Validate(row);
        inventory.Replace(Reservations(updated.Values), () => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!).AvailableFreeSpace);
        try
        {
            using (var output = new FileStream(path + ".new", FileMode.Create, FileAccess.Write, FileShare.None)) { JsonSerializer.Serialize(output, updated.Values.ToArray()); output.Flush(true); }
            AtomicStateFile.Commit(path + ".new", path); records.Clear(); foreach (var row in updated) records.Add(row.Key, row.Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { inventory.Uncertain(); recovery.Block("Outgoing progress could not be saved. Preserve the snapshots and review this queue."); throw; }
    }
    private OutgoingCopy Put(OutgoingCopy row, bool progressOnly = false)
    {
        SendingPhase? previous;
        lock (gate) { previous = records.GetValueOrDefault(row.Id)?.Phase; var updated = new Dictionary<string, OutgoingCopy>(records) { [row.Id] = row }; Save(updated); }
        if (previous != row.Phase)
        {
            WindowsActivityOutcome? outcome = row.Phase switch { SendingPhase.Ready => WindowsActivityOutcome.Accepted, SendingPhase.Completed => WindowsActivityOutcome.Completed,
                SendingPhase.Paused => WindowsActivityOutcome.Paused, SendingPhase.Review => WindowsActivityOutcome.Review, SendingPhase.Failed => WindowsActivityOutcome.Failed, _ => null };
            if (outcome is { } result) WindowsActivity.Publish(Activity, new(row.Phase == SendingPhase.Ready || row.Phase == SendingPhase.Failed && previous == SendingPhase.Preparing ? WindowsActivityKind.PrivateCopy : WindowsActivityKind.FileSend,
                result, row.Authority.Peer, row.Phase is SendingPhase.Ready or SendingPhase.Completed ? row.Size : -1));
        }
        NotifyChanged(progressOnly); return row;
    }
    private static string SnapshotName(string id) => id + ".bin";
    private static void RequireFile(OutgoingCopy row, SecureWindowsDirectory file, bool full = true)
    {
        var info = file.Info;
        if (info.Identity != row.Identity || info.IsDirectory || info.Links != 1 || full && info.Size != row.Size) throw new InvalidDataException("The private source snapshot changed. Preserve it and prepare a new copy.");
    }
    private SecureWindowsDirectory OpenDirectory() => SecureWindowsDirectory.OpenRoot(directory);
    private SecureWindowsDirectory.DirectoryName[] PrivateFiles(SecureWindowsDirectory root)
    {
        var files = root.EnumerateNames(default).Where(x => x.Name.EndsWith(".bin", StringComparison.Ordinal)).Take(9).ToArray();
        if (files.Length > 8 || files.Any(x => x.IsDirectory || !ProtocolRules.TokenPattern().IsMatch(x.Name[..^4]))) throw new IOException("Private upload snapshots need review.");
        return files;
    }
    public Task<string> PrepareAsync(string sourcePath, UploadAuthority authority, CancellationToken ct = default) =>
        PrepareAsync(sourcePath, authority, false, ct);

    public Task<string> PrepareMoveAsync(string sourcePath, UploadAuthority authority, CancellationToken ct = default) =>
        PrepareAsync(sourcePath, authority, true, ct);

    private Task<string> PrepareAsync(string sourcePath, UploadAuthority authority, bool forMove, CancellationToken ct)
    {
        var full = Path.GetFullPath(sourcePath); var volume = Path.GetPathRoot(full)!;
        if (volume.Length != 3 || volume[1] != ':' || !char.IsAsciiLetter(volume[0])) throw new InvalidDataException("Choose a file on a normal local drive.");
        using var sourceRoot = SecureWindowsDirectory.OpenRoot(volume);
        var sourceCanonical = sourceRoot.Info.CanonicalPath; var sourceRootIdentity = sourceRoot.Info.Identity;
        var segments = full[volume.Length..].Split(Path.DirectorySeparatorChar); foreach (var name in segments) StorageRules.ValidateName(name);
        using var source = SecureWindowsDirectory.OpenPath(sourceRoot.Info.CanonicalPath, sourceRoot.Info.Identity, segments, false);
        var info = source.Info;
        OutgoingMoveSource? move = null;
        if (forMove)
        {
            using var parent = SecureWindowsDirectory.OpenPath(sourceCanonical, sourceRootIdentity, segments[..^1], true);
            RequirePublicMoveSource(info.CanonicalPath);
            move = new(full, parent.Info.CanonicalPath, parent.Info.Identity, segments[^1], info.Identity, info.Version);
        }
        var row = new OutgoingCopy(ProtocolRules.NewToken(), authority, segments[^1], info.Size, MoveSource: move);
        IDisposable preparationWorker;
        lock (gate)
        {
            RequireAuthority(row); if (row.Size is < 0 or > UploadRules.PeerQuota || records.Count >= 8 || work.Count >= 2) throw new IOException("Remove a private snapshot or wait for preparation before adding another.");
            using var root = OpenDirectory(); var orphans = PrivateFiles(root).Where(x => !records.ContainsKey(x.Name[..^4])).ToArray(); long orphanBytes = 0;
            foreach (var orphan in orphans) { using var file = root.OpenChild(orphan.Name); orphanBytes = checked(orphanBytes + file.Info.Size); }
            if (orphans.Length != 0) { inventory.Uncertain(); throw new IOException("Review abandoned private file copies before preparing another file or folder."); }
            if (PrivateFiles(root).Length >= 8 || row.Size > UploadRules.PeerQuota - records.Values.Sum(x => x.Size) - orphanBytes) throw new IOException("The private upload snapshot reservation is full.");
            var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!).AvailableFreeSpace;
            var pending = inventory.OtherPending();
            if (row.Size > Math.Max(0, free - pending - 64L * 1024 * 1024)) throw new IOException("Not enough private disk space to prepare and verify this snapshot.");
            preparationWorker = inventory.TryWorker() ?? throw new InvalidOperationException("Two outgoing preparations or sends are already active. Retry after one stops.");
            try { Put(row); } catch { preparationWorker.Dispose(); throw; }
        }
        // Open an independent held source before returning; all ancestors remain pinned during copying.
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task staging;
        try { staging = Start(row.Id, ct, async token =>
        {
            {
                var currentRow = row;
                try
                {
                    using var retained = SecureWindowsDirectory.OpenPath(sourceCanonical, sourceRootIdentity, segments, false);
                    if (retained.Info.Identity != info.Identity || retained.Info.Size != row.Size ||
                        move is not null && retained.Info.Version != move.Version)
                        throw new IOException("The chosen local source changed.");
                    using var root = OpenDirectory(); using var snapshot = root.CreateFile(SnapshotName(row.Id));
                    currentRow = Put(row with { Identity = snapshot.Info.Identity });
                    using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var bytes = new byte[StorageRules.MaxReadRange]; var offset = 0L;
                    while (offset < row.Size)
                    {
                        token.ThrowIfCancellationRequested(); RequireAuthority(currentRow); var length = (int)Math.Min(bytes.Length, row.Size - offset); ReadExactly(retained, offset, bytes.AsSpan(0, length));
                        snapshot.Write(offset, bytes.AsSpan(0, length)); digest.AppendData(bytes, 0, length); offset += length;
                        if (offset % (1024 * 1024) == 0) { lock (gate) records[row.Id] = currentRow with { Offset = offset }; NotifyChanged(progressOnly: true); }
                    }
                    token.ThrowIfCancellationRequested(); RequireAuthority(currentRow); snapshot.Flush();
                    currentRow = Put(currentRow with { Hash = Convert.ToHexStringLower(digest.GetHashAndReset()), Phase = SendingPhase.Ready, Offset = 0, Message = "Private snapshot ready. Choose a receiving location and send explicitly." });
                    completion.TrySetResult(currentRow.Id);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
                { try { Put(currentRow with { Offset = 0, Phase = SendingPhase.Failed, Message = "Preparation stopped. The original and private partial were kept." }); } catch (IOException) { } completion.TrySetException(ex); }
            }
            await Task.CompletedTask;
        }, preparationWorker); } catch { preparationWorker.Dispose(); throw; }
        return CompletedPreparation();
        async Task<string> CompletedPreparation() { await staging; return await completion.Task; }
    }
    private Task Start(string id, CancellationToken ct, Func<CancellationToken, Task> action, IDisposable? reservedWorker = null)
    {
        lock (gate)
        {
            RequireReady(); if (work.ContainsKey(id)) throw new InvalidOperationException("This copy is already active.");
            if (work.Count >= 2) { if (records[id].Phase == SendingPhase.Preparing) Put(records[id] with { Phase = SendingPhase.Failed, Message = "Two copies are already active. Remove this unused record and select the source again." }); throw new InvalidOperationException("Two private copies are already active."); }
            var worker = reservedWorker ?? inventory.TryWorker() ?? throw new InvalidOperationException("Two outgoing preparations or sends are already active. Retry after one stops.");
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct); var active = new Work(cancellation); work.Add(id, active);
            active.Task = Task.Run(async () => { try { await action(cancellation.Token); } finally { lock (gate) { worker.Dispose(); work.Remove(id); } cancellation.Dispose(); NotifyChanged(); } }); NotifyChanged();
            return active.Task;
        }
    }
    private static void ReadExactly(SecureWindowsDirectory file, long offset, Span<byte> bytes)
    { var read = 0; while (read < bytes.Length) { var count = RandomAccess.Read(file.Handle, bytes[read..], offset + read); if (count == 0) throw new IOException("Private source ended unexpectedly."); read += count; } }
    public Task SendAsync(string id, FileEntry? destination, Request request, CancellationToken ct = default)
    {
        lock (gate)
        {
            var row = records[id]; RequireAuthority(row);
            if (row.Hash.Length == 0 || row.Phase == SendingPhase.Completed || work.Count >= 2) throw new InvalidOperationException("Choose a ready, paused or reviewable snapshot.");
            if (row.RemoteId.Length == 0 && (destination is null || !destination.IsDirectory || (destination.Permissions & 12) != 12)) throw new InvalidOperationException("Select an approved receiving folder with Write and Create permissions.");
            Start(id, ct, token => TransferAsync(row, destination, request, token)); return work[id].Task;
        }
    }
    public Task VerifyPublishedAsync(string id, Request request, CancellationToken ct = default)
    {
        lock (gate)
        {
            var row = CompletedCopy(id);
            return StartReviewed(id, ct, async token =>
            {
                await RequirePublishedProofAsync(row, request, token);
                return true;
            });
        }
    }
    private OutgoingCopy CompletedCopy(string id)
    {
        var row = records[id];
        RequireAuthority(row);
        if (row.Phase != SendingPhase.Completed || row.RemoteId.Length == 0 || row.FinalName.Length == 0)
            throw new InvalidOperationException("Choose a completed copy with a retained receiver receipt.");
        return row;
    }
    private Task<T> StartReviewed<T>(string id, CancellationToken ct, Func<CancellationToken, Task<T>> run)
    {
        var outcome = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = Start(id, ct, async token =>
        {
            try { outcome.TrySetResult(await run(token)); }
            catch (OperationCanceledException) { outcome.TrySetCanceled(token); }
            catch (Exception ex) { outcome.TrySetException(ex); }
        });
        return ObserveAsync();
        async Task<T> ObserveAsync() { await work; return await outcome.Task; }
    }
    private async Task RequirePublishedProofAsync(OutgoingCopy row, Request request, CancellationToken token)
    {
        var challenge = ProtocolRules.NewToken();
        var digest = Convert.FromHexString(row.Hash);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromMinutes(10))
        {
            token.ThrowIfCancellationRequested();
            RequireAuthority(row);
            var response = await request(new()
            {
                Operation = FileOperation.VerifyPublishedUpload, TransferId = row.RemoteId,
                MutationEpoch = row.RemoteEpoch, MutationId = challenge
            }, token);
            token.ThrowIfCancellationRequested();
            RequireAuthority(row);
            StorageRules.ValidateResponse(response);
            if (response.Status != FileStatus.Ok) throw new IOException(response.Message);
            if (response.MutationEpoch != row.RemoteEpoch ||
                response.PublishedUploadCheck is not { } check || check.Challenge != challenge ||
                response.Upload is not { } upload || upload.Id != row.RemoteId || upload.Name != row.Name ||
                upload.FinalName != row.FinalName || upload.Size != row.Size || upload.Collision != UploadCollision.UploadKeepBoth ||
                !CryptographicOperations.FixedTimeEquals(upload.Sha256.Span, digest))
                throw new InvalidDataException("The receiver did not return a matching fresh check for this copy.");
            if (check.Verified) return;
            await Task.Delay(300, token);
        }
        throw new IOException("The receiver did not finish its published-copy check before the time limit.");
    }
    private async Task TransferAsync(OutgoingCopy initial, FileEntry? destination, Request request, CancellationToken ct)
    {
        var row = initial; var staleReceipt = false; var payloadRun = ProtocolRules.NewToken();
        try
        {
            RequireAuthority(row); row = Put(row with { Phase = SendingPhase.Sending, Message = "Checking retained private source bytes." });
            using var root = OpenDirectory(); using var source = root.OpenChild(SnapshotName(row.Id)); RequireFile(row, source);
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var bytes = new byte[StorageRules.MaxReadRange]; var offset = 0L;
            while (offset < row.Size) { ct.ThrowIfCancellationRequested(); RequireAuthority(row); var count = (int)Math.Min(bytes.Length, row.Size - offset); ReadExactly(source, offset, bytes.AsSpan(0, count)); digest.AppendData(bytes, 0, count); offset += count; }
            if (!CryptographicOperations.FixedTimeEquals(digest.GetHashAndReset(), Convert.FromHexString(row.Hash))) throw new InvalidDataException("The retained snapshot checksum changed.");
            async Task<FileResponse> Call(FileRequest value)
            { ct.ThrowIfCancellationRequested(); RequireAuthority(row); var response = await request(value, ct); StorageRules.ValidateResponse(response); RequireAuthority(row); if (response.Status != FileStatus.Ok) { staleReceipt |= response.Status == FileStatus.StaleReference; throw new IOException(response.Message); } return response; }
            UploadInfo Checked(UploadInfo? info)
            { if (info is null) throw new InvalidDataException("Missing incoming receipt."); UploadRules.Validate(info); if (info.Id != row.RemoteId || info.Name != row.Name || info.Size != row.Size || info.Collision != UploadCollision.UploadKeepBoth || !CryptographicOperations.FixedTimeEquals(info.Sha256.Span, Convert.FromHexString(row.Hash))) throw new InvalidDataException("The receiver changed the incoming descriptor."); return info; }
            UploadInfo progress;
            if (row.RemoteId.Length == 0)
            {
                var context = await Call(new() { Operation = FileOperation.UploadContext }); if (!ProtocolRules.TokenPattern().IsMatch(context.MutationEpoch)) throw new InvalidDataException("Missing incoming context.");
                row = Put(row with { RemoteId = MutationRules.NewId(), RemoteEpoch = context.MutationEpoch, Destination = destination!.Name, Message = "Requesting a new incoming copy." });
                progress = Checked((await Call(new() { Operation = FileOperation.PrepareUpload, TransferId = row.RemoteId, DestinationId = destination.Id, MutationEpoch = row.RemoteEpoch,
                    Upload = new() { Id = row.RemoteId, Name = row.Name, Size = row.Size, Sha256 = ByteString.CopyFrom(Convert.FromHexString(row.Hash)), Collision = UploadCollision.UploadKeepBoth } })).Upload);
            }
            else progress = Checked((await Call(new() { Operation = FileOperation.UploadStatus, TransferId = row.RemoteId, MutationEpoch = row.RemoteEpoch })).Upload);
            row = Put(row with { Offset = progress.Offset });
            // A retained receipt establishes the baseline; its earlier bytes are not new send progress.
            PublishPayload(row, payloadRun, progress.Phase == UploadPhase.UploadReceiving);
            while (progress.Phase == UploadPhase.UploadReceiving && progress.Offset < row.Size)
            {
                ct.ThrowIfCancellationRequested(); RequireAuthority(row); var count = (int)Math.Min(bytes.Length, row.Size - progress.Offset); ReadExactly(source, progress.Offset, bytes.AsSpan(0, count)); var expected = progress.Offset + count;
                progress = Checked((await Call(new() { Operation = FileOperation.UploadWrite, TransferId = row.RemoteId, MutationEpoch = row.RemoteEpoch, Offset = progress.Offset, Content = ByteString.CopyFrom(bytes, 0, count) })).Upload);
                if (progress.Offset != expected) throw new InvalidDataException("The acknowledged incoming range changed. Review its receipt.");
                row = Put(row with { Offset = progress.Offset }, progressOnly: progress.Phase == UploadPhase.UploadReceiving && progress.Offset < row.Size);
                PublishPayload(row, payloadRun, progress.Phase == UploadPhase.UploadReceiving);
            }
            if (progress.Phase is UploadPhase.UploadReceiving or UploadPhase.UploadVerifying or UploadPhase.UploadFinalizing || progress.Phase == UploadPhase.UploadReview && progress.FinalName.Length != 0)
                progress = Checked((await Call(new() { Operation = FileOperation.FinishUpload, TransferId = row.RemoteId, MutationEpoch = row.RemoteEpoch })).Upload);
            while (progress.Phase is UploadPhase.UploadVerifying or UploadPhase.UploadFinalizing)
            {
                row = Put(row with { Phase = SendingPhase.Verifying, Message = "Receiver is verifying and publishing. Original and private copies are kept." }); await Task.Delay(300, ct);
                progress = Checked((await Call(new() { Operation = FileOperation.UploadStatus, TransferId = row.RemoteId, MutationEpoch = row.RemoteEpoch })).Upload);
            }
            if (progress.Phase != UploadPhase.UploadCompleted) throw new IOException(progress.Message.Length == 0 ? "Receiver did not confirm completion. Review its existing receipt." : progress.Message);
            Put(row with { Offset = row.Size, Phase = SendingPhase.Completed, FinalName = progress.FinalName, Message = "Receiver confirmed a verified copy. The original and private snapshot are kept." });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException or NotSupportedException)
        { try { Put(row with { Phase = ex is OperationCanceledException ? SendingPhase.Paused : SendingPhase.Review, Message = staleReceipt
            ? "The receiver refused the existing operation context. Keep this receipt. If its write window expired, ask the receiving owner to review it, then resume this same copy explicitly. Earlier copies are kept."
            : "Sending stopped. Reconnect to the same pairing and explicitly check the existing receipt; earlier copies are kept." }); } catch (IOException) { } }
        finally { PublishPayload(row, payloadRun, false); }
    }
    public void Pause(string id) { lock (gate) if (work.TryGetValue(id, out var active)) active.Cancellation.Cancel(); }
    public void PauseAll() { lock (gate) foreach (var active in work.Values) active.Cancellation.Cancel(); }
    public Task WhenIdleAsync() { lock (gate) return Task.WhenAll(work.Values.Select(x => x.Task)); }
    public void Rename(string id, string name) { lock (gate) { RequireReady(); if (work.ContainsKey(id) || records[id].RemoteId.Length != 0 || records[id].Hash.Length == 0) throw new InvalidOperationException("Change the name before starting a receiver request."); StorageRules.ValidateName(name); Put(records[id] with { Name = name }); } }
    public void AnotherCopy(string id)
    {
        lock (gate)
        {
            RequireReady();
            var row = records[id];
            if (work.ContainsKey(id) || row.Hash.Length == 0) throw new InvalidOperationException("Pause a prepared copy first.");
            if (row.SourceRecoveryId.Length != 0) throw new InvalidOperationException("This move already has a source-recovery transaction.");
            moveReviews.Remove(id);
            Put(row with { RemoteId = "", RemoteEpoch = "", Destination = "", FinalName = "", Offset = 0, Phase = SendingPhase.Ready,
                Message = "Choose a destination for a deliberately new copy. Earlier receiver copies are kept." });
        }
    }
    public async Task RemoveAsync(string id)
    {
        Task stopping; lock (gate) { RequireReady(); if (work.TryGetValue(id, out var active)) { active.Cancellation.Cancel(); stopping = active.Task; } else stopping = Task.CompletedTask; }
        await stopping;
        lock (gate)
        {
            RequireReady(); var row = records[id]; using var root = OpenDirectory();
            try { using var file = root.OpenMutableChild(SnapshotName(id)); RequireFile(row, file, false); file.Delete(); } catch (StorageException ex) when (ex.Status == FileStatus.NotFound) { }
            var updated = new Dictionary<string, OutgoingCopy>(records); updated.Remove(id); Save(updated); moveReviews.Remove(id); NotifyChanged();
        }
    }
    public int Orphans { get { lock (gate) { using var root = OpenDirectory(); return PrivateFiles(root).Count(x => !records.ContainsKey(x.Name[..^4])); } } }
    public void RemoveOrphans()
    { lock (gate) { RequireReady(); if (work.Count != 0) throw new InvalidOperationException("Pause private copies first."); using var root = OpenDirectory(); foreach (var entry in PrivateFiles(root).Where(x => !records.ContainsKey(x.Name[..^4]))) { using var file = root.OpenMutableChild(entry.Name); if (file.Info.Links != 1 || file.Info.IsDirectory) throw new IOException("Unexpected private copy was kept."); file.Delete(); } SyncInventory(); } }
    public void ArchiveRecovery(string id) { lock (gate) { if (work.Count != 0) throw new InvalidOperationException("Pause sending before reviewing metadata."); recovery.Archive(id); if (!recovery.Needed) { Save([]); moveReviews.Clear(); } SyncInventory(); NotifyChanged(); } }
    public void ClearArchives() { lock (gate) recovery.ClearArchives(); }
    public async ValueTask DisposeAsync() { Task stopping; lock (gate) { if (disposed) return; disposed = true; PauseAll(); stopping = WhenIdleAsync(); } await stopping; lock (gate) { inventory.Dispose(); lease.Dispose(); } }
}
