using Google.Protobuf;
using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using Xunit;

namespace IConnect.Tests;

public sealed partial class BatchSenderTests
{
    private static UploadBatchHeader ReceiverHeader(Fixture f, string id) => UploadBatchHeader.Parser.ParseFrom(f.Sender.All.Single(x => x.Id == id).Header!);
    private static FileRequest ReceiverRequest(UploadBatchHeader h, FileOperation op) => new() { Operation = op, TransferId = h.Id, MutationEpoch = h.Epoch };
    private static void FinishReceiverRetirement(Fixture f, string id)
    { for (var attempts = 0; attempts < 260; attempts++) if (f.Receiver.BatchPlans.ContinueRetirement(id)) return; throw new IOException("Receiver retirement did not finish."); }
    private static async Task<UploadBatchHeader> CanceledReceiver(Fixture f, FileEntry target, string epoch)
    {
        var manifest = new UploadBatchManifest { Collision = UploadCollision.UploadKeepBoth }; manifest.Items.Add(new UploadBatchItem { Parent = -1, Name = "empty", IsDirectory = true });
        var h = UploadBatchRules.Header(MutationRules.NewId(), epoch, target.GrantId, manifest);
        Assert.Equal(FileStatus.Ok, (await f.Request(withDestination(), default)).Status);
        Assert.Equal(UploadBatchPlanPhase.UploadPlanCanceled, (await f.Request(ReceiverRequest(h, FileOperation.StopUploadBatch), default)).Batch.Phase); return h;
        FileRequest withDestination() => new() { Operation = FileOperation.BeginUploadBatch, TransferId = h.Id, MutationEpoch = h.Epoch, DestinationId = target.Id, BatchHeader = h };
    }
    [Fact]
    public async Task Finished_receiver_plans_release_active_peer_slots_while_parent_receipts_remain()
    {
        await using var f = new Fixture(); var first = await f.Queue(); var source = Assert.Single(f.Sources.All).Id; var target = await f.Target();
        for (var index = 0; index < 6; index++)
        {
            var id = index == 0 ? first : f.Sender.Queue(source, anotherCopy: true); await f.Sender.SendAsync(id, target, f.Request);
            Assert.Equal(BatchSendingPhase.Completed, f.Sender.All.Single(x => x.Id == id).Phase); Archive(f, id);
        }
        Assert.Equal(6, f.Receiver.BatchPlans.All.Count); Assert.Equal(0, f.Receiver.BatchPlans.ReservedBytes(null)); Assert.Equal(6, Directory.EnumerateDirectories(f.Destination).Count());
        await f.ReopenReceiver(); Assert.Empty(f.Receiver.BatchPlans.RecoveryIssues); Assert.Equal(6, f.Receiver.BatchPlans.All.Count);
    }
    [Fact]
    public async Task Reviewed_receiver_retirement_keeps_final_parent_outcomes_and_cannot_replay_removed_items()
    {
        await using var f = new Fixture(); var id = await f.Queue(); var target = await f.Target(); await f.Sender.SendAsync(id, target, f.Request); var h = ReceiverHeader(f, id);
        var before = await f.Request(ReceiverRequest(h, FileOperation.UploadBatchStatus), default); var review = f.Receiver.BatchPlans.ReviewRetirement(h.Id);
        if (!f.Receiver.BatchPlans.ConfirmRetirement(review)) FinishReceiverRetirement(f, h.Id);
        Assert.True(Assert.Single(f.Receiver.BatchPlans.All).Retired); Assert.Equal(before.Batch.ItemOutcomes, (await f.Request(ReceiverRequest(h, FileOperation.UploadBatchStatus), default)).Batch.ItemOutcomes);
        var child = ReceiverRequest(h, FileOperation.UploadBatchItemStatus); child.BatchItemIndex = 0; Assert.Equal(FileStatus.NotFound, (await f.Request(child, default)).Status);
        var begin = ReceiverRequest(h, FileOperation.BeginUploadBatch); begin.BatchHeader = h; begin.DestinationId = target.Id;
        Assert.Equal(UploadBatchPlanPhase.UploadPlanCompleted, (await f.Request(begin, default)).Batch.Phase); Assert.Single(Directory.EnumerateDirectories(f.Destination));
        await f.ReopenReceiver(); Assert.Empty(f.Receiver.BatchPlans.RecoveryIssues); Assert.True(Assert.Single(f.Receiver.BatchPlans.All).Retired); Assert.Equal(before.Batch.ItemOutcomes, (await f.Request(ReceiverRequest(h, FileOperation.StopUploadBatch), default)).Batch.ItemOutcomes);
        Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Destination, "original", "file.bin"))); Assert.Single(f.Sources.All);
    }
    [Fact]
    public async Task Active_receiver_plans_and_changed_or_reused_reviews_cannot_retire_history()
    {
        await using var f = new Fixture(); var target = await f.Target(); var epoch = (await f.Request(new() { Operation = FileOperation.UploadBatchContext }, default)).MutationEpoch;
        var h = await CanceledReceiver(f, target, epoch); var review = f.Receiver.BatchPlans.ReviewRetirement(h.Id);
        Assert.Throws<IOException>(() => f.Receiver.BatchPlans.ConfirmRetirement(review with { MetadataFiles = 999 })); Assert.Throws<IOException>(() => f.Receiver.BatchPlans.ConfirmRetirement(review));
        review = f.Receiver.BatchPlans.ReviewRetirement(h.Id); Assert.True(f.Receiver.BatchPlans.ConfirmRetirement(review)); Assert.Throws<IOException>(() => f.Receiver.BatchPlans.ConfirmRetirement(review));
        var id = await f.Queue(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<FileResponse> Lost(FileRequest value, CancellationToken ct) { var reply = await f.Request(value, ct); if (value.Operation == FileOperation.BeginUploadBatch) { entered.TrySetResult(); throw new IOException("Lost reply."); } return reply; }
        await f.Sender.SendAsync(id, target, Lost); var active = ReceiverHeader(f, id); Assert.Throws<IOException>(() => f.Receiver.BatchPlans.ReviewRetirement(active.Id)); Assert.Throws<IOException>(() => f.Receiver.BatchPlans.ResetClosedHistory());
    }
    [Fact]
    public async Task Compact_receipts_free_detailed_capacity_and_closed_reset_rotates_the_replay_namespace()
    {
        await using var f = new Fixture(); var target = await f.Target(); var epoch = (await f.Request(new() { Operation = FileOperation.UploadBatchContext }, default)).MutationEpoch;
        var headers = new List<UploadBatchHeader>(); for (var index = 0; index < WindowsBatchPlans.MaxDetailedPlans; index++) headers.Add(await CanceledReceiver(f, target, epoch));
        var manifest = new UploadBatchManifest { Collision = UploadCollision.UploadKeepBoth }; manifest.Items.Add(new UploadBatchItem { Parent = -1, Name = "extra", IsDirectory = true });
        var extra = UploadBatchRules.Header(MutationRules.NewId(), epoch, target.GrantId, manifest); var begin = ReceiverRequest(extra, FileOperation.BeginUploadBatch); begin.BatchHeader = extra; begin.DestinationId = target.Id;
        Assert.Equal(FileStatus.ResourceExhausted, (await f.Request(begin, default)).Status);
        Assert.True(f.Receiver.BatchPlans.ConfirmRetirement(f.Receiver.BatchPlans.ReviewRetirement(headers[0].Id))); Assert.Equal(FileStatus.Ok, (await f.Request(begin, default)).Status);
        await f.Request(ReceiverRequest(extra, FileOperation.StopUploadBatch), default); f.Receiver.BatchPlans.ResetClosedHistory(); var fresh = (await f.Request(new() { Operation = FileOperation.UploadBatchContext }, default)).MutationEpoch;
        Assert.NotEqual(epoch, fresh); Assert.Empty(f.Receiver.BatchPlans.All); Assert.Equal(FileStatus.StaleReference, (await f.Request(begin, default)).Status); Assert.Empty(Directory.EnumerateFileSystemEntries(f.Destination));
    }
    [Fact]
    public async Task Large_receiver_retirement_resumes_both_metadata_passes_after_restart_without_deleting_publication()
    {
        await using var f = new Fixture(extras: 66); var id = await f.Queue(); await f.Sender.SendAsync(id, await f.Target(), f.Request); var h = ReceiverHeader(f, id); Assert.Equal(70u, h.ItemCount);
        Assert.False(f.Receiver.BatchPlans.ConfirmRetirement(f.Receiver.BatchPlans.ReviewRetirement(h.Id))); Assert.Equal(32u, Assert.Single(f.Receiver.BatchPlans.All).RetirementCursor);
        await f.ReopenReceiver(); Assert.Empty(f.Receiver.BatchPlans.RecoveryIssues);
        while (Assert.Single(f.Receiver.BatchPlans.All).RetirementCursor < 102) Assert.False(f.Receiver.BatchPlans.ContinueRetirement(h.Id));
        f.Service.Dispose(); await f.Receiver.BatchPlans.WhenFilesIdleAsync(); var path = Path.Combine(f.Root, "receiver", "incoming-batch-plans", h.Id + ".json");
        var saved = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!; saved["Row"]!["RetirementCursor"] = 70; File.WriteAllText(path, saved.ToJsonString());
        await f.ReopenReceiver(); Assert.Empty(f.Receiver.BatchPlans.RecoveryIssues); FinishReceiverRetirement(f, h.Id); Assert.True(Assert.Single(f.Receiver.BatchPlans.All).Retired);
        Assert.Equal(68, Directory.EnumerateFiles(Path.Combine(f.Destination, "original")).Count()); Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Destination, "original", "file.bin")));
        await f.ReopenReceiver(); Assert.Empty(f.Receiver.BatchPlans.RecoveryIssues); Assert.True(Assert.Single(f.Receiver.BatchPlans.All).Retired);
    }
    [Fact]
    public async Task Compact_receipt_budget_is_bounded_and_reopens_without_replaying_admission()
    {
        await using var f = new Fixture(); var target = await f.Target(); var epoch = (await f.Request(new() { Operation = FileOperation.UploadBatchContext }, default)).MutationEpoch;
        for (var index = 0; index < WindowsBatchPlans.MaxRetiredPlans; index++) { var h = await CanceledReceiver(f, target, epoch); Assert.True(f.Receiver.BatchPlans.ConfirmRetirement(f.Receiver.BatchPlans.ReviewRetirement(h.Id))); }
        var extra = await CanceledReceiver(f, target, epoch); Assert.Throws<IOException>(() => f.Receiver.BatchPlans.ReviewRetirement(extra.Id)); await f.ReopenReceiver();
        Assert.Empty(f.Receiver.BatchPlans.RecoveryIssues); Assert.Equal(WindowsBatchPlans.MaxRetiredPlans, f.Receiver.BatchPlans.All.Count(x => x.Retired)); Assert.Empty(Directory.EnumerateFileSystemEntries(f.Destination));
    }
    [Fact]
    public async Task Reappearing_private_parts_and_changed_metadata_prevent_retirement_without_deleting_any_file()
    {
        await using var f = new Fixture(); var id = await f.Queue(); await f.Sender.SendAsync(id, await f.Target(), f.Request); var h = ReceiverHeader(f, id); var source = f.Sources.ReadManifest(Assert.Single(f.Sources.All).Id); var index = source.Items.IndexOf(source.Items.Single(x => x.Name == "file.bin"));
        var path = Path.Combine(f.Root, "receiver", "incoming-batch-plans", h.Id + "." + index + ".upload.json"); var cache = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!; var temporary = cache["Rows"]![0]!["Temporary"]!.GetValue<string>();
        var part = Path.Combine(f.Destination, "original", ".iconnect-transfer-" + temporary + ".part"); File.WriteAllText(part, "owner content"); Assert.ThrowsAny<Exception>(() => f.Receiver.BatchPlans.ReviewRetirement(h.Id)); Assert.Equal("owner content", File.ReadAllText(part)); Assert.False(Assert.Single(f.Receiver.BatchPlans.All).Retiring);
        File.Delete(part); var review = f.Receiver.BatchPlans.ReviewRetirement(h.Id); var child = Path.Combine(f.Root, "receiver", "incoming-batch-plans", h.Id + ".0.json"); File.AppendAllText(child, " ");
        Assert.Throws<IOException>(() => f.Receiver.BatchPlans.ConfirmRetirement(review)); Assert.True(File.Exists(child)); Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Destination, "original", "file.bin")));
    }
    [Fact]
    public async Task Legacy_receiver_schema_two_metadata_keeps_its_original_plan_before_retirement_upgrade()
    {
        await using var f = new Fixture(); var target = await f.Target(); var epoch = (await f.Request(new() { Operation = FileOperation.UploadBatchContext }, default)).MutationEpoch; var h = await CanceledReceiver(f, target, epoch); f.Service.Dispose();
        var path = Path.Combine(f.Root, "receiver", "incoming-batch-plans", h.Id + ".json"); var saved = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!; saved["Schema"] = 2;
        foreach (var field in new[] { "Retiring", "Retired", "RetirementCursor", "RetirementBits" }) saved["Row"]!.AsObject().Remove(field); File.WriteAllText(path, saved.ToJsonString()); await f.ReopenReceiver();
        Assert.Empty(f.Receiver.BatchPlans.RecoveryIssues); Assert.Equal(h, (await f.Request(ReceiverRequest(h, FileOperation.UploadBatchStatus), default)).Batch.Header);
        Assert.True(f.Receiver.BatchPlans.ConfirmRetirement(f.Receiver.BatchPlans.ReviewRetirement(h.Id))); Assert.Equal(3, System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!["Schema"]!.GetValue<int>());
    }
}
