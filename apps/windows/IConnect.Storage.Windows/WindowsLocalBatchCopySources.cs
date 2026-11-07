using Google.Protobuf;
using IConnect.Core;
using IConnect.Protocol;
using System.Security.Cryptography;

namespace IConnect.Storage.Windows;

public sealed partial class WindowsLocalBatchCopies
{
    private static Plan Scan(LocalCopySelection[] selections, CancellationToken ct)
    {
        var roots = new List<SourceRoot>(); var nodes = new List<Node>(); var identities = new HashSet<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long bytes = 0;
        foreach (var selected in selections)
        {
            ct.ThrowIfCancellationRequested(); StorageRules.ValidateName(selected.Name);
            if (!names.Add(selected.Name)) throw new IOException("Selected destination names collide. Plan them separately.");
            var rootIndex = roots.Count; var rootId = ProtocolRules.NewToken();
            if (selected.IsDirectory)
            {
                using var directory = SecureWindowsDirectory.OpenPath(selected.Grant.CanonicalPath, selected.Grant.RootIdentity, selected.Segments, true);
                RequireSelection(directory.Info, selected);
                roots.Add(new(rootId, directory.Info.CanonicalPath, directory.Info.Identity, directory.Info.Version, selected.Name, selected.SourceLocation, false));
                Visit(directory, rootId, rootIndex, -1, [], selected.Name);
            }
            else
            {
                using var parent = SecureWindowsDirectory.OpenPath(selected.Grant.CanonicalPath, selected.Grant.RootIdentity, selected.Segments[..^1], true);
                using var file = parent.OpenChild(selected.Segments[^1]); RequireSelection(file.Info, selected);
                roots.Add(new(rootId, parent.Info.CanonicalPath, parent.Info.Identity, parent.Info.Version, "Local selection", selected.SourceLocation, true));
                Visit(file, ProtocolRules.NewToken(), rootIndex, -1, [selected.Name], selected.Name);
            }
        }
        var plan = new Plan(3, ProtocolRules.NewToken(), roots.ToArray(), nodes.ToArray()); Validate(plan); return plan;

        void Visit(SecureWindowsDirectory item, string id, int root, int parent, string[] path, string name)
        {
            ct.ThrowIfCancellationRequested(); Bound(path); var info = item.Info;
            if (!identities.Add(info.Identity)) throw new IOException("Selections overlap or repeat the same native item. Select each item once.");
            if (!info.IsDirectory && (info.Size > StorageRules.MaxTransferSize || (bytes += info.Size) > 16L * 1024 * 1024 * 1024))
                throw new IOException("This local batch exceeds its supported source size.");
            var index = nodes.Count; nodes.Add(new(id, root, parent, path, name, info.IsDirectory, info.IsDirectory ? 0 : info.Size, info.Identity, info.Version));
            if (!info.IsDirectory) return;
            var children = item.EnumerateNames(ct).Take(TransferManifests.MaxItems - nodes.Count + 1).OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
            if (children.Length > TransferManifests.MaxItems - nodes.Count) throw new IOException("The folder exceeds 4,096 planned items.");
            foreach (var child in children)
            {
                ct.ThrowIfCancellationRequested(); StorageRules.ValidateName(child.Name); var childPath = path.Append(child.Name).ToArray(); Bound(childPath);
                SecureWindowsDirectory opened;
                try { opened = item.OpenChild(child.Name, child.IsDirectory); }
                catch (Exception ex) when (ex is StorageException or IOException or UnauthorizedAccessException)
                {
                    nodes.Add(new(ProtocolRules.NewToken(), root, index, childPath, child.Name, child.IsDirectory, 0, "", StorageRules.Version(ProtocolRules.NewToken(), 0, 0), "This item could not be safely opened. It will not be copied.")); continue;
                }
                using (opened) Visit(opened, ProtocolRules.NewToken(), root, index, childPath, child.Name);
            }
            if (SecureWindowsDirectory.Inspect(item.Handle).Version != info.Version) throw new IOException("A folder changed during planning. Review it and plan again.");
        }
        void Bound(string[] path)
        {
            if (nodes.Count >= TransferManifests.MaxItems || path.Length > TransferManifests.MaxDepth || path.Sum(x => x.Length) > 2048)
                throw new IOException("The source exceeds the item, depth or path planning limit.");
        }
    }
    private static void RequireSelection(SecureWindowsDirectory.Snapshot info, LocalCopySelection selected)
    {
        if (info.Identity != selected.Identity || info.Version != selected.Version || info.IsDirectory != selected.IsDirectory || !info.IsDirectory && info.Size != selected.Size)
            throw new IOException("The selected source changed. Refresh it and select it again.");
    }
    private static void Validate(Plan plan)
    {
        static bool Hash(string? s) => s is { Length: 64 } && s.All(char.IsAsciiHexDigitLower);
        if (plan is null || plan.Schema is < 1 or > 3 || plan.Id is null || !ProtocolRules.TokenPattern().IsMatch(plan.Id) || plan.Roots is null || plan.Nodes is null || plan.Roots.Length is < 1 or > 128 || plan.Nodes.Length is < 1 or > TransferManifests.MaxItems)
            throw new InvalidDataException("Invalid local plan bounds.");
        var ids = new HashSet<string>(); var rootIds = new HashSet<string>(); var nativeIds = new HashSet<string>(); var tops = new int[plan.Roots.Length];
        foreach (var root in plan.Roots)
        {
            if (root is null || root.Id is null || root.Canonical is null || root.Identity is null || root.Display is null || root.Name is null || !ProtocolRules.TokenPattern().IsMatch(root.Id) || !rootIds.Add(root.Id) || !SecureWindowsDirectory.IsLocalVolumePath(root.Canonical) ||
                root.Identity.Length is < 1 or > 256 || !Hash(root.Version) || root.Display.Length is < 3 or > 8192 || root.Display.Any(char.IsControl)) throw new InvalidDataException("Invalid local source root.");
            StorageRules.ValidateName(root.Name);
        }
        for (var i = 0; i < plan.Nodes.Length; i++)
        {
            var node = plan.Nodes[i];
            if (node is null || node.Id is null || node.Path is null || node.Name is null || node.Identity is null || node.Error is null || !ProtocolRules.TokenPattern().IsMatch(node.Id) || !ids.Add(node.Id) || node.Root < 0 || node.Root >= plan.Roots.Length ||
                node.Parent < -1 || node.Parent >= i || !Hash(node.Version) || node.Identity.Length > 256 ||
                node.Error.Length == 0 && (node.Identity.Length == 0 || !nativeIds.Add(node.Identity)) || node.Directory && node.Size != 0)
                throw new InvalidDataException("Invalid local source item.");
            var root = plan.Roots[node.Root];
            if (node.Parent == -1)
            {
                tops[node.Root]++;
                if (root.FileOnly ? node.Directory || !node.Path.SequenceEqual(new[] { node.Name }) || node.Id == root.Id : !node.Directory || node.Path.Length != 0 || node.Id != root.Id || node.Name != root.Name)
                    throw new InvalidDataException("A local selection escaped its root.");
            }
            else if (plan.Nodes[node.Parent].Root != node.Root || rootIds.Contains(node.Id)) throw new InvalidDataException("Invalid local source parent.");
        }
        if (tops.Any(x => x != 1) || plan.Nodes.Any(n => rootIds.Contains(n.Id) && (plan.Roots[n.Root].FileOnly || n.Parent != -1))) throw new InvalidDataException("Ambiguous local roots.");
        var manifest = Manifest(plan); TransferManifests.Validate(manifest);
        if (manifest.Bytes > 16L * 1024 * 1024 * 1024) throw new InvalidDataException("Local plan source sizes exceed 16 GiB.");
        if (plan.Move is { } move) ValidateMove(plan, move);
    }
    private sealed class SourceLease(SecureWindowsDirectory item, SecureWindowsDirectory? parent = null) : IDisposable
    {
        internal SecureWindowsDirectory Item { get; } = item;
        public void Dispose() { Item.Dispose(); parent?.Dispose(); }
    }
    private static SourceLease OpenNode(Plan plan, Node node)
    {
        if (node.Error.Length != 0) throw new IOException("This planned item is unavailable.");
        var root = plan.Roots[node.Root]; var chain = new List<(string Name, string Identity)>();
        for (var index = node.Directory ? Array.IndexOf(plan.Nodes, node) : node.Parent; index >= 0; index = plan.Nodes[index].Parent)
        { var ancestor = plan.Nodes[index]; if (ancestor.Path.Length != 0) chain.Add((ancestor.Name, ancestor.Identity)); }
        chain.Reverse(); var parent = SecureWindowsDirectory.OpenBoundPath(root.Canonical, root.Identity, chain);
        if (node.Directory) return new(parent);
        SecureWindowsDirectory? file = null;
        try
        {
            file = parent.OpenChild(node.Name);
            if (file.Info.Identity != node.Identity || file.Info.Version != node.Version || file.Info.Size != node.Size) throw new IOException("A planned file changed. Review it as a new selection.");
            return new(file, parent);
        }
        catch { file?.Dispose(); parent.Dispose(); throw; }
    }
    private TransferManifests.Request Reader(Plan plan)
    {
        var serial = new object(); var hashes = new Dictionary<string, byte[]>(); var nodes = plan.Nodes.ToDictionary(x => x.Id);
        var roots = plan.Roots.ToDictionary(x => x.Id); var cursors = new Dictionary<string, (string Node, int Next)>();
        var rootCursors = Enumerable.Range(1, (plan.Roots.Length - 1) / StorageRules.PageSize).ToDictionary(_ => ProtocolRules.NewToken(), x => x * StorageRules.PageSize);
        return (value, ct) => Task.Run(() =>
        {
            lock (serial)
            {
                ct.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(closed, this);
                var request = value.Clone(); request.RequestId = ProtocolRules.NewToken(); var result = new FileResponse { RequestId = request.RequestId };
                switch (request.Operation)
                {
                    case FileOperation.ListRoots:
                        var rootStart = 0;
                        if (request.Continuation.Length != 0)
                        { if (!rootCursors.TryGetValue(request.Continuation, out rootStart)) throw new IOException("Invalid local roots cursor."); }
                        result.Entries.AddRange(plan.Roots.Skip(rootStart).Take(StorageRules.PageSize).Select(RootEntry));
                        if (rootStart + result.Entries.Count < plan.Roots.Length)
                        result.Continuation = rootCursors.Single(x => x.Value == rootStart + result.Entries.Count).Key; break;
                    case FileOperation.GetMetadata:
                        if (nodes.TryGetValue(request.NodeId, out var node)) { using var pinned = OpenNode(plan, node); result.Entries.Add(Entry(node)); }
                        else if (roots.TryGetValue(request.NodeId, out var root)) { using var pinned = SecureWindowsDirectory.OpenBoundPath(root.Canonical, root.Identity, []); result.Entries.Add(RootEntry(root)); }
                        else throw new IOException("Unknown local planned item."); break;
                    case FileOperation.ListDirectory:
                        if (!roots.TryGetValue(request.NodeId, out var selectedRoot))
                        { var directory = nodes[request.NodeId]; if (!directory.Directory) throw new IOException("Select a planned folder."); using var pinned = OpenNode(plan, directory); }
                        else { using var pinned = SecureWindowsDirectory.OpenBoundPath(selectedRoot.Canonical, selectedRoot.Identity, []); }
                        var children = plan.Nodes.Where(x => x.Parent >= 0 ? plan.Nodes[x.Parent].Id == request.NodeId : plan.Roots[x.Root].FileOnly && plan.Roots[x.Root].Id == request.NodeId).ToArray();
                        var start = 0;
                        if (request.Continuation.Length != 0)
                        { if (!cursors.Remove(request.Continuation, out var cursor) || cursor.Node != request.NodeId) throw new IOException("Invalid local listing cursor."); start = cursor.Next; }
                        result.Entries.AddRange(children.Skip(start).Take(StorageRules.PageSize).Select(Entry));
                        if (start + result.Entries.Count < children.Length)
                        { if (cursors.Count >= 128) throw new IOException("Too many local listing cursors."); result.Continuation = ProtocolRules.NewToken(); cursors.Add(result.Continuation, (request.NodeId, start + result.Entries.Count)); } break;
                    case FileOperation.CloseListing:
                        if (cursors.TryGetValue(request.Continuation, out var close) && close.Node == request.NodeId) cursors.Remove(request.Continuation); break;
                    case FileOperation.PrepareDownload:
                    case FileOperation.DownloadStatus:
                        var file = nodes[request.Operation == FileOperation.PrepareDownload ? request.NodeId : request.TransferId];
                        if (file.Directory || request.Operation == FileOperation.PrepareDownload && file.Version != request.ExpectedVersion) throw new IOException("The planned source version changed.");
                        using (var pinned = OpenNode(plan, file))
                        {
                            if (!hashes.TryGetValue(file.Id, out var hash))
                            {
                                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[StorageRules.MaxReadRange]; long offset = 0;
                                while (offset < file.Size) { ct.ThrowIfCancellationRequested(); var read = pinned.Item.Read(offset, buffer); if (read == 0 || read > file.Size - offset) throw new IOException("Source size changed."); digest.AppendData(buffer, 0, read); offset += read; }
                                ct.ThrowIfCancellationRequested(); hashes[file.Id] = hash = digest.GetHashAndReset();
                            }
                            result.Download = new DownloadInfo { Id = file.Id, Name = file.Name, Version = file.Version, Size = file.Size, PreparedBytes = file.Size, ExpiresAt = StorageRules.MaxTimestamp, State = DownloadState.DownloadReady, Sha256 = ByteString.CopyFrom(hash) };
                        }
                        break;
                    case FileOperation.DownloadRead:
                        var readFile = nodes[request.TransferId];
                        if (!hashes.ContainsKey(readFile.Id) || request.Offset < 0 || request.Offset > readFile.Size || request.Length is < 1 or > StorageRules.MaxReadRange) throw new IOException("Invalid local copy range.");
                        using (var pinned = OpenNode(plan, readFile))
                        {
                            var bytes = new byte[(int)Math.Min(request.Length, readFile.Size - request.Offset)]; var read = 0;
                            while (read < bytes.Length) { ct.ThrowIfCancellationRequested(); var count = RandomAccess.Read(pinned.Item.Handle, bytes.AsSpan(read), request.Offset + read); if (count == 0) throw new IOException("The source changed."); read += count; }
                            result.Content = ByteString.CopyFrom(bytes); result.EndOfFile = request.Offset + read == readFile.Size;
                        }
                        break;
                    case FileOperation.ReleaseDownload:
                        if (!nodes.TryGetValue(request.TransferId, out var release) || release.Directory) throw new IOException("Unknown local copy."); break;
                    default: throw new InvalidOperationException("This operation is unavailable for a local plan.");
                }
                return result;
            }
        }, ct);
        FileEntry RootEntry(SourceRoot root) => new() { Id = root.Id, GrantId = root.Id, Name = root.Name, IsRoot = true, IsDirectory = true, Version = root.Version, Permissions = (uint)StoragePermission.ReadOnly };
        FileEntry Entry(Node node)
        {
            var root = plan.Roots[node.Root]; return new() { Id = node.Id, GrantId = root.Id, ParentId = node.Parent < 0 ? root.FileOnly ? root.Id : "" : plan.Nodes[node.Parent].Id,
                IsRoot = node.Parent < 0 && !root.FileOnly, Name = node.Name, IsDirectory = node.Directory, Size = node.Size, Version = node.Version, Permissions = node.Error.Length == 0 ? (uint)StoragePermission.ReadOnly : 0 };
        }
    }
}
