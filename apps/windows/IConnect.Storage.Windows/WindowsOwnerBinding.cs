using IConnect.Core;
using IConnect.Protocol;
using System.Collections.Concurrent;
using System.Text.Json;

namespace IConnect.Storage.Windows;

/// <summary>Binds persisted remote authority to the local public identity and one owner generation.</summary>
public sealed class WindowsOwnerBinding
{
    private sealed record Stamp(int Schema, string OwnerId, string Epoch);
    private sealed record Envelope<T>(int Schema, string OwnerId, string Epoch, Dictionary<string, T> Records);
    private sealed class Shared(string owner)
    {
        internal readonly object Gate = new();
        internal readonly string Owner = owner;
        internal volatile bool Invalidated;
        internal volatile bool Blocked;
        internal string Epoch = "";
        internal string Issue = "";
        internal bool Loaded;
    }
    private static readonly ConcurrentDictionary<string, Shared> Profiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Shared shared;
    private readonly string directory;
    private readonly string stampPath;
    private readonly string intentPath;
    private readonly string volumePath;
    private readonly string volumeIdentity;
    private readonly string[] profileSegments;
    private readonly string profileIdentity;
    public string OwnerId => shared.Owner;
    public string ProfileDirectory => directory;
    public string PrivateCanonicalPath { get; }
    public string Epoch { get { lock (shared.Gate) return shared.Epoch; } }
    public bool Healthy => !shared.Invalidated && !shared.Blocked;
    public string Issue { get { lock (shared.Gate) return shared.Invalidated ? "This Windows identity changed. Reopen iConnect before reconnecting." : shared.Issue; } }
    public int PreservedRecords { get { lock (shared.Gate) return new StateRecovery(directory).ArchivedCount; } }
    public void ClearPreservedRecords() { lock (shared.Gate) new StateRecovery(directory).ClearArchives(); }

    public WindowsOwnerBinding(string profile, string ownId)
    {
        if (ownId.Length != 64 || ownId.Any(c => !char.IsAsciiHexDigitLower(c))) throw new InvalidDataException("Invalid local Windows identity.");
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile)); Directory.CreateDirectory(directory);
        var drive = Path.GetPathRoot(directory);
        if (drive is null || drive.Length != 3 || !char.IsAsciiLetter(drive[0]) || drive[1] != ':') throw new InvalidDataException("Choose an ordinary local drive folder for the Windows profile.");
        using (var volume = SecureWindowsDirectory.OpenRoot(drive))
        {
            volumePath = volume.Info.CanonicalPath; volumeIdentity = volume.Info.Identity;
            profileSegments = directory[drive.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            using var selected = SecureWindowsDirectory.OpenPrivatePath(volumePath, volumeIdentity, profileSegments);
            PrivateCanonicalPath = selected.Info.CanonicalPath; profileIdentity = selected.Info.Identity;
        }
        stampPath = Path.Combine(directory, "owner-binding.json"); intentPath = Path.Combine(directory, "owner-reset.json");
        shared = Profiles.AddOrUpdate(directory, _ => new(ownId), (_, prior) =>
        {
            if (prior.Owner == ownId && !prior.Invalidated) return prior;
            prior.Invalidated = true; return new(ownId);
        });
        lock (shared.Gate)
        {
            var latched = shared.Loaded && shared.Blocked;
            try
            {
                if (FamilyExists(intentPath)) throw new InvalidDataException("Owner-policy recovery was interrupted. Finish resetting trust and approvals locally.");
                if (!File.Exists(stampPath))
                {
                    if (FamilyExists(stampPath) || HasPriorAuthority())
                        throw new InvalidDataException("Earlier trust and storage approvals need owner review before they can be used with this Windows identity.");
                    var fresh = new Stamp(1, ownId, ProtocolRules.NewToken());
                    Write(intentPath, fresh);
                    Write(Path.Combine(directory, "peers.json"), new Envelope<object>(1, ownId, fresh.Epoch, []));
                    Write(Path.Combine(directory, "storage-grants.json"), new Envelope<object>(1, ownId, fresh.Epoch, []));
                    Write(stampPath, fresh); ClearIntent(); shared.Epoch = fresh.Epoch;
                }
                else
                {
                    var stamp = StateRecovery.Read<Stamp>(stampPath, 16 * 1024);
                    if (stamp.Schema != 1 || stamp.OwnerId != ownId || !ProtocolRules.TokenPattern().IsMatch(stamp.Epoch))
                        throw new InvalidDataException("Saved trust belongs to another Windows identity or owner generation. Pair and approve storage again after local recovery.");
                    shared.Epoch = stamp.Epoch;
                    if (!File.Exists(Path.Combine(directory, "peers.json")) || !File.Exists(Path.Combine(directory, "storage-grants.json")))
                        throw new InvalidDataException("An owner authority record is missing. Review trust and approvals before reconnecting.");
                }
                if (!latched) { shared.Blocked = false; shared.Issue = ""; }
            }
            catch (Exception ex) when (StateRecovery.IsRecordFailure(ex)) { Block(ex.Message); }
            finally { shared.Loaded = true; }
        }
    }
    private bool HasPriorAuthority()
    {
        foreach (var name in new[] { "peers.json", "storage-grants.json" })
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path + ".new") || Directory.Exists(path + ".new") || File.Exists(path + ".bak") || Directory.Exists(path + ".bak") || Directory.Exists(path)) return true;
            if (!File.Exists(path)) continue;
            var root = StateRecovery.Read<JsonElement>(path, 4 * 1024 * 1024);
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any()) return true;
        }
        return false;
    }
    private static bool FamilyExists(string path) => new[] { path, path + ".new", path + ".bak" }.Any(x => File.Exists(x) || Directory.Exists(x));
    private void Block(string reason) { shared.Blocked = true; shared.Issue = reason; }
    public void RejectMetadata(string reason) { lock (shared.Gate) Block(reason); }
    public void Require(string epoch)
    {
        if (!Healthy || epoch.Length == 0 || epoch != shared.Epoch)
            throw new StorageException(FileStatus.AccessDenied, "Windows trust and storage approvals need local owner review before remote access.");
    }
    public T Authorized<T>(string epoch, Func<T> operation)
    {
        lock (shared.Gate)
        {
            Require(epoch);
            using var profile = SecureWindowsDirectory.OpenPrivatePath(volumePath, volumeIdentity, profileSegments);
            if (profile.Info.Identity != profileIdentity || !profile.Info.CanonicalPath.Equals(PrivateCanonicalPath, StringComparison.OrdinalIgnoreCase))
                throw new StorageException(FileStatus.AccessDenied, "The private profile location changed. Reopen iConnect before accessing remote files.");
            var value = operation(); Require(epoch); return value;
        }
    }
    public void RequirePublicLocation(string canonicalPath)
    {
        if (SecureWindowsDirectory.IsWithin(canonicalPath, PrivateCanonicalPath))
            throw new StorageException(FileStatus.AccessDenied, "The iConnect profile and its recovery records are private and cannot be shared.");
    }
    public Dictionary<string, T> ReadRecords<T>(string path, int limit, string epoch)
    {
        lock (shared.Gate)
        {
            Require(epoch);
            try
            {
                if (!File.Exists(path)) throw new InvalidDataException("Missing owner authority needs review.");
                var root = StateRecovery.Read<JsonElement>(path, limit);
                if (root.ValueKind == JsonValueKind.Object && !root.EnumerateObject().Any()) return []; // Empty legacy data confers no authority.
                var saved = root.Deserialize<Envelope<T>>() ?? throw new InvalidDataException("Invalid owner authority.");
                if (saved.Schema != 1 || saved.OwnerId != OwnerId || saved.Epoch != epoch || saved.Records is null)
                    throw new InvalidDataException("Trust or approval metadata belongs to another owner generation.");
                return saved.Records;
            }
            catch (Exception ex) when (StateRecovery.IsRecordFailure(ex)) { Block(ex.Message); throw new StorageException(FileStatus.AccessDenied, "Saved Windows authority needs local owner recovery."); }
        }
    }
    public void SaveRecords<T>(string path, string epoch, Dictionary<string, T> records) => Authorized(epoch, () =>
    { Write(path, new Envelope<T>(1, OwnerId, epoch, records)); return true; });
    private static void Write<T>(string path, T value)
    {
        using (var output = new FileStream(path + ".new", FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(output, value); output.Flush(true); }
        AtomicStateFile.Commit(path + ".new", path);
    }
    private void ClearIntent()
    {
        using (var root = SecureWindowsDirectory.OpenRoot(directory))
            foreach (var name in new[] { "owner-reset.json", "owner-reset.json.new", "owner-reset.json.bak" })
                if (File.Exists(Path.Combine(directory, name))) { using var marker = root.OpenMutableChild(name); marker.Delete(); }
        if (FamilyExists(intentPath)) throw new IOException("The recovery barrier could not be cleared. Keep remote access paused and retry.");
    }
    /// <summary>Owner-confirmed metadata recovery. File contents, local browser shortcuts and receiving copies are kept.</summary>
    public void Reset()
    {
        shared.Blocked = true;
        lock (shared.Gate)
        {
            if (shared.Invalidated) throw new IOException("Reopen the profile with its current Windows identity before recovery.");
            shared.Issue = "Owner-policy recovery needs completion. Keep remote access paused and retry the local reset.";
            Write(intentPath, new Stamp(1, OwnerId, ProtocolRules.NewToken()));
            var recovery = new StateRecovery(directory);
            foreach (var name in new[] { "peers.json", "storage-grants.json", "owner-binding.json" })
            {
                var path = Path.Combine(directory, name);
                if (!FamilyExists(path)) continue;
                if (new[] { path, path + ".new", path + ".bak" }.Any(Directory.Exists)) throw new IOException("An authority record is a directory. Review local storage before retrying recovery.");
                recovery.Add(path, "Earlier owner authority was preserved before resetting trust and storage approvals.");
            }
            foreach (var issue in recovery.Issues.ToArray()) recovery.Archive(issue.Id);
            var epoch = ProtocolRules.NewToken();
            Write(Path.Combine(directory, "peers.json"), new Envelope<object>(1, OwnerId, epoch, []));
            Write(Path.Combine(directory, "storage-grants.json"), new Envelope<object>(1, OwnerId, epoch, []));
            Write(stampPath, new Stamp(1, OwnerId, epoch));
            ClearIntent();
            shared.Epoch = epoch; shared.Issue = ""; shared.Blocked = false;
        }
    }
}
