using System.Text.Json.Nodes;
using IConnect.Core;
using IConnect.Connectivity;
using IConnect.Storage.Windows;
using IConnect.AppModel;
using System.Security.Cryptography;
using System.Text;
using IConnect.Protocol;
using Xunit;

namespace IConnect.Tests;

public sealed partial class RecoveryTests
{
    private sealed class ReviewClock : TimeProvider
    {
        private long elapsed;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => elapsed;
        public void Advance(TimeSpan duration) => elapsed += duration.Ticks;
    }
    private sealed class RecoveryReplayClock : TimeProvider
    {
        public TimeSpan Offset { get; set; }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + Offset;
    }
    private RecoveryReplayClock UseReplayClock()
    {
        var clock = new RecoveryReplayClock();
        files.Dispose(); grants = new(state, clock); files = new(grants);
        return clock;
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Empty_vault_cleanup_keeps_settled_receipts_and_refuses_delete_replay(bool purge)
    {
        var deletion = await DeleteRequest();
        Assert.Equal(MutationPhase.ChangeSucceeded, (await Request(deletion)).Mutation.Phase);
        var row = Assert.Single(grants.Deleted.ListLocalRecords());
        Assert.Throws<IOException>(() => grants.Deleted.PrepareEmptyVaultLocal(row.Item.Id));
        if (purge)
        {
            var review = grants.Deleted.PreparePurgeLocal(row.Item.Id);
            Assert.True(grants.Deleted.PurgeLocal(review.Token, "DELETE").Complete);
        }
        else Assert.Equal(FileStatus.Ok, (await Restore(row.Item.Id)).Status);
        var vault = Path.GetDirectoryName(row.SavedLocation)!;
        var unknown = Directory.CreateDirectory(vault + "-unknown").FullName;

        var cleanup = grants.Deleted.PrepareEmptyVaultLocal(row.Item.Id);
        Assert.NotNull(cleanup.Token);
        Assert.EndsWith(Path.GetFileName(vault), cleanup.Location);
        grants.Deleted.RemoveEmptyVaultLocal(cleanup.Token);
        Assert.False(Directory.Exists(vault));
        Assert.True(Directory.Exists(unknown));
        Assert.Throws<IOException>(() => grants.Deleted.RemoveEmptyVaultLocal(cleanup.Token));
        Assert.Throws<IOException>(() => grants.Deleted.DismissRestoredLocal(row.Item.Id));
        Assert.Equal(MutationPhase.ChangeSucceeded, (await Request(deletion)).Mutation.Phase);
        if (purge) Assert.False(File.Exists(row.OriginalLocation));
        else Assert.Equal("Keep these real bytes.", File.ReadAllText(row.OriginalLocation));
        Restart();
        Assert.True(Assert.Single(grants.Deleted.ListLocalRecords()).CanDismiss);
        Assert.Null(grants.Deleted.PrepareEmptyVaultLocal(row.Item.Id).Token);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Empty_vault_cleanup_refuses_unreviewed_entries_and_named_data_streams(bool stream)
    {
        await Request(await DeleteRequest());
        var row = Assert.Single(grants.Deleted.ListLocalRecords());
        Assert.Equal(FileStatus.Ok, (await Restore(row.Item.Id)).Status);
        var review = grants.Deleted.PrepareEmptyVaultLocal(row.Item.Id);
        Assert.NotNull(review.Token);
        var vault = Path.GetDirectoryName(row.SavedLocation)!;
        var contents = stream ? vault + ":fixture" : Path.Combine(vault, "unexpected.txt");
        File.WriteAllText(contents, "Unreviewed bytes");
        Assert.ThrowsAny<IOException>(() => grants.Deleted.RemoveEmptyVaultLocal(review.Token));
        Assert.ThrowsAny<IOException>(() => grants.Deleted.PrepareEmptyVaultLocal(row.Item.Id));
        Assert.Equal("Unreviewed bytes", File.ReadAllText(contents));
        Assert.Equal("Keep these real bytes.", File.ReadAllText(row.OriginalLocation));
    }
    [Fact]
    public async Task Empty_vault_cleanup_refuses_a_same_name_replacement()
    {
        await Request(await DeleteRequest());
        var row = Assert.Single(grants.Deleted.ListLocalRecords());
        Assert.Equal(FileStatus.Ok, (await Restore(row.Item.Id)).Status);
        var review = grants.Deleted.PrepareEmptyVaultLocal(row.Item.Id);
        Assert.NotNull(review.Token);
        var vault = Path.GetDirectoryName(row.SavedLocation)!;
        Directory.Move(vault, vault + "-moved");
        Directory.CreateDirectory(vault);
        Assert.ThrowsAny<IOException>(() => grants.Deleted.RemoveEmptyVaultLocal(review.Token));
        Assert.ThrowsAny<IOException>(() => grants.Deleted.PrepareEmptyVaultLocal(row.Item.Id));
        Assert.True(Directory.Exists(vault));
        Assert.True(Directory.Exists(vault + "-moved"));
    }
    [Fact]
    public async Task Empty_vault_cleanup_requires_a_current_review_and_no_pending_operation()
    {
        var clock = new ReviewClock();
        files.Dispose(); grants = new(state, clock); files = new(grants);
        await Request(await DeleteRequest());
        var row = Assert.Single(grants.Deleted.ListLocalRecords());
        var result = await Restore(row.Item.Id);
        Assert.Equal(FileStatus.Ok, result.Status);
        var review = grants.Deleted.PrepareEmptyVaultLocal(row.Item.Id);
        Assert.NotNull(review.Token);
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Throws<IOException>(() => grants.Deleted.RemoveEmptyVaultLocal(review.Token));
        review = grants.Deleted.PrepareEmptyVaultLocal(row.Item.Id);
        Assert.NotNull(review.Token);
        RewriteCatalog(json => json["Receipts"]!.AsArray().Single(x => (string?)x!["Id"] == result.Mutation.Id)!["Phase"] = (int)MutationPhase.ChangePending);
        Assert.Throws<IOException>(() => grants.Deleted.RemoveEmptyVaultLocal(review.Token));
        Assert.Throws<IOException>(() => grants.Deleted.PrepareEmptyVaultLocal(row.Item.Id));
        Assert.True(Directory.Exists(Path.GetDirectoryName(row.SavedLocation)));
    }
    [Fact]
    public async Task Absent_record_archive_exports_metadata_and_preserves_replacement_after_replay_window()
    {
        var clock = UseReplayClock();
        var deletion = await DeleteRequest();
        Assert.Equal(MutationPhase.ChangeSucceeded, (await Request(deletion)).Mutation.Phase);
        var row = Assert.Single(grants.Deleted.ListLocalRecords());
        var moved = Path.Combine(directory, "owner-moved.txt");
        File.Move(row.SavedLocation, moved);
        File.WriteAllText(row.OriginalLocation, "Replacement contents");
        var output = Directory.CreateDirectory(Path.Combine(directory, "export")).FullName;
        var recent = grants.Deleted.InspectLocal(row.Item.Id);
        Assert.True(recent.IsAbsent);
        Assert.False(recent.CanArchiveAbsent);
        Assert.Throws<IOException>(() => grants.Deleted.ArchiveAbsentLocal(recent.Token, output));
        Assert.Empty(Directory.GetFiles(output));

        clock.Offset = TimeSpan.FromHours(25);
        var review = grants.Deleted.InspectLocal(row.Item.Id);
        Assert.True(review.CanArchiveAbsent);
        var exported = grants.Deleted.ArchiveAbsentLocal(review.Token, output);
        var metadata = JsonNode.Parse(File.ReadAllText(exported))!;
        Assert.Equal(row.Item.Id, (string?)metadata["Item"]!["Id"]);
        Assert.NotNull(metadata["AbsentAtRecordedLocations"]);
        Assert.Single(metadata["Receipts"]!.AsArray());
        Assert.Empty(grants.Deleted.ListLocalRecords());
        Assert.Equal(FileStatus.StaleReference, (await Request(deletion)).Status);
        Assert.Equal("Replacement contents", File.ReadAllText(row.OriginalLocation));
        Assert.Equal("Keep these real bytes.", File.ReadAllText(moved));
        Assert.True(Directory.Exists(Path.GetDirectoryName(row.SavedLocation)));
        Restart();
        Assert.Empty(grants.Deleted.ListLocalRecords());
    }
    [Fact]
    public async Task Absent_record_archive_refuses_reappeared_item_and_existing_export()
    {
        var clock = UseReplayClock();
        await Request(await DeleteRequest());
        var row = Assert.Single(grants.Deleted.ListLocalRecords());
        var moved = Path.Combine(directory, "owner-moved.txt");
        File.Move(row.SavedLocation, moved);
        clock.Offset = TimeSpan.FromHours(25);
        var output = Directory.CreateDirectory(Path.Combine(directory, "export")).FullName;
        var review = grants.Deleted.InspectLocal(row.Item.Id);
        Assert.True(review.CanArchiveAbsent);
        File.Move(moved, row.SavedLocation);
        Assert.Throws<IOException>(() => grants.Deleted.ArchiveAbsentLocal(review.Token, output));
        Assert.Empty(Directory.GetFiles(output));
        Assert.Equal("Keep these real bytes.", File.ReadAllText(row.SavedLocation));

        File.Move(row.SavedLocation, moved);
        Assert.Throws<IOException>(() => grants.Deleted.ArchiveAbsentLocal(review.Token, output));
        var existing = Path.Combine(output, WindowsRecovery.RecoveryExportName(row.Item.Id));
        File.WriteAllText(existing, "An existing export.");
        review = grants.Deleted.InspectLocal(row.Item.Id);
        Assert.True(review.CanArchiveAbsent);
        Assert.ThrowsAny<IOException>(() => grants.Deleted.ArchiveAbsentLocal(review.Token, output));
        Assert.Equal("An existing export.", File.ReadAllText(existing));
        Assert.Single(grants.Deleted.ListLocalRecords());
    }
    [Fact]
    public async Task Absent_record_archive_requires_accessible_vault_and_all_recorded_parents()
    {
        var clock = UseReplayClock();
        await Request(await DeleteRequest());
        var row = Assert.Single(grants.Deleted.ListLocalRecords());
        var destination = Directory.CreateDirectory(Path.Combine(directory, "alternate")).FullName;
        var plan = grants.Deleted.PrepareAlternateRestoreLocal(row.Item.Id, destination, "restored.txt");
        Assert.Equal(MutationPhase.ChangeSucceeded, grants.Deleted.ConfirmAlternateRestoreLocal(plan.Token).Phase);
        var output = Directory.CreateDirectory(Path.Combine(directory, "export")).FullName;
        clock.Offset = TimeSpan.FromHours(25);
        var vault = Path.GetDirectoryName(row.SavedLocation)!;
        Directory.Move(vault, vault + "-moved");
        var review = grants.Deleted.InspectLocal(row.Item.Id);
        Assert.Contains("unavailable", review.SavedStatus);
        Assert.False(review.CanArchiveAbsent);
        Assert.Throws<IOException>(() => grants.Deleted.ArchiveAbsentLocal(review.Token, output));

        Directory.Move(vault + "-moved", vault);
        Directory.Move(destination, destination + "-moved");
        review = grants.Deleted.InspectLocal(row.Item.Id);
        Assert.Contains("unavailable", review.RestoredStatus);
        Assert.False(review.CanArchiveAbsent);
        Assert.Throws<IOException>(() => grants.Deleted.ArchiveAbsentLocal(review.Token, output));
        Assert.Equal("Keep these real bytes.", File.ReadAllText(Path.Combine(destination + "-moved", "restored.txt")));
        Assert.Empty(Directory.GetFiles(output));
        Assert.Single(grants.Deleted.ListLocalRecords());
    }
    [Fact]
    public async Task Absent_record_archive_preserves_expired_but_pending_receipt()
    {
        var clock = UseReplayClock();
        await Request(await DeleteRequest());
        var row = Assert.Single(grants.Deleted.ListLocalRecords());
        File.Move(row.SavedLocation, Path.Combine(directory, "owner-moved.txt"));
        RewriteCatalog(json => json["Receipts"]![0]!["Phase"] = (int)MutationPhase.ChangePending);
        clock.Offset = TimeSpan.FromHours(25);
        var review = grants.Deleted.InspectLocal(row.Item.Id);
        Assert.True(review.IsAbsent);
        Assert.False(review.CanArchiveAbsent);
        var output = Directory.CreateDirectory(Path.Combine(directory, "export")).FullName;
        Assert.Throws<IOException>(() => grants.Deleted.ArchiveAbsentLocal(review.Token, output));
        Assert.Empty(Directory.GetFiles(output));
        Assert.Equal(DeletedState.DeletedReview, Assert.Single(grants.Deleted.ListLocalRecords()).Item.State);
    }
    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Desktop_controller_runs_offline_owner_review_export_and_purge_after_policy_recovery()
    {
        await Request(await DeleteRequest()); var saved = grants.Deleted.ListLocalRecords().Single(); files.Dispose();
        var keyName = "iConnect.Desktop.v1." + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(state.ToUpperInvariant())));
        try
        {
            using (var identity = DeviceIdentity.OpenWindows(keyName, "Offline recovery controller"))
            using (var trust = new TrustedPeerStore(state, identity.Id)) trust.ResetOwnerPolicy(); // This fixture's earlier unbound approval is deliberately retired.
            await using var model = new DesktopController(state, action => action());
            Assert.False(model.Connected); Assert.Empty(model.Grants); await model.RefreshLocalDeletedAsync();
            Assert.True(model.LocalDeletedLoaded); Assert.Equal(saved.OriginalLocation, Assert.Single(model.LocalDeleted).OriginalLocation);
            var inspection = await model.InspectLocalDeletedAsync(saved.Item.Id); Assert.True(inspection.CanConfirm);
            await model.ConfirmLocalDeletedAsync(inspection.Token);
            var output = Path.Combine(directory, "controller-export"); Directory.CreateDirectory(output);
            Assert.True(File.Exists(await model.ExportLocalDeletedAsync(saved.Item.Id, output)));
            var review = await model.PrepareLocalPurgeAsync(saved.Item.Id);
            await model.PurgeLocalDeletedAsync(review.Token, "DELETE");
            Assert.Equal("Permanently deleted", Assert.Single(model.LocalDeleted).State); Assert.False(File.Exists(saved.SavedLocation));
            Assert.Contains(model.Activity, x => x.Description == "A reviewed saved copy was permanently deleted locally."); Assert.False(model.Connected);
        }
        finally { if (CngKey.Exists(keyName)) { using var key = CngKey.Open(keyName); key.Delete(); } }
    }
    [Fact]
    public async Task Review_expiry_eviction_and_owner_reset_require_a_fresh_owner_decision()
    {
        await Request(await DeleteRequest()); var id = grants.Deleted.ListLocalRecords().Single().Item.Id;
        files.Dispose(); var clock = new ReviewClock(); grants = new(state, timeProvider: clock); files = new(grants);
        var review = grants.Deleted.PreparePurgeLocal(id); clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Throws<IOException>(() => grants.Deleted.PurgeLocal(review.Token, "DELETE"));
        review = grants.Deleted.PreparePurgeLocal(id);
        for (var i = 0; i < 8; i++) { clock.Advance(TimeSpan.FromSeconds(1)); _ = grants.Deleted.InspectLocal(id); }
        Assert.Throws<IOException>(() => grants.Deleted.PurgeLocal(review.Token, "DELETE")); Assert.True(File.Exists(grants.Deleted.ListLocalRecords().Single().SavedLocation));

        files.Dispose(); using var owner = DeviceIdentity.Ephemeral("New recovery review owner"); using var trust = new TrustedPeerStore(state, owner.Id);
        trust.ResetOwnerPolicy(); grants = new(state, ownerBinding: trust.OwnerBinding); files = new(grants);
        review = grants.Deleted.PreparePurgeLocal(id); trust.ResetOwnerPolicy(); grants.ReloadOwnerScope();
        Assert.Throws<IOException>(() => grants.Deleted.PurgeLocal(review.Token, "DELETE"));
        Assert.True(File.Exists(grants.Deleted.ListLocalRecords().Single().SavedLocation));
    }
    [Fact]
    public async Task Recovery_export_is_private_metadata_only_and_never_replaces_an_existing_export()
    {
        await Request(await DeleteRequest()); var row = grants.Deleted.ListLocalRecords().Single(); var output = Path.Combine(directory, "owner-export"); Directory.CreateDirectory(output);
        var exported = grants.Deleted.ExportLocalRecord(row.Item.Id, output); var text = File.ReadAllText(exported); var data = JsonNode.Parse(text)!;
        Assert.Equal(row.Item.Id, data["Item"]!["Id"]!.GetValue<string>()); Assert.Contains(row.Item.Name, text); Assert.DoesNotContain("Keep these real bytes.", text);
        File.AppendAllText(exported, "Owner annotation kept.");
        Assert.ThrowsAny<IOException>(() => grants.Deleted.ExportLocalRecord(row.Item.Id, output)); Assert.EndsWith("Owner annotation kept.", File.ReadAllText(exported));
        Assert.True(File.Exists(row.SavedLocation));
    }
    [Fact]
    public async Task Readonly_purge_failure_stays_visible_and_an_owner_can_review_the_remaining_tree_again()
    {
        Directory.CreateDirectory(Path.Combine(shared, "folder")); File.WriteAllText(Path.Combine(shared, "folder", "a.txt"), "First saved bytes.");
        File.WriteAllText(Path.Combine(shared, "folder", "z.txt"), "Readonly saved bytes."); await Request(await DeleteRequest("folder"));
        var row = grants.Deleted.ListLocalRecords().Single(); var locked = Path.Combine(row.SavedLocation, "z.txt"); File.SetAttributes(locked, FileAttributes.ReadOnly);
        try
        {
            var review = grants.Deleted.PreparePurgeLocal(row.Item.Id); var result = grants.Deleted.PurgeLocal(review.Token, "DELETE");
            Assert.False(result.Complete); Assert.Equal(1, result.RemovedEntries); Assert.False(File.Exists(Path.Combine(row.SavedLocation, "a.txt")));
            Assert.True(File.Exists(locked)); Assert.Equal("Readonly saved bytes.", File.ReadAllText(locked));
            Restart(); row = grants.Deleted.ListLocalRecords().Single(); Assert.True(row.CanRestoreRemaining); Assert.False(row.Item.CanRestore);
            Assert.Equal(FileStatus.AccessDenied, (await Restore(row.Item.Id)).Status);
            File.SetAttributes(locked, FileAttributes.Normal); review = grants.Deleted.PreparePurgeLocal(row.Item.Id); Assert.True(review.HasEarlierPurge);
            Assert.True(grants.Deleted.PurgeLocal(review.Token, "DELETE").Complete); Assert.False(Directory.Exists(row.SavedLocation));
        }
        finally { if (File.Exists(locked)) File.SetAttributes(locked, FileAttributes.Normal); }
    }
    [Fact]
    public async Task Hard_links_in_saved_trees_cannot_delete_an_unrelated_file()
    {
        Directory.CreateDirectory(Path.Combine(shared, "folder")); File.WriteAllText(Path.Combine(shared, "folder", "child.txt"), "Saved child.");
        await Request(await DeleteRequest("folder")); var row = grants.Deleted.ListLocalRecords().Single(); var review = grants.Deleted.PreparePurgeLocal(row.Item.Id);
        var outside = Path.Combine(directory, "unrelated.txt"); File.WriteAllText(outside, "Unrelated fixture bytes.");
        Assert.True(CreateRecoveryHardLink(Path.Combine(row.SavedLocation, "alias.txt"), outside, IntPtr.Zero));
        Assert.ThrowsAny<IOException>(() => grants.Deleted.PurgeLocal(review.Token, "DELETE")); Assert.True(File.Exists(Path.Combine(row.SavedLocation, "child.txt")));
        Assert.Equal("Unrelated fixture bytes.", File.ReadAllText(outside));
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateRecoveryHardLink(string name, string existing, IntPtr security);
    [Fact]
    public async Task New_private_profile_inside_a_saved_tree_cannot_be_permanently_deleted()
    {
        Directory.CreateDirectory(Path.Combine(shared, "folder")); File.WriteAllText(Path.Combine(shared, "folder", "child.txt"), "Saved child.");
        await Request(await DeleteRequest("folder")); var row = grants.Deleted.ListLocalRecords().Single();
        var newProfile = Path.Combine(row.SavedLocation, "new-private-profile"); using var owner = DeviceIdentity.Ephemeral("Moved owner profile");
        var binding = new WindowsOwnerBinding(newProfile, owner.Id);
        Directory.CreateDirectory(Path.Combine(newProfile, "deleted-items"));
        File.Copy(Path.Combine(state, "deleted-items", "catalog.json"), Path.Combine(newProfile, "deleted-items", "catalog.json"));
        var current = new StorageGrantStore(newProfile, ownerBinding: binding);
        Assert.ThrowsAny<IOException>(() => current.Deleted.PreparePurgeLocal(row.Item.Id));
        Assert.True(File.Exists(Path.Combine(newProfile, "owner-binding.json"))); Assert.True(File.Exists(Path.Combine(row.SavedLocation, "child.txt")));
    }
    [Fact]
    public async Task Oversized_or_deep_saved_trees_are_refused_without_a_purge_intent()
    {
        Directory.CreateDirectory(Path.Combine(shared, "folder")); await Request(await DeleteRequest("folder")); var row = grants.Deleted.ListLocalRecords().Single();
        var many = Path.Combine(row.SavedLocation, "many"); Directory.CreateDirectory(many);
        for (var i = 0; i < 4096; i++) File.WriteAllText(Path.Combine(many, $"{i:D4}.txt"), "Keep.");
        Assert.ThrowsAny<IOException>(() => grants.Deleted.PreparePurgeLocal(row.Item.Id)); Assert.Equal(4096, Directory.GetFiles(many).Length);
        // Keep those files; a different saved tree tests depth independently.
        Directory.CreateDirectory(Path.Combine(shared, "deep")); await Request(await DeleteRequest("deep"));
        var deep = grants.Deleted.ListLocalRecords().Single(x => x.Item.Name == "deep"); var path = deep.SavedLocation;
        for (var i = 0; i < 33; i++) { path = Path.Combine(path, "nested"); Directory.CreateDirectory(path); }
        Assert.ThrowsAny<IOException>(() => grants.Deleted.PreparePurgeLocal(deep.Item.Id)); Assert.True(Directory.Exists(path));
        Assert.All(grants.Deleted.ListLocalRecords(), x => Assert.Equal(DeletedState.DeletedStored, x.Item.State));
    }
    [Fact]
    public async Task Permanent_delete_requires_review_and_confirmation_and_never_touches_the_original()
    {
        var deletion = await DeleteRequest(); await Request(deletion);
        File.WriteAllText(Path.Combine(shared, "original.txt"), "Keep the replacement original.");
        var row = Assert.Single(grants.Deleted.ListLocalRecords());
        Assert.Equal(Path.Combine(shared, "original.txt"), row.OriginalLocation); Assert.True(File.Exists(row.SavedLocation));
        Assert.DoesNotContain(shared, Assert.Single((await List()).DeletedItems).ToString(), StringComparison.OrdinalIgnoreCase);
        var review = grants.Deleted.PreparePurgeLocal(deletion.MutationId); Assert.Equal(1, review.Files); Assert.Equal(0, review.Folders); Assert.True(review.Bytes > 0);
        Assert.Throws<IOException>(() => grants.Deleted.PurgeLocal(review.Token, "")); Assert.True(File.Exists(row.SavedLocation));
        var result = grants.Deleted.PurgeLocal(review.Token, "DELETE"); Assert.True(result.Complete); Assert.Equal(1, result.RemovedEntries);
        Assert.False(File.Exists(row.SavedLocation)); Assert.Equal("Keep the replacement original.", File.ReadAllText(Path.Combine(shared, "original.txt")));
        Assert.Empty((await List()).DeletedItems); Assert.Throws<IOException>(() => grants.Deleted.PurgeLocal(review.Token, "DELETE"));
        Restart(); row = Assert.Single(grants.Deleted.ListLocalRecords()); Assert.Equal("Permanently deleted", row.State); Assert.True(row.CanDismiss); Assert.False(row.CanPurge);
        Assert.Throws<IOException>(() => grants.Deleted.DismissRestoredLocal(deletion.MutationId));
        Assert.Equal(MutationPhase.ChangeSucceeded, (await Request(deletion)).Mutation.Phase); Assert.True(File.Exists(Path.Combine(shared, "original.txt")));
    }
    [Fact]
    public async Task Permanent_folder_delete_counts_and_removes_only_the_reviewed_saved_tree()
    {
        Directory.CreateDirectory(Path.Combine(shared, "folder", "empty")); Directory.CreateDirectory(Path.Combine(shared, "folder", "nested"));
        File.WriteAllText(Path.Combine(shared, "folder", "one.txt"), "one"); File.WriteAllText(Path.Combine(shared, "folder", "nested", "文書.txt"), "two");
        var deletion = await DeleteRequest("folder"); await Request(deletion);
        var row = Assert.Single(grants.Deleted.ListLocalRecords()); var review = grants.Deleted.PreparePurgeLocal(row.Item.Id);
        Assert.Equal(2, review.Files); Assert.Equal(3, review.Folders); Assert.Equal(6, review.Bytes);
        var result = grants.Deleted.PurgeLocal(review.Token, "DELETE"); Assert.True(result.Complete); Assert.Equal(5, result.RemovedEntries);
        Assert.False(Directory.Exists(row.SavedLocation)); Assert.True(Directory.Exists(Path.GetDirectoryName(row.SavedLocation)));
        Assert.Equal("Keep these real bytes.", File.ReadAllText(Path.Combine(shared, "original.txt")));
    }
    [Fact]
    public async Task Changed_file_or_added_child_invalidates_review_before_any_deletion()
    {
        Directory.CreateDirectory(Path.Combine(shared, "folder")); File.WriteAllText(Path.Combine(shared, "folder", "child.txt"), "Original saved child.");
        await Request(await DeleteRequest("folder")); var row = Assert.Single(grants.Deleted.ListLocalRecords()); var review = grants.Deleted.PreparePurgeLocal(row.Item.Id);
        File.WriteAllText(Path.Combine(row.SavedLocation, "added.txt"), "Never approved for deletion.");
        Assert.Throws<IOException>(() => grants.Deleted.PurgeLocal(review.Token, "DELETE")); Assert.True(File.Exists(Path.Combine(row.SavedLocation, "child.txt")));
        Assert.True(File.Exists(Path.Combine(row.SavedLocation, "added.txt"))); Assert.Throws<IOException>(() => grants.Deleted.PurgeLocal(review.Token, "DELETE"));
        review = grants.Deleted.PreparePurgeLocal(row.Item.Id); File.AppendAllText(Path.Combine(row.SavedLocation, "child.txt"), " Changed.");
        Assert.Throws<IOException>(() => grants.Deleted.PurgeLocal(review.Token, "DELETE")); Assert.Equal(2, Directory.GetFiles(row.SavedLocation).Length);
    }
    [Fact]
    public async Task Changed_vault_replaced_item_and_active_reader_prevent_permanent_deletion()
    {
        await Request(await DeleteRequest()); var row = Assert.Single(grants.Deleted.ListLocalRecords()); var review = grants.Deleted.PreparePurgeLocal(row.Item.Id);
        using (var reader = File.Open(row.SavedLocation, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.ThrowsAny<IOException>(() => grants.Deleted.PurgeLocal(review.Token, "DELETE"));
        Assert.True(File.Exists(row.SavedLocation));
        review = grants.Deleted.PreparePurgeLocal(row.Item.Id); File.Move(row.SavedLocation, row.SavedLocation + "-keep"); File.WriteAllText(row.SavedLocation, "Replacement saved bytes.");
        Assert.ThrowsAny<IOException>(() => grants.Deleted.PurgeLocal(review.Token, "DELETE")); Assert.Equal("Replacement saved bytes.", File.ReadAllText(row.SavedLocation));
        Assert.Equal("Keep these real bytes.", File.ReadAllText(row.SavedLocation + "-keep"));
    }
    [Fact]
    public async Task Failed_intent_write_preserves_every_reviewed_file()
    {
        await Request(await DeleteRequest()); var row = Assert.Single(grants.Deleted.ListLocalRecords()); var review = grants.Deleted.PreparePurgeLocal(row.Item.Id);
        var blocker = Path.Combine(state, "deleted-items", "catalog.json.new"); Directory.CreateDirectory(blocker);
        Assert.True(Record.Exception(() => grants.Deleted.PurgeLocal(review.Token, "DELETE")) is IOException or UnauthorizedAccessException); Directory.Delete(blocker);
        Assert.True(File.Exists(row.SavedLocation)); Assert.Equal(DeletedState.DeletedStored, Assert.Single(grants.Deleted.ListLocal()).State);
    }
    [Fact]
    public async Task Interrupted_permanent_delete_never_resumes_and_partial_restore_is_explicit_and_local()
    {
        Directory.CreateDirectory(Path.Combine(shared, "folder")); File.WriteAllText(Path.Combine(shared, "folder", "first.txt"), "Already removed in simulated interruption.");
        File.WriteAllText(Path.Combine(shared, "folder", "remaining.txt"), "Keep remaining bytes."); await Request(await DeleteRequest("folder"));
        var row = Assert.Single(grants.Deleted.ListLocalRecords());
        File.Delete(Path.Combine(row.SavedLocation, "first.txt")); // Only this fixture's generated saved child; simulate a crash after one deletion.
        files.Dispose(); RewriteCatalog(json =>
        {
            json["Items"]![0]!["State"] = (int)DeletedState.DeletedReview;
            json["Items"]![0]!["Purge"] = new JsonObject { ["Id"] = MutationRules.NewId(), ["Phase"] = 0, ["Fingerprint"] = new string('0', 64), ["Files"] = 2, ["Folders"] = 1, ["Bytes"] = 128, ["RemovedEntries"] = 0, ["EarlierAttempt"] = false };
        });
        Restart(); row = Assert.Single(grants.Deleted.ListLocalRecords()); Assert.True(row.CanRestoreRemaining); Assert.False(row.Item.CanRestore);
        Assert.True(File.Exists(Path.Combine(row.SavedLocation, "remaining.txt"))); Assert.Equal(FileStatus.AccessDenied, (await Restore(row.Item.Id)).Status);
        Assert.Throws<IOException>(() => grants.Deleted.RestoreLocal(row.Item.Id));
        var review = grants.Deleted.PrepareRemainingRestoreLocal(row.Item.Id); Assert.Equal(1, review.Files); Assert.True(review.HasEarlierPurge);
        Assert.Throws<IOException>(() => grants.Deleted.RestoreRemainingLocal(review.Token, "recovered", ""));
        var result = grants.Deleted.RestoreRemainingLocal(review.Token, "recovered", "RESTORE REMAINS"); Assert.Equal(FileStatus.Ok, result.Status);
        Assert.Equal("Keep remaining bytes.", File.ReadAllText(Path.Combine(shared, "recovered", "remaining.txt"))); Assert.False(File.Exists(Path.Combine(shared, "recovered", "first.txt")));
        row = Assert.Single(grants.Deleted.ListLocalRecords()); Assert.Equal("Remaining data restored", row.State); Assert.False(row.CanPurge);
    }
    [Fact]
    public async Task Review_can_confirm_an_unchanged_original_after_uncertain_delete_without_moving_bytes()
    {
        await Request(await DeleteRequest()); var row = Assert.Single(grants.Deleted.ListLocalRecords());
        File.Move(row.SavedLocation, Path.Combine(shared, "original.txt")); files.Dispose();
        RewriteCatalog(json => { json["Items"]![0]!["State"] = (int)DeletedState.DeletedReview; }); Restart();
        var review = grants.Deleted.InspectLocal(row.Item.Id); Assert.True(review.CanConfirm); Assert.Contains("Recorded object found", review.OriginalStatus);
        var confirmed = grants.Deleted.ConfirmLocationsLocal(review.Token); Assert.True(confirmed.CanDismiss);
        Assert.True(File.Exists(Path.Combine(shared, "original.txt"))); Assert.False(File.Exists(row.SavedLocation));
        Assert.Throws<IOException>(() => grants.Deleted.ConfirmLocationsLocal(review.Token));
    }
    [Fact]
    public async Task Location_review_refuses_unavailable_or_changed_locations_and_detects_late_changes()
    {
        await Request(await DeleteRequest()); var row = Assert.Single(grants.Deleted.ListLocalRecords());
        var review = grants.Deleted.InspectLocal(row.Item.Id); Assert.True(review.CanConfirm);
        File.AppendAllText(row.SavedLocation, " Changed after review."); Assert.Throws<IOException>(() => grants.Deleted.ConfirmLocationsLocal(review.Token));
        File.Move(row.SavedLocation, row.SavedLocation + "-keep"); File.WriteAllText(row.SavedLocation, "Other object.");
        review = grants.Deleted.InspectLocal(row.Item.Id); Assert.False(review.CanConfirm); Assert.Throws<IOException>(() => grants.Deleted.ConfirmLocationsLocal(review.Token));
        Assert.Equal("Other object.", File.ReadAllText(row.SavedLocation));
    }
    [Fact]
    public async Task No_permanent_deletion_of_restored_copies_and_reviews_do_not_survive_restart()
    {
        await Request(await DeleteRequest()); var row = Assert.Single(grants.Deleted.ListLocalRecords()); var review = grants.Deleted.PreparePurgeLocal(row.Item.Id);
        Restart(); Assert.Throws<IOException>(() => grants.Deleted.PurgeLocal(review.Token, "DELETE")); Assert.True(File.Exists(row.SavedLocation));
        Assert.Equal(FileStatus.Ok, (await Restore(row.Item.Id)).Status); Assert.Throws<IOException>(() => grants.Deleted.PreparePurgeLocal(row.Item.Id));
        Assert.True(File.Exists(Path.Combine(shared, "original.txt")));
    }
}
