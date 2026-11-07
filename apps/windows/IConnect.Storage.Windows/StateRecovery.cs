using IConnect.Core;
using System.Text.Json;

namespace IConnect.Storage.Windows;

public sealed record StateRecoveryIssue(string Id, string Record, string Message, bool CanArchive);

/// <summary>Preserves unreadable private progress metadata. It never opens a transfer destination.</summary>
public sealed class StateRecovery(string directory)
{
    private sealed record Stamp(string Name, string Identity, long Size, long Written);
    private sealed record Issue(StateRecoveryIssue Public, Stamp[] Files);
    private readonly Dictionary<string, Issue> issues = [];
    public IReadOnlyList<StateRecoveryIssue> Issues => issues.Values.Select(x => x.Public).ToArray();
    public bool Needed => issues.Count != 0;
    public void Block(string message)
    { var id = ProtocolRules.NewToken(); issues.Add(id, new(new(id, "Queue storage", message, false), [])); }
    public int ArchivedCount => Directory.Exists(Path.Combine(directory, "recovery")) ? Directory.EnumerateDirectories(Path.Combine(directory, "recovery")).Take(257).Count() : 0;
    public void ClearArchives()
    {
        if (!Directory.Exists(Path.Combine(directory, "recovery"))) return;
        using var root = SecureWindowsDirectory.OpenRoot(directory); using var shelf = root.OpenChild("recovery", true);
        foreach (var folder in shelf.EnumerateNames(default).Take(257).ToArray())
        {
            if (issues.ContainsKey(folder.Name)) continue;
            if (!folder.IsDirectory || !ProtocolRules.TokenPattern().IsMatch(folder.Name)) throw new IOException("Recovery storage contains an unexpected item. Review it before removing backups.");
            using var archive = SecureWindowsDirectory.OpenPath(shelf.Info.CanonicalPath, shelf.Info.Identity, [folder.Name], true, mutation: true);
            var names = archive.EnumerateNames(default).Take(5).ToArray();
            if (names.Length > 3 || names.Any(x => x.IsDirectory || !MetadataName(x.Name))) throw new IOException("Recovery storage contains an unexpected item. It was kept.");
            foreach (var name in names)
            { using var file = archive.OpenMutableChild(name.Name); file.Delete(); }
            archive.Delete();
        }
    }
    private static bool MetadataName(string name) => name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".json.new", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".json.bak", StringComparison.OrdinalIgnoreCase);
    public static bool IsRecordFailure(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or
        InvalidOperationException or NullReferenceException or FormatException or KeyNotFoundException or NotSupportedException;
    public void RequireHealthy()
    { if (Needed) throw new IOException("Saved transfer progress needs review. Open Transfers on this device before starting or resuming work."); }
    public void Add(string path, string message)
    {
        var name = Path.GetFileName(path); var stamps = new List<Stamp>();
        try
        {
            using var root = SecureWindowsDirectory.OpenRoot(directory);
            foreach (var candidate in new[] { name, name + ".bak", name + ".new" })
            {
                if (!File.Exists(Path.Combine(directory, candidate))) continue;
                using var file = root.OpenChild(candidate); var info = file.Info;
                stamps.Add(new(candidate, info.Identity, info.Size, info.WriteTimestamp));
            }
        }
        catch (IOException) { stamps.Clear(); }
        catch (UnauthorizedAccessException) { stamps.Clear(); }
        var id = ProtocolRules.NewToken();
        issues.Add(id, new(new(id, new string(name.Where(c => !char.IsControl(c)).Take(100).ToArray()), message, stamps.Count > 0), stamps.ToArray()));
    }
    public void Archive(string id)
    {
        if (!issues.TryGetValue(id, out var issue)) return;
        if (!issue.Public.CanArchive) throw new IOException("Restore access to this progress record and restart the app before reviewing it.");
        using var root = SecureWindowsDirectory.OpenRoot(directory);
        foreach (var stamp in issue.Files.Where(x => File.Exists(Path.Combine(directory, x.Name))))
        {
            using var saved = SecureWindowsDirectory.OpenPath(root.Info.CanonicalPath, root.Info.Identity, [stamp.Name], false, readData: false);
            if (saved.Info.Identity != stamp.Identity || saved.Info.Size != stamp.Size || saved.Info.WriteTimestamp != stamp.Written)
                throw new IOException("The saved record changed. Restart the app and review its current state.");
        }
        using var shelf = Folder(root, "recovery");
        if (!Directory.Exists(Path.Combine(directory, "recovery", id)) && shelf.EnumerateNames(default).Take(257).Count() >= 256) throw new IOException("The recovery folder is full. Review the preserved records there before archiving more.");
        using var target = Folder(shelf, id);
        foreach (var stamp in issue.Files)
        {
            var path = Path.Combine(directory, stamp.Name);
            if (!File.Exists(path))
            {
                using var prior = target.OpenChild(stamp.Name);
                if (prior.Info.Identity != stamp.Identity) throw new IOException("The recovery record changed. Restart and review it again.");
                continue;
            }
            using var source = SecureWindowsDirectory.OpenPath(root.Info.CanonicalPath, root.Info.Identity, [stamp.Name], false, mutation: true, readData: false);
            if (source.Info.Identity != stamp.Identity || source.Info.Size != stamp.Size || source.Info.WriteTimestamp != stamp.Written)
                throw new IOException("The saved record changed. Restart the app and review its current state.");
            source.RenameTo(target, stamp.Name);
        }
        issues.Remove(id);
    }
    private static SecureWindowsDirectory Folder(SecureWindowsDirectory parent, string name)
    {
        try { return parent.CreateDirectory(name); }
        catch (StorageException ex) when (ex.NativeError is 80 or 183) { return parent.OpenChild(name, true); }
    }
    public static T Read<T>(string path, int maximum)
    {
        using var parent = SecureWindowsDirectory.OpenRoot(Path.GetDirectoryName(path)!);
        using var file = SecureWindowsDirectory.OpenPath(parent.Info.CanonicalPath, parent.Info.Identity, [Path.GetFileName(path)], false, readData: true);
        if (file.Info.Size > maximum) throw new InvalidDataException("Saved progress exceeds its size limit.");
        var bytes = new byte[checked((int)file.Info.Size)]; var offset = 0;
        while (offset < bytes.Length)
        {
            var read = System.IO.RandomAccess.Read(file.Handle, bytes.AsSpan(offset), offset);
            if (read == 0) throw new EndOfStreamException("The saved record changed while loading."); offset += read;
        }
        if (SecureWindowsDirectory.Inspect(file.Handle).Version != file.Info.Version) throw new InvalidDataException("The saved record changed while loading.");
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException("Saved progress is empty.");
    }
    public static string[] Journals(string directory, int maximum = 1024) => Directory.EnumerateFiles(directory).Where(x => x.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
        x.EndsWith(".json.new", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".json.bak", StringComparison.OrdinalIgnoreCase))
        .Select(x => x.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? x : x[..^4]).Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(maximum + 1).Order(StringComparer.OrdinalIgnoreCase).ToArray();
}
