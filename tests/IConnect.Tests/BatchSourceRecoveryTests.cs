using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using Xunit;

namespace IConnect.Tests;

public sealed partial class BatchSenderTests
{
    private static string Copies(Fixture f) => Path.Combine(f.Profile, "outgoing-batch-copies");
    private static async Task DamageSource(Fixture f, string source, bool missing = false)
    {
        await f.ReopenPrivateSources(() =>
        {
            var path = Path.Combine(Copies(f), source + ".json");
            if (missing) File.Delete(path); else File.WriteAllText(path, "generated unreadable snapshot ownership");
        });
        Assert.NotEmpty(f.Sources.RecoveryIssues); Assert.False(f.Resources.InventoryReady);
    }
    private static async Task<string> PreserveSource(Fixture f)
    {
        await f.Sender.WhenIdleAsync(); await f.Sources.WhenIdleAsync();
        return f.Sender.ConfirmSourceStoreReset(f.Sender.ReviewSourceStoreReset());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Source_recovery_preserves_actual_payload_receipts_originals_and_received_files_without_adopting_missing_ownership(bool missing)
    {
        await using var f = new Fixture(); var id = await f.Queue(); var source = Assert.Single(f.Sources.All);
        await f.Sender.SendAsync(id, await f.Target(), f.Request); var header = Assert.Single(f.Sender.All).Header!.ToArray();
        await DamageSource(f, source.Id, missing); var calls = f.Calls; var review = f.Sender.ReviewSourceStoreReset();
        var archiveId = f.Sender.ConfirmSourceStoreReset(review); var archive = f.Sources.RecoveryArchiveLocation(archiveId);
        Assert.Empty(f.Sources.All); Assert.Empty(f.Sources.RecoveryIssues); Assert.True(f.Resources.InventoryReady);
        Assert.Equal(calls, f.Calls); Assert.Equal(header, Assert.Single(f.Sender.All).Header);
        var inventory = Directory.EnumerateFiles(archive, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
        Assert.Equal(inventory, f.Resources.ReservedBytes); Assert.Equal(inventory, f.Resources.ReservedForPeer(new string('d', 64)));
        Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(archive, source.Id, "2.bin")));
        Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Destination, "original", "file.bin"))); Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Original, "file.bin")));
        if (missing) Assert.False(File.Exists(Path.Combine(archive, source.Id + ".json")));
        else Assert.Equal("generated unreadable snapshot ownership", File.ReadAllText(Path.Combine(archive, source.Id + ".json")));
        Assert.Throws<IOException>(() => f.Sender.ConfirmSourceStoreReset(review));
        await f.ReopenPrivateSources(); Assert.Equal(inventory, f.Resources.ReservedBytes); Assert.Single(f.Sources.RecoveryArchives); Assert.Empty(f.Sources.All);
        Assert.True(f.Sources.ConfirmArchiveRemoval(f.Sources.ReviewArchiveRemoval(archiveId))); Assert.Equal(0, f.Resources.ReservedBytes);
        await f.ReopenPrivateSources(); var fresh = await f.Sources.PrepareAsync([f.Original], f.Authority, UploadCollision.UploadKeepBoth);
        Assert.True(Assert.Single(f.Sources.All).MayHaveReceiverCopies); Assert.Throws<IOException>(() => f.Sender.Queue(fresh)); Assert.NotEmpty(f.Sender.Queue(fresh, anotherCopy: true));
    }
    [Fact]
    public async Task Source_recovery_keeps_the_original_receiver_stop_controls_and_does_not_restore_archived_sources()
    {
        await using var f = new Fixture(); var id = await f.Queue(); var source = Assert.Single(f.Sources.All); var lost = false;
        async Task<FileResponse> Lose(FileRequest request, CancellationToken ct)
        { var reply = await f.Request(request, ct); if (!lost && request.Operation == FileOperation.WriteBatchFile) { lost = true; throw new IOException("Generated lost reply"); } return reply; }
        await f.Sender.SendAsync(id, await f.Target(), Lose); Assert.True(lost); var header = Assert.Single(f.Sender.All).Header!.ToArray();
        await DamageSource(f, source.Id); await PreserveSource(f);
        await f.Sender.StopAsync(id, f.Request); Assert.Equal(BatchSendingPhase.Stopped, Assert.Single(f.Sender.All).Phase);
        Assert.Equal(header, Assert.Single(f.Sender.All).Header); Assert.Empty(f.Sources.All); Assert.Single(f.Sources.RecoveryArchives); Assert.Equal(1, f.Begins);
    }
    [Fact]
    public async Task Source_reset_review_refuses_healthy_live_stale_forged_and_changed_owner_states()
    {
        await using var f = new Fixture(); var id = await f.Queue(); var source = Assert.Single(f.Sources.All);
        Assert.Throws<IOException>(() => f.Sender.ReviewSourceStoreReset());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<FileResponse> Hold(FileRequest request, CancellationToken ct)
        { if (request.Operation == FileOperation.WriteBatchFile) { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); } return await f.Request(request, ct); }
        var active = f.Sender.SendAsync(id, await f.Target(), Hold); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        File.AppendAllText(Path.Combine(Copies(f), source.Id + ".json"), " changed"); Assert.Throws<IOException>(() => f.Sources.ReadManifest(source.Id));
        Assert.Throws<IOException>(() => f.Sender.ReviewSourceStoreReset()); f.Sender.PauseAll(); await active; await f.Sender.WhenIdleAsync();
        var review = f.Sender.ReviewSourceStoreReset(); Assert.Throws<IOException>(() => f.Sender.ConfirmSourceStoreReset(review with { Bytes = review.Bytes + 1 }));
        review = f.Sender.ReviewSourceStoreReset(); File.AppendAllText(Path.Combine(Copies(f), source.Id + ".json"), " changed again");
        Assert.Throws<IOException>(() => f.Sender.ConfirmSourceStoreReset(review)); Assert.Empty(f.Sources.RecoveryArchives);
        review = f.Sender.ReviewSourceStoreReset(); f.RecoveryOwnerOverride = new string('c', 64) + "." + ProtocolRules.NewToken();
        Assert.Throws<IOException>(() => f.Sender.ConfirmSourceStoreReset(review)); Assert.Empty(f.Sources.RecoveryArchives);
    }
    [Fact]
    public async Task Source_archive_removal_is_bounded_and_cold_reopen_requires_fresh_review_and_keeps_remaining_bytes_reserved()
    {
        await using var f = new Fixture(extras: 40); await f.Queue(); var source = Assert.Single(f.Sources.All); await DamageSource(f, source.Id);
        var id = await PreserveSource(f); var path = f.Sources.RecoveryArchiveLocation(id); var review = f.Sources.ReviewArchiveRemoval(id);
        Assert.True(review.Files > 64); Assert.Equal(50, f.Sources.InspectRecoveryArchive(id, 0).Count);
        Assert.NotEmpty(f.Sources.InspectRecoveryArchive(id, 50)); Assert.False(f.Sources.ConfirmArchiveRemoval(review));
        Assert.Equal(64, Assert.Single(f.Sources.RecoveryArchives).Removed);
        var remaining = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length); Assert.Equal(remaining, f.Resources.ReservedBytes);
        await f.ReopenPrivateSources(); Assert.False(Assert.Single(f.Sources.RecoveryArchives).Removing); Assert.Equal(remaining, f.Resources.ReservedBytes);
        Assert.Throws<IOException>(() => f.Sources.ContinueArchiveRemoval(id)); var done = f.Sources.ConfirmArchiveRemoval(f.Sources.ReviewArchiveRemoval(id));
        while (!done) done = f.Sources.ContinueArchiveRemoval(id); Assert.Equal(0, f.Resources.ReservedBytes); Assert.Empty(f.Sources.RecoveryArchives);
        Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Original, "file.bin")));
    }
    [Fact]
    public async Task New_and_changed_preserved_entries_stay_for_fresh_review_and_current_private_snapshots_stay_outside_removal()
    {
        await using var f = new Fixture(extras: 40); await f.Queue(); var source = Assert.Single(f.Sources.All); await DamageSource(f, source.Id);
        var id = await PreserveSource(f); var path = f.Sources.RecoveryArchiveLocation(id);
        var current = await f.Sources.PrepareAsync([f.Original], f.Authority, UploadCollision.UploadKeepBoth);
        Assert.False(f.Sources.ConfirmArchiveRemoval(f.Sources.ReviewArchiveRemoval(id)));
        var remaining = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).First(); File.AppendAllText(remaining, " generated owner change");
        Assert.Throws<IOException>(() => f.Sources.ContinueArchiveRemoval(id)); Assert.True(File.Exists(remaining));
        var review = f.Sources.ReviewArchiveRemoval(id); File.WriteAllText(Path.Combine(path, "owner-added.txt"), "keep");
        Assert.Throws<IOException>(() => f.Sources.ConfirmArchiveRemoval(review)); Assert.Equal("keep", File.ReadAllText(Path.Combine(path, "owner-added.txt")));
        var done = f.Sources.ConfirmArchiveRemoval(f.Sources.ReviewArchiveRemoval(id)); while (!done) done = f.Sources.ContinueArchiveRemoval(id);
        Assert.Equal(current, Assert.Single(f.Sources.All).Id); Assert.NotEmpty(f.Sources.ReadManifest(current).Items); Assert.True(f.Resources.ReservedBytes > 0);
    }
    [Fact]
    public async Task Peer_revocation_allows_local_source_recovery_but_owner_generation_change_ends_archive_deletion()
    {
        await using var f = new Fixture(extras: 40); await f.Queue(); var source = Assert.Single(f.Sources.All); await DamageSource(f, source.Id); f.Allowed = false;
        var id = await PreserveSource(f); Assert.False(f.Sources.ConfirmArchiveRemoval(f.Sources.ReviewArchiveRemoval(id)));
        f.RecoveryOwnerOverride = f.Authority.Owner + "." + ProtocolRules.NewToken();
        Assert.Throws<IOException>(() => f.Sources.ContinueArchiveRemoval(id)); Assert.False(Assert.Single(f.Sources.RecoveryArchives).Removing);
        var done = f.Sources.ConfirmArchiveRemoval(f.Sources.ReviewArchiveRemoval(id)); while (!done) done = f.Sources.ContinueArchiveRemoval(id); Assert.Empty(f.Sources.RecoveryArchives);
    }
    [Fact]
    public async Task Source_recovery_preserves_unknown_regular_files_but_refuses_nested_redirected_or_hard_linked_entries()
    {
        await using var f = new Fixture(); await f.Queue(); var source = Assert.Single(f.Sources.All); await DamageSource(f, source.Id);
        File.WriteAllText(Path.Combine(Copies(f), "owner-added.txt"), "keep me");
        File.WriteAllText(Path.Combine(Copies(f), source.Id, "owner.lock"), "generated owner-added child");
        var review = f.Sender.ReviewSourceStoreReset(); Assert.Equal(2, review.UnrecognizedEntries);
        Assert.Equal(Directory.EnumerateFiles(Copies(f), "*", SearchOption.AllDirectories).Count() - 1, review.Files);
        var id = f.Sender.ConfirmSourceStoreReset(review); Assert.Equal("keep me", File.ReadAllText(Path.Combine(f.Sources.RecoveryArchiveLocation(id), "owner-added.txt")));
        Assert.Equal("generated owner-added child", File.ReadAllText(Path.Combine(f.Sources.RecoveryArchiveLocation(id), source.Id, "owner.lock")));
        var current = await f.Sources.PrepareAsync([f.Original], f.Authority, UploadCollision.UploadKeepBoth);
        await DamageSource(f, current); var nested = Path.Combine(Copies(f), current, "nested"); Directory.CreateDirectory(nested);
        Assert.Throws<IOException>(() => f.Sender.ReviewSourceStoreReset()); Directory.Delete(nested);
        var outside = Path.Combine(f.Root, "generated-owner-file"); File.WriteAllText(outside, "outside"); var link = Path.Combine(Copies(f), "owner-link.txt");
        Assert.True(RecoveryHardLink(link, outside, IntPtr.Zero)); Assert.ThrowsAny<IOException>(() => f.Sender.ReviewSourceStoreReset()); Assert.Equal("outside", File.ReadAllText(outside));
    }
    [Fact]
    public async Task Restart_after_whole_source_preservation_before_new_directory_creation_reloads_bytes_and_copy_decisions()
    {
        await using var f = new Fixture(); await f.Queue(); var source = Assert.Single(f.Sources.All); await DamageSource(f, source.Id); var id = await PreserveSource(f);
        var reserved = f.Resources.ReservedBytes;
        await f.ReopenPrivateSources(() => { File.Delete(Path.Combine(Copies(f), "owner.lock")); Directory.Delete(Copies(f)); });
        Assert.Empty(f.Sources.All); Assert.Equal(id, Assert.Single(f.Sources.RecoveryArchives).Id); Assert.Equal(reserved, f.Resources.ReservedBytes); Assert.True(f.Resources.InventoryReady);
        Assert.ThrowsAny<IOException>(() => new WindowsBatchSources(f.Profile, f.Current));
        var next = await f.Sources.PrepareAsync([f.Original], f.Authority, UploadCollision.UploadKeepBoth); Assert.True(Assert.Single(f.Sources.All).MayHaveReceiverCopies);
        Assert.Throws<IOException>(() => f.Sender.Queue(next));
    }
    [Fact]
    public async Task Open_verified_readers_prevent_source_preservation_until_their_handles_are_closed()
    {
        await using var f = new Fixture(); await f.Queue(); var source = Assert.Single(f.Sources.All);
        using var reader = f.Sources.OpenFile(source.Id, 2);
        File.AppendAllText(Path.Combine(Copies(f), source.Id + ".json"), " generated metadata change");
        Assert.Throws<IOException>(() => f.Sources.ReadManifest(source.Id)); Assert.Throws<IOException>(() => f.Sender.ReviewSourceStoreReset());
        reader.Dispose(); var id = await PreserveSource(f); Assert.Single(f.Sources.RecoveryArchives);
        Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Sources.RecoveryArchiveLocation(id), source.Id, "2.bin")));
    }
    [Fact]
    public async Task Source_archive_admission_is_bounded_and_explicit_removal_releases_a_preservation_slot()
    {
        await using var f = new Fixture();
        for (var index = 0; index < 8; index++)
        {
            var source = await f.Sources.PrepareAsync([f.Original], f.Authority, UploadCollision.UploadKeepBoth);
            await DamageSource(f, source); await PreserveSource(f);
        }
        var last = await f.Sources.PrepareAsync([f.Original], f.Authority, UploadCollision.UploadKeepBoth); await DamageSource(f, last);
        Assert.Equal(8, f.Sources.RecoveryArchives.Count); Assert.Throws<IOException>(() => f.Sender.ReviewSourceStoreReset());
        var old = f.Sources.RecoveryArchives[0].Id; Assert.True(f.Sources.ConfirmArchiveRemoval(f.Sources.ReviewArchiveRemoval(old)));
        await PreserveSource(f); Assert.Equal(8, f.Sources.RecoveryArchives.Count);
    }
    [Theory]
    [InlineData("manifest")]
    [InlineData("receipt")]
    [InlineData("payload")]
    [InlineData("uncommitted")]
    public async Task Source_recovery_preserves_the_actual_remainder_after_child_damage_or_an_uncommitted_parent(string kind)
    {
        await using var f = new Fixture(); await f.Queue(); var source = Assert.Single(f.Sources.All);
        await f.ReopenPrivateSources(() =>
        {
            var plan = Path.Combine(Copies(f), source.Id);
            switch (kind)
            {
                case "manifest": File.WriteAllText(Path.Combine(plan, "manifest.bin"), "generated damaged manifest"); break;
                case "receipt": File.Delete(Path.Combine(plan, "2.json")); break;
                case "payload": File.Delete(Path.Combine(plan, "2.bin")); break;
                default: File.WriteAllText(Path.Combine(Copies(f), source.Id + ".json.new"), "generated uncommitted parent"); break;
            }
        });
        Assert.NotEmpty(f.Sources.RecoveryIssues); var id = await PreserveSource(f); var archive = f.Sources.RecoveryArchiveLocation(id);
        Assert.Empty(f.Sources.All); Assert.True(f.Resources.InventoryReady); Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Original, "file.bin")));
        if (kind == "manifest") Assert.Equal("generated damaged manifest", File.ReadAllText(Path.Combine(archive, source.Id, "manifest.bin")));
        if (kind == "receipt") Assert.False(File.Exists(Path.Combine(archive, source.Id, "2.json")));
        if (kind == "payload") Assert.False(File.Exists(Path.Combine(archive, source.Id, "2.bin")));
        if (kind == "uncommitted") Assert.Equal("generated uncommitted parent", File.ReadAllText(Path.Combine(archive, source.Id + ".json.new")));
        Assert.Equal(Directory.EnumerateFiles(archive, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length), f.Resources.ReservedBytes);
    }
    [Fact]
    public async Task Restoring_a_valid_older_source_store_alone_keeps_the_independent_recovery_copy_review()
    {
        await using var f = new Fixture(); var job = await f.Queue(); var source = Assert.Single(f.Sources.All);
        Assert.False(source.MayHaveReceiverCopies); var validRecord = File.ReadAllText(Path.Combine(Copies(f), source.Id + ".json"));
        await DamageSource(f, source.Id); var backup = await PreserveSource(f); var saved = f.Sources.RecoveryArchiveLocation(backup);
        await f.ReopenPrivateSources(() =>
        {
            Assert.Equal("owner.lock", Assert.Single(Directory.GetFiles(Copies(f))).Split(Path.DirectorySeparatorChar).Last());
            Assert.Empty(Directory.GetDirectories(Copies(f))); File.Delete(Path.Combine(Copies(f), "owner.lock")); Directory.Delete(Copies(f));
            File.WriteAllText(Path.Combine(saved, source.Id + ".json"), validRecord); Directory.Move(saved, Copies(f));
            File.Delete(Path.Combine(Jobs(f), job + ".json"));
        });
        Assert.Empty(f.Sources.RecoveryIssues); Assert.Empty(f.Sources.RecoveryArchives); Assert.Empty(f.Sender.All);
        Assert.True(Assert.Single(f.Sources.All).MayHaveReceiverCopies); Assert.Throws<IOException>(() => f.Sender.Queue(source.Id));
        Assert.NotEmpty(f.Sender.Queue(source.Id, anotherCopy: true)); Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(Copies(f), source.Id, "2.bin")));
    }
}
