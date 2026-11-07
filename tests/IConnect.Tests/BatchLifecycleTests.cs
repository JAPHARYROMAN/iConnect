using Google.Protobuf;
using IConnect.Core;
using IConnect.Protocol;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Xunit;

namespace IConnect.Tests;

public sealed partial class BatchPlanAdmissionTests
{
    private static async Task<UploadBatchReceipt> StopPlan(Fixture f, UploadBatchHeader header)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var receipt = (await f.Call(f.Request(header, FileOperation.StopUploadBatch))).Batch;
            if (UploadBatchRules.IsTerminal(receipt.Phase)) return receipt;
            await f.Grants.BatchPlans.WhenFilesIdleAsync(header.Id);
        }
        throw new IOException("Fixture batch did not stop.");
    }
    [Fact]
    public async Task Completion_requires_final_item_receipts_releases_bytes_and_survives_lost_replies_and_restart()
    {
        await using var f = new Fixture(checks: new AcceptedFiles()); var bytes = new byte[] { 7, 8, 9 }; var h = await ReadyDirectories(f, FilePlan(bytes));
        Assert.Equal(FileStatus.Conflict, (await f.Raw(f.Request(h, FileOperation.FinishUploadBatch))).Status); Assert.Equal(3, f.Grants.BatchPlans.ReservedBytes(Fixture.Peer));
        await f.Call(ChildRequest(f, h, 0)); await f.Call(ChildRequest(f, h, 1, FileOperation.PrepareBatchFile)); await WriteFile(f, h, bytes);
        var file = await FinishFile(f, h); var path = Path.Combine(f.Destination, "folder", file.FinalName);
        var done = (await f.Call(f.Request(h, FileOperation.FinishUploadBatch))).Batch;
        Assert.Equal(UploadBatchPlanPhase.UploadPlanCompleted, done.Phase); Assert.Equal(new byte[] { 1, 1 }, done.ItemOutcomes.ToByteArray()); Assert.Equal(0, f.Grants.BatchPlans.ReservedBytes(null));
        Assert.Equal(done, (await f.Call(f.Request(h, FileOperation.FinishUploadBatch))).Batch); Assert.Equal(done, (await f.Call(f.Request(h, FileOperation.StopUploadBatch))).Batch);
        Assert.Equal(FileStatus.Conflict, (await f.Raw(ChildRequest(f, h, 1, FileOperation.PrepareBatchFile))).Status);
        await f.Reopen(); Assert.Empty(f.Grants.BatchPlans.RecoveryIssues); Assert.Equal(done, (await f.Call(f.Request(h, FileOperation.UploadBatchStatus))).Batch);
        Assert.Equal(file, (await f.Call(ChildRequest(f, h, 1, FileOperation.UploadBatchItemStatus))).Upload); Assert.Equal(bytes, File.ReadAllBytes(path));
        var target = (await f.Call(new() { Operation = FileOperation.UploadContext })).Entries.Single(); Assert.Equal(done, (await f.Begin(h, target.Id)).Batch);
        var next = await f.Intent(FilePlan(bytes)); next.Header.TotalBytes = UploadRules.PeerQuota;
        await f.Begin(next.Header, next.Target); Assert.Equal(UploadRules.PeerQuota, f.Grants.BatchPlans.ReservedBytes(null));
    }
    [Fact]
    public async Task Completion_folds_source_errors_and_skipped_branches_without_creating_their_descendants()
    {
        await using var f = new Fixture(checks: new AcceptedFiles()); Directory.CreateDirectory(Path.Combine(f.Destination, "existing")); var bytes = new byte[] { 3, 4, 5 };
        var manifest = new UploadBatchManifest { Collision = UploadCollision.UploadSkip };
        manifest.Items.Add(new UploadBatchItem { Parent = -1, Name = "existing", IsDirectory = true });
        manifest.Items.Add(new UploadBatchItem { Parent = 0, Name = "not-copied.bin", Size = 3, Sha256 = ByteString.CopyFrom(SHA256.HashData(bytes)) });
        manifest.Items.Add(new UploadBatchItem { Parent = -1, Name = "unavailable.bin", Error = "The owner selected an unavailable source." });
        manifest.Items.Add(new UploadBatchItem { Parent = -1, Name = "copied.bin", Size = 3, Sha256 = ByteString.CopyFrom(SHA256.HashData(bytes)) });
        var h = await ReadyDirectories(f, manifest); Assert.Equal(UploadBatchItemPhase.BatchItemSkipped, (await f.Call(ChildRequest(f, h, 0))).BatchItem.Phase);
        await f.Call(ChildRequest(f, h, 3, FileOperation.PrepareBatchFile)); await WriteFile(f, h, bytes, 3); await FinishFile(f, h, 3);
        var done = (await f.Call(f.Request(h, FileOperation.FinishUploadBatch))).Batch; Assert.Equal(new byte[] { 2, 2, 3, 1 }, done.ItemOutcomes.ToByteArray());
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(f.Destination, "existing"))); Assert.Equal(0, f.Grants.BatchPlans.ReservedBytes(null));
        await f.Reopen(); Assert.Empty(f.Grants.BatchPlans.RecoveryIssues); Assert.Equal(done, (await f.Call(f.Request(h, FileOperation.UploadBatchStatus))).Batch);
    }
    [Fact]
    public async Task Stop_removes_only_identified_partials_keeps_created_folders_and_cannot_be_resumed()
    {
        await using var f = new Fixture(); var bytes = new byte[StorageRules.MaxReadRange + 7]; var h = await ReadyDirectories(f, FilePlan(bytes, 2));
        await f.Call(ChildRequest(f, h, 0)); var keep = Path.Combine(f.Destination, "folder", "owner.txt"); File.WriteAllText(keep, "keep");
        await f.Call(ChildRequest(f, h, 1, FileOperation.PrepareBatchFile)); var write = ChildRequest(f, h, 1, FileOperation.WriteBatchFile); write.Content = ByteString.CopyFrom(bytes, 0, StorageRules.MaxReadRange); await f.Call(write);
        var stopped = await StopPlan(f, h); Assert.Equal(UploadBatchPlanPhase.UploadPlanStopped, stopped.Phase); Assert.Equal(new byte[] { 1, 3, 0 }, stopped.ItemOutcomes.ToByteArray());
        Assert.Equal("keep", File.ReadAllText(keep)); Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(keep)!)); Assert.Equal(0, f.Grants.BatchPlans.ReservedBytes(null));
        Assert.Equal(UploadPhase.UploadCanceled, (await f.Call(ChildRequest(f, h, 1, FileOperation.UploadBatchItemStatus))).Upload.Phase);
        Assert.Equal(FileStatus.Conflict, (await f.Raw(write)).Status); Assert.Equal(FileStatus.Conflict, (await f.Raw(ChildRequest(f, h, 2, FileOperation.PrepareBatchFile))).Status);
        Assert.Equal(FileStatus.Conflict, (await f.Raw(f.Request(h, FileOperation.FinishUploadBatch))).Status);
        await f.Reopen(); Assert.Empty(f.Grants.BatchPlans.RecoveryIssues); Assert.Equal(stopped, await StopPlan(f, h)); Assert.Equal("keep", File.ReadAllText(keep));
    }
    [Fact]
    public async Task Stop_commits_before_canceling_a_held_native_check_and_keeps_its_possible_publication()
    {
        var checks = new HeldFiles(); await using var f = new Fixture(checks: checks); var bytes = new byte[] { 8, 4, 2 }; var h = await ReadyDirectories(f, FilePlan(bytes));
        var canceledState = new TaskCompletionSource<UploadBatchPlanPhase>(TaskCreationOptions.RunContinuationsAsynchronously);
        checks.Canceled = () => canceledState.TrySetResult((UploadBatchPlanPhase)JsonNode.Parse(File.ReadAllText(Path.Combine(f.Profile, "incoming-batch-plans", h.Id + ".json")))!["Row"]!["Phase"]!.GetValue<int>());
        await f.Call(ChildRequest(f, h, 0)); await f.Call(ChildRequest(f, h, 1, FileOperation.PrepareBatchFile)); await WriteFile(f, h, bytes);
        await f.Call(ChildRequest(f, h, 1, FileOperation.FinishBatchFile));
        var end = DateTime.UtcNow.AddSeconds(10); while (Volatile.Read(ref checks.Active) == 0 && DateTime.UtcNow < end) await Task.Delay(10); Assert.Equal(1, checks.Active);
        var barrier = (await f.Call(f.Request(h, FileOperation.StopUploadBatch)).WaitAsync(TimeSpan.FromSeconds(5))).Batch;
        Assert.Contains(barrier.Phase, new[] { UploadBatchPlanPhase.UploadPlanStopping, UploadBatchPlanPhase.UploadPlanStopped });
        Assert.Equal(UploadBatchPlanPhase.UploadPlanStopping, await canceledState.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(barrier.Phase == UploadBatchPlanPhase.UploadPlanStopped ? 0 : 3, f.Grants.BatchPlans.ReservedBytes(null));
        Assert.Equal(FileStatus.Conflict, (await f.Raw(ChildRequest(f, h, 1, FileOperation.FinishBatchFile))).Status);
        await f.Grants.BatchPlans.WhenFilesIdleAsync(); checks.Release.TrySetResult(); var stopped = await StopPlan(f, h);
        Assert.Equal(new byte[] { 1, 4 }, stopped.ItemOutcomes.ToByteArray()); Assert.Equal(0, f.Grants.BatchPlans.ReservedBytes(null));
        var published = Assert.Single(Directory.EnumerateFiles(Path.Combine(f.Destination, "folder"))); Assert.Equal(bytes, File.ReadAllBytes(published)); Assert.Contains("ZoneId=4", File.ReadAllText(published + ":Zone.Identifier"));
        Assert.Equal(UploadPhase.UploadReview, (await f.Call(ChildRequest(f, h, 1, FileOperation.UploadBatchItemStatus))).Upload.Phase);
        await f.Reopen(); Assert.Empty(f.Grants.BatchPlans.RecoveryIssues); Assert.Equal(stopped, await StopPlan(f, h));
    }
    [Fact]
    public async Task Stop_cleanup_is_bounded_and_cold_retry_continues_the_same_barrier()
    {
        await using var f = new Fixture(); var h = await ReadyDirectories(f, FilePlan([1, 2, 3], 17)); await f.Call(ChildRequest(f, h, 0));
        for (var index = 1; index <= 17; index++) await f.Call(ChildRequest(f, h, index, FileOperation.PrepareBatchFile));
        var stopping = (await f.Call(f.Request(h, FileOperation.StopUploadBatch))).Batch; Assert.Equal(UploadBatchPlanPhase.UploadPlanStopping, stopping.Phase); Assert.Equal(51, f.Grants.BatchPlans.ReservedBytes(null));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(f.Destination, "folder"))); await f.Reopen(); Assert.Empty(f.Grants.BatchPlans.RecoveryIssues);
        var stopped = await StopPlan(f, h); Assert.Equal(UploadBatchPlanPhase.UploadPlanStopped, stopped.Phase); Assert.Empty(Directory.EnumerateFiles(Path.Combine(f.Destination, "folder")));
        Assert.Equal(0, f.Grants.BatchPlans.ReservedBytes(null)); Assert.All(stopped.ItemOutcomes.Skip(1), value => Assert.Equal(3, value));
    }
    [Fact]
    public async Task Changed_terminal_outcomes_cannot_release_or_adopt_retained_history_after_restart()
    {
        await using var f = new Fixture(); var h = await ReadyDirectories(f, FilePlan([1])); await f.Call(ChildRequest(f, h, 0)); await f.Call(ChildRequest(f, h, 1, FileOperation.PrepareBatchFile));
        await StopPlan(f, h); var path = Path.Combine(f.Profile, "incoming-batch-plans", h.Id + ".json"); var value = JsonNode.Parse(File.ReadAllText(path))!;
        value["Row"]!["Outcomes"] = Convert.ToBase64String(new byte[] { 1, 1 }); File.WriteAllText(path, value.ToJsonString());
        await f.Reopen(); Assert.NotEmpty(f.Grants.BatchPlans.RecoveryIssues); Assert.True(Directory.Exists(Path.Combine(f.Destination, "folder"))); Assert.True(File.Exists(path));
    }
    [Fact]
    public async Task Stopping_one_plan_does_not_cancel_another_plans_active_native_check()
    {
        var checks = new HeldFiles(); await using var f = new Fixture(checks: checks); var bytes = new byte[] { 2, 3, 4 };
        var first = await ReadyDirectories(f, FilePlan(bytes)); var second = await ReadyDirectories(f, FilePlan(bytes));
        foreach (var h in new[] { first, second }) { await f.Call(ChildRequest(f, h, 0)); await f.Call(ChildRequest(f, h, 1, FileOperation.PrepareBatchFile)); await WriteFile(f, h, bytes); await f.Call(ChildRequest(f, h, 1, FileOperation.FinishBatchFile)); }
        var end = DateTime.UtcNow.AddSeconds(10); while (Volatile.Read(ref checks.Active) != 2 && DateTime.UtcNow < end) await Task.Delay(10); Assert.Equal(2, checks.Active);
        var stopped = await StopPlan(f, first); Assert.Equal(UploadBatchPlanPhase.UploadPlanStopped, stopped.Phase); Assert.Equal(1, checks.Active); Assert.Equal(3, f.Grants.BatchPlans.ReservedBytes(null));
        checks.Release.TrySetResult(); await f.Grants.BatchPlans.WhenFilesIdleAsync(second.Id); Assert.Equal(UploadPhase.UploadCompleted, (await BatchFileStatus(f, second)).Phase);
        Assert.Equal(UploadBatchPlanPhase.UploadPlanCompleted, (await f.Call(f.Request(second, FileOperation.FinishUploadBatch))).Batch.Phase); Assert.Equal(0, f.Grants.BatchPlans.ReservedBytes(null));
    }
    [Fact]
    public async Task Malformed_or_foreign_end_requests_do_not_install_a_stop_barrier()
    {
        await using var f = new Fixture(); var h = await ReadyDirectories(f, FilePlan([1])); var malformed = f.Request(h, FileOperation.StopUploadBatch); malformed.BatchItemIndex = 0;
        Assert.Equal(FileStatus.InvalidRequest, (await f.Raw(malformed)).Status); var wrong = f.Request(h, FileOperation.StopUploadBatch); wrong.MutationEpoch = ProtocolRules.NewToken();
        Assert.Equal(FileStatus.StaleReference, (await f.Raw(wrong)).Status); Assert.Equal(FileStatus.NotFound, (await f.Raw(f.Request(h, FileOperation.StopUploadBatch), new string('c', 64))).Status);
        Assert.Equal(UploadBatchPlanPhase.UploadPlanReady, (await f.Call(f.Request(h, FileOperation.UploadBatchStatus))).Batch.Phase);
        var canceled = await StopPlan(f, h); Assert.Equal(UploadBatchPlanPhase.UploadPlanCanceled, canceled.Phase); Assert.Empty(canceled.ItemOutcomes); Assert.Empty(Directory.EnumerateFileSystemEntries(f.Destination));
    }
}
