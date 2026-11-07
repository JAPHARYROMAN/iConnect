using System.Text.Json;
using IConnect.Core;
using IConnect.Storage.Windows;

namespace IConnect.AppModel;

public sealed record DesktopSettings(string DeviceName, string Coordinator = "", string Stun = "", bool SharePresence = false);
public sealed record ActivityEntry(string Id, long At, string Category, string Description, string? PeerId);

public static class LocalState
{
    public static T? Read<T>(string path, int maximumBytes)
    {
        if (!File.Exists(path)) return default;
        if (new FileInfo(path).Length > maximumBytes) throw new InvalidDataException("Local settings exceed their limit.");
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? throw new InvalidDataException("Local settings are invalid.");
    }
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var file = new FileStream(path + ".new", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, value, new JsonSerializerOptions { WriteIndented = true });
            file.Flush(true);
        }
        AtomicStateFile.Commit(path + ".new", path);
    }
    public static void Validate(DesktopSettings settings)
    {
        if (settings.DeviceName is null || settings.DeviceName.Length is < 1 or > 64 || settings.DeviceName.Any(char.IsControl))
            throw new InvalidDataException("Choose a device name between 1 and 64 characters.");
        if (settings.Coordinator is null || settings.Coordinator.Length > 2048) throw new InvalidDataException("Choose a valid coordinator address.");
        if (!string.IsNullOrWhiteSpace(settings.Coordinator)) SignalConnection.ValidateServer(new Uri(settings.Coordinator));
        ConnectionRules.Stun(settings.Stun);
    }
}

/// <summary>Bounded local history contains action descriptions, never invitations, codes or file content.</summary>
public sealed class LocalActivityStore
{
    private readonly object gate = new();
    private readonly string path;
    private List<ActivityEntry> entries = [];
    private sealed record Saved(int Schema, int Days, ActivityEntry[] Entries);
    private readonly string directory, identity;
    private readonly StateRecovery recovery;
    private string version = "";
    public int Days { get; private set; } = 30;
    public string Issue { get; private set; } = "";
    public bool CanReset { get { lock (gate) return recovery.Issues.Count != 0 && recovery.Issues.All(x => x.CanArchive); } }
    public static bool ValidDays(int value) => value is 0 or 1 or 7 or 30 or 90;
    public LocalActivityStore(string directory)
    {
        this.directory = directory; path = Path.Combine(directory, "activity.json"); Directory.CreateDirectory(directory); recovery = new(directory);
        using (var root = SecureWindowsDirectory.OpenRoot(directory)) identity = root.Info.Identity;
        try
        {
            using var root = Root();
            if (root.EnumerateNames(default).Any(x => x.Name is "activity.json.new" or "activity.json.bak")) throw new IOException("Activity has uncommitted metadata.");
            if (!File.Exists(path)) return;
            var saved = StateRecovery.Read<JsonElement>(path, 2 * 1024 * 1024);
            if (saved.ValueKind == JsonValueKind.Array) entries = saved.Deserialize<List<ActivityEntry>>() ?? throw new InvalidDataException();
            else { var value = saved.Deserialize<Saved>() ?? throw new InvalidDataException(); if (value.Schema != 2 || !ValidDays(value.Days) || value.Entries is null) throw new InvalidDataException(); Days = value.Days; entries = value.Entries.ToList(); }
            if (entries.Count > 500 || entries.Any(x => x is null || !ProtocolRules.TokenPattern().IsMatch(x.Id) ||
            x.At is < 0 or > StorageRules.MaxTimestamp || x.Category is null || x.Category.Length is < 1 or > 32 || x.Description is null || x.Description.Length > 256 ||
            x.Category.Any(char.IsControl) || x.Description.Any(char.IsControl) || x.PeerId is not null && (x.PeerId.Length != 64 || x.PeerId.Any(c => !char.IsAsciiHexDigitLower(c)))))
                throw new InvalidDataException("Local activity history is invalid.");
            using (var current = root.OpenChild("activity.json")) version = current.Info.Version;
            var retained = Retained(entries); if (retained.Count != entries.Count) Save(retained, Days);
        }
        catch (Exception ex) when (StateRecovery.IsRecordFailure(ex)) { entries = []; Block(); }
    }
    private SecureWindowsDirectory Root() { var root = SecureWindowsDirectory.OpenRoot(directory); if (root.Info.Identity != identity) { root.Dispose(); throw new IOException("Activity storage changed."); } return root; }
    private List<ActivityEntry> Retained(IEnumerable<ActivityEntry> values) => Days == 0 ? [] : values.Where(x => x.At >= DateTimeOffset.UtcNow.AddDays(-Days).ToUnixTimeMilliseconds()).TakeLast(500).ToList();
    private void Block() { if (Issue.Length != 0) return; Issue = "Local activity could not be saved or validated. Recent events may be missing. Preserve and reset its history before recording again."; recovery.Add(path, Issue); }
    private void Save(List<ActivityEntry> updated, int days)
    {
        try
        {
            if (Issue.Length != 0) throw new IOException(Issue);
            using var root = Root();
            if (version.Length != 0) { using var previous = root.OpenChild("activity.json"); if (previous.Info.Version != version) throw new IOException("Activity changed outside its owner."); }
            else if (File.Exists(path)) throw new IOException("Unreviewed activity metadata was kept.");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Saved(2, days, updated.ToArray()));
            if (bytes.Length > 2 * 1024 * 1024) throw new IOException("Activity exceeds its size limit.");
            using (var staged = root.CreateFile("activity.json.new")) { staged.Write(0, bytes); staged.SetLength(bytes.Length); staged.Flush(); }
            AtomicStateFile.Commit(path + ".new", path);
            using var current = root.OpenChild("activity.json"); version = current.Info.Version; entries = updated; Days = days;
        }
        catch (Exception ex) when (StateRecovery.IsRecordFailure(ex)) { Block(); throw new IOException(Issue, ex); }
    }
    public IReadOnlyList<ActivityEntry> All { get { lock (gate) return entries.AsEnumerable().Reverse().ToArray(); } }
    public void Add(string category, string description, string? peerId = null)
    {
        if (category.Length is < 1 or > 32 || description.Length > 256 || category.Any(char.IsControl) || description.Any(char.IsControl))
            throw new ArgumentException("Activity descriptions must be short plain text.");
        lock (gate)
        {
            if (Days == 0 && Issue.Length == 0) return;
            if (peerId is not null && (peerId.Length != 64 || peerId.Any(c => !char.IsAsciiHexDigitLower(c)))) peerId = null;
            var updated = Retained(entries).TakeLast(499).Append(new ActivityEntry(
                ProtocolRules.NewToken(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), category, description, peerId)).ToList();
            Save(updated, Days);
        }
    }
    public void Clear() { lock (gate) Save([], Days); }
    internal void AddObserved(IReadOnlyList<ActivityEntry> observed)
    {
        lock (gate)
        {
            if (Days == 0 && Issue.Length == 0) return;
            Save(Retained(entries.Concat(observed)), Days);
        }
    }
    public void Configure(int days)
    {
        if (!ValidDays(days)) throw new ArgumentException("Choose a supported activity-retention period.");
        lock (gate) { var previous = Days; List<ActivityEntry> retained; try { Days = days; retained = Retained(entries); } finally { Days = previous; } Save(retained, days); }
    }
    public void PreserveAndReset()
    {
        lock (gate)
        {
            if (Issue.Length == 0 || !CanReset) throw new IOException("Restore activity storage access and reopen iConnect before resetting its history.");
            using var root = Root(); foreach (var issue in recovery.Issues.ToArray()) recovery.Archive(issue.Id);
            Issue = ""; version = ""; entries = []; Save([], 30);
        }
    }
}
