using IConnect.Core;
using IConnect.Protocol;
using System.Text.Json;

namespace IConnect.Storage.Windows;

public sealed record LocalFileLocation(string Id, string Label, string Path, string CanonicalPath, string Identity);
internal interface ILocalFileAuthority : IWindowsFileAuthority { }

/// <summary>Opaque owner-selected source. Only the local explorer can construct it.</summary>
public sealed record LocalCopySelection
{
    internal StorageGrant Grant { get; }
    internal string[] Segments { get; }
    internal string Identity { get; }
    internal string Version { get; }
    public string Name { get; }
    public long Size { get; }
    public bool IsDirectory { get; }
    public string SourceLocation { get; }
    internal LocalCopySelection(StorageGrant grant, string[] segments, string identity, FileEntry entry)
    { Grant = grant; Segments = segments.ToArray(); Identity = identity; Version = entry.Version; Name = entry.Name; Size = entry.Size; IsDirectory = entry.IsDirectory;
        SourceLocation = System.IO.Path.Combine(new[] { grant.DisplayPath }.Concat(segments).ToArray()); }
}

/// <summary>Owner-only local shortcuts. This store is never supplied to a network session.</summary>
public sealed class LocalFileLocations : IDisposable
{
    private sealed class Authority : ILocalFileAuthority
    {
        private readonly string path;
        private readonly object gate = new();
        private LocalFileLocation[] locations;
        internal readonly string Owner = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        public event Action? Changed;
        public Authority(string directory)
        {
            Directory.CreateDirectory(directory); path = System.IO.Path.Combine(directory, "local-locations.json");
            locations = [];
            if (File.Exists(path))
            {
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                if (input.Length > 256 * 1024) throw new InvalidDataException("Local folder shortcuts need repair.");
                locations = JsonSerializer.Deserialize<LocalFileLocation[]>(input) ?? throw new InvalidDataException("Local folder shortcuts need repair.");
            }
            if (locations.Length > 32 || locations.Select(x => x.Id).Distinct().Count() != locations.Length) throw new InvalidDataException("Local folder shortcuts need repair.");
            foreach (var row in locations)
                if (!ProtocolRules.TokenPattern().IsMatch(row.Id) || row.Label.Length is < 1 or > 80 || row.Label.Any(char.IsControl) ||
                    row.Path.Length is < 3 or > 8192 || row.CanonicalPath.Length is < 3 or > 8192 || row.Identity.Length is < 1 or > 256)
                    throw new InvalidDataException("Local folder shortcuts need repair.");
            foreach (var row in locations)
                if (!LocalDrivePath(row.Path) || !LocalVolumePath(row.CanonicalPath))
                    throw new InvalidDataException("Local shortcuts must reference normal local drives or folders.");
        }
        private static bool LocalDrivePath(string value) => value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] == '\\';
        private static bool LocalVolumePath(string value)
        {
            const string prefix = "\\\\?\\Volume{";
            return value.Length >= prefix.Length + 38 && value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                Guid.TryParseExact(value.AsSpan(prefix.Length, 36), "D", out _) && value[prefix.Length + 36] == '}' && value[prefix.Length + 37] == '\\' &&
                value[(prefix.Length + 38)..].Split('\\', StringSplitOptions.RemoveEmptyEntries).All(x => x is not ("." or "..") && !x.Any(char.IsControl) && !x.Contains(':') && !x.Contains('/'));
        }
        internal LocalFileLocation[] All { get { lock (gate) return locations.ToArray(); } }
        public T WithAuthorization<T>(Func<T> operation) { lock (gate) return operation(); }
        internal LocalFileLocation Add(string selectedPath)
        {
            var full = System.IO.Path.GetFullPath(selectedPath);
            if (full.Length > 8192 || full.Length < 3 || !char.IsAsciiLetter(full[0]) || full[1] != ':' || full[2] != '\\')
                throw new IOException("Choose a normal local drive or folder.");
            if (new DriveInfo(System.IO.Path.GetPathRoot(full)!).DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.CDRom or DriveType.Ram))
                throw new IOException("Choose a folder on a local drive. Network and unavailable drives are not supported.");
            using var root = SecureWindowsDirectory.OpenRoot(full);
            var label = System.IO.Path.GetFileName(full.TrimEnd('\\')); if (label.Length == 0) label = full;
            label = new string(label.Where(c => !char.IsControl(c)).Take(80).ToArray()); if (label.Length == 0) label = "Local folder";
            LocalFileLocation result;
            lock (gate)
            {
                var old = locations.FirstOrDefault(x => x.CanonicalPath.Equals(root.Info.CanonicalPath, StringComparison.OrdinalIgnoreCase));
                if (old is null && locations.Length >= 32) throw new IOException("Remove a local shortcut before adding another. At most 32 are saved.");
                result = new(old?.Id ?? ProtocolRules.NewToken(), label, full, root.Info.CanonicalPath, root.Info.Identity);
                Save(locations.Where(x => x.Id != result.Id).Append(result).ToArray());
            }
            Changed?.Invoke(); return result;
        }
        internal void Remove(string id)
        { lock (gate) Save(locations.Where(x => x.Id != id).ToArray()); Changed?.Invoke(); }
        private void Save(LocalFileLocation[] updated)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(updated);
            if (bytes.Length > 256 * 1024) throw new IOException("The local shortcut record is full. Remove an old shortcut before adding another.");
            using (var file = new FileStream(path + ".new", FileMode.Create, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
            AtomicStateFile.Commit(path + ".new", path); locations = updated;
        }
        public IReadOnlyList<StorageGrant> ForPeer(string peer) => peer == Owner ? All.Select(Grant).ToArray() : [];
        public StorageGrant Get(string id, string peer) => ForPeer(peer).SingleOrDefault(x => x.Id == id)
            ?? throw new StorageException(FileStatus.AccessDenied, "This local folder shortcut was removed.");
        private StorageGrant Grant(LocalFileLocation row) => new(row.Id, Owner, row.Label, row.Path, row.CanonicalPath, row.Identity,
            StoragePermission.ReadOnly, [], null);
        public void ValidateLocation(StorageGrant grant, IReadOnlyList<string> segments)
        {
            if (segments.Count > 128) throw new StorageException(FileStatus.Unsupported, "This folder is too deep to browse.");
            foreach (var segment in segments) StorageRules.ValidateName(segment);
        }
    }
    private readonly Authority authority;
    private readonly WindowsFileService service;
    public LocalFileLocations(string profile) { authority = new(profile); service = new(authority); }
    public IReadOnlyList<LocalFileLocation> All => authority.All;
    public LocalFileLocation Add(string path) => authority.Add(path);
    public void Remove(string id) => authority.Remove(id);
    public Task<LocalCopySelection> CaptureCopyAsync(FileEntry entry, CancellationToken ct = default)
        => service.CaptureLocalFileAsync(authority.Owner, entry.Clone(), ct);
    public Task<LocalCopySelection> CaptureBatchItemAsync(FileEntry entry, CancellationToken ct = default)
        => service.CaptureLocalItemAsync(authority.Owner, entry.Clone(), ct);
    public Task<FileResponse> RequestAsync(FileRequest value, CancellationToken ct)
    {
        if (value.Operation is not (FileOperation.ListRoots or FileOperation.ListDirectory or FileOperation.CloseListing or FileOperation.GetMetadata or FileOperation.ReadRange))
            throw new InvalidOperationException("The local browser only inspects files. Sharing requires a separate device approval.");
        var request = value.Clone(); request.RequestId = ProtocolRules.NewToken(); return service.ExecuteAsync(authority.Owner, request, ct);
    }
    public void Dispose() => service.Dispose();
}
