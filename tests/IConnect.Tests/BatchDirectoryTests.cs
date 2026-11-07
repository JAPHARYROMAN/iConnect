using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using System.Text.Json.Nodes;
using Xunit;

namespace IConnect.Tests;

public sealed partial class BatchPlanAdmissionTests
{
    private static UploadBatchManifest DirectoryPlan(int count = 2, UploadCollision collision = UploadCollision.UploadKeepBoth)
    {
        var plan = new UploadBatchManifest { Collision = collision };
        for (var index = 0; index < count; index++) plan.Items.Add(new UploadBatchItem { Parent = index == 0 ? -1 : 0, Name = index == 0 ? "folder" : "empty-" + index, IsDirectory = true });
        return plan;
    }
    private static async Task<UploadBatchHeader> ReadyDirectories(Fixture f, UploadBatchManifest manifest)
    { var (h, target) = await f.Intent(manifest); await f.Begin(h, target); await f.Upload(h, manifest); await f.Call(f.Request(h, FileOperation.SealUploadBatch)); return h; }
    private static FileRequest ChildRequest(Fixture f, UploadBatchHeader h, int index, FileOperation operation = FileOperation.CreateBatchDirectory)
    { var request = f.Request(h, operation); request.BatchItemIndex = (uint)index; return request; }
    private static async Task<UploadBatchItemReceipt> DirectoryCall(Fixture f, UploadBatchHeader h, int index, FileOperation operation = FileOperation.CreateBatchDirectory)
        => UploadBatchRules.CheckItemReceipt(Fixture.Peer, h, index, true, await f.Call(ChildRequest(f, h, index, operation)));
    [Fact]
    public async Task Scoped_directory_children_preserve_nested_empty_structure_and_restart_identity()
    {
        await using var f = new Fixture(); var h = await ReadyDirectories(f, DirectoryPlan());
        Assert.Equal(FileStatus.Conflict, (await f.Raw(ChildRequest(f, h, 1))).Status); Assert.Empty(Directory.EnumerateFileSystemEntries(f.Destination));
        var root = await DirectoryCall(f, h, 0); var nested = await DirectoryCall(f, h, 1);
        Assert.Equal(UploadBatchItemPhase.BatchItemSucceeded, root.Phase); Assert.Equal("empty-1", nested.ResultingName);
        Assert.True(Directory.Exists(Path.Combine(f.Destination, root.ResultingName, nested.ResultingName))); Assert.True(f.Grants.BatchPlans.All.Single().EffectsStarted);
        Assert.Equal(FileStatus.Conflict, (await f.Raw(f.Request(h, FileOperation.CancelUploadBatch))).Status);
        await f.Reopen(); Assert.Empty(f.Grants.BatchPlans.RecoveryIssues);
        Assert.Equal(root, await DirectoryCall(f, h, 0, FileOperation.UploadBatchItemStatus)); Assert.Equal(nested, await DirectoryCall(f, h, 1));
        Assert.Single(Directory.EnumerateDirectories(f.Destination)); Assert.Empty(Directory.EnumerateFiles(f.Destination, "*", SearchOption.AllDirectories));
    }
    [Theory]
    [InlineData(UploadCollision.UploadFail)][InlineData(UploadCollision.UploadSkip)][InlineData(UploadCollision.UploadKeepBoth)]
    public async Task Directory_collisions_never_merge_existing_owner_content(UploadCollision collision)
    {
        await using var f = new Fixture(); Directory.CreateDirectory(Path.Combine(f.Destination, "folder")); File.WriteAllText(Path.Combine(f.Destination, "folder", "owner.txt"), "kept");
        var h = await ReadyDirectories(f, DirectoryPlan(collision: collision)); var root = await DirectoryCall(f, h, 0);
        if (collision == UploadCollision.UploadKeepBoth)
        { Assert.Equal("folder (2)", root.ResultingName); await DirectoryCall(f, h, 1); Assert.True(Directory.Exists(Path.Combine(f.Destination, "folder (2)", "empty-1"))); }
        else
        { Assert.Equal(collision == UploadCollision.UploadSkip ? UploadBatchItemPhase.BatchItemSkipped : UploadBatchItemPhase.BatchItemFailed, root.Phase); Assert.Equal(FileStatus.Conflict, (await f.Raw(ChildRequest(f, h, 1))).Status); }
        Assert.Equal("kept", File.ReadAllText(Path.Combine(f.Destination, "folder", "owner.txt"))); Assert.Empty(Directory.EnumerateDirectories(Path.Combine(f.Destination, "folder")));
    }
    [Fact]
    public async Task Same_name_replacement_never_becomes_a_replayed_batch_directory()
    {
        await using var f = new Fixture(); var h = await ReadyDirectories(f, DirectoryPlan()); var root = await DirectoryCall(f, h, 0);
        var path = Path.Combine(f.Destination, root.ResultingName); Directory.Move(path, path + "-original"); Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, "owner.txt"), "replacement");
        await f.Reopen(); Assert.Equal(FileStatus.StaleReference, (await f.Raw(ChildRequest(f, h, 0))).Status);
        Assert.Equal(FileStatus.StaleReference, (await f.Raw(ChildRequest(f, h, 1))).Status); Assert.Equal("replacement", File.ReadAllText(Path.Combine(path, "owner.txt")));
        Assert.Empty(Directory.EnumerateDirectories(path));
    }
    [Fact]
    public async Task Unknown_directory_effect_is_kept_for_review_without_name_adoption()
    {
        await using var f = new Fixture(); var h = await ReadyDirectories(f, DirectoryPlan()); await DirectoryCall(f, h, 0);
        var path = Path.Combine(f.Profile, "incoming-batch-plans", h.Id + ".0.json"); var json = JsonNode.Parse(File.ReadAllText(path))!;
        json["Identity"] = ""; json["Phase"] = (int)UploadBatchItemPhase.BatchItemPending; File.WriteAllText(path, json.ToJsonString());
        await f.Reopen(); var review = await DirectoryCall(f, h, 0); Assert.Equal(UploadBatchItemPhase.BatchItemReview, review.Phase); Assert.Empty(review.ResultingName);
        Assert.Equal(review, await DirectoryCall(f, h, 0)); Assert.Single(Directory.EnumerateDirectories(f.Destination));
        Assert.Equal(FileStatus.Conflict, (await f.Raw(ChildRequest(f, h, 1))).Status);
    }
    [Fact]
    public async Task Failed_directory_result_commit_cannot_create_a_second_copy_after_restart()
    {
        await using var f = new Fixture(); var h = await ReadyDirectories(f, DirectoryPlan()); FileStream? reader = null;
        var path = Path.Combine(f.Profile, "incoming-batch-plans", h.Id + ".0.json");
        f.Grants.BatchPlans.Changed += () => { if (reader is null && File.Exists(path)) reader = File.OpenRead(path); };
        try { Assert.NotEqual(FileStatus.Ok, (await f.Raw(ChildRequest(f, h, 0))).Status); } finally { reader?.Dispose(); }
        Assert.Single(Directory.EnumerateDirectories(f.Destination)); await f.Reopen();
        Assert.Equal(UploadBatchItemPhase.BatchItemReview, (await DirectoryCall(f, h, 0)).Phase); Assert.Single(Directory.EnumerateDirectories(f.Destination));
    }
    [Fact]
    public async Task Receive_only_batch_folders_have_fresh_names_and_no_directory_activity_metadata()
    {
        await using var f = new Fixture(); f.Grants.Update(f.Grant.Id, StoragePermission.Write | StoragePermission.Create, [], null, true, f.Grant.Label);
        Directory.CreateDirectory(Path.Combine(f.Destination, "folder")); var h = await ReadyDirectories(f, DirectoryPlan()); var result = await DirectoryCall(f, h, 0);
        Assert.NotEqual("folder", result.ResultingName); File.WriteAllText(Path.Combine(f.Destination, result.ResultingName, "owner-added.txt"), "owner activity");
        Assert.Equal(result, await DirectoryCall(f, h, 0, FileOperation.UploadBatchItemStatus));
        Assert.Empty((await f.Call(new() { Operation = FileOperation.ListRoots })).Entries);
    }
    [Fact]
    public async Task Missing_parent_receipt_blocks_shared_receiving_instead_of_recreating_a_folder()
    {
        await using var f = new Fixture(); var h = await ReadyDirectories(f, DirectoryPlan()); await DirectoryCall(f, h, 0); await DirectoryCall(f, h, 1);
        File.Delete(Path.Combine(f.Profile, "incoming-batch-plans", h.Id + ".0.json")); await f.Reopen();
        Assert.NotEmpty(f.Grants.BatchPlans.RecoveryIssues); Assert.NotEqual(FileStatus.Ok, (await f.Raw(ChildRequest(f, h, 1))).Status);
        Assert.Single(Directory.EnumerateDirectories(f.Destination));
    }
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task Missing_leaf_receipt_or_attempt_index_never_becomes_a_fresh_directory_operation(bool receipt)
    {
        await using var f = new Fixture(); var h = await ReadyDirectories(f, DirectoryPlan(1)); await DirectoryCall(f, h, 0);
        File.Delete(Path.Combine(f.Profile, "incoming-batch-plans", h.Id + (receipt ? ".0.json" : ".children.json")));
        Assert.NotEqual(FileStatus.Ok, (await f.Raw(ChildRequest(f, h, 0))).Status); Assert.Single(Directory.EnumerateDirectories(f.Destination));
        await f.Reopen(); Assert.NotEmpty(f.Grants.BatchPlans.RecoveryIssues); Assert.NotEqual(FileStatus.Ok, (await f.Raw(ChildRequest(f, h, 0))).Status);
        Assert.Single(Directory.EnumerateDirectories(f.Destination));
    }
    [Fact]
    public async Task Corrupt_attempt_bits_are_detected_before_a_missing_receipt_can_be_replayed()
    {
        await using var f = new Fixture(); var h = await ReadyDirectories(f, DirectoryPlan(1)); await DirectoryCall(f, h, 0);
        var path = Path.Combine(f.Profile, "incoming-batch-plans", h.Id + ".children.json"); var json = JsonNode.Parse(File.ReadAllText(path))!; json["Bits"] = Convert.ToBase64String([0]); File.WriteAllText(path, json.ToJsonString());
        File.Delete(Path.Combine(f.Profile, "incoming-batch-plans", h.Id + ".0.json"));
        Assert.NotEqual(FileStatus.Ok, (await f.Raw(ChildRequest(f, h, 0))).Status); Assert.NotEmpty(f.Grants.BatchPlans.RecoveryIssues);
        Assert.Single(Directory.EnumerateDirectories(f.Destination));
    }
    [Fact]
    public async Task Full_4096_directory_effects_fit_the_scoped_receipt_capacity()
    {
        await using var f = new Fixture(); var manifest = DirectoryPlan(4096); var h = await ReadyDirectories(f, manifest);
        for (var index = 0; index < manifest.Items.Count; index++) Assert.Equal(UploadBatchItemPhase.BatchItemSucceeded, (await DirectoryCall(f, h, index)).Phase);
        Assert.Equal(4095, Directory.EnumerateDirectories(Path.Combine(f.Destination, "folder")).Count());
        await f.Reopen(); Assert.Empty(f.Grants.BatchPlans.RecoveryIssues); Assert.Equal(UploadBatchItemPhase.BatchItemSucceeded, (await DirectoryCall(f, h, 4095, FileOperation.UploadBatchItemStatus)).Phase);
        Assert.Empty(f.Grants.Uploads.All);
    }
}
