using IConnect.Core;
using IConnect.Protocol;
using System.Collections.Concurrent;
using System.Text.Json;

namespace IConnect.Storage.Windows;

internal sealed record MutationTarget(string Grant, string RootIdentity, string[] Path, string Identity);
internal sealed record WindowsMutation(string Id, string Peer, FileOperation Operation, string Fingerprint, MutationTarget? Source,
    MutationTarget Destination, string Name, string ExpectedVersion, bool Directory, MutationPhase Phase = MutationPhase.ChangePending,
    FileStatus Status = FileStatus.Ok, string Message = "The operation is awaiting reconciliation.", string ResultName = "", string ResultIdentity = "", string RequestedName = "");
internal sealed record WindowsMutationResult(string Name, string Identity);

/// <summary>Durable write-ahead receipts. A repeated request never replays a filesystem effect.</summary>
internal sealed class WindowsMutations
{
    private sealed record Cache(int Schema, string Epoch, WindowsMutation[] Records);
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate;
    private readonly IWindowsFileAuthority grants;
    private readonly bool local;
    private readonly string path;
    private StateRecovery recovery;
    private Cache cache = new(1, "", []);
    public WindowsMutations(IWindowsFileAuthority grants, string directory, bool local = false)
    { this.grants = grants; this.local = local; directory = Path.Combine(directory, "file-operations"); Directory.CreateDirectory(directory); path = Path.Combine(directory, "journal.json"); gate = Gates.GetOrAdd(Path.GetFullPath(path), _ => new()); recovery = new(directory); lock (gate) { try { Load(); } catch (Exception ex) when (StateRecovery.IsRecordFailure(ex)) { recovery.Add(path, "File-operation receipts need review. Existing files were kept; writes are blocked until this record is archived."); } } }
    public IReadOnlyList<StateRecoveryIssue> RecoveryIssues { get { lock (gate) return recovery.Issues; } }
    public int PreservedRecords { get { lock (gate) return recovery.ArchivedCount; } }
    public void ClearPreservedRecords() { lock (gate) recovery.ClearArchives(); }
    public void ArchiveRecovery(string id)
    { lock (gate) { if (!recovery.Issues.Any(x => x.Id == id)) return; recovery.Archive(id); if (!recovery.Needed) { cache = new(1, ProtocolRules.NewToken(), []); Save(cache); } } }
    private void Load()
    {
        if (!File.Exists(path))
        {
            if (File.Exists(path + ".new") || File.Exists(path + ".bak")) throw new InvalidDataException("Uncommitted file-operation receipts need review.");
            cache = new(1, ProtocolRules.NewToken(), []); Save(cache); return;
        }
        var loaded = StateRecovery.Read<Cache>(path, 8 * 1024 * 1024);
        if (loaded.Schema != 1 || !ProtocolRules.TokenPattern().IsMatch(loaded.Epoch) || loaded.Records.Length > 512 || loaded.Records.Select(x => x.Id).Distinct().Count() != loaded.Records.Length)
            throw new InvalidDataException("Invalid file-operation journal.");
        foreach (var row in loaded.Records)
        {
            if (row.ResultIdentity is null || row.RequestedName is null) throw new InvalidDataException("Invalid created-folder receipt fields.");
            MutationRules.Validate(Info(row));
            if (!MutationRules.IsChange(row.Operation)) throw new InvalidDataException("Invalid file-operation type.");
            if ((local ? row.Peer.Length != 0 : row.Peer.Length != 64 || row.Peer.Any(c => !char.IsAsciiHexDigitLower(c))) || row.Fingerprint.Length != 64 || row.Fingerprint.Any(c => !char.IsAsciiHexDigitLower(c)) ||
                (row.Source is null) != (row.Operation == FileOperation.CreateDirectory) || row.ExpectedVersion.Length > 256 ||
                row.Operation == FileOperation.CreateDirectory && !row.Directory || row.ResultIdentity.Length > 256 || row.RequestedName.Length > 255)
                throw new InvalidDataException("Invalid receipt ownership or operation.");
            ValidateTarget(row.Destination); if (row.Source is not null) ValidateTarget(row.Source); StorageRules.ValidateName(row.Name);
            if (row.RequestedName.Length != 0) StorageRules.ValidateName(row.RequestedName);
            if (row.ResultName.Length != 0) StorageRules.ValidateName(row.ResultName);
        }
        cache = loaded;
    }
    private static void ValidateTarget(MutationTarget target)
    {
        if (!ProtocolRules.TokenPattern().IsMatch(target.Grant) || target.RootIdentity.Length is < 1 or > 256 || target.Path.Length > 128 || target.Identity.Length is < 1 or > 256)
            throw new InvalidDataException("Invalid receipt location.");
        foreach (var name in target.Path) StorageRules.ValidateName(name);
    }
    private void Ready()
    {
        recovery.RequireHealthy();
        try { Load(); }
        catch (Exception ex) when (StateRecovery.IsRecordFailure(ex)) { recovery.Add(path, "File-operation receipts changed or could not be read. Review them on this computer before writing."); recovery.RequireHealthy(); }
    }
    private void Save(Cache updated)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(updated);
        if (bytes.Length > 8 * 1024 * 1024) throw new IOException("File-operation receipt storage is full. Wait for old receipts to expire before new changes.");
        using (var file = new FileStream(path + ".new", FileMode.Create, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
        AtomicStateFile.Commit(path + ".new", path); cache = updated;
    }
    private WindowsMutation Put(WindowsMutation row)
    { Save(cache with { Records = cache.Records.Where(x => x.Id != row.Id).Append(row).ToArray() }); return row; }
    private StorageGrant TargetGrant(MutationTarget target, string peer, StoragePermission permission)
    {
        var grant = grants.Get(target.Grant, peer); StorageRules.Require(grant.Permissions, permission);
        if (grant.RootIdentity != target.RootIdentity) throw new StorageException(FileStatus.StaleReference, "This operation belongs to a replaced storage location.");
        grants.ValidateLocation(grant, target.Path); return grant;
    }
    private void Authorize(WindowsMutation row)
    {
        var required = row.Operation == FileOperation.Rename ? StoragePermission.Rename : StoragePermission.Move;
        if (row.Source is not null) _ = TargetGrant(row.Source, row.Peer, required);
        var destination = TargetGrant(row.Destination, row.Peer, StoragePermission.Create | (row.Source is null ? 0 : required));
        grants.ValidateLocation(destination, row.Destination.Path.Append(row.Name).ToArray());
        if (row.RequestedName.Length != 0) grants.ValidateLocation(destination, row.Destination.Path.Append(row.RequestedName).ToArray());
    }
    internal IReadOnlyList<WindowsMutation> LocalRecords()
    { lock (gate) { Ready(); return cache.Records.Where(x => x.Peer.Length == 0).Select(x => { Authorize(x); return Reconcile(x); }).ToArray(); } }
    internal IReadOnlyList<string> ReferencedRoots()
    { lock (gate) { Ready(); return cache.Records.SelectMany(x => x.Source is null ? new[] { x.Destination.Grant } : new[] { x.Source.Grant, x.Destination.Grant }).Distinct().ToArray(); } }
    public FileResponse Query(string peer, FileRequest request)
    {
        lock (gate)
        {
            Ready(); var result = new FileResponse { RequestId = request.RequestId, MutationEpoch = cache.Epoch };
            if (request.Operation == FileOperation.MutationContext) return result;
            if (request.MutationEpoch != cache.Epoch) throw new StorageException(FileStatus.StaleReference, "File-operation history was reset. Refresh its context before continuing.");
            if (request.Operation == FileOperation.MutationStatus)
            {
                var row = cache.Records.SingleOrDefault(x => x.Id == request.MutationId && x.Peer == peer) ?? throw new StorageException(FileStatus.NotFound, "No current receipt exists. Refresh the files before deciding on another change.");
                Authorize(row); result.Mutation = Info(Reconcile(row)); return result;
            }
            if (request.Offset is < 0 or > 512) throw new StorageException(FileStatus.InvalidRequest, "Request a bounded receipt page.");
            var rows = cache.Records.Where(x => x.Peer == peer).OrderByDescending(x => MutationRules.CreatedAt(x.Id)).ThenBy(x => x.Id).ToArray();
            foreach (var row in rows.Skip((int)request.Offset).Take(32))
            { try { Authorize(row); result.Mutations.Add(Info(Reconcile(row))); } catch (StorageException) { } }
            result.EndOfFile = request.Offset + 32 >= rows.Length; return result;
        }
    }
    public WindowsMutation ResolveCreatedDirectory(string peer, FileRequest request)
    {
        lock (gate)
        {
            Ready();
            if (request.MutationEpoch != cache.Epoch) throw new StorageException(FileStatus.StaleReference, "File-operation history changed. Review a current context.");
            var row = cache.Records.SingleOrDefault(x => x.Id == request.MutationId && x.Peer == peer) ?? throw new StorageException(FileStatus.NotFound, "No matching created-folder receipt exists for this device.");
            Authorize(row); row = Reconcile(row);
            if (row.Operation != FileOperation.CreateDirectory || row.Phase != MutationPhase.ChangeSucceeded || row.ResultIdentity.Length == 0 || row.ResultName.Length == 0)
                throw new StorageException(FileStatus.Conflict, "This receipt has no confirmed created-folder identity. Review the destination before a deliberately new operation.");
            var grant = TargetGrant(row.Destination, peer, StoragePermission.Create); grants.ValidateLocation(grant, row.Destination.Path.Append(row.ResultName).ToArray());
            return row;
        }
    }
    public MutationInfo Execute(string peer, FileRequest request, Func<WindowsMutation> prepare, Func<WindowsMutationResult> apply)
    {
        lock (gate)
        {
            Ready(); if (request.MutationEpoch != cache.Epoch) throw new StorageException(FileStatus.StaleReference, "Refresh the operation context before making a change.");
            MutationRules.RequireCurrent(request.MutationId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var old = cache.Records.SingleOrDefault(x => x.Id == request.MutationId);
            if (old is not null)
            {
                if (old.Peer != peer || old.Fingerprint != MutationRules.Fingerprint(request)) throw new StorageException(FileStatus.Conflict, "This operation identifier already belongs to a different request.");
                Authorize(old); return Info(Reconcile(old));
            }
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var kept = cache.Records.Where(x => MutationRules.CreatedAt(x.Id) >= now - MutationRules.Lifetime).ToArray();
            if (kept.Length >= 512 || kept.Count(x => x.Peer == peer) >= 128) throw new StorageException(FileStatus.ResourceExhausted, "The receipt limit was reached. Wait for earlier changes to expire before making more.");
            var row = prepare(); Authorize(row); Save(cache with { Records = kept.Append(row).ToArray() });
            try { var applied = apply(); return Info(Put(row with { Phase = MutationPhase.ChangeSucceeded, Status = FileStatus.Ok, ResultName = applied.Name, ResultIdentity = applied.Identity, Message = "The change completed." })); }
            catch (StorageException ex) when (ex.NativeError is 80 or 183)
            { return Info(Put(row with { Phase = MutationPhase.ChangeFailed, Status = FileStatus.Conflict, Message = "An existing destination was kept. Refresh and choose another name." })); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { return Info(Reconcile(row)); }
        }
    }
    private WindowsMutation Reconcile(WindowsMutation row)
    {
        if (row.Phase != MutationPhase.ChangePending) return row;
        var review = row with { Phase = MutationPhase.ChangeReview, Status = FileStatus.Conflict,
            Message = "The change may have happened. Refresh its original and destination folders before deciding on a new operation." };
        if (row.Source is null) return Put(review);
        try
        {
            _ = TargetGrant(row.Source, row.Peer, row.Operation == FileOperation.Rename ? StoragePermission.Rename : StoragePermission.Move);
            var toGrant = TargetGrant(row.Destination, row.Peer, StoragePermission.Create);
            using var target = SecureWindowsDirectory.OpenPath(toGrant.CanonicalPath, toGrant.RootIdentity, row.Destination.Path, true);
            if (target.Info.Identity != row.Destination.Identity) return Put(review);
            using var destination = target.OpenChild(row.Name, row.Directory);
            if (destination.Info.Identity == row.Source.Identity)
                return Put(row with { Phase = MutationPhase.ChangeSucceeded, Status = FileStatus.Ok, ResultName = row.Name, ResultIdentity = row.Source.Identity, Message = "The completed change was reconciled by file identity." });
            return Put(review);
        }
        catch (StorageException ex) when (ex.Status is FileStatus.NotFound or FileStatus.Conflict or FileStatus.AccessDenied or FileStatus.StaleReference) { return Put(review); }
    }
    internal static MutationInfo Info(WindowsMutation row) => new() { Id = row.Id, Operation = row.Operation, Phase = row.Phase, Status = row.Status,
        Message = row.Message, ResultingName = row.ResultName, RequestedName = row.RequestedName.Length == 0 ? row.Name : row.RequestedName, CreatedAt = MutationRules.CreatedAt(row.Id) };
}
