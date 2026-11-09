using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using Xunit;

namespace IConnect.Tests;

public sealed partial class BatchSenderTests
{
    private static void Archive(Fixture f, string id) => Assert.True(f.Sender.ConfirmHistory(f.Sender.ReviewHistory(id, BatchHistoryAction.Archive)));
    [Fact]
    public async Task Inactive_history_archives_and_restores_the_same_job_without_releasing_private_source_bytes()
    {
        await using var f = new Fixture(); var id = await f.Queue(); var source = Assert.Single(f.Sources.All); Assert.True(f.Sender.SourceInUse(source.Id));
        Archive(f, id); Assert.True(Assert.Single(f.Sender.All).Archived); Assert.False(f.Sender.SourceInUse(source.Id)); Assert.Equal(0, f.Calls);
        await f.ReopenSender(); Assert.Empty(f.Sender.RecoveryIssues); Assert.True(Assert.Single(f.Sender.All).Archived);
        Assert.True(f.Sender.ConfirmHistory(f.Sender.ReviewHistory(id, BatchHistoryAction.Restore))); Assert.False(Assert.Single(f.Sender.All).Archived); Assert.True(f.Sender.SourceInUse(source.Id));
        Assert.Equal(source, Assert.Single(f.Sources.All)); Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Original, "file.bin")));
    }
    [Fact]
    public async Task Archived_interrupted_admission_cannot_send_until_restored_and_keeps_its_original_remote_header()
    {
        await using var f = new Fixture(); var id = await f.Queue();
        async Task<FileResponse> Lost(FileRequest value, CancellationToken ct) { var reply = await f.Request(value, ct); if (value.Operation == FileOperation.BeginUploadBatch) throw new IOException("Lost accepted admission reply."); return reply; }
        await f.Sender.SendAsync(id, await f.Target(), Lost); var before = Assert.Single(f.Sender.All); Archive(f, id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Sender.SendAsync(id, null, f.Request));
        Assert.Throws<IOException>(() => f.Sender.Queue(before.Source));
        await f.ReopenSender(); Assert.True(f.Sender.ConfirmHistory(f.Sender.ReviewHistory(id, BatchHistoryAction.Restore)));
        await f.Sender.SendAsync(id, null, f.Request); var done = Assert.Single(f.Sender.All); Assert.Equal(BatchSendingPhase.Completed, done.Phase); Assert.Equal(before.Header, done.Header); Assert.Equal(1, f.Begins);
        Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Destination, "original", "file.bin")));
    }
    [Fact]
    public async Task Archiving_requires_idle_work_and_single_use_current_owner_reviews()
    {
        await using var f = new Fixture(); var id = await f.Queue(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<FileResponse> Waiting(FileRequest value, CancellationToken ct) { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return await f.Request(value, ct); }
        var sending = f.Sender.SendAsync(id, await f.Target(), Waiting); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Throws<IOException>(() => f.Sender.ReviewHistory(id, BatchHistoryAction.Archive)); f.Sender.Pause(id); await sending;
        var review = f.Sender.ReviewHistory(id, BatchHistoryAction.Archive); Assert.Throws<IOException>(() => f.Sender.ConfirmHistory(review with { Results = 999 })); Assert.Throws<IOException>(() => f.Sender.ConfirmHistory(review));
        review = f.Sender.ReviewHistory(id, BatchHistoryAction.Archive); Assert.True(f.Sender.ConfirmHistory(review)); Assert.Throws<IOException>(() => f.Sender.ConfirmHistory(review));
        await f.Sender.DisposeAsync(); await using var foreign = new WindowsBatchSender(f.Profile, f.Sources, f.Current, localOwner: _ => false);
        Assert.Throws<IOException>(() => foreign.ReviewHistory(id, BatchHistoryAction.Purge)); Assert.True(Assert.Single(foreign.All).Archived);
    }
    [Fact]
    public async Task A_changed_job_or_result_invalidates_history_confirmation_before_any_deletion()
    {
        await using var f = new Fixture(); var id = await f.Queue(); var old = f.Sender.ReviewHistory(id, BatchHistoryAction.Archive);
        await f.Sender.SendAsync(id, await f.Target(), f.Request); Assert.Throws<IOException>(() => f.Sender.ConfirmHistory(old)); Archive(f, id);
        var purge = f.Sender.ReviewHistory(id, BatchHistoryAction.Purge); var path = Path.Combine(f.Profile, "outgoing-batch-jobs", id + ".0.json"); File.AppendAllText(path, " ");
        Assert.Throws<IOException>(() => f.Sender.ConfirmHistory(purge)); Assert.True(File.Exists(path)); Assert.True(File.Exists(Path.Combine(f.Profile, "outgoing-batch-jobs", id + ".json")));
        Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Destination, "original", "file.bin")));
    }
    [Fact]
    public async Task Revoked_peer_cannot_send_but_local_owner_can_archive_and_purge_only_its_metadata()
    {
        await using var f = new Fixture(); var id = await f.Queue(); await f.Sender.SendAsync(id, await f.Target(), f.Request); var calls = f.Calls;
        f.Allowed = false; Archive(f, id); Assert.True(f.Sender.ConfirmHistory(f.Sender.ReviewHistory(id, BatchHistoryAction.Purge)));
        Assert.Empty(f.Sender.All); Assert.Equal(calls, f.Calls); Assert.Single(f.Sources.All); Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Destination, "original", "file.bin"))); Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Original, "file.bin")));
        await f.ReopenSender(); Assert.Empty(f.Sender.RecoveryIssues); Assert.Empty(f.Sender.All);
    }
    [Fact]
    public async Task Retained_history_has_a_separate_bound_and_purging_frees_that_history_slot()
    {
        await using var f = new Fixture(); var id = await f.Queue(); var source = Assert.Single(f.Sources.All).Id; var first = id;
        for (var index = 0; index < WindowsBatchSender.MaxHistory; index++) { if (index != 0) id = f.Sender.Queue(source); Archive(f, id); }
        var current = f.Sender.Queue(source); Assert.Throws<IOException>(() => f.Sender.ReviewHistory(current, BatchHistoryAction.Archive));
        Assert.True(f.Sender.ConfirmHistory(f.Sender.ReviewHistory(first, BatchHistoryAction.Purge))); Archive(f, current);
        Assert.Equal(WindowsBatchSender.MaxHistory, f.Sender.All.Count); Assert.All(f.Sender.All, row => Assert.True(row.Archived)); Assert.Single(f.Sources.All);
        await f.ReopenSender(); Assert.Empty(f.Sender.RecoveryIssues); Assert.Equal(WindowsBatchSender.MaxHistory, f.Sender.All.Count);
    }
    [Fact]
    public async Task Explicit_another_copy_does_not_let_a_second_active_job_silently_replace_the_original_intent()
    {
        await using var f = new Fixture(); var id = await f.Queue();
        async Task<FileResponse> Lost(FileRequest value, CancellationToken ct) { var reply = await f.Request(value, ct); if (value.Operation == FileOperation.BeginUploadBatch) throw new IOException("Lost reply."); return reply; }
        await f.Sender.SendAsync(id, await f.Target(), Lost); var source = Assert.Single(f.Sources.All).Id; Archive(f, id);
        Assert.Throws<IOException>(() => f.Sender.Queue(source)); var another = f.Sender.Queue(source, anotherCopy: true);
        Assert.Throws<IOException>(() => f.Sender.ReviewHistory(id, BatchHistoryAction.Restore)); Archive(f, another);
        Assert.True(f.Sender.ConfirmHistory(f.Sender.ReviewHistory(id, BatchHistoryAction.Restore))); Assert.Equal(1, f.Begins);
    }
    [Fact]
    public async Task Bounded_history_purge_survives_deleted_files_before_its_cursor_checkpoint_and_keeps_publication()
    {
        await using var f = new Fixture(extras: 66); var id = await f.Queue(); await f.Sender.SendAsync(id, await f.Target(), f.Request); var row = Assert.Single(f.Sender.All); Assert.Equal(70, row.Next); Archive(f, id);
        Assert.False(f.Sender.ConfirmHistory(f.Sender.ReviewHistory(id, BatchHistoryAction.Purge))); row = Assert.Single(f.Sender.All); Assert.True(row.Purging); Assert.Equal(64, row.PurgeCursor);
        var parent = Path.Combine(f.Profile, "outgoing-batch-jobs", id + ".json"); await f.Sender.DisposeAsync();
        var saved = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(parent))!; saved["Row"]!["PurgeCursor"] = 0; File.WriteAllText(parent, saved.ToJsonString());
        await f.ReopenSender(); Assert.Empty(f.Sender.RecoveryIssues); Assert.False(f.Sender.ContinuePurge(id)); Assert.True(f.Sender.ContinuePurge(id)); Assert.Empty(f.Sender.All);
        Assert.DoesNotContain(Directory.EnumerateFiles(Path.Combine(f.Profile, "outgoing-batch-jobs")), path => Path.GetFileName(path).StartsWith(id, StringComparison.Ordinal));
        Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Destination, "original", "file.bin"))); Assert.Equal(68, Directory.EnumerateFiles(Path.Combine(f.Destination, "original")).Count()); Assert.Single(f.Sources.All);
        await f.ReopenSender(); Assert.Empty(f.Sender.RecoveryIssues);
    }
    [Fact]
    public async Task Unreadable_archived_item_metadata_cannot_restore_but_can_be_owner_reviewed_and_purged()
    {
        await using var f = new Fixture(); var id = await f.Queue(); await f.Sender.SendAsync(id, await f.Target(), f.Request); Archive(f, id);
        var path = Path.Combine(f.Profile, "outgoing-batch-jobs", id + ".0.json"); File.WriteAllText(path, "{damaged"); await f.ReopenSender(); Assert.Empty(f.Sender.RecoveryIssues);
        Assert.ThrowsAny<Exception>(() => f.Sender.ReviewHistory(id, BatchHistoryAction.Restore));
        Assert.True(f.Sender.ConfirmHistory(f.Sender.ReviewHistory(id, BatchHistoryAction.Purge))); Assert.Empty(f.Sender.All); Assert.False(File.Exists(path)); Assert.Single(f.Sources.All);
        Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Destination, "original", "file.bin")));
    }
    [Fact]
    public async Task Unknown_job_objects_invalidate_owner_review_and_are_never_removed_as_history()
    {
        await using var f = new Fixture(); var id = await f.Queue(); Archive(f, id); var review = f.Sender.ReviewHistory(id, BatchHistoryAction.Purge);
        var unknown = Path.Combine(f.Profile, "outgoing-batch-jobs", id + ".bin"); File.WriteAllText(unknown, "owner data"); Assert.Throws<IOException>(() => f.Sender.ConfirmHistory(review));
        Assert.Equal("owner data", File.ReadAllText(unknown)); Assert.True(File.Exists(Path.Combine(f.Profile, "outgoing-batch-jobs", id + ".json"))); Assert.Single(f.Sources.All);
    }
    [Fact]
    public async Task Schema_one_jobs_load_without_losing_authority_and_upgrade_only_when_written()
    {
        await using var f = new Fixture(); var id = await f.Queue(); var before = Assert.Single(f.Sender.All); await f.Sender.DisposeAsync();
        var path = Path.Combine(f.Profile, "outgoing-batch-jobs", id + ".json"); var saved = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!; saved["Schema"] = 1;
        foreach (var field in new[] { "Archived", "Purging", "PurgeCursor", "PurgeCount" }) saved["Row"]!.AsObject().Remove(field); File.WriteAllText(path, saved.ToJsonString());
        await f.ReopenSender(); Assert.Empty(f.Sender.RecoveryIssues); var row = Assert.Single(f.Sender.All); Assert.Equal(before.Authority, row.Authority); Assert.Equal(before.Source, row.Source); Assert.False(row.Archived);
        Archive(f, id); Assert.Equal(3, System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!["Schema"]!.GetValue<int>());
    }
    [Fact]
    public async Task A_written_item_ahead_of_the_parent_cursor_remains_owned_history_and_purges_without_an_orphan()
    {
        await using var f = new Fixture(); var id = await f.Queue();
        async Task<FileResponse> StopBeforeParentFinish(FileRequest value, CancellationToken ct) { if (value.Operation == FileOperation.FinishUploadBatch) throw new IOException("Crash before parent finish."); return await f.Request(value, ct); }
        await f.Sender.SendAsync(id, await f.Target(), StopBeforeParentFinish); var row = Assert.Single(f.Sender.All); await f.Sender.DisposeAsync();
        var path = Path.Combine(f.Profile, "outgoing-batch-jobs", id + ".json"); var saved = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!; var outcomes = row.Outcomes!.ToArray(); outcomes[^1] = 0;
        saved["Row"]!["Next"] = row.Items - 1; saved["Row"]!["Attempted"] = true; saved["Row"]!["Outcomes"] = Convert.ToBase64String(outcomes); File.WriteAllText(path, saved.ToJsonString());
        await f.ReopenSender(); Assert.Empty(f.Sender.RecoveryIssues); Archive(f, id); var review = f.Sender.ReviewHistory(id, BatchHistoryAction.Purge); Assert.Equal(row.Items, review.Results); Assert.True(f.Sender.ConfirmHistory(review));
        await f.ReopenSender(); Assert.Empty(f.Sender.RecoveryIssues); Assert.Empty(f.Sender.All); Assert.True(f.Receiver.BatchPlans.ReservedBytes(null) > 0); Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Destination, "original", "file.bin")));
    }
    [Fact]
    public async Task Missing_metadata_beyond_the_interrupted_cleanup_window_blocks_cold_recovery_without_touching_copies()
    {
        await using var f = new Fixture(extras: 66); var id = await f.Queue(); await f.Sender.SendAsync(id, await f.Target(), f.Request); Archive(f, id); Assert.False(f.Sender.ConfirmHistory(f.Sender.ReviewHistory(id, BatchHistoryAction.Purge)));
        await f.Sender.DisposeAsync(); var path = Path.Combine(f.Profile, "outgoing-batch-jobs", id + ".json"); var saved = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!; saved["Row"]!["PurgeCursor"] = 0; File.WriteAllText(path, saved.ToJsonString());
        File.Delete(Path.Combine(f.Profile, "outgoing-batch-jobs", id + ".69.json")); await f.ReopenSender(); Assert.NotEmpty(f.Sender.RecoveryIssues); Assert.True(File.Exists(path)); Assert.True(File.Exists(Path.Combine(f.Profile, "outgoing-batch-jobs", id + ".64.json")));
        Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Destination, "original", "file.bin"))); Assert.Single(f.Sources.All);
    }
    [Fact]
    public async Task Source_removal_is_guarded_against_a_restored_current_job_and_keeps_archived_history()
    {
        await using var f = new Fixture(); var id = await f.Queue(); var source = Assert.Single(f.Sources.All).Id; var target = await f.Target(); Assert.Throws<IOException>(() => f.Sender.ReviewSourceRemoval(source)); Archive(f, id);
        var review = f.Sender.ReviewSourceRemoval(source); Assert.True(f.Sender.ConfirmHistory(f.Sender.ReviewHistory(id, BatchHistoryAction.Restore))); Assert.Throws<IOException>(() => f.Sender.RemoveSourceReviewed(review)); Assert.Single(f.Sources.All);
        Archive(f, id); f.Sender.RemoveSourceReviewed(review); Assert.Empty(f.Sources.All); Assert.True(Assert.Single(f.Sender.All).Archived); Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Original, "file.bin")));
        Assert.True(f.Sender.ConfirmHistory(f.Sender.ReviewHistory(id, BatchHistoryAction.Restore))); var calls = f.Calls; await f.Sender.SendAsync(id, target, f.Request); Assert.Equal(calls, f.Calls); Assert.Equal(BatchSendingPhase.Review, Assert.Single(f.Sender.All).Phase);
    }
    [Fact]
    public async Task Purging_one_archived_job_keeps_another_jobs_results_and_all_published_copies()
    {
        await using var f = new Fixture(); var first = await f.Queue(); var source = Assert.Single(f.Sources.All).Id; var target = await f.Target(); await f.Sender.SendAsync(first, target, f.Request); Archive(f, first);
        var second = f.Sender.Queue(source, anotherCopy: true); await f.Sender.SendAsync(second, target, f.Request); Archive(f, second); var result = f.Sender.ItemResult(second, 0);
        Assert.True(f.Sender.ConfirmHistory(f.Sender.ReviewHistory(first, BatchHistoryAction.Purge))); Assert.Equal(second, Assert.Single(f.Sender.All).Id); Assert.Equal(result, f.Sender.ItemResult(second, 0)); Assert.Equal(2, Directory.EnumerateDirectories(f.Destination).Count());
        await f.ReopenSender(); Assert.Empty(f.Sender.RecoveryIssues); Assert.Equal(result, f.Sender.ItemResult(second, 0)); Assert.Single(f.Sources.All); Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Original, "file.bin")));
    }
}
