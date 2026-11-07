using IConnect.Core;
using IConnect.Storage.Windows;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Xunit;

namespace IConnect.Tests;

public sealed partial class LocalBatchCopyTests
{
    private sealed class RecoveryChecks(Func<CancellationToken, Task> check) : IAttachmentChecks
    { public Task CheckAsync(string path, string identity, CancellationToken ct) => check(ct); }
    private string BatchRecord(string id) => Path.Combine(profile, "local-batch-copies", "receiving", "batches", id + ".json");
    private string FileRecord(string id) => Path.Combine(profile, "local-batch-copies", "receiving", "downloads", id + ".json");
    private string Decision(string id, string suffix = "started") => Path.Combine(profile, "local-batch-copies", "copy-decisions", id + "." + suffix);
    private async Task<(string Context, string File, string Part)> PausedLocalReceipt()
    {
        File.WriteAllBytes(Path.Combine(source, "large.bin"), RandomNumberGenerator.GetBytes(5 * 1024 * 1024));
        var selected = await Capture("large.bin"); await using var copies = Open(); var id = await copies.PrepareAsync(selected); var requested = false;
        copies.Changed += () => { if (!requested && Directory.EnumerateFiles(destination, ".iconnect-transfer-*.part").Any(x => new FileInfo(x).Length > 0)) { requested = true; copies.Pause(id); } };
        copies.ChooseDestination(id, destination, DownloadCollision.KeepBoth); var row = await Finish(copies, id); Assert.Equal(BatchPhase.Paused, row.Phase);
        return (id, row.Items.Single().DownloadId, Assert.Single(Directory.GetFiles(destination, ".iconnect-transfer-*.part")));
    }
    private static async Task WaitForOrphan(WindowsLocalBatchCopies copies, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); while (copies.OrphanActive(id)) await Task.Delay(10, timeout.Token);
    }
    [Fact]
    public async Task Missing_parent_exposes_a_real_partial_receipt_and_separate_review_preserves_sources()
    {
        var (id, child, part) = await PausedLocalReceipt(); File.Delete(BatchRecord(id)); var original = SHA256.HashData(File.ReadAllBytes(Path.Combine(source, "large.bin")));
        await using var copies = Open(); var events = new List<WindowsActivityEvent>(); copies.Activity = value => { lock (events) events.Add(value); }; var context = copies.Jobs.Single(); Assert.True(context.CopyMayExist); Assert.True(context.HasSource); Assert.Null(context.Transfer);
        Assert.Equal(child, Assert.Single(copies.OrphanFiles).Id); Assert.Throws<IOException>(() => copies.ChooseDestination(id, destination, DownloadCollision.KeepBoth));
        await Assert.ThrowsAsync<IOException>(() => copies.Cancel(id)); Assert.Throws<IOException>(() => copies.ReviewMissing(id));
        await copies.StopOrphanAsync(child); await WaitForOrphan(copies, child); Assert.False(File.Exists(part)); Assert.True(copies.CanRemoveOrphan(child)); copies.RemoveOrphan(child);
        Assert.Empty(copies.OrphanFiles); Assert.True(copies.Jobs.Single().CopyMayExist); Assert.True(File.Exists(Decision(id))); Assert.False(File.Exists(Decision(id, "ready")));
        var review = copies.ReviewMissing(id); copies.ConfirmRemoveMissing(review); Assert.Empty(copies.Jobs); Assert.Throws<IOException>(() => copies.ConfirmRemoveMissing(review));
        Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(Path.Combine(source, "large.bin"))));
        Assert.Contains(events, x => x.Kind == WindowsActivityKind.LocalBatchRecovery && x.Outcome == WindowsActivityOutcome.Completed); Assert.All(events, x => Assert.Empty(x.Peer));
        var history = System.Text.Json.JsonSerializer.Serialize(events); Assert.DoesNotContain(source, history); Assert.DoesNotContain(part, history); Assert.DoesNotContain(id, history); Assert.DoesNotContain(child, history);
    }
    [Fact]
    public async Task Missing_source_and_parent_still_expose_owned_cleanup_and_a_durable_context()
    {
        var (id, child, part) = await PausedLocalReceipt(); File.Delete(BatchRecord(id)); File.Delete(Record(id));
        await using (var copies = Open())
        {
            Assert.False(copies.Jobs.Single().HasSource); Assert.True(copies.Jobs.Single().CopyMayExist); await copies.StopOrphanAsync(child); await WaitForOrphan(copies, child); copies.RemoveOrphan(child);
            Assert.False(File.Exists(part)); Assert.Single(copies.Jobs);
        }
        await using var reopened = Open(); var review = reopened.ReviewMissing(id); Assert.False(review.HasSource); reopened.ConfirmRemoveMissing(review); Assert.Empty(reopened.Jobs);
        Assert.True(File.Exists(Path.Combine(source, "large.bin")));
    }
    [Fact]
    public async Task Lost_completed_receiving_history_cannot_make_the_surviving_plan_start_another_copy()
    {
        string id;
        await using (var copies = Open()) { id = await copies.PrepareAsync(await Capture("separate.txt")); copies.ChooseDestination(id, destination, DownloadCollision.KeepBoth); Assert.Equal(BatchPhase.Completed, (await Finish(copies, id)).Phase); }
        File.Delete(BatchRecord(id)); await using var reopened = Open(); Assert.Empty(reopened.OrphanFiles); Assert.True(reopened.Jobs.Single().CopyMayExist);
        Assert.Throws<IOException>(() => reopened.ChooseDestination(id, destination, DownloadCollision.KeepBoth)); reopened.ConfirmRemoveMissing(reopened.ReviewMissing(id));
        Assert.Equal("Generated separate selection.", File.ReadAllText(Path.Combine(destination, "separate.txt"))); Assert.Single(Directory.GetFiles(destination));
    }
    [Fact]
    public async Task Loss_of_all_decisions_and_receiving_history_keeps_a_used_source_nonfresh()
    {
        string id;
        await using (var copies = Open()) { id = await copies.PrepareAsync(await Capture("separate.txt")); copies.ChooseDestination(id, destination, DownloadCollision.KeepBoth); await Finish(copies, id); }
        File.Delete(BatchRecord(id)); File.Delete(Decision(id));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Decision(id))!));
        await using var reopened = Open(); Assert.True(reopened.Jobs.Single().CopyMayExist); Assert.Throws<IOException>(() => reopened.ChooseDestination(id, destination, DownloadCollision.KeepBoth));
        reopened.ConfirmRemoveMissing(reopened.ReviewMissing(id)); Assert.Empty(reopened.Jobs); Assert.Single(Directory.GetFiles(destination));
    }
    [Fact]
    public async Task A_draft_without_its_unused_record_requires_review_before_any_copy_can_start()
    {
        string id; await using (var copies = Open()) id = await copies.PrepareAsync(await Capture("separate.txt")); File.Delete(Decision(id, "ready"));
        await using var reopened = Open(); Assert.True(reopened.Jobs.Single().CopyMayExist); Assert.Throws<IOException>(() => reopened.ChooseDestination(id, destination, DownloadCollision.KeepBoth));
        reopened.ConfirmRemoveMissing(reopened.ReviewMissing(id)); Assert.Empty(reopened.Jobs); Assert.Empty(Directory.GetFileSystemEntries(destination));
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Legacy_source_plans_require_unknown_copy_review_instead_of_implicit_new_admission(int schema)
    {
        string id; await using (var copies = Open()) id = await copies.PrepareAsync(await Capture("separate.txt"));
        var legacy = JsonNode.Parse(File.ReadAllText(Record(id)))!; legacy["Schema"] = schema; File.WriteAllText(Record(id), legacy.ToJsonString()); File.Delete(Decision(id, "ready"));
        await using var reopened = Open(); Assert.True(reopened.Jobs.Single().CopyMayExist); Assert.Throws<IOException>(() => reopened.ChooseDestination(id, destination, DownloadCollision.KeepBoth));
        reopened.ConfirmRemoveMissing(reopened.ReviewMissing(id)); Assert.Empty(reopened.Jobs); Assert.Empty(Directory.GetFileSystemEntries(destination));
    }
    [Fact]
    public async Task A_published_orphan_can_recheck_without_source_reads_and_remove_only_its_receipt()
    {
        string id, child; var failed = new RecoveryChecks(_ => throw new IOException("Generated attachment refusal."));
        await using (var copies = new WindowsLocalBatchCopies(profile, failed))
        {
            id = await copies.PrepareAsync(await Capture("separate.txt")); copies.ChooseDestination(id, destination, DownloadCollision.KeepBoth); Assert.Equal(BatchPhase.Interrupted, (await Finish(copies, id)).Phase);
            child = copies.Jobs.Single().Transfer!.Items.Single().DownloadId;
        }
        File.Delete(BatchRecord(id)); File.Delete(Record(id)); File.Delete(Path.Combine(source, "separate.txt"));
        await using var reopened = Open(); var before = Assert.Single(reopened.OrphanFiles); Assert.NotEmpty(before.FinalName);
        await reopened.RecheckOrphanAsync(child); await WaitForOrphan(reopened, child); Assert.Equal(DownloadPhase.Completed, reopened.OrphanFiles.Single().Phase);
        reopened.RemoveOrphan(child); Assert.Empty(reopened.OrphanFiles); Assert.Single(Directory.GetFiles(destination)); Assert.Equal("Generated separate selection.", File.ReadAllText(Path.Combine(destination, before.FinalName)));
        reopened.ConfirmRemoveMissing(reopened.ReviewMissing(id)); Assert.Empty(reopened.Jobs);
    }
    [Fact]
    public async Task An_incomplete_orphan_cannot_fetch_source_bytes_through_recheck()
    {
        var (id, child, part) = await PausedLocalReceipt(); File.Delete(BatchRecord(id)); var length = new FileInfo(part).Length;
        await using var reopened = Open(); await Assert.ThrowsAsync<IOException>(() => reopened.RecheckOrphanAsync(child)); await WaitForOrphan(reopened, child);
        Assert.Equal(length, new FileInfo(part).Length); Assert.False(File.Exists(Path.Combine(destination, "large.bin"))); Assert.False(reopened.CanRemoveOrphan(child));
    }
    [Fact]
    public async Task A_receipt_with_its_original_parent_cannot_use_orphan_controls()
    {
        var (_, child, part) = await PausedLocalReceipt(); await using var reopened = Open(); Assert.Empty(reopened.OrphanFiles);
        await Assert.ThrowsAsync<IOException>(() => reopened.StopOrphanAsync(child)); await Assert.ThrowsAsync<IOException>(() => reopened.RecheckOrphanAsync(child)); Assert.Throws<IOException>(() => reopened.RemoveOrphan(child)); Assert.True(File.Exists(part));
    }
    [Fact]
    public async Task Changed_receiving_metadata_blocks_orphan_cleanup_until_the_current_state_is_reopened()
    {
        var (id, child, part) = await PausedLocalReceipt(); File.Delete(BatchRecord(id)); await using var reopened = Open();
        var saved = JsonNode.Parse(File.ReadAllText(FileRecord(child)))!; saved["Name"] = "changed-name.bin"; File.WriteAllText(FileRecord(child), saved.ToJsonString());
        await Assert.ThrowsAsync<IOException>(() => reopened.StopOrphanAsync(child)); Assert.True(File.Exists(part)); Assert.False(reopened.CanRemoveOrphan(child));
    }
    [Fact]
    public async Task A_new_receipt_invalidates_missing_context_removal_and_review_is_single_use()
    {
        string id; await using (var copies = Open()) { id = await copies.PrepareAsync(await Capture("separate.txt")); copies.ChooseDestination(id, destination, DownloadCollision.KeepBoth); await Finish(copies, id); }
        File.Delete(BatchRecord(id)); await using var reopened = Open(); var review = reopened.ReviewMissing(id);
        var second = await reopened.PrepareAsync(await Capture("Reports")); reopened.ChooseDestination(second, destination, DownloadCollision.KeepBoth); await Finish(reopened, second);
        Assert.Throws<IOException>(() => reopened.ConfirmRemoveMissing(review)); Assert.Throws<IOException>(() => reopened.ConfirmRemoveMissing(review)); Assert.True(File.Exists(Record(id)));
        reopened.ConfirmRemoveMissing(reopened.ReviewMissing(id)); Assert.Single(reopened.Jobs);
    }
    [Fact]
    public async Task A_changed_decision_blocks_old_reviews_without_deleting_files_or_source_access()
    {
        string id; await using (var copies = Open()) { id = await copies.PrepareAsync(await Capture("separate.txt")); copies.ChooseDestination(id, destination, DownloadCollision.KeepBoth); await Finish(copies, id); }
        File.Delete(BatchRecord(id)); await using var reopened = Open(); var review = reopened.ReviewMissing(id); File.AppendAllText(Decision(id), "changed decision metadata");
        Assert.Throws<IOException>(() => reopened.ConfirmRemoveMissing(review)); Assert.True(File.Exists(Record(id))); Assert.True(File.Exists(Decision(id))); Assert.Single(Directory.GetFiles(destination));
    }
    [Fact]
    public async Task Restart_and_changed_confirmation_cannot_reuse_a_missing_progress_review()
    {
        string id; LocalBatchMissingReview review;
        await using (var copies = Open()) { id = await copies.PrepareAsync(await Capture("separate.txt")); copies.ChooseDestination(id, destination, DownloadCollision.KeepBoth); await Finish(copies, id); }
        File.Delete(BatchRecord(id)); await using (var copies = Open()) review = copies.ReviewMissing(id);
        await using var reopened = Open(); Assert.Throws<IOException>(() => reopened.ConfirmRemoveMissing(review)); review = reopened.ReviewMissing(id);
        Assert.Throws<IOException>(() => reopened.ConfirmRemoveMissing(review with { HasSource = false })); Assert.Throws<IOException>(() => reopened.ConfirmRemoveMissing(review)); Assert.True(File.Exists(Record(id)));
    }
    [Fact]
    public async Task Damaged_source_preservation_does_not_hide_its_orphan_receipt_or_copy_decision()
    {
        var (id, child, part) = await PausedLocalReceipt(); File.Delete(BatchRecord(id)); File.WriteAllText(Record(id), "{damaged source");
        await using var reopened = Open(); var issue = Assert.Single(reopened.RecoveryIssues); Assert.Single(reopened.OrphanFiles);
        reopened.Preserve(issue.Id); reopened.ClearPreserved(); Assert.Empty(reopened.RecoveryIssues); Assert.True(reopened.Jobs.Single().CopyMayExist);
        await reopened.StopOrphanAsync(child); await WaitForOrphan(reopened, child); reopened.RemoveOrphan(child); reopened.ConfirmRemoveMissing(reopened.ReviewMissing(id));
        Assert.False(File.Exists(part)); Assert.Empty(reopened.Jobs); Assert.True(File.Exists(Path.Combine(source, "large.bin")));
    }
    [Fact]
    public async Task Missing_contexts_count_against_admission_until_their_references_are_reviewed()
    {
        await using (var copies = Open()) for (var i = 0; i < 8; i++) { var id = await copies.PrepareAsync(await Capture("separate.txt")); copies.ChooseDestination(id, destination, DownloadCollision.KeepBoth); await Finish(copies, id); }
        foreach (var path in Directory.GetFiles(Path.Combine(profile, "local-batch-copies", "receiving", "batches"), "*.json")) File.Delete(path);
        await using var reopened = Open(); var selection = await Capture("Reports"); await Assert.ThrowsAsync<IOException>(() => reopened.PrepareAsync(selection)); Assert.Equal(8, reopened.Jobs.Count);
        reopened.ConfirmRemoveMissing(reopened.ReviewMissing(reopened.Jobs.First().Id)); await reopened.PrepareAsync(selection); Assert.Equal(8, reopened.Jobs.Count);
    }
    [Fact]
    public async Task Closing_during_orphan_recheck_cancels_the_worker_and_keeps_the_published_copy()
    {
        string id, child;
        await using (var first = new WindowsLocalBatchCopies(profile, new RecoveryChecks(_ => throw new IOException("Generated attachment refusal."))))
        {
            id = await first.PrepareAsync(await Capture("separate.txt")); first.ChooseDestination(id, destination, DownloadCollision.KeepBoth); await Finish(first, id); child = first.Jobs.Single().Transfer!.Items.Single().DownloadId;
        }
        File.Delete(BatchRecord(id)); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = new RecoveryChecks(async ct => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }); var copies = new WindowsLocalBatchCopies(profile, blocked);
        try
        {
            var checking = copies.RecheckOrphanAsync(child); Assert.Same(checking, copies.RecheckOrphanAsync(child)); await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await Assert.ThrowsAsync<IOException>(() => copies.StopOrphanAsync(child)); Assert.Throws<IOException>(() => copies.RemoveOrphan(child));
            await Task.WhenAll(checking, copies.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(30));
            await using var reopened = Open(); Assert.False(reopened.OrphanActive(child)); Assert.Single(reopened.OrphanFiles); Assert.Single(Directory.GetFiles(destination));
        }
        finally { await copies.DisposeAsync(); }
    }
    [Fact]
    public async Task A_single_file_receipt_is_rejected_by_the_batch_receiving_namespace()
    {
        var selected = (await Capture("separate.txt"))[0]; string id;
        await using (var single = new WindowsLocalCopies(profile, new Checks()))
        { id = single.Prepare(selected); single.ChooseDestination(id, destination, DownloadCollision.KeepBoth); await single.WaitAsync(id).WaitAsync(TimeSpan.FromSeconds(30)); Assert.Equal(DownloadPhase.Completed, single.Jobs.Single().Transfer!.Phase); }
        Directory.CreateDirectory(Path.GetDirectoryName(FileRecord(id))!); File.Copy(Path.Combine(profile, "local-copies", "receiving", "downloads", id + ".json"), FileRecord(id));
        await using var copies = Open(); Assert.Single(copies.RecoveryIssues); Assert.Empty(copies.OrphanFiles); copies.Preserve(copies.RecoveryIssues.Single().Id); copies.ClearPreserved();
        Assert.Empty(copies.RecoveryIssues); Assert.Single(Directory.GetFiles(destination)); Assert.True(File.Exists(Path.Combine(source, "separate.txt")));
    }
}
