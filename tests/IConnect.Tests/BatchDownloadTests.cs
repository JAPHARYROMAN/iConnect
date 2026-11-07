using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using Google.Protobuf;
using System.Security.Cryptography;
using Xunit;

namespace IConnect.Tests;

public sealed partial class BatchDownloadTests : IAsyncLifetime
{
    private string sandbox = "", source = "", target = "", state = "";
    private readonly string peer = new('b', 64);
    private StorageGrantStore grants = null!;
    private DownloadSourceStore sources = null!;
    private WindowsFileService service = null!;
    private byte[] expected = [];
    public Task InitializeAsync()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "Directory.Build.props"))) repo = repo.Parent;
        sandbox = Path.Combine(repo!.FullName, ".local", "batch-tests", ProtocolRules.NewToken());
        source = Path.Combine(sandbox, "source"); target = Path.Combine(sandbox, "target"); state = Path.Combine(sandbox, "receiver");
        Directory.CreateDirectory(Path.Combine(source, "bundle", "empty")); Directory.CreateDirectory(Path.Combine(source, "bundle", "nested")); Directory.CreateDirectory(target);
        expected = RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 17);
        File.WriteAllBytes(Path.Combine(source, "bundle", "nested", "maelezo-文書.bin"), expected);
        File.WriteAllText(Path.Combine(source, "bundle", "z.txt"), "nested document");
        File.WriteAllText(Path.Combine(source, "extra.txt"), "independent file");
        grants = new StorageGrantStore(Path.Combine(sandbox, "owner")); grants.Add(peer, source, label: "Fixture documents");
        sources = new DownloadSourceStore(grants, Path.Combine(sandbox, "owner")); service = new WindowsFileService(grants, sources);
        return Task.CompletedTask;
    }
    private async Task<FileResponse> Rpc(FileRequest request, CancellationToken ct)
    {
        var copy = request.Clone(); copy.RequestId = ProtocolRules.NewToken(); return await service.ExecuteAsync(peer, copy, ct);
    }
    private async Task<TransferManifest> Plan(bool multiple = false, TransferManifests.Request? request = null)
    {
        var root = (await Rpc(new FileRequest { Operation = FileOperation.ListRoots }, default)).Entries.Single();
        var children = await TransferManifests.ListAsync(Rpc, root, default);
        var selection = children.Where(x => x.Name == "bundle" || multiple && x.Name == "extra.txt")
            .Select(x => new TransferSelection(x, [x.Name])).ToArray();
        return await TransferManifests.BuildAsync(selection, request ?? Rpc, default);
    }
    [Fact]
    public async Task Nested_empty_and_multiple_selected_items_are_verified_without_replacing_existing_data()
    {
        Directory.CreateDirectory(Path.Combine(target, "bundle")); File.WriteAllText(Path.Combine(target, "bundle", "owner.txt"), "keep");
        var plan = await Plan(true);
        await using var files = new WindowsDownloadManager(state); await using var batches = new WindowsBatchDownloads(state, files);
        var id = batches.Start(peer, plan, target, DownloadCollision.KeepBoth, Rpc);
        await batches.WaitAsync(id).WaitAsync(TimeSpan.FromSeconds(20));
        var result = Assert.Single(batches.Jobs);
        Assert.Equal(BatchPhase.Completed, result.Phase); Assert.Equal(plan.Items.Length, result.Completed);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "bundle", "owner.txt")));
        Assert.True(Directory.Exists(Path.Combine(target, "bundle (2)", "empty")));
        Assert.Equal(expected, File.ReadAllBytes(Path.Combine(target, "bundle (2)", "nested", "maelezo-文書.bin")));
        Assert.Equal("nested document", File.ReadAllText(Path.Combine(target, "bundle (2)", "z.txt")));
        Assert.Equal("independent file", File.ReadAllText(Path.Combine(target, "extra.txt")));
        Assert.All(result.Items.Where(x => !x.Source.Directory), x => Assert.Equal(64, x.Hash.Length));
        Assert.Empty(files.Jobs); Assert.True(batches.CanRemove(id)); batches.Remove(id);
        Assert.True(File.Exists(Path.Combine(target, "extra.txt")));
    }
    [Theory]
    [InlineData(DownloadCollision.Skip)]
    [InlineData(DownloadCollision.Fail)]
    public async Task Folder_collisions_report_each_uncopied_item_and_do_not_merge(DownloadCollision collision)
    {
        Directory.CreateDirectory(Path.Combine(target, "bundle")); File.WriteAllText(Path.Combine(target, "bundle", "owner.txt"), "keep");
        await using var files = new WindowsDownloadManager(state); await using var batches = new WindowsBatchDownloads(state, files);
        var id = batches.Start(peer, await Plan(true), target, collision, Rpc); await batches.WaitAsync(id);
        var result = Assert.Single(batches.Jobs); Assert.Equal(BatchPhase.CompletedWithIssues, result.Phase);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "bundle", "owner.txt")));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(target, "bundle")));
        Assert.DoesNotContain(result.Items.Where(x => x.Source.SourcePath[0] == "bundle"), x => x.Phase == BatchItemPhase.Completed);
        Assert.Equal("independent file", File.ReadAllText(Path.Combine(target, "extra.txt")));
        Assert.Empty(files.Jobs);
    }
    [Fact]
    public async Task Paused_batch_rebinds_pending_sources_and_resumes_checkpoints_after_both_sides_restart()
    {
        var plan = await Plan(); string id;
        await using (var files = new WindowsDownloadManager(state))
        await using (var batches = new WindowsBatchDownloads(state, files))
        {
            async Task<FileResponse> Interrupt(FileRequest value, CancellationToken ct)
            {
                if (value.Operation == FileOperation.DownloadRead && value.Offset >= 1024 * 1024) batches.Pause(batches.Jobs.Single().Id);
                return await Rpc(value, ct);
            }
            id = batches.Start(peer, plan, target, DownloadCollision.Fail, Interrupt); await batches.WaitAsync(id);
            Assert.Equal(BatchPhase.Paused, batches.Jobs.Single().Phase);
            Assert.True(files.Jobs.Single().Offset >= 1024 * 1024);
        }
        service.Dispose(); await sources.DisposeAsync();
        sources = new DownloadSourceStore(grants, Path.Combine(sandbox, "owner")); service = new WindowsFileService(grants, sources);
        await using var resumedFiles = new WindowsDownloadManager(state); await using var resumed = new WindowsBatchDownloads(state, resumedFiles);
        await resumed.Resume(id, Rpc).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(BatchPhase.Completed, resumed.Jobs.Single().Phase);
        Assert.Equal(expected, File.ReadAllBytes(Path.Combine(target, "bundle", "nested", "maelezo-文書.bin")));
        Assert.Equal("nested document", File.ReadAllText(Path.Combine(target, "bundle", "z.txt")));
        Assert.Empty(resumedFiles.Jobs);
    }
    [Fact]
    public async Task Replaced_destination_parent_is_denied_before_resuming_or_creating_more_files()
    {
        await using var files = new WindowsDownloadManager(state); await using var batches = new WindowsBatchDownloads(state, files);
        async Task<FileResponse> Interrupt(FileRequest value, CancellationToken ct)
        {
            if (value.Operation == FileOperation.DownloadRead && value.Offset >= 1024 * 1024) batches.Pause(batches.Jobs.Single().Id);
            return await Rpc(value, ct);
        }
        var id = batches.Start(peer, await Plan(), target, DownloadCollision.Fail, Interrupt); await batches.WaitAsync(id);
        var nested = Path.Combine(target, "bundle", "nested"); Directory.Move(nested, nested + "-original"); Directory.CreateDirectory(nested);
        await batches.Resume(id, Rpc);
        Assert.Equal(BatchPhase.Interrupted, batches.Jobs.Single().Phase);
        Assert.Contains("replaced", batches.Jobs.Single().Error);
        Assert.Empty(Directory.EnumerateFileSystemEntries(nested)); Assert.False(File.Exists(Path.Combine(target, "bundle", "z.txt")));
        Assert.NotEmpty(Directory.EnumerateFiles(nested + "-original", ".iconnect-*.part"));
    }
    [Fact]
    public async Task Interrupted_directory_identity_commit_requires_review_instead_of_adopting_unknown_content()
    {
        var plan = await Plan(); string id; string blocker = "";
        await using (var files = new WindowsDownloadManager(state))
        await using (var batches = new WindowsBatchDownloads(state, files))
        {
            batches.Changed += () =>
            {
                var job = batches.Jobs.SingleOrDefault();
                if (blocker.Length == 0 && job?.Items.Any(x => x.Phase == BatchItemPhase.Creating) == true)
                { blocker = Path.Combine(state, "batches", job.Id + ".json.new"); Directory.CreateDirectory(blocker); }
            };
            id = batches.Start(peer, plan, target, DownloadCollision.Fail, Rpc); await batches.WaitAsync(id);
            Assert.Equal(BatchPhase.Interrupted, batches.Jobs.Single().Phase);
        }
        Assert.True(Directory.Exists(Path.Combine(target, "bundle"))); Directory.Delete(blocker);
        File.WriteAllText(Path.Combine(target, "bundle", "owner.txt"), "unknown owner content");
        await using var resumedFiles = new WindowsDownloadManager(state); await using var resumed = new WindowsBatchDownloads(state, resumedFiles);
        await resumed.Resume(id, Rpc);
        Assert.Equal(BatchPhase.Interrupted, resumed.Jobs.Single().Phase); Assert.Contains("identity", resumed.Jobs.Single().Error);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(target, "bundle")));
        Assert.Empty(resumedFiles.Jobs);
    }
    [Fact]
    public async Task Manifest_paging_and_unsupported_names_have_bounded_explicit_results()
    {
        for (var i = 0; i < 40; i++) File.WriteAllText(Path.Combine(source, "bundle", $"small-{i:D2}.txt"), "small");
        async Task<FileResponse> Unsupported(FileRequest value, CancellationToken ct)
        {
            var response = await Rpc(value, ct);
            foreach (var item in response.Entries.Where(x => x.Name == "z.txt")) item.Name = "../escape.txt";
            return response;
        }
        var plan = await Plan(request: Unsupported);
        Assert.Equal(45, plan.Items.Length); Assert.Single(plan.Items, x => x.Error.Length != 0);
        await using var files = new WindowsDownloadManager(state); await using var batches = new WindowsBatchDownloads(state, files);
        var id = batches.Start(peer, plan, target, DownloadCollision.Fail, Rpc);
        // More than forty real attachment-service checks share the bounded OS
        // broker pool with other integration tests. This is not a throughput test.
        try { await batches.WaitAsync(id).WaitAsync(TimeSpan.FromMinutes(2)); }
        catch (TimeoutException ex)
        {
            var stalled = batches.Jobs.Single();
            throw new TimeoutException($"The 45-item attachment integration batch exceeded two minutes: {stalled.Phase}; " +
                string.Join(", ", stalled.Items.GroupBy(x => x.Phase).Select(x => $"{x.Key}={x.Count()}")) + "; " + stalled.Error, ex);
        }
        Assert.True(batches.Jobs.Single().Phase == BatchPhase.CompletedWithIssues,
            $"{batches.Jobs.Single().Phase}: {batches.Jobs.Single().Error} / {string.Join("; ", batches.Jobs.Single().Items.Where(x => x.Error.Length != 0).Select(x => x.Error))}");
        Assert.False(File.Exists(Path.Combine(target, "escape.txt"))); Assert.False(File.Exists(Path.Combine(sandbox, "escape.txt")));
        Assert.Equal(40, Directory.EnumerateFiles(Path.Combine(target, "bundle"), "small-*").Count());
    }
    [Theory]
    [InlineData("parent")]
    [InlineData("duplicate")]
    [InlineData("page")]
    public async Task Ambiguous_parent_duplicate_and_repeated_page_manifests_abort_before_any_destination_write(string fault)
    {
        async Task<FileResponse> Bad(FileRequest value, CancellationToken ct)
        {
            var response = await Rpc(value, ct);
            if (value.Operation == FileOperation.ListDirectory)
            {
                if (fault == "parent" && response.Entries.Count != 0) response.Entries[0].ParentId = ProtocolRules.NewToken();
                if (fault == "duplicate" && response.Entries.Count > 1) response.Entries[1].Name = response.Entries[0].Name.ToUpperInvariant();
                if (fault == "page") { response.Status = FileStatus.Ok; response.Entries.Clear(); response.Continuation = new string('x', 43); }
            }
            return response;
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => Plan(request: Bad)); Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }
    [Fact]
    public async Task A_failed_file_does_not_hide_sibling_success_and_cancel_cleans_only_its_private_partial()
    {
        async Task<FileResponse> Corrupt(FileRequest value, CancellationToken ct)
        {
            var response = await Rpc(value, ct);
            if (value.Operation == FileOperation.DownloadRead && value.Offset == 0 && response.Content.Length > 100)
            { var bytes = response.Content.ToByteArray(); bytes[0] ^= 1; response.Content = ByteString.CopyFrom(bytes); }
            return response;
        }
        await using var files = new WindowsDownloadManager(state); await using var batches = new WindowsBatchDownloads(state, files);
        var id = batches.Start(peer, await Plan(true), target, DownloadCollision.Fail, Corrupt); await batches.WaitAsync(id).WaitAsync(TimeSpan.FromSeconds(20));
        var result = batches.Jobs.Single(); Assert.Equal(BatchPhase.CompletedWithIssues, result.Phase);
        Assert.Single(result.Items, x => x.Phase == BatchItemPhase.Failed);
        Assert.Equal("independent file", File.ReadAllText(Path.Combine(target, "extra.txt")));
        Assert.Equal("nested document", File.ReadAllText(Path.Combine(target, "bundle", "z.txt")));
        Assert.False(File.Exists(Path.Combine(target, "bundle", "nested", "maelezo-文書.bin")));
        Assert.False(batches.CanRemove(id));
        await Task.WhenAll(batches.Cancel(id), batches.Cancel(id));
        Assert.True(batches.CanRemove(id)); Assert.Empty(files.Jobs);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(target, "bundle", "nested")));
        Assert.Equal("independent file", File.ReadAllText(Path.Combine(target, "extra.txt")));
    }
    [Fact]
    public async Task Changed_source_is_never_copied_under_an_earlier_manifest_version()
    {
        var plan = await Plan(); File.WriteAllText(Path.Combine(source, "bundle", "nested", "maelezo-文書.bin"), "replacement");
        await using var files = new WindowsDownloadManager(state); await using var batches = new WindowsBatchDownloads(state, files);
        var id = batches.Start(peer, plan, target, DownloadCollision.Fail, Rpc); await batches.WaitAsync(id);
        Assert.Equal(BatchPhase.Interrupted, batches.Jobs.Single().Phase); Assert.Contains("changed", batches.Jobs.Single().Error);
        Assert.Contains(batches.Jobs.Single().Items, x => x.Phase == BatchItemPhase.Interrupted && x.Error.Contains("changed", StringComparison.Ordinal));
        Assert.Empty(files.Jobs); Assert.Empty(Directory.EnumerateFiles(Path.Combine(target, "bundle", "nested")));
    }
    public async Task DisposeAsync()
    {
        service.Dispose(); await sources.DisposeAsync(); Directory.Delete(sandbox, true);
    }
}
