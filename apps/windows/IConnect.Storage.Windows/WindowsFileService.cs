using Google.Protobuf;
using IConnect.Core;
using IConnect.Protocol;

namespace IConnect.Storage.Windows;

public sealed class WindowsFileService : IFileService, IPublishedBatchCheckCancellation
{
    public void BindOwnerPolicy(WindowsOwnerBinding binding)
    {
        if (store is not StorageGrantStore approvals) throw new InvalidOperationException("Local browser locations cannot authorize a remote session.");
        approvals.BindOwnerPolicy(binding);
    }
    public void BindOwnerPolicy(WindowsOwnerBinding binding, string peer, string pairing)
    {
        if (peer.Length != 64 || peer.Any(c => !char.IsAsciiHexDigitLower(c)) || !UploadBatchRules.CanonicalToken(pairing)) throw new InvalidDataException("Invalid session pairing binding.");
        BindOwnerPolicy(binding); boundPeer = peer; boundPairing = pairing;
    }
    private string boundPeer = "", boundPairing = "";
    private WindowsBatchPlans? batchPlanHost;
    private sealed record Node(string GrantId, string[] Segments, string Identity, bool IsDirectory, string? ParentId);
    private sealed class Cursor(string peer, string nodeId, SecureWindowsDirectory directory, IEnumerator<SecureWindowsDirectory.DirectoryName> entries) : IDisposable
    {
        public string Peer { get; } = peer;
        public string NodeId { get; } = nodeId;
        public SecureWindowsDirectory Directory { get; } = directory;
        public IEnumerator<SecureWindowsDirectory.DirectoryName> Entries { get; } = entries;
        public DateTimeOffset LastUsed { get; set; } = DateTimeOffset.UtcNow;
        public void Dispose() { Entries.Dispose(); Directory.Dispose(); }
    }
    private readonly IWindowsFileAuthority store;
    private readonly DownloadSourceStore? downloads;
    private readonly WindowsMutations? mutations;
    private readonly StorageGrantStore? approvalStore;
    private readonly string downloadScope = ProtocolRules.NewToken();
    private readonly Dictionary<string, Node> nodes = [];
    private readonly Dictionary<string, string> nodeKeys = [];
    private readonly LinkedList<string> recent = new();
    private readonly Dictionary<string, LinkedListNode<string>> recentIndex = [];
    private readonly Dictionary<string, Cursor> cursors = [];
    private readonly SemaphoreSlim operationGate = new(1);
    private WindowsUploads? incomingUploadHost;
    private bool disposed;
    private int policyInvalidated;

    public WindowsFileService(StorageGrantStore store, DownloadSourceStore? downloads = null)
        : this((IWindowsFileAuthority)store, downloads) { mutations = store.Mutations; approvalStore = store; }
    public bool SupportsCreatedDirectoryReferences => mutations is not null;
    public bool SupportsPublishedUploadChecks => approvalStore is not null;
    public bool SupportsFolderBatchReception(string peer) => approvalStore is not null && boundPeer == peer;
    internal WindowsFileService(IWindowsFileAuthority store, DownloadSourceStore? downloads = null)
    { this.store = store; this.downloads = downloads; store.Changed += PolicyChanged; }
    private void PolicyChanged()
    {
        Interlocked.Exchange(ref policyInvalidated, 1);
        if (!operationGate.Wait(0)) return;
        try { ClearInvalidatedCursors(); } finally { operationGate.Release(); }
    }
    private void ClearInvalidatedCursors()
    {
        if (Interlocked.Exchange(ref policyInvalidated, 0) == 0) return;
        foreach (var cursor in cursors.Values) cursor.Dispose();
        cursors.Clear();
    }

    public async Task<FileResponse> ExecuteAsync(string peerId, FileRequest request, CancellationToken cancellationToken)
    {
        var valid = ProtocolRules.TokenPattern().IsMatch(request.RequestId);
        try
        {
            var result = await ExecuteCoreAsync(peerId, request, cancellationToken);
            if (valid) approvalStore?.RecordActivity(WindowsActivityEvent.Request(peerId, request.Operation, result.Status));
            return result;
        }
        catch (OperationCanceledException)
        {
            if (valid) approvalStore?.RecordActivity(new(WindowsActivityKind.FileRequest, WindowsActivityOutcome.Canceled, peerId, Operation: request.Operation));
            throw;
        }
        catch (Exception)
        {
            if (valid) approvalStore?.RecordActivity(new(WindowsActivityKind.FileRequest, WindowsActivityOutcome.Failed, peerId, Operation: request.Operation));
            throw;
        }
    }

    private async Task<FileResponse> ExecuteCoreAsync(string peerId, FileRequest request, CancellationToken cancellationToken)
    {
        if (!ProtocolRules.TokenPattern().IsMatch(request.RequestId))
            return Error(request.RequestId, FileStatus.InvalidRequest, "Invalid file request identifier.");
        InterruptPublishedBatchCheck(peerId, request);
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            foreach (var id in cursors.Where(x => DateTimeOffset.UtcNow - x.Value.LastUsed > TimeSpan.FromMinutes(2)).Select(x => x.Key).ToArray())
            { cursors[id].Dispose(); cursors.Remove(id); }
            return await Task.Run(() => request.Operation is >= FileOperation.PrepareDownload and <= FileOperation.ReleaseDownload
                ? Execute(peerId, request, cancellationToken)
                : store.WithAuthorization(() => Execute(peerId, request, cancellationToken)), cancellationToken);
        }
        catch (StorageException ex) { return Error(request.RequestId, ex.Status, ex.Message); }
        catch (InvalidDataException) { return Error(request.RequestId, FileStatus.InvalidRequest, "Use a current, valid file-operation identifier."); }
        catch (FormatException) { return Error(request.RequestId, FileStatus.InvalidRequest, "Invalid file-operation identifier."); }
        catch (UnauthorizedAccessException) { return Error(request.RequestId, FileStatus.AccessDenied, "The operating system denied access."); }
        catch (IOException) { return Error(request.RequestId, FileStatus.IoError, "The filesystem operation could not finish."); }
        finally { ClearInvalidatedCursors(); operationGate.Release(); }
    }

    public void InterruptPublishedBatchCheck(string peer, FileRequest request)
    {
        if (!disposed && ProtocolRules.TokenPattern().IsMatch(request.RequestId))
            Volatile.Read(ref batchPlanHost)?.InterruptPublishedBatchCheck(peer, downloadScope, request);
    }

    private FileResponse Execute(string peer, FileRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (UploadBatchRules.IsRpc(request.Operation))
        {
            if (approvalStore is null) throw new StorageException(FileStatus.Unsupported, "Local browser shortcuts cannot admit remote batches.");
            var pairing = approvalStore.OwnerScope == "local" ? "local" : boundPeer == peer && boundPairing.Length != 0 ? boundPairing :
                throw new StorageException(FileStatus.AccessDenied, "Batch admission requires the current authenticated pairing generation.");
            return (batchPlanHost ??= approvalStore.BatchPlans).Execute(peer, pairing, downloadScope, request, () =>
            {
                var (node, grant) = Resolve(peer, request.DestinationId, StoragePermission.Write | StoragePermission.Create);
                if (!node.IsDirectory) throw new StorageException(FileStatus.InvalidRequest, "Choose an approved receiving folder.");
                using var opened = Open(node, grant);
                return new(grant.Id, grant.CanonicalPath, grant.RootIdentity, node.Segments, opened.Info.Identity);
            }, ct);
        }
        var result = new FileResponse { RequestId = request.RequestId };
        switch (request.Operation)
        {
            case FileOperation.ResolveCreatedDirectory:
                result.Entries.Add(ResolveCreatedDirectory(peer, request, result)); break;
            case >= FileOperation.UploadContext and <= FileOperation.ListUploads:
            case FileOperation.VerifyPublishedUpload:
                if (approvalStore is null) throw new StorageException(FileStatus.AccessDenied, "Local shortcuts cannot approve incoming remote copies.");
                var uploads = approvalStore.Uploads;
                incomingUploadHost = uploads;
                var incoming = uploads.Execute(peer, request, () =>
                {
                    var (target, targetGrant) = Resolve(peer, request.DestinationId, StoragePermission.Write | StoragePermission.Create);
                    if (!target.IsDirectory) throw new StorageException(FileStatus.InvalidRequest, "Choose an approved incoming destination folder.");
                    using var opened = Open(target, targetGrant);
                    return new(target.GrantId, targetGrant.CanonicalPath, targetGrant.RootIdentity, target.Segments, target.Identity);
                }, ct, downloadScope);
                if (request.Operation == FileOperation.UploadContext)
                    foreach (var grant in store.ForPeer(peer))
                    {
                        if ((grant.Permissions & (StoragePermission.Write | StoragePermission.Create)) != (StoragePermission.Write | StoragePermission.Create)) continue;
                        try
                        {
                            using var root = SecureWindowsDirectory.OpenPath(grant.CanonicalPath, grant.RootIdentity, [], true);
                            var destination = Issue(grant, [], root.Info, null);
                            if ((grant.Permissions & StoragePermission.List) == 0) { destination.ModifiedAt = 0; destination.Size = 0; destination.Version = StorageRules.Version(root.Info.Identity, 0, 0); }
                            incoming.Entries.Add(destination);
                        }
                        catch (StorageException) { }
                    }
                return incoming;
            case FileOperation.RecoveryContext:
            case FileOperation.ListDeleted:
            case FileOperation.RecoveryStatus:
                return RequireRecovery().Query(peer, request);
            case FileOperation.Delete:
                ClearCursors(peer);
                result.Mutation = RequireRecovery().Delete(peer, request, () =>
                {
                    var (node, grant) = Resolve(peer, request.NodeId, StoragePermission.Delete);
                    if (node.Segments.Length == 0) throw new StorageException(FileStatus.AccessDenied, "Approved roots cannot be deleted.");
                    if (node.IsDirectory) CheckMutationTree(node, grant, ct);
                    return new(grant, node.Segments, node.Identity, node.IsDirectory);
                }, ct);
                if (result.Mutation.Phase == MutationPhase.ChangeSucceeded) RemoveNode(request.NodeId);
                result.Status = result.Mutation.Status; result.Message = result.Mutation.Message; break;
            case FileOperation.Restore:
                ClearCursors(peer);
                result.Mutation = RequireRecovery().Restore(peer, request, ct);
                result.Status = result.Mutation.Status; result.Message = result.Mutation.Message; break;
            case FileOperation.MutationContext:
            case FileOperation.MutationStatus:
            case FileOperation.ListMutations:
                return (mutations ?? throw new StorageException(FileStatus.Unsupported, "File-operation receipts are unavailable in this host.")).Query(peer, request);
            case FileOperation.PrepareDownload:
            {
                var (node, grant) = Resolve(peer, request.NodeId, StoragePermission.Read);
                if (node.IsDirectory) throw new StorageException(FileStatus.InvalidRequest, "Choose a file to download.");
                using var file = Open(node, grant, readData: true);
                RequireVersion(request.ExpectedVersion, file.Info.Version);
                result.Download = RequireDownloads().Prepare(peer, grant.Id, node.Segments, node.Identity, file.Info.Version, file.Info.Size, downloadScope);
                break;
            }
            case FileOperation.DownloadStatus:
                result.Download = RequireDownloads().Status(peer, request.TransferId, downloadScope); break;
            case FileOperation.DownloadRead:
                return RequireDownloads().Read(peer, request);
            case FileOperation.ReleaseDownload:
                RequireDownloads().Release(peer, request.TransferId); break;
            case FileOperation.ListRoots:
                foreach (var grant in store.ForPeer(peer))
                {
                    if ((grant.Permissions & StoragePermission.List) == 0) continue;
                    try
                    {
                        using var root = SecureWindowsDirectory.OpenPath(grant.CanonicalPath, grant.RootIdentity, [], true);
                        result.Entries.Add(Issue(grant, [], root.Info, null));
                    }
                    catch (StorageException) { /* Removed/unavailable volumes do not become new grants. */ }
                }
                break;
            case FileOperation.ListDirectory:
                ListDirectory(peer, request, result, ct);
                break;
            case FileOperation.CloseListing:
                if (cursors.TryGetValue(request.Continuation, out var listing) && listing.Peer == peer && listing.NodeId == request.NodeId)
                { listing.Dispose(); cursors.Remove(request.Continuation); }
                break;
            case FileOperation.GetMetadata:
            {
                var (node, grant) = Resolve(peer, request.NodeId, StoragePermission.List);
                using var opened = Open(node, grant);
                result.Entries.Add(Describe(request.NodeId, node, grant, opened.Info));
                break;
            }
            case FileOperation.ReadRange:
            {
                var (node, grant) = Resolve(peer, request.NodeId, StoragePermission.Read);
                if (node.IsDirectory || request.Offset < 0 || request.Length is < 1 or > StorageRules.MaxReadRange)
                    throw new StorageException(FileStatus.InvalidRequest, "Request a bounded range from a file.");
                using var opened = Open(node, grant, readData: true);
                RequireVersion(request.ExpectedVersion, opened.Info.Version);
                var data = new byte[request.Length];
                var read = opened.Read(request.Offset, data);
                result.Content = ByteString.CopyFrom(data, 0, read);
                result.EndOfFile = request.Offset + read >= opened.Info.Size;
                result.Entries.Add(Describe(request.NodeId, node, grant, opened.Info));
                break;
            }
            case FileOperation.CreateDirectory:
            {
                if (request.MutationId.Length == 0) { _ = Resolve(peer, request.DestinationId, StoragePermission.Create); throw new StorageException(FileStatus.InvalidRequest, "Update the client to supply a durable operation identifier."); }
                Node? parent = null; StorageGrant? grant = null; var creationName = "";
                result.Mutation = (mutations ?? throw new StorageException(FileStatus.Unsupported, "File changes are unavailable.")).Execute(peer, request, () =>
                {
                    (parent, grant) = Resolve(peer, request.DestinationId, StoragePermission.Create);
                    if (!parent.IsDirectory) throw new StorageException(FileStatus.InvalidRequest, "Choose a destination folder.");
                    store.ValidateLocation(grant, parent.Segments.Append(request.Name).ToArray());
                    creationName = (grant.Permissions & StoragePermission.List) == 0 ? IncomingFolderName(request.Name) : request.Name;
                    store.ValidateLocation(grant, parent.Segments.Append(creationName).ToArray());
                    using var opened = Open(parent, grant);
                    return new(request.MutationId, peer, request.Operation, MutationRules.Fingerprint(request), null,
                        new(grant.Id, grant.RootIdentity, parent.Segments, parent.Identity), creationName, "", true, RequestedName: request.Name);
                }, () =>
                {
                    using var directory = Open(parent!, grant!); using var created = directory.CreateDirectory(creationName);
                    return new(creationName, created.Info.Identity);
                });
                result.Status = result.Mutation.Status; result.Message = result.Mutation.Message;
                if (result.Mutation.Phase == MutationPhase.ChangeSucceeded) result.Entries.Add(ResolveCreatedDirectory(peer, request, result));
                break;
            }
            case FileOperation.Rename:
            case FileOperation.Move:
            {
                var required = request.Operation == FileOperation.Rename ? StoragePermission.Rename : StoragePermission.Move;
                if (request.MutationId.Length == 0) { _ = Resolve(peer, request.NodeId, required); throw new StorageException(FileStatus.InvalidRequest, "Update the client to supply a durable operation identifier."); }
                Node? node = null, destination = null; StorageGrant? grant = null, destinationGrant = null; string destinationId = "", name = "";
                result.Mutation = (mutations ?? throw new StorageException(FileStatus.Unsupported, "File changes are unavailable.")).Execute(peer, request, () =>
                {
                (node, grant) = Resolve(peer, request.NodeId, required);
                if (node.Segments.Length == 0) throw new StorageException(FileStatus.AccessDenied, "Approved roots cannot be renamed or moved.");
                destinationId = request.Operation == FileOperation.Rename ? node.ParentId ?? "" : request.DestinationId;
                (destination, destinationGrant) = Resolve(peer, destinationId, required | StoragePermission.Create);
                if (!destination.IsDirectory) throw new StorageException(FileStatus.InvalidRequest, "Choose a destination folder.");
                name = request.Name.Length == 0 ? node.Segments[^1] : request.Name;
                var newSegments = destination.Segments.Append(name).ToArray();
                store.ValidateLocation(destinationGrant, newSegments);
                if (node.IsDirectory && grant.Id == destinationGrant.Id && IsPrefix(node.Segments, destination.Segments))
                    throw new StorageException(FileStatus.InvalidRequest, "A folder cannot be moved inside itself.");
                ClearCursors(peer);
                using var source = Open(node, grant); RequireVersion(request.ExpectedVersion, source.Info.Version);
                using var target = Open(destination, destinationGrant);
                if (node.IsDirectory) CheckMutationTree(node, grant, ct);
                return new(request.MutationId, peer, request.Operation, MutationRules.Fingerprint(request),
                    new(grant.Id, grant.RootIdentity, node.Segments, node.Identity), new(destinationGrant.Id, destinationGrant.RootIdentity, destination.Segments, destination.Identity),
                    name, request.ExpectedVersion, node.IsDirectory);
                }, () =>
                {
                using var source = Open(node!, grant!, mutation: true); RequireVersion(request.ExpectedVersion, source.Info.Version);
                using var target = Open(destination!, destinationGrant!);
                source.RenameTo(target, name);
                RemoveNode(request.NodeId);
                var changed = SecureWindowsDirectory.Inspect(source.Handle);
                result.Entries.Add(Issue(destinationGrant!, destination!.Segments.Append(name).ToArray(), changed, destinationId)); return new(name, changed.Identity);
                });
                result.Status = result.Mutation.Status; result.Message = result.Mutation.Message;
                break;
            }
            default:
                throw new StorageException(FileStatus.Unsupported, "This file operation is not available in the current storage engine.");
        }
        return result;
    }

    private DownloadSourceStore RequireDownloads() => downloads ?? throw new StorageException(FileStatus.Unsupported, "Real-file downloads are unavailable in this host.");
    private WindowsRecovery RequireRecovery() => approvalStore?.Deleted ?? throw new StorageException(FileStatus.Unsupported, "Recoverable deletion is unavailable in this browser.");
    private void CheckMutationTree(Node node, StorageGrant grant, CancellationToken ct)
    {
        var pending = new Queue<string[]>(); pending.Enqueue(node.Segments); var visited = 0;
        while (pending.TryDequeue(out var path))
        {
            ct.ThrowIfCancellationRequested();
            if (path.Length > node.Segments.Length + 32 || ++visited > 4096) throw new StorageException(FileStatus.Unsupported, "This folder is too large or deep for a safe management scan.");
            using var folder = SecureWindowsDirectory.OpenPath(grant.CanonicalPath, grant.RootIdentity, path, true);
            foreach (var child in folder.EnumerateNames(ct))
            {
                if (++visited > 4096) throw new StorageException(FileStatus.Unsupported, "This folder is too large for a safe management scan.");
                var segments = path.Append(child.Name).ToArray(); store.ValidateLocation(grant, segments);
                using var opened = SecureWindowsDirectory.OpenPath(grant.CanonicalPath, grant.RootIdentity, segments, child.IsDirectory, readData: false);
                if (child.IsDirectory) pending.Enqueue(segments);
            }
        }
    }

    private void ListDirectory(string peer, FileRequest request, FileResponse response, CancellationToken ct)
    {
        var (node, grant) = Resolve(peer, request.NodeId, StoragePermission.List);
        if (!node.IsDirectory) throw new StorageException(FileStatus.InvalidRequest, "Choose a folder to browse.");
        Cursor cursor;
        string token;
        if (request.Continuation.Length > 0)
        {
            token = request.Continuation;
            if (!cursors.TryGetValue(token, out cursor!) || cursor.Peer != peer || cursor.NodeId != request.NodeId)
                throw new StorageException(FileStatus.StaleReference, "The directory page expired. Refresh the folder.");
        }
        else
        {
            ClearCursors(peer);
            var directory = Open(node, grant);
            cursor = new(peer, request.NodeId, directory, directory.EnumerateNames(ct).GetEnumerator());
            token = ProtocolRules.NewToken();
            cursors.Add(token, cursor);
        }
        cursor.LastUsed = DateTimeOffset.UtcNow;
        try
        {
            while (response.Entries.Count < StorageRules.PageSize)
            {
                ct.ThrowIfCancellationRequested();
                if (!cursor.Entries.MoveNext())
                { cursor.Dispose(); cursors.Remove(token); return; }
                var name = cursor.Entries.Current;
                var segments = node.Segments.Append(name.Name).ToArray();
                try
                {
                    store.ValidateLocation(grant, segments);
                    // Inspect through a separate relative handle; no path-based link following.
                    using var child = SecureWindowsDirectory.OpenPath(grant.CanonicalPath, grant.RootIdentity, segments, name.IsDirectory, readData: false);
                    response.Entries.Add(Issue(grant, segments, child.Info, request.NodeId));
                }
                catch (StorageException ex) when (ex.Status is FileStatus.AccessDenied or FileStatus.NotFound or FileStatus.Unsupported or FileStatus.InvalidRequest or FileStatus.Conflict)
                { /* Inaccessible or redirected entries never receive remote references. */ }
            }
            response.Continuation = token;
        }
        catch
        {
            cursor.Dispose(); cursors.Remove(token); throw;
        }
    }

    private (Node Node, StorageGrant Grant) Resolve(string peer, string id, StoragePermission permission)
    {
        if (!nodes.TryGetValue(id, out var node)) throw new StorageException(FileStatus.StaleReference, "Refresh the storage location to obtain a current file reference.");
        Touch(id);
        var grant = store.Get(node.GrantId, peer);
        StorageRules.Require(grant.Permissions, permission);
        store.ValidateLocation(grant, node.Segments);
        return (node, grant);
    }
    internal Task<LocalCopySelection> CaptureLocalFileAsync(string owner, FileEntry entry, CancellationToken ct)
    {
        if (entry.IsDirectory) throw new IOException("Select a file for a single-file copy.");
        return CaptureLocalItemAsync(owner, entry, ct);
    }
    internal async Task<LocalCopySelection> CaptureLocalItemAsync(string owner, FileEntry entry, CancellationToken ct)
    {
        await operationGate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (store is not ILocalFileAuthority) throw new InvalidOperationException("Only a local explorer can select a local copy source.");
            return store.WithAuthorization(() =>
            {
                var (node, grant) = Resolve(owner, entry.Id, entry.IsDirectory ? StoragePermission.List : StoragePermission.Read);
                if (node.IsDirectory != entry.IsDirectory || entry.GrantId != grant.Id) throw new IOException("Select a current local item.");
                using var opened = Open(node, grant, readData: true);
                RequireVersion(entry.Version, opened.Info.Version);
                if (!entry.IsDirectory && entry.Size != opened.Info.Size) throw new IOException("The selected file changed. Refresh it first.");
                return new LocalCopySelection(grant, node.Segments, node.Identity, entry);
            });
        }
        finally { operationGate.Release(); }
    }
    private static SecureWindowsDirectory Open(Node node, StorageGrant grant, bool mutation = false, bool readData = false)
    {
        var opened = SecureWindowsDirectory.OpenPath(grant.CanonicalPath, grant.RootIdentity, node.Segments, node.IsDirectory, mutation: mutation, readData: readData);
        if (opened.Info.Identity != node.Identity)
        { opened.Dispose(); throw new StorageException(FileStatus.StaleReference, "This item was replaced. Refresh the folder."); }
        return opened;
    }
    private FileEntry Issue(StorageGrant grant, string[] segments, SecureWindowsDirectory.Snapshot snapshot, string? parent)
    {
        var node = new Node(grant.Id, segments, snapshot.Identity, snapshot.IsDirectory, parent);
        var key = Key(node);
        if (nodeKeys.TryGetValue(key, out var existing))
        { Touch(existing); return Describe(existing, nodes[existing], grant, snapshot); }
        if (nodes.Count >= StorageRules.MaxIssuedNodes)
        {
            var protectedIds = new HashSet<string>();
            void Protect(string? id)
            {
                while (id is not null && protectedIds.Add(id) && nodes.TryGetValue(id, out var ancestor)) id = ancestor.ParentId;
            }
            Protect(parent);
            foreach (var cursor in cursors.Values) Protect(cursor.NodeId);
            var candidate = recent.Last;
            while (candidate is not null && (protectedIds.Contains(candidate.Value) || nodes[candidate.Value].Segments.Length == 0)) candidate = candidate.Previous;
            if (candidate is null) throw new StorageException(FileStatus.ResourceExhausted, "Too many active storage references.");
            RemoveNode(candidate.Value);
        }
        var id = ProtocolRules.NewToken();
        nodes.Add(id, node);
        nodeKeys.Add(key, id);
        recentIndex.Add(id, recent.AddFirst(id));
        return Describe(id, node, grant, snapshot);
    }
    private static string Key(Node node) => node.GrantId + "\0" + node.Identity + "\0" + string.Join('\0', node.Segments);
    private void Touch(string id)
    {
        var item = recentIndex[id]; recent.Remove(item); recent.AddFirst(item);
    }
    private void RemoveNode(string id)
    {
        if (!nodes.Remove(id, out var node)) return;
        nodeKeys.Remove(Key(node));
        recent.Remove(recentIndex[id]); recentIndex.Remove(id);
    }
    private FileEntry ResolveCreatedDirectory(string peer, FileRequest request, FileResponse response)
    {
        var row = (mutations ?? throw new StorageException(FileStatus.Unsupported, "Created-folder recovery is unavailable in this host.")).ResolveCreatedDirectory(peer, request);
        var grant = store.Get(row.Destination.Grant, peer);
        using var parent = SecureWindowsDirectory.OpenPath(grant.CanonicalPath, row.Destination.RootIdentity, row.Destination.Path, true);
        if (parent.Info.Identity != row.Destination.Identity) throw new StorageException(FileStatus.StaleReference, "The created folder's recorded parent was replaced.");
        using var child = parent.OpenChild(row.ResultName, true);
        if (child.Info.Identity != row.ResultIdentity) throw new StorageException(FileStatus.StaleReference, "The created folder was moved or replaced. The current item was kept.");
        var parentEntry = Issue(grant, row.Destination.Path, parent.Info, null);
        response.MutationEpoch = request.MutationEpoch; response.Mutation = WindowsMutations.Info(row);
        return Issue(grant, row.Destination.Path.Append(row.ResultName).ToArray(), child.Info, parentEntry.Id);
    }
    private static string IncomingFolderName(string name)
    {
        StorageRules.ValidateName(name); var suffix = " (" + ProtocolRules.NewToken()[..16] + ")";
        var stem = name[..Math.Min(name.Length, 255 - suffix.Length)].TrimEnd(' ', '.');
        if (stem.Length != 0 && char.IsHighSurrogate(stem[^1])) stem = stem[..^1];
        var result = stem + suffix; StorageRules.ValidateName(result); return result;
    }
    private static FileEntry Describe(string id, Node node, StorageGrant grant, SecureWindowsDirectory.Snapshot snapshot) => new()
    {
        Id = id, GrantId = grant.Id, ParentId = node.ParentId ?? "", Name = node.Segments.Length == 0 ? grant.Label : node.Segments[^1],
        IsDirectory = snapshot.IsDirectory, IsRoot = node.Segments.Length == 0, Size = snapshot.IsDirectory ? 0 : snapshot.Size,
        ModifiedAt = snapshot.IsDirectory && (grant.Permissions & StoragePermission.List) == 0 ? 0 : snapshot.ModifiedAt, Permissions = (uint)grant.Permissions,
        Version = snapshot.IsDirectory && (grant.Permissions & StoragePermission.List) == 0 ? StorageRules.Version("incoming:" + grant.Id + ":" + snapshot.Identity, 0, 0) : snapshot.Version
    };
    private void ClearCursors(string peer)
    {
        foreach (var key in cursors.Where(x => x.Value.Peer == peer).Select(x => x.Key).ToArray())
        { cursors[key].Dispose(); cursors.Remove(key); }
    }
    private static bool IsPrefix(string[] prefix, string[] path) => path.Length >= prefix.Length &&
        prefix.Zip(path).All(x => x.First.Equals(x.Second, StringComparison.OrdinalIgnoreCase));
    private static void RequireVersion(string expected, string current)
    {
        if (expected.Length == 0 || expected != current)
            throw new StorageException(FileStatus.StaleReference, "The item changed. Refresh it before continuing.");
    }
    private static FileResponse Error(string request, FileStatus status, string message) =>
        new() { RequestId = request, Status = status, Message = message };
    public void Dispose()
    {
        downloads?.CancelScope(downloadScope);
        store.Changed -= PolicyChanged;
        operationGate.Wait();
        try { disposed = true; incomingUploadHost?.EndIncomingScope(downloadScope); batchPlanHost?.EndScope(downloadScope); foreach (var cursor in cursors.Values) cursor.Dispose(); cursors.Clear(); nodes.Clear(); nodeKeys.Clear(); recent.Clear(); recentIndex.Clear(); }
        finally { operationGate.Release(); }
    }
}
