using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using IConnect.Connectivity;
using Google.Protobuf;
using System.Security.Cryptography;
using Xunit;

namespace IConnect.Tests;

public sealed class DownloadSourceRecoveryTests
{
    internal sealed class Clock : TimeProvider
    {
        private readonly DateTimeOffset start = DateTimeOffset.UtcNow;
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public override DateTimeOffset GetUtcNow() => start.AddTicks(GetTimestamp());
        public void Advance(TimeSpan amount) => Interlocked.Add(ref ticks, amount.Ticks);
    }
    internal sealed class Fixture : IAsyncDisposable
    {
        public readonly string Sandbox, Source, Destination, Profile;
        public readonly string Peer;
        public readonly Clock Time = new();
        public readonly StorageGrantStore Grants;
        public readonly DownloadSourceStore Sources;
        public WindowsFileService Service;
        public WindowsDownloadManager Receiver;
        public readonly byte[] Bytes = RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 17);
        public readonly List<string> Prepared = [];
        public WindowsDownloadManager.Request? Transport;
        public string Id = "";
        public bool Current = true;
        private bool stall;
        private TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Fixture(string? peer = null)
        {
            Peer = peer ?? new string('a', 64);
            var repo = new DirectoryInfo(AppContext.BaseDirectory); while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "Directory.Build.props"))) repo = repo.Parent;
            Sandbox = Path.Combine(repo!.FullName, ".local", "source-recovery-tests", ProtocolRules.NewToken()); Source = Path.Combine(Sandbox, "source"); Destination = Path.Combine(Sandbox, "destination"); Profile = Path.Combine(Sandbox, "receiver");
            Directory.CreateDirectory(Source); Directory.CreateDirectory(Destination); File.WriteAllBytes(Path.Combine(Source, "generated-文書.bin"), Bytes);
            Grants = new(Path.Combine(Sandbox, "owner"), timeProvider: Time); Grants.Add(Peer, Source); Sources = new(Grants, Path.Combine(Sandbox, "owner")); Service = new(Grants, Sources);
            Receiver = new(Profile, new WindowsAttachmentChecks(), clock: Time);
        }
        public async Task<FileResponse> Request(FileRequest request, CancellationToken ct)
        {
            if (stall && request.Operation == FileOperation.DownloadRead && request.Offset >= 1024 * 1024) { reached.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
            var copy = request.Clone(); copy.RequestId = ProtocolRules.NewToken(); var response = Transport is null ? await Service.ExecuteAsync(Peer, copy, ct) : await Transport(copy, ct);
            if (request.Operation == FileOperation.PrepareDownload && response.Download is { } info) lock (Prepared) Prepared.Add(info.Id);
            return response;
        }
        public async Task<FileEntry> Selected()
        {
            var root = (await Request(new() { Operation = FileOperation.ListRoots }, default)).Entries.Single();
            return (await Request(new() { Operation = FileOperation.ListDirectory, NodeId = root.Id }, default)).Entries.Single();
        }
        public async Task PauseAtCheckpoint(string batch = "")
        {
            stall = true; Id = Receiver.Start(Peer, await Selected(), Destination, DownloadCollision.KeepBoth, Request, batchId: batch);
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(20)); Receiver.Pause(Id); await Receiver.WaitAsync(Id).WaitAsync(TimeSpan.FromSeconds(20)); stall = false;
            Assert.Equal(1024 * 1024, Job.Offset); Assert.Equal(DownloadPhase.Paused, Job.Phase); Assert.Equal(64, Job.Hash.Length);
        }
        public DownloadJob Job => Receiver.Jobs.Single(x => x.Id == Id);
        public Task<DownloadSourceReview> Review(CancellationToken ct = default) => ReviewCore(ct);
        private async Task<DownloadSourceReview> ReviewCore(CancellationToken ct) => await Receiver.PrepareSourceReviewAsync(Id, Peer, await Selected(), Request, () => Current, ct);
        public async Task Finish()
        { await Receiver.Resume(Id, Request).WaitAsync(TimeSpan.FromSeconds(25)); Assert.Equal(DownloadPhase.Completed, Job.Phase); Assert.Equal(Bytes, File.ReadAllBytes(Path.Combine(Destination, Job.FinalName))); }
        public async ValueTask DisposeAsync()
        {
            await Receiver.DisposeAsync(); Service.Dispose(); await Sources.DisposeAsync(); var full = Path.GetFullPath(Sandbox);
            if (!full.Contains("\\.local\\source-recovery-tests\\", StringComparison.Ordinal)) throw new IOException("Unexpected generated-data cleanup target."); Directory.Delete(full, true);
        }
    }
    [Fact]
    public async Task Expired_real_source_requires_review_and_keeps_the_same_verified_part_through_restart()
    {
        await using var f = new Fixture(); await f.PauseAtCheckpoint(); var previous = f.Job; f.Time.Advance(TimeSpan.FromHours(25));
        await f.Receiver.Resume(f.Id, f.Request); Assert.Equal(DownloadPhase.Interrupted, f.Job.Phase); Assert.Equal(previous.Offset, f.Job.Offset);
        var review = await f.Review(); Assert.True(review.OriginalHashKnown); Assert.Equal(previous.Offset, review.Checkpoint); Assert.True(review.SourceExpiresAt > f.Time.GetUtcNow().ToUnixTimeMilliseconds());
        await f.Receiver.ConfirmSourceReviewAsync(review); var adopted = f.Job; Assert.NotEqual(previous.Ticket, adopted.Ticket);
        Assert.Equal(previous.RootIdentity, adopted.RootIdentity); Assert.Equal(previous.TemporaryIdentity, adopted.TemporaryIdentity); Assert.Equal(previous.Offset, adopted.Offset); Assert.Equal(previous.PrefixHash, adopted.PrefixHash); Assert.Equal(DownloadPhase.Interrupted, adopted.Phase);
        await f.Receiver.DisposeAsync(); f.Receiver = new(f.Profile, new WindowsAttachmentChecks(), clock: f.Time); await f.Finish(); Assert.Single(Directory.GetFiles(f.Destination));
    }
    [Fact]
    public async Task Same_content_with_new_metadata_is_explicitly_adopted_and_new_opaque_node_is_retained()
    {
        await using var f = new Fixture(); await f.PauseAtCheckpoint(); var old = f.Job;
        File.SetLastWriteTimeUtc(Path.Combine(f.Source, old.Name), DateTime.UtcNow.AddMinutes(5)); f.Service.Dispose(); f.Service = new(f.Grants, f.Sources);
        var selected = await f.Selected(); var review = await f.Review(); Assert.NotEqual(old.SourceVersion, review.NewVersion);
        await f.Receiver.ConfirmSourceReviewAsync(review); Assert.Equal(selected.Id, f.Job.SourceNode); Assert.NotEqual(old.SourceNode, f.Job.SourceNode); Assert.Equal(old.Hash, f.Job.Hash); await f.Finish();
    }
    [Fact]
    public async Task Different_full_content_is_refused_without_touching_part_or_old_receipt()
    {
        await using var f = new Fixture(); await f.PauseAtCheckpoint(); var old = f.Job; var part = Directory.GetFiles(f.Destination).Single(); var before = File.ReadAllBytes(part);
        File.WriteAllBytes(Path.Combine(f.Source, old.Name), RandomNumberGenerator.GetBytes(f.Bytes.Length));
        await Assert.ThrowsAsync<IOException>(() => f.Review()); Assert.Equal(old, f.Job); Assert.Equal(before, File.ReadAllBytes(part));
        var ticket = f.Prepared.Last(); Assert.Equal(FileStatus.AccessDenied, (await f.Request(new() { Operation = FileOperation.DownloadStatus, TransferId = ticket }, default)).Status);
    }
    [Theory]
    [InlineData("cancel")] [InlineData("changed")] [InlineData("expired")] [InlineData("connection")] [InlineData("canceled-token")]
    public async Task Unconfirmed_or_invalid_reviews_leave_original_authority_and_progress_unchanged(string choice)
    {
        await using var f = new Fixture(); await f.PauseAtCheckpoint(); var old = f.Job; var review = await f.Review();
        if (choice == "cancel") await f.Receiver.CancelSourceReviewAsync(review.Id);
        else
        {
            if (choice == "expired") f.Time.Advance(TimeSpan.FromMinutes(6)); if (choice == "connection") f.Current = false;
            using var cancel = new CancellationTokenSource(); if (choice == "canceled-token") cancel.Cancel();
            await Assert.ThrowsAnyAsync<Exception>(() => f.Receiver.ConfirmSourceReviewAsync(choice == "changed" ? review with { Hash = new string('f', 64) } : review, cancel.Token));
        }
        Assert.Equal(old, f.Job); await Assert.ThrowsAsync<IOException>(() => f.Receiver.ConfirmSourceReviewAsync(review));
        Assert.Equal(FileStatus.AccessDenied, (await f.Request(new() { Operation = FileOperation.DownloadStatus, TransferId = f.Prepared.Last() }, default)).Status);
    }
    [Fact]
    public async Task A_job_changed_after_review_cannot_be_reactivated()
    {
        await using var f = new Fixture(); await f.PauseAtCheckpoint(); var review = await f.Review(); await f.Receiver.Cancel(f.Id);
        await Assert.ThrowsAsync<IOException>(() => f.Receiver.ConfirmSourceReviewAsync(review)); Assert.Equal(DownloadPhase.Canceled, f.Job.Phase); Assert.Empty(Directory.GetFiles(f.Destination));
    }
    [Fact]
    public async Task Corrupt_checkpoint_is_not_trusted_after_a_fresh_source_is_approved()
    {
        await using var f = new Fixture(); await f.PauseAtCheckpoint(); var review = await f.Review(); var part = Directory.GetFiles(f.Destination).Single();
        using (var stream = new FileStream(part, FileMode.Open, FileAccess.Write)) stream.WriteByte((byte)(f.Bytes[0] ^ 255));
        await f.Receiver.ConfirmSourceReviewAsync(review); await f.Receiver.Resume(f.Id, f.Request); Assert.Equal(DownloadPhase.Failed, f.Job.Phase); Assert.False(File.Exists(Path.Combine(f.Destination, f.Job.Name)));
    }
    [Fact]
    public async Task Destination_collision_still_keeps_both_after_source_recovery()
    {
        await using var f = new Fixture(); await f.PauseAtCheckpoint(); File.WriteAllText(Path.Combine(f.Destination, f.Job.Name), "existing destination");
        await f.Receiver.ConfirmSourceReviewAsync(await f.Review()); await f.Finish(); Assert.NotEqual(f.Job.Name, f.Job.FinalName); Assert.Equal("existing destination", File.ReadAllText(Path.Combine(f.Destination, f.Job.Name)));
    }
    [Fact]
    public async Task Batch_owned_files_cannot_adopt_a_standalone_source_review()
    {
        await using var f = new Fixture(); await f.PauseAtCheckpoint(ProtocolRules.NewToken()); var before = f.Prepared.Count;
        await Assert.ThrowsAsync<IOException>(() => f.Review()); Assert.Equal(before, f.Prepared.Count);
    }
    [Fact]
    public async Task Repeated_review_is_bounded_and_disposal_releases_its_prepared_lease()
    {
        await using var f = new Fixture(); await f.PauseAtCheckpoint(); await f.Review(); var before = f.Prepared.Count;
        await Assert.ThrowsAsync<IOException>(() => f.Review()); Assert.Equal(before, f.Prepared.Count); await f.Receiver.DisposeAsync();
        Assert.Equal(FileStatus.AccessDenied, (await f.Request(new() { Operation = FileOperation.DownloadStatus, TransferId = f.Prepared.Last() }, default)).Status);
    }
    [Fact]
    public async Task Source_revocation_after_review_prevents_adoption_and_cannot_copy_more_bytes()
    {
        await using var f = new Fixture(); await f.PauseAtCheckpoint(); var old = f.Job; var review = await f.Review(); f.Grants.Remove(f.Grants.All.Single().Id);
        await Assert.ThrowsAsync<IOException>(() => f.Receiver.ConfirmSourceReviewAsync(review)); Assert.Equal(old, f.Job);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Ordinary_status_cannot_clear_or_extend_a_recorded_expiry(bool clear)
    {
        await using var f = new Fixture(); await f.PauseAtCheckpoint(); var old = f.Job; f.Time.Advance(TimeSpan.FromHours(25)); var reads = 0;
        async Task<FileResponse> Misleading(FileRequest request, CancellationToken ct)
        {
            if (request.Operation == FileOperation.DownloadStatus) return new() { RequestId = ProtocolRules.NewToken(), Download = new() { Id = old.Ticket, Name = old.Name, Size = old.Size, Version = old.SourceVersion,
                State = DownloadState.DownloadReady, Sha256 = ByteString.CopyFrom(Convert.FromHexString(old.Hash)), PreparedBytes = old.Size, ExpiresAt = clear ? 0 : f.Time.GetUtcNow().AddDays(3).ToUnixTimeMilliseconds() } };
            if (request.Operation == FileOperation.DownloadRead) ++reads; return await f.Request(request, ct);
        }
        await f.Receiver.Resume(f.Id, Misleading); Assert.Equal(old.SourceExpiresAt, f.Job.SourceExpiresAt); Assert.Equal(0, reads); Assert.Equal(old.Offset, f.Job.Offset); Assert.Equal(DownloadPhase.Interrupted, f.Job.Phase);
    }
    [Fact]
    public async Task Native_authenticated_source_refresh_completes_the_retained_file_without_a_second_destination_copy()
    {
        using var hostIdentity = DeviceIdentity.Ephemeral("Generated source host"); using var guestIdentity = DeviceIdentity.Ephemeral("Generated receiver");
        await using var f = new Fixture(guestIdentity.Id); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(65)); var id = ProtocolRules.NewToken();
        using var host = new PeerSession(hostIdentity, guestIdentity.PublicKey, id, true, Path.Combine(f.Sandbox, "host-inbox"), _ => { }, loopbackOnly: true, fileService: f.Service);
        using var guest = new PeerSession(guestIdentity, hostIdentity.PublicKey, id, false, Path.Combine(f.Sandbox, "guest-inbox"), _ => { }, loopbackOnly: true);
        var hostRun = host.RunAsync(timeout.Token); var guestRun = guest.RunAsync(timeout.Token);
        async Task Bridge(PeerSession from, PeerSession to)
        { await foreach (var signal in from.Rtc.Signals.ReadAllAsync(timeout.Token)) if (signal.Kind == "candidate") to.Rtc.AddCandidate(signal.Body, signal.Mid); else to.Rtc.SetDescription(signal.Body, signal.Kind); }
        var bridges = new[] { Bridge(host, guest), Bridge(guest, host) };
        try
        {
            host.Rtc.Offer(); await Task.WhenAll(host.Authenticated, guest.Authenticated).WaitAsync(timeout.Token); f.Transport = guest.RequestFilesAsync;
            await f.PauseAtCheckpoint(); var old = f.Job; f.Time.Advance(TimeSpan.FromHours(25)); await f.Receiver.Resume(f.Id, f.Request); Assert.Equal(DownloadPhase.Interrupted, f.Job.Phase);
            await f.Receiver.ConfirmSourceReviewAsync(await f.Review()); Assert.Equal(old.TemporaryIdentity, f.Job.TemporaryIdentity); await f.Finish(); Assert.Single(Directory.GetFiles(f.Destination));
        }
        finally
        {
            timeout.Cancel(); host.Dispose(); guest.Dispose();
            foreach (var task in bridges.Append(hostRun).Append(guestRun)) try { await task; } catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task A_source_without_an_original_hash_can_be_reviewed_only_without_a_checkpoint(bool empty)
    {
        await using var f = new Fixture(); if (empty) File.WriteAllBytes(Path.Combine(f.Source, "generated-文書.bin"), []);
        f.Id = f.Receiver.Start(f.Peer, await f.Selected(), f.Destination, DownloadCollision.KeepBoth,
            (_, _) => Task.FromResult(new FileResponse { RequestId = ProtocolRules.NewToken(), Status = FileStatus.AccessDenied, Message = "Generated preparation failure before a descriptor." }));
        await f.Receiver.WaitAsync(f.Id); Assert.Equal(DownloadPhase.Interrupted, f.Job.Phase); Assert.Empty(f.Job.Hash); Assert.Equal(0, f.Job.Offset);
        var review = await f.Review(); Assert.False(review.OriginalHashKnown); await f.Receiver.ConfirmSourceReviewAsync(review); await f.Receiver.Resume(f.Id, f.Request);
        Assert.Equal(DownloadPhase.Completed, f.Job.Phase); Assert.Equal(empty ? [] : f.Bytes, File.ReadAllBytes(Path.Combine(f.Destination, f.Job.FinalName)));
    }
    [Fact]
    public async Task Removed_receipt_cannot_adopt_a_review_and_its_unused_source_is_released()
    {
        await using var f = new Fixture(); await f.PauseAtCheckpoint(); var review = await f.Review(); var fresh = f.Prepared.Last();
        await f.Receiver.Cancel(f.Id); f.Receiver.RemoveRecord(f.Id); await Assert.ThrowsAsync<IOException>(() => f.Receiver.ConfirmSourceReviewAsync(review));
        Assert.Empty(f.Receiver.Jobs); Assert.Equal(FileStatus.AccessDenied, (await f.Request(new() { Operation = FileOperation.DownloadStatus, TransferId = fresh }, default)).Status);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Shutdown_cancels_and_waits_for_source_preparation_or_confirmation(bool confirming)
    {
        await using var f = new Fixture(); await f.PauseAtCheckpoint(); var old = f.Job; var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DownloadSourceReview? review = confirming ? await f.Review() : null;
        async Task<FileResponse> Held(FileRequest request, CancellationToken ct)
        {
            if (request.Operation == (confirming ? FileOperation.DownloadStatus : FileOperation.GetMetadata)) { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
            var copy = request.Clone(); copy.RequestId = ProtocolRules.NewToken(); return await f.Service.ExecuteAsync(f.Peer, copy, ct);
        }
        Task work;
        if (confirming) { f.Transport = Held; work = f.Receiver.ConfirmSourceReviewAsync(review!); }
        else work = f.Receiver.PrepareSourceReviewAsync(f.Id, f.Peer, await f.Selected(), Held, () => true);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); var closing = f.Receiver.DisposeAsync().AsTask(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work); await closing.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(old, f.Job); f.Transport = null;
    }
    [Theory]
    [InlineData("peer")] [InlineData("name")] [InlineData("size")] [InlineData("connection")]
    public async Task Source_review_is_bound_to_the_retained_peer_and_current_selection(string changed)
    {
        await using var f = new Fixture(); await f.PauseAtCheckpoint(); var file = await f.Selected(); var before = f.Prepared.Count;
        if (changed == "name") file.Name = "different.bin"; if (changed == "size") ++file.Size;
        await Assert.ThrowsAsync<IOException>(() => f.Receiver.PrepareSourceReviewAsync(f.Id, changed == "peer" ? new string('b', 64) : f.Peer, file, f.Request, () => changed != "connection")); Assert.Equal(before, f.Prepared.Count);
    }
    [Fact]
    public async Task Preparing_status_shortening_survives_failure_and_cannot_be_extended_on_retry()
    {
        await using var f = new Fixture(); await f.PauseAtCheckpoint(); var old = f.Job; var statuses = 0;
        f.Transport = async (request, ct) =>
        {
            if (request.Operation == FileOperation.DownloadStatus && ++statuses == 3) throw new IOException("Generated preparation interruption");
            var response = await f.Service.ExecuteAsync(f.Peer, request, ct);
            if (request.Operation == FileOperation.DownloadStatus) { response.Download.State = DownloadState.DownloadPreparing; response.Download.Sha256 = ByteString.Empty; response.Download.PreparedBytes = 0; response.Download.ExpiresAt = old.SourceExpiresAt - statuses * 1000; }
            return response;
        };
        await f.Receiver.Resume(f.Id, f.Request); Assert.Equal(old.SourceExpiresAt - 2000, f.Job.SourceExpiresAt); Assert.Equal(old.Offset, f.Job.Offset);
        f.Time.Advance(TimeSpan.FromMilliseconds(old.SourceExpiresAt - 1500 - f.Time.GetUtcNow().ToUnixTimeMilliseconds())); var reads = 0;
        f.Transport = async (request, ct) => { if (request.Operation == FileOperation.DownloadRead) reads++; var response = await f.Service.ExecuteAsync(f.Peer, request, ct); if (response.Download is { } info) info.ExpiresAt = old.SourceExpiresAt + 1000000; return response; };
        await f.Receiver.Resume(f.Id, f.Request); Assert.Equal(0, reads); Assert.Equal(old.SourceExpiresAt - 2000, f.Job.SourceExpiresAt); Assert.Equal(old.Offset, f.Job.Offset);
    }
}
