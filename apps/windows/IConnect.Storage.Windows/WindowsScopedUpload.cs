using IConnect.Core;
using IConnect.Protocol;

namespace IConnect.Storage.Windows;

// Constructed only by the accepted-plan host. Never derived from a standalone request.
internal sealed record WindowsScopedUpload(string JournalPath, string Id, string Peer, UploadInfo Descriptor,
    UploadDestination Destination, bool Fresh, Action Authorize, Func<SecureWindowsDirectory> Open,
    Action<byte[], bool> Persist, bool StopOnly = false);

/// <summary>Bounded private journals; no destination bytes or filesystem effects live here.</summary>
internal sealed class WindowsBatchUploadJournals(string directory, Action changed)
{
    internal const int RecordLimit = 256 * 1024, BatchLimit = 16 * 1024 * 1024;
    private sealed record Entry(long Bytes, string Version);
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = [];
    private readonly Dictionary<string, long> totals = [];
    private string identity = "";
    private int fault, notification;
    private readonly StateRecoveryIssue issue = new(ProtocolRules.NewToken(), "Batch file journals",
        "Batch file ownership needs local review. Partial and published files were kept; batch operations and new incoming copies are blocked.", false);
    internal IReadOnlyList<StateRecoveryIssue> Issues => Volatile.Read(ref fault) == 0 ? [] : [issue];
    internal void Block() { Interlocked.Exchange(ref fault, 1); Notify(); }
    internal void RequireHealthy() { if (Volatile.Read(ref fault) != 0) throw new StorageException(FileStatus.Conflict, issue.Message); }
    private void Notify()
    {
        if (Interlocked.Exchange(ref notification, 1) != 0) return;
        ThreadPool.QueueUserWorkItem(_ => { Interlocked.Exchange(ref notification, 0); changed(); });
    }
    internal static string Name(string batch, uint index) => batch + "." + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".upload.json";
    internal static bool ParseName(string name, out string batch, out uint index)
    {
        batch = ""; index = 0;
        if (!name.EndsWith(".upload.json", StringComparison.Ordinal)) return false;
        var stem = name[..^".upload.json".Length]; var split = stem.IndexOf('.');
        if (split < 0 || !UploadBatchRules.CanonicalToken(stem[..split]) ||
            !uint.TryParse(stem[(split + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out index) ||
            index >= UploadBatchRules.MaxItems || stem[(split + 1)..] != index.ToString(System.Globalization.CultureInfo.InvariantCulture)) return false;
        batch = stem[..split]; return true;
    }
    private SecureWindowsDirectory Root()
    {
        var root = SecureWindowsDirectory.OpenRoot(directory);
        if (root.Info.Identity != identity) { root.Dispose(); throw new IOException("The private batch journal directory changed."); }
        return root;
    }
    internal void Reset()
    {
        lock (gate) { using var root = SecureWindowsDirectory.OpenRoot(directory); identity = root.Info.Identity; entries.Clear(); totals.Clear(); Volatile.Write(ref fault, 0); }
    }
    internal void Load(IEnumerable<string> paths)
    {
        Reset(); lock (gate)
        {
            using var root = Root();
            foreach (var path in paths)
            {
                var name = Path.GetFileName(path); if (!ParseName(name, out var batch, out _)) continue;
                using var file = root.OpenChild(name); var info = file.Info;
                if (info.Size > RecordLimit || entries.Count >= WindowsBatchPlans.MaxDetailedPlans * UploadBatchRules.MaxItems) throw new InvalidDataException("Batch file journal capacity exceeded.");
                var total = totals.GetValueOrDefault(batch) + info.Size;
                if (total > BatchLimit || !entries.TryAdd(name, new(info.Size, info.Version))) throw new InvalidDataException("Batch file journal metadata exceeds its limit.");
                totals[batch] = total;
            }
        }
    }
    internal void Check(string name, bool fresh = false)
    {
        RequireHealthy(); lock (gate)
        {
            try
            {
                using var root = Root();
                if (fresh)
                {
                    if (entries.ContainsKey(name)) throw new IOException("An unexpected batch file journal already exists.");
                    try { using var existing = root.OpenChild(name); throw new IOException("An unowned batch journal was kept."); }
                    catch (StorageException ex) when (ex.NativeError is 2 or 3) { }
                }
                else
                {
                    if (!entries.TryGetValue(name, out var expected)) throw new IOException("The retained batch file journal is missing.");
                    using var file = root.OpenChild(name); if (file.Info.Version != expected.Version) throw new IOException("The batch file journal changed outside its owner.");
                }
            }
            catch (Exception ex) when (StateRecovery.IsRecordFailure(ex)) { Block(); throw new StorageException(FileStatus.Conflict, issue.Message); }
        }
    }
    internal void Reserve(string batch)
    { RequireHealthy(); lock (gate) if (totals.GetValueOrDefault(batch) > BatchLimit - RecordLimit) throw new StorageException(FileStatus.ResourceExhausted, "Batch file journal capacity is full. Review this queue locally."); }
    internal void ValidateRoot() { lock (gate) { RequireHealthy(); using var root = Root(); } }
    internal ReceivingPartProtection ReceivingPartProtection(string owner)
    {
        lock (gate)
        {
            RequireHealthy(); using var root = Root();
            var all = root.EnumerateNames(default).Take(1 + WindowsBatchPlans.MaxDetailedPlans * (UploadBatchRules.MaxItems * 2 + 2) + WindowsBatchPlans.MaxRetiredPlans + 2).ToArray();
            if (all.Length > 1 + WindowsBatchPlans.MaxDetailedPlans * (UploadBatchRules.MaxItems * 2 + 2) + WindowsBatchPlans.MaxRetiredPlans + 1)
                throw new IOException("Batch metadata exceeds its recovery scan limit. Its files were kept.");
            var names = all.Where(x => x.Name.EndsWith(".upload.json", StringComparison.OrdinalIgnoreCase) ||
                x.Name.EndsWith(".json.new", StringComparison.OrdinalIgnoreCase) || x.Name.EndsWith(".json.bak", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (names.Length != entries.Count || names.Any(x => x.IsDirectory || !entries.ContainsKey(x.Name)))
                throw new IOException("Batch file ownership changed or has uncommitted metadata. Review its history before partial-file cleanup.");
            var protection = new ReceivingPartProtection();
            try
            {
                protection.Bind(root.Info.Identity);
                foreach (var (name, expected) in entries.OrderBy(x => x.Key, StringComparer.Ordinal))
                {
                    using var file = root.OpenChild(name);
                    if (file.Info.Version != expected.Version) throw new IOException("A batch file journal changed. Its private and published files were kept.");
                    WindowsUploads.ProtectScopedReceivingParts(Path.Combine(directory, name), owner, protection);
                    protection.Bind(name); protection.Bind(expected.Version);
                }
                return protection;
            }
            catch { protection.Dispose(); throw; }
        }
    }
    internal void RemoveRetired(string name)
    {
        lock (gate)
        {
            Check(name); var saved = entries[name]; using var root = Root(); using var file = root.OpenMutableChild(name);
            if (file.Info.Version != saved.Version) throw new IOException("The reviewed file journal changed. It was kept.");
            file.Delete(); entries.Remove(name); ParseName(name, out var batch, out _); totals[batch] -= saved.Bytes;
        }
    }
    internal void Write(string name, byte[] bytes, bool allowNew, bool progressOnly)
    {
        RequireHealthy(); if (!ParseName(name, out var batch, out _) || bytes.Length > RecordLimit) throw new StorageException(FileStatus.ResourceExhausted, "The batch file journal exceeds its record limit.");
        lock (gate)
        {
            Check(name, allowNew && !entries.ContainsKey(name));
            var total = totals.GetValueOrDefault(batch) - (entries.GetValueOrDefault(name)?.Bytes ?? 0) + bytes.Length;
            if (total > BatchLimit) throw new StorageException(FileStatus.ResourceExhausted, "Batch file journal storage is full.");
            try
            {
                using var root = Root(); SecureWindowsDirectory staged;
                try { staged = root.CreateFile(name + ".new"); }
                catch (StorageException ex) when (ex.NativeError is 80 or 183) { staged = root.OpenWritableMetadataChild(name + ".new"); }
                using (staged) { staged.Write(0, bytes); System.IO.RandomAccess.SetLength(staged.Handle, bytes.Length); staged.Flush(); }
                AtomicStateFile.Commit(Path.Combine(directory, name + ".new"), Path.Combine(directory, name));
                using var current = root.OpenChild(name); entries[name] = new(bytes.Length, current.Info.Version); totals[batch] = total;
            }
            catch (Exception ex) when (StateRecovery.IsRecordFailure(ex)) { Block(); throw; }
        }
        if (!progressOnly) Notify();
    }
}
