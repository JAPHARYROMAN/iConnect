using Google.Protobuf;
using IConnect.Core;
using IConnect.Protocol;
using System.Security.Cryptography;
using System.Text.Json;

namespace IConnect.Storage.Windows;

public sealed partial class DownloadSourceStore : IAsyncDisposable
{
    private sealed record Source(string Id, string Peer, string Grant, string[] Segments, string Identity,
        string Version, long Size, long Expires, DownloadState State, string Hash = "", long Prepared = 0);
    private sealed record Worker(string Scope, CancellationTokenSource Cancellation, Task Task);
    private readonly object gate = new();
    private readonly StorageGrantStore grants;
    private readonly string path;
    private readonly Dictionary<string, Source> sources;
    private readonly Dictionary<string, Worker> workers = [];
    private readonly SemaphoreSlim hashing = new(2);
    private bool disposed;
    private readonly StateRecovery recovery;
    public IReadOnlyList<StateRecoveryIssue> RecoveryIssues { get { lock (gate) return recovery.Issues; } }
    public int PreservedRecords { get { lock (gate) return recovery.ArchivedCount; } }
    public void ClearPreservedRecords() { lock (gate) recovery.ClearArchives(); }
    public void ArchiveRecovery(string id) { lock (gate) { if (workers.Count != 0) throw new IOException("Pause sending before reviewing saved source state."); recovery.Archive(id); } }
    public DownloadSourceStore(StorageGrantStore grants, string directory)
    {
        this.grants = grants; Directory.CreateDirectory(directory); path = Path.Combine(directory, "download-sources.json");
        recovery = new(directory); sources = [];
        try
        {
        if (File.Exists(path + ".new") || File.Exists(path + ".bak")) throw new InvalidDataException("Uncommitted source approvals need local review.");
        if (File.Exists(path)) sources = StateRecovery.Read<Dictionary<string, Source>>(path, 4 * 1024 * 1024);
        else if (File.Exists(path + ".new") || File.Exists(path + ".bak")) throw new InvalidDataException("An uncommitted source record needs review.");
        if (sources.Count > 128) throw new InvalidDataException("Too many source downloads.");
        foreach (var (id, source) in sources)
        {
            if (source is null || id != source.Id || !ProtocolRules.TokenPattern().IsMatch(id) ||
                source.Peer.Length != 64 || source.Peer.Any(c => !char.IsAsciiHexDigitLower(c)) ||
                !ProtocolRules.TokenPattern().IsMatch(source.Grant) || source.Segments.Length is < 1 or > 128 ||
                source.Size is < 0 or > StorageRules.MaxTransferSize || source.Expires is < 0 or > StorageRules.MaxTimestamp ||
                source.Identity is null || source.Identity.Length is < 1 or > 256 || source.Version.Length != 64 || source.Version.Any(c => !char.IsAsciiHexDigitLower(c)) || source.Prepared < 0 || source.Prepared > source.Size || !Enum.IsDefined(source.State) ||
                source.Hash.Length != 0 && (source.Hash.Length != 64 || source.Hash.Any(c => !char.IsAsciiHexDigitLower(c))) ||
                (source.State == DownloadState.DownloadReady && (source.Hash.Length != 64 || source.Prepared != source.Size)))
                throw new InvalidDataException("Invalid source download record.");
            foreach (var segment in source.Segments) StorageRules.ValidateName(segment);
        }
        if (PurgeExpired() != 0) Save();
        }
        catch (Exception ex) when (StateRecovery.IsRecordFailure(ex))
        { sources.Clear(); recovery.Add(path, "Saved sending tickets could not be validated. Archive this record before preparing new transfers; existing file copies are kept."); }
    }
    public DownloadInfo Prepare(string peer, string grantId, string[] segments, string fileIdentity, string version, long size, string scope)
    {
        if (segments.Length == 0 || size is < 0 or > StorageRules.MaxTransferSize) throw new StorageException(FileStatus.Unsupported, "Choose a supported file.");
        try { return grants.WithAuthorization(() => { lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            recovery.RequireHealthy();
            if (PurgeExpired() != 0) Save();
            // Each receiver job owns its lease. Sharing a ticket for the same file
            // would let completion/cancel of one download revoke another.
            if (sources.Count >= 128 || sources.Values.Count(x => x.Peer == peer) >= 8)
                throw new StorageException(FileStatus.ResourceExhausted, "Close an existing download before preparing another.");
            var source = new Source(ProtocolRules.NewToken(), peer, grantId, (string[])segments.Clone(), fileIdentity,
                version, size, grants.Clock.GetUtcNow().AddHours(24).ToUnixTimeMilliseconds(), DownloadState.DownloadPreparing);
            Authorize(source, peer);
            sources.Add(source.Id, source);
            try { Save(); } catch { sources.Remove(source.Id); throw; }
            Start(source, scope); return Describe(source);
        } }); }
        finally { NotifySources(); }
    }
    public DownloadInfo Status(string peer, string id, string scope)
    {
        return grants.WithAuthorization(() => { lock (gate)
        {
            var source = Find(peer, id); Authorize(source, peer);
            using var opened = Open(source, peer);
            Start(source, scope); return Describe(sources[id]);
        } });
    }
    public FileResponse Read(string peer, FileRequest request)
    {
        Source source;
        lock (gate) source = Find(peer, request.TransferId);
        return grants.WithAuthorization(() => ReadAuthorized(source, peer, request));
    }
    private FileResponse ReadAuthorized(Source source, string peer, FileRequest request)
    {
        if (source.State != DownloadState.DownloadReady) throw new StorageException(FileStatus.Conflict, "Wait for source preparation to finish.");
        if (request.Offset < 0 || request.Offset > source.Size || request.Length is < 1 or > StorageRules.MaxReadRange)
            throw new StorageException(FileStatus.InvalidRequest, "Request a bounded download range.");
        using var file = Open(source, peer);
        var bytes = new byte[Math.Min((long)request.Length, source.Size - request.Offset)];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = RandomAccess.Read(file.Handle, bytes.AsSpan(count), request.Offset + count);
            if (read == 0) throw new StorageException(FileStatus.Conflict, "The source file changed.");
            count += read;
        }
        lock (gate)
        {
            if (Find(peer, source.Id) != source) throw new StorageException(FileStatus.AccessDenied, "This prepared source changed or was stopped. Review a fresh source before resuming.");
            Authorize(source, peer);
            return new FileResponse { RequestId = request.RequestId, Content = ByteString.CopyFrom(bytes), EndOfFile = request.Offset + count == source.Size };
        }
    }
    public void Release(string peer, string id)
    {
        var changed = false;
        try { grants.WithAuthorization(() => { lock (gate)
        {
            if (!sources.TryGetValue(id, out var source) || source.Peer != peer) return true;
            changed = true;
            if (workers.TryGetValue(id, out var worker)) worker.Cancellation.Cancel();
            sources.Remove(id); Save(); return true;
        } }); }
        finally { if (changed) NotifySources(); }
    }
    public void CancelScope(string scope)
    {
        lock (gate) foreach (var worker in workers.Values.Where(x => x.Scope == scope)) worker.Cancellation.Cancel();
    }
    private Source Find(string peer, string id)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        recovery.RequireHealthy();
        if (!sources.TryGetValue(id, out var source) || source.Peer != peer || source.Expires <= grants.Clock.GetUtcNow().ToUnixTimeMilliseconds())
            throw new StorageException(FileStatus.AccessDenied, "The source download expired or belongs to another device. Refresh the file.");
        return source;
    }
    private StorageGrant Authorize(Source source, string peer)
    {
        if (source.Peer != peer || source.Expires <= grants.Clock.GetUtcNow().ToUnixTimeMilliseconds()) throw new StorageException(FileStatus.AccessDenied, "Source approval expired.");
        var grant = grants.Get(source.Grant, peer); StorageRules.Require(grant.Permissions, StoragePermission.Read);
        grants.ValidateAccess(grant, source.Segments); return grant;
    }
    private SecureWindowsDirectory Open(Source source, string peer)
    {
        var grant = Authorize(source, peer);
        var file = SecureWindowsDirectory.OpenPath(grant.CanonicalPath, grant.RootIdentity, source.Segments, false);
        if (file.Info.Identity != source.Identity || file.Info.Version != source.Version || file.Info.Size != source.Size)
        { file.Dispose(); throw new StorageException(FileStatus.Conflict, "The source changed. Refresh the file and start a new download."); }
        return file;
    }
    private void Start(Source source, string scope)
    {
        if (!sources.ContainsKey(source.Id) || source.State != DownloadState.DownloadPreparing || workers.ContainsKey(source.Id)) return;
        var cancellation = new CancellationTokenSource();
        // The dictionary is populated under gate before the worker can inspect it.
        var task = Task.Run(() => HashAsync(source, cancellation.Token));
        workers[source.Id] = new(scope, cancellation, task);
    }
    private async Task HashAsync(Source source, CancellationToken ct)
    {
        var entered = false;
        try
        {
            await hashing.WaitAsync(ct); entered = true;
            using var file = Open(source, source.Peer);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[StorageRules.MaxReadRange]; var offset = 0L;
            while (offset < source.Size)
            {
                ct.ThrowIfCancellationRequested(); Authorize(source, source.Peer);
                var read = file.Read(offset, buffer);
                if (read <= 0 || offset + read > source.Size) throw new IOException("Source changed.");
                hash.AppendData(buffer, 0, read); offset += read;
                lock (gate) if (sources.TryGetValue(source.Id, out var current)) sources[source.Id] = current with { Prepared = offset };
            }
            ct.ThrowIfCancellationRequested(); Authorize(source, source.Peer);
            file.Dispose(); // Ready must not retain the preparation handle's write lock.
            lock (gate)
            {
                if (sources.TryGetValue(source.Id, out var current))
                {
                    sources[source.Id] = current with { State = DownloadState.DownloadReady, Hash = Convert.ToHexStringLower(hash.GetHashAndReset()), Prepared = offset };
                    Save();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or ObjectDisposedException)
        {
            lock (gate) if (sources.TryGetValue(source.Id, out var current))
            {
                sources[source.Id] = current with { State = ex is OperationCanceledException ? DownloadState.DownloadPreparing : DownloadState.DownloadFailed, Prepared = 0, Hash = "" };
                try { Save(); } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { sources[source.Id] = current with { State = DownloadState.DownloadFailed, Hash = "" }; }
            }
        }
        finally
        {
            if (entered) hashing.Release();
            lock (gate) { if (workers.Remove(source.Id, out var worker)) worker.Cancellation.Dispose(); }
            NotifySources();
        }
    }
    private static DownloadInfo Describe(Source source) => new()
    {
        Id = source.Id, Name = source.Segments[^1], Size = source.Size, Version = source.Version, ExpiresAt = source.Expires,
        State = source.State, PreparedBytes = source.Prepared, Sha256 = source.Hash.Length == 0 ? ByteString.Empty : ByteString.CopyFrom(Convert.FromHexString(source.Hash))
    };
    private int PurgeExpired()
    {
        var now = grants.Clock.GetUtcNow().ToUnixTimeMilliseconds(); var expired = sources.Values.Where(x => x.Expires <= now).ToArray();
        foreach (var source in expired)
        { if (workers.TryGetValue(source.Id, out var worker)) worker.Cancellation.Cancel(); sources.Remove(source.Id); }
        return expired.Length;
    }
    private void Save()
    {
        if (recovery.Needed && sources.Count == 0) return;
        recovery.RequireHealthy();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(sources);
        if (bytes.Length > 4 * 1024 * 1024 - 32 * 1024) throw new StorageException(FileStatus.ResourceExhausted, "Source download state is full.");
        try
        {
            using (var output = new FileStream(path + ".new", FileMode.Create, FileAccess.Write, FileShare.None))
            { output.Write(bytes); output.Flush(true); }
            AtomicStateFile.Commit(path + ".new", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { recovery.Block("Saved source approvals could not be updated. Prepared downloads are paused; original files were kept. Restore storage access and reopen iConnect to review current progress."); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        Task[] tasks;
        lock (gate) { if (disposed) return; disposed = true; foreach (var worker in workers.Values) worker.Cancellation.Cancel(); tasks = workers.Values.Select(x => x.Task).ToArray(); }
        await Task.WhenAll(tasks); hashing.Dispose();
    }
}
