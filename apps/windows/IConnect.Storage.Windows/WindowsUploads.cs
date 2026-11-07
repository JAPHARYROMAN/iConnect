using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using IConnect.Core;
using IConnect.Protocol;

namespace IConnect.Storage.Windows;

public sealed record IncomingUpload(string Id, string Peer, string Name, long Size, long Offset, UploadPhase Phase, string FinalName, string Message, string Location, int SecurityPolicy = 0, long OperationExpiresAt = 0);
internal sealed record UploadDestination(string Grant, string CanonicalRoot, string RootIdentity, string[] Path, string Identity);

/// <summary>Owner-grant-bound incoming copies. Never replaces or deletes a published destination.</summary>
public sealed partial class WindowsUploads
{
    private sealed record Row(string Id, string Peer, UploadDestination Destination, string Name, long Size, string Hash,
        UploadCollision Collision, string Temporary, string PreferredName = "", string TemporaryIdentity = "", long Offset = 0,
        string Prefix = "0000000000000000000000000000000000000000000000000000000000000000", UploadPhase Phase = UploadPhase.UploadPending,
        string FinalName = "", string Message = "Preparing incoming file.", int SecurityPolicy = 0, long OperationApprovedAt = 0);
    private sealed record Cache(int Schema, string OwnerScope, string Epoch, Row[] Rows, string Scope = "");
    private sealed class Work(CancellationTokenSource cancellation, IDisposable slot, IncomingFinalizationTracker? finalization = null)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public IDisposable Slot { get; } = slot;
        public IncomingFinalizationTracker? Finalization { get; } = finalization;
        public Task Task { get; set; } = Task.CompletedTask;
    }
    private readonly object gate = new();
    private readonly StorageGrantStore grants;
    private readonly string directory, path;
    private string partRecoveryDirectoryIdentity = "";
    private const string BackupPrefix = "incoming-upload-backup-";
    private StateRecovery recovery;
    private volatile Cache cache;
    private readonly WindowsScopedUpload? scope;
    private readonly ConcurrentDictionary<string, Work> verifying = new();
    private readonly HashSet<string> checkedPrefixes = [];
    public event Action? Changed;
    public event Action? MetadataChanged;
    internal WindowsUploads(StorageGrantStore grants, string profile, WindowsScopedUpload? scope = null)
    {
        this.grants = grants; this.scope = scope; directory = scope is null ? Path.Combine(profile, "incoming-uploads") : Path.GetDirectoryName(scope.JournalPath)!; path = scope?.JournalPath ?? Path.Combine(directory, "journal.json");
        payloadOrigin = grants.Clock.GetTimestamp();
        var fresh = scope?.Fresh ?? !Directory.Exists(directory); Directory.CreateDirectory(directory); recovery = new(directory);
        if (scope is null) { using var root = SecureWindowsDirectory.OpenRoot(directory); partRecoveryDirectoryIdentity = root.Info.Identity; }
        cache = new(1, grants.OwnerScope, ProtocolRules.NewToken(), [], scope?.Id ?? "");
        try
        {
            if (fresh) { if (scope is null) Save(cache); }
            else
            {
                var loaded = StateRecovery.Read<Cache>(path, scope is null ? 2 * 1024 * 1024 : WindowsBatchUploadJournals.RecordLimit); Validate(loaded); ValidateScope(loaded, grants.OwnerScope, scope);
                if (loaded.OwnerScope != grants.OwnerScope)
                {
                    if (loaded.Rows.Length != 0) throw new InvalidDataException("Incoming copies belong to earlier Windows authority.");
                    loaded = new(1, grants.OwnerScope, ProtocolRules.NewToken(), []); Save(loaded);
                }
                cache = loaded;
                var rows = cache.Rows.Select(x => x.Phase == UploadPhase.UploadVerifying ? x with { Phase = UploadPhase.UploadReceiving, Message = "Verification was interrupted. Resume explicitly." } : x).ToArray();
                if (!rows.SequenceEqual(cache.Rows)) Save(cache with { Rows = rows });
            }
        }
        catch (Exception ex) when (StateRecovery.IsRecordFailure(ex))
        { recovery.Add(path, "Incoming-copy metadata needs review. Partial and published files are kept; incoming writes are blocked."); }
        if (scope is null)
        {
            grants.Changed += InvalidateOperationReviews;
            grants.Changed += InvalidatePublishedChecks;
            grants.Changed += ClearPayloadRates;
        }
    }
    public IReadOnlyList<StateRecoveryIssue> RecoveryIssues { get { lock (gate) return recovery.Issues; } }
    private string[] Backups() => Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(directory)!, BackupPrefix + "*").Take(33).ToArray();
    public int PreservedRecords { get { lock (gate) return recovery.ArchivedCount + Backups().Length; } }
    public IReadOnlyList<IncomingUpload> All { get { lock (gate) return cache.Rows.Select(ToIncomingUpload).Reverse().ToArray(); } }
    public IncomingUpload? FindUpload(string id) { lock (gate) return cache.Rows.SingleOrDefault(x => x.Id == id) is { } row ? ToIncomingUpload(row) : null; }
    private IncomingUpload ToIncomingUpload(Row row) => new(row.Id, row.Peer, row.Name, row.Size, row.Offset, row.Phase, row.FinalName, row.Message,
        row.Destination.CanonicalRoot.TrimEnd('\\') + "\\" + string.Join('\\', row.Destination.Path), row.SecurityPolicy, scope is null ? OperationExpiry(row) : 0);
    public Task WhenIdleAsync() { lock (gate) return Task.WhenAll(verifying.Values.Select(x => x.Task)); }
    public bool HasCompletedAttachmentCheck(string id) { lock (gate) return cache.Rows.Any(x => x.Id == id && x.Phase == UploadPhase.UploadCompleted && x.SecurityPolicy == ReceivedFileSecurity.PolicyVersion); }
    internal bool IsIdle => verifying.IsEmpty;
    internal string Epoch { get { lock (gate) { Ready(); return cache.Epoch; } } }
    internal static void ValidateScopedJournal(WindowsScopedUpload scope, string owner)
    { var saved = StateRecovery.Read<Cache>(scope.JournalPath, WindowsBatchUploadJournals.RecordLimit); Validate(saved); ValidateScope(saved, owner, scope); }
    internal static UploadInfo InspectScopedJournal(WindowsScopedUpload scope, string owner)
    { var saved = StateRecovery.Read<Cache>(scope.JournalPath, WindowsBatchUploadJournals.RecordLimit); Validate(saved); ValidateScope(saved, owner, scope); return Info(saved.Rows.Single()); }
    internal static bool ScopedPartialExists(WindowsScopedUpload scope, string owner)
    {
        var saved = StateRecovery.Read<Cache>(scope.JournalPath, WindowsBatchUploadJournals.RecordLimit); Validate(saved); ValidateScope(saved, owner, scope); var row = saved.Rows.Single();
        using var root = SecureWindowsDirectory.OpenPath(row.Destination.CanonicalRoot, row.Destination.RootIdentity, row.Destination.Path, true);
        if (root.Info.Identity != row.Destination.Identity) throw new StorageException(FileStatus.StaleReference, "The original partial location changed.");
        try { using var file = root.OpenTransferFile(row.Temporary, false); RequireFile(row, file, 0); return true; }
        catch (StorageException ex) when (ex.Status == FileStatus.NotFound) { return false; }
    }
    internal UploadInfo StopScopedPartial()
    {
        if (scope is null || !scope.StopOnly) throw new InvalidOperationException("Only the stopped parent may clean a scoped partial.");
        scope.Authorize(); lock (gate)
        {
            Ready(); if (!IsIdle) throw new StorageException(FileStatus.Conflict, "File verification is still stopping.");
            var row = cache.Rows.Single(); using var root = Open(row);
            if (UploadRules.IsTerminal(row.Phase) || row.FinalName.Length != 0)
            {
                if (ScopedPartialExists(scope, grants.OwnerScope)) throw new StorageException(FileStatus.Conflict, "A published copy's former private path changed. Preserve it for owner review.");
                if (!UploadRules.IsTerminal(row.Phase)) row = Put(row with { Phase = UploadPhase.UploadReview,
                    Message = "The parent batch stopped. A possible published copy was kept for review." });
                return Info(row);
            }
            try { using var file = root.OpenTransferFile(row.Temporary, false); RequireFile(row, file, 0); file.Delete(); }
            catch (StorageException ex) when (ex.Status == FileStatus.NotFound) { }
            if (!UploadRules.IsTerminal(row.Phase)) row = Put(row with { Phase = row.FinalName.Length == 0 ? UploadPhase.UploadCanceled : UploadPhase.UploadReview,
                Message = "The parent batch stopped. Identified private staging was removed; possible published copies were kept." });
            return Info(row);
        }
    }
    private static void ValidateScope(Cache saved, string owner, WindowsScopedUpload? scope)
    {
        if (scope is null) { if (saved.Scope.Length != 0) throw new InvalidDataException("A scoped journal cannot become ordinary incoming history."); return; }
        if (saved.Scope != scope.Id || saved.OwnerScope != owner || saved.Rows.Length != 1 || !MatchesScope(saved.Rows[0], scope)) throw new InvalidDataException("The file journal does not belong to this batch child.");
    }
    private static bool MatchesScope(Row row, WindowsScopedUpload scope) => row.Id == scope.Id && row.Peer == scope.Peer && row.Name == scope.Descriptor.Name && row.Size == scope.Descriptor.Size &&
        row.Hash == Convert.ToHexStringLower(scope.Descriptor.Sha256.Span) && row.Collision == scope.Descriptor.Collision && row.Destination.Grant == scope.Destination.Grant &&
        row.Destination.Identity == scope.Destination.Identity && row.Destination.RootIdentity == scope.Destination.RootIdentity && row.Destination.CanonicalRoot == scope.Destination.CanonicalRoot && row.Destination.Path.SequenceEqual(scope.Destination.Path);
    internal long ReservedBytes(string? peer) { lock (gate) { Ready(); var oldest = grants.Clock.GetUtcNow().ToUnixTimeMilliseconds() - MutationRules.Lifetime;
        return cache.Rows.Where(x => (peer is null || x.Peer == peer) && x.Phase is not (UploadPhase.UploadCanceled or UploadPhase.UploadSkipped) &&
            (!UploadRules.IsTerminal(x.Phase) || OperationStart(x) >= oldest)).Sum(x => x.Size); } }
    public void PauseVerification()
    {
        InvalidatePublishedChecks();
        ClearPayloadRates();
        foreach (var item in verifying.Values) { try { item.Cancellation.Cancel(); } catch (ObjectDisposedException) { } }
    }
    // Owner action only; current peer permission is irrelevant to inspecting a
    // previously recorded local copy. This never creates a new file or grant.
    public Task CheckOwnedPublishedAsync(string id) => grants.WithAuthorization(() => { lock (gate)
    {
        Ready(); var row = cache.Rows.Single(x => x.Id == id);
        if (verifying.ContainsKey(id) || verifying.Count >= 2 || row.FinalName.Length == 0 || row.TemporaryIdentity.Length == 0 || row.Offset != row.Size)
            throw new IOException("Choose an idle published copy with a complete recorded identity.");
        var slot = grants.ReserveIncomingVerification() ?? throw new IOException("Two incoming copies are already being verified.");
        try { Put(row with { Phase = UploadPhase.UploadFinalizing, SecurityPolicy = 0, Message = "Owner requested Windows attachment checks of the recorded copy. Peer access stays unchanged." }); }
        catch { slot.Dispose(); throw; }
        var cancellation = new CancellationTokenSource(); var work = new Work(cancellation, slot, new(id, row.Size, () => PayloadNow)); verifying.TryAdd(id, work);
        work.Task = Task.Run(() => VerifyAndPublish(id, work, ownerOnly: true)); return work.Task;
    } });
    // Local owner action only. No remote operation calls this method or derives its authority.
    public async Task DiscardOwnedPartialAsync(string id)
    {
        if (verifying.TryGetValue(id, out var requested)) { try { requested.Cancellation.Cancel(); } catch (ObjectDisposedException) { } }
        Task stopping = Task.CompletedTask;
        grants.WithAuthorization(() => { lock (gate)
        {
            Ready(); var row = cache.Rows.Single(x => x.Id == id);
            if (UploadRules.IsTerminal(row.Phase)) return true;
            if (row.FinalName.Length != 0) throw new IOException("Publication may have completed. Review its verified receipt before discarding anything.");
            if (verifying.TryGetValue(id, out var work)) { work.Cancellation.Cancel(); stopping = work.Task; }
            Put(row with { Phase = UploadPhase.UploadReview, Message = "The owner stopped this incoming copy. Partial cleanup is pending." }); return true;
        } });
        await stopping;
        grants.WithAuthorization(() => { lock (gate)
        {
            Ready(); var row = cache.Rows.Single(x => x.Id == id); if (UploadRules.IsTerminal(row.Phase)) return true;
            if (row.FinalName.Length != 0 || row.TemporaryIdentity.Length == 0) throw new IOException("This partial cannot be identified safely. Preserve it for local review.");
            grants.RequirePublicLocation(row.Destination.CanonicalRoot.TrimEnd('\\') + "\\" + string.Join('\\', row.Destination.Path));
            using var root = SecureWindowsDirectory.OpenPath(row.Destination.CanonicalRoot, row.Destination.RootIdentity, row.Destination.Path, true);
            if (root.Info.Identity != row.Destination.Identity) throw new IOException("The incoming destination folder changed. Its contents were kept.");
            try { using var file = root.OpenTransferFile(row.Temporary, false); RequireFile(row, file, 0); file.Delete(); }
            catch (StorageException ex) when (ex.Status == FileStatus.NotFound) { }
            Put(row with { Phase = UploadPhase.UploadCanceled, Message = "The owner discarded the identified partial. Published files were kept." }); return true;
        } });
    }
    public void ArchiveRecovery(string id) => grants.WithAuthorization(() =>
    {
        lock (gate)
        {
            if (verifying.Count != 0) throw new IOException("Pause connections before archiving incoming-copy metadata.");
            recovery.Archive(id);
            if (!recovery.Needed) Save(new(1, grants.OwnerScope, ProtocolRules.NewToken(), []));
            return true;
        }
    });
    public void ClearPreservedRecords()
    {
        lock (gate)
        {
            recovery.ClearArchives(); using var parent = SecureWindowsDirectory.OpenRoot(Path.GetDirectoryName(directory)!);
            foreach (var backup in Backups())
            {
                var name = Path.GetFileName(backup);
                if (!ProtocolRules.TokenPattern().IsMatch(name[BackupPrefix.Length..])) throw new IOException("An incoming-history backup has an unexpected name. It was kept.");
                new StateRecovery(backup).ClearArchives();
                using var folder = parent.OpenManagementChild(name, true, mutation: true);
                try
                {
                    using var shelf = folder.OpenManagementChild("recovery", true, mutation: true);
                    if (shelf.EnumerateNames(default).Take(1).Any()) throw new IOException("A history backup contains unexpected recovery data. It was kept.");
                    shelf.Delete();
                }
                catch (StorageException ex) when (ex.Status == FileStatus.NotFound) { }
                var files = folder.EnumerateNames(default).Take(4).ToArray();
                if (files.Length > 3 || files.Any(x => x.IsDirectory || x.Name is not ("journal.json" or "journal.json.new" or "journal.json.bak"))) throw new IOException("A backup contains unexpected entries. Its contents were kept.");
                foreach (var file in files) { using var metadata = folder.OpenMutableChild(file.Name); metadata.Delete(); }
                folder.Delete();
            }
        }
    }
    public void ArchiveHistory() => grants.WithAuthorization(() =>
    {
        lock (gate)
        {
            if (!verifying.IsEmpty) throw new IOException("Pause connections and wait for incoming verification before resetting history.");
            if (Backups().Length >= 32) throw new IOException("Review the preserved incoming-history backups before archiving another.");
            using var parent = SecureWindowsDirectory.OpenRoot(Path.GetDirectoryName(directory)!);
            using (var previous = parent.OpenManagementChild("incoming-uploads", true, mutation: true)) previous.RenameTo(parent, BackupPrefix + ProtocolRules.NewToken());
            using var fresh = parent.CreateDirectory("incoming-uploads");
            partRecoveryDirectoryIdentity = fresh.Info.Identity;
            recovery = new(directory); checkedPrefixes.Clear(); Save(new(1, grants.OwnerScope, ProtocolRules.NewToken(), [])); Changed?.Invoke(); MetadataChanged?.Invoke(); return true;
        }
    });
    internal void OwnerScopeChanged()
    {
        lock (gate)
        {
            InvalidateOperationReviews();
            if (cache.OwnerScope == grants.OwnerScope || recovery.Needed) return;
            if (cache.Rows.Length == 0 && verifying.IsEmpty) Save(new(1, grants.OwnerScope, ProtocolRules.NewToken(), []));
            else recovery.Add(path, "Incoming-copy history belongs to earlier owner authority. Archive its metadata to accept fresh copies; partial and published files are preserved.");
        }
    }
    private void Ready()
    {
        recovery.RequireHealthy();
        if (cache.OwnerScope != grants.OwnerScope) throw new StorageException(FileStatus.AccessDenied, "Incoming-copy history belongs to earlier owner authority. Review it locally.");
        if (cache.Scope != (scope?.Id ?? "")) throw new StorageException(FileStatus.Conflict, "Incoming-copy scope changed.");
    }
    private void Save(Cache updated, bool progressOnly = false)
    {
        if (updated.Epoch != cache.Epoch || updated.OwnerScope != cache.OwnerScope)
        {
            InvalidateOperationReviews();
            ClearPayloadRates();
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(updated);
        if (scope is not null)
        {
            try { scope.Persist(bytes, progressOnly); cache = updated; }
            catch { ClearPayloadRates(); throw; }
            return;
        }
        if (bytes.Length > 2 * 1024 * 1024) throw new StorageException(FileStatus.ResourceExhausted, "Incoming-copy metadata is full.");
        try
        {
            using (var file = new FileStream(path + ".new", FileMode.Create, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
            AtomicStateFile.Commit(path + ".new", path); cache = updated;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { ClearPayloadRates(); recovery.Block("Incoming progress could not be saved. Preserve the files and review this queue locally."); MetadataChanged?.Invoke(); throw; }
    }
    private long lastNotification;
    private Row Put(Row row, string? writeScope = null)
    {
        var previous = cache.Rows.SingleOrDefault(x => x.Id == row.Id);
        var phase = previous?.Phase;
        var progressOnly = !string.IsNullOrEmpty(writeScope) && previous is not null && row.Phase == UploadPhase.UploadReceiving &&
            row.Offset > previous.Offset && row.Offset < row.Size && previous == (row with { Offset = previous.Offset, Prefix = previous.Prefix });
        Save(cache with { Rows = cache.Rows.Where(x => x.Id != row.Id).Append(row).ToArray() }, progressOnly);
        UpdatePayload(row, previous, writeScope);
        if (phase != row.Phase && row.Phase is UploadPhase.UploadCompleted or UploadPhase.UploadReview or UploadPhase.UploadCanceled or UploadPhase.UploadSkipped)
            grants.RecordActivity(new(scope is null ? WindowsActivityKind.IncomingCopy : WindowsActivityKind.BatchFile,
                row.Phase == UploadPhase.UploadCompleted ? WindowsActivityOutcome.Completed : row.Phase == UploadPhase.UploadReview ? WindowsActivityOutcome.Review : row.Phase == UploadPhase.UploadSkipped ? WindowsActivityOutcome.Skipped : WindowsActivityOutcome.Canceled,
                row.Peer, row.Phase == UploadPhase.UploadCompleted ? row.Size : -1));
        var now = Environment.TickCount64;
        if (phase != row.Phase || row.Offset == row.Size || now - lastNotification >= 200) { lastNotification = now; Changed?.Invoke(); }
        if (!progressOnly) MetadataChanged?.Invoke();
        return row;
    }
    private static UploadInfo Info(Row row) => new() { Id = row.Id, Name = row.Name, Size = row.Size, Offset = row.Offset,
        Sha256 = ByteString.CopyFrom(Convert.FromHexString(row.Hash)), Collision = row.Collision, Phase = row.Phase, FinalName = row.FinalName, Message = row.Message };
    private static void Validate(Cache saved)
    {
        if (saved.Scope is null || saved.Scope.Length > 128 || saved.Rows is null || saved.Rows.Any(x => x is null || x.Destination is null) || saved.Schema != 1 || saved.OwnerScope.Length is < 1 or > 128 || !ProtocolRules.TokenPattern().IsMatch(saved.Epoch) || saved.Rows.Length > 128 || saved.Rows.Select(x => x.Id).Distinct().Count() != saved.Rows.Length)
            throw new InvalidDataException("Invalid incoming-copy journal.");
        foreach (var row in saved.Rows)
        {
            UploadRules.Validate(Info(row));
            if (row.Peer.Length != 64 || row.Peer.Any(c => !char.IsAsciiHexDigitLower(c)) || !ProtocolRules.TokenPattern().IsMatch(row.Destination.Grant) ||
                !SecureWindowsDirectory.IsLocalVolumePath(row.Destination.CanonicalRoot) || row.Destination.CanonicalRoot.Length > 8192 ||
                row.Destination.RootIdentity.Length is < 1 or > 256 || row.Destination.Identity.Length is < 1 or > 256 || row.Destination.Path.Length > 128 ||
                !ProtocolRules.TokenPattern().IsMatch(row.Temporary) || row.TemporaryIdentity.Length > 256 ||
                row.Prefix.Length != 64 || row.Prefix.Any(c => !char.IsAsciiHexDigitLower(c)) || row.Offset != row.Size && row.Offset % StorageRules.MaxReadRange != 0 ||
                row.Offset > 0 && row.TemporaryIdentity.Length == 0 || row.SecurityPolicy is < 0 or > ReceivedFileSecurity.PolicyVersion ||
                row.OperationApprovedAt != 0 && (saved.Scope.Length != 0 || row.OperationApprovedAt < MutationRules.CreatedAt(row.Id) || row.OperationApprovedAt > StorageRules.MaxTimestamp - MutationRules.Lifetime)) throw new InvalidDataException("Invalid incoming-copy ownership or checkpoint.");
            foreach (var segment in row.Destination.Path) StorageRules.ValidateName(segment);
            StorageRules.ValidateName(row.PreferredName);
        }
    }
    private StorageGrant Authorize(Row row)
    {
        Ready(); if (scope is not null) { scope.Authorize(); if (!MatchesScope(row, scope)) throw new InvalidDataException("The scoped file binding changed."); }
        var grant = grants.Get(row.Destination.Grant, row.Peer);
        StorageRules.Require(grant.Permissions, StoragePermission.Write | StoragePermission.Create);
        if (grant.RootIdentity != row.Destination.RootIdentity || !grant.CanonicalPath.Equals(row.Destination.CanonicalRoot, StringComparison.OrdinalIgnoreCase)) throw new StorageException(FileStatus.AccessDenied, "The incoming destination grant changed.");
        grants.ValidateAccess(grant, row.Destination.Path); grants.ValidateAccess(grant, row.Destination.Path.Append(row.Name).ToArray());
        if (row.FinalName.Length != 0) grants.ValidateAccess(grant, row.Destination.Path.Append(row.FinalName).ToArray());
        return grant;
    }
    private SecureWindowsDirectory Open(Row row)
    {
        var grant = Authorize(row);
        var root = scope?.Open() ?? SecureWindowsDirectory.OpenPath(grant.CanonicalPath, grant.RootIdentity, row.Destination.Path, true);
        if (root.Info.Identity != row.Destination.Identity) { root.Dispose(); throw new StorageException(FileStatus.StaleReference, "The incoming destination folder was replaced."); }
        return root;
    }
    private static void RequireFile(Row row, SecureWindowsDirectory file, long minimum)
    {
        var info = file.Info;
        if (info.Identity != row.TemporaryIdentity || info.IsDirectory || info.Links != 1 || info.Size < minimum ||
            (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0) throw new StorageException(FileStatus.Conflict, "The incoming partial file changed. Review it locally.");
    }
    private static byte[] Chain(byte[] previous, long offset, ReadOnlySpan<byte> data)
    { Span<byte> input = stackalloc byte[72]; previous.CopyTo(input); BinaryPrimitives.WriteInt64LittleEndian(input[32..], offset); SHA256.HashData(data, input[40..]); return SHA256.HashData(input); }
    private static void ReadExactly(SecureWindowsDirectory file, long offset, Span<byte> bytes)
    {
        var read = 0;
        while (read < bytes.Length) { var count = RandomAccess.Read(file.Handle, bytes[read..], offset + read); if (count == 0) throw new IOException("The incoming partial file ended unexpectedly."); read += count; }
    }
    private void CheckPrefix(Row row, SecureWindowsDirectory file, CancellationToken ct)
    {
        RequireFile(row, file, row.Offset);
        if (checkedPrefixes.Contains(row.Id)) return;
        VerifyPrefix(row, file, ct);
        checkedPrefixes.Add(row.Id);
    }
    private static void VerifyPrefix(Row row, SecureWindowsDirectory file, CancellationToken ct, Action? check = null)
    {
        RequireFile(row, file, row.Offset);
        var chain = new byte[32]; var buffer = new byte[StorageRules.MaxReadRange]; var offset = 0L;
        while (offset < row.Offset) { ct.ThrowIfCancellationRequested(); check?.Invoke(); var count = (int)Math.Min(buffer.Length, row.Offset - offset); ReadExactly(file, offset, buffer.AsSpan(0, count)); chain = Chain(chain, offset, buffer.AsSpan(0, count)); offset += count; }
        if (!CryptographicOperations.FixedTimeEquals(chain, Convert.FromHexString(row.Prefix))) throw new StorageException(FileStatus.Conflict, "The acknowledged incoming prefix changed. Cancel this copy or review it locally.");
    }
    internal FileResponse Execute(string peer, FileRequest request, Func<UploadDestination> destination, CancellationToken ct, string requestScope = "")
    {
        lock (gate)
        {
            Ready(); var result = new FileResponse { RequestId = request.RequestId, MutationEpoch = cache.Epoch };
            if (request.Operation == FileOperation.UploadContext) return result;
            if (request.MutationEpoch != cache.Epoch) throw new StorageException(FileStatus.StaleReference, "Incoming-copy history changed. Review a new copy context.");
            if (request.Operation == FileOperation.PrepareUpload)
            {
                var descriptor = request.Upload ?? throw new InvalidDataException("An incoming descriptor is required."); UploadRules.Validate(descriptor);
                if (request.TransferId != descriptor.Id || descriptor.Offset != 0 || descriptor.Phase != UploadPhase.UploadPending || descriptor.FinalName.Length != 0 || descriptor.Message.Length != 0)
                    throw new InvalidDataException("Use a fresh incoming descriptor.");
                if (scope is null) MutationRules.RequireCurrent(descriptor.Id, grants.Clock.GetUtcNow().ToUnixTimeMilliseconds());
                else if (descriptor.Id != scope.Id || !descriptor.Equals(scope.Descriptor)) throw new InvalidDataException("Use the original scoped file descriptor.");
                var target = destination();
                var row = new Row(descriptor.Id, peer, target, descriptor.Name, descriptor.Size, Convert.ToHexStringLower(descriptor.Sha256.Span), descriptor.Collision, ProtocolRules.NewToken());
                Authorize(row);
                row = row with { PreferredName = (grants.Get(target.Grant, peer).Permissions & StoragePermission.List) == 0
                    ? AddSuffix(row.Name, " (" + ProtocolRules.NewToken()[..16] + ")") : row.Name };
                if (cache.Rows.SingleOrDefault(x => x.Id == row.Id) is { } previous)
                {
                    if (previous.Peer != peer || previous.Name != row.Name || previous.Size != row.Size || previous.Hash != row.Hash || previous.Collision != row.Collision ||
                        previous.Destination.Grant != target.Grant || previous.Destination.Identity != target.Identity || previous.Destination.RootIdentity != target.RootIdentity || previous.Destination.CanonicalRoot != target.CanonicalRoot || !previous.Destination.Path.SequenceEqual(target.Path))
                        throw new StorageException(FileStatus.Conflict, "This incoming identifier already describes a different copy.");
                    result.Upload = Info(previous); return result;
                }
                var now = grants.Clock.GetUtcNow().ToUnixTimeMilliseconds();
                var kept = scope is null ? cache.Rows.Where(x => !UploadRules.IsTerminal(x.Phase) || OperationStart(x) >= now - MutationRules.Lifetime).ToArray() : cache.Rows;
                if (scope is not null ? kept.Length != 0 : kept.Length >= 128 || kept.Count(x => x.Peer == peer) >= 32 || row.Size > UploadRules.PeerQuota - kept.Where(x => x.Peer == peer && x.Phase is not (UploadPhase.UploadCanceled or UploadPhase.UploadSkipped)).Sum(x => x.Size) - grants.BatchPlans.ReservedBytes(peer) ||
                    row.Size > UploadRules.TotalQuota - kept.Where(x => x.Phase is not (UploadPhase.UploadCanceled or UploadPhase.UploadSkipped)).Sum(x => x.Size) - grants.BatchPlans.ReservedBytes(null))
                    throw new StorageException(FileStatus.ResourceExhausted, "Incoming-copy record or reserved-byte quota reached. Review existing copies locally.");
                using var root = Open(row);
                Save(cache with { Rows = kept.Append(row).ToArray() }); // Intent is durable before creating anything.
                try
                {
                    Authorize(row);
                    using var temporary = root.OpenTransferFile(row.Temporary, true); temporary.Flush();
                    row = Put(row with { TemporaryIdentity = temporary.Info.Identity });
                    temporary.MarkUntrusted();
                    row = Put(row with { Phase = UploadPhase.UploadReceiving, Message = "Receiving an untrusted new file; existing files are kept." }); checkedPrefixes.Add(row.Id);
                }
                catch { if (!recovery.Needed) Put(row with { Phase = UploadPhase.UploadReview, Message = "Creation was interrupted. Review the preserved partial file." }); throw; }
                result.Upload = Info(row); return result;
            }
            if (request.Operation == FileOperation.ListUploads)
            {
                if (request.Offset is < 0 or > 128) throw new InvalidDataException("Use a bounded incoming-copy page.");
                var rows = cache.Rows.Where(x => x.Peer == peer).OrderByDescending(x => MutationRules.CreatedAt(x.Id)).ToArray();
                foreach (var item in rows.Skip((int)request.Offset).Take(32)) { try { Authorize(item); result.Uploads.Add(Info(item)); } catch (StorageException) { } }
                result.EndOfFile = request.Offset + 32 >= rows.Length; return result;
            }
            var current = cache.Rows.SingleOrDefault(x => x.Id == request.TransferId && x.Peer == peer) ?? throw new StorageException(FileStatus.NotFound, "This device has no matching incoming-copy receipt.");
            Authorize(current);
            if (request.Operation == FileOperation.VerifyPublishedUpload) return VerifyPublished(current, request, requestScope, result, ct);
            if (request.Operation == FileOperation.UploadStatus) { result.Upload = Info(current); return result; }
            if (request.Operation == FileOperation.CancelUpload)
            {
                if (UploadRules.IsTerminal(current.Phase)) { result.Upload = Info(current); return result; }
                if (current.FinalName.Length != 0) throw new StorageException(FileStatus.Conflict, "Publication may have completed. Finish verification and inspect the receipt; published files are kept.");
                if (verifying.TryGetValue(current.Id, out var active))
                { active.Cancellation.Cancel(); result.Status = FileStatus.Conflict; result.Message = "Verification is stopping. Check status and repeat cancellation after it stops."; result.Upload = Info(current); return result; }
                using var root = Open(current);
                current = Put(current with { Phase = UploadPhase.UploadReview, Message = "Cancellation cleanup is pending." });
                if (current.TemporaryIdentity.Length == 0) throw new StorageException(FileStatus.Conflict, "The partial identity is unknown; review its location locally.");
                try { using var file = root.OpenTransferFile(current.Temporary, false); RequireFile(current, file, 0); Authorize(current); file.Delete(); }
                catch (StorageException ex) when (ex.Status == FileStatus.NotFound) { }
                current = Put(current with { Phase = UploadPhase.UploadCanceled, Message = "Canceled. The owned partial file was removed; published files were kept." });
            }
            else
            {
                if (scope is null) RequireOperationCurrent(current);
                if (request.Operation == FileOperation.UploadWrite)
                {
                    if (current.Phase != UploadPhase.UploadReceiving || request.Offset < 0 || request.Offset >= current.Size || request.Offset % StorageRules.MaxReadRange != 0 ||
                        request.Content.Length != Math.Min(StorageRules.MaxReadRange, current.Size - request.Offset) || request.Offset > current.Offset)
                        throw new StorageException(FileStatus.Conflict, "Refresh the acknowledged incoming offset before sending the next bounded range.");
                    using var root = Open(current); using var file = root.OpenTransferFile(current.Temporary, false); CheckPrefix(current, file, ct);
                    if (request.Offset < current.Offset)
                    {
                        if (request.Offset + request.Content.Length > current.Offset) throw new InvalidDataException("Invalid repeated range.");
                        var retained = new byte[request.Content.Length]; ReadExactly(file, request.Offset, retained);
                        if (!CryptographicOperations.FixedTimeEquals(retained, request.Content.Span)) throw new StorageException(FileStatus.Conflict, "A repeated incoming range contains different bytes.");
                    }
                    else
                    {
                        Authorize(current);
                        file.SetLength(current.Offset); ct.ThrowIfCancellationRequested(); file.Write(current.Offset, request.Content.Span); file.Flush();
                        current = Put(current with { Offset = current.Offset + request.Content.Length, Prefix = Convert.ToHexStringLower(Chain(Convert.FromHexString(current.Prefix), current.Offset, request.Content.Span)) }, requestScope);
                    }
                }
                else if (request.Operation == FileOperation.FinishUpload)
                {
                    if (current.Phase == UploadPhase.UploadCompleted) { result.Upload = Info(current); return result; }
                    if (current.Offset != current.Size || current.Phase is not (UploadPhase.UploadReceiving or UploadPhase.UploadVerifying or UploadPhase.UploadFinalizing) && !(current.Phase == UploadPhase.UploadReview && current.FinalName.Length != 0))
                        throw new StorageException(FileStatus.Conflict, "Receive all bytes before verifying this incoming copy.");
                    if (!verifying.ContainsKey(current.Id))
                    {
                        if (verifying.Count >= 2) throw new StorageException(FileStatus.ResourceExhausted, "Two incoming copies are already being verified. Retry shortly.");
                        var slot = grants.ReserveIncomingVerification() ?? throw new StorageException(FileStatus.ResourceExhausted, "Two incoming copies are already being verified. Retry shortly.");
                        try { current = Put(current with { Phase = current.FinalName.Length == 0 ? UploadPhase.UploadVerifying : UploadPhase.UploadFinalizing, Message = "Verifying the retained file before publication." }); }
                        catch { slot.Dispose(); throw; }
                        var id = current.Id; var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        var work = new Work(cancellation, slot, new(id, current.Size, () => PayloadNow)); verifying.TryAdd(id, work);
                        work.Task = Task.Run(() => VerifyAndPublish(id, work));
                    }
                }
                else throw new StorageException(FileStatus.Unsupported, "Unsupported incoming-copy operation.");
            }
            result.Upload = Info(current); return result;
        }
    }
    private async Task VerifyAndPublish(string id, Work work, bool ownerOnly = false)
    {
        var cancellation = work.Cancellation; var progress = work.Finalization!;
        void Allowed(Row value)
        {
            if (!ownerOnly) { Authorize(value); return; }
            Ready(); if (value.FinalName.Length == 0 || value.TemporaryIdentity.Length == 0 || value.Offset != value.Size) throw new IOException("The recorded local copy is incomplete.");
            grants.RequirePublicLocation(value.Destination.CanonicalRoot.TrimEnd('\\') + "\\" + string.Join('\\', value.Destination.Path.Append(value.FinalName)));
        }
        try
        {
            Row row = null!; SecureWindowsDirectory root = null!, file = null!; var alreadyPublished = false;
            try
            {
                grants.WithAuthorization(() => { lock (gate)
                {
                    row = cache.Rows.Single(x => x.Id == id); Allowed(row);
                    root = ownerOnly ? SecureWindowsDirectory.OpenPath(row.Destination.CanonicalRoot, row.Destination.RootIdentity, row.Destination.Path, true) : Open(row);
                    if (root.Info.Identity != row.Destination.Identity) throw new IOException("The recorded destination changed.");
                    try {
                        if (ownerOnly) { file = root.OpenChild(row.FinalName); alreadyPublished = true; }
                        else { file = root.OpenTransferFile(row.Temporary, false); CheckPrefix(row, file, cancellation.Token); Allowed(row); file.SetLength(row.Size); file.Flush(); }
                    }
                    catch (StorageException ex) when (ex.Status == FileStatus.NotFound && row.FinalName.Length != 0)
                    { file = root.OpenChild(row.FinalName); alreadyPublished = true; }
                    RequireFile(row, file, row.Size); if (file.Info.Size != row.Size) throw new IOException("The incoming size changed."); file.MarkUntrusted();
                    return true;
                } });
                progress.Begin(alreadyPublished ? IncomingFinalizationStage.CheckingPublishedBytes : IncomingFinalizationStage.CheckingSavedBytes);
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var bytes = new byte[StorageRules.MaxReadRange]; var offset = 0L;
                while (offset < row.Size)
                {
                    cancellation.Token.ThrowIfCancellationRequested(); var length = (int)Math.Min(bytes.Length, row.Size - offset);
                    grants.WithAuthorization(() => { lock (gate) { Allowed(row); if (cache.Rows.Single(x => x.Id == id).Phase is not (UploadPhase.UploadVerifying or UploadPhase.UploadFinalizing)) throw new OperationCanceledException(); ReadExactly(file, offset, bytes.AsSpan(0, length)); return true; } });
                    digest.AppendData(bytes, 0, length); offset += length; progress.Advance(offset);
                }
                if (!CryptographicOperations.FixedTimeEquals(digest.GetHashAndReset(), Convert.FromHexString(row.Hash))) throw new StorageException(FileStatus.Conflict, "Incoming SHA-256 verification failed. The partial file was kept for review.");
                if (!alreadyPublished) progress.Begin(IncomingFinalizationStage.Publishing);
                grants.WithAuthorization(() => { lock (gate)
                {
                    cancellation.Token.ThrowIfCancellationRequested(); row = cache.Rows.Single(x => x.Id == id); Allowed(row); RequireFile(row, file, row.Size);
                    if (!alreadyPublished)
                    {
                        for (var number = 1; number <= 10_000; number++)
                        {
                            cancellation.Token.ThrowIfCancellationRequested(); Allowed(row);
                            var name = row.FinalName.Length != 0 ? row.FinalName : CollisionName(row.PreferredName, number);
                            grants.ValidateAccess(grants.Get(row.Destination.Grant, row.Peer), row.Destination.Path.Append(name).ToArray());
                            row = Put(row with { Phase = UploadPhase.UploadFinalizing, FinalName = name, Message = "Publication is being reconciled." });
                            cancellation.Token.ThrowIfCancellationRequested(); Allowed(row);
                            try { file.RenameTo(root, name); break; }
                            catch (StorageException ex) when (ex.NativeError is 80 or 183 && row.Collision == UploadCollision.UploadKeepBoth && number < 10_000)
                            { row = Put(row with { Phase = UploadPhase.UploadVerifying, FinalName = "", Message = "Choosing a nonconflicting destination name." }); continue; }
                            catch (StorageException ex) when (ex.NativeError is 80 or 183 && row.Collision == UploadCollision.UploadSkip)
                            {
                                row = Put(row with { Phase = UploadPhase.UploadReview, FinalName = "", Message = "Discarding a collided owned partial." }); file.Delete();
                                row = Put(row with { Phase = UploadPhase.UploadSkipped, Message = "An existing destination was kept." }); return true;
                            }
                            catch (StorageException ex) when (ex.NativeError is 80 or 183)
                            { row = Put(row with { Phase = UploadPhase.UploadReview, FinalName = "", Message = "An existing destination was kept. Cancel this partial or review a new name." }); return true; }
                        }
                    }
                    row = Put(row with { Phase = UploadPhase.UploadFinalizing, Message = "Published untrusted copy. Checking Windows attachment policy before completion." }); return true;
                } });
                if (row.Phase is UploadPhase.UploadSkipped or UploadPhase.UploadReview) return;
                file.Dispose(); file = null!;
                void Current() => grants.WithAuthorization(() => { lock (gate) { Allowed(row); if (cache.Rows.Single(x => x.Id == id).Phase != UploadPhase.UploadFinalizing) throw new OperationCanceledException(); return true; } });
                progress.Begin(IncomingFinalizationStage.AttachmentCheck);
                await ReceivedFileSecurity.VerifyPublishedAsync(root, row.FinalName, row.TemporaryIdentity, row.Size, row.Hash, grants.AttachmentChecks, Current, cancellation.Token,
                    () => progress.Begin(IncomingFinalizationStage.CheckingPublishedBytes), progress.Advance);
                grants.WithAuthorization(() => { lock (gate) { Current(); cancellation.Token.ThrowIfCancellationRequested(); Put(row with { Phase = UploadPhase.UploadCompleted,
                    SecurityPolicy = ReceivedFileSecurity.PolicyVersion, Message = "Verified copy and successful Windows attachment checks. The file remains untrusted." }); return true; } });
            }
            finally { file?.Dispose(); root?.Dispose(); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or OperationCanceledException or InvalidOperationException)
        {
            lock (gate)
            {
                var row = cache.Rows.Single(x => x.Id == id);
                if (row.Phase is UploadPhase.UploadVerifying or UploadPhase.UploadFinalizing && !recovery.Needed)
                {
                    try { Put(row with { Phase = row.FinalName.Length != 0 ? UploadPhase.UploadReview : ex is OperationCanceledException ? UploadPhase.UploadReceiving : UploadPhase.UploadReview,
                        Message = ex is OperationCanceledException ? "Verification paused; finish explicitly after reconnecting." : ex is AttachmentCheckException ? ex.Message : row.FinalName.Length != 0 ?
                            "Windows checks or publication need review. The published copy remains untrusted and was kept. " + string.Concat(ex.Message.Where(c => !char.IsControl(c))).Substring(0, Math.Min(300, string.Concat(ex.Message.Where(c => !char.IsControl(c))).Length)) :
                            "Incoming verification or publication needs review. Existing files were kept." }); }
                    catch (Exception saved) when (saved is IOException or UnauthorizedAccessException) { }
                }
            }
        }
        finally { lock (gate) if (verifying.TryRemove(id, out var finished)) finished.Slot.Dispose(); cancellation.Dispose(); }
    }
    private static string CollisionName(string name, int number)
    {
        if (number == 1) return name;
        return AddSuffix(name, $" ({number})");
    }
    private static string AddSuffix(string name, string suffix)
    {
        var extension = Path.GetExtension(name); var stem = Path.GetFileNameWithoutExtension(name); var maximum = 255 - extension.Length - suffix.Length;
        if (maximum < 1) throw new IOException("Choose a shorter incoming file name.");
        if (stem.Length > maximum) stem = stem[..maximum].TrimEnd();
        if (stem.Length > 0 && char.IsHighSurrogate(stem[^1])) stem = stem[..^1];
        var result = stem + suffix + extension; StorageRules.ValidateName(result); return result;
    }
}
