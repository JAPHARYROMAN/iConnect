using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using Xunit;

namespace IConnect.Tests;

public sealed class OutgoingResourcesTests
{
    private static readonly string Peer = new('b', 64);
    private static OutgoingCopyResources.Reservation Row(long bytes, long pending = 0, string? peer = null) => new(ProtocolRules.NewToken(), peer ?? Peer, bytes, pending);
    [Fact]
    public void Preserved_unknown_ownership_charges_every_peer_once_globally_and_survives_participant_close()
    {
        var pool = new OutgoingCopyResources(); using var single = pool.Open("single", Path.GetTempPath()); using var batch = pool.Open("batch", Path.GetTempPath());
        var row = Row(UploadRules.PeerQuota - 10); single.Recover([row], false); batch.Recover([], false, 10);
        Assert.True(pool.InventoryReady); Assert.Equal(UploadRules.PeerQuota, pool.ReservedBytes); Assert.Equal(UploadRules.PeerQuota, pool.ReservedForPeer(Peer));
        Assert.Equal(10, pool.ReservedForPeer(new string('c', 64))); Assert.Equal(0, single.OtherPending());
        Assert.Throws<IOException>(() => single.Replace([row with { Bytes = row.Bytes + 1 }]));
        batch.Dispose(); Assert.Equal(UploadRules.PeerQuota, pool.ReservedBytes); Assert.False(pool.InventoryReady);
        using var reopened = pool.Open("batch", Path.GetTempPath()); reopened.Recover([], false, 10);
        Assert.True(pool.InventoryReady); single.Replace([row with { Bytes = row.Bytes - 1 }]); Assert.Equal(UploadRules.PeerQuota - 1, pool.ReservedBytes);
        reopened.Recover([], false, 0); single.Replace([row with { Bytes = UploadRules.PeerQuota }]); Assert.Equal(UploadRules.PeerQuota, pool.ReservedBytes);
    }
    [Fact]
    public void Preserved_bytes_apply_to_the_profile_limit_even_across_distinct_peer_groups()
    {
        var pool = new OutgoingCopyResources(); using var single = pool.Open("single", Path.GetTempPath()); using var batch = pool.Open("batch", Path.GetTempPath());
        var rows = Enumerable.Range(0, 4).Select(index => Row(UploadRules.PeerQuota - 10, peer: new string((char)('a' + index), 64))).ToArray();
        single.Recover(rows, false); batch.Recover([], false, 10);
        var last = Row(30, peer: new string('e', 64)); batch.Replace([last]); Assert.Equal(UploadRules.PeerQuota * 4, pool.ReservedBytes);
        Assert.Throws<IOException>(() => batch.Replace([last with { Bytes = 31 }])); Assert.Equal(UploadRules.PeerQuota * 4, pool.ReservedBytes);
    }
    [Fact]
    public async Task Competing_stores_admit_pending_disk_bytes_atomically()
    {
        var pool = new OutgoingCopyResources(); using var single = pool.Open("single", Path.GetTempPath()); using var batch = pool.Open("batch", Path.GetTempPath());
        single.Recover([], false); batch.Recover([], false);
        using var start = new ManualResetEventSlim();
        Task<bool> Attempt(OutgoingCopyResources.Participant store) => Task.Run(() => { start.Wait(); try { store.Replace([Row(60, 60)], () => 64L * 1024 * 1024 + 100); return true; } catch (IOException) { return false; } });
        var a = Attempt(single); var b = Attempt(batch); start.Set(); var results = await Task.WhenAll(a, b);
        Assert.Single(results, x => x); Assert.Equal(60, pool.ReservedBytes); Assert.Equal(60, single.OtherPending());
        // Reductions remain possible when the disk subsequently fills up.
        single.Replace([], () => 0); batch.Replace([], () => 0); Assert.Equal(0, pool.ReservedBytes);
    }
    [Fact]
    public void Shared_byte_boundaries_pending_space_and_unknown_inventory_are_atomic()
    {
        var pool = new OutgoingCopyResources(); using var single = pool.Open("single", Path.GetTempPath());
        var first = Row(UploadRules.PeerQuota - 3, 7); single.Recover([first], false);
        Assert.False(pool.InventoryReady); Assert.Throws<IOException>(() => single.Replace([first, Row(0)]));
        using var batch = pool.Open("batch", Path.GetTempPath()); batch.Recover([], false); Assert.True(pool.InventoryReady);
        var last = Row(3, 2); batch.Replace([last]); Assert.Equal(UploadRules.PeerQuota, pool.ReservedForPeer(Peer)); Assert.Equal(7, batch.OtherPending(last.Id));
        Assert.Throws<IOException>(() => batch.Replace([last with { Bytes = 4 }])); Assert.Equal(UploadRules.PeerQuota, pool.ReservedBytes);
        batch.Uncertain(); Assert.False(pool.InventoryReady); single.Replace([first with { Pending = 0 }]);
        Assert.Throws<IOException>(() => single.Replace([first, Row(0)]));
        batch.Recover([last], false); Assert.True(pool.InventoryReady); batch.Dispose(); Assert.False(pool.InventoryReady); Assert.Equal(UploadRules.PeerQuota, pool.ReservedBytes);
        using var reopened = pool.Open("batch", Path.GetTempPath()); reopened.Recover([last], false); Assert.True(pool.InventoryReady);
    }
    [Fact]
    public void Global_quota_is_shared_across_peers_and_disposal_releases_worker_once()
    {
        var pool = new OutgoingCopyResources(); using var single = pool.Open("single", Path.GetTempPath()); using var batch = pool.Open("batch", Path.GetTempPath());
        var rows = Enumerable.Range(0, 4).Select(index => Row(UploadRules.PeerQuota, peer: new string((char)('a' + index), 64))).ToArray();
        single.Recover(rows[..2], false); batch.Recover(rows[2..], false); Assert.Equal(UploadRules.PeerQuota * 4, pool.ReservedBytes);
        Assert.Throws<IOException>(() => batch.Replace([.. rows[2..], Row(1, peer: new string('f', 64))]));
        var a = pool.TryWorker(); var b = pool.TryWorker(); Assert.NotNull(a); Assert.NotNull(b); Assert.Null(pool.TryWorker()); Assert.Equal(2, pool.ActiveWorkers);
        a.Dispose(); a.Dispose(); Assert.Equal(1, pool.ActiveWorkers); using var again = pool.TryWorker(); Assert.NotNull(again); Assert.Null(pool.TryWorker()); b.Dispose();
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string workspace;
        public readonly string Root;
        public string Profile => Path.Combine(Root, "profile");
        public string FilePath => Path.Combine(Root, "one.bin");
        public string Folder => Path.Combine(Root, "folder");
        public readonly UploadAuthority Authority = new(new string('a', 64), ProtocolRules.NewToken(), Peer, ProtocolRules.NewToken());
        public readonly OutgoingCopyResources Pool = new();
        public WindowsUploadSender Single;
        public WindowsBatchSources Batches;
        public Fixture(bool orphan = false)
        {
            var path = new DirectoryInfo(AppContext.BaseDirectory); while (path is not null && !System.IO.File.Exists(Path.Combine(path.FullName, "Directory.Build.props"))) path = path.Parent;
            workspace = path!.FullName; Root = Path.Combine(workspace, ".local", "outgoing-resource-tests", ProtocolRules.NewToken()); Directory.CreateDirectory(Folder);
            System.IO.File.WriteAllBytes(FilePath, [1, 2, 3]); System.IO.File.WriteAllBytes(Path.Combine(Folder, "two.bin"), [4, 5, 6]);
            if (orphan) { var privateFolder = Path.Combine(Profile, "outgoing-uploads"); Directory.CreateDirectory(privateFolder); System.IO.File.WriteAllBytes(Path.Combine(privateFolder, ProtocolRules.NewToken() + ".bin"), [9]); }
            Single = new(Profile, value => value == Authority, resources: Pool); Batches = OpenBatches();
        }
        private WindowsBatchSources OpenBatches() => new(Profile, value => value == Authority, resources: Pool);
        public async Task ReopenBatches() { await Batches.DisposeAsync(); Batches = OpenBatches(); }
        public async ValueTask DisposeAsync()
        {
            await Batches.DisposeAsync(); await Single.DisposeAsync();
            var expected = Path.GetFullPath(Path.Combine(workspace, ".local", "outgoing-resource-tests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Root).StartsWith(expected, StringComparison.OrdinalIgnoreCase) || !UploadBatchRules.CanonicalToken(Path.GetFileName(Root))) throw new IOException("Fixture cleanup escaped its workspace.");
            Directory.Delete(Root, true);
        }
    }
    [Fact]
    public async Task Real_single_sending_and_batch_readers_share_two_slots_and_retained_bytes()
    {
        await using var f = new Fixture(); var single = await f.Single.PrepareAsync(f.FilePath, f.Authority); var batch = await f.Batches.PrepareAsync([f.Folder], f.Authority, UploadCollision.UploadFail);
        Assert.Equal(6, f.Pool.ReservedBytes); Assert.Equal(0, f.Pool.ActiveWorkers);
        using var reader = f.Batches.OpenFile(batch, 1); Assert.Equal(1, f.Pool.ActiveWorkers);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sending = f.Single.SendAsync(single, new FileEntry { IsDirectory = true, Permissions = 12 }, async (value, ct) => {
            Assert.Equal(FileOperation.UploadContext, value.Operation); entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); throw new IOException("Unreachable fixture."); });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(2, f.Pool.ActiveWorkers);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Batches.PrepareAsync([f.Folder], f.Authority, UploadCollision.UploadFail)); Assert.Single(f.Batches.All);
        Assert.Throws<InvalidOperationException>(() => f.Batches.OpenFile(batch, 1));
        f.Single.Pause(single); await sending; Assert.Equal(1, f.Pool.ActiveWorkers); Assert.Equal(SendingPhase.Paused, Assert.Single(f.Single.All).Phase);
        using (var second = f.Batches.OpenFile(batch, 1)) { Assert.Equal(2, f.Pool.ActiveWorkers); Assert.Equal(new byte[] { 4, 5, 6 }, second.ReadRange(0, 3)); }
        reader.Dispose(); Assert.Equal(0, f.Pool.ActiveWorkers); Assert.Equal(6, f.Pool.ReservedBytes);
        await f.Single.RemoveAsync(single); f.Batches.RemoveReviewed(f.Batches.ReviewRemoval(batch)); Assert.Equal(0, f.Pool.ReservedBytes);
    }
    [Fact]
    public async Task Host_shutdown_closes_readers_and_retains_inventory_until_reopened()
    {
        await using var f = new Fixture(); var batch = await f.Batches.PrepareAsync([f.Folder], f.Authority, UploadCollision.UploadFail); var reader = f.Batches.OpenFile(batch, 1);
        await f.Batches.DisposeAsync(); Assert.Equal(0, f.Pool.ActiveWorkers); Assert.Equal(3, f.Pool.ReservedBytes); Assert.False(f.Pool.InventoryReady);
        Assert.Throws<ObjectDisposedException>(() => reader.ReadRange(0, 1)); await Assert.ThrowsAsync<IOException>(() => f.Single.PrepareAsync(f.FilePath, f.Authority)); Assert.Empty(f.Single.All);
        await f.ReopenBatches(); Assert.True(f.Pool.InventoryReady); await f.Single.PrepareAsync(f.FilePath, f.Authority); Assert.Equal(6, f.Pool.ReservedBytes); reader.Dispose(); Assert.Equal(0, f.Pool.ActiveWorkers);
    }
    [Fact]
    public async Task Orphaned_single_bytes_block_both_preparers_until_explicit_owner_cleanup()
    {
        await using var f = new Fixture(orphan: true); Assert.False(f.Pool.InventoryReady); Assert.Equal(1, f.Single.Orphans);
        await Assert.ThrowsAsync<IOException>(() => f.Batches.PrepareAsync([f.Folder], f.Authority, UploadCollision.UploadFail)); Assert.Empty(f.Batches.All); Assert.Equal(0, f.Pool.ActiveWorkers);
        f.Single.RemoveOrphans(); Assert.True(f.Pool.InventoryReady); await f.Batches.PrepareAsync([f.Folder], f.Authority, UploadCollision.UploadFail); Assert.Equal(3, f.Pool.ReservedBytes);
    }
    [Fact]
    public async Task Damaged_batch_inventory_blocks_new_single_preparation_without_deleting_payload()
    {
        await using var f = new Fixture(); var batch = await f.Batches.PrepareAsync([f.Folder], f.Authority, UploadCollision.UploadFail);
        await f.Batches.DisposeAsync(); System.IO.File.WriteAllText(Path.Combine(f.Profile, "outgoing-batch-copies", batch + ".json"), "{"); await f.ReopenBatches();
        Assert.NotEmpty(f.Batches.RecoveryIssues); Assert.False(f.Pool.InventoryReady);
        await Assert.ThrowsAsync<IOException>(() => f.Single.PrepareAsync(f.FilePath, f.Authority)); Assert.Empty(f.Single.All); Assert.Equal(0, f.Pool.ActiveWorkers);
        Assert.Equal(new byte[] { 4, 5, 6 }, System.IO.File.ReadAllBytes(Path.Combine(f.Profile, "outgoing-batch-copies", batch, "1.bin")));
    }
}
