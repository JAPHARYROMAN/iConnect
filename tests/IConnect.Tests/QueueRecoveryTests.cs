using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using Xunit;

namespace IConnect.Tests;

public sealed class QueueRecoveryTests : IAsyncLifetime
{
    private string directory = "", source = "", target = "", receiver = "";
    private readonly string peer = new('b', 64);
    private WindowsFileService service = null!;
    private DownloadSourceStore tickets = null!;
    private StorageGrantStore grants = null!;
    public Task InitializeAsync()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Directory.Build.props"))) root = root.Parent;
        directory = Path.Combine(root!.FullName, ".local", "queue-recovery-tests", ProtocolRules.NewToken());
        source = Path.Combine(directory, "source"); target = Path.Combine(directory, "received"); receiver = Path.Combine(directory, "receiver");
        Directory.CreateDirectory(Path.Combine(source, "Bundle", "Empty")); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(source, "Bundle", "notes.txt"), "Recovery fixture content."); File.WriteAllText(Path.Combine(source, "file.txt"), "Verified receiver content.");
        grants = new StorageGrantStore(Path.Combine(directory, "owner")); grants.Add(peer, source);
        tickets = new(grants, Path.Combine(directory, "owner")); service = new(grants, tickets); return Task.CompletedTask;
    }
    private Task<FileResponse> Rpc(FileRequest request, CancellationToken ct)
    { var copy = request.Clone(); copy.RequestId = ProtocolRules.NewToken(); return service.ExecuteAsync(peer, copy, ct); }
    private async Task<FileEntry[]> Entries()
    {
        var root = (await Rpc(new() { Operation = FileOperation.ListRoots }, default)).Entries.Single();
        return (await Rpc(new() { Operation = FileOperation.ListDirectory, NodeId = root.Id }, default)).Entries.ToArray();
    }
    [Fact]
    public async Task Damaged_file_progress_does_not_prevent_startup_and_archiving_preserves_every_copy()
    {
        var entry = (await Entries()).Single(x => x.Name == "file.txt");
        await using (var first = new WindowsDownloadManager(receiver)) { var id = first.Start(peer, entry, target, DownloadCollision.KeepBoth, Rpc); await first.WaitAsync(id); Assert.Equal(DownloadPhase.Completed, first.Jobs.Single().Phase); }
        var badId = ProtocolRules.NewToken(); var path = Path.Combine(receiver, "downloads", badId + ".json");
        File.WriteAllText(path, "{damaged"); File.WriteAllText(path + ".new", "unfinished metadata");
        var unknown = Path.Combine(target, ".iconnect-transfer-" + badId + ".part"); File.WriteAllText(unknown, "unknown partial bytes");
        await using var loaded = new WindowsDownloadManager(receiver); Assert.Single(loaded.Jobs); var issue = Assert.Single(loaded.RecoveryIssues); Assert.True(issue.CanArchive);
        Assert.Throws<IOException>(() => loaded.Start(peer, entry, target, DownloadCollision.KeepBoth, Rpc));
        Assert.Equal("{damaged", File.ReadAllText(path)); loaded.ArchiveRecovery(issue.Id); Assert.Empty(loaded.RecoveryIssues);
        var saved = Directory.GetFiles(Path.Combine(receiver, "downloads", "recovery"), "*.json*", SearchOption.AllDirectories);
        Assert.Equal(2, saved.Length); Assert.Contains(saved, file => File.ReadAllText(file) == "{damaged");
        var unexpected = Path.Combine(Path.GetDirectoryName(saved[0])!, "unexpected.bin"); File.WriteAllText(unexpected, "preserve unknown content");
        Assert.Throws<IOException>(loaded.ClearPreservedRecords); Assert.Equal("preserve unknown content", File.ReadAllText(unexpected)); File.Delete(unexpected);
        loaded.ClearPreservedRecords(); Assert.Equal(0, loaded.PreservedRecords);
        Assert.Equal("unknown partial bytes", File.ReadAllText(unknown)); Assert.Equal("Verified receiver content.", File.ReadAllText(Path.Combine(target, "file.txt")));
        var next = loaded.Start(peer, entry, target, DownloadCollision.KeepBoth, Rpc); await loaded.WaitAsync(next); Assert.All(loaded.Jobs, x => Assert.Equal(DownloadPhase.Completed, x.Phase));
    }
    [Fact]
    public async Task Orphaned_staged_metadata_is_reviewed_and_changed_records_are_not_archived_under_an_old_decision()
    {
        var folder = Path.Combine(receiver, "downloads"); Directory.CreateDirectory(folder);
        var id = ProtocolRules.NewToken(); var path = Path.Combine(folder, id + ".json"); File.WriteAllText(path + ".new", "uncommitted");
        await using (var first = new WindowsDownloadManager(receiver))
        {
            var issue = Assert.Single(first.RecoveryIssues); File.WriteAllText(path + ".new", "changed after the review was shown");
            Assert.Throws<IOException>(() => first.ArchiveRecovery(issue.Id)); Assert.Single(first.RecoveryIssues); Assert.True(File.Exists(path + ".new"));
        }
        await using var reviewed = new WindowsDownloadManager(receiver); reviewed.ArchiveRecovery(reviewed.RecoveryIssues.Single().Id);
        Assert.Empty(reviewed.RecoveryIssues); Assert.False(File.Exists(path + ".new"));
    }
    [Fact]
    public async Task A_damaged_batch_cannot_promote_its_known_child_to_a_standalone_transfer()
    {
        var entry = (await Entries()).Single(x => x.Name == "file.txt"); var batchId = ProtocolRules.NewToken(); string child;
        await using (var files = new WindowsDownloadManager(receiver)) { child = files.Start(peer, entry, target, DownloadCollision.KeepBoth, Rpc, batchId: batchId); await files.WaitAsync(child); }
        var path = Path.Combine(receiver, "batches", batchId + ".json"); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "null");
        await using var loaded = new WindowsDownloadManager(receiver); await using var batches = new WindowsBatchDownloads(receiver, loaded);
        var issue = Assert.Single(batches.RecoveryIssues); Assert.Empty(batches.Jobs); Assert.Equal(batchId, loaded.Jobs.Single().BatchId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => loaded.Resume(child, Rpc));
        batches.ArchiveRecovery(issue.Id); await Assert.ThrowsAsync<InvalidOperationException>(() => loaded.Resume(child, Rpc));
        loaded.RemoveRecord(child); Assert.Equal("Verified receiver content.", File.ReadAllText(Path.Combine(target, "file.txt")));
    }
    [Fact]
    public async Task A_valid_completed_batch_remains_visible_beside_a_bad_record_and_can_be_recovered_without_destination_changes()
    {
        var folder = (await Entries()).Single(x => x.IsDirectory); var plan = await TransferManifests.BuildAsync([new(folder, [folder.Name])], Rpc, default);
        await using (var files = new WindowsDownloadManager(receiver)) await using (var batches = new WindowsBatchDownloads(receiver, files))
        { var id = batches.Start(peer, plan, target, DownloadCollision.KeepBoth, Rpc); await batches.WaitAsync(id); Assert.Equal(BatchPhase.Completed, batches.Jobs.Single().Phase); }
        var bad = Path.Combine(receiver, "batches", ProtocolRules.NewToken() + ".json"); File.WriteAllText(bad, "[]");
        await using var restoredFiles = new WindowsDownloadManager(receiver); await using var restoredBatches = new WindowsBatchDownloads(receiver, restoredFiles);
        Assert.Single(restoredBatches.Jobs); Assert.Equal(BatchPhase.Completed, restoredBatches.Jobs.Single().Phase);
        restoredBatches.ArchiveRecovery(restoredBatches.RecoveryIssues.Single().Id);
        Assert.Equal("Recovery fixture content.", File.ReadAllText(Path.Combine(target, "Bundle", "notes.txt"))); Assert.True(Directory.Exists(Path.Combine(target, "Bundle", "Empty")));
    }
    [Fact]
    public async Task Damaged_source_ticket_state_preserves_browsing_and_cannot_be_overwritten_by_new_preparation()
    {
        var profile = Path.Combine(directory, "damaged-source"); Directory.CreateDirectory(profile);
        var path = Path.Combine(profile, "download-sources.json"); File.WriteAllText(path, "{bad tickets");
        await using var sourceTickets = new DownloadSourceStore(grants, profile); using var host = new WindowsFileService(grants, sourceTickets);
        Task<FileResponse> Request(FileRequest value) { var copy = value.Clone(); copy.RequestId = ProtocolRules.NewToken(); return host.ExecuteAsync(peer, copy, default); }
        var root = (await Request(new() { Operation = FileOperation.ListRoots })).Entries.Single();
        var file = (await Request(new() { Operation = FileOperation.ListDirectory, NodeId = root.Id })).Entries.Single(x => x.Name == "file.txt");
        var prepare = new FileRequest { Operation = FileOperation.PrepareDownload, NodeId = file.Id, ExpectedVersion = file.Version };
        Assert.Equal(FileStatus.IoError, (await Request(prepare)).Status); Assert.Equal("{bad tickets", File.ReadAllText(path));
        sourceTickets.Release(peer, ProtocolRules.NewToken()); sourceTickets.CancelScope(ProtocolRules.NewToken());
        sourceTickets.ArchiveRecovery(sourceTickets.RecoveryIssues.Single().Id); Assert.Empty(sourceTickets.RecoveryIssues);
        Assert.Equal(FileStatus.Ok, (await Request(prepare)).Status); Assert.Equal("Verified receiver content.", File.ReadAllText(Path.Combine(source, "file.txt")));
    }
    public async Task DisposeAsync() { service.Dispose(); await tickets.DisposeAsync(); Directory.Delete(directory, true); }
}
