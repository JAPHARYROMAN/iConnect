using Google.Protobuf;
using IConnect.Core;
using IConnect.Protocol;
using System.Security.Cryptography;
using System.Text.Json;

namespace IConnect.Storage.Windows;

public sealed record LocalCopyJob(string Id, string Name, long Size, string SourceLocation, DownloadJob? Transfer, string? MoveDestination = null);

/// <summary>Owner-selected file copies. Source references and receiving state never enter a peer service.</summary>
public sealed partial class WindowsLocalCopies : IAsyncDisposable
{
    private sealed record Source(int Schema, string Id, string Root, string RootIdentity, string[] Segments,
        string Identity, string Version, long Size, string Name, string Display, MoveTarget? Move = null)
    {
        internal string CopyName => Move?.Name ?? Name;
    }
    private readonly object gate = new();
    private readonly SecureWindowsDirectory root, sourceRoot, lease, receivingRoot, downloadsRoot;
    private readonly WindowsDownloadManager receiver;
    private readonly StateRecovery recovery;
    private readonly Dictionary<string, Source> sources = [];
    private readonly Dictionary<string, string> versions = [];
    private readonly HashSet<Task> controls = [];
    private bool closed;
    public event Action? Changed;
    public event Action<DownloadJob>? PayloadChanged;
    public bool HasFinalizationProgress => receiver.HasFinalizationProgress;
    public IncomingFinalizationProgress? ReadFinalization(string id) => receiver.ReadFinalization(id);
    public DownloadJob? FileProgress(string id) => receiver.FindJob(id);
    public Action<WindowsActivityEvent>? Activity { get; set; }
    public WindowsLocalCopies(string profile) : this(profile, new WindowsAttachmentChecks()) { }
    internal WindowsLocalCopies(string profile, IAttachmentChecks checks)
    {
        var directory = Path.Combine(profile, "local-copies"); Directory.CreateDirectory(directory);
        root = SecureWindowsDirectory.OpenRoot(directory);
        try
        {
            try { lease = root.CreateFile("owner.lock"); }
            catch (StorageException ex) when (ex.NativeError is 80 or 183) { lease = root.OpenWritableMetadataChild("owner.lock"); }
            sourceRoot = Folder(root, "sources"); receivingRoot = Folder(root, "receiving"); downloadsRoot = Folder(receivingRoot, "downloads");
            recovery = new(Path.Combine(directory, "sources"));
            receiver = new(Path.Combine(directory, "receiving"), checks, localCopy: true, ownerGate: gate);
            var paths = StateRecovery.Journals(Path.Combine(directory, "sources"));
            if (paths.Length > 8) recovery.Block("More than eight saved local sources exist. Review private copy storage before restarting.");
            foreach (var path in paths.Take(8))
            {
                try
                {
                    using var record = sourceRoot.OpenChild(Path.GetFileName(path));
                    var row = StateRecovery.Read<Source>(path, 64 * 1024); Validate(row);
                    if (Path.GetFileName(path) != row.Id + ".json" || sources.ContainsKey(row.Id)) throw new InvalidDataException("Invalid local source owner.");
                    sources.Add(row.Id, row); versions.Add(row.Id, record.Info.Version);
                }
                catch (Exception ex) when (StateRecovery.IsRecordFailure(ex))
                { recovery.Add(path, "Saved local source access could not be validated. Preserve this metadata before continuing; original and destination files are kept."); }
            }
            receiver.Activity = value => WindowsActivity.Publish(Activity, value);
            receiver.Changed += NotifyPayload;
            receiver.MetadataChanged += _ => Notify();
        }
        catch { downloadsRoot?.Dispose(); receivingRoot?.Dispose(); sourceRoot?.Dispose(); lease?.Dispose(); root.Dispose(); throw; }
    }
    private static SecureWindowsDirectory Folder(SecureWindowsDirectory parent, string name)
    {
        try { return parent.CreateDirectory(name); }
        catch (StorageException ex) when (ex.NativeError is 80 or 183) { return parent.OpenChild(name, true); }
    }
    public IReadOnlyList<LocalCopyJob> Jobs
    {
        get { lock (gate)
        {
            var rows = receiver.Jobs.ToDictionary(x => x.Id);
            return sources.Values.Select(x => new LocalCopyJob(x.Id, x.CopyName, x.Size, x.Display, rows.GetValueOrDefault(x.Id),
                x.Move is { } move ? Path.Combine(move.Directory, move.Name) : null))
                .Concat(rows.Values.Where(x => !sources.ContainsKey(x.Id)).Select(x => new LocalCopyJob(x.Id, x.Name, x.Size, "Source record unavailable", x))).Reverse().ToArray();
        } }
    }
    public IReadOnlyList<StateRecoveryIssue> RecoveryIssues { get { lock (gate) return recovery.Issues.Concat(receiver.RecoveryIssues).ToArray(); } }
    public int PreservedRecords { get { lock (gate) return recovery.ArchivedCount + receiver.PreservedRecords; } }
    public bool Active(string id) { lock (gate) return finishingMoves.Contains(id) || !receiver.WaitAsync(id).IsCompleted; }
    private void NotifyPayload(DownloadJob row)
    {
        if (closed || PayloadChanged is not { } listeners) return;
        foreach (Action<DownloadJob> listener in listeners.GetInvocationList()) try { listener(row); } catch (Exception) { }
    }
    internal T WithReceivingPartProtection<T>(Func<ReceivingPartProtection, T> operation)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            if (controls.Count != 0) throw new IOException("Wait for local copy cleanup before reviewing destination partial files.");
            return receiver.WithReceivingPartProtection(operation);
        }
    }
    private void Healthy() { ObjectDisposedException.ThrowIf(closed, this); recovery.RequireHealthy(); receiver.RequireHealthy(); }
    public string Prepare(LocalCopySelection selection) => Prepare(selection, null);
    private string Prepare(LocalCopySelection selection, MoveTarget? move)
    {
        lock (gate)
        {
            Healthy();
            if (selection.IsDirectory) throw new IOException("Use a folder batch for directory selections.");
            if (sources.Count >= 8 || sources.Values.Sum(x => x.Size) > 16L * 1024 * 1024 * 1024 - selection.Size)
                throw new IOException("Remove an old local copy record first. Up to eight sources and 16 GiB of source sizes are retained.");
            var row = new Source(1, ProtocolRules.NewToken(), selection.Grant.CanonicalPath, selection.Grant.RootIdentity,
                selection.Segments.ToArray(), selection.Identity, selection.Version, selection.Size, selection.Name,
                Path.Combine(new[] { selection.Grant.DisplayPath }.Concat(selection.Segments).ToArray()), move);
            Validate(row);
            using var file = sourceRoot.CreateFile(row.Id + ".json.new");
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(row); if (bytes.Length > 64 * 1024) throw new IOException("The source record exceeds its limit.");
                file.Write(0, bytes); file.Flush(); file.RenameTo(sourceRoot, row.Id + ".json");
                sources.Add(row.Id, row); versions.Add(row.Id, SecureWindowsDirectory.Inspect(file.Handle).Version);
            }
            catch { if (!sources.ContainsKey(row.Id)) file.Delete(); throw; }
            Notify(); return row.Id;
        }
    }
    public string ChooseDestination(string id, string destination, DownloadCollision collision)
    {
        lock (gate)
        {
            Healthy(); var source = sources[id]; RequireRecord(source);
            if (source.Move is not null) throw new IOException("Start the move copy using its recorded destination.");
            if (!Enum.IsDefined(collision)) throw new InvalidDataException("Choose a supported collision policy.");
            if (receiver.Jobs.Any(x => x.Id == id)) throw new IOException("This copy already has a destination. Use its saved progress.");
            return StartCopy(source, destination, collision);
        }
    }
    private string StartCopy(Source source, string destination, DownloadCollision collision, string? identity = null)
    {
        var entry = new FileEntry { Id = source.Id, Name = source.CopyName, Version = source.Version, Size = source.Size, Permissions = (uint)StoragePermission.ReadOnly };
        var result = receiver.Start("", entry, destination, collision, Reader(source), id: source.Id, destinationIdentity: identity);
        NotifyWhenIdle(source.Id);
        return result;
    }
    public Task Resume(string id)
    {
        lock (gate)
        {
            Healthy(); RequireIdleMove(id); var job = receiver.Jobs.Single(x => x.Id == id);
            WindowsDownloadManager.Request? reader = null;
            if (job.Offset != job.Size || job.Hash.Length != 64)
            {
                if (!sources.TryGetValue(id, out var source)) throw new IOException("Source access is missing. Cancel this copy and select the original again.");
                RequireRecord(source); reader = Reader(source);
            }
            var result = receiver.Resume(id, reader); NotifyWhenIdle(id); return result;
        }
    }
    public void Pause(string id) { lock (gate) receiver.Pause(id); }
    public Task WaitAsync(string id) => receiver.WaitAsync(id);
    public Task Cancel(string id)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            RequireIdleMove(id);
            if (!receiver.Jobs.Any(x => x.Id == id)) { RemoveSource(id); Notify(); return Task.CompletedTask; }
            var task = receiver.Cancel(id); controls.Add(task);
            _ = task.ContinueWith(_ => { lock (gate) controls.Remove(task); Notify(); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
    }
    public void Remove(string id)
    { lock (gate) { ObjectDisposedException.ThrowIf(closed, this); RequireIdleMove(id); receiver.RemoveRecord(id); RemoveSource(id); Notify(); } }
    public Task Recheck(string id) { lock (gate) { Healthy(); RequireIdleMove(id); var result = receiver.CheckPublishedAsync(id); NotifyWhenIdle(id); return result; } }
    public void Preserve(string id)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            if (receiver.Jobs.Any(x => Active(x.Id))) throw new IOException("Pause local copies before preserving metadata.");
            if (recovery.Issues.Any(x => x.Id == id)) recovery.Archive(id); else receiver.ArchiveRecovery(id);
            Notify();
        }
    }
    public void ClearPreserved()
    { lock (gate) { ObjectDisposedException.ThrowIf(closed, this); if (receiver.Jobs.Any(x => Active(x.Id))) throw new IOException("Pause local copies first."); recovery.ClearArchives(); receiver.ClearPreservedRecords(); Notify(); } }
    private void RemoveSource(string id)
    {
        if (!sources.TryGetValue(id, out var source)) return;
        RequireRecord(source);
        using var file = sourceRoot.OpenMutableChild(id + ".json"); if (file.Info.Version != versions[id]) throw new IOException("Saved source access changed. Restart and review it.");
        file.Delete(); sources.Remove(id); versions.Remove(id);
        moveReviews.Remove(id);
    }
    private void RequireRecord(Source source)
    {
        using var file = sourceRoot.OpenChild(source.Id + ".json");
        if (file.Info.Version != versions[source.Id]) throw new IOException("Saved source access changed. Restart and review it.");
    }
    private static void Validate(Source source)
    {
        static bool Hash(string s) => s.Length == 64 && s.All(char.IsAsciiHexDigitLower);
        if (source.Schema != 1 || !ProtocolRules.TokenPattern().IsMatch(source.Id) || !SecureWindowsDirectory.IsLocalVolumePath(source.Root) ||
            source.RootIdentity.Length is < 1 or > 256 || source.Identity.Length is < 1 or > 256 || !Hash(source.Version) ||
            source.Size is < 0 or > StorageRules.MaxTransferSize || source.Segments.Length is < 1 or > 128 || source.Display.Length is < 3 or > 8192)
            throw new InvalidDataException("Invalid saved local source.");
        StorageRules.ValidateName(source.Name); foreach (var name in source.Segments) StorageRules.ValidateName(name);
        if (!source.Name.Equals(source.Segments[^1], StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The source name changed.");
        if (source.Move is { } move)
        {
            StorageRules.ValidateName(move.Name);
            if (!SecureWindowsDirectory.IsLocalVolumePath(move.CanonicalPath) || move.Identity.Length is < 1 or > 256 ||
                move.Directory.Length is < 3 or > 8192 || move.Directory[1] != ':' || move.Directory[2] != '\\')
                throw new InvalidDataException("Invalid verified move destination.");
        }
    }
    private static SecureWindowsDirectory Open(Source source)
    {
        var file = SecureWindowsDirectory.OpenPath(source.Root, source.RootIdentity, source.Segments, false);
        if (file.Info.Identity != source.Identity || file.Info.Version != source.Version || file.Info.Size != source.Size)
        { file.Dispose(); throw new IOException("The selected source changed. Select it as a new copy."); }
        return file;
    }
    private WindowsDownloadManager.Request Reader(Source source)
    {
        byte[]? hash = null;
        return (value, ct) => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(closed, this);
            var request = value.Clone(); request.RequestId = ProtocolRules.NewToken();
            var result = new FileResponse { RequestId = request.RequestId };
            switch (request.Operation)
            {
                case FileOperation.PrepareDownload:
                case FileOperation.DownloadStatus:
                    if (request.Operation == FileOperation.PrepareDownload ? request.NodeId != source.Id || request.ExpectedVersion != source.Version : request.TransferId != source.Id)
                        throw new InvalidDataException("This local source does not match the copy.");
                    using (var file = Open(source))
                    {
                        if (hash is null)
                        {
                            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[StorageRules.MaxReadRange]; var offset = 0L;
                            while (offset < source.Size)
                            {
                                ct.ThrowIfCancellationRequested(); var read = file.Read(offset, buffer);
                                if (read == 0 || read > source.Size - offset) throw new IOException("The source size changed.");
                                digest.AppendData(buffer, 0, read); offset += read;
                            }
                            ct.ThrowIfCancellationRequested(); hash = digest.GetHashAndReset();
                        }
                    }
                    result.Download = new DownloadInfo { Id = source.Id, Name = source.CopyName, Version = source.Version, Size = source.Size,
                        PreparedBytes = source.Size, ExpiresAt = StorageRules.MaxTimestamp, State = DownloadState.DownloadReady, Sha256 = ByteString.CopyFrom(hash) }; break;
                case FileOperation.DownloadRead:
                    if (request.TransferId != source.Id || hash is null || request.Offset < 0 || request.Offset > source.Size || request.Length is < 1 or > StorageRules.MaxReadRange)
                        throw new InvalidDataException("Invalid local source range.");
                    using (var file = Open(source))
                    {
                        var bytes = new byte[(int)Math.Min(request.Length, source.Size - request.Offset)]; var read = 0;
                        while (read < bytes.Length) { ct.ThrowIfCancellationRequested(); var count = RandomAccess.Read(file.Handle, bytes.AsSpan(read), request.Offset + read); if (count == 0) throw new IOException("The source changed."); read += count; }
                        result.Content = ByteString.CopyFrom(bytes); result.EndOfFile = request.Offset + read == source.Size;
                    }
                    break;
                case FileOperation.ReleaseDownload:
                    if (request.TransferId != source.Id) throw new InvalidDataException("Invalid local source release."); break;
                default: throw new InvalidOperationException("This local copy operation is unavailable.");
            }
            return result;
        }, ct);
    }
    private void NotifyWhenIdle(string id) => _ = receiver.WaitAsync(id).ContinueWith(completed => { _ = completed.Exception; Notify(); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    private void Notify() { if (closed || Changed is not { } listeners) return; foreach (Action listener in listeners.GetInvocationList()) try { listener(); } catch (Exception) { } }
    public async ValueTask DisposeAsync()
    {
        Task[] pending;
        lock (gate) { if (closed) return; closed = true; foreach (var row in receiver.Jobs) receiver.Pause(row.Id); pending = controls.ToArray(); }
        try { await receiver.DisposeAsync(); await Task.WhenAll(pending); }
        finally { downloadsRoot.Dispose(); receivingRoot.Dispose(); sourceRoot.Dispose(); lease.Dispose(); root.Dispose(); }
    }
}
