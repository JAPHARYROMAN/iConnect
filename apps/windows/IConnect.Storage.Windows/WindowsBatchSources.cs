using Google.Protobuf;
using IConnect.Core;
using IConnect.Protocol;
using System.Security.Cryptography;
using System.Text.Json;

namespace IConnect.Storage.Windows;

public enum BatchSourcePhase { Preparing, Ready, Failed }
public sealed record OutgoingBatchSource(string Id, UploadAuthority Authority, BatchSourcePhase Phase, int Items = 0, long Bytes = 0,
    string Hash = "", int EncodedLength = 0, string Message = "Preparing private folder copies.", string DirectoryIdentity = "", string ManifestIdentity = "", int PreparedFiles = 0, long PreparedBytes = 0, string Label = "", bool MayHaveReceiverCopies = false, int Schema = 2);
public sealed record BatchSourceRemovalReview(string Id, string Plan, int Files, long Bytes);

/// <summary>Owner-selected, private batch snapshots. No source path is included in a wire manifest.</summary>
public sealed partial class WindowsBatchSources : IAsyncDisposable
{
    private sealed record Snapshot(int Index, long Size, string Identity, string Hash, long Written = 0);
    private sealed record Folder(string Volume, string VolumeIdentity, (string Name, string Identity)[] Bindings, long Written);
    private sealed record Source(int Index, Folder Parent, string Name, SecureWindowsDirectory.Snapshot Info);
    private sealed record ManifestCache(string Version, string Hash, UploadBatchManifest Manifest);
    private sealed record FileStamp(string Name, string Identity, long Size, long Written);
    private sealed record Review(BatchSourceRemovalReview Public, string RecordVersion, FileStamp[] Files, DateTimeOffset Expires);
    private sealed class Work(CancellationTokenSource cancellation)
    { public CancellationTokenSource Cancellation { get; } = cancellation; public Task Task { get; set; } = Task.CompletedTask; }
    private readonly object gate = new();
    private readonly string directory, protectedProfile;
    private SecureWindowsDirectory root;
    private SecureWindowsDirectory lease;
    private readonly Func<UploadAuthority, bool> current;
    private readonly Func<bool> ownerReady;
    private readonly Dictionary<string, OutgoingBatchSource> records = [];
    private readonly Dictionary<string, string> versions = [];
    private readonly Dictionary<string, ManifestCache> manifests = [];
    private readonly Dictionary<string, Review> reviews = [];
    private readonly Dictionary<string, Work> work = [];
    private readonly HashSet<SourceReader> readers = [];
    private StateRecovery recovery;
    private readonly OutgoingCopyResources.Participant inventory;
    private bool disposed;
    public event Action? Changed;
    public event Action? MetadataChanged;
    private void NotifyChanged(bool progressOnly = false)
    {
        Changed?.Invoke();
        if (!progressOnly) MetadataChanged?.Invoke();
    }
    public Action<WindowsActivityEvent>? Activity { get; set; }
    public IReadOnlyList<OutgoingBatchSource> All { get { lock (gate) return records.Values.Reverse().ToArray(); } }
    public IReadOnlyList<StateRecoveryIssue> RecoveryIssues { get { lock (gate) return recovery.Issues; } }
    public OutgoingBatchSource? FindSource(string id) { lock (gate) return records.GetValueOrDefault(id); }
    public bool HasPreparationProgress { get { lock (gate) return work.Count != 0; } }
    public bool Active(string id) { lock (gate) return work.ContainsKey(id); }

    public WindowsBatchSources(string profile, Func<UploadAuthority, bool> current, Func<bool>? ownerReady = null, OutgoingCopyResources? resources = null, Func<string>? recoveryOwner = null)
    {
        this.current = current; this.ownerReady = ownerReady ?? (() => true);
        profile = Path.GetFullPath(profile); var volumePath = Path.GetPathRoot(profile)!;
        if (volumePath.Length != 3 || volumePath[1] != ':' || !char.IsAsciiLetter(volumePath[0])) throw new InvalidDataException("Private batch storage requires a normal local drive.");
        Directory.CreateDirectory(profile);
        using (var protectedRoot = SecureWindowsDirectory.OpenRoot(profile)) protectedProfile = protectedRoot.Info.CanonicalPath;
        directory = Path.Combine(profile, SourceStoreName); this.recoveryOwner = recoveryOwner;
        recoveryProfile = SecureWindowsDirectory.OpenRoot(profile);
        try { profileLease = SourceOpenLease(recoveryProfile, ".outgoing-batch-sources.lock"); }
        catch { recoveryProfile.Dispose(); throw; }
        try { (root, lease) = OpenSourceStore(); }
        catch { profileLease.Dispose(); recoveryProfile.Dispose(); throw; }
        try { inventory = (resources ?? OutgoingCopyResources.Isolated(OutgoingCopyResources.Batch)).Open(OutgoingCopyResources.Batch, profile); }
        catch { lease.Dispose(); root.Dispose(); profileLease.Dispose(); recoveryProfile.Dispose(); throw; }
        recovery = new(directory);
        try
        {
            var paths = StateRecovery.Journals(directory, UploadBatchRules.MaxPlans);
            if (paths.Length > UploadBatchRules.MaxPlans) throw new InvalidDataException("Too many private batch plans.");
            foreach (var path in paths)
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (!UploadBatchRules.CanonicalToken(name)) throw new InvalidDataException("Unrecognized private batch metadata.");
                if (File.Exists(path + ".new") || File.Exists(path + ".bak")) throw new InvalidDataException("Uncommitted private batch ownership needs review.");
                var row = ReadSource(path); Validate(row);
                if (row.Id != name || !records.TryAdd(row.Id, row)) throw new InvalidDataException("A private batch lost its ownership record.");
                using (var record = root.OpenChild(row.Id + ".json")) versions[row.Id] = record.Info.Version;
                if (row.DirectoryIdentity.Length == 0) { if (Directory.Exists(PlanPath(row))) throw new InvalidDataException("An unbound private folder was preserved for review."); }
                else { using var retainedDirectory = Plan(row); }
                if (row.Phase == BatchSourcePhase.Ready) _ = ReadManifestCore(row);
                else if (row.Phase == BatchSourcePhase.Preparing) records[row.Id] = row with { Phase = BatchSourcePhase.Failed, Message = "Private preparation was interrupted. Original files and partial snapshots were kept." };
            }
            if (records.Values.GroupBy(x => x.Authority.Peer).Any(x => x.Count() > UploadBatchRules.MaxPeerPlans)) throw new InvalidDataException("Too many private batches for a peer.");
            if (records.Values.Sum(x => x.Bytes) > UploadRules.PeerQuota * 4 || records.Values.GroupBy(x => x.Authority.Peer).Any(x => x.Sum(row => row.Bytes) > UploadRules.PeerQuota))
                throw new InvalidDataException("Private batch reservations exceed their aggregate limits.");
            var names = root.EnumerateNames(default).Take(UploadBatchRules.MaxPlans * 4 + 2).ToArray();
            if (names.Length > UploadBatchRules.MaxPlans * 4 + 1 || names.Any(x => x.IsDirectory ? !records.ContainsKey(x.Name) : x.Name != "owner.lock" &&
                !records.Keys.Any(id => x.Name == id + ".json" || x.Name == id + ".json.new" || x.Name == id + ".json.bak")))
                throw new InvalidDataException("Unowned private batch copies were preserved for review.");
        }
        catch (Exception ex) when (StateRecovery.IsRecordFailure(ex))
        { records.Clear(); recovery.Block("Private batch ownership needs local review. Original files and every snapshot were kept; new preparation is blocked."); }
        try { RefreshSourceArchives(); }
        catch (Exception ex) when (StateRecovery.IsRecordFailure(ex)) { recovery.Block("Preserved private snapshots need local owner review. Their bytes remain uncertain; new outgoing copies are blocked."); }
        inventory.Recover(Reservations(records.Values), recovery.Needed, retainedSourceBytes);
        try { LoadSourceCopyReview(); }
        catch (Exception ex) when (StateRecovery.IsRecordFailure(ex)) { inventory.Uncertain(); recovery.Block("Private snapshot recovery history needs local review. Copies were kept."); }
        if (!recovery.Needed)
        {
            try
            {
                foreach (var row in records.Values.Where(x => x.Phase == BatchSourcePhase.Ready && (x.Schema == 1 || recoveryCopyReview && !x.MayHaveReceiverCopies)).ToArray())
                    NoteReceiverIntent(row.Id, row.Authority, row.Hash);
            }
            catch (Exception ex) when (StateRecovery.IsRecordFailure(ex))
            { inventory.Uncertain(); recovery.Block("Private batch copy review history could not be preserved. Copies were kept for local review."); }
        }
    }
    private static IEnumerable<OutgoingCopyResources.Reservation> Reservations(IEnumerable<OutgoingBatchSource> rows) => rows.Select(row => new OutgoingCopyResources.Reservation(row.Id, row.Authority.Peer, row.Bytes, row.Phase == BatchSourcePhase.Preparing ? row.Bytes - row.PreparedBytes : 0));
    private const int RecordLimit = 64 * 1024;
    private static bool Hex(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);
    private static OutgoingBatchSource ReadSource(string path)
    {
        var saved = StateRecovery.Read<JsonElement>(path, RecordLimit);
        var schema = saved.TryGetProperty(nameof(OutgoingBatchSource.Schema), out var version) ? version.GetInt32() : 1;
        var present = saved.TryGetProperty(nameof(OutgoingBatchSource.MayHaveReceiverCopies), out var flag);
        if (schema is not (1 or 2) || schema == 1 && present || schema == 2 && (!present || flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False)))
            throw new InvalidDataException("Private batch copy history is missing or malformed.");
        return (saved.Deserialize<OutgoingBatchSource>() ?? throw new InvalidDataException("Private batch source is empty.")) with { Schema = schema };
    }
    private static void Validate(OutgoingBatchSource row)
    {
        if (row.Schema is not (1 or 2) || row.Schema == 1 && row.MayHaveReceiverCopies || !UploadBatchRules.CanonicalToken(row.Id) || !Hex(row.Authority.Owner) || !Hex(row.Authority.Peer) || !UploadBatchRules.CanonicalToken(row.Authority.OwnerEpoch) ||
            !UploadBatchRules.CanonicalToken(row.Authority.Pairing) || !Enum.IsDefined(row.Phase) || row.Items is < 0 or > UploadBatchRules.MaxItems ||
            row.Bytes is < 0 or > UploadRules.PeerQuota || row.Hash.Length != 0 && !Hex(row.Hash) || row.EncodedLength is < 0 or > UploadBatchRules.MaxEncodedBytes ||
            row.PreparedFiles < 0 || row.PreparedFiles > row.Items || row.PreparedBytes < 0 || row.PreparedBytes > row.Bytes || row.Phase == BatchSourcePhase.Ready && row.PreparedBytes != row.Bytes ||
            row.Message.Length > 512 || row.Message.Any(char.IsControl) || row.Label.Length > 512 || row.Label.Any(char.IsControl) || row.DirectoryIdentity.Length > 256 || row.ManifestIdentity.Length > 256 ||
            row.Phase == BatchSourcePhase.Ready && (row.Items == 0 || row.Hash.Length == 0 || row.EncodedLength == 0 || row.DirectoryIdentity.Length == 0 || row.ManifestIdentity.Length == 0))
            throw new InvalidDataException("Invalid private batch source.");
    }
    private void RequireReady()
    {
        ObjectDisposedException.ThrowIf(disposed, this); recovery.RequireHealthy();
        if (!ownerReady()) throw new InvalidOperationException("Review the current identity and approvals first.");
        if (SecureWindowsDirectory.Inspect(root.Handle).Identity != root.Info.Identity) throw new IOException("Private batch storage changed.");
        try
        {
            foreach (var row in records.Values) { using var file = root.OpenChild(row.Id + ".json"); if (file.Info.Version != versions[row.Id]) throw new IOException("Private batch ownership metadata changed."); }
        }
        catch (Exception ex) when (StateRecovery.IsRecordFailure(ex)) { inventory.Uncertain(); recovery.Block("Private batch ownership changed. Preserve copies and review the queue locally."); throw; }
    }
    private void RequireAuthority(OutgoingBatchSource row) { lock (gate) { RequireReady(); if (!current(row.Authority)) throw new InvalidOperationException("This private batch belongs to an earlier identity or pairing."); } }
    private OutgoingBatchSource Put(OutgoingBatchSource row, bool progressOnly = false)
    {
        BatchSourcePhase? previous;
        lock (gate)
        {
            previous = records.GetValueOrDefault(row.Id)?.Phase;
            row = row with { Schema = 2 };
            Validate(row);
            inventory.Replace(Reservations(records.Values.Where(x => x.Id != row.Id).Append(row)), () => new DriveInfo(Path.GetPathRoot(directory)!).AvailableFreeSpace);
            try { WriteJson(Path.Combine(directory, row.Id + ".json"), row); records[row.Id] = row; using var file = root.OpenChild(row.Id + ".json"); versions[row.Id] = file.Info.Version; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { inventory.Uncertain(); recovery.Block("Private batch progress could not be committed. Preserve the snapshots and review this queue."); throw; }
        }
        if (previous != row.Phase && row.Phase != BatchSourcePhase.Preparing) WindowsActivity.Publish(Activity, new(WindowsActivityKind.BatchPrepare,
            row.Phase == BatchSourcePhase.Ready ? WindowsActivityOutcome.Accepted : WindowsActivityOutcome.Failed, row.Authority.Peer, row.Phase == BatchSourcePhase.Ready ? row.Bytes : -1));
        NotifyChanged(progressOnly); return row;
    }
    private static void WriteJson<T>(string path, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > RecordLimit) throw new InvalidDataException("Private batch metadata exceeds its record limit.");
        using var parent = SecureWindowsDirectory.OpenRoot(Path.GetDirectoryName(path)!);
        using (var output = parent.CreateFile(Path.GetFileName(path) + ".new")) { output.Write(0, bytes); output.Flush(); }
        AtomicStateFile.Commit(path + ".new", path);
    }
    private SecureWindowsDirectory Plan(OutgoingBatchSource row)
    {
        var plan = root.OpenChild(row.Id, true);
        if (plan.Info.Identity == row.DirectoryIdentity) return plan;
        plan.Dispose(); throw new InvalidDataException("The private batch snapshot folder was replaced.");
    }
    private string PlanPath(OutgoingBatchSource row) => Path.Combine(directory, row.Id);
    private static string Part(int index) => index + ".bin";
    private static string Receipt(int index) => index + ".json";
    private static Folder Binding(string volume, string identity, List<(string Name, string Identity)> bindings, SecureWindowsDirectory folder)
        => new(volume, identity, bindings.ToArray(), folder.Info.WriteTimestamp);
    private static SecureWindowsDirectory OpenFolder(Folder folder) => SecureWindowsDirectory.OpenBoundPath(folder.Volume, folder.VolumeIdentity, folder.Bindings);

    public Task<string> PrepareAsync(IReadOnlyList<string> selectedPaths, UploadAuthority authority, UploadCollision collision, CancellationToken ct = default)
    {
        if (selectedPaths.Count is < 1 or > UploadBatchRules.MaxRoots || !Enum.IsDefined(collision)) throw new InvalidDataException("Choose at most 128 files or folders and a supported collision choice.");
        var paths = selectedPaths.Select(Path.GetFullPath).ToArray();
        var label = Path.GetFileName(paths[0].TrimEnd(Path.DirectorySeparatorChar)) + (paths.Length > 1 ? $" + {paths.Length - 1} more" : "");
        var row = new OutgoingBatchSource(ProtocolRules.NewToken(), authority, BatchSourcePhase.Preparing, Label: label, MayHaveReceiverCopies: recoveryCopyReview);
        lock (gate)
        {
            RefreshSourceAccounting();
            RequireAuthority(row);
            row = row with { MayHaveReceiverCopies = recoveryCopyReview };
            if (records.Count >= UploadBatchRules.MaxPlans || records.Values.Count(x => x.Authority.Peer == authority.Peer) >= UploadBatchRules.MaxPeerPlans || work.Count >= 2)
                throw new IOException("Private batch slots are full. Wait for preparation or review retained copies.");
            var worker = inventory.TryWorker() ?? throw new InvalidOperationException("Two outgoing preparations or sends are already active. Retry after one stops.");
            try { Put(row); } catch { worker.Dispose(); throw; }
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct); var active = new Work(cancellation); work.Add(row.Id, active);
            active.Task = Task.Run(() => Prepare(row, paths, collision, cancellation.Token)).ContinueWith(task =>
            {
                lock (gate) { worker.Dispose(); work.Remove(row.Id); } cancellation.Dispose(); NotifyChanged(); return task.GetAwaiter().GetResult();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return AwaitPrepared();
            async Task<string> AwaitPrepared() { await active.Task; return row.Id; }
        }
    }
    private string Prepare(OutgoingBatchSource row, string[] paths, UploadCollision collision, CancellationToken ct)
    {
        try
        {
            var manifest = new UploadBatchManifest { Collision = collision }; var sources = new List<Source>(); var folders = new List<Folder>();
            foreach (var path in paths) ScanRoot(path);
            var bytes = UploadBatchRules.Validate(manifest);
            lock (gate)
            {
                RequireAuthority(row);
                var retained = records.Values.Where(x => x.Id != row.Id).ToArray();
                if (bytes > UploadRules.PeerQuota - retained.Where(x => x.Authority.Peer == row.Authority.Peer).Sum(x => x.Bytes) ||
                    bytes > UploadRules.PeerQuota * 4 - retained.Sum(x => x.Bytes)) throw new IOException("Private batch copies exceed the aggregate reservation.");
                var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!).AvailableFreeSpace;
                if (bytes > Math.Max(0, free - 64L * 1024 * 1024 - inventory.OtherPending(row.Id)))
                    throw new IOException("Not enough private disk space for the selected batch snapshots.");
                row = Put(row with { Items = manifest.Items.Count, Bytes = bytes });
            }
            using var plan = root.CreateDirectory(row.Id);
            row = Put(row with { DirectoryIdentity = plan.Info.Identity });
            var preparedFiles = 0; long preparedBytes = 0;
            foreach (var source in sources)
            {
                Check(); using var parent = OpenFolder(source.Parent); using var original = parent.OpenChild(source.Name);
                if (original.Info.Version != source.Info.Version) throw new IOException("A selected file changed before it could be copied.");
                var available = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!).AvailableFreeSpace;
                if (source.Info.Size > Math.Max(0, available - 64L * 1024 * 1024)) throw new IOException("Not enough private disk space to finish the selected snapshot.");
                Snapshot receipt;
                using (var snapshot = plan.CreateFile(Part(source.Index)))
                {
                    receipt = new(source.Index, source.Info.Size, snapshot.Info.Identity, "");
                    WriteJson(Path.Combine(PlanPath(row), Receipt(source.Index)), receipt);
                    using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[StorageRules.MaxReadRange]; long offset = 0;
                    while (offset < source.Info.Size)
                    {
                        Check(); var length = (int)Math.Min(buffer.Length, source.Info.Size - offset); ReadExactly(original, offset, buffer.AsSpan(0, length));
                        snapshot.Write(offset, buffer.AsSpan(0, length)); digest.AppendData(buffer, 0, length); offset += length;
                    }
                    Check(); snapshot.Flush();
                    if (SecureWindowsDirectory.Inspect(original.Handle).Version != source.Info.Version) throw new IOException("A selected file changed during preparation.");
                    receipt = receipt with { Hash = Convert.ToHexStringLower(digest.GetHashAndReset()) };
                }
                // The last-write timestamp is final only after the writable handle closes.
                using (var saved = plan.OpenChild(Part(source.Index)))
                {
                    receipt = receipt with { Written = saved.Info.WriteTimestamp }; RequireSnapshot(receipt, saved);
                    using var readback = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[StorageRules.MaxReadRange]; long offset = 0;
                    while (offset < receipt.Size) { Check(); var length = (int)Math.Min(buffer.Length, receipt.Size - offset); ReadExactly(saved, offset, buffer.AsSpan(0, length)); readback.AppendData(buffer, 0, length); offset += length; }
                    if (!CryptographicOperations.FixedTimeEquals(readback.GetHashAndReset(), Convert.FromHexString(receipt.Hash))) throw new IOException("The private snapshot did not verify after flushing.");
                }
                WriteJson(Path.Combine(PlanPath(row), Receipt(source.Index)), receipt);
                manifest.Items[source.Index].Sha256 = ByteString.CopyFrom(Convert.FromHexString(receipt.Hash));
                preparedFiles++; preparedBytes += receipt.Size;
                if (preparedFiles % 32 == 0 || preparedBytes - row.PreparedBytes >= 1024 * 1024) row = Put(row with { PreparedFiles = preparedFiles, PreparedBytes = preparedBytes }, progressOnly: true);
            }
            foreach (var folder in folders) { Check(); using var retained = OpenFolder(folder); if (retained.Info.WriteTimestamp != folder.Written) throw new IOException("A selected folder changed during preparation. Private copies were kept."); }
            var encoded = UploadBatchRules.Encode(manifest); Check();
            using (var output = plan.CreateFile("manifest.bin")) { row = Put(row with { ManifestIdentity = output.Info.Identity }); output.Write(0, encoded); output.Flush(); }
            Check(); Put(row with { Phase = BatchSourcePhase.Ready, Hash = Convert.ToHexStringLower(SHA256.HashData(encoded)), EncodedLength = encoded.Length,
                PreparedFiles = preparedFiles, PreparedBytes = preparedBytes,
                Message = "Private batch ready. Original folders remain local; choose an approved receiving location explicitly." });
            return row.Id;

            void Check() { ct.ThrowIfCancellationRequested(); RequireAuthority(row); }
            void ScanRoot(string path)
            {
                Check(); var volumePath = Path.GetPathRoot(path)!;
                if (volumePath.Length != 3 || volumePath[1] != ':' || !char.IsAsciiLetter(volumePath[0])) throw new InvalidDataException("Choose sources on a normal local drive.");
                var segments = path[volumePath.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length is < 1 or > 64) throw new InvalidDataException("Choose a named source within the local path limit.");
                foreach (var name in segments) StorageRules.ValidateName(name);
                if (StorageGrantStore.IsSensitiveSegments(segments[..^1]) || StorageGrantStore.IsSystemTree(segments)) throw new IOException("System and credential locations are excluded from private folder preparation.");
                using var volume = SecureWindowsDirectory.OpenRoot(volumePath); var bindings = new List<(string Name, string Identity)>();
                var held = new List<SecureWindowsDirectory>(); var parent = volume;
                try
                {
                    foreach (var component in segments[..^1]) { parent = parent.OpenChild(component, true); held.Add(parent); bindings.Add((component, parent.Info.Identity)); }
                    // Kind is only a hint; the native relative open below enforces it and rejects links.
                    var candidate = new SecureWindowsDirectory.DirectoryName(segments[^1], (File.GetAttributes(path) & FileAttributes.Directory) != 0);
                    Scan(parent, candidate, -1, 1, 0, volume.Info.CanonicalPath, volume.Info.Identity, bindings);
                }
                finally { foreach (var item in held.AsEnumerable().Reverse()) item.Dispose(); }
            }
            void Scan(SecureWindowsDirectory parent, SecureWindowsDirectory.DirectoryName name, int parentIndex, int depth, int pathChars,
                string volume, string volumeIdentity, List<(string Name, string Identity)> bindings)
            {
                Check();
                if (manifest.Items.Count >= UploadBatchRules.MaxItems || depth > UploadBatchRules.MaxDepth || pathChars + name.Name.Length > UploadBatchRules.MaxPathChars)
                    throw new IOException("The selected batch exceeds its item, depth or path limit.");
                var index = manifest.Items.Count; var item = new UploadBatchItem { Parent = parentIndex, Name = name.Name, IsDirectory = name.IsDirectory };
                manifest.Items.Add(item);
                try { StorageRules.ValidateName(name.Name); }
                catch (StorageException) { item.Error = "This name cannot be represented safely on both devices."; return; }
                if (StorageGrantStore.IsSensitiveSegments([name.Name])) { item.Error = "System, credential and private application data are excluded from folder snapshots."; return; }
                SecureWindowsDirectory opened;
                try { opened = parent.OpenChild(name.Name, name.IsDirectory); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { item.Error = "This entry is unavailable or cannot be opened safely. Its contents were not copied."; return; }
                using var sourceHandle = opened;
                if (SecureWindowsDirectory.IsWithin(opened.Info.CanonicalPath, protectedProfile)) { item.Error = "Private iConnect data is excluded from folder snapshots."; return; }
                if (name.IsDirectory)
                {
                    bindings.Add((name.Name, opened.Info.Identity)); var folder = Binding(volume, volumeIdentity, bindings, opened); folders.Add(folder);
                    var children = opened.EnumerateNames(ct).Take(UploadBatchRules.MaxItems + 1).OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
                    if (children.Length > UploadBatchRules.MaxItems) throw new IOException("The selected folder exceeds the batch item limit.");
                    foreach (var child in children) Scan(opened, child, index, depth + 1, pathChars + name.Name.Length, volume, volumeIdentity, bindings);
                    if (SecureWindowsDirectory.Inspect(opened.Handle).Version != opened.Info.Version) throw new IOException("A selected folder changed while being enumerated.");
                    bindings.RemoveAt(bindings.Count - 1);
                }
                else
                {
                    item.Size = opened.Info.Size; item.Sha256 = ByteString.CopyFrom(new byte[32]);
                    sources.Add(new(index, Binding(volume, volumeIdentity, bindings, parent), name.Name, opened.Info));
                }
            }
        }
        catch (Exception ex) when (StateRecovery.IsRecordFailure(ex) || ex is OperationCanceledException)
        {
            lock (gate) if (!recovery.Needed) Put(row with { Phase = BatchSourcePhase.Failed, Message = "Private preparation stopped. Original files and owned partial snapshots were kept; choose sources again deliberately." });
            throw;
        }
    }
    private UploadBatchManifest ReadManifestCore(OutgoingBatchSource row, bool validateFiles = true)
    {
        if (row.Phase != BatchSourcePhase.Ready) throw new InvalidOperationException("This private batch is not ready to send.");
        using var plan = Plan(row); using var encoded = plan.OpenChild("manifest.bin");
        if (encoded.Info.Identity != row.ManifestIdentity || encoded.Info.Size != row.EncodedLength) throw new InvalidDataException("The private batch manifest changed.");
        UploadBatchManifest manifest;
        if (manifests.TryGetValue(row.Id, out var cached) && cached.Version == encoded.Info.Version && cached.Hash == row.Hash) manifest = cached.Manifest;
        else
        {
            var bytes = new byte[row.EncodedLength]; ReadExactly(encoded, 0, bytes);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(row.Hash))) throw new InvalidDataException("The private batch manifest checksum changed.");
            manifest = UploadBatchRules.Decode(bytes);
            if (manifest.Items.Count != row.Items || UploadBatchRules.Validate(manifest) != row.Bytes || manifest.Items.Count(x => !x.IsDirectory && x.Error.Length == 0) != row.PreparedFiles)
                throw new InvalidDataException("The private batch no longer matches its retained plan.");
            if (manifests.Count >= 2) manifests.Remove(manifests.Keys.First());
            manifests[row.Id] = new(encoded.Info.Version, row.Hash, manifest);
        }
        if (!validateFiles) return manifest;
        var expected = new HashSet<string>(StringComparer.Ordinal) { "manifest.bin" };
        for (var index = 0; index < manifest.Items.Count; index++)
        {
            var item = manifest.Items[index]; if (item.IsDirectory || item.Error.Length != 0) continue;
            expected.Add(Part(index)); expected.Add(Receipt(index));
            var snapshot = StateRecovery.Read<Snapshot>(Path.Combine(PlanPath(row), Receipt(index)), RecordLimit);
            if (snapshot.Index != index || snapshot.Size != item.Size || string.IsNullOrEmpty(snapshot.Identity) || snapshot.Identity.Length > 256 || !Hex(snapshot.Hash) || !Convert.FromHexString(snapshot.Hash).AsSpan().SequenceEqual(item.Sha256.Span))
                throw new InvalidDataException("A private batch file lost its descriptor binding.");
            using var file = plan.OpenChild(Part(index)); RequireSnapshot(snapshot, file);
        }
        var names = plan.EnumerateNames(default).Take(UploadBatchRules.MaxItems * 2 + 2).ToArray();
        if (names.Length != expected.Count || names.Any(x => x.IsDirectory || !expected.Contains(x.Name))) throw new InvalidDataException("Unowned private batch entries were preserved for review.");
        return manifest;
    }
    public UploadBatchManifest ReadManifest(string id) { lock (gate) { var row = records[id]; RequireAuthority(row); return ReadManifestCore(row).Clone(); } }
    public UploadBatchManifest InspectManifest(string id) { lock (gate) { RequireReady(); return ReadManifestCore(records[id], false).Clone(); } }
    internal void NoteReceiverIntent(string id, UploadAuthority authority, string hash)
    {
        lock (gate)
        {
            RequireReady(); var row = records[id];
            if (row.Authority != authority || row.Hash != hash || row.Phase != BatchSourcePhase.Ready) throw new InvalidDataException("The original private snapshot changed before saving its receiver-copy history.");
            if (!row.MayHaveReceiverCopies) Put(row with { MayHaveReceiverCopies = true });
        }
    }
    public sealed class SourceReader : IDisposable
    {
        private readonly SecureWindowsDirectory parent, file;
        private readonly Action authorize;
        private readonly IDisposable worker;
        private readonly Action<SourceReader> closed;
        private int disposed;
        public long Size { get; }
        internal string PlanId { get; }
        internal SourceReader(string id, SecureWindowsDirectory parent, SecureWindowsDirectory file, long size, Action authorize, IDisposable worker, Action<SourceReader> closed)
        { PlanId = id; this.parent = parent; this.file = file; Size = size; this.authorize = authorize; this.worker = worker; this.closed = closed; }
        internal void ReadVerified(long offset, Span<byte> bytes)
        { ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this); authorize(); ReadExactly(file, offset, bytes); }
        public byte[] ReadRange(long offset, int length)
        {
            if (offset < 0 || length is < 1 or > StorageRules.MaxReadRange || offset > Size - length) throw new InvalidDataException("Invalid private batch range.");
            var bytes = new byte[length]; ReadVerified(offset, bytes); return bytes;
        }
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) != 0) return; try { file.Dispose(); parent.Dispose(); } finally { worker.Dispose(); closed(this); } }
    }
    // Verify the complete immutable snapshot before the sender makes its first remote effect.
    // Keep this reader open for the entire attempt so another writer cannot change later ranges.
    public SourceReader OpenFile(string id, int index, CancellationToken ct = default)
    {
        OutgoingBatchSource row; Snapshot snapshot; SecureWindowsDirectory? plan = null, file = null; IDisposable? worker = null; SourceReader? reader = null;
        try
        {
            lock (gate)
            {
                row = records[id]; RequireAuthority(row); var manifest = ReadManifestCore(row, false);
                if (index < 0 || index >= manifest.Items.Count || manifest.Items[index].IsDirectory || manifest.Items[index].Error.Length != 0) throw new InvalidDataException("This planned item has no private file snapshot.");
                snapshot = StateRecovery.Read<Snapshot>(Path.Combine(PlanPath(row), Receipt(index)), RecordLimit);
                if (snapshot.Index != index || snapshot.Size != manifest.Items[index].Size || !Hex(snapshot.Hash) ||
                    !Convert.FromHexString(snapshot.Hash).AsSpan().SequenceEqual(manifest.Items[index].Sha256.Span)) throw new InvalidDataException("The private snapshot descriptor changed.");
                worker = inventory.TryWorker() ?? throw new InvalidOperationException("Two outgoing preparations or sends are already active. Retry after one stops.");
                plan = Plan(row); file = plan.OpenChild(Part(index)); RequireSnapshot(snapshot, file);
                reader = new SourceReader(id, plan, file, snapshot.Size, () => { ct.ThrowIfCancellationRequested(); RequireAuthority(row); }, worker, value => { lock (gate) readers.Remove(value); });
                readers.Add(reader); plan = null; file = null; worker = null;
            }
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[StorageRules.MaxReadRange]; long offset = 0;
            while (offset < snapshot.Size)
            {
                ct.ThrowIfCancellationRequested(); RequireAuthority(row); var length = (int)Math.Min(buffer.Length, snapshot.Size - offset);
                reader.ReadVerified(offset, buffer.AsSpan(0, length)); digest.AppendData(buffer, 0, length); offset += length;
            }
            ct.ThrowIfCancellationRequested(); RequireAuthority(row);
            if (!CryptographicOperations.FixedTimeEquals(digest.GetHashAndReset(), Convert.FromHexString(snapshot.Hash))) throw new InvalidDataException("The private snapshot checksum changed. No remote effect should be started.");
            return reader;
        }
        catch { reader?.Dispose(); throw; }
        finally { file?.Dispose(); plan?.Dispose(); worker?.Dispose(); }
    }
    private static void RequireSnapshot(Snapshot snapshot, SecureWindowsDirectory file)
    { if (file.Info.Identity != snapshot.Identity || file.Info.Size != snapshot.Size || file.Info.WriteTimestamp != snapshot.Written || file.Info.IsDirectory || file.Info.Links != 1) throw new InvalidDataException("A private batch snapshot changed."); }
    private static void ReadExactly(SecureWindowsDirectory source, long offset, Span<byte> bytes)
    { var read = 0; while (read < bytes.Length) { var count = RandomAccess.Read(source.Handle, bytes[read..], offset + read); if (count == 0) throw new EndOfStreamException("Private preparation encountered a short file."); read += count; } }
    public BatchSourceRemovalReview ReviewRemoval(string id)
    {
        lock (gate)
        {
            RequireReady(); if (work.ContainsKey(id) || readers.Any(x => x.PlanId == id)) throw new InvalidOperationException("Stop private preparation and sending before reviewing its copies.");
            var row = records[id]; var stamps = new List<FileStamp>();
            if (row.DirectoryIdentity.Length != 0)
            {
                using var plan = Plan(row); var names = plan.EnumerateNames(default).Take(UploadBatchRules.MaxItems * 2 + 2).ToArray();
                if (names.Length > UploadBatchRules.MaxItems * 2 + 1 || names.Any(x => x.IsDirectory)) throw new IOException("Unexpected private snapshot entries were kept for review.");
                var receipts = new Dictionary<int, Snapshot>();
                foreach (var name in names.Where(x => x.Name.EndsWith(".json", StringComparison.Ordinal)))
                {
                    if (!int.TryParse(name.Name[..^5], out var index) || index is < 0 or >= UploadBatchRules.MaxItems || name.Name != Receipt(index)) throw new IOException("Unexpected snapshot metadata was kept.");
                    var snapshot = StateRecovery.Read<Snapshot>(Path.Combine(PlanPath(row), name.Name), RecordLimit);
                    if (snapshot.Index != index || snapshot.Size is < 0 or > UploadRules.PeerQuota || string.IsNullOrEmpty(snapshot.Identity) || snapshot.Identity.Length > 256 || snapshot.Hash is null || snapshot.Hash.Length != 0 && !Hex(snapshot.Hash))
                        throw new IOException("Private snapshot ownership needs review before removal.");
                    receipts.Add(index, snapshot);
                }
                foreach (var name in names)
                {
                    if (name.Name != "manifest.bin")
                    {
                        var extension = Path.GetExtension(name.Name); var text = Path.GetFileNameWithoutExtension(name.Name);
                        if (extension is not (".bin" or ".json") || !int.TryParse(text, out var index) || text != index.ToString() || !receipts.TryGetValue(index, out var snapshot))
                            throw new IOException("Unowned private files were kept for separate review.");
                        if (extension == ".bin")
                        {
                            using var file = plan.OpenChild(name.Name);
                            if (file.Info.Identity != snapshot.Identity || file.Info.Size > StorageRules.MaxTransferSize)
                                throw new IOException("A private snapshot changed. Review its ownership before removal.");
                        }
                    }
                    using var owned = plan.OpenChild(name.Name);
                    if (name.Name == "manifest.bin" && (row.ManifestIdentity.Length == 0 || owned.Info.Identity != row.ManifestIdentity)) throw new IOException("An unowned manifest was kept for separate review.");
                    stamps.Add(new(name.Name, owned.Info.Identity, owned.Info.Size, owned.Info.WriteTimestamp));
                }
            }
            else if (Directory.Exists(PlanPath(row))) throw new IOException("An uncertain private folder was kept. It cannot be removed by name.");
            foreach (var expired in reviews.Where(x => x.Value.Expires < DateTimeOffset.UtcNow).Select(x => x.Key).ToArray()) reviews.Remove(expired);
            if (reviews.Count >= UploadBatchRules.MaxPlans) reviews.Remove(reviews.Keys.First());
            var review = new BatchSourceRemovalReview(ProtocolRules.NewToken(), id, stamps.Count(x => x.Name.EndsWith(".bin", StringComparison.Ordinal) && x.Name != "manifest.bin"),
                stamps.Where(x => x.Name.EndsWith(".bin", StringComparison.Ordinal) && x.Name != "manifest.bin").Sum(x => x.Size));
            reviews.Add(review.Id, new(review, versions[id], stamps.ToArray(), DateTimeOffset.UtcNow.AddMinutes(2))); return review;
        }
    }
    public void RemoveReviewed(BatchSourceRemovalReview confirmation)
    {
        lock (gate)
        {
            RequireReady();
            if (!reviews.Remove(confirmation.Id, out var review) || review.Public != confirmation || review.Expires < DateTimeOffset.UtcNow || work.ContainsKey(confirmation.Plan) ||
                !versions.TryGetValue(confirmation.Plan, out var version) || version != review.RecordVersion || readers.Any(x => x.PlanId == confirmation.Plan)) throw new IOException("This private-copy review changed or expired. Review it again.");
            var row = records[confirmation.Plan];
            if (row.DirectoryIdentity.Length != 0)
            {
                using var plan = SecureWindowsDirectory.OpenPath(root.Info.CanonicalPath, root.Info.Identity, [row.Id], true, mutation: true);
                if (plan.Info.Identity != row.DirectoryIdentity) throw new IOException("The reviewed private folder was replaced. Copies were kept.");
                var names = plan.EnumerateNames(default).Take(review.Files.Length + 1).ToArray();
                if (names.Length != review.Files.Length || names.Any(x => x.IsDirectory || !review.Files.Any(file => file.Name == x.Name))) throw new IOException("The reviewed private folder changed. Copies were kept.");
                // Validate the complete review before deleting any payload.
                foreach (var stamp in review.Files) { using var file = plan.OpenChild(stamp.Name); RequireStamp(file, stamp); }
                foreach (var stamp in review.Files.OrderBy(x => x.Name.EndsWith(".json", StringComparison.Ordinal))) { using var file = plan.OpenMutableChild(stamp.Name); RequireStamp(file, stamp); file.Delete(); }
                plan.Delete();
            }
            using (var record = root.OpenMutableChild(row.Id + ".json")) { if (record.Info.Version != review.RecordVersion) throw new IOException("Private ownership metadata changed. Its record was kept."); record.Delete(); }
            records.Remove(row.Id); versions.Remove(row.Id); manifests.Remove(row.Id); inventory.Replace(Reservations(records.Values)); NotifyChanged();
        }
        static void RequireStamp(SecureWindowsDirectory file, FileStamp stamp)
        { if (file.Info.Identity != stamp.Identity || file.Info.Size != stamp.Size || file.Info.WriteTimestamp != stamp.Written) throw new IOException("A reviewed private file changed. It was kept."); }
    }
    public void PauseAll() { lock (gate) foreach (var active in work.Values) active.Cancellation.Cancel(); }
    public void CancelPreparation(string id) { lock (gate) if (work.TryGetValue(id, out var active)) active.Cancellation.Cancel(); }
    public async Task WhenIdleAsync() { Task[] tasks; lock (gate) tasks = work.Values.Select(x => x.Task).ToArray(); try { await Task.WhenAll(tasks); } catch (Exception ex) when (StateRecovery.IsRecordFailure(ex) || ex is OperationCanceledException) { } }
    public async ValueTask DisposeAsync()
    {
        SourceReader[] active; lock (gate) { if (disposed) return; disposed = true; PauseAll(); active = readers.ToArray(); }
        foreach (var reader in active) reader.Dispose(); await WhenIdleAsync(); inventory.Dispose(); lease.Dispose(); root.Dispose(); profileLease.Dispose(); recoveryProfile.Dispose();
    }
}
