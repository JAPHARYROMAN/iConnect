using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Xunit;

namespace IConnect.Tests;

public sealed class ServingSourceTests
{
    private sealed class Clock : TimeProvider
    {
        private long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        public Action? OnRead;
        public override DateTimeOffset GetUtcNow() { Interlocked.Exchange(ref OnRead, null)?.Invoke(); return DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Read(ref now)); }
        public void Advance(TimeSpan amount) => Interlocked.Add(ref now, (long)amount.TotalMilliseconds);
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public readonly string Root, Documents, Profile;
        public readonly string Peer = new('a', 64);
        public readonly Clock Time = new();
        public readonly byte[] Bytes = RandomNumberGenerator.GetBytes(128 * 1024 + 17);
        public readonly StorageGrantStore Grants;
        public DownloadSourceStore Sources;
        public readonly string Grant, Identity, Version;
        public string Original => Path.Combine(Documents, "generated.bin");
        public string Journal => Path.Combine(Profile, "download-sources.json");
        public Fixture()
        {
            var repo = new DirectoryInfo(AppContext.BaseDirectory); while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "Directory.Build.props"))) repo = repo.Parent;
            Root = Path.Combine(repo!.FullName, ".local", "serving-source-tests", ProtocolRules.NewToken()); Documents = Path.Combine(Root, "documents"); Profile = Path.Combine(Root, "profile");
            Directory.CreateDirectory(Documents); File.WriteAllBytes(Original, Bytes); Grants = new(Profile, Time); Grant = Grants.Add(Peer, Documents).Id;
            using var root = SecureWindowsDirectory.OpenRoot(Documents); using var file = root.OpenChild("generated.bin"); Identity = file.Info.Identity; Version = file.Info.Version;
            Sources = new(Grants, Profile);
        }
        public DownloadInfo Prepare() => Sources.Prepare(Peer, Grant, ["generated.bin"], Identity, Version, Bytes.Length, "generated-scope");
        public async Task<DownloadInfo> Ready(string id)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (true)
            {
                var result = Sources.Status(Peer, id, "generated-scope"); Assert.NotEqual(DownloadState.DownloadFailed, result.State);
                if (result.State == DownloadState.DownloadReady && !Sources.All.Single(x => x.Id == id).Preparing) return result;
                await Task.Delay(10, timeout.Token);
            }
        }
        public FileResponse Read(string id) => Sources.Read(Peer, new() { Operation = FileOperation.DownloadRead, TransferId = id, Length = StorageRules.MaxReadRange, RequestId = ProtocolRules.NewToken() });
        public async Task Reopen() { await Sources.DisposeAsync(); Sources = new(Grants, Profile); }
        public async ValueTask DisposeAsync()
        {
            Time.OnRead = null; await Sources.DisposeAsync();
            if (!Path.GetFullPath(Root).Contains("\\.local\\serving-source-tests\\", StringComparison.Ordinal)) throw new IOException("Unexpected generated-data cleanup root.");
            Directory.Delete(Root, true);
        }
    }
    [Fact]
    public async Task Owner_inventory_has_bounded_metadata_and_stopping_one_approval_keeps_an_independent_read_and_original()
    {
        await using var f = new Fixture(); var first = f.Prepare(); var second = f.Prepare(); await f.Ready(first.Id); await f.Ready(second.Id);
        var row = Assert.Single(f.Sources.All, x => x.Id == first.Id); Assert.Equal(f.Peer, row.Peer); Assert.Equal("generated.bin", row.Name);
        Assert.Equal(f.Bytes.Length, row.Size); Assert.Equal(row.Size, row.Prepared); Assert.False(row.Expired); Assert.False(row.Preparing);
        await f.Sources.StopOwnedAsync(first.Id); Assert.Throws<StorageException>(() => f.Read(first.Id)); Assert.Single(f.Sources.All);
        Assert.Equal(f.Bytes[..StorageRules.MaxReadRange], f.Read(second.Id).Content.ToByteArray()); Assert.Equal(f.Bytes, File.ReadAllBytes(f.Original));
        Assert.Equal(StoragePermission.ReadOnly, f.Grants.Get(f.Grant, f.Peer).Permissions);
    }
    [Fact]
    public async Task A_successful_stop_is_durable_and_idempotent_without_changing_shared_folder_approval()
    {
        await using var f = new Fixture(); var source = f.Prepare(); await f.Ready(source.Id); await f.Sources.StopOwnedAsync(source.Id); await f.Sources.StopOwnedAsync(source.Id);
        await f.Reopen(); Assert.Empty(f.Sources.All); Assert.Throws<StorageException>(() => f.Read(source.Id));
        var fresh = f.Prepare(); Assert.NotEqual(source.Id, fresh.Id); await f.Ready(fresh.Id); Assert.NotEmpty(f.Read(fresh.Id).Content); Assert.Equal(f.Bytes, File.ReadAllBytes(f.Original));
    }
    [Fact]
    public async Task Owner_stop_frees_one_of_the_eight_peer_slots_without_releasing_the_other_seven()
    {
        await using var f = new Fixture(); var sources = Enumerable.Range(0, 8).Select(_ => f.Prepare()).ToArray();
        Assert.Equal(FileStatus.ResourceExhausted, Assert.Throws<StorageException>(() => f.Prepare()).Status);
        await f.Sources.StopOwnedAsync(sources[0].Id); var replacement = f.Prepare(); Assert.Equal(8, f.Sources.All.Count);
        Assert.DoesNotContain(f.Sources.All, x => x.Id == sources[0].Id); Assert.Contains(f.Sources.All, x => x.Id == replacement.Id);
        foreach (var source in sources.Skip(1)) Assert.Contains(f.Sources.All, x => x.Id == source.Id);
    }
    [Fact]
    public async Task Stopping_preparation_drains_its_worker_and_a_later_status_cannot_restart_it()
    {
        await using var f = new Fixture(); var source = f.Prepare(); await f.Sources.StopOwnedAsync(source.Id);
        Assert.False(f.Sources.Preparing); Assert.Empty(f.Sources.All); Assert.Throws<StorageException>(() => f.Sources.Status(f.Peer, source.Id, "another-scope"));
        Assert.Equal(f.Bytes, File.ReadAllBytes(f.Original));
    }
    [Fact]
    public async Task Owner_stop_between_ticket_lookup_and_range_publication_cannot_return_old_bytes()
    {
        await using var f = new Fixture(); var source = f.Prepare(); await f.Ready(source.Id);
        f.Time.OnRead = () => f.Sources.StopOwnedAsync(source.Id).GetAwaiter().GetResult();
        Assert.Equal(FileStatus.AccessDenied, Assert.Throws<StorageException>(() => f.Read(source.Id)).Status);
        Assert.Empty(f.Sources.All); Assert.Equal(f.Bytes, File.ReadAllBytes(f.Original));
    }
    [Fact]
    public async Task Remote_release_cannot_remove_another_peers_source()
    {
        await using var f = new Fixture(); var source = f.Prepare(); await f.Ready(source.Id);
        f.Sources.Release(new string('c', 64), source.Id); Assert.Single(f.Sources.All); Assert.NotEmpty(f.Read(source.Id).Content);
        f.Sources.Release(f.Peer, source.Id); Assert.Empty(f.Sources.All);
    }
    [Fact]
    public async Task Explicit_expiry_cleanup_keeps_a_later_live_approval_and_persists_the_result()
    {
        await using var f = new Fixture(); var expired = f.Prepare(); await f.Ready(expired.Id); f.Time.Advance(TimeSpan.FromHours(12)); var live = f.Prepare(); await f.Ready(live.Id);
        f.Time.Advance(TimeSpan.FromHours(13)); var before = File.ReadAllBytes(f.Original); Assert.True(f.Sources.All.Single(x => x.Id == expired.Id).Expired);
        Assert.Equal(1, await f.Sources.RemoveExpiredAsync()); Assert.Equal(0, await f.Sources.RemoveExpiredAsync()); Assert.Equal(live.Id, Assert.Single(f.Sources.All).Id);
        await f.Reopen(); Assert.Equal(live.Id, Assert.Single(f.Sources.All).Id); Assert.NotEmpty(f.Read(live.Id).Content); Assert.Equal(before, File.ReadAllBytes(f.Original));
    }
    [Fact]
    public async Task Reopening_a_valid_store_removes_only_expired_operational_records()
    {
        await using var f = new Fixture(); var source = f.Prepare(); await f.Ready(source.Id); f.Time.Advance(TimeSpan.FromHours(25));
        await f.Reopen(); Assert.Empty(f.Sources.All); Assert.Empty(JsonNode.Parse(File.ReadAllText(f.Journal))!.AsObject()); Assert.Equal(f.Bytes, File.ReadAllBytes(f.Original));
    }
    [Fact]
    public async Task A_new_preparation_persists_expired_slot_cleanup_and_gets_its_own_deadline()
    {
        await using var f = new Fixture(); var first = f.Prepare(); await f.Ready(first.Id); f.Time.Advance(TimeSpan.FromHours(25)); var fresh = f.Prepare(); await f.Ready(fresh.Id);
        Assert.Equal(fresh.Id, Assert.Single(f.Sources.All).Id); Assert.Equal(f.Time.GetUtcNow().AddHours(24).ToUnixTimeMilliseconds(), fresh.ExpiresAt);
        await f.Reopen(); Assert.Equal(fresh.Id, Assert.Single(f.Sources.All).Id); Assert.DoesNotContain(first.Id, File.ReadAllText(f.Journal));
    }
    [Fact]
    public async Task A_failed_stop_write_blocks_ticketed_reads_and_preserves_the_last_saved_metadata_and_original()
    {
        await using var f = new Fixture(); var first = f.Prepare(); var second = f.Prepare(); await f.Ready(first.Id); await f.Ready(second.Id); var before = File.ReadAllBytes(f.Journal);
        using (var blocker = new FileStream(f.Journal + ".new", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => f.Sources.StopOwnedAsync(first.Id));
            Assert.ThrowsAny<IOException>(() => f.Read(second.Id)); Assert.NotEmpty(f.Sources.RecoveryIssues); Assert.Equal(before, File.ReadAllBytes(f.Journal));
        }
        await f.Reopen(); Assert.NotEmpty(f.Sources.RecoveryIssues); Assert.ThrowsAny<IOException>(() => f.Read(first.Id)); Assert.Equal(f.Bytes, File.ReadAllBytes(f.Original));
    }
    [Theory]
    [InlineData(".new")]
    [InlineData(".bak")]
    public async Task An_ambiguous_source_journal_blocks_replay_even_with_a_valid_primary(string suffix)
    {
        await using var f = new Fixture(); var source = f.Prepare(); await f.Ready(source.Id); File.Copy(f.Journal, f.Journal + suffix); await f.Reopen();
        Assert.NotEmpty(f.Sources.RecoveryIssues); Assert.ThrowsAny<IOException>(() => f.Read(source.Id)); Assert.Equal(f.Bytes, File.ReadAllBytes(f.Original));
    }
    [Fact]
    public async Task Unrecognized_native_ownership_is_preserved_for_review_instead_of_exposed_as_an_approval()
    {
        await using var f = new Fixture(); var source = f.Prepare(); await f.Ready(source.Id); var json = JsonNode.Parse(File.ReadAllText(f.Journal))!; json[source.Id]!["Identity"] = ""; File.WriteAllText(f.Journal, json.ToJsonString());
        await f.Reopen(); Assert.NotEmpty(f.Sources.RecoveryIssues); Assert.Empty(f.Sources.All); Assert.Equal(f.Bytes, File.ReadAllBytes(f.Original));
    }
    [Fact]
    public async Task Throwing_inventory_observers_do_not_change_preparation_or_stop_outcomes()
    {
        await using var f = new Fixture(); f.Sources.Changed += () => throw new InvalidOperationException("Generated observer failure.");
        var source = f.Prepare(); await f.Ready(source.Id); Assert.NotEmpty(f.Read(source.Id).Content); await f.Sources.StopOwnedAsync(source.Id); Assert.Empty(f.Sources.All);
    }
    [Fact]
    public async Task A_reentrant_owner_stop_during_inventory_notification_cannot_start_a_late_worker()
    {
        await using var f = new Fixture(); var stopped = false;
        f.Sources.Changed += () => { if (!stopped && f.Sources.All.SingleOrDefault() is { } row) { stopped = true; f.Sources.StopOwnedAsync(row.Id).GetAwaiter().GetResult(); } };
        var source = f.Prepare(); Assert.True(stopped); Assert.Empty(f.Sources.All); Assert.False(f.Sources.Preparing); Assert.Throws<StorageException>(() => f.Read(source.Id));
    }
    [Fact]
    public async Task Stopping_a_source_keeps_the_actual_receiver_prefix_and_a_reviewed_source_can_complete_it()
    {
        await using var f = new DownloadSourceRecoveryTests.Fixture(); await f.PauseAtCheckpoint(); var before = f.Job;
        await f.Sources.StopOwnedAsync(before.Ticket); Assert.Empty(f.Sources.All);
        await f.Receiver.Resume(f.Id, f.Request); Assert.True(f.Job.Phase is DownloadPhase.Interrupted or DownloadPhase.Paused); Assert.Equal(before.Offset, f.Job.Offset);
        Assert.Equal(before.Id, f.Job.Id); Assert.Equal(before.TemporaryIdentity, f.Job.TemporaryIdentity); Assert.Equal(before.RootIdentity, f.Job.RootIdentity);
        var review = await f.Review(); await f.Receiver.ConfirmSourceReviewAsync(review); await f.Finish(); Assert.Single(Directory.GetFiles(f.Destination));
    }
}
