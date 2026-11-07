using IConnect.Core;
using IConnect.Protocol;
using System.Globalization;
using System.Text.Json;

namespace IConnect.Storage.Windows;

public sealed partial class WindowsBatchPlans
{
    private const int MaxStoredPlanFiles = 1 + MaxDetailedPlans * (UploadBatchRules.MaxItems * 2 + 2) + MaxRetiredPlans;
    private const int ChildRecordLimit = 32 * 1024, ChildMetadataLimit = 4 * 1024 * 1024;
    private sealed record DirectoryChild(int Schema, uint Index, string Id, string Name, string ParentIdentity, string Identity, UploadBatchItemPhase Phase, string Message, bool IsDirectory = true);
    private sealed record DirectoryAttempts(int Schema, string Batch, string Hash, byte[] Bits, string Checksum);
    private static string AttemptsChecksum(UploadBatchHeader header, byte[] bits) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData([.. header.Sha256.Span, .. System.Text.Encoding.ASCII.GetBytes(header.Id), .. bits]));
    private readonly Dictionary<string, long> directoryBytes = [];
    private sealed record ManifestCache(string Version, Row Row, byte[] Encoded, UploadBatchManifest Manifest);
    private readonly Dictionary<string, ManifestCache> manifestCache = [];
    private static string ChildName(string batch, uint index) => batch + "." + index.ToString(CultureInfo.InvariantCulture) + ".json";
    private static string AttemptsName(string batch) => batch + ".children.json";
    private static bool IsAttemptsName(string name) => name.EndsWith(".children.json", StringComparison.Ordinal) && UploadBatchRules.CanonicalToken(name[..^".children.json".Length]);
    private DirectoryAttempts? ReadAttempts(Row row)
    {
        var h = Header(row);
        try
        {
            var index = StateRecovery.Read<DirectoryAttempts>(Path.Combine(directory, AttemptsName(h.Id)), 4096);
            if (index.Schema != 1 || index.Batch != h.Id || index.Hash != Convert.ToHexStringLower(h.Sha256.Span) || index.Bits is null || index.Bits.Length != (h.ItemCount + 7) / 8 ||
                h.ItemCount % 8 != 0 && (index.Bits[^1] >> (int)(h.ItemCount % 8)) != 0 || index.Checksum != AttemptsChecksum(h, index.Bits)) throw new InvalidDataException("Invalid batch child attempt index.");
            return index;
        }
        catch (StorageException ex) when (ex.NativeError is 2 or 3) { return null; }
        catch (Exception ex) when (StateRecovery.IsRecordFailure(ex))
        { recovery.Block("Batch attempt ownership needs local review. Existing destination items were kept."); Changed?.Invoke(); throw new IOException("The batch attempt index could not be verified.", ex); }
    }
    private static bool Attempted(DirectoryAttempts index, uint item) => (index.Bits[item / 8] & (1 << (int)(item % 8))) != 0;
    private static bool TryChildName(string name, out string batch, out uint index)
    {
        batch = ""; index = 0;
        if (!name.EndsWith(".json", StringComparison.Ordinal)) return false;
        var stem = name[..^5]; var split = stem.IndexOf('.');
        if (split < 0 || !UploadBatchRules.CanonicalToken(stem[..split]) || !uint.TryParse(stem[(split + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out index) ||
            index >= UploadBatchRules.MaxItems || stem[(split + 1)..] != index.ToString(CultureInfo.InvariantCulture)) return false;
        batch = stem[..split]; return true;
    }
    private UploadBatchManifest AcceptedManifest(Row row, out byte[] encoded)
    {
        if (row.Retired) throw new StorageException(FileStatus.NotFound, "This compact parent receipt has no retained item manifest.");
        try
        {
        var h = Header(row); string version;
        using (var root = SecureWindowsDirectory.OpenRoot(directory))
        using (var file = root.OpenChild(h.Id + ".json")) version = file.Info.Version;
        if (manifestCache.TryGetValue(h.Id, out var cached) && cached.Version == version && SamePlanRow(cached.Row, row))
        { encoded = cached.Encoded; return cached.Manifest; }
        var saved = StateRecovery.Read<Envelope>(Path.Combine(directory, h.Id + ".json"), RecordLimit);
        Validate(saved);
        if (!SamePlanRow(saved.Row, row))
            throw new InvalidDataException("The retained batch plan changed.");
        encoded = saved.Manifest; var manifest = UploadBatchRules.Decode(h, encoded);
        if (manifestCache.Count >= 2) manifestCache.Remove(manifestCache.Keys.First());
        manifestCache[h.Id] = new(version, row, encoded, manifest); return manifest;
        }
        catch (Exception ex) when (StateRecovery.IsRecordFailure(ex))
        { recovery.Block("The accepted batch metadata changed or is damaged. Existing copies were kept; review this history locally."); Changed?.Invoke(); throw new StorageException(FileStatus.Conflict, "The accepted batch plan could not be verified."); }
    }
    private static bool SamePlanRow(Row a, Row b) => Header(a).Equals(Header(b)) && a.Owner == b.Owner && a.Peer == b.Peer && a.Pairing == b.Pairing &&
        SameTarget(a.Target, b.Target) && UploadBatchRules.HasManifest(a.Phase) && a.Phase == b.Phase && a.EffectsStarted == b.EffectsStarted && a.StopCursor == b.StopCursor && (a.Outcomes ?? []).AsSpan().SequenceEqual(b.Outcomes ?? []);
    private static void ValidateChild(Row row, UploadBatchManifest manifest, DirectoryChild child)
    {
        var h = Header(row);
        if (child.Schema != 1 || child.Index >= manifest.Items.Count || manifest.Items[(int)child.Index].IsDirectory != child.IsDirectory || manifest.Items[(int)child.Index].Error.Length != 0 ||
            child.Id != UploadBatchRules.ChildId(row.Peer, h, (int)child.Index, child.IsDirectory) || !Enum.IsDefined(child.Phase) ||
            child.ParentIdentity.Length is < 1 or > 256 || child.Identity.Length > 256 || child.Message.Length > 512 || child.Message.Any(char.IsControl) ||
            (child.Phase == UploadBatchItemPhase.BatchItemSucceeded) != (child.Identity.Length != 0) || !child.IsDirectory && child.Phase != UploadBatchItemPhase.BatchItemPending) throw new InvalidDataException("Invalid batch child ownership receipt.");
        StorageRules.ValidateName(child.Name);
    }
    private void LoadDirectoryReceipts(string[] paths)
    {
        var groups = new Dictionary<string, List<(uint Index, string Path)>>();
        var indexes = new Dictionary<string, DirectoryAttempts>();
        foreach (var (batch, row) in records)
        {
            if (row.Retiring || row.Retired) continue;
            var index = ReadAttempts(row);
            if (row.EffectsStarted && index is null || !row.EffectsStarted && index is not null && index.Bits.Any(x => x != 0)) throw new InvalidDataException("Batch child attempt ownership is missing or changed.");
            if (index is not null) indexes[batch] = index;
        }
        foreach (var path in paths)
        {
            var name = Path.GetFileName(path);
            if (name == "context.json" || name.EndsWith(".json", StringComparison.Ordinal) && UploadBatchRules.CanonicalToken(name[..^5])) continue;
            if (IsAttemptsName(name)) { var id = name[..^".children.json".Length]; if (!indexes.ContainsKey(id) && (!records.TryGetValue(id, out var owner) || !owner.Retiring)) throw new InvalidDataException("An orphaned batch child index was kept."); continue; }
            if (WindowsBatchUploadJournals.ParseName(name, out _, out _)) continue;
            if (!TryChildName(name, out var batch, out var index)) throw new InvalidDataException("Unexpected batch metadata filename.");
            if (!groups.TryGetValue(batch, out var group)) groups[batch] = group = [];
            group.Add((index, path));
        }
        foreach (var (batch, pathsForBatch) in groups)
        {
            if (!records.TryGetValue(batch, out var row) || !row.EffectsStarted || pathsForBatch.Count > Header(row).ItemCount) throw new InvalidDataException("Orphaned batch child receipts were kept for review.");
            if (row.Retiring) continue;
            if (row.Retired) throw new InvalidDataException("A compact receipt has unexpected child records.");
            var manifest = AcceptedManifest(row, out _); var children = new Dictionary<uint, DirectoryChild>(); long bytes = 0;
            foreach (var (index, path) in pathsForBatch)
            {
                var child = StateRecovery.Read<DirectoryChild>(path, ChildRecordLimit); ValidateChild(row, manifest, child);
                if (child.Index != index || !children.TryAdd(index, child)) throw new InvalidDataException("Ambiguous batch child receipt.");
                if (child.Phase == UploadBatchItemPhase.BatchItemSucceeded && !Attempted(indexes[batch], index)) throw new InvalidDataException("A successful child lost its durable attempt marker.");
                bytes += new FileInfo(path).Length; if (bytes > ChildMetadataLimit) throw new InvalidDataException("Batch child metadata exceeds its limit.");
            }
            foreach (var child in children.Values)
            {
                var parent = manifest.Items[(int)child.Index].Parent;
                var expected = parent < 0 ? row.Target.Identity : children.TryGetValue((uint)parent, out var ancestor) && ancestor.Phase == UploadBatchItemPhase.BatchItemSucceeded ? ancestor.Identity : "";
                if (expected.Length == 0 || expected != child.ParentIdentity) throw new InvalidDataException("A batch child lost its recorded parent identity.");
                if (!child.IsDirectory)
                {
                    if (!Attempted(indexes[batch], child.Index)) throw new InvalidDataException("A file child lost its attempt marker.");
                    var target = LogicalFileTarget(row, manifest, child, children);
                    WindowsUploads.ValidateScopedJournal(FileScope(row, manifest, child, target, false), context.Owner);
                }
            }
            directoryBytes[batch] = bytes;
            for (uint item = 0; item < Header(row).ItemCount; item++) if (Attempted(indexes[batch], item) && !children.ContainsKey(item)) throw new InvalidDataException("A started batch item lost its retained receipt.");
        }
        foreach (var (batch, index) in indexes) if (index.Bits.Any(x => x != 0) && !groups.ContainsKey(batch)) throw new InvalidDataException("Batch child receipts are missing. Creation will not be replayed.");
        foreach (var path in paths)
        {
            if (!WindowsBatchUploadJournals.ParseName(Path.GetFileName(path), out var batch, out var index)) continue;
            if (records.TryGetValue(batch, out var retiring) && retiring.Retiring) continue;
            if (!records.TryGetValue(batch, out var row) || ReadDirectoryChild(row, AcceptedManifest(row, out _), index) is not { IsDirectory: false }) throw new InvalidDataException("An orphaned batch file journal was kept.");
        }
    }
    private DirectoryChild? ReadDirectoryChild(Row row, UploadBatchManifest manifest, uint index)
    {
        try
        {
            var child = StateRecovery.Read<DirectoryChild>(Path.Combine(directory, ChildName(Header(row).Id, index)), ChildRecordLimit);
            ValidateChild(row, manifest, child); if (child.Index != index) throw new InvalidDataException("The batch item index changed."); return child;
        }
        catch (StorageException ex) when (ex.NativeError is 2 or 3) { return null; }
        catch (Exception ex) when (StateRecovery.IsRecordFailure(ex))
        { recovery.Block("A retained batch child receipt is damaged. Existing copies were kept; review this history locally."); Changed?.Invoke(); throw new StorageException(FileStatus.Conflict, "The retained batch child receipt could not be verified."); }
    }
    private void SaveDirectoryChild(Row row, DirectoryChild child)
    {
        var batch = Header(row).Id; var name = ChildName(batch, child.Index); var file = Path.Combine(directory, name);
        var previous = File.Exists(file) ? new FileInfo(file).Length : 0; var bytes = JsonSerializer.SerializeToUtf8Bytes(child).Length;
        var total = directoryBytes.GetValueOrDefault(batch) - previous + bytes;
        if (bytes > ChildRecordLimit || total > ChildMetadataLimit) throw new StorageException(FileStatus.ResourceExhausted, "Batch child receipt storage is full.");
        Save(name, child, ChildRecordLimit); directoryBytes[batch] = total; Changed?.Invoke();
    }
    private sealed class DirectoryParent(List<SecureWindowsDirectory> held, string[] path) : IDisposable
    {
        public SecureWindowsDirectory Directory => held[^1];
        public string[] Path { get; } = path;
        public SecureWindowsDirectory Detach() => SecureWindowsDirectory.TakeHeldPath(held);
        public void Dispose() { for (var i = held.Count - 1; i >= 0; i--) held[i].Dispose(); }
    }
    private DirectoryParent OpenDirectoryParent(Row row, UploadBatchManifest manifest, uint index)
    {
        var chain = new Stack<DirectoryChild>(); var parent = manifest.Items[(int)index].Parent;
        while (parent >= 0)
        {
            var receipt = ReadDirectoryChild(row, manifest, (uint)parent);
            if (receipt?.Phase != UploadBatchItemPhase.BatchItemSucceeded) throw new StorageException(FileStatus.Conflict, "A batch parent folder has no confirmed creation receipt.");
            chain.Push(receipt); parent = manifest.Items[parent].Parent;
        }
        var held = new List<SecureWindowsDirectory>(); var segments = row.Target.Path.ToList();
        try
        {
            var current = SecureWindowsDirectory.OpenPath(row.Target.CanonicalRoot, row.Target.RootIdentity, row.Target.Path, true); held.Add(current);
            if (current.Info.Identity != row.Target.Identity) throw new StorageException(FileStatus.StaleReference, "The original batch destination was replaced.");
            var grant = grants.Get(row.Target.Grant, row.Peer);
            foreach (var child in chain)
            {
                if (child.ParentIdentity != current.Info.Identity) throw new StorageException(FileStatus.StaleReference, "A batch directory's recorded parent changed.");
                segments.Add(child.Name); grants.ValidateAccess(grant, segments);
                var next = current.OpenChild(child.Name, true); held.Add(next);
                if (next.Info.Identity != child.Identity) throw new StorageException(FileStatus.StaleReference, "A created batch parent was replaced. Existing folders were kept.");
                current = next;
            }
            return new(held, segments.ToArray());
        }
        catch { for (var i = held.Count - 1; i >= 0; i--) held[i].Dispose(); throw; }
    }
    private static UploadBatchItemReceipt ChildInfo(Row row, DirectoryChild child) => new()
    { Header = Header(row), Index = child.Index, Id = child.Id, IsDirectory = true, Phase = child.Phase,
        ResultingName = child.Phase == UploadBatchItemPhase.BatchItemSucceeded ? child.Name : "", Message = child.Message };
    private UploadBatchItemReceipt DirectoryOperation(Row row, string peer, string pairing, FileRequest request, CancellationToken ct)
    {
        if (!request.HasBatchItemIndex || request.BatchItemIndex >= Header(row).ItemCount) throw new InvalidDataException("Choose a valid batch item index.");
        if (row.Phase != UploadBatchPlanPhase.UploadPlanReady) throw new StorageException(FileStatus.Conflict, "Only an accepted, uncanceled plan can create or recover child items.");
        var manifest = AcceptedManifest(row, out var encoded); var index = request.BatchItemIndex; var item = manifest.Items[(int)index];
        if (!item.IsDirectory || item.Error.Length != 0) throw new StorageException(FileStatus.Unsupported, "This planned item is not a usable directory.");
        using var lease = OpenDirectoryParent(row, manifest, index); var parent = lease.Directory; var grant = grants.Get(row.Target.Grant, peer);
        var attempts = ReadAttempts(row);
        if (row.EffectsStarted && attempts is null) { recovery.Block("Batch attempt ownership needs review. Destination items were kept; child creation is blocked."); throw new IOException("The batch child attempt index is missing."); }
        var previous = ReadDirectoryChild(row, manifest, index);
        if (previous is null && attempts is not null && Attempted(attempts, index))
        { recovery.Block("A started batch item lost its receipt. Existing destination items were kept; creation is blocked."); throw new IOException("The retained batch child receipt is missing."); }
        if (previous?.Phase == UploadBatchItemPhase.BatchItemSucceeded && (attempts is null || !Attempted(attempts, index)))
        { recovery.Block("A successful child lost its attempt ownership marker. Existing destination items were kept."); throw new IOException("The batch item ownership marker changed."); }
        if (previous is not null)
        {
            if (previous.ParentIdentity != parent.Info.Identity) throw new StorageException(FileStatus.StaleReference, "The batch item's recorded parent changed.");
            if (previous.Phase == UploadBatchItemPhase.BatchItemPending)
            { previous = previous with { Phase = UploadBatchItemPhase.BatchItemReview, Message = "This folder may already exist. Review the destination; uncertain creation is never replayed." }; SaveDirectoryChild(row, previous); }
            if (previous.Phase == UploadBatchItemPhase.BatchItemSucceeded)
            {
                grants.ValidateAccess(grant, [.. lease.Path, previous.Name]);
                try { using var current = parent.OpenChild(previous.Name, true); if (current.Info.Identity != previous.Identity) throw new IOException(); }
                catch (IOException) { throw new StorageException(FileStatus.StaleReference, "The recorded batch folder moved or was replaced. Existing items were kept."); }
            }
            return ChildInfo(row, previous);
        }
        if (request.Operation == FileOperation.UploadBatchItemStatus) throw new StorageException(FileStatus.NotFound, "No child receipt exists. Status does not create a directory.");
        if (directoryBytes.GetValueOrDefault(Header(row).Id) > ChildMetadataLimit - ChildRecordLimit) throw new StorageException(FileStatus.ResourceExhausted, "Batch child receipt capacity is full.");
        grants.ValidateAccess(grant, [.. lease.Path, item.Name]); Authorize(row, peer, pairing);
        if (!row.EffectsStarted)
        {
            var bits = new byte[(Header(row).ItemCount + 7) / 8];
            attempts ??= new(1, Header(row).Id, Convert.ToHexStringLower(Header(row).Sha256.Span), bits, AttemptsChecksum(Header(row), bits));
            if (attempts.Bits.Any(x => x != 0)) throw new InvalidDataException("The batch attempt state changed before its first effect.");
            Save(AttemptsName(Header(row).Id), attempts, 4096); row = row with { EffectsStarted = true }; Put(row, encoded);
        }
        var blind = (grant.Permissions & StoragePermission.List) == 0; var id = UploadBatchRules.ChildId(peer, Header(row), (int)index, true);
        for (var attempt = 1; attempt <= 1024; attempt++)
        {
            ct.ThrowIfCancellationRequested(); var name = blind ? DirectoryName(item.Name, " (" + ProtocolRules.NewToken()[..16] + ")") : attempt == 1 ? item.Name : DirectoryName(item.Name, " (" + attempt.ToString(CultureInfo.InvariantCulture) + ")");
            grants.ValidateAccess(grant, [.. lease.Path, name]);
            var child = new DirectoryChild(1, index, id, name, parent.Info.Identity, "", UploadBatchItemPhase.BatchItemPending, "The directory creation intent is durable.");
            SaveDirectoryChild(row, child);
            if (!Attempted(attempts!, index))
            {
                attempts!.Bits[index / 8] |= (byte)(1 << (int)(index % 8));
                attempts = attempts with { Checksum = AttemptsChecksum(Header(row), attempts.Bits) };
                Save(AttemptsName(Header(row).Id), attempts, 4096);
            }
            try
            {
                using var created = parent.CreateDirectory(name);
                child = child with { Identity = created.Info.Identity, Phase = UploadBatchItemPhase.BatchItemSucceeded, Message = "A new batch directory was created and its identity retained." };
                SaveDirectoryChild(row, child); return ChildInfo(row, child);
            }
            catch (StorageException ex) when (ex.NativeError is 80 or 183)
            {
                if (blind || manifest.Collision == UploadCollision.UploadKeepBoth) continue;
                child = child with { Phase = manifest.Collision == UploadCollision.UploadSkip ? UploadBatchItemPhase.BatchItemSkipped : UploadBatchItemPhase.BatchItemFailed,
                    Message = "An existing destination was kept. Batch folders are never merged." };
                SaveDirectoryChild(row, child); return ChildInfo(row, child);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (recovery.Needed) throw;
                child = child with { Phase = UploadBatchItemPhase.BatchItemReview, Message = "Directory creation needs review. A possible destination item was kept and will not be created again automatically." };
                SaveDirectoryChild(row, child); return ChildInfo(row, child);
            }
        }
        throw new StorageException(FileStatus.ResourceExhausted, "Too many colliding batch directory names. Review the destination.");
    }
    private static string DirectoryName(string name, string suffix)
    { var stem = name[..Math.Min(name.Length, 255 - suffix.Length)].TrimEnd(' ', '.'); if (stem.Length != 0 && char.IsHighSurrogate(stem[^1])) stem = stem[..^1]; return stem + suffix; }
}
