using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using Xunit;

namespace IConnect.Tests;

public sealed partial class BatchSenderTests
{
    private static string Jobs(Fixture f) => Path.Combine(f.Profile, "outgoing-batch-jobs");
    private static async Task Damage(Fixture f, string id)
    {
        await f.Sender.DisposeAsync(); File.WriteAllText(Path.Combine(Jobs(f), id + ".json"), "generated damaged sender intent"); await f.ReopenSender();
        Assert.NotEmpty(f.Sender.RecoveryIssues);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reviewed_sender_recovery_preserves_actual_received_files_snapshots_and_exact_damaged_metadata(bool missingParent)
    {
        await using var f = new Fixture(); var id = await f.Queue(); var source = Assert.Single(f.Sources.All);
        await f.Sender.SendAsync(id, await f.Target(), f.Request); var bytes = File.ReadAllBytes(Path.Combine(f.Destination, "original", "file.bin"));
        if (missingParent)
        { await f.Sender.DisposeAsync(); File.Delete(Path.Combine(Jobs(f), id + ".json")); await f.ReopenSender(); Assert.NotEmpty(f.Sender.RecoveryIssues); }
        else await Damage(f, id);
        var review = f.Sender.ReviewStoreReset(); var calls = f.Calls;
        Assert.True(review.Files >= 4); Assert.Equal(!missingParent, File.Exists(Path.Combine(Jobs(f), id + ".json")));
        var archiveId = f.Sender.ConfirmStoreReset(review); var archive = f.Sender.RecoveryArchiveLocation(archiveId);
        Assert.Empty(f.Sender.RecoveryIssues); Assert.Empty(f.Sender.All); Assert.Equal(calls, f.Calls);
        if (missingParent) Assert.False(File.Exists(Path.Combine(archive, id + ".json")));
        else Assert.Equal("generated damaged sender intent", File.ReadAllText(Path.Combine(archive, id + ".json")));
        Assert.True(File.Exists(Path.Combine(archive, id + ".0.json")));
        Assert.True(Assert.Single(f.Sources.All).MayHaveReceiverCopies); Assert.Throws<IOException>(() => f.Sender.Queue(source.Id));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(f.Destination, "original", "file.bin")));
        Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Original, "file.bin")));
        Assert.Throws<IOException>(() => f.Sender.ConfirmStoreReset(review));
        await f.ReopenSender(); Assert.Empty(f.Sender.RecoveryIssues); Assert.Equal(archiveId, Assert.Single(f.Sender.RecoveryArchives).Id);
        Assert.NotEmpty(f.Sender.Queue(source.Id, anotherCopy: true));
    }
    [Fact]
    public async Task Recovery_marks_an_unadmitted_retained_snapshot_as_uncertain_without_granting_new_permissions()
    {
        await using var f = new Fixture(); var id = await f.Queue(); var source = Assert.Single(f.Sources.All);
        Assert.False(source.MayHaveReceiverCopies); await Damage(f, id);
        f.Sender.ConfirmStoreReset(f.Sender.ReviewStoreReset()); Assert.True(Assert.Single(f.Sources.All).MayHaveReceiverCopies);
        Assert.Equal(source.Authority, Assert.Single(f.Sources.All).Authority); Assert.Equal(source.Hash, Assert.Single(f.Sources.All).Hash);
        Assert.Throws<IOException>(() => f.Sender.Queue(source.Id)); Assert.Equal(0, f.Calls);
    }
    [Fact]
    public async Task Changed_metadata_and_forged_or_stale_reset_reviews_cannot_move_any_store()
    {
        await using var f = new Fixture(); var id = await f.Queue(); await Damage(f, id); var review = f.Sender.ReviewStoreReset();
        Assert.Throws<IOException>(() => f.Sender.ConfirmStoreReset(review with { Files = review.Files + 1 }));
        review = f.Sender.ReviewStoreReset(); File.AppendAllText(Path.Combine(Jobs(f), id + ".json"), " changed");
        Assert.Throws<IOException>(() => f.Sender.ConfirmStoreReset(review)); Assert.Empty(f.Sender.RecoveryArchives);
        Assert.Contains("changed", File.ReadAllText(Path.Combine(Jobs(f), id + ".json")));
        review = f.Sender.ReviewStoreReset(); f.RecoveryOwnerOverride = new string('c', 64) + "." + ProtocolRules.NewToken();
        Assert.Throws<IOException>(() => f.Sender.ConfirmStoreReset(review)); Assert.Empty(f.Sender.RecoveryArchives);
    }
    [Fact]
    public async Task Peer_revocation_does_not_prevent_current_local_owner_recovery()
    {
        await using var f = new Fixture(); var id = await f.Queue(); await Damage(f, id); f.Allowed = false;
        var archive = f.Sender.ConfirmStoreReset(f.Sender.ReviewStoreReset()); Assert.Single(f.Sender.RecoveryArchives);
        Assert.True(f.Sender.ConfirmArchiveRemoval(f.Sender.ReviewArchiveRemoval(archive))); Assert.Empty(f.Sender.RecoveryArchives);
        Assert.Single(f.Sources.All); Assert.Equal(0, f.Calls);
    }
    [Fact]
    public async Task Preserved_unknown_regular_files_require_explicit_archive_removal_and_remain_untouched_when_review_is_canceled()
    {
        await using var f = new Fixture(); var id = await f.Queue(); await f.Sender.DisposeAsync();
        File.WriteAllText(Path.Combine(Jobs(f), "owner-added.txt"), "generated owner data"); await f.ReopenSender();
        var review = f.Sender.ReviewStoreReset(); Assert.Equal(1, review.UnrecognizedFiles);
        var archive = f.Sender.ConfirmStoreReset(review); var path = f.Sender.RecoveryArchiveLocation(archive);
        var removal = f.Sender.ReviewArchiveRemoval(archive); Assert.Equal(1, removal.UnrecognizedFiles);
        Assert.Equal("generated owner data", File.ReadAllText(Path.Combine(path, "owner-added.txt")));
        Assert.True(f.Sender.ConfirmArchiveRemoval(removal)); Assert.False(Directory.Exists(path));
        Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Original, "file.bin"))); Assert.Single(f.Sources.All);
    }
    [Fact]
    public async Task Owner_archive_removal_is_bounded_and_after_restart_requires_fresh_review_of_remaining_files()
    {
        await using var f = new Fixture(extras: 66); var id = await f.Queue(); await f.Sender.SendAsync(id, await f.Target(), f.Request);
        await Damage(f, id); var archive = f.Sender.ConfirmStoreReset(f.Sender.ReviewStoreReset()); var path = f.Sender.RecoveryArchiveLocation(archive);
        var review = f.Sender.ReviewArchiveRemoval(archive); Assert.True(review.Files > 64);
        Assert.False(f.Sender.ConfirmArchiveRemoval(review)); Assert.Equal(review.Files - 64, Directory.GetFiles(path).Length);
        Assert.Equal(64, Assert.Single(f.Sender.RecoveryArchives).Removed);
        await f.ReopenSender(); Assert.False(Assert.Single(f.Sender.RecoveryArchives).Removing);
        Assert.Throws<IOException>(() => f.Sender.ContinueArchiveRemoval(archive));
        var remaining = f.Sender.ReviewArchiveRemoval(archive); Assert.Equal(review.Files - 64, remaining.Files);
        var done = f.Sender.ConfirmArchiveRemoval(remaining); while (!done) done = f.Sender.ContinueArchiveRemoval(archive);
        Assert.Empty(f.Sender.RecoveryArchives); Assert.Single(f.Sources.All);
        Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Destination, "original", "file.bin")));
    }
    [Fact]
    public async Task Changed_or_new_archive_files_are_preserved_until_fresh_owner_review()
    {
        await using var f = new Fixture(extras: 66); var id = await f.Queue(); await f.Sender.SendAsync(id, await f.Target(), f.Request);
        await Damage(f, id); var archive = f.Sender.ConfirmStoreReset(f.Sender.ReviewStoreReset()); var path = f.Sender.RecoveryArchiveLocation(archive);
        var review = f.Sender.ReviewArchiveRemoval(archive); Assert.False(f.Sender.ConfirmArchiveRemoval(review));
        var remaining = Directory.GetFiles(path).First(); File.AppendAllText(remaining, " owner changed remaining metadata");
        Assert.Throws<IOException>(() => f.Sender.ContinueArchiveRemoval(archive)); Assert.True(File.Exists(remaining));
        review = f.Sender.ReviewArchiveRemoval(archive); File.WriteAllText(Path.Combine(path, "new-owner-file.txt"), "keep me");
        Assert.Throws<IOException>(() => f.Sender.ConfirmArchiveRemoval(review));
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(path, "new-owner-file.txt")));
    }
    [Fact]
    public async Task Recovery_refuses_live_workers_and_healthy_stores_and_keeps_the_profile_exclusively_owned()
    {
        await using var f = new Fixture(); var id = await f.Queue(); Assert.Throws<IOException>(() => f.Sender.ReviewStoreReset());
        Assert.ThrowsAny<IOException>(() => new WindowsBatchSender(f.Profile, f.Sources, f.Current));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<FileResponse> Holding(FileRequest value, CancellationToken ct) { if (value.Operation == FileOperation.WriteBatchFile) { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); } return await f.Request(value, ct); }
        var sending = f.Sender.SendAsync(id, await f.Target(), Holding); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        File.AppendAllText(Path.Combine(Jobs(f), id + ".json"), " changed while running");
        Assert.Throws<IOException>(() => f.Sender.Queue(Assert.Single(f.Sources.All).Id)); Assert.NotEmpty(f.Sender.RecoveryIssues);
        Assert.Throws<IOException>(() => f.Sender.ReviewStoreReset()); await f.Sender.PauseAndWaitAsync(id); await sending;
        Assert.NotNull(f.Sender.ReviewStoreReset());
    }
    [Fact]
    public async Task Archive_removal_keeps_a_new_current_job_and_its_private_snapshot()
    {
        await using var f = new Fixture(); var id = await f.Queue(); await Damage(f, id); var archive = f.Sender.ConfirmStoreReset(f.Sender.ReviewStoreReset());
        var source = Assert.Single(f.Sources.All).Id; var current = f.Sender.Queue(source, anotherCopy: true);
        Assert.True(f.Sender.ConfirmArchiveRemoval(f.Sender.ReviewArchiveRemoval(archive)));
        Assert.Equal(current, Assert.Single(f.Sender.All).Id); Assert.Single(f.Sources.All); Assert.Empty(f.Sender.RecoveryArchives);
        await f.ReopenSender(); Assert.Equal(current, Assert.Single(f.Sender.All).Id); Assert.Empty(f.Sender.RecoveryIssues);
    }
    [Fact]
    public async Task Recovery_archive_admission_is_bounded_and_owner_removal_releases_one_slot()
    {
        await using var f = new Fixture(); var id = await f.Queue(); var source = Assert.Single(f.Sources.All).Id;
        for (var index = 0; index < 8; index++)
        {
            await Damage(f, id); f.Sender.ConfirmStoreReset(f.Sender.ReviewStoreReset()); id = f.Sender.Queue(source, anotherCopy: true);
        }
        await Damage(f, id); Assert.Equal(8, f.Sender.RecoveryArchives.Count); Assert.Throws<IOException>(() => f.Sender.ReviewStoreReset());
        var archive = f.Sender.RecoveryArchives[0].Id; Assert.True(f.Sender.ConfirmArchiveRemoval(f.Sender.ReviewArchiveRemoval(archive)));
        Assert.Equal(7, f.Sender.RecoveryArchives.Count); f.Sender.ConfirmStoreReset(f.Sender.ReviewStoreReset()); Assert.Equal(8, f.Sender.RecoveryArchives.Count);
    }
    [Fact]
    public async Task Changed_local_owner_ends_archive_removal_and_requires_fresh_review()
    {
        await using var f = new Fixture(extras: 66); var id = await f.Queue(); await f.Sender.SendAsync(id, await f.Target(), f.Request);
        await Damage(f, id); var archive = f.Sender.ConfirmStoreReset(f.Sender.ReviewStoreReset());
        Assert.False(f.Sender.ConfirmArchiveRemoval(f.Sender.ReviewArchiveRemoval(archive)));
        f.RecoveryOwnerOverride = new string('c', 64) + "." + ProtocolRules.NewToken();
        Assert.Throws<IOException>(() => f.Sender.ContinueArchiveRemoval(archive)); Assert.False(Assert.Single(f.Sender.RecoveryArchives).Removing);
        var done = f.Sender.ConfirmArchiveRemoval(f.Sender.ReviewArchiveRemoval(archive)); while (!done) done = f.Sender.ContinueArchiveRemoval(archive);
        Assert.Empty(f.Sender.RecoveryArchives); Assert.Single(f.Sources.All);
    }
    [Fact]
    public async Task Unexpected_nested_folders_and_file_aliases_are_preserved_without_reset_or_external_deletion()
    {
        await using var f = new Fixture(); var id = await f.Queue(); await f.Sender.DisposeAsync();
        var nested = Path.Combine(Jobs(f), "owner-folder"); Directory.CreateDirectory(nested); File.WriteAllText(Path.Combine(nested, "keep.txt"), "keep");
        await f.ReopenSender(); Assert.Throws<IOException>(() => f.Sender.ReviewStoreReset()); Assert.Equal("keep", File.ReadAllText(Path.Combine(nested, "keep.txt")));
        // Only generated, verified fixture paths are removed before testing the separate alias boundary.
        Assert.StartsWith(Path.GetFullPath(f.Root) + Path.DirectorySeparatorChar, Path.GetFullPath(nested), StringComparison.OrdinalIgnoreCase);
        await f.Sender.DisposeAsync(); File.Delete(Path.Combine(nested, "keep.txt")); Directory.Delete(nested, false);
        Assert.True(RecoveryHardLink(Path.Combine(Jobs(f), "owner-alias.json"), Path.Combine(f.Original, "file.bin"), IntPtr.Zero));
        await f.ReopenSender(); Assert.ThrowsAny<IOException>(() => f.Sender.ReviewStoreReset()); Assert.Empty(f.Sender.RecoveryArchives);
        Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Original, "file.bin")));
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool RecoveryHardLink(string link, string target, IntPtr security);
    [Fact]
    public async Task Restart_after_preservation_before_fresh_queue_creation_keeps_archive_and_copy_review()
    {
        await using var f = new Fixture(); var id = await f.Queue(); var source = Assert.Single(f.Sources.All).Id;
        await Damage(f, id); var archive = f.Sender.ConfirmStoreReset(f.Sender.ReviewStoreReset()); var preserved = f.Sender.RecoveryArchiveLocation(archive);
        await f.Sender.DisposeAsync(); var fresh = Path.GetFullPath(Jobs(f));
        Assert.StartsWith(Path.GetFullPath(f.Root) + Path.DirectorySeparatorChar, fresh, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("owner.lock", Path.GetFileName(Assert.Single(Directory.GetFiles(fresh))));
        File.Delete(Path.Combine(fresh, "owner.lock")); Directory.Delete(fresh, false);
        await f.ReopenSender(); Assert.Empty(f.Sender.RecoveryIssues); Assert.Empty(f.Sender.All);
        Assert.Equal(archive, Assert.Single(f.Sender.RecoveryArchives).Id); Assert.True(Directory.Exists(preserved));
        Assert.Equal("generated damaged sender intent", File.ReadAllText(Path.Combine(preserved, id + ".json")));
        Assert.Throws<IOException>(() => f.Sender.Queue(source)); Assert.Equal(0, f.Calls);
    }
}
