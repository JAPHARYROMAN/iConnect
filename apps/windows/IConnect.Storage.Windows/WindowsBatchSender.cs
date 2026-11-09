using Google.Protobuf;
using IConnect.Core;
using IConnect.Protocol;
using System.Text.Json;

namespace IConnect.Storage.Windows;

public enum BatchSendingPhase { Ready, Admitting, Sending, Paused, Review, Stopping, Stopped, Completed }
public sealed record OutgoingBatchSend(string Id, string Source, UploadAuthority Authority, string SourceHash, int Items, long Bytes, int EncodedLength,
    BatchSendingPhase Phase = BatchSendingPhase.Ready, byte[]? Header = null, int Next = 0, bool Attempted = false, long Offset = 0,
    byte[]? Outcomes = null, string Destination = "", string Message = "Private batch ready. Choose an approved receiving folder.", byte[]? ReceiverOutcomes = null, bool EffectsStarted = false,
    bool Archived = false, bool Purging = false, int PurgeCursor = 0, int PurgeCount = 0, string Label = "",
    OutgoingFolderMoveSource? MoveSource = null, string SourceRecoveryId = "", bool SourceRecovered = false);
public sealed record OutgoingBatchItemResult(int Index, UploadBatchItemPhase Phase, string Name, string Message);
public sealed record OutgoingBatchPayload(int Index, string Name, long Size, long Offset, PayloadRateEstimate Estimate,
    long BatchRemaining, PayloadRateEstimate BatchEstimate);

/// <summary>Durable sender intents and item attempts; reviewed moves separately pin an owner-selected original.</summary>
public sealed partial class WindowsBatchSender : IAsyncDisposable
{
    private sealed record Envelope(int Schema, OutgoingBatchSend Row);
    private sealed record Item(int Schema, string Job, string HeaderHash, OutgoingBatchItemResult Result);
    private sealed record PayloadSample(int Index, string Name, long Size, long Offset, string Run, long BatchRemaining, long BatchAcknowledged)
    { public string Scope => $"{Run}/{Index}"; }

    // The validated manifest orders parents before children, including noncontiguous branches.
    private sealed class PendingPayload
    {
        private readonly long[] branchBytes;
        public long Remaining { get; private set; }

        public PendingPayload(UploadBatchManifest manifest, OutgoingBatchSend row)
        {
            branchBytes = new long[manifest.Items.Count];
            var excluded = new bool[branchBytes.Length];
            for (var index = 0; index < branchBytes.Length; index++)
            {
                var item = manifest.Items[index];
                excluded[index] = item.Error.Length != 0 || item.Parent >= 0 && excluded[item.Parent] ||
                    index < row.Next && item.IsDirectory && row.Outcomes![index] != (byte)UploadBatchItemPhase.BatchItemSucceeded;
                if (index >= row.Next && !excluded[index] && !item.IsDirectory)
                {
                    branchBytes[index] = item.Size;
                    Remaining += item.Size;
                }
            }
            for (var index = branchBytes.Length - 1; index >= 0; index--)
            {
                var parent = manifest.Items[index].Parent;
                if (parent >= 0) branchBytes[parent] += branchBytes[index];
            }
        }

        public void RemoveBranch(int index) => Remaining -= branchBytes[index];
    }
    private sealed class Work(CancellationTokenSource cancellation, bool folderMove = false)
    { public CancellationTokenSource Cancellation { get; } = cancellation; public bool FolderMove { get; } = folderMove; public Task Task { get; set; } = Task.CompletedTask; }
    private const int RecordLimit = 64 * 1024, ResultLimit = 8 * 1024, ResultsLimit = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All) };
    private readonly object gate = new();
    private readonly WindowsBatchSources sources;
    private readonly Func<UploadAuthority, bool> current;
    private readonly Func<bool> ownerReady;
    private readonly Func<UploadAuthority, bool> localOwner;
    private readonly Func<string>? recoveryOwner;
    private readonly string directory;
    private readonly SecureWindowsDirectory recoveryProfile, recoveryLease;
    private SecureWindowsDirectory root, lease;
    private StateRecovery recovery;
    private readonly Dictionary<string, OutgoingBatchSend> rows = [];
    private readonly Dictionary<string, string> versions = [];
    private readonly Dictionary<string, long> resultBytes = [];
    private readonly Dictionary<string, Work> work = [];
    private readonly Dictionary<string, PayloadSample> payload = [];
    private readonly PayloadRateTracker payloadRates = new();
    private readonly PayloadRateTracker batchPayloadRates = new();
    private readonly Func<long> payloadNow;
    private bool disposed;
    public event Action? Changed;
    public event Action? MetadataChanged;
    private void NotifyChanged(bool progressOnly = false)
    {
        Changed?.Invoke();
        if (!progressOnly) MetadataChanged?.Invoke();
    }
    public Action<WindowsActivityEvent>? Activity { get; set; }
    public IReadOnlyList<OutgoingBatchSend> All { get { lock (gate) return rows.Values.Reverse().Select(Clone).ToArray(); } }
    public IReadOnlyList<StateRecoveryIssue> RecoveryIssues { get { lock (gate) return recovery.Issues; } }
    public bool Active(string id) { lock (gate) return work.ContainsKey(id); }
    public bool FolderMoveActive(string id) { lock (gate) return work.TryGetValue(id, out var active) && active.FolderMove; }
    public long? ReadCheckpoint(string id, int index)
    {
        lock (gate) return rows.TryGetValue(id, out var row) && row.Next == index ? row.Offset : null;
    }
    public bool HasPayload { get { lock (gate) return payload.Count != 0; } }

    public OutgoingBatchPayload? ReadPayload(string id, long limit = 0)
    {
        lock (gate)
        {
            if (!payload.TryGetValue(id, out var sample) || !work.TryGetValue(id, out var active) || active.Cancellation.IsCancellationRequested ||
                !rows.TryGetValue(id, out var row) || row.Phase != BatchSendingPhase.Sending || row.Next != sample.Index || row.Offset != sample.Offset)
                return null;
            var now = payloadNow();
            var estimate = payloadRates.Read(id, sample.Scope, sample.Size, sample.Offset, true, now, limit);
            var batchEstimate = sample.BatchRemaining == 0 ? new PayloadRateEstimate(PayloadRateState.Received)
                : batchPayloadRates.Read(id, sample.Run, row.Bytes, sample.BatchAcknowledged, true, now, limit, sample.BatchRemaining);
            return new(sample.Index, sample.Name, sample.Size, sample.Offset, estimate, sample.BatchRemaining, batchEstimate);
        }
    }

    public void ClearPayloadRates() { lock (gate) { payloadRates.Clear(); batchPayloadRates.Clear(); } }

    private OutgoingBatchSend SavePayload(OutgoingBatchSend row, string run, UploadInfo receipt, long planned, long acknowledged, bool progressOnly = false)
    {
        lock (gate)
        {
            if (receipt.Phase == UploadPhase.UploadReceiving || receipt.Offset == receipt.Size &&
                receipt.Phase is UploadPhase.UploadVerifying or UploadPhase.UploadFinalizing)
            {
                var sample = new PayloadSample(row.Next, receipt.Name, receipt.Size, receipt.Offset, run, planned - receipt.Offset, acknowledged);
                var now = payloadNow();
                payloadRates.Observe(row.Id, sample.Scope, sample.Size, sample.Offset, true, now);
                batchPayloadRates.Observe(row.Id, run, row.Bytes, acknowledged, true, now);
                payload[row.Id] = sample;
            }
        }
        return Put(row with { Offset = receipt.Offset }, progressOnly);
    }

    private void EndPayload(string id)
    {
        lock (gate)
            if (payload.Remove(id, out var sample))
                payloadRates.Observe(id, sample.Scope, sample.Size, sample.Offset, false, payloadNow());
    }

    private static OutgoingBatchSend Clone(OutgoingBatchSend row) => row with { Header = row.Header?.ToArray(), Outcomes = row.Outcomes?.ToArray(), ReceiverOutcomes = row.ReceiverOutcomes?.ToArray() };
    private static bool Hex(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigitLower);
    private static string ResultName(string id, int index) => id + "." + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".json";
    public WindowsBatchSender(string profile, WindowsBatchSources sources, Func<UploadAuthority, bool> current, Func<bool>? ownerReady = null,
        Func<UploadAuthority, bool>? localOwner = null, Func<string>? recoveryOwner = null, Func<long>? payloadNow = null)
    {
        this.sources = sources; this.current = current; this.ownerReady = ownerReady ?? (() => true); this.localOwner = localOwner ?? current; this.recoveryOwner = recoveryOwner;
        this.payloadNow = payloadNow ?? (() => Environment.TickCount64);
        directory = Path.Combine(Path.GetFullPath(profile), StoreName);
        recoveryProfile = SecureWindowsDirectory.OpenRoot(Path.GetFullPath(profile));
        try { recoveryLease = OpenLease(recoveryProfile, ".outgoing-batch-sender.lock"); }
        catch { recoveryProfile.Dispose(); throw; }
        try { (root, lease) = OpenStore(); }
        catch { recoveryLease.Dispose(); recoveryProfile.Dispose(); throw; }
        recovery = new(directory);
        try
        {
            var paths = StateRecovery.Journals(directory, MaxRecords * (UploadBatchRules.MaxItems + 1));
            var metadataPaths = paths.ToHashSet(StringComparer.Ordinal);
            var parents = paths.Where(x => UploadBatchRules.CanonicalToken(Path.GetFileNameWithoutExtension(x))).ToArray();
            if (parents.Length > MaxRecords) throw new InvalidDataException("Too many outgoing batch jobs.");
            foreach (var path in parents)
            {
                var saved = ReadEnvelope(path);
                if (saved.Schema is not (1 or 2 or 3) || saved.Schema == 1 && (saved.Row.Archived || saved.Row.Purging || saved.Row.PurgeCursor != 0 || saved.Row.PurgeCount != 0) ||
                    saved.Schema < 3 && (saved.Row.MoveSource is not null || saved.Row.SourceRecoveryId is not "" || saved.Row.SourceRecovered))
                    throw new InvalidDataException("Unsupported outgoing batch job.");
                Validate(saved.Row); if (Path.GetFileName(path) != saved.Row.Id + ".json" || !rows.TryAdd(saved.Row.Id, saved.Row)) throw new InvalidDataException("Invalid outgoing batch job ownership.");
                using var file = root.OpenChild(Path.GetFileName(path)); versions[saved.Row.Id] = file.Info.Version;
            }
            foreach (var row in rows.Values.ToArray())
            {
                if (!row.Purging) for (var index = 0; index < row.Next; index++)
                    if (row.Archived) { if (!metadataPaths.Contains(Path.Combine(directory, ResultName(row.Id, index)))) throw new InvalidDataException("Archived results are missing."); }
                    else _ = ReadResult(row, index);
                if (!row.Archived && row.Phase is BatchSendingPhase.Admitting or BatchSendingPhase.Sending)
                    rows[row.Id] = row with { Phase = BatchSendingPhase.Paused, Message = "Sending was interrupted. Explicitly reconnect and check the original receiver receipts." };
            }
            ValidateSlotCounts();
            foreach (var path in paths.Except(parents))
            {
                var name = Path.GetFileNameWithoutExtension(path); var split = name.IndexOf('.');
                if (split < 0 || !rows.TryGetValue(name[..split], out var row) || !int.TryParse(name[(split + 1)..], out var index) || name[(split + 1)..] != index.ToString(System.Globalization.CultureInfo.InvariantCulture) || index < 0 || index > row.Next || index >= row.Items)
                    throw new InvalidDataException("Unowned outgoing batch item receipts were kept.");
                if (row.Purging && (index < row.PurgeCursor || index >= row.PurgeCount)) throw new InvalidDataException("Unexpected purging item record.");
                using var file = root.OpenChild(Path.GetFileName(path)); if (file.Info.Size > ResultLimit) throw new InvalidDataException("Oversized outgoing result.");
                if (!row.Archived) _ = ReadResult(row, index, committed: index < row.Next);
                resultBytes[row.Id] = resultBytes.GetValueOrDefault(row.Id) + file.Info.Size;
                if (resultBytes[row.Id] > ResultsLimit) throw new InvalidDataException("Outgoing batch results exceed their limit.");
            }
            foreach (var row in rows.Values.Where(x => x.Purging)) ValidatePurgeInventory(row);
            var names = root.EnumerateNames(default).Take(MaxRecords * (UploadBatchRules.MaxItems + 1) + 2).ToArray();
            var metadataNames = paths.Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
            if (names.Length > paths.Length + 1 || names.Any(x => x.IsDirectory || x.Name != "owner.lock" && !metadataNames.Contains(x.Name)))
                throw new InvalidDataException("Unexpected outgoing batch storage was preserved.");
            foreach (var row in rows.Values.Where(x => x.Header is { Length: > 0 } && this.localOwner(x.Authority)))
                if (sources.All.Any(x => x.Id == row.Source && x.Authority == row.Authority && x.Hash == row.SourceHash && x.Phase == BatchSourcePhase.Ready))
                    sources.NoteReceiverIntent(row.Source, row.Authority, row.SourceHash);
        }
        catch (Exception ex) when (StateRecovery.IsRecordFailure(ex))
        { rows.Clear(); recovery.Block("Outgoing batch intents need local review. Private snapshots and receiver copies were kept; new sends are blocked."); }
        try { RefreshRecoveryArchives(); }
        catch (Exception ex) when (StateRecovery.IsRecordFailure(ex)) { recovery.Block("Preserved sender stores need owner review. Their files were kept."); }
    }
    private static UploadBatchHeader Header(OutgoingBatchSend row) => UploadBatchHeader.Parser.ParseFrom(row.Header ?? throw new InvalidDataException("Missing remote intent."));
    private static void Validate(OutgoingBatchSend row)
    {
        if (!UploadBatchRules.CanonicalToken(row.Id) || !UploadBatchRules.CanonicalToken(row.Source) || !Hex(row.Authority.Owner) || !Hex(row.Authority.Peer) ||
            !UploadBatchRules.CanonicalToken(row.Authority.OwnerEpoch) || !UploadBatchRules.CanonicalToken(row.Authority.Pairing) || !Hex(row.SourceHash) ||
            row.Items is < 1 or > UploadBatchRules.MaxItems || row.Bytes is < 0 or > UploadRules.PeerQuota || row.EncodedLength is < 1 or > UploadBatchRules.MaxEncodedBytes || row.Next < 0 || row.Next > row.Items ||
            row.Offset < 0 || row.Offset > UploadRules.PeerQuota || !Enum.IsDefined(row.Phase) || row.Destination.Length > 255 || row.Destination.Any(char.IsControl) || row.Message.Length > 512 || row.Message.Any(char.IsControl) || row.Label.Length > 512 || row.Label.Any(char.IsControl) ||
            row.Outcomes is null || row.Outcomes.Length != row.Items || row.Outcomes.Any(x => x > 4) || row.Next == row.Items && row.Attempted)
            throw new InvalidDataException("Invalid outgoing batch job.");
        for (var index = 0; index < row.Next; index++) if (row.Outcomes[index] is 0 or 4) throw new InvalidDataException("A committed sender item has no final outcome.");
        for (var index = row.Next; index < row.Items; index++) if (row.Outcomes[index] != 0) throw new InvalidDataException("An uncommitted sender item claims an outcome.");
        if (row.Header is { Length: > 0 })
        {
            if (row.Header.Length > 1024) throw new InvalidDataException("Outgoing intent is too large.");
            var h = Header(row); UploadBatchRules.ValidateHeader(h);
            if (h.ItemCount != row.Items || h.TotalBytes != row.Bytes || h.EncodedLength != row.EncodedLength || Convert.ToHexStringLower(h.Sha256.Span) != row.SourceHash) throw new InvalidDataException("Outgoing intent changed its source.");
        }
        else if (row.Next != 0 || row.Attempted || row.Offset != 0 || row.EffectsStarted || row.Phase is BatchSendingPhase.Admitting or BatchSendingPhase.Sending or BatchSendingPhase.Stopping or BatchSendingPhase.Completed)
            throw new InvalidDataException("Outgoing job lost its remote intent.");
        if (row.Phase == BatchSendingPhase.Completed && (row.Next != row.Items || row.ReceiverOutcomes is null || !row.Outcomes.AsSpan().SequenceEqual(row.ReceiverOutcomes))) throw new InvalidDataException("Incomplete outgoing job claims completion.");
        if (row.ReceiverOutcomes is { Length: > 0 } && (row.ReceiverOutcomes.Length != row.Items || row.ReceiverOutcomes.Any(x => x > 4) || row.Phase is not (BatchSendingPhase.Completed or BatchSendingPhase.Stopped))) throw new InvalidDataException("Invalid retained receiver outcomes.");
        if (row.ReceiverOutcomes is { Length: > 0 } && !row.Outcomes.AsSpan(0, row.Next).SequenceEqual(row.ReceiverOutcomes.AsSpan(0, row.Next))) throw new InvalidDataException("Retained terminal outcomes changed committed items.");
        if (row.Phase == BatchSendingPhase.Stopped && row.EffectsStarted && row.ReceiverOutcomes is not { Length: > 0 }) throw new InvalidDataException("Stopped sender lost its receiver outcomes.");
        if (row.Purging ? !row.Archived || row.PurgeCount < row.Next || row.PurgeCount > Math.Min(row.Items, row.Next + 1) || row.PurgeCursor < 0 || row.PurgeCursor > row.PurgeCount : row.PurgeCursor != 0 || row.PurgeCount != 0)
            throw new InvalidDataException("Invalid outgoing history cleanup cursor.");
        ValidateFolderMove(row);
    }
    private void Ready()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (root.Handle.IsClosed || lease.Handle.IsClosed) throw new IOException("Sender storage could not be reopened. Restore local access and review recovery again.");
        recovery.RequireHealthy(); if (!ownerReady()) throw new InvalidOperationException("Review the current owner identity first.");
        try { foreach (var row in rows.Values) { using var file = root.OpenChild(row.Id + ".json"); if (file.Info.Version != versions[row.Id]) throw new IOException("Outgoing batch metadata changed."); } }
        catch (Exception ex) when (StateRecovery.IsRecordFailure(ex)) { recovery.Block("Outgoing batch ownership changed or is missing. Copies were kept for review."); throw new IOException("Outgoing batch ownership needs review.", ex); }
    }
    private void Authorize(OutgoingBatchSend row) { lock (gate) { Ready(); if (!current(row.Authority)) throw new InvalidOperationException("This batch belongs to an earlier identity or pairing."); } }
    private void Save<T>(string name, T value, int limit)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions); if (bytes.Length > limit) throw new IOException("Outgoing batch metadata storage is full.");
        try
        {
            using (var staged = root.CreateFile(name + ".new")) { staged.Write(0, bytes); staged.Flush(); }
            AtomicStateFile.Commit(Path.Combine(directory, name + ".new"), Path.Combine(directory, name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { recovery.Block("Outgoing batch progress could not be saved. Existing snapshots and receiver copies were kept."); throw; }
    }
    private OutgoingBatchSend Put(OutgoingBatchSend row, bool progressOnly = false)
    {
        BatchSendingPhase? previous;
        lock (gate) { previous = rows.GetValueOrDefault(row.Id)?.Phase; Validate(row); recovery.RequireHealthy(); Save(row.Id + ".json", new Envelope(3, row), RecordLimit); rows[row.Id] = Clone(row); using var file = root.OpenChild(row.Id + ".json"); versions[row.Id] = file.Info.Version; }
        if (previous != row.Phase)
        {
            var allCopied = row.ReceiverOutcomes is { Length: > 0 } && row.ReceiverOutcomes.All(x => x == 1);
            WindowsActivityOutcome? outcome = row.Phase switch { BatchSendingPhase.Ready => WindowsActivityOutcome.Accepted, BatchSendingPhase.Completed => allCopied ? WindowsActivityOutcome.Completed : WindowsActivityOutcome.CompletedWithIssues,
                BatchSendingPhase.Paused => WindowsActivityOutcome.Paused, BatchSendingPhase.Review => WindowsActivityOutcome.Review, BatchSendingPhase.Stopped => WindowsActivityOutcome.Canceled, _ => null };
            if (outcome is { } result) WindowsActivity.Publish(Activity, new(WindowsActivityKind.BatchSend, result, row.Authority.Peer, row.Phase == BatchSendingPhase.Completed && allCopied ? row.Bytes : -1));
        }
        NotifyChanged(progressOnly); return row;
    }
    public string Queue(string source, bool anotherCopy = false)
    {
        lock (gate)
        {
            Ready(); var snapshot = sources.All.Single(x => x.Id == source); if (snapshot.Phase != BatchSourcePhase.Ready || !current(snapshot.Authority)) throw new InvalidOperationException("Choose a ready private batch for the current pairing.");
            if (rows.Count >= MaxRecords || rows.Values.Count(x => !x.Archived) >= UploadBatchRules.MaxPlans || rows.Values.Count(x => !x.Archived && x.Authority.Peer == snapshot.Authority.Peer) >= UploadBatchRules.MaxPeerPlans || rows.Values.Any(x => !x.Archived && x.Source == source && x.Phase is not (BatchSendingPhase.Completed or BatchSendingPhase.Stopped)))
                throw new IOException("Outgoing batch job slots are full or this source already has a retained send.");
            if (!anotherCopy && (snapshot.MayHaveReceiverCopies || rows.Values.Any(x => x.Source == source && x.Header is { Length: > 0 }))) throw new IOException("Review the earlier receiver copies and explicitly choose another copy before queueing this snapshot again.");
            var manifest = sources.ReadManifest(source); var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(UploadBatchRules.Encode(manifest)));
            if (hash != snapshot.Hash) throw new InvalidDataException("The private source manifest changed.");
            var row = new OutgoingBatchSend(ProtocolRules.NewToken(), source, snapshot.Authority, snapshot.Hash, snapshot.Items, snapshot.Bytes, snapshot.EncodedLength, Outcomes: new byte[snapshot.Items], Label: snapshot.Label.Length == 0 ? manifest.Items.First(x => x.Parent < 0).Name : snapshot.Label); Put(row); return row.Id;
        }
    }
    private UploadBatchManifest Source(OutgoingBatchSend row)
    {
        Authorize(row); var source = sources.All.Single(x => x.Id == row.Source);
        if (source.Authority != row.Authority || source.Phase != BatchSourcePhase.Ready || source.Hash != row.SourceHash || source.Items != row.Items || source.Bytes != row.Bytes || source.EncodedLength != row.EncodedLength) throw new InvalidDataException("The original private source is no longer available.");
        return sources.ReadManifest(row.Source);
    }
    private Task Start(string id, FileEntry? destination, WindowsUploadSender.Request request, bool stop, CancellationToken ct)
    {
        lock (gate)
        {
            Ready(); var row = rows[id]; Authorize(row);
            if (row.Archived) throw new InvalidOperationException("Restore this saved history before checking or sending its original receiver plan.");
            if (work.ContainsKey(id) || work.Count >= 2) throw new InvalidOperationException("Two outgoing batch controls are already active, or this batch is busy.");
            if (!stop && row.Phase is BatchSendingPhase.Completed or BatchSendingPhase.Stopped or BatchSendingPhase.Stopping) throw new InvalidOperationException("This batch cannot resume. Check or stop its original receiver plan.");
            if (!stop && (row.Header is null || row.Header.Length == 0) && (destination is null || !destination.IsDirectory || (destination.Permissions & 12) != 12)) throw new InvalidOperationException("Choose an approved Write/Create receiving folder.");
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct); var active = new Work(cancellation); work[id] = active;
            active.Task = Task.Run(async () => { try { await Run(row, destination?.Clone(), request, stop, cancellation.Token); } finally { lock (gate) work.Remove(id); cancellation.Dispose(); NotifyChanged(); } });
            NotifyChanged(); return active.Task;
        }
    }
    public Task SendAsync(string id, FileEntry? destination, WindowsUploadSender.Request request, CancellationToken ct = default) => Start(id, destination, request, false, ct);
    public Task StopAsync(string id, WindowsUploadSender.Request request, CancellationToken ct = default) => Start(id, null, request, true, ct);
    private async Task Run(OutgoingBatchSend initial, FileEntry? destination, WindowsUploadSender.Request request, bool stop, CancellationToken ct)
    {
        var row = initial;
        var payloadRun = ProtocolRules.NewToken();
        try
        {
            async Task<FileResponse> Call(FileRequest value)
            {
                ct.ThrowIfCancellationRequested(); Authorize(row); value.RequestId = ProtocolRules.NewToken(); var response = await request(value, ct); StorageRules.ValidateResponse(response); Authorize(row);
                if (response.Status != FileStatus.Ok) throw new StorageException(response.Status, response.Message.Length == 0 ? "Check the existing receiver receipt before another copy." : response.Message);
                if (row.Header is { Length: > 0 } && value.Operation != FileOperation.UploadBatchContext)
                {
                    var parent = UploadBatchRules.CheckReceipt(Header(row), response);
                    if (row.EffectsStarted && !parent.EffectsStarted) throw new InvalidDataException("The receiver lost its retained effect barrier.");
                    if (parent.EffectsStarted && !row.EffectsStarted) row = Put(row with { EffectsStarted = true });
                }
                return response;
            }
            FileRequest Op(FileOperation operation, int? index = null) { var h = Header(row); var value = new FileRequest { Operation = operation, TransferId = h.Id, MutationEpoch = h.Epoch }; if (index is not null) value.BatchItemIndex = (uint)index.Value; return value; }
            UploadBatchReceipt Checked(FileResponse response) => UploadBatchRules.CheckReceipt(Header(row), response);
            if (stop && (row.Header is null || row.Header.Length == 0)) { Put(row with { Phase = BatchSendingPhase.Stopped, Message = "The unstarted local job stopped. Private snapshots and originals were kept." }); return; }
            if (stop)
            {
                row = Put(row with { Phase = BatchSendingPhase.Stopping, Message = "Stopping the original receiver plan. Published and uncertain copies are kept." });
                while (true)
                {
                    var receipt = Checked(await Call(Op(FileOperation.StopUploadBatch)));
                    if (UploadBatchRules.IsTerminal(receipt.Phase)) { AcceptTerminal(row, receipt); return; }
                    if (receipt.Phase != UploadBatchPlanPhase.UploadPlanStopping) throw new InvalidDataException("The receiver did not retain its stop barrier.");
                    await Task.Delay(250, ct);
                }
            }
            var manifest = Source(row); var encoded = UploadBatchRules.Encode(manifest); UploadBatchReceipt admitted;
            if (row.Header is null || row.Header.Length == 0)
            {
                var context = await Call(new() { Operation = FileOperation.UploadBatchContext });
                if (!UploadBatchRules.CanonicalToken(context.MutationEpoch) || !UploadBatchRules.CanonicalToken(destination!.GrantId)) throw new InvalidDataException("The receiver returned an invalid batch namespace or receiving grant.");
                var h = UploadBatchRules.Header(MutationRules.NewId(), context.MutationEpoch, destination.GrantId, manifest);
                row = Put(row with { Header = h.ToByteArray(), Destination = destination.Name, Phase = BatchSendingPhase.Admitting, Message = "The original receiver intent is saved before admission." });
                sources.NoteReceiverIntent(row.Source, row.Authority, row.SourceHash);
                var begin = Op(FileOperation.BeginUploadBatch); begin.DestinationId = destination.Id; begin.BatchHeader = h; admitted = Checked(await Call(begin));
            }
            else admitted = Checked(await Call(Op(FileOperation.UploadBatchStatus)));
            if (UploadBatchRules.IsTerminal(admitted.Phase)) { AcceptTerminal(row, admitted); return; }
            if (admitted.Phase == UploadBatchPlanPhase.UploadPlanStopping) { Put(row with { Phase = BatchSendingPhase.Stopping, Message = "The receiver is stopping this original plan. Explicitly finish stopping it." }); return; }
            if (admitted.Phase == UploadBatchPlanPhase.UploadPlanReceiving)
            {
                var offset = (int)admitted.ManifestOffset;
                if (offset != encoded.Length && offset % StorageRules.MaxReadRange != 0) throw new InvalidDataException("The receiver changed its manifest checkpoint.");
                while (offset < encoded.Length)
                {
                    var count = Math.Min(StorageRules.MaxReadRange, encoded.Length - offset); var value = Op(FileOperation.WriteUploadManifest); value.Offset = offset; value.Content = ByteString.CopyFrom(encoded, offset, count);
                    admitted = Checked(await Call(value)); if (admitted.Phase != UploadBatchPlanPhase.UploadPlanReceiving || admitted.ManifestOffset != offset + count) throw new InvalidDataException("The receiver acknowledged an inconsistent manifest range."); offset += count;
                }
                admitted = Checked(await Call(Op(FileOperation.SealUploadBatch)));
            }
            if (admitted.Phase != UploadBatchPlanPhase.UploadPlanReady) throw new InvalidDataException("The receiver did not accept the original complete manifest.");
            var pendingPayload = new PendingPayload(manifest, row);
            long acknowledgedPayload = 0;
            row = Put(row with { Phase = BatchSendingPhase.Sending, Message = "Sending the retained private batch through its original item bindings." });
            while (row.Next < row.Items)
            {
                ct.ThrowIfCancellationRequested(); Authorize(row); var index = row.Next; var item = manifest.Items[index];
                if (item.Error.Length != 0 || item.Parent >= 0 && row.Outcomes![item.Parent] != 1)
                {
                    var phase = item.Error.Length != 0 ? UploadBatchItemPhase.BatchItemFailed : UploadBatchItemPhase.BatchItemSkipped;
                    row = Commit(row, new(index, phase, "", item.Error.Length != 0 ? item.Error : "The parent branch was not copied.")); continue;
                }
                if (item.IsDirectory)
                {
                    var attempted = row.Attempted; if (!attempted) row = Put(row with { Attempted = true, Offset = 0 });
                    var reply = await Call(Op(attempted ? FileOperation.UploadBatchItemStatus : FileOperation.CreateBatchDirectory, index));
                    var result = UploadBatchRules.CheckItemReceipt(row.Authority.Owner, Header(row), index, true, reply);
                    if (result.Phase is UploadBatchItemPhase.BatchItemPending or UploadBatchItemPhase.BatchItemReview) throw new IOException("Directory creation needs review. Its original attempt will not be replayed.");
                    if (result.Phase != UploadBatchItemPhase.BatchItemSucceeded) pendingPayload.RemoveBranch(index);
                    row = Commit(row, new(index, result.Phase, result.ResultingName, result.Message)); continue;
                }
                using var source = sources.OpenFile(row.Source, index, ct);
                var descriptor = new UploadInfo { Id = UploadBatchRules.ChildId(row.Authority.Owner, Header(row), index, false), Name = item.Name, Size = item.Size, Sha256 = item.Sha256, Collision = manifest.Collision };
                var wasAttempted = row.Attempted; if (!wasAttempted) row = Put(row with { Attempted = true, Offset = 0 });
                var fileReply = await Call(Op(wasAttempted ? FileOperation.UploadBatchItemStatus : FileOperation.PrepareBatchFile, index));
                UploadInfo FileChecked(FileResponse response) => UploadBatchRules.CheckFileReceipt(row.Authority.Owner, Header(row), index, descriptor, response);
                var progress = FileChecked(fileReply);
                if (progress.Offset != item.Size && progress.Offset % StorageRules.MaxReadRange != 0) throw new InvalidDataException("The receiver changed its file checkpoint.");
                row = SavePayload(row, payloadRun, progress, pendingPayload.Remaining, acknowledgedPayload);
                while (progress.Phase == UploadPhase.UploadReceiving && progress.Offset < item.Size)
                {
                    var count = (int)Math.Min(StorageRules.MaxReadRange, item.Size - progress.Offset); var value = Op(FileOperation.WriteBatchFile, index); value.Offset = progress.Offset; value.Content = ByteString.CopyFrom(source.ReadRange(progress.Offset, count));
                    var expected = progress.Offset + count; progress = FileChecked(await Call(value));
                    if (progress.Offset != expected || progress.Phase != UploadPhase.UploadReceiving) throw new InvalidDataException("The receiver changed its acknowledged file range.");
                    acknowledgedPayload += count;
                    row = SavePayload(row, payloadRun, progress, pendingPayload.Remaining, acknowledgedPayload, progressOnly: progress.Offset < item.Size);
                }
                if (progress.Phase is UploadPhase.UploadReceiving or UploadPhase.UploadVerifying or UploadPhase.UploadFinalizing || progress.Phase == UploadPhase.UploadReview && progress.FinalName.Length != 0)
                    progress = FileChecked(await Call(Op(FileOperation.FinishBatchFile, index)));
                while (progress.Phase is UploadPhase.UploadVerifying or UploadPhase.UploadFinalizing)
                { await Task.Delay(250, ct); progress = FileChecked(await Call(Op(FileOperation.UploadBatchItemStatus, index))); }
                var outcome = progress.Phase switch { UploadPhase.UploadCompleted => UploadBatchItemPhase.BatchItemSucceeded, UploadPhase.UploadSkipped => UploadBatchItemPhase.BatchItemSkipped, UploadPhase.UploadCanceled => UploadBatchItemPhase.BatchItemFailed, _ => UploadBatchItemPhase.BatchItemReview };
                if (outcome == UploadBatchItemPhase.BatchItemReview) throw new IOException("The receiver kept an uncertain file copy. Review or stop this original plan before deliberately starting another.");
                EndPayload(row.Id);
                pendingPayload.RemoveBranch(index);
                row = Commit(row, new(index, outcome, progress.FinalName, progress.Message));
            }
            var finished = Checked(await Call(Op(FileOperation.FinishUploadBatch))); if (finished.Phase != UploadBatchPlanPhase.UploadPlanCompleted) throw new InvalidDataException("The receiver did not confirm batch completion."); AcceptTerminal(row, finished);
        }
        catch (Exception ex) when (StateRecovery.IsRecordFailure(ex) || ex is OperationCanceledException or TimeoutException)
        {
            try { Put(row with { Phase = row.Phase == BatchSendingPhase.Stopping ? BatchSendingPhase.Stopping : ex is OperationCanceledException ? BatchSendingPhase.Paused : BatchSendingPhase.Review,
                Message = row.Phase == BatchSendingPhase.Stopping ? "Stopping was interrupted. Reconnect to the same pairing and explicitly continue the saved stop request." : "Sending stopped. Check the original receiver receipts; earlier copies and private snapshots were kept." }); } catch (Exception save) when (StateRecovery.IsRecordFailure(save)) { }
        }
        finally
        {
            EndPayload(row.Id);
            batchPayloadRates.Observe(row.Id, payloadRun, row.Bytes, 0, false, payloadNow());
        }
    }
    private OutgoingBatchSend Commit(OutgoingBatchSend row, OutgoingBatchItemResult result)
    {
        lock (gate)
        {
            if (result.Index != row.Next || result.Phase is UploadBatchItemPhase.BatchItemPending or UploadBatchItemPhase.BatchItemReview) throw new InvalidDataException("Only a final current item can advance a sender.");
            var saved = new Item(1, row.Id, row.SourceHash, result); var bytes = JsonSerializer.SerializeToUtf8Bytes(saved, JsonOptions).Length; var name = ResultName(row.Id, result.Index);
            var previous = File.Exists(Path.Combine(directory, name)) ? new FileInfo(Path.Combine(directory, name)).Length : 0;
            if (previous != 0 && ReadResult(row, result.Index, false) != result) throw new InvalidDataException("The receiver changed an already retained item result.");
            if (resultBytes.GetValueOrDefault(row.Id) - previous + bytes > ResultsLimit) throw new IOException("Outgoing item results are full.");
            Save(name, saved, ResultLimit); resultBytes[row.Id] = resultBytes.GetValueOrDefault(row.Id) - previous + bytes;
            var outcomes = row.Outcomes!.ToArray(); outcomes[result.Index] = (byte)result.Phase; return Put(row with { Next = row.Next + 1, Attempted = false, Offset = 0, Outcomes = outcomes });
        }
    }
    private OutgoingBatchItemResult ReadResult(OutgoingBatchSend row, int index, bool committed = true)
    {
        var value = StateRecovery.Read<Item>(Path.Combine(directory, ResultName(row.Id, index)), ResultLimit); var result = value.Result;
        if (value.Schema != 1 || value.Job != row.Id || value.HeaderHash != row.SourceHash || result.Index != index || !Enum.IsDefined(result.Phase) || result.Phase is UploadBatchItemPhase.BatchItemPending or UploadBatchItemPhase.BatchItemReview ||
            result.Name.Length > 255 || result.Message.Length > 512 || result.Message.Any(char.IsControl) || committed && row.Outcomes![index] != (byte)result.Phase) throw new InvalidDataException("The retained outgoing item result changed.");
        if (result.Name.Length != 0) StorageRules.ValidateName(result.Name); return result;
    }
    public OutgoingBatchItemResult ItemResult(string id, int index) { lock (gate) { Ready(); var row = rows[id]; if (row.Purging || index < 0 || index >= row.Next) throw new ArgumentOutOfRangeException(nameof(index)); return ReadResult(row, index); } }
    private void AcceptTerminal(OutgoingBatchSend row, UploadBatchReceipt receipt)
    {
        if (receipt.Phase == UploadBatchPlanPhase.UploadPlanCompleted && row.Next != row.Items) throw new InvalidDataException("The receiver claims completion before this sender retained its item results.");
        if (receipt.Phase == UploadBatchPlanPhase.UploadPlanCanceled && row.EffectsStarted) throw new InvalidDataException("The receiver canceled a plan with a retained effect barrier.");
        if (receipt.Phase != UploadBatchPlanPhase.UploadPlanCanceled)
            for (var index = 0; index < row.Next; index++) if (receipt.ItemOutcomes[index] != row.Outcomes![index]) throw new InvalidDataException("The receiver changed a retained terminal item outcome.");
        Put(row with { Phase = receipt.Phase == UploadBatchPlanPhase.UploadPlanCompleted ? BatchSendingPhase.Completed : BatchSendingPhase.Stopped,
            ReceiverOutcomes = receipt.ItemOutcomes.ToByteArray(),
            Message = receipt.Phase == UploadBatchPlanPhase.UploadPlanCompleted ? "Receiver confirmed all final item outcomes. Originals and private snapshots were kept." : "The original receiver plan stopped. Published and uncertain copies were kept." });
    }
    public void Pause(string id) { lock (gate) if (work.TryGetValue(id, out var value)) value.Cancellation.Cancel(); }
    public async Task PauseAndWaitAsync(string id)
    {
        Task task = Task.CompletedTask;
        lock (gate) if (work.TryGetValue(id, out var value)) { value.Cancellation.Cancel(); task = value.Task; }
        await task;
    }
    public void PauseAll() { lock (gate) foreach (var value in work.Values.ToArray()) value.Cancellation.Cancel(); }
    public Task WhenIdleAsync() { lock (gate) return Task.WhenAll(work.Values.Select(x => x.Task)); }
    public async ValueTask DisposeAsync()
    {
        Task stopping; lock (gate) { if (disposed) return; disposed = true; PauseAll(); stopping = WhenIdleAsync(); } await stopping;
        lock (gate) { resetReviews.Clear(); archiveReviews.Clear(); archiveDeletions.Clear(); lease.Dispose(); root.Dispose(); recoveryLease.Dispose(); recoveryProfile.Dispose(); }
    }
}
