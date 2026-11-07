using IConnect.AppModel;
using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Xunit;

namespace IConnect.Tests;

public sealed partial class LocalBatchCopyTests : IDisposable
{
    private readonly string sandbox, source, destination, profile;
    private sealed class Checks(Func<CancellationToken, Task>? check = null) : IAttachmentChecks
    {
        public Task CheckAsync(string path, string identity, CancellationToken ct) => check?.Invoke(ct) ?? Task.CompletedTask;
    }
    public LocalBatchCopyTests()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory); while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "Directory.Build.props"))) repo = repo.Parent;
        sandbox = Path.Combine(repo!.FullName, ".local", "local-batch-copy-tests", ProtocolRules.NewToken());
        source = Path.Combine(sandbox, "source"); destination = Path.Combine(sandbox, "destination"); profile = Path.Combine(sandbox, "profile");
        Directory.CreateDirectory(Path.Combine(source, "Reports", "Empty")); Directory.CreateDirectory(Path.Combine(source, "Reports", "Nested")); Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(source, "Reports", "Nested", "文書.txt"), "Generated owner document.\nSalama!\n");
        File.WriteAllBytes(Path.Combine(source, "Reports", "zero.bin"), []); File.WriteAllText(Path.Combine(source, "separate.txt"), "Generated separate selection.");
    }
    private async Task<LocalCopySelection[]> Capture(params string[] names)
    {
        using var local = new LocalFileLocations(profile); local.Add(source);
        var root = (await local.RequestAsync(new() { Operation = FileOperation.ListRoots }, default)).Entries.Single();
        var rows = await ExplorerCatalog.LoadAsync([root], local.RequestAsync, default);
        var result = new List<LocalCopySelection>(); foreach (var name in names) result.Add(await local.CaptureBatchItemAsync(rows.Rows.Single(x => x.Entry.Name == name).Entry)); return result.ToArray();
    }
    private static async Task<BatchDownload> Finish(WindowsLocalBatchCopies copies, string id)
    { await copies.WaitAsync(id).WaitAsync(TimeSpan.FromSeconds(45)); return copies.Jobs.Single(x => x.Id == id).Transfer!; }
    private WindowsLocalBatchCopies Open() => new(profile, new Checks());
    private string Record(string id) => Path.Combine(profile, "local-batch-copies", "sources", id + ".json");
    private string BatchReceipt(string id) => Path.Combine(profile, "local-batch-copies", "receiving", "batches", id + ".json");

    [Fact]
    public async Task Completed_destination_check_uses_durable_identities_after_restart_and_ignores_original_changes()
    {
        string id;
        await using (var copies = Open())
        {
            id = await copies.PrepareAsync(await Capture("Reports"));
            copies.ChooseDestination(id, destination, DownloadCollision.Fail);
            var result = await Finish(copies, id);
            Assert.True(result.HasBoundDestination);
            foreach (var item in result.Items.Where(x => !x.Source.Directory))
            {
                Assert.NotEmpty(item.FileIdentity);
                Assert.Null(copies.FileProgress(item.DownloadId));
            }
        }
        File.WriteAllText(Path.Combine(source, "Reports", "Nested", "文書.txt"), "Original changed after the copy.");
        File.WriteAllText(Path.Combine(destination, "unrelated.txt"), "Unrelated item beside copied tree.");
        await using var reopened = Open();
        var verified = await reopened.VerifyCompletedAsync(id);
        Assert.Equal(destination, verified.Destination);
        Assert.Equal(2, verified.Files);
        Assert.Equal(3, verified.Folders);
        Assert.Equal(new FileInfo(Path.Combine(destination, "Reports", "Nested", "文書.txt")).Length, verified.Bytes);
        Assert.False(reopened.Active(id));
        Assert.True(Directory.Exists(Path.Combine(destination, "Reports", "Empty")));
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("file")]
    [InlineData("folder")]
    [InlineData("entry")]
    public async Task Completed_destination_check_refuses_changed_bytes_replacements_and_extra_entries(string change)
    {
        await using var copies = Open();
        var id = await copies.PrepareAsync(await Capture("Reports"));
        copies.ChooseDestination(id, destination, DownloadCollision.Fail);
        Assert.Equal(BatchPhase.Completed, (await Finish(copies, id)).Phase);
        var output = Path.Combine(destination, "Reports");
        var file = Path.Combine(output, "Nested", "文書.txt");
        var bytes = File.ReadAllBytes(file);
        switch (change)
        {
            case "bytes":
                bytes[0] ^= 1; File.WriteAllBytes(file, bytes);
                break;
            case "file":
                File.Move(file, Path.Combine(sandbox, "previous-file")); File.WriteAllBytes(file, bytes);
                break;
            case "folder":
                Directory.Move(Path.Combine(output, "Empty"), Path.Combine(sandbox, "previous-empty"));
                Directory.CreateDirectory(Path.Combine(output, "Empty"));
                break;
            case "entry":
                File.WriteAllText(Path.Combine(output, "Empty", "new.txt"), "Unexpected file");
                break;
        }
        var error = await Xunit.Record.ExceptionAsync(() => copies.VerifyCompletedAsync(id));
        Assert.NotNull(error);
        Assert.Contains(change switch
        {
            "bytes" => "SHA-256",
            "entry" => "unexpected entry",
            _ => "replaced"
        }, error.Message);
        Assert.Equal(bytes, File.ReadAllBytes(file));
        Assert.Equal(BatchPhase.Completed, copies.Jobs.Single().Transfer!.Phase);
        Assert.False(copies.Active(id));
    }

    [Fact]
    public async Task Legacy_batch_history_loads_without_adopting_destination_file_identities()
    {
        string id;
        await using (var copies = Open())
        {
            id = await copies.PrepareAsync(await Capture("Reports"));
            copies.ChooseDestination(id, destination, DownloadCollision.Fail);
            Assert.True((await Finish(copies, id)).HasBoundDestination);
        }
        var path = BatchReceipt(id);
        var journal = JsonNode.Parse(File.ReadAllText(path))!;
        foreach (var item in journal["Items"]!.AsArray()) item!.AsObject().Remove("FileIdentity");
        File.WriteAllText(path, journal.ToJsonString());
        await using var reopened = Open();
        Assert.Empty(reopened.RecoveryIssues);
        var result = reopened.Jobs.Single().Transfer!;
        Assert.Equal(BatchPhase.Completed, result.Phase);
        Assert.False(result.HasBoundDestination);
        Assert.Throws<IOException>(() => { _ = reopened.VerifyCompletedAsync(id); });
        Assert.True(reopened.CanRemove(id));
    }

    [Fact]
    public async Task Completed_destination_check_refuses_uncommitted_progress_and_can_cancel()
    {
        await using var copies = Open();
        var id = await copies.PrepareAsync(await Capture("Reports"));
        copies.ChooseDestination(id, destination, DownloadCollision.Fail);
        Assert.True((await Finish(copies, id)).HasBoundDestination);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => copies.VerifyCompletedAsync(id, canceled.Token));
        Assert.False(copies.Active(id));
        File.WriteAllText(BatchReceipt(id) + ".new", "Uncommitted metadata");
        await Assert.ThrowsAsync<IOException>(() => copies.VerifyCompletedAsync(id));
        Assert.Equal("Uncommitted metadata", File.ReadAllText(BatchReceipt(id) + ".new"));
        Assert.Equal(BatchPhase.Completed, copies.Jobs.Single().Transfer!.Phase);
    }
    [Fact]
    public async Task Offline_multi_selection_copies_nested_empty_and_zero_byte_items_without_sharing()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var selected = await Capture("Reports", "separate.txt");
        await using var copies = new WindowsLocalBatchCopies(profile, new Checks(ct => { entered.TrySetResult(); return release.Task.WaitAsync(ct); }));
        var activity = new List<WindowsActivityEvent>(); copies.Activity = x => { lock (activity) activity.Add(x); };
        var payload = new List<DownloadJob>(); var visible = new HashSet<string>();
        copies.PayloadChanged += _ => throw new InvalidOperationException("Failing batch display fixture.");
        copies.PayloadChanged += file => { payload.Add(file); if (file.Phase == DownloadPhase.Downloading && copies.FileProgress(file.Id) is not null) visible.Add(file.Id); };
        var id = await copies.PrepareAsync(selected); Assert.Equal(6, copies.Jobs.Single().Items); Assert.False(copies.CanRemove(id));
        using (var local = new LocalFileLocations(profile)) local.Remove(local.All.Single().Id);
        copies.ChooseDestination(id, destination, DownloadCollision.KeepBoth);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var activeFile = payload.Last(x => x.Phase == DownloadPhase.Finalizing);
        Assert.Equal(id, activeFile.BatchId);
        Assert.Equal(activeFile.Id, copies.ActiveFileProgress(id)?.Id);
        Assert.True(copies.HasFinalizationProgress);
        var finalization = Assert.IsType<IncomingFinalizationProgress>(copies.ReadFinalization(activeFile.Id));
        Assert.Equal(IncomingFinalizationStage.AttachmentCheck, finalization.Stage);
        Assert.Null(finalization.Estimate);
        release.TrySetResult();
        var row = await Finish(copies, id);
        Assert.False(copies.HasFinalizationProgress);
        Assert.Null(copies.ReadFinalization(activeFile.Id));
        Assert.Equal(BatchPhase.Completed, row.Phase); Assert.Empty(row.PeerId); Assert.All(row.Items, x => Assert.Equal(BatchItemPhase.Completed, x.Phase));
        Assert.NotEmpty(visible); Assert.Contains(payload, x => x.Phase == DownloadPhase.Downloading); Assert.All(payload, x => Assert.Empty(x.PeerId));
        foreach (var item in row.Items.Where(x => !x.Source.Directory))
        { Assert.Null(copies.FileProgress(item.DownloadId)); Assert.Contains(payload, x => x.Id == item.DownloadId && x.Phase == DownloadPhase.Completed && x.Offset == item.Source.Size); }
        Assert.True(Directory.Exists(Path.Combine(destination, "Reports", "Empty"))); Assert.Empty(File.ReadAllBytes(Path.Combine(destination, "Reports", "zero.bin")));
        Assert.Equal(File.ReadAllBytes(Path.Combine(source, "Reports", "Nested", "文書.txt")), File.ReadAllBytes(Path.Combine(destination, "Reports", "Nested", "文書.txt")));
        Assert.Contains("ZoneId=4", File.ReadAllText(Path.Combine(destination, "separate.txt") + ":Zone.Identifier"));
        Assert.Empty(new StorageGrantStore(profile).All); Assert.False(File.Exists(Path.Combine(profile, "trusted-peers.json")));
        Assert.Contains(activity, x => x.Kind == WindowsActivityKind.LocalBatchPlan); Assert.Contains(activity, x => x.Kind == WindowsActivityKind.LocalBatchCopy && x.Outcome == WindowsActivityOutcome.Completed);
        Assert.All(activity, x => Assert.Empty(x.Peer)); Assert.True(copies.CanRemove(id)); copies.Remove(id); Assert.Empty(copies.Jobs); Assert.True(File.Exists(Path.Combine(destination, "separate.txt")));
    }
    [Theory]
    [InlineData(DownloadCollision.KeepBoth, "Reports (2)", BatchPhase.Completed)]
    [InlineData(DownloadCollision.Skip, "Reports", BatchPhase.CompletedWithIssues)]
    [InlineData(DownloadCollision.Fail, "Reports", BatchPhase.CompletedWithIssues)]
    public async Task Folder_collisions_never_merge_or_overwrite(DownloadCollision policy, string output, BatchPhase phase)
    {
        Directory.CreateDirectory(Path.Combine(destination, "Reports")); File.WriteAllText(Path.Combine(destination, "Reports", "keep.txt"), "Keep destination data.");
        await using var copies = Open(); var id = await copies.PrepareAsync(await Capture("Reports")); copies.ChooseDestination(id, destination, policy); var row = await Finish(copies, id);
        Assert.Equal(phase, row.Phase); Assert.Equal("Keep destination data.", File.ReadAllText(Path.Combine(destination, "Reports", "keep.txt")));
        Assert.Equal(policy == DownloadCollision.KeepBoth, File.Exists(Path.Combine(destination, output, "Nested", "文書.txt")));
        Assert.False(File.Exists(Path.Combine(destination, "Reports", "zero.bin")));
    }
    [Fact]
    public async Task Restart_keeps_a_draft_and_frozen_tree_excludes_new_source_files()
    {
        string id; await using (var copies = Open()) { id = await copies.PrepareAsync(await Capture("Reports", "separate.txt")); Assert.Null(copies.Jobs.Single().Transfer); }
        File.WriteAllText(Path.Combine(source, "Reports", "added-later.txt"), "Excluded later source.");
        await using var reopened = Open(); Assert.Null(reopened.Jobs.Single().Transfer); reopened.ChooseDestination(id, destination, DownloadCollision.KeepBoth);
        Assert.Equal(BatchPhase.Completed, (await Finish(reopened, id)).Phase); Assert.False(File.Exists(Path.Combine(destination, "Reports", "added-later.txt")));
    }
    [Fact]
    public async Task Changed_file_is_rejected_before_its_bytes_are_published()
    {
        await using var copies = Open(); var id = await copies.PrepareAsync(await Capture("Reports")); File.AppendAllText(Path.Combine(source, "Reports", "Nested", "文書.txt"), "changed");
        copies.ChooseDestination(id, destination, DownloadCollision.KeepBoth); Assert.Equal(BatchPhase.Interrupted, (await Finish(copies, id)).Phase);
        Assert.False(File.Exists(Path.Combine(destination, "Reports", "Nested", "文書.txt")));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replaced_root_or_ancestor_is_rejected(bool ancestor)
    {
        await using var copies = Open(); var id = await copies.PrepareAsync(await Capture("Reports"));
        var target = Path.Combine(source, "Reports", ancestor ? "Nested" : "").TrimEnd(Path.DirectorySeparatorChar);
        Directory.Move(target, target + "-previous"); Directory.CreateDirectory(target); File.WriteAllText(Path.Combine(target, "文書.txt"), "Replacement content.");
        copies.ChooseDestination(id, destination, DownloadCollision.KeepBoth); Assert.Equal(BatchPhase.Interrupted, (await Finish(copies, id)).Phase);
        Assert.False(File.Exists(Path.Combine(destination, "Reports", "Nested", "文書.txt")));
    }
    [Fact]
    public async Task Duplicate_selection_and_canceled_planning_leave_no_partial_plan()
    {
        var selected = (await Capture("Reports"))[0]; await using var copies = Open();
        await Assert.ThrowsAsync<IOException>(() => copies.PrepareAsync([selected, selected])); Assert.Empty(copies.Jobs);
        using var canceled = new CancellationTokenSource(); canceled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => copies.PrepareAsync([selected], canceled.Token));
        Assert.Empty(copies.Jobs); Assert.Empty(Directory.GetFiles(Path.Combine(profile, "local-batch-copies", "sources")));
    }
    [Fact]
    public async Task More_than_4096_source_items_are_refused_before_a_plan_is_committed()
    {
        var folder = Path.Combine(source, "Large"); Directory.CreateDirectory(folder);
        for (var i = 0; i < 4096; i++) File.WriteAllBytes(Path.Combine(folder, $"{i:D4}.txt"), []);
        var selected = await Capture("Large"); await using var copies = Open(); await Assert.ThrowsAsync<IOException>(() => copies.PrepareAsync(selected)); Assert.Empty(copies.Jobs);
    }
    [Fact]
    public async Task Native_links_are_explicit_source_issues_and_never_followed()
    {
        var target = Path.Combine(source, "separate.txt"); var alias = Path.Combine(source, "Reports", "alias.txt"); Assert.True(CreateHardLink(alias, target, IntPtr.Zero));
        await using var copies = Open(); var id = await copies.PrepareAsync(await Capture("Reports")); Assert.Equal(1, copies.Jobs.Single().Issues);
        copies.ChooseDestination(id, destination, DownloadCollision.KeepBoth); Assert.Equal(BatchPhase.CompletedWithIssues, (await Finish(copies, id)).Phase); Assert.False(File.Exists(Path.Combine(destination, "Reports", "alias.txt")));
    }
    [Fact]
    public async Task Source_metadata_tampering_is_rejected_and_damaged_metadata_can_be_preserved()
    {
        string id;
        await using (var copies = Open())
        {
            id = await copies.PrepareAsync(await Capture("separate.txt")); File.AppendAllText(Record(id), " ");
            Assert.Throws<IOException>(() => copies.ChooseDestination(id, destination, DownloadCollision.KeepBoth)); Assert.Empty(Directory.GetFileSystemEntries(destination));
        }
        File.WriteAllText(Record(id), "{damaged source plan"); await using var reopened = Open(); var issue = Assert.Single(reopened.RecoveryIssues);
        reopened.Preserve(issue.Id); Assert.Empty(reopened.RecoveryIssues); Assert.Equal(1, reopened.PreservedRecords); reopened.ClearPreserved(); Assert.Equal(0, reopened.PreservedRecords);
        Assert.True(File.Exists(Path.Combine(source, "separate.txt")));
    }
    [Fact]
    public async Task Native_queue_lease_prevents_a_second_writer_and_draft_cancel_keeps_sources()
    {
        await using var copies = Open(); Assert.Throws<StorageException>(() => Open()); var id = await copies.PrepareAsync(await Capture("Reports"));
        await copies.Cancel(id); Assert.Empty(copies.Jobs); Assert.True(Directory.Exists(Path.Combine(source, "Reports")));
    }
    [Fact]
    public async Task Destination_inside_a_selected_folder_is_rejected_and_public_rows_are_detached()
    {
        await using var copies = Open(); var id = await copies.PrepareAsync(await Capture("Reports")); Assert.Throws<IOException>(() => copies.ChooseDestination(id, Path.Combine(source, "Reports"), DownloadCollision.KeepBoth));
        copies.ChooseDestination(id, destination, DownloadCollision.KeepBoth); Assert.Equal(BatchPhase.Completed, (await Finish(copies, id)).Phase);
        var row = copies.Jobs.Single(); row.Sources[0] = "unrelated"; row.Transfer!.Items[1].Source.SourcePath[0] = "unrelated";
        Assert.NotEqual("unrelated", copies.Jobs.Single().Sources[0]); Assert.DoesNotContain(copies.Jobs.Single().Transfer!.Items, x => x.Source.SourcePath.Contains("unrelated"));
    }
    [Fact]
    public async Task A_plan_with_128_selected_files_uses_paged_roots_and_keeps_unselected_siblings_out()
    {
        var names = Enumerable.Range(0, 128).Select(x => $"selected-{x:D3}.txt").ToArray(); foreach (var name in names) File.WriteAllText(Path.Combine(source, name), "Generated " + name);
        await using var copies = Open(); var id = await copies.PrepareAsync(await Capture(names)); copies.ChooseDestination(id, destination, DownloadCollision.KeepBoth);
        Assert.Equal(BatchPhase.Completed, (await Finish(copies, id)).Phase); Assert.Equal(128, Directory.GetFiles(destination).Length); Assert.False(File.Exists(Path.Combine(destination, "separate.txt")));
    }
    [Fact]
    public async Task Pause_close_restart_resume_rechecks_private_source_and_completes_bytes()
    {
        var payload = RandomNumberGenerator.GetBytes(5 * 1024 * 1024 + 17); File.WriteAllBytes(Path.Combine(source, "large.bin"), payload); string id;
        await using (var copies = Open())
        {
            id = await copies.PrepareAsync(await Capture("large.bin")); var paused = false;
            copies.Changed += () => { if (!paused && Directory.EnumerateFiles(destination, ".iconnect-transfer-*.part").Any(x => new FileInfo(x).Length > 0)) { paused = true; copies.Pause(id); } };
            copies.ChooseDestination(id, destination, DownloadCollision.KeepBoth); Assert.Equal(BatchPhase.Paused, (await Finish(copies, id)).Phase);
            Assert.True(new FileInfo(Assert.Single(Directory.GetFiles(destination, ".iconnect-transfer-*.part"))).Length > 0);
        }
        await using var reopened = Open(); Assert.False(reopened.Active(id)); await reopened.Resume(id).WaitAsync(TimeSpan.FromSeconds(45));
        Assert.Equal(BatchPhase.Completed, reopened.Jobs.Single().Transfer!.Phase); Assert.Equal(SHA256.HashData(payload), SHA256.HashData(File.ReadAllBytes(Path.Combine(destination, "large.bin"))));
    }
    [Fact]
    public async Task Real_attachment_worker_checks_a_local_batch_file()
    {
        var selected = await Capture("separate.txt"); await using var copies = new WindowsLocalBatchCopies(profile); var id = await copies.PrepareAsync(selected);
        copies.ChooseDestination(id, destination, DownloadCollision.KeepBoth); var row = await Finish(copies, id); Assert.Equal(BatchPhase.Completed, row.Phase);
        Assert.Contains("ZoneId=4", File.ReadAllText(Path.Combine(destination, "separate.txt") + ":Zone.Identifier"));
    }
    [Fact]
    public async Task Model_captures_multiple_loaded_items_before_selection_changes()
    {
        await using var model = new LocalExplorerController(profile); await model.AddAsync(source); await model.BrowseAsync(model.Rows.Single().Entry);
        model.SelectMany(model.Rows.Select(x => x.Entry.Id)); Assert.Null(model.Selected); var captured = await model.CaptureBatchSelectionAsync(); model.Select(null);
        var id = await model.PrepareBatchAsync(captured); var job = model.BatchCopies!.Jobs.Single(); Assert.Equal(id, job.Id); Assert.Equal(2, job.Sources.Length); Assert.Empty(model.SelectedIds);
        await model.BatchCopies.Cancel(id); Assert.Empty(new StorageGrantStore(profile).All);
    }
    [Fact]
    public async Task Folder_and_descendant_selection_are_rejected_as_overlapping()
    {
        using var browser = new LocalFileLocations(profile); browser.Add(source); var root = (await browser.RequestAsync(new() { Operation = FileOperation.ListRoots }, default)).Entries.Single();
        var children = await ExplorerCatalog.LoadAsync([root], browser.RequestAsync, default); var found = await ExplorerCatalog.SearchAsync([root], "文書", browser.RequestAsync, default);
        var folder = children.Rows.Single(x => x.Entry.Name == "Reports").Entry; var file = found.Rows.Single().Entry;
        var selected = new[] { await browser.CaptureBatchItemAsync(folder), await browser.CaptureBatchItemAsync(file) }; await using var copies = Open();
        await Assert.ThrowsAsync<IOException>(() => copies.PrepareAsync(selected)); Assert.Empty(copies.Jobs);
    }
    [Fact]
    public async Task A_later_hard_link_is_rejected_without_publishing_the_planned_file()
    {
        await using var copies = Open(); var id = await copies.PrepareAsync(await Capture("separate.txt"));
        Assert.True(CreateHardLink(Path.Combine(source, "later-alias.txt"), Path.Combine(source, "separate.txt"), IntPtr.Zero));
        copies.ChooseDestination(id, destination, DownloadCollision.KeepBoth); Assert.Equal(BatchPhase.Interrupted, (await Finish(copies, id)).Phase); Assert.Empty(Directory.GetFiles(destination));
    }
    [Fact]
    public async Task Concurrent_cancel_and_close_finish_cleanup_before_the_native_lease_is_released()
    {
        File.WriteAllBytes(Path.Combine(source, "large.bin"), RandomNumberGenerator.GetBytes(5 * 1024 * 1024)); var selected = await Capture("large.bin"); var copies = Open(); Task? stopping = null;
        var id = await copies.PrepareAsync(selected);
        copies.Changed += () => { if (stopping is null && Directory.EnumerateFiles(destination, ".iconnect-transfer-*.part").Any(x => new FileInfo(x).Length > 0)) stopping = copies.Cancel(id); };
        try
        {
            copies.ChooseDestination(id, destination, DownloadCollision.KeepBoth); await copies.WaitAsync(id).WaitAsync(TimeSpan.FromSeconds(30)); Assert.NotNull(stopping);
            await Task.WhenAll(stopping!, copies.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(30));
            await using var reopened = Open(); Assert.Equal(BatchPhase.Canceled, reopened.Jobs.Single().Transfer!.Phase); Assert.Empty(Directory.GetFiles(destination));
            Assert.True(reopened.CanRemove(id)); reopened.Remove(id); Assert.True(File.Exists(Path.Combine(source, "large.bin")));
        }
        finally { await copies.DisposeAsync(); }
    }
    public void Dispose() { if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true); }
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHardLink(string name, string existing, IntPtr security);
}
