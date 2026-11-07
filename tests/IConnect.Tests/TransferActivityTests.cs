using System.Collections.Concurrent;
using System.Text.Json;
using IConnect.AppModel;
using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using Xunit;

namespace IConnect.Tests;

public sealed partial class UploadSenderTests
{
    [Fact]
    public async Task Transfer_activity_distinguishes_private_preparation_uncertain_send_and_verified_receiver_completion()
    {
        await using var f = new Fixture(); await f.Initialize(); var events = new ConcurrentQueue<WindowsActivityEvent>(); f.Sender.Activity = events.Enqueue;
        var id = await f.Sender.PrepareAsync(f.SourcePath, f.Authority); Assert.Contains(events, x => x.Kind == WindowsActivityKind.PrivateCopy && x.Outcome == WindowsActivityOutcome.Accepted && x.Bytes == f.Bytes.Length);
        Assert.DoesNotContain(events, x => x.Kind == WindowsActivityKind.FileSend && x.Outcome == WindowsActivityOutcome.Completed);
        var lost = false;
        await f.Sender.SendAsync(id, f.Target, async (value, token) => { var reply = await f.Request(value, token); if (!lost && value.Operation == FileOperation.UploadWrite) { lost = true; throw new IOException("NEVER-LOG-LOST-REPLY"); } return reply; });
        Assert.Equal(SendingPhase.Review, f.Sender.All.Single().Phase); Assert.Contains(events, x => x.Kind == WindowsActivityKind.FileSend && x.Outcome == WindowsActivityOutcome.Review);
        await f.Sender.SendAsync(id, null, f.Request); Assert.Equal(SendingPhase.Completed, f.Sender.All.Single().Phase);
        Assert.Contains(events, x => x.Kind == WindowsActivityKind.FileSend && x.Outcome == WindowsActivityOutcome.Completed && x.Bytes == f.Bytes.Length);
        Assert.Equal(f.Bytes, File.ReadAllBytes(f.SourcePath)); Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Destination, f.Sender.All.Single().FinalName)));
        var json = JsonSerializer.Serialize(events); Assert.DoesNotContain("NEVER-LOG", json); Assert.DoesNotContain(id, json); Assert.DoesNotContain(f.SourcePath, json); Assert.DoesNotContain("original.bin", json);
        f.Sender.Activity = _ => throw new IOException("generated observer failure");
        var next = await f.Sender.PrepareAsync(f.SourcePath, f.Authority); await f.Sender.SendAsync(next, f.Target, f.Request);
        Assert.Equal(SendingPhase.Completed, f.Sender.All.Single(x => x.Id == next).Phase); Assert.Equal(f.Bytes, File.ReadAllBytes(f.SourcePath));
    }
}

public sealed partial class DownloadTests
{
    [Fact]
    public async Task Transfer_activity_waits_for_verified_download_completion_and_reports_skip_without_observer_authority()
    {
        var entry = await FileEntry(); var events = new ConcurrentQueue<WindowsActivityEvent>();
        await using var manager = new WindowsDownloadManager(Path.Combine(sandbox, "receiver")) { Activity = events.Enqueue };
        var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<FileResponse> Wait(FileRequest value, CancellationToken token) { if (value.Operation == FileOperation.DownloadRead && value.Offset == 0) { seen.TrySetResult(); await release.Task.WaitAsync(token); } return await Rpc(value, token); }
        var id = manager.Start(peer, entry, target, DownloadCollision.KeepBoth, Wait);
        await seen.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.DoesNotContain(events, x => x.Outcome == WindowsActivityOutcome.Completed); release.SetResult();
        await manager.WaitAsync(id).WaitAsync(TimeSpan.FromSeconds(20)); Assert.True(manager.HasCompletedAttachmentCheck(id));
        Assert.Contains(events, x => x.Kind == WindowsActivityKind.Download && x.Outcome == WindowsActivityOutcome.Completed && x.Bytes == expected.Length);
        manager.Activity = value => { events.Enqueue(value); throw new IOException("NEVER-LOG-OBSERVER-ERROR"); };
        var skipped = manager.Start(peer, entry, target, DownloadCollision.Skip, Rpc); await manager.WaitAsync(skipped).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(DownloadPhase.Skipped, manager.Jobs.Single(x => x.Id == skipped).Phase); Assert.False(manager.HasCompletedAttachmentCheck(skipped)); Assert.Contains(events, x => x.Outcome == WindowsActivityOutcome.Skipped);
        Assert.Equal(expected, File.ReadAllBytes(Path.Combine(target, entry.Name))); var json = JsonSerializer.Serialize(events);
        Assert.DoesNotContain(entry.Name, json); Assert.DoesNotContain(entry.Id, json); Assert.DoesNotContain("NEVER-LOG", json);
    }
    [Fact]
    public async Task Transfer_activity_marks_uncommitted_download_progress_for_review_then_verifies_the_original_receipt_on_resume()
    {
        var entry = await FileEntry(); var state = Path.Combine(sandbox, "receiver"); var events = new ConcurrentQueue<WindowsActivityEvent>();
        await using var manager = new WindowsDownloadManager(state) { Activity = events.Enqueue };
        var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<FileResponse> Wait(FileRequest value, CancellationToken token) { if (value.Operation == FileOperation.DownloadRead && value.Offset == 0) { seen.TrySetResult(); await Task.Delay(Timeout.Infinite, token); } return await Rpc(value, token); }
        var id = manager.Start(peer, entry, target, DownloadCollision.KeepBoth, Wait); await seen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var blocker = Path.Combine(state, "downloads", id + ".json.new"); Directory.CreateDirectory(blocker); manager.Pause(id);
        await manager.WaitAsync(id).WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(DownloadPhase.Interrupted, manager.Jobs.Single().Phase);
        Assert.Contains(events, x => x.Kind == WindowsActivityKind.Download && x.Outcome == WindowsActivityOutcome.Review); Assert.DoesNotContain(events, x => x.Outcome == WindowsActivityOutcome.Completed);
        Directory.Delete(blocker); await manager.Resume(id, Rpc).WaitAsync(TimeSpan.FromSeconds(20)); Assert.True(manager.HasCompletedAttachmentCheck(id));
        Assert.Equal(expected, File.ReadAllBytes(Path.Combine(target, entry.Name))); Assert.Contains(events, x => x.Outcome == WindowsActivityOutcome.Completed);
    }
}

public sealed partial class BatchSenderTests
{
    [Fact]
    public async Task Transfer_activity_records_actual_batch_preparation_and_verified_terminal_receipts_without_source_metadata()
    {
        await using var f = new Fixture(); var events = new ConcurrentQueue<WindowsActivityEvent>(); f.Sources.Activity = f.Sender.Activity = f.Receiver.Activity = events.Enqueue;
        var id = await f.Queue(); Assert.Contains(events, x => x.Kind == WindowsActivityKind.BatchPrepare && x.Outcome == WindowsActivityOutcome.Accepted);
        Assert.Contains(events, x => x.Kind == WindowsActivityKind.BatchSend && x.Outcome == WindowsActivityOutcome.Accepted); Assert.DoesNotContain(events, x => x.Outcome == WindowsActivityOutcome.Completed);
        await f.Sender.SendAsync(id, await f.Target(), f.Request);
        Assert.Contains(events, x => x.Kind == WindowsActivityKind.BatchSend && x.Outcome == WindowsActivityOutcome.Completed && x.Bytes == f.Bytes.Length);
        Assert.Contains(events, x => x.Kind == WindowsActivityKind.BatchReceive && x.Outcome == WindowsActivityOutcome.Completed && x.Bytes == f.Bytes.Length);
        Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Destination, "original", "file.bin"))); var json = JsonSerializer.Serialize(events);
        Assert.DoesNotContain(id, json); Assert.DoesNotContain("file.bin", json); Assert.DoesNotContain(f.Original, json);
        f.Sources.Activity = f.Sender.Activity = f.Receiver.Activity = _ => throw new IOException("generated observer fault");
        var next = await f.Queue(); await f.Sender.SendAsync(next, await f.Target(), f.Request); Assert.Equal(BatchSendingPhase.Completed, f.Sender.All.Single(x => x.Id == next).Phase);
    }
    [Fact]
    public async Task Transfer_activity_never_calls_a_batch_with_skipped_items_a_fully_copied_batch()
    {
        await using var f = new Fixture(); Directory.CreateDirectory(Path.Combine(f.Destination, "original")); File.WriteAllText(Path.Combine(f.Destination, "original", "owner.txt"), "generated owner bytes");
        var events = new ConcurrentQueue<WindowsActivityEvent>(); f.Sender.Activity = f.Receiver.Activity = events.Enqueue;
        var source = await f.Sources.PrepareAsync([f.Original], f.Authority, UploadCollision.UploadSkip); var id = f.Sender.Queue(source);
        await f.Sender.SendAsync(id, await f.Target(), f.Request); Assert.Equal(BatchSendingPhase.Completed, f.Sender.All.Single().Phase); Assert.Contains(f.Sender.All.Single().ReceiverOutcomes!, x => x != 1);
        Assert.Contains(events, x => x.Kind == WindowsActivityKind.BatchSend && x.Outcome == WindowsActivityOutcome.CompletedWithIssues && x.Bytes == -1);
        Assert.Contains(events, x => x.Kind == WindowsActivityKind.BatchReceive && x.Outcome == WindowsActivityOutcome.CompletedWithIssues);
        Assert.DoesNotContain(events, x => x.Kind is WindowsActivityKind.BatchSend or WindowsActivityKind.BatchReceive && x.Outcome == WindowsActivityOutcome.Completed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Sender.SendAsync(id, null, f.Request)); Assert.Single(events, x => x.Kind == WindowsActivityKind.BatchSend && x.Outcome == WindowsActivityOutcome.CompletedWithIssues);
        Assert.Equal("generated owner bytes", File.ReadAllText(Path.Combine(f.Destination, "original", "owner.txt"))); Assert.False(File.Exists(Path.Combine(f.Destination, "original", "file.bin")));
    }
}

public sealed partial class BatchDownloadTests
{
    [Fact]
    public async Task Transfer_activity_distinguishes_a_mixed_skipped_folder_download_and_preserves_owner_files()
    {
        Directory.CreateDirectory(Path.Combine(target, "bundle")); File.WriteAllText(Path.Combine(target, "bundle", "owner.txt"), "keep owner bytes");
        var events = new ConcurrentQueue<WindowsActivityEvent>(); await using var files = new WindowsDownloadManager(state) { Activity = events.Enqueue };
        await using var batches = new WindowsBatchDownloads(state, files) { Activity = events.Enqueue };
        var id = batches.Start(peer, await Plan(true), target, DownloadCollision.Skip, Rpc); await batches.WaitAsync(id).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(BatchPhase.CompletedWithIssues, batches.Jobs.Single().Phase);
        Assert.Contains(events, x => x.Kind == WindowsActivityKind.DownloadBatch && x.Outcome == WindowsActivityOutcome.CompletedWithIssues && x.Bytes == new FileInfo(Path.Combine(target, "extra.txt")).Length);
        Assert.Contains(events, x => x.Kind == WindowsActivityKind.DownloadBatchFile && x.Outcome == WindowsActivityOutcome.Completed);
        Assert.Equal("keep owner bytes", File.ReadAllText(Path.Combine(target, "bundle", "owner.txt"))); Assert.False(Directory.Exists(Path.Combine(target, "bundle", "nested")));
    }
}

public sealed partial class DesktopModelTests
{
    [Fact]
    public async Task Recovery_activity_preserves_failed_metadata_cleanup_and_records_only_the_actual_later_completion()
    {
        await using var model = new DesktopController(state, action => action()); var keep = Path.Combine(directory, "owner.txt"); File.WriteAllText(keep, "generated owner bytes");
        var blocker = Path.Combine(state, "downloads", "recovery", "NEVER-LOG-OWNER-NAME.txt"); Directory.CreateDirectory(Path.GetDirectoryName(blocker)!); File.WriteAllText(blocker, "generated preserved bytes");
        await Assert.ThrowsAsync<IOException>(() => model.ClearPreservedProgress()); await model.FlushActivity();
        Assert.Contains(model.Activity, x => x.Description == "Remove preserved progress metadata · needs review"); Assert.Equal("generated preserved bytes", File.ReadAllText(blocker));
        File.Delete(blocker); await model.ClearPreservedProgress(); await model.FlushActivity(); Assert.Contains(model.Activity, x => x.Description == "Remove preserved progress metadata · completed");
        Assert.Equal("generated owner bytes", File.ReadAllText(keep)); Assert.Empty(model.Grants); Assert.Empty(model.Peers); Assert.False(model.Connected);
        Assert.DoesNotContain("NEVER-LOG", File.ReadAllText(Path.Combine(state, "activity.json")));
    }
    [Fact]
    public async Task Recovery_activity_refuses_stale_store_reset_review_then_records_fresh_preservation_without_exposing_ids()
    {
        await using (var initialized = new DesktopController(state, action => action())) { }
        var damagedRoot = Path.Combine(state, "outgoing-batch-jobs"); Directory.CreateDirectory(damagedRoot); var id = ProtocolRules.NewToken(); var damaged = Path.Combine(damagedRoot, id + ".json"); File.WriteAllText(damaged, "generated unreadable record");
        await using var model = new DesktopController(state, action => action()); var stale = await model.ReviewBatchSenderReset(); File.AppendAllText(damaged, " changed after review");
        await Assert.ThrowsAnyAsync<IOException>(() => model.ConfirmBatchSenderReset(stale)); await model.FlushActivity();
        Assert.Contains(model.Activity, x => x.Description == "Preserve and reset sender store · needs review"); Assert.EndsWith(" changed after review", File.ReadAllText(damaged));
        var fresh = await model.ReviewBatchSenderReset(); var archive = await model.ConfirmBatchSenderReset(fresh); await model.FlushActivity();
        Assert.Contains(model.Activity, x => x.Description == "Preserve and reset sender store · completed"); Assert.Empty(model.OutgoingBatchSends); Assert.False(model.Connected);
        var json = File.ReadAllText(Path.Combine(state, "activity.json")); Assert.DoesNotContain(id, json); Assert.DoesNotContain(archive, json); Assert.DoesNotContain(stale.Id, json);
    }
}
