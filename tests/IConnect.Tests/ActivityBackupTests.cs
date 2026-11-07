using System.Runtime.InteropServices;
using IConnect.AppModel;
using IConnect.Core;
using Xunit;

namespace IConnect.Tests;

public sealed partial class DesktopModelTests
{
    private (LocalActivityStore Store, string Archive) ActivityBackupFixture()
    {
        Directory.CreateDirectory(state);
        File.WriteAllText(Path.Combine(state, "activity.json"), "generated damaged activity");
        File.WriteAllText(Path.Combine(state, "activity.json.new"), "generated uncommitted activity");
        var store = new LocalActivityStore(state); Assert.True(store.CanReset); store.PreserveAndReset();
        return (store, Assert.Single(Directory.GetDirectories(Path.Combine(state, "recovery"))));
    }

    [Fact]
    public void Activity_backup_review_removes_only_selected_metadata_and_preserves_current_and_other_stores()
    {
        var (store, archive) = ActivityBackupFixture(); store.Add("Files", "Generated live activity.");
        var active = File.ReadAllBytes(Path.Combine(state, "activity.json"));
        var other = Directory.CreateDirectory(Path.Combine(state, "recovery", ProtocolRules.NewToken())).FullName;
        File.WriteAllText(Path.Combine(other, "journal.json"), "generated transfer metadata");
        var mixed = Directory.CreateDirectory(Path.Combine(state, "recovery", ProtocolRules.NewToken())).FullName;
        File.WriteAllText(Path.Combine(mixed, "activity.json"), "generated preserved activity");
        File.WriteAllText(Path.Combine(mixed, "keep.txt"), "generated owner content");
        using var manager = new LocalActivityBackups(store, () => "owner-a", () => true);
        var inventory = manager.Inspect(0); Assert.Equal(1, inventory.Total); Assert.Equal(1, inventory.KeptFolders);
        var backup = Assert.Single(inventory.Entries); Assert.Equal(Path.GetFileName(archive), backup.Id);
        var review = manager.Review(backup.Id); Assert.Equal(2, review.Files);
        Assert.ThrowsAny<IOException>(() => manager.Confirm(review with { Bytes = review.Bytes + 1 }));
        Assert.ThrowsAny<IOException>(() => manager.Confirm(review)); Assert.True(Directory.Exists(archive));
        review = manager.Review(backup.Id); manager.CancelReview(); Assert.ThrowsAny<IOException>(() => manager.Confirm(review));
        manager.Confirm(manager.Review(backup.Id)); Assert.False(Directory.Exists(archive));
        Assert.Equal(active, File.ReadAllBytes(Path.Combine(state, "activity.json")));
        Assert.Equal("generated transfer metadata", File.ReadAllText(Path.Combine(other, "journal.json")));
        Assert.Equal("generated owner content", File.ReadAllText(Path.Combine(mixed, "keep.txt")));
        Assert.Equal("generated preserved activity", File.ReadAllText(Path.Combine(mixed, "activity.json")));
        Assert.Throws<ArgumentException>(() => manager.Review("activity.json"));
    }

    [Fact]
    public void Activity_backup_hash_detects_changed_bytes_even_when_size_and_write_time_are_restored()
    {
        var (store, archive) = ActivityBackupFixture();
        using var manager = new LocalActivityBackups(store, () => "owner-a", () => true);
        var review = manager.Review(Path.GetFileName(archive));
        var path = Path.Combine(archive, "activity.json"); var written = File.GetLastWriteTimeUtc(path); var bytes = File.ReadAllBytes(path);
        bytes[0] ^= 1; File.WriteAllBytes(path, bytes); File.SetLastWriteTimeUtc(path, written);
        Assert.ThrowsAny<IOException>(() => manager.Confirm(review));
        Assert.Equal(bytes, File.ReadAllBytes(path)); Assert.True(File.Exists(Path.Combine(archive, "activity.json.new")));
        manager.Confirm(manager.Review(Path.GetFileName(archive))); Assert.False(Directory.Exists(archive));
    }

    [Fact]
    public void Activity_backup_reviews_expire_and_end_on_owner_screen_restart_and_disposal_changes()
    {
        var (store, archive) = ActivityBackupFixture(); var owner = "owner-a"; long now = 1000; var open = true;
        using var manager = new LocalActivityBackups(store, () => owner, () => true, () => now);
        var id = Path.GetFileName(archive); var review = manager.Review(id); now += 120_001;
        Assert.ThrowsAny<IOException>(() => manager.Confirm(review));
        review = manager.Review(id); owner = "owner-b"; Assert.ThrowsAny<IOException>(() => manager.Confirm(review)); owner = "owner-a";
        review = manager.Review(id, () => open); open = false; Assert.Throws<OperationCanceledException>(() => manager.Confirm(review, () => open)); open = true;
        Assert.ThrowsAny<IOException>(() => manager.Confirm(review));
        review = manager.Review(id);
        using (var reopened = new LocalActivityBackups(store, () => owner, () => true)) Assert.ThrowsAny<IOException>(() => reopened.Confirm(review));
        manager.Dispose(); Assert.Throws<ObjectDisposedException>(() => manager.Inspect(0)); Assert.True(Directory.Exists(archive));
    }

    [Fact]
    public void Activity_backup_location_replacement_cannot_adopt_matching_names_and_bytes()
    {
        var (store, archive) = ActivityBackupFixture();
        using var manager = new LocalActivityBackups(store, () => "owner-a", () => true);
        var review = manager.Review(Path.GetFileName(archive));
        var shelf = Path.Combine(state, "recovery"); var moved = Path.Combine(state, "recovery-held");
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(state)) + Path.DirectorySeparatorChar;
        Assert.StartsWith(prefix, Path.GetFullPath(shelf), StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(prefix, Path.GetFullPath(moved), StringComparison.OrdinalIgnoreCase);
        Directory.Move(shelf, moved);
        Directory.CreateDirectory(archive);
        foreach (var file in Directory.GetFiles(Path.Combine(moved, review.Backup))) File.Copy(file, Path.Combine(archive, Path.GetFileName(file)));
        Assert.ThrowsAny<IOException>(() => manager.Confirm(review));
        Assert.Equal(2, Directory.GetFiles(archive).Length); Assert.Equal(2, Directory.GetFiles(Path.Combine(moved, review.Backup)).Length);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ActivityBackupHardLink(string link, string target, IntPtr attributes);

    [Fact]
    public void Activity_backup_removal_preserves_added_streams_and_linked_owner_files()
    {
        var (store, archive) = ActivityBackupFixture();
        using var manager = new LocalActivityBackups(store, () => "owner-a", () => true);
        var id = Path.GetFileName(archive); var path = Path.Combine(archive, "activity.json"); var review = manager.Review(id);
        File.WriteAllText(path + ":owner-data", "generated unreviewed stream");
        Assert.ThrowsAny<IOException>(() => manager.Confirm(review)); Assert.Equal("generated unreviewed stream", File.ReadAllText(path + ":owner-data"));
        Assert.Empty(manager.Inspect(0).Entries);
        File.Delete(path + ":owner-data");
        File.WriteAllText(archive + ":owner-data", "generated unreviewed directory stream");
        Assert.ThrowsAny<IOException>(() => manager.Review(id)); Assert.Empty(manager.Inspect(0).Entries);
        Assert.Equal("generated unreviewed directory stream", File.ReadAllText(archive + ":owner-data"));
        File.Delete(archive + ":owner-data"); File.Delete(path);
        var keep = Path.Combine(directory, "generated-linked-owner.txt"); File.WriteAllText(keep, "generated owner bytes");
        Assert.True(ActivityBackupHardLink(path, keep, IntPtr.Zero));
        Assert.ThrowsAny<IOException>(() => manager.Review(id)); Assert.Empty(manager.Inspect(0).Entries);
        Assert.Equal("generated owner bytes", File.ReadAllText(keep)); Assert.True(File.Exists(path));
    }

    [Fact]
    public void Activity_backup_screen_withdrawal_during_cleanup_preserves_unremoved_metadata_for_fresh_review()
    {
        var (store, archive) = ActivityBackupFixture();
        using var manager = new LocalActivityBackups(store, () => "owner-a", () => true);
        var first = Path.Combine(archive, "activity.json"); var remaining = Path.Combine(archive, "activity.json.new");
        var review = manager.Review(Path.GetFileName(archive));
        Assert.Throws<OperationCanceledException>(() => manager.Confirm(review, () => File.Exists(first)));
        Assert.False(File.Exists(first)); Assert.Equal("generated uncommitted activity", File.ReadAllText(remaining));
        Assert.True(Directory.Exists(archive)); Assert.True(File.Exists(Path.Combine(state, "activity.json")));
        Assert.ThrowsAny<IOException>(() => manager.Confirm(review));
        using var reopened = new LocalActivityBackups(store, () => "owner-a", () => true);
        reopened.Confirm(reopened.Review(Path.GetFileName(archive))); Assert.False(Directory.Exists(archive));
        Assert.Empty(store.All);
    }

    [Fact]
    public void Activity_backup_cleanup_protects_a_partially_archived_journal_until_reset_finishes()
    {
        Directory.CreateDirectory(state); File.WriteAllText(Path.Combine(state, "activity.json"), "generated damaged activity");
        var stage = Path.Combine(state, "activity.json.new"); File.WriteAllText(stage, "generated uncommitted activity");
        var store = new LocalActivityStore(state);
        using var manager = new LocalActivityBackups(store, () => "owner-a", () => true);
        string archive;
        using (var blocker = new FileStream(stage, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<IOException>(store.PreserveAndReset);
            archive = Assert.Single(Directory.GetDirectories(Path.Combine(state, "recovery")));
            Assert.True(File.Exists(Path.Combine(archive, "activity.json")));
            var inventory = manager.Inspect(0); Assert.Empty(inventory.Entries); Assert.True(inventory.KeptFolders > 0);
            Assert.ThrowsAny<IOException>(() => manager.Review(Path.GetFileName(archive)));
        }
        store.PreserveAndReset(); manager.Confirm(manager.Review(Path.GetFileName(archive)));
        Assert.False(Directory.Exists(archive)); Assert.Empty(store.All); Assert.Equal("", store.Issue);
    }

    [Fact]
    public async Task Activity_backup_controller_pages_inventory_and_invalidates_review_on_owner_reset()
    {
        var (store, _) = ActivityBackupFixture();
        for (var index = 0; index < 50; index++)
        {
            var target = Directory.CreateDirectory(Path.Combine(state, "recovery", ProtocolRules.NewToken())).FullName;
            File.WriteAllText(Path.Combine(target, "activity.json"), "generated earlier activity");
        }
        await using var model = new DesktopController(state, action => action());
        var first = await model.InspectActivityBackups(); Assert.Equal(51, first.Total); Assert.Equal(50, first.Entries.Count);
        var last = Assert.Single((await model.InspectActivityBackups(50)).Entries);
        var review = await model.ReviewActivityBackupRemoval(last.Id);
        await model.ResetOwnerPolicyAsync(); await Assert.ThrowsAsync<IOException>(() => model.ConfirmActivityBackupRemoval(review));
        Assert.True(Directory.Exists(Path.Combine(state, "recovery", last.Id)));
        await model.ConfirmActivityBackupRemoval(await model.ReviewActivityBackupRemoval(last.Id));
        Assert.Equal(50, (await model.InspectActivityBackups()).Total); Assert.Empty(model.Peers); Assert.Empty(model.Grants); Assert.False(model.Connected);
    }
}
