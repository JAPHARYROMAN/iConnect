using System.Text.Json.Nodes;
using IConnect.Connectivity;
using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using Xunit;

namespace IConnect.Tests;

public sealed partial class RecoveryTests : IDisposable
{
    private readonly string directory;
    private readonly string shared;
    private readonly string state;
    private readonly string peer = new('a', 64);
    private StorageGrantStore grants;
    private WindowsFileService files;
    private StorageGrant grant;
    public RecoveryTests()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Directory.Build.props"))) repository = repository.Parent;
        directory = Path.Combine(repository!.FullName, ".local", "recovery-tests", ProtocolRules.NewToken());
        shared = Path.Combine(directory, "shared"); state = Path.Combine(directory, "state"); Directory.CreateDirectory(shared);
        grants = new(state); grant = grants.Add(peer, shared, StoragePermission.ReadOnly | StoragePermission.Delete | StoragePermission.Create);
        files = new(grants); File.WriteAllText(Path.Combine(shared, "original.txt"), "Keep these real bytes.");
    }
    private async Task<FileResponse> Request(FileRequest request, string? asPeer = null)
    {
        request.RequestId = ProtocolRules.NewToken(); var result = await files.ExecuteAsync(asPeer ?? peer, request, default);
        StorageRules.ValidateResponse(result); return result;
    }
    private async Task<FileEntry> Entry(string name = "original.txt")
    {
        var root = Assert.Single((await Request(new() { Operation = FileOperation.ListRoots })).Entries);
        var listing = await Request(new() { Operation = FileOperation.ListDirectory, NodeId = root.Id });
        return listing.Entries.Single(x => x.Name == name);
    }
    private async Task<FileRequest> DeleteRequest(string name = "original.txt")
    {
        var entry = await Entry(name); var context = await Request(new() { Operation = FileOperation.RecoveryContext });
        return new() { Operation = FileOperation.Delete, NodeId = entry.Id, ExpectedVersion = entry.Version,
            MutationId = MutationRules.NewId(), MutationEpoch = context.MutationEpoch };
    }
    private async Task<FileResponse> List(long offset = 0)
    {
        var context = await Request(new() { Operation = FileOperation.RecoveryContext });
        return await Request(new() { Operation = FileOperation.ListDeleted, MutationEpoch = context.MutationEpoch, Offset = offset });
    }
    private async Task<FileResponse> Restore(string id, string name = "")
    {
        var context = await Request(new() { Operation = FileOperation.RecoveryContext });
        return await Request(new() { Operation = FileOperation.Restore, DeletedId = id, Name = name,
            MutationId = MutationRules.NewId(), MutationEpoch = context.MutationEpoch });
    }
    private void Restart() { files.Dispose(); grants = new(state); files = new(grants); }
    [Fact]
    public async Task Alternate_restore_reconciles_after_restart_with_the_original_parent_replaced()
    {
        var originalParent = Directory.CreateDirectory(Path.Combine(shared, "nested")).FullName;
        File.WriteAllText(Path.Combine(originalParent, "notes.txt"), "Saved bytes");
        var parent = await Entry("nested");
        var listing = await Request(new() { Operation = FileOperation.ListDirectory, NodeId = parent.Id });
        var file = Assert.Single(listing.Entries);
        var context = await Request(new() { Operation = FileOperation.RecoveryContext });
        var deletion = await Request(new() { Operation = FileOperation.Delete, NodeId = file.Id, ExpectedVersion = file.Version,
            MutationId = MutationRules.NewId(), MutationEpoch = context.MutationEpoch });
        Assert.Equal(MutationPhase.ChangeSucceeded, deletion.Mutation.Phase);
        var row = Assert.Single(grants.Deleted.ListLocalRecords());
        Directory.Move(originalParent, Path.Combine(shared, "renamed-parent"));
        Directory.CreateDirectory(originalParent);
        File.WriteAllText(row.OriginalLocation, "Different object");

        var destination = Directory.CreateDirectory(Path.Combine(directory, "owner-destination")).FullName;
        var review = grants.Deleted.PrepareAlternateRestoreLocal(row.Item.Id, destination, "owner-notes.txt");
        var restored = grants.Deleted.ConfirmAlternateRestoreLocal(review.Token);
        Assert.Equal(MutationPhase.ChangeSucceeded, restored.Phase);
        RewriteCatalog(json =>
        {
            json["Items"]![0]!["State"] = (int)DeletedState.RestorePending;
            json["Receipts"]!.AsArray().Single(x => (string?)x!["Id"] == restored.Id)!["Phase"] = (int)MutationPhase.ChangePending;
        });
        Restart();
        var reopened = Assert.Single(grants.Deleted.ListLocalRecords());
        Assert.Equal(DeletedState.DeletedRestored, reopened.Item.State);
        Assert.Equal(review.Destination, reopened.RestoredLocation);
        Assert.Equal("Recorded object found", grants.Deleted.InspectLocal(row.Item.Id).RestoredStatus);
        Assert.Equal("Saved bytes", File.ReadAllText(reopened.RestoredLocation));
        Assert.Equal("Different object", File.ReadAllText(row.OriginalLocation));
        Assert.False(File.Exists(row.SavedLocation));
        Assert.Empty(Assert.Single((await List()).DeletedItems).RestoredName);
        var status = await Request(new() { Operation = FileOperation.RecoveryStatus, MutationId = restored.Id, MutationEpoch = context.MutationEpoch });
        Assert.Equal(FileStatus.NotFound, status.Status);
    }
    [Fact]
    public async Task Alternate_restore_rejects_replaced_destination_and_keeps_both_files_on_collision()
    {
        await Request(await DeleteRequest());
        var row = Assert.Single(grants.Deleted.ListLocalRecords());
        var destination = Directory.CreateDirectory(Path.Combine(directory, "destination")).FullName;
        var review = grants.Deleted.PrepareAlternateRestoreLocal(row.Item.Id, destination, "kept.txt");
        Directory.Move(destination, destination + "-old");
        Directory.CreateDirectory(destination);
        Assert.ThrowsAny<IOException>(() => grants.Deleted.ConfirmAlternateRestoreLocal(review.Token));
        Assert.Empty(Directory.GetFiles(destination));
        Assert.Throws<IOException>(() => grants.Deleted.ConfirmAlternateRestoreLocal(review.Token));

        var collision = Path.Combine(destination, "kept.txt");
        File.WriteAllText(collision, "Existing bytes");
        review = grants.Deleted.PrepareAlternateRestoreLocal(row.Item.Id, destination, "kept.txt");
        var result = grants.Deleted.ConfirmAlternateRestoreLocal(review.Token);
        Assert.Equal(FileStatus.Conflict, result.Status);
        Assert.Equal(MutationPhase.ChangeFailed, result.Phase);
        Assert.Equal("Existing bytes", File.ReadAllText(collision));
        Assert.Equal("Keep these real bytes.", File.ReadAllText(row.SavedLocation));
        Assert.True(Assert.Single(grants.Deleted.ListLocalRecords()).Item.CanRestore);
    }
    [Fact]
    public async Task Alternate_restore_rejects_changed_saved_folder_contents_after_review()
    {
        var folder = Directory.CreateDirectory(Path.Combine(shared, "folder")).FullName;
        File.WriteAllText(Path.Combine(folder, "nested.txt"), "Before review");
        await Request(await DeleteRequest("folder"));
        var row = Assert.Single(grants.Deleted.ListLocalRecords());
        var destination = Directory.CreateDirectory(Path.Combine(directory, "destination")).FullName;
        var review = grants.Deleted.PrepareAlternateRestoreLocal(row.Item.Id, destination, "restored");
        File.AppendAllText(Path.Combine(row.SavedLocation, "nested.txt"), " and after");
        Assert.Throws<IOException>(() => grants.Deleted.ConfirmAlternateRestoreLocal(review.Token));
        Assert.False(Path.Exists(review.Destination));
        Assert.Equal("Before review and after", File.ReadAllText(Path.Combine(row.SavedLocation, "nested.txt")));
    }
    [Fact]
    public async Task Delete_restart_replay_and_restore_preserve_real_bytes()
    {
        var request = await DeleteRequest(); var deleted = await Request(request);
        Assert.Equal(FileStatus.Ok, deleted.Status); Assert.Equal(MutationPhase.ChangeSucceeded, deleted.Mutation.Phase);
        Assert.False(File.Exists(Path.Combine(shared, "original.txt")));
        var vault = Assert.Single(Directory.GetDirectories(shared)); Assert.StartsWith(".iconnect-trash-", Path.GetFileName(vault));
        Assert.Equal("Keep these real bytes.", File.ReadAllText(Assert.Single(Directory.GetFiles(vault))));
        Restart(); var replay = await Request(request); Assert.Equal(MutationPhase.ChangeSucceeded, replay.Mutation.Phase);
        var row = Assert.Single((await List()).DeletedItems); Assert.Equal(DeletedState.DeletedStored, row.State);
        Assert.Equal(FileStatus.Ok, (await Restore(row.Id)).Status);
        Assert.Equal("Keep these real bytes.", File.ReadAllText(Path.Combine(shared, "original.txt")));
        Assert.Equal(DeletedState.DeletedRestored, Assert.Single((await List()).DeletedItems).State);
        Assert.Equal(MutationPhase.ChangeSucceeded, (await Request(request)).Mutation.Phase); // Never delete the restored original again.
        Assert.True(File.Exists(Path.Combine(shared, "original.txt")));
    }
    [Fact]
    public async Task Restore_collision_keeps_both_copies_and_allows_an_explicit_new_name()
    {
        var deletion = await DeleteRequest(); await Request(deletion);
        File.WriteAllText(Path.Combine(shared, "original.txt"), "New destination bytes.");
        var failed = await Restore(deletion.MutationId); Assert.Equal(FileStatus.Conflict, failed.Status);
        Assert.Equal(DeletedState.DeletedStored, Assert.Single((await List()).DeletedItems).State);
        Assert.Equal(FileStatus.Ok, (await Restore(deletion.MutationId, "recovered.txt")).Status);
        Assert.Equal("New destination bytes.", File.ReadAllText(Path.Combine(shared, "original.txt")));
        Assert.Equal("Keep these real bytes.", File.ReadAllText(Path.Combine(shared, "recovered.txt")));
    }
    [Fact]
    public async Task Revocation_hides_remote_records_but_owner_can_restore()
    {
        var deletion = await DeleteRequest(); await Request(deletion); grants.RevokePeer(peer);
        Assert.Empty((await List()).DeletedItems); Assert.Equal(FileStatus.AccessDenied, (await Restore(deletion.MutationId)).Status);
        Assert.Equal(MutationPhase.ChangeSucceeded, grants.Deleted.RestoreLocal(deletion.MutationId).Phase);
        Assert.Equal("Keep these real bytes.", File.ReadAllText(Path.Combine(shared, "original.txt")));
    }
    [Fact]
    public async Task Permission_root_version_and_operation_ownership_are_enforced()
    {
        var request = await DeleteRequest(); grants.Update(grant.Id, StoragePermission.ReadOnly, [], null, true);
        Assert.Equal(FileStatus.AccessDenied, (await Request(request)).Status);
        grants.Update(grant.Id, StoragePermission.Manage, [], null, true);
        File.AppendAllText(Path.Combine(shared, "original.txt"), " Changed."); Assert.Equal(FileStatus.StaleReference, (await Request(request)).Status);
        request = await DeleteRequest(); var root = Assert.Single((await Request(new() { Operation = FileOperation.ListRoots })).Entries);
        var rootRequest = request.Clone(); rootRequest.NodeId = root.Id; rootRequest.ExpectedVersion = root.Version;
        Assert.Equal(FileStatus.AccessDenied, (await Request(rootRequest)).Status);
        await Request(request); var changed = request.Clone(); changed.ExpectedVersion = new string('0', 64);
        Assert.Equal(FileStatus.Conflict, (await Request(changed)).Status);
        Assert.Equal(FileStatus.Conflict, (await Request(request, new string('b', 64))).Status);
        Assert.Empty((await Request(new() { Operation = FileOperation.ListDeleted, MutationEpoch = request.MutationEpoch }, new string('b', 64))).DeletedItems);
    }
    [Fact]
    public async Task Nested_and_empty_folders_survive_delete_and_restore()
    {
        Directory.CreateDirectory(Path.Combine(shared, "folder", "empty"));
        File.WriteAllText(Path.Combine(shared, "folder", "文書.txt"), "Nested Unicode bytes.");
        var deletion = await DeleteRequest("folder"); Assert.Equal(FileStatus.Ok, (await Request(deletion)).Status);
        Restart(); Assert.Equal(FileStatus.Ok, (await Restore(deletion.MutationId)).Status);
        Assert.True(Directory.Exists(Path.Combine(shared, "folder", "empty")));
        Assert.Equal("Nested Unicode bytes.", File.ReadAllText(Path.Combine(shared, "folder", "文書.txt")));
    }
    [Fact]
    public async Task Excluded_descendants_and_changed_parent_or_vault_are_preserved()
    {
        Directory.CreateDirectory(Path.Combine(shared, "folder")); File.WriteAllText(Path.Combine(shared, "folder", "private.txt"), "Excluded.");
        var deletion = await DeleteRequest("folder"); grants.Update(grant.Id, StoragePermission.Manage, ["folder/private.txt"], null, true);
        Assert.Equal(FileStatus.AccessDenied, (await Request(deletion)).Status); Assert.True(Directory.Exists(Path.Combine(shared, "folder")));
        grants.Update(grant.Id, StoragePermission.Manage, [], null, true);
        deletion = await DeleteRequest("folder"); await Request(deletion);
        var vault = Assert.Single(Directory.GetDirectories(shared, ".iconnect-trash-*")); Directory.Move(vault, vault + "-saved"); Directory.CreateDirectory(vault);
        Assert.Equal(FileStatus.StaleReference, (await Restore(deletion.MutationId)).Status);
        Assert.Single(Directory.GetDirectories(vault + "-saved"));
    }
    [Fact]
    public async Task Lost_completion_receipts_are_reconciled_without_repeating_the_effect()
    {
        var deletion = await DeleteRequest(); await Request(deletion); files.Dispose();
        RewriteCatalog(json => { json["Items"]![0]!["State"] = 0; json["Receipts"]![0]!["Phase"] = 0; });
        Restart(); Assert.Equal(DeletedState.DeletedStored, Assert.Single((await List()).DeletedItems).State);
        await Restore(deletion.MutationId); files.Dispose();
        RewriteCatalog(json => { json["Items"]![0]!["State"] = 2; json["Receipts"]![1]!["Phase"] = 0; });
        Restart(); Assert.Equal(DeletedState.DeletedRestored, Assert.Single((await List()).DeletedItems).State);
        Assert.Equal("Keep these real bytes.", File.ReadAllText(Path.Combine(shared, "original.txt")));
    }
    [Fact]
    public async Task Damaged_catalog_blocks_changes_and_archive_never_removes_saved_content()
    {
        var deletion = await DeleteRequest(); await Request(deletion); var vault = Assert.Single(Directory.GetDirectories(shared));
        File.WriteAllText(Path.Combine(state, "deleted-items", "catalog.json"), "broken"); Restart();
        Assert.NotEqual(FileStatus.Ok, (await List()).Status); Assert.Single(grants.Deleted.RecoveryIssues);
        grants.Deleted.ArchiveRecovery(grants.Deleted.RecoveryIssues.Single().Id);
        Assert.Empty((await List()).DeletedItems); Assert.Single(Directory.GetFiles(vault));
        Assert.Equal(FileStatus.StaleReference, (await Request(deletion)).Status);
    }
    [Fact]
    public async Task Changed_original_parent_is_not_recreated_or_followed()
    {
        Directory.CreateDirectory(Path.Combine(shared, "parent")); File.WriteAllText(Path.Combine(shared, "parent", "child.txt"), "Child bytes.");
        var root = Assert.Single((await Request(new() { Operation = FileOperation.ListRoots })).Entries);
        var parent = (await Request(new() { Operation = FileOperation.ListDirectory, NodeId = root.Id })).Entries.Single(x => x.Name == "parent");
        var child = Assert.Single((await Request(new() { Operation = FileOperation.ListDirectory, NodeId = parent.Id })).Entries);
        var context = await Request(new() { Operation = FileOperation.RecoveryContext });
        var deletion = new FileRequest { Operation = FileOperation.Delete, NodeId = child.Id, ExpectedVersion = child.Version,
            MutationId = MutationRules.NewId(), MutationEpoch = context.MutationEpoch };
        Assert.Equal(FileStatus.Ok, (await Request(deletion)).Status);
        Directory.Move(Path.Combine(shared, "parent"), Path.Combine(shared, "old-parent")); Directory.CreateDirectory(Path.Combine(shared, "parent"));
        Assert.Equal(FileStatus.StaleReference, (await Restore(deletion.MutationId)).Status);
        Assert.False(File.Exists(Path.Combine(shared, "parent", "child.txt")));
        Assert.Equal(DeletedState.DeletedStored, Assert.Single((await List()).DeletedItems).State);
    }
    [Fact]
    public async Task Identity_reset_keeps_copies_for_local_owner_without_remote_authority()
    {
        files.Dispose(); using var identity = DeviceIdentity.Ephemeral("Recovery owner");
        using var trust = new TrustedPeerStore(state, identity.Id); Assert.False(trust.OwnerReady); trust.ResetOwnerPolicy();
        grants = new(state, ownerBinding: trust.OwnerBinding); grant = grants.Add(peer, shared, StoragePermission.Manage); files = new(grants);
        var deletion = await DeleteRequest(); await Request(deletion); trust.ResetOwnerPolicy(); grants.ReloadOwnerScope();
        Assert.Empty((await List()).DeletedItems); Assert.Equal(FileStatus.AccessDenied, (await Restore(deletion.MutationId)).Status);
        Assert.Equal(MutationPhase.ChangeSucceeded, grants.Deleted.RestoreLocal(deletion.MutationId).Phase);
        Assert.Equal("Keep these real bytes.", File.ReadAllText(Path.Combine(shared, "original.txt")));
    }
    [Fact]
    public async Task Active_reader_prevents_delete_and_creates_no_receipt_or_saved_copy()
    {
        var deletion = await DeleteRequest();
        using var opened = File.Open(Path.Combine(shared, "original.txt"), FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.Equal(FileStatus.Conflict, (await Request(deletion)).Status);
        Assert.True(File.Exists(Path.Combine(shared, "original.txt"))); Assert.Empty((await List()).DeletedItems); Assert.Empty(Directory.GetDirectories(shared));
    }
    [Fact]
    public async Task Interrupted_intent_is_never_reissued_automatically()
    {
        var deletion = await DeleteRequest(); await Request(deletion); files.Dispose();
        var vault = Assert.Single(Directory.GetDirectories(shared)); var slot = Assert.Single(Directory.GetFiles(vault));
        File.Move(slot, Path.Combine(shared, "original.txt")); // Simulate an intent whose filesystem effect did not happen.
        RewriteCatalog(json => { json["Items"]![0]!["State"] = 0; json["Receipts"]![0]!["Phase"] = 0; });
        Restart(); var receipt = await Request(deletion); Assert.Equal(MutationPhase.ChangeReview, receipt.Mutation.Phase);
        Assert.True(File.Exists(Path.Combine(shared, "original.txt"))); Assert.Equal(DeletedState.DeletedReview, Assert.Single((await List()).DeletedItems).State);
    }
    [Fact]
    public async Task Source_write_failure_and_changed_exclusions_keep_saved_content()
    {
        var deletion = await DeleteRequest(); var blocker = Path.Combine(state, "deleted-items", "catalog.json.new"); Directory.CreateDirectory(blocker);
        Assert.NotEqual(FileStatus.Ok, (await Request(deletion)).Status); Assert.True(File.Exists(Path.Combine(shared, "original.txt")));
        Directory.Delete(blocker); deletion = await DeleteRequest(); Assert.Equal(FileStatus.Ok, (await Request(deletion)).Status);
        grants.Update(grant.Id, StoragePermission.Manage, ["original.txt"], null, true);
        Assert.Empty((await List()).DeletedItems); Assert.Equal(FileStatus.AccessDenied, (await Restore(deletion.MutationId)).Status);
        Assert.Equal("Keep these real bytes.", File.ReadAllText(Directory.GetFiles(shared, "*", SearchOption.AllDirectories).Single()));
    }
    [Theory]
    [InlineData("null")]
    [InlineData("{\"Schema\":1}")]
    public async Task Structurally_damaged_catalogs_fail_closed_without_crashing_the_host(string value)
    {
        _ = await Request(new() { Operation = FileOperation.RecoveryContext }); File.WriteAllText(Path.Combine(state, "deleted-items", "catalog.json"), value);
        Assert.NotEqual(FileStatus.Ok, (await List()).Status); Assert.Single(grants.Deleted.RecoveryIssues); Assert.True(File.Exists(Path.Combine(shared, "original.txt")));
    }
    private void RewriteCatalog(Action<JsonNode> change)
    {
        var path = Path.Combine(state, "deleted-items", "catalog.json"); var json = JsonNode.Parse(File.ReadAllText(path))!; change(json); File.WriteAllText(path, json.ToJsonString());
    }
    public void Dispose() { files.Dispose(); Directory.Delete(directory, true); } // Only this fixture's newly allocated .local subtree.
}
