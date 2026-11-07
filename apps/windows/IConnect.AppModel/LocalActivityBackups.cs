using System.Security.Cryptography;
using IConnect.Core;
using IConnect.Storage.Windows;

namespace IConnect.AppModel;

public sealed record ActivityBackupEntry(string Id, int Files, long Bytes, long ModifiedAt);
public sealed record ActivityBackupInventory(IReadOnlyList<ActivityBackupEntry> Entries, int Total, int KeptFolders);
public sealed record ActivityBackupRemovalReview(string Id, string Backup, int Files, long Bytes);

/// <summary>Owner-local, exact review of activity-only metadata archives. No live journal or transfer store is removed.</summary>
public sealed class LocalActivityBackups(LocalActivityStore store, Func<string> currentContext, Func<bool> available,
    Func<long>? clock = null) : IDisposable
{
    private const long MaximumFileBytes = 64L * 1024 * 1024;
    private readonly object workGate = new(), pendingGate = new();
    private sealed record Entry(string Name, string Version, long Bytes, string Hash);
    private sealed record Snapshot(string Shelf, string Archive, Entry[] Files);
    private sealed record Pending(ActivityBackupRemovalReview Value, string Context, long Revision, long Expires, Snapshot Snapshot);
    private Pending? pending;
    private long revision;
    private volatile bool disposed;
    private long Now => clock?.Invoke() ?? Environment.TickCount64;
    private static bool ActivityName(string name) => name is "activity.json" or "activity.json.new" or "activity.json.bak";
    private string Actor(Func<bool> continuing)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!available() || !continuing()) throw new OperationCanceledException("Keep the activity-backup review open to continue.");
        var context = currentContext();
        if (string.IsNullOrEmpty(context)) throw new IOException("The current local owner context is unavailable.");
        return context;
    }
    private void Check(string context, long generation, Func<bool> continuing)
    {
        if (Actor(continuing) != context || Volatile.Read(ref revision) != generation)
            throw new IOException("The activity-backup review lost its owner or screen context. Review it again.");
    }
    public void CancelReview()
    {
        Interlocked.Increment(ref revision);
        lock (pendingGate) pending = null;
    }
    private static SecureWindowsDirectory? Shelf(SecureWindowsDirectory root)
    {
        if (!root.EnumerateNames(default).Any(x => x.Name == "recovery")) return null;
        return root.OpenChild("recovery", true);
    }
    private static (string Name, bool IsDirectory)[] Names(SecureWindowsDirectory archive) =>
        archive.EnumerateNames(default).Take(4).Select(x => (x.Name, x.IsDirectory)).OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
    private static void RequireActivityFiles((string Name, bool IsDirectory)[] names)
    {
        if (names.Length is < 1 or > 3 || names.Any(x => x.IsDirectory || !ActivityName(x.Name)))
            throw new IOException("This backup contains empty or unrecognized storage. Its contents were kept.");
    }
    private static void RequireSize(long bytes)
    {
        if (bytes < 0 || bytes > MaximumFileBytes)
            throw new IOException("This activity backup exceeds the 64 MiB per-file review limit. It was kept for local inspection.");
    }
    public ActivityBackupInventory Inspect(int start, Func<bool>? continuing = null)
    {
        continuing ??= () => true;
        lock (workGate)
        {
            var context = Actor(continuing); var generation = Volatile.Read(ref revision);
            return store.WithActivityBackupProtection((root, protectedIds) =>
            {
                using var shelf = Shelf(root);
                var entries = new List<ActivityBackupEntry>(); var kept = 0;
                if (shelf is not null)
                {
                    var folders = shelf.EnumerateNames(default).Take(257).ToArray();
                    if (folders.Length > 256) throw new IOException("Recovery inventory exceeds its 256-folder limit. No backup was removed.");
                    foreach (var folder in folders)
                    {
                        Check(context, generation, continuing);
                        if (!folder.IsDirectory || !ProtocolRules.TokenPattern().IsMatch(folder.Name) || protectedIds.Contains(folder.Name)) { kept++; continue; }
                        try
                        {
                            using var archive = shelf.OpenChild(folder.Name, true); var names = Names(archive);
                            if (names.Length <= 3 && !names.Any(x => ActivityName(x.Name))) continue;
                            RequireActivityFiles(names);
                            archive.RequireOnlyDefaultDataStream();
                            long bytes = 0, modified = 0;
                            foreach (var name in names)
                            {
                                using var file = archive.OpenChild(name.Name); RequireSize(file.Info.Size); file.RequireOnlyDefaultDataStream();
                                bytes = checked(bytes + file.Info.Size); modified = Math.Max(modified, file.Info.ModifiedAt);
                            }
                            entries.Add(new(folder.Name, names.Length, bytes, modified));
                        }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { kept++; }
                    }
                }
                Check(context, generation, continuing);
                if (start < 0 || start > entries.Count) throw new ArgumentOutOfRangeException(nameof(start));
                return new ActivityBackupInventory(entries.OrderByDescending(x => x.ModifiedAt).ThenBy(x => x.Id, StringComparer.Ordinal).Skip(start).Take(50).ToArray(), entries.Count, kept);
            });
        }
    }
    private Entry Read(SecureWindowsDirectory file, string name, string context, long generation, Func<bool> continuing)
    {
        RequireSize(file.Info.Size);
        file.RequireOnlyDefaultDataStream();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var bytes = new byte[64 * 1024]; long offset = 0;
        while (offset < file.Info.Size)
        {
            Check(context, generation, continuing);
            var count = RandomAccess.Read(file.Handle, bytes.AsSpan(0, (int)Math.Min(bytes.Length, file.Info.Size - offset)), offset);
            if (count == 0) throw new IOException("The activity backup changed while being reviewed.");
            hash.AppendData(bytes, 0, count); offset += count;
        }
        Check(context, generation, continuing);
        if (SecureWindowsDirectory.Inspect(file.Handle).Version != file.Info.Version)
            throw new IOException("The activity backup changed while being reviewed.");
        file.RequireOnlyDefaultDataStream();
        return new(name, file.Info.Version, file.Info.Size, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }
    public ActivityBackupRemovalReview Review(string backup, Func<bool>? continuing = null)
    {
        continuing ??= () => true;
        lock (workGate)
        {
            CancelReview(); var context = Actor(continuing); var generation = Volatile.Read(ref revision);
            if (!ProtocolRules.TokenPattern().IsMatch(backup)) throw new ArgumentException("Choose a current activity backup.");
            return store.WithActivityBackupProtection((root, protectedIds) =>
            {
                if (protectedIds.Contains(backup)) throw new IOException("This backup is still needed by current recovery. Finish that review first.");
                using var shelf = Shelf(root) ?? throw new IOException("The activity backup is no longer available.");
                using var archive = shelf.OpenChild(backup, true); var names = Names(archive); RequireActivityFiles(names);
                archive.RequireOnlyDefaultDataStream();
                var files = new List<Entry>();
                foreach (var name in names) { using var file = archive.OpenChild(name.Name); files.Add(Read(file, name.Name, context, generation, continuing)); }
                if (!names.SequenceEqual(Names(archive))) throw new IOException("The activity backup inventory changed. Review it again.");
                var value = new ActivityBackupRemovalReview(ProtocolRules.NewToken(), backup, files.Count, files.Sum(x => x.Bytes));
                Check(context, generation, continuing);
                lock (pendingGate)
                {
                    Check(context, generation, continuing);
                    pending = new(value, context, generation, checked(Now + 120_000), new(shelf.Info.Identity, archive.Info.Identity, files.ToArray()));
                }
                return value;
            });
        }
    }
    public void Confirm(ActivityBackupRemovalReview value, Func<bool>? continuing = null)
    {
        continuing ??= () => true;
        lock (workGate)
        {
            Pending? captured; lock (pendingGate) { captured = pending; pending = null; }
            if (captured is null || captured.Value != value || captured.Expires < Now)
                throw new IOException("This activity-backup review changed, expired or was already used. Review it again.");
            bool ContinuingReview()
            {
                var active = continuing();
                if (captured.Expires < Now) throw new IOException("This activity-backup review expired. Review remaining metadata again.");
                return active;
            }
            Check(captured.Context, captured.Revision, ContinuingReview);
            store.WithActivityBackupProtection((root, protectedIds) =>
            {
                if (protectedIds.Contains(value.Backup)) throw new IOException("This backup is needed by current recovery. It was kept.");
                using var shelf = Shelf(root) ?? throw new IOException("The activity backup is no longer available.");
                using var archive = SecureWindowsDirectory.OpenPath(shelf.Info.CanonicalPath, shelf.Info.Identity, [value.Backup], true, mutation: true);
                if (shelf.Info.Identity != captured.Snapshot.Shelf || archive.Info.Identity != captured.Snapshot.Archive)
                    throw new IOException("The activity backup location changed. Its contents were kept.");
                archive.RequireOnlyDefaultDataStream();
                var expected = captured.Snapshot.Files; var names = expected.Select(x => (x.Name, IsDirectory: false)).ToArray();
                if (!Names(archive).SequenceEqual(names)) throw new IOException("The activity backup inventory changed. Its contents were kept.");
                var held = new List<SecureWindowsDirectory>(); var removed = 0;
                try
                {
                    // Acquire and verify every mutation handle before deleting any file.
                    foreach (var entry in expected)
                    {
                        var file = archive.OpenMutableChild(entry.Name);
                        held.Add(file);
                        if (Read(file, entry.Name, captured.Context, captured.Revision, ContinuingReview) != entry)
                            throw new IOException("The reviewed activity bytes or native identity changed. Its contents were kept.");
                    }
                    if (!Names(archive).SequenceEqual(names)) throw new IOException("The activity backup inventory changed. Its contents were kept.");
                    foreach (var file in held)
                    {
                        Check(captured.Context, captured.Revision, ContinuingReview); file.RequireOnlyDefaultDataStream();
                        Check(captured.Context, captured.Revision, ContinuingReview); file.Delete(); file.Dispose(); removed++;
                    }
                    Check(captured.Context, captured.Revision, ContinuingReview);
                    if (archive.EnumerateNames(default).Any()) throw new IOException("New backup contents were kept for a fresh review.");
                    archive.RequireOnlyDefaultDataStream();
                    Check(captured.Context, captured.Revision, ContinuingReview);
                    archive.Delete(); return true;
                }
                catch (Exception error) when (removed > 0 && error is IOException or UnauthorizedAccessException)
                { throw new IOException($"Cleanup stopped after removing {removed} reviewed metadata files. Inspect the remaining backup again.", error); }
                finally { foreach (var file in held) file.Dispose(); }
            });
        }
    }
    public void Dispose() { disposed = true; CancelReview(); }
}
