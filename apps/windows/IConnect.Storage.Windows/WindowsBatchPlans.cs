using Google.Protobuf;
using IConnect.Core;
using IConnect.Protocol;
using System.Text.Json;

namespace IConnect.Storage.Windows;

public sealed record IncomingBatchPlan(string Id, string Peer, long Bytes, uint Items, UploadBatchPlanPhase Phase, bool EffectsStarted = false, bool Retiring = false, bool Retired = false, uint RetirementCursor = 0);

/// <summary>Accepted copy plans and identity-bound directory/file children.</summary>
public sealed partial class WindowsBatchPlans
{
    private sealed record Context(int Schema, string Owner, string Epoch);
    private sealed record Row(string Owner, string Peer, string Pairing, UploadDestination Target, byte[] Header, UploadBatchPlanPhase Phase, bool EffectsStarted = false, byte[]? Outcomes = null, uint StopCursor = 0,
        bool Retiring = false, bool Retired = false, uint RetirementCursor = 0, byte[]? RetirementBits = null);
    private sealed record Envelope(int Schema, Row Row, byte[] Manifest);
    private sealed record AssemblyWork(string Scope, UploadBatchAssembly Assembly);
    private readonly StorageGrantStore grants;
    private readonly string directory;
    private readonly object gate = new();
    private StateRecovery recovery;
    private Context context;
    private readonly Dictionary<string, Row> records = [];
    private readonly Dictionary<string, AssemblyWork> assemblies = [];
    private readonly WindowsBatchUploadJournals fileJournals;
    private const int RecordLimit = 8 * 1024 * 1024;
    private const string BackupPrefix = "batch-plan-backup-";
    public event Action? Changed;
    internal WindowsBatchPlans(StorageGrantStore grants, string profile)
    {
        this.grants = grants; directory = Path.Combine(profile, "incoming-batch-plans"); Directory.CreateDirectory(directory);
        recovery = new(directory); context = new(1, grants.OwnerScope, ProtocolRules.NewToken()); fileJournals = new(directory, () => Changed?.Invoke());
        try
        {
            var paths = StateRecovery.Journals(directory, MaxStoredPlanFiles);
            if (paths.Length > MaxStoredPlanFiles) throw new InvalidDataException("Too many batch metadata files.");
            fileJournals.Load(paths);
            if (paths.Length == 0) Save("context.json", context);
            else
            {
                context = StateRecovery.Read<Context>(Path.Combine(directory, "context.json"), 4096);
                if (context.Schema != 1 || context.Owner != grants.OwnerScope || !UploadBatchRules.CanonicalToken(context.Epoch)) throw new InvalidDataException("Batch history belongs to earlier owner authority.");
                var rows = paths.Where(x => Path.GetFileName(x) != "context.json" && !Path.GetFileNameWithoutExtension(x).Contains('.')).ToArray();
                if (rows.Length > MaxDetailedPlans + MaxRetiredPlans) throw new InvalidDataException("Too many batch plan records.");
                foreach (var path in rows)
                {
                    var saved = StateRecovery.Read<Envelope>(path, RecordLimit); var header = Validate(saved);
                    if (Path.GetFileName(path) != header.Id + ".json" || !records.TryAdd(header.Id, saved.Row)) throw new InvalidDataException("Invalid batch receipt filename.");
                }
                if (records.Values.Count(x => !x.Retired) > MaxDetailedPlans || records.Values.Count(x => x.Retired) > MaxRetiredPlans || records.Values.Count(x => !UploadBatchRules.IsTerminal(x.Phase)) > UploadBatchRules.MaxPlans ||
                    records.Values.Where(x => !UploadBatchRules.IsTerminal(x.Phase)).GroupBy(x => x.Peer).Any(x => x.Count() > UploadBatchRules.MaxPeerPlans) ||
                    records.Values.Where(x => !UploadBatchRules.IsTerminal(x.Phase)).GroupBy(x => x.Peer).Any(x => x.Sum(y => Header(y).TotalBytes) > UploadRules.PeerQuota) ||
                    records.Values.Where(x => !UploadBatchRules.IsTerminal(x.Phase)).Sum(x => Header(x).TotalBytes) > UploadRules.TotalQuota)
                    throw new InvalidDataException("Invalid batch reservation totals.");
                LoadDirectoryReceipts(paths);
                foreach (var row in records.Values.Where(x => x.Retiring || x.Retired)) ValidateRetirementInventory(row);
                foreach (var row in records.Values.Where(x => !x.Retiring && !x.Retired && x.Phase is UploadBatchPlanPhase.UploadPlanStopped or UploadBatchPlanPhase.UploadPlanCompleted))
                {
                    var saved = Inventory(row); var outcomes = Outcomes(row, saved.Manifest, saved.Children, row.Phase == UploadBatchPlanPhase.UploadPlanCompleted, false);
                    if (!outcomes.AsSpan().SequenceEqual(row.Outcomes ?? [])) throw new InvalidDataException("Terminal batch outcomes do not match their retained item receipts.");
                }
            }
        }
        catch (Exception ex) when (StateRecovery.IsRecordFailure(ex))
        { records.Clear(); recovery.Block("Batch-plan metadata needs local review. Incoming writes are blocked; destination files are kept."); }
        grants.Changed += ClearFilePayloadRates;
        grants.Changed += InvalidatePublishedBatchChecks;
    }
    private static UploadBatchHeader Header(Row row) => UploadBatchHeader.Parser.ParseFrom(row.Header);
    private UploadBatchHeader Validate(Envelope saved)
    {
        if (saved.Schema is not (1 or 2 or 3) || saved.Row is null || saved.Manifest is null || saved.Schema < 3 && (saved.Row.Retiring || saved.Row.Retired || saved.Row.RetirementCursor != 0 || saved.Row.RetirementBits is { Length: > 0 }) || saved.Schema == 1 && saved.Row.Phase is not (UploadBatchPlanPhase.UploadPlanReceiving or UploadBatchPlanPhase.UploadPlanReady or UploadBatchPlanPhase.UploadPlanCanceled)) throw new InvalidDataException("Invalid batch receipt.");
        var row = saved.Row; var header = Header(row); UploadBatchRules.ValidateHeader(header);
        if (row.Owner != context.Owner || header.Epoch != context.Epoch || row.Peer.Length != 64 || row.Peer.Any(c => !char.IsAsciiHexDigitLower(c)) ||
            row.Pairing != "local" && !UploadBatchRules.CanonicalToken(row.Pairing) || row.Pairing == "local" && row.Owner != "local" ||
            !Enum.IsDefined(row.Phase) || row.EffectsStarted && !UploadBatchRules.HasManifest(row.Phase) || row.Target is null || row.Target.Grant != header.GrantId || !SecureWindowsDirectory.IsLocalVolumePath(row.Target.CanonicalRoot) ||
            row.Target.RootIdentity.Length is < 1 or > 256 || row.Target.Identity.Length is < 1 or > 256 || row.Target.Path is null || row.Target.Path.Length > 64)
            throw new InvalidDataException("Invalid batch authority binding.");
        foreach (var part in row.Target.Path) StorageRules.ValidateName(part);
        UploadBatchRules.ValidateOutcomes(row.Phase, header.ItemCount, row.Outcomes ?? []);
        if (row.Phase is UploadBatchPlanPhase.UploadPlanStopping or UploadBatchPlanPhase.UploadPlanStopped && !row.EffectsStarted) throw new InvalidDataException("An unstarted batch must use its canceled receipt.");
        if (row.StopCursor > header.ItemCount || row.Phase == UploadBatchPlanPhase.UploadPlanStopped && row.StopCursor != header.ItemCount ||
            row.Phase is not (UploadBatchPlanPhase.UploadPlanStopping or UploadBatchPlanPhase.UploadPlanStopped) && row.StopCursor != 0) throw new InvalidDataException("Invalid batch cleanup cursor.");
        ValidateRetirementRow(row);
        if (row.Retired) { if (saved.Manifest.Length != 0) throw new InvalidDataException("A compact receipt still contains a manifest."); }
        else if (UploadBatchRules.HasManifest(row.Phase)) UploadBatchRules.Decode(header, saved.Manifest);
        else if (saved.Manifest.Length != 0) throw new InvalidDataException("An unfinished plan contains an uncommitted manifest.");
        return header;
    }
    private void Ready()
    {
        fileJournals.RequireHealthy();
        recovery.RequireHealthy();
        if (context.Owner != grants.OwnerScope) throw new StorageException(FileStatus.AccessDenied, "Batch history belongs to an earlier Windows owner generation.");
    }
    private void Save<T>(string name, T value, int maximum = RecordLimit)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > maximum) throw new StorageException(FileStatus.ResourceExhausted, "Batch receipt storage is full.");
        try
        {
            using var root = SecureWindowsDirectory.OpenRoot(directory);
            using (var staged = Stage(root, name + ".new")) { staged.Write(0, bytes); System.IO.RandomAccess.SetLength(staged.Handle, bytes.Length); staged.Flush(); }
            AtomicStateFile.Commit(Path.Combine(directory, name + ".new"), Path.Combine(directory, name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { recovery.Block("Batch progress could not be committed. Preserve the metadata and review this queue locally."); Changed?.Invoke(); throw; }
    }
    private static SecureWindowsDirectory Stage(SecureWindowsDirectory root, string name)
    { try { return root.CreateFile(name); } catch (StorageException ex) when (ex.NativeError is 80 or 183) { return root.OpenWritableMetadataChild(name); } }
    private void Put(Row row, byte[]? manifest = null)
    {
        var saved = new Envelope(3, row, manifest ?? []); var header = Validate(saved); var before = records.GetValueOrDefault(header.Id)?.Phase;
        Save(header.Id + ".json", saved); records[header.Id] = row; Changed?.Invoke();
        if (before != row.Phase && UploadBatchRules.IsTerminal(row.Phase)) grants.RecordActivity(new(WindowsActivityKind.BatchReceive,
            row.Phase == UploadBatchPlanPhase.UploadPlanCompleted ? row.Outcomes is { Length: > 0 } && row.Outcomes.All(x => x == 1) ? WindowsActivityOutcome.Completed : WindowsActivityOutcome.CompletedWithIssues : WindowsActivityOutcome.Canceled, row.Peer,
            row.Phase == UploadBatchPlanPhase.UploadPlanCompleted && row.Outcomes is { Length: > 0 } && row.Outcomes.All(x => x == 1) ? header.TotalBytes : -1));
    }
    private void Authorize(Row row, string peer, string pairing)
    {
        Ready(); if (row.Peer != peer || row.Pairing != pairing) throw new StorageException(FileStatus.AccessDenied, "This batch belongs to another pairing generation.");
        var grant = grants.Get(row.Target.Grant, peer); StorageRules.Require(grant.Permissions, StoragePermission.Write | StoragePermission.Create);
        if (grant.CanonicalPath != row.Target.CanonicalRoot || grant.RootIdentity != row.Target.RootIdentity) throw new StorageException(FileStatus.AccessDenied, "This batch approval changed.");
        grants.ValidateAccess(grant, row.Target.Path);
        using var parent = SecureWindowsDirectory.OpenPath(grant.CanonicalPath, grant.RootIdentity, row.Target.Path, true);
        if (parent.Info.Identity != row.Target.Identity) throw new StorageException(FileStatus.StaleReference, "The batch destination folder was replaced.");
    }
    internal long ReservedBytes(string? peer) { lock (gate) { Ready(); return records.Values.Where(x => !UploadBatchRules.IsTerminal(x.Phase) && (peer is null || x.Peer == peer)).Sum(x => Header(x).TotalBytes); } }
    public IReadOnlyList<IncomingBatchPlan> All { get { lock (gate) return records.Values.Select(x => { var h = Header(x); return new IncomingBatchPlan(h.Id, x.Peer, h.TotalBytes, h.ItemCount, x.Phase, x.EffectsStarted, x.Retiring, x.Retired, x.RetirementCursor); }).ToArray(); } }
    public IReadOnlyList<StateRecoveryIssue> RecoveryIssues { get { lock (gate) return recovery.Issues.Concat(fileJournals.Issues).ToArray(); } }
    internal void OwnerScopeChanged() { lock (gate) if (context.Owner != grants.OwnerScope) recovery.Block("Batch plans belong to an earlier Windows owner generation. Preserve or archive their history locally."); }
    private string[] Backups() => Directory.EnumerateDirectories(Path.GetDirectoryName(directory)!, BackupPrefix + "*").Take(33).ToArray();
    public int PreservedRecords { get { lock (gate) return Backups().Length; } }
    public void ArchiveHistory() => grants.WithAuthorization(() =>
    {
        lock (gate)
        {
            if (publishedBatchWork.Count != 0 || fileReceivers.Values.Any(x => !x.Uploads.IsIdle)) throw new IOException("Pause connections and wait for batch file verification before archiving history.");
            if (Backups().Length >= 32) throw new IOException("Remove reviewed batch-plan backups before creating another archive.");
            foreach (var id in assemblies.Keys.ToArray()) EndAssembly(id);
            try
            {
                using var profile = SecureWindowsDirectory.OpenRoot(Path.GetDirectoryName(directory)!);
                if (Directory.Exists(directory)) { using var old = profile.OpenManagementChild("incoming-batch-plans", true, mutation: true); old.RenameTo(profile, BackupPrefix + ProtocolRules.NewToken()); }
                else if (Backups().Length == 0) throw new IOException("No batch history or preserved archive could be identified.");
                using var fresh = profile.CreateDirectory("incoming-batch-plans");
                records.Clear(); directoryBytes.Clear(); manifestCache.Clear(); fileReceivers.Clear(); fileJournals.Reset(); recovery = new(directory); context = new(1, grants.OwnerScope, ProtocolRules.NewToken()); Save("context.json", context); Changed?.Invoke();
            }
            catch { recovery.Block("Batch history reset was interrupted. Keep the private archives and retry local recovery."); Changed?.Invoke(); throw; }
        }
        return true;
    });
    public void ClearPreservedRecords() => grants.WithAuthorization(() =>
    {
        lock (gate)
        {
            var backups = Backups(); if (backups.Length > 32) throw new IOException("Unexpected batch-plan backup count. Review the private profile.");
            using var profile = SecureWindowsDirectory.OpenRoot(Path.GetDirectoryName(directory)!);
            foreach (var path in backups)
            {
                var name = Path.GetFileName(path); if (!UploadBatchRules.CanonicalToken(name[BackupPrefix.Length..])) throw new IOException("Unexpected batch-plan backup name.");
                using var backup = profile.OpenManagementChild(name, true, mutation: true); var files = backup.EnumerateNames(default).Take(MaxStoredPlanFiles * 2 + 1).ToArray();
                if (files.Length > MaxStoredPlanFiles * 2 || files.Any(x => x.IsDirectory || !MetadataName(x.Name))) throw new IOException("Unexpected backup contents were kept.");
                foreach (var file in files) { using var owned = backup.OpenMutableChild(file.Name); owned.Delete(); }
                backup.Delete();
            }
        }
        return true;
    });
    private static bool MetadataName(string name)
    {
        if (name.EndsWith(".new", StringComparison.Ordinal)) name = name[..^4];
        return name == "context.json" || name.EndsWith(".json", StringComparison.Ordinal) && UploadBatchRules.CanonicalToken(name[..^5]) || IsAttemptsName(name) || TryChildName(name, out _, out _) || WindowsBatchUploadJournals.ParseName(name, out _, out _);
    }
    private UploadBatchReceipt Receipt(Row row, string scope)
    {
        var h = Header(row); var offset = UploadBatchRules.HasManifest(row.Phase) ? h.EncodedLength :
            assemblies.TryGetValue(h.Id, out var current) && current.Scope == scope ? (uint)current.Assembly.Offset : 0;
        return new() { Header = h, Phase = row.Phase, ManifestOffset = offset, EffectsStarted = row.EffectsStarted, ItemOutcomes = Google.Protobuf.ByteString.CopyFrom(row.Outcomes ?? []), Message = row.Phase switch {
            _ when row.Retired || row.Retiring => "Final parent outcomes are retained. The receiver owner is removing detailed item history; no new copy can be started from this intent.",
            UploadBatchPlanPhase.UploadPlanStopping => "The batch is stopped from starting new work. Repeat Stop while retained verification or private cleanup finishes.",
            UploadBatchPlanPhase.UploadPlanStopped => "Stopped. Identified private partials were removed; published or uncertain destination copies were kept.",
            UploadBatchPlanPhase.UploadPlanCompleted => "All planned items have durable final outcomes. Published destination copies were kept.",
            UploadBatchPlanPhase.UploadPlanReady when row.EffectsStarted => "Batch child effects have started. Check retained item receipts before resuming.",
            UploadBatchPlanPhase.UploadPlanReady => "The private batch plan is durably accepted. No destination items have been created.",
            UploadBatchPlanPhase.UploadPlanCanceled => "The unstarted batch plan was canceled. Destination files were kept.",
            _ => "Receiving the manifest. Check status after reconnect; assembly offsets are not durable." } };
    }
    internal FileResponse Execute(string peer, string pairing, string scope, FileRequest request, Func<UploadDestination> target, CancellationToken ct)
    {
        lock (gate)
        {
            Ready(); ct.ThrowIfCancellationRequested();
            if (peer.Length != 64 || peer.Any(c => !char.IsAsciiHexDigitLower(c)) || pairing != "local" && !UploadBatchRules.CanonicalToken(pairing) || pairing == "local" && context.Owner != "local")
                throw new StorageException(FileStatus.AccessDenied, "Batch admission requires a current pairing generation.");
            var result = new FileResponse { RequestId = request.RequestId, MutationEpoch = context.Epoch };
            if (request.Operation == FileOperation.UploadBatchContext) return result;
            if (request.MutationEpoch != context.Epoch) throw new StorageException(FileStatus.StaleReference, "Batch history changed. Review the original intent before another batch.");
            if (!UploadBatchRules.CanonicalToken(request.TransferId)) throw new InvalidDataException("Invalid batch identifier.");
            Row row;
            if (request.Operation == FileOperation.BeginUploadBatch)
            {
                var header = request.BatchHeader ?? throw new InvalidDataException("A batch intent is required."); UploadBatchRules.ValidateHeader(header);
                if (header.Id != request.TransferId || header.Epoch != context.Epoch) throw new InvalidDataException("The batch intent changed its namespace.");
                var destination = target(); if (destination.Grant != header.GrantId) throw new InvalidDataException("The batch grant changed.");
                if (destination.Path.Length > 64) throw new StorageException(FileStatus.ResourceExhausted, "Choose a batch destination within 64 components of its approved root.");
                row = new(context.Owner, peer, pairing, destination, header.ToByteArray(), UploadBatchPlanPhase.UploadPlanReceiving); Authorize(row, peer, pairing);
                if (records.TryGetValue(header.Id, out var previous))
                {
                    Authorize(previous, peer, pairing);
                    if (!Header(previous).Equals(header) || !SameTarget(previous.Target, destination)) throw new StorageException(FileStatus.Conflict, "This batch identifier already belongs to a different plan or destination.");
                    row = previous;
                }
                else
                {
                    MutationRules.RequireCurrent(header.Id, grants.Clock.GetUtcNow().ToUnixTimeMilliseconds());
                    if (records.Values.Count(x => !x.Retired) >= MaxDetailedPlans || records.Values.Count(x => !UploadBatchRules.IsTerminal(x.Phase)) >= UploadBatchRules.MaxPlans || records.Values.Count(x => x.Peer == peer && !UploadBatchRules.IsTerminal(x.Phase)) >= UploadBatchRules.MaxPeerPlans ||
                        header.TotalBytes > UploadRules.PeerQuota - ReservedBytes(peer) - grants.Uploads.ReservedBytes(peer) ||
                        header.TotalBytes > UploadRules.TotalQuota - ReservedBytes(null) - grants.Uploads.ReservedBytes(null))
                        throw new StorageException(FileStatus.ResourceExhausted, "Incoming batch slots or shared incoming-byte reservations are full.");
                    Put(row);
                }
            }
            else
            {
                row = records.GetValueOrDefault(request.TransferId) ?? throw new StorageException(FileStatus.NotFound, "No retained batch receipt exists. A missing receipt cannot resume another copy.");
                if (row.Peer != peer) throw new StorageException(FileStatus.NotFound, "No retained batch receipt exists. A missing receipt cannot resume another copy.");
                Authorize(row, peer, pairing);
                if ((row.Retiring || row.Retired) && request.Operation is not (FileOperation.UploadBatchStatus or FileOperation.FinishUploadBatch or FileOperation.StopUploadBatch))
                    throw new StorageException(FileStatus.NotFound, "The receiver owner removed this plan's detailed item history. Its final parent receipt remains; no new copy is created.");
                if (request.Operation == FileOperation.VerifyPublishedBatch)
                    VerifyPublishedBatch(row, scope, request, result, ct);
                else if (request.Operation == FileOperation.CancelPublishedBatchCheck)
                    CancelPublishedBatchCheck(row, scope, request, result);
                else if (request.Operation is FileOperation.FinishUploadBatch or FileOperation.StopUploadBatch)
                    row = EndPlan(row, request, scope, ct);
                else if (UploadBatchRules.IsTerminal(row.Phase) && request.Operation == FileOperation.UploadBatchItemStatus)
                    TerminalItem(row, request, result);
                else if (request.Operation is >= FileOperation.PrepareBatchFile and <= FileOperation.CancelBatchFile || request.Operation == FileOperation.UploadBatchItemStatus && IsFileItem(row, request))
                {
                    var file = HandleBatchFile(row, peer, pairing, scope, request, ct); result.Upload = file.Upload; result.BatchItem = file.BatchItem; result.Status = file.Status; result.Message = file.Message; row = records[request.TransferId];
                }
                else if (request.Operation is FileOperation.CreateBatchDirectory or FileOperation.UploadBatchItemStatus)
                {
                    var child = DirectoryOperation(row, peer, pairing, request, ct); result.BatchItem = child; row = records[request.TransferId];
                }
                else if (request.Operation == FileOperation.WriteUploadManifest)
                {
                    if (row.Phase != UploadBatchPlanPhase.UploadPlanReceiving) throw new StorageException(FileStatus.Conflict, "This plan no longer accepts manifest chunks.");
                    if (!assemblies.TryGetValue(request.TransferId, out var current)) assemblies[request.TransferId] = current = new(scope, new(Header(row)));
                    if (current.Scope != scope) throw new StorageException(FileStatus.Conflict, "Close the earlier manifest session before resuming this intent.");
                    if (request.Offset is < 0 or > int.MaxValue) throw new InvalidDataException("Invalid manifest offset.");
                    current.Assembly.Add((int)request.Offset, request.Content.Span);
                }
                else if (request.Operation == FileOperation.SealUploadBatch && row.Phase != UploadBatchPlanPhase.UploadPlanReady)
                {
                    if (row.Phase != UploadBatchPlanPhase.UploadPlanReceiving || !assemblies.TryGetValue(request.TransferId, out var current) || current.Scope != scope)
                        throw new StorageException(FileStatus.Conflict, "Upload the original complete manifest before accepting this plan.");
                    try
                    {
                        var manifest = current.Assembly.Complete(); var grant = grants.Get(row.Target.Grant, peer); var paths = new string[manifest.Items.Count][];
                        for (var i = 0; i < paths.Length; i++) { ct.ThrowIfCancellationRequested(); var item = manifest.Items[i]; paths[i] = item.Parent < 0 ? [item.Name] : [.. paths[item.Parent], item.Name];
                            if (item.Error.Length == 0) grants.ValidateAccess(grant, [.. row.Target.Path, .. paths[i]]); }
                        Authorize(row, peer, pairing); row = row with { Phase = UploadBatchPlanPhase.UploadPlanReady }; Put(row, UploadBatchRules.Encode(manifest));
                    }
                    finally { EndAssembly(request.TransferId); }
                }
                else if (request.Operation == FileOperation.CancelUploadBatch)
                { if (UploadBatchRules.IsTerminal(row.Phase)) { result.Batch = Receipt(row, scope); return result; }
                    if (row.EffectsStarted || row.Phase == UploadBatchPlanPhase.UploadPlanStopping) throw new StorageException(FileStatus.Conflict, "Use Stop for a started batch. Published items are kept.");
                    row = row with { Phase = UploadBatchPlanPhase.UploadPlanCanceled }; Put(row); EndAssembly(request.TransferId); }
                else if (request.Operation is not (FileOperation.UploadBatchStatus or FileOperation.SealUploadBatch)) throw new InvalidDataException("Unsupported batch admission request.");
            }
            result.Batch = Receipt(row, scope); return result;
        }
    }
    private static bool SameTarget(UploadDestination a, UploadDestination b) => a.Grant == b.Grant && a.CanonicalRoot == b.CanonicalRoot && a.RootIdentity == b.RootIdentity && a.Identity == b.Identity && a.Path.SequenceEqual(b.Path);
    private void EndAssembly(string id) { if (assemblies.Remove(id, out var work)) work.Assembly.Dispose(); }
    internal void EndScope(string scope)
    {
        WindowsUploads[] stopping;
        lock (gate)
        {
            CancelPublishedBatchChecks(scope);
            foreach (var id in assemblies.Where(x => x.Value.Scope == scope).Select(x => x.Key).ToArray()) EndAssembly(id);
            var receivers = fileReceivers.Values;
            foreach (var receiver in receivers) receiver.Uploads.ClearPayloadScope(scope);
            stopping = receivers.Where(x => x.WorkScope == scope).Select(x => x.Uploads).ToArray();
        }
        foreach (var uploads in stopping) uploads.PauseVerification();
    }
}
