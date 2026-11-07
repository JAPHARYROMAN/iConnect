using Google.Protobuf;
using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Xunit;

namespace IConnect.Tests;

public sealed partial class UploadTests
{
    internal sealed class OperationClock : TimeProvider
    {
        private long wall = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), elapsed;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Interlocked.Read(ref elapsed);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Read(ref wall));
        public void Wall(TimeSpan delta) => Interlocked.Add(ref wall, (long)delta.TotalMilliseconds);
        public void Monotonic(TimeSpan delta) => Interlocked.Add(ref elapsed, (long)delta.TotalMilliseconds);
        public void Advance(TimeSpan delta) { Wall(delta); Monotonic(delta); }
    }
    private static string OperationJournal(Fixture f) => Path.Combine(f.Profile, "incoming-uploads", "journal.json");
    private static string OperationPart(Fixture f) => Assert.Single(Directory.GetFiles(f.Destination, "*.part"));
    private static string OperationIdentity(Fixture f, string path)
    {
        using var root = SecureWindowsDirectory.OpenRoot(f.Destination);
        using var file = root.OpenTransferFile(Path.GetFileName(path)[".iconnect-transfer-".Length..^5], false); return file.Info.Identity;
    }
    private static byte[] OperationBytes() => RandomNumberGenerator.GetBytes(3 * StorageRules.MaxReadRange + 17);
    private static Task<FileResponse> OperationWrite(Fixture f, UploadInfo row, byte[] bytes, long offset) => f.Raw(new()
    {
        Operation = FileOperation.UploadWrite, TransferId = row.Id, MutationEpoch = f.Epoch, Offset = offset,
        Content = ByteString.CopyFrom(bytes, (int)offset, (int)Math.Min(StorageRules.MaxReadRange, bytes.Length - offset))
    });
    private static async Task<UploadInfo> OperationCheckpoint(Fixture f, byte[] bytes)
    {
        await f.Initialize(); var row = await f.Begin(Fixture.Descriptor("retained.bin", bytes));
        Assert.Equal(FileStatus.Ok, (await OperationWrite(f, row, bytes, 0)).Status); return row;
    }

    [Fact]
    public async Task Incoming_rate_samples_only_saved_new_ranges_and_clears_on_scope_end()
    {
        var clock = new OperationClock();
        await using var f = new Fixture(clock: clock);
        var data = OperationBytes();
        var receipt = await OperationCheckpoint(f, data);
        var uploads = f.Grants.Uploads;
        var range = StorageRules.MaxReadRange;
        PayloadRateEstimate? Estimate() => uploads.ReadPayload(uploads.FindUpload(receipt.Id)!);
        var metadataChanges = 0;
        uploads.MetadataChanged += () => metadataChanges++;

        Assert.Equal(PayloadRateState.Measuring, Estimate()!.State);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(FileStatus.Ok, (await OperationWrite(f, receipt, data, range)).Status);
        Assert.Equal(new PayloadRateEstimate(PayloadRateState.Available, range / 2, 3), Estimate());
        Assert.Equal(0, metadataChanges);
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(FileStatus.Ok, (await OperationWrite(f, receipt, data, range)).Status);
        Assert.Equal(2L * range, uploads.All.Single().Offset);
        Assert.Equal(PayloadRateState.Waiting, Estimate()!.State);

        await f.Restart();
        Assert.Null(Estimate());
        uploads = f.Grants.Uploads;
        uploads.MetadataChanged += () => metadataChanges++;
        Assert.Null(Estimate());
        Assert.Equal(FileStatus.Ok, (await OperationWrite(f, receipt, data, 2L * range)).Status);
        Assert.Equal(PayloadRateState.Measuring, Estimate()!.State);
        Assert.Equal(FileStatus.Ok, (await OperationWrite(f, receipt, data, 3L * range)).Status);
        Assert.Null(Estimate());
        Assert.Equal(1, metadataChanges);
        var done = await f.Finish(receipt);
        Assert.Equal(UploadPhase.UploadCompleted, done.Phase);
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(f.Destination, done.FinalName)));
    }

    [Fact]
    public async Task Expired_operation_preserves_status_and_owner_cancel_but_refuses_write_and_finish()
    {
        var clock = new OperationClock(); await using var f = new Fixture(clock: clock); var bytes = OperationBytes(); var row = await OperationCheckpoint(f, bytes);
        clock.Advance(TimeSpan.FromHours(25)); var offset = f.Grants.Uploads.All.Single().Offset;
        Assert.Equal(FileStatus.StaleReference, (await OperationWrite(f, row, bytes, offset)).Status);
        Assert.Equal(FileStatus.StaleReference, (await f.Raw(new() { Operation = FileOperation.FinishUpload, TransferId = row.Id, MutationEpoch = f.Epoch })).Status);
        Assert.Equal(offset, (await f.Call(new() { Operation = FileOperation.UploadStatus, TransferId = row.Id, MutationEpoch = f.Epoch })).Upload.Offset);
        Assert.Equal(UploadPhase.UploadCanceled, (await f.Call(new() { Operation = FileOperation.CancelUpload, TransferId = row.Id, MutationEpoch = f.Epoch })).Upload.Phase);
        Assert.Empty(Directory.GetFiles(f.Destination));
    }
    [Fact]
    public async Task Cancelled_owner_window_review_preserves_exact_metadata_part_and_checkpoint()
    {
        var clock = new OperationClock(); await using var f = new Fixture(clock: clock); var bytes = OperationBytes(); var row = await OperationCheckpoint(f, bytes);
        clock.Advance(TimeSpan.FromHours(25)); var before = File.ReadAllBytes(OperationJournal(f)); var part = OperationPart(f); var partBytes = File.ReadAllBytes(part);
        var review = await f.Grants.Uploads.ReviewOperationAsync(row.Id, () => true); f.Grants.Uploads.CancelOperationReview(review.Id);
        await Assert.ThrowsAnyAsync<IOException>(() => f.Grants.Uploads.ConfirmOperationAsync(review));
        Assert.Equal(before, File.ReadAllBytes(OperationJournal(f))); Assert.Equal(partBytes, File.ReadAllBytes(part));
        Assert.Equal(FileStatus.StaleReference, (await OperationWrite(f, row, bytes, StorageRules.MaxReadRange)).Status);
    }
    [Fact]
    public async Task Reviewed_window_survives_restart_and_resumes_the_same_native_partial_without_another_copy()
    {
        var clock = new OperationClock(); await using var f = new Fixture(clock: clock); var bytes = OperationBytes(); var row = await OperationCheckpoint(f, bytes);
        var part = OperationPart(f); var identity = OperationIdentity(f, part);
        clock.Advance(TimeSpan.FromHours(25)); var review = await f.Grants.Uploads.ReviewOperationAsync(row.Id, () => true);
        Assert.Equal(StorageRules.MaxReadRange, review.Checkpoint); Assert.Equal(row.Id, review.Receipt);
        await f.Grants.Uploads.ConfirmOperationAsync(review);
        Assert.Equal(UploadPhase.UploadReceiving, f.Grants.Uploads.All.Single().Phase); Assert.Equal(StorageRules.MaxReadRange, f.Grants.Uploads.All.Single().Offset);
        Assert.Single(Directory.GetFiles(f.Destination)); Assert.Equal(part, OperationPart(f));
        Assert.Equal(identity, OperationIdentity(f, part));
        await f.Restart(); Assert.Equal(review.ProposedExpiresAt, f.Grants.Uploads.All.Single().OperationExpiresAt);
        await f.Write(row, bytes, review.Checkpoint); var done = await f.Finish(row);
        Assert.Equal(UploadPhase.UploadCompleted, done.Phase); Assert.Equal(row.Id, done.Id); Assert.Single(Directory.GetFiles(f.Destination));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(f.Destination, done.FinalName)));
        var later = Fixture.Descriptor("later-empty.bin", []); var laterId = RandomNumberGenerator.GetBytes(32);
        BinaryPrimitives.WriteInt64BigEndian(laterId, clock.GetUtcNow().ToUnixTimeMilliseconds()); later.Id = ProtocolRules.Base64Url(laterId);
        await f.Begin(later); Assert.Equal(2, f.Grants.Uploads.All.Count);
        Assert.Equal(UploadPhase.UploadCompleted, (await f.Call(new() { Operation = FileOperation.UploadStatus, TransferId = row.Id, MutationEpoch = f.Epoch })).Upload.Phase);
        Assert.Equal(bytes.Length, f.Grants.Uploads.ReservedBytes(f.Peer));
    }
    [Fact]
    public async Task Confirmation_keeps_the_shown_deadline_and_status_cannot_slide_it_forward()
    {
        var clock = new OperationClock(); await using var f = new Fixture(clock: clock); var bytes = OperationBytes(); var row = await OperationCheckpoint(f, bytes);
        clock.Advance(TimeSpan.FromHours(25)); var review = await f.Grants.Uploads.ReviewOperationAsync(row.Id, () => true);
        clock.Advance(TimeSpan.FromMinutes(1)); await f.Grants.Uploads.ConfirmOperationAsync(review);
        Assert.Equal(review.ProposedExpiresAt, f.Grants.Uploads.All.Single().OperationExpiresAt);
        await Assert.ThrowsAnyAsync<IOException>(() => f.Grants.Uploads.ConfirmOperationAsync(review));
        clock.Advance(TimeSpan.FromHours(24)); await f.Call(new() { Operation = FileOperation.UploadStatus, TransferId = row.Id, MutationEpoch = f.Epoch });
        Assert.Equal(review.ProposedExpiresAt, f.Grants.Uploads.All.Single().OperationExpiresAt);
        Assert.Equal(FileStatus.StaleReference, (await OperationWrite(f, row, bytes, review.Checkpoint)).Status);
        var again = await f.Grants.Uploads.ReviewOperationAsync(row.Id, () => true); await f.Grants.Uploads.ConfirmOperationAsync(again);
        Assert.Equal(FileStatus.Ok, (await OperationWrite(f, row, bytes, review.Checkpoint)).Status);
    }
    [Fact]
    public async Task An_unexpired_window_cannot_be_renewed_in_advance()
    {
        var clock = new OperationClock(); await using var f = new Fixture(clock: clock); var row = await OperationCheckpoint(f, OperationBytes()); var before = File.ReadAllBytes(OperationJournal(f));
        await Assert.ThrowsAnyAsync<IOException>(() => f.Grants.Uploads.ReviewOperationAsync(row.Id, () => true)); Assert.Equal(before, File.ReadAllBytes(OperationJournal(f)));
    }
    [Theory]
    [InlineData("wall")]
    [InlineData("monotonic")]
    [InlineData("both")]
    [InlineData("backward-wall")]
    [InlineData("backward-monotonic")]
    public async Task Expired_or_rolled_back_review_clocks_cannot_extend_a_window(string kind)
    {
        var clock = new OperationClock(); await using var f = new Fixture(clock: clock); var row = await OperationCheckpoint(f, OperationBytes()); clock.Advance(TimeSpan.FromHours(25));
        var review = await f.Grants.Uploads.ReviewOperationAsync(row.Id, () => true); var before = File.ReadAllBytes(OperationJournal(f));
        if (kind == "wall") clock.Wall(TimeSpan.FromMinutes(5));
        if (kind == "monotonic") clock.Monotonic(TimeSpan.FromMinutes(5));
        if (kind == "both") clock.Advance(TimeSpan.FromMinutes(5));
        if (kind == "backward-wall") clock.Wall(TimeSpan.FromMilliseconds(-1));
        if (kind == "backward-monotonic") clock.Monotonic(TimeSpan.FromMilliseconds(-1));
        await Assert.ThrowsAnyAsync<IOException>(() => f.Grants.Uploads.ConfirmOperationAsync(review)); Assert.Equal(before, File.ReadAllBytes(OperationJournal(f)));
    }
    [Theory]
    [InlineData("deadline")]
    [InlineData("checkpoint")]
    [InlineData("receipt")]
    public async Task Changed_public_review_values_are_consumed_without_mutating_the_receipt(string field)
    {
        var clock = new OperationClock(); await using var f = new Fixture(clock: clock); var row = await OperationCheckpoint(f, OperationBytes()); clock.Advance(TimeSpan.FromHours(25));
        var review = await f.Grants.Uploads.ReviewOperationAsync(row.Id, () => true); var before = File.ReadAllBytes(OperationJournal(f));
        var changed = field switch { "deadline" => review with { ProposedExpiresAt = review.ProposedExpiresAt + 1 }, "checkpoint" => review with { Checkpoint = 0 }, _ => review with { Receipt = MutationRules.NewId() } };
        await Assert.ThrowsAnyAsync<IOException>(() => f.Grants.Uploads.ConfirmOperationAsync(changed)); await Assert.ThrowsAnyAsync<IOException>(() => f.Grants.Uploads.ConfirmOperationAsync(review));
        Assert.Equal(before, File.ReadAllBytes(OperationJournal(f)));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Changed_checkpoint_bytes_are_rehashed_even_when_the_native_version_was_restored(bool afterReview)
    {
        var clock = new OperationClock(); await using var f = new Fixture(clock: clock); var row = await OperationCheckpoint(f, OperationBytes()); clock.Advance(TimeSpan.FromHours(25));
        var review = afterReview ? await f.Grants.Uploads.ReviewOperationAsync(row.Id, () => true) : null;
        var path = OperationPart(f); var original = File.ReadAllBytes(path); var time = File.GetLastWriteTimeUtc(path); var before = File.ReadAllBytes(OperationJournal(f));
        using (var changed = new FileStream(path, FileMode.Open, FileAccess.Write)) changed.WriteByte((byte)(original[0] ^ 1)); File.SetLastWriteTimeUtc(path, time);
        if (review is null) await Assert.ThrowsAnyAsync<IOException>(() => f.Grants.Uploads.ReviewOperationAsync(row.Id, () => true));
        else await Assert.ThrowsAnyAsync<IOException>(() => f.Grants.Uploads.ConfirmOperationAsync(review));
        Assert.Equal(before, File.ReadAllBytes(OperationJournal(f))); Assert.Single(Directory.GetFiles(f.Destination));
    }
    [Theory]
    [InlineData("part")]
    [InlineData("folder")]
    public async Task A_replaced_native_part_or_destination_cannot_receive_the_reviewed_window(string replace)
    {
        var clock = new OperationClock(); await using var f = new Fixture(clock: clock); var row = await OperationCheckpoint(f, OperationBytes()); clock.Advance(TimeSpan.FromHours(25));
        var review = await f.Grants.Uploads.ReviewOperationAsync(row.Id, () => true); var before = File.ReadAllBytes(OperationJournal(f)); var part = OperationPart(f);
        if (replace == "part") { var retained = File.ReadAllBytes(part); File.Move(part, part + ".kept"); File.WriteAllBytes(part, retained); }
        else { Directory.Move(f.Destination, f.Destination + "-kept"); Directory.CreateDirectory(f.Destination); }
        await Assert.ThrowsAnyAsync<IOException>(() => f.Grants.Uploads.ConfirmOperationAsync(review)); Assert.Equal(before, File.ReadAllBytes(OperationJournal(f)));
    }
    [Theory]
    [InlineData("pause")]
    [InlineData("permissions")]
    [InlineData("exclusion")]
    public async Task An_approval_change_invalidates_review_even_when_the_original_approval_is_restored(string change)
    {
        var clock = new OperationClock(); await using var f = new Fixture(clock: clock); var row = await OperationCheckpoint(f, OperationBytes()); clock.Advance(TimeSpan.FromHours(25));
        var review = await f.Grants.Uploads.ReviewOperationAsync(row.Id, () => true); var before = File.ReadAllBytes(OperationJournal(f));
        f.Grants.Update(f.Grant.Id, change == "permissions" ? StoragePermission.ReadOnly : StoragePermission.Contribute, change == "exclusion" ? [row.Name] : [], null, change != "pause");
        f.Grants.Update(f.Grant.Id, StoragePermission.Contribute, [], null, true);
        await Assert.ThrowsAnyAsync<IOException>(() => f.Grants.Uploads.ConfirmOperationAsync(review)); Assert.Equal(before, File.ReadAllBytes(OperationJournal(f)));
    }
    [Fact]
    public async Task A_new_window_does_not_extend_the_underlying_grant_or_enable_another_peer()
    {
        var clock = new OperationClock(); await using var f = new Fixture(clock: clock); var bytes = OperationBytes(); var row = await OperationCheckpoint(f, bytes); clock.Advance(TimeSpan.FromHours(25));
        var grantExpiry = clock.GetUtcNow().AddMinutes(1); f.Grants.Update(f.Grant.Id, StoragePermission.Contribute, [], grantExpiry, true);
        var review = await f.Grants.Uploads.ReviewOperationAsync(row.Id, () => true); await f.Grants.Uploads.ConfirmOperationAsync(review);
        Assert.Equal(grantExpiry.ToUnixTimeMilliseconds(), f.Grants.Get(f.Grant.Id, f.Peer).ExpiresAt);
        Assert.Equal(FileStatus.NotFound, (await f.Raw(new() { Operation = FileOperation.UploadStatus, TransferId = row.Id, MutationEpoch = f.Epoch }, new string('c', 64))).Status);
        clock.Advance(TimeSpan.FromMinutes(2)); Assert.Equal(FileStatus.AccessDenied, (await OperationWrite(f, row, bytes, review.Checkpoint)).Status);
        Assert.Equal(review.Checkpoint, f.Grants.Uploads.All.Single().Offset);
    }
    [Fact]
    public async Task Owner_reset_ends_previous_window_reviews()
    {
        var clock = new OperationClock(); await using var f = new Fixture(ownerBound: true, clock: clock); var row = await OperationCheckpoint(f, OperationBytes()); clock.Advance(TimeSpan.FromHours(25));
        var review = await f.Grants.Uploads.ReviewOperationAsync(row.Id, () => true); f.Binding!.Reset(); f.Grants.ReloadOwnerScope();
        await Assert.ThrowsAnyAsync<IOException>(() => f.Grants.Uploads.ConfirmOperationAsync(review)); Assert.Single(Directory.GetFiles(f.Destination));
    }
    [Fact]
    public async Task Context_withdrawal_and_explicit_invalidation_end_old_reviews()
    {
        var clock = new OperationClock(); await using var f = new Fixture(clock: clock); var row = await OperationCheckpoint(f, OperationBytes()); clock.Advance(TimeSpan.FromHours(25)); var allowed = true;
        var review = await f.Grants.Uploads.ReviewOperationAsync(row.Id, () => allowed); var before = File.ReadAllBytes(OperationJournal(f)); allowed = false;
        await Assert.ThrowsAnyAsync<IOException>(() => f.Grants.Uploads.ConfirmOperationAsync(review)); allowed = true;
        review = await f.Grants.Uploads.ReviewOperationAsync(row.Id, () => allowed); f.Grants.Uploads.InvalidateOperationReviews();
        await Assert.ThrowsAnyAsync<IOException>(() => f.Grants.Uploads.ConfirmOperationAsync(review)); Assert.Equal(before, File.ReadAllBytes(OperationJournal(f)));
    }
    [Fact]
    public async Task Interrupted_checkpoint_check_releases_its_slot_without_changing_metadata()
    {
        var clock = new OperationClock(); await using var f = new Fixture(clock: clock); var row = await OperationCheckpoint(f, OperationBytes()); clock.Advance(TimeSpan.FromHours(25)); var before = File.ReadAllBytes(OperationJournal(f));
        using var cancel = new CancellationTokenSource(); var checks = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Grants.Uploads.ReviewOperationAsync(row.Id, () => { if (++checks == 2) cancel.Cancel(); return true; }, cancel.Token));
        Assert.Equal(before, File.ReadAllBytes(OperationJournal(f))); var review = await f.Grants.Uploads.ReviewOperationAsync(row.Id, () => true); f.Grants.Uploads.CancelOperationReview(review.Id);
    }
    [Fact]
    public async Task Restart_drops_an_unconfirmed_offer_and_history_rotation_preserves_its_partial()
    {
        var clock = new OperationClock(); await using var f = new Fixture(clock: clock); var row = await OperationCheckpoint(f, OperationBytes()); clock.Advance(TimeSpan.FromHours(25));
        var review = await f.Grants.Uploads.ReviewOperationAsync(row.Id, () => true); await f.Restart();
        await Assert.ThrowsAnyAsync<IOException>(() => f.Grants.Uploads.ConfirmOperationAsync(review));
        review = await f.Grants.Uploads.ReviewOperationAsync(row.Id, () => true); f.Grants.Uploads.ArchiveHistory();
        await Assert.ThrowsAnyAsync<IOException>(() => f.Grants.Uploads.ConfirmOperationAsync(review)); Assert.Single(Directory.GetFiles(f.Destination));
    }
    [Fact]
    public async Task An_expired_recorded_publication_is_reconciled_without_creating_another_file()
    {
        var clock = new OperationClock(); await using var f = new Fixture(clock: clock); var bytes = OperationBytes(); var row = await OperationCheckpoint(f, bytes); await f.Write(row, bytes, StorageRules.MaxReadRange);
        var done = await f.Finish(row); Assert.Equal(UploadPhase.UploadCompleted, done.Phase);
        var journal = JsonNode.Parse(File.ReadAllText(OperationJournal(f)))!; var saved = journal["Rows"]![0]!; saved["Phase"] = (int)UploadPhase.UploadReview; saved["SecurityPolicy"] = 0;
        File.WriteAllText(OperationJournal(f), journal.ToJsonString()); await f.Restart(); clock.Advance(TimeSpan.FromHours(25));
        var review = await f.Grants.Uploads.ReviewOperationAsync(row.Id, () => true); Assert.Equal(done.FinalName, review.PublishedName); await f.Grants.Uploads.ConfirmOperationAsync(review);
        var reconciled = await f.Finish(row); Assert.Equal(UploadPhase.UploadCompleted, reconciled.Phase); Assert.Equal(done.FinalName, reconciled.FinalName);
        Assert.Single(Directory.GetFiles(f.Destination)); Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(f.Destination, reconciled.FinalName)));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public async Task An_empty_native_checkpoint_can_be_reviewed_without_starting_receiving(int size)
    {
        var clock = new OperationClock(); await using var f = new Fixture(clock: clock); await f.Initialize(); var bytes = RandomNumberGenerator.GetBytes(size); var row = await f.Begin(Fixture.Descriptor("empty-checkpoint.bin", bytes));
        var part = OperationPart(f); clock.Advance(TimeSpan.FromHours(25)); var review = await f.Grants.Uploads.ReviewOperationAsync(row.Id, () => true); await f.Grants.Uploads.ConfirmOperationAsync(review);
        Assert.Equal(0, f.Grants.Uploads.All.Single().Offset); Assert.Equal(0, new FileInfo(part).Length);
        await f.Write(row, bytes); Assert.Equal(UploadPhase.UploadCompleted, (await f.Finish(row)).Phase); Assert.Single(Directory.GetFiles(f.Destination));
    }
    [Fact]
    public async Task Missing_staging_is_preserved_as_missing_and_cannot_be_recreated_by_window_review()
    {
        var clock = new OperationClock(); await using var f = new Fixture(clock: clock); var row = await OperationCheckpoint(f, OperationBytes()); clock.Advance(TimeSpan.FromHours(25));
        var path = OperationPart(f); File.Delete(path); var before = File.ReadAllBytes(OperationJournal(f));
        await Assert.ThrowsAnyAsync<IOException>(() => f.Grants.Uploads.ReviewOperationAsync(row.Id, () => true)); Assert.False(File.Exists(path)); Assert.Equal(before, File.ReadAllBytes(OperationJournal(f)));
    }
    [Theory]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    public async Task Unsupported_persisted_window_authority_blocks_the_queue_without_removing_bytes(long invalid)
    {
        var clock = new OperationClock(); await using var f = new Fixture(clock: clock); await OperationCheckpoint(f, OperationBytes());
        var json = JsonNode.Parse(File.ReadAllText(OperationJournal(f)))!; json["Rows"]![0]!["OperationApprovedAt"] = invalid; File.WriteAllText(OperationJournal(f), json.ToJsonString());
        var reopened = new StorageGrantStore(f.Profile, clock); Assert.NotEmpty(reopened.Uploads.RecoveryIssues); Assert.Single(Directory.GetFiles(f.Destination));
    }
    [Fact]
    public async Task Legacy_receipts_keep_their_original_deadline_without_implied_renewal()
    {
        var clock = new OperationClock(); await using var f = new Fixture(clock: clock); var bytes = OperationBytes(); var row = await OperationCheckpoint(f, bytes);
        var json = JsonNode.Parse(File.ReadAllText(OperationJournal(f)))!; json["Rows"]![0]!.AsObject().Remove("OperationApprovedAt"); File.WriteAllText(OperationJournal(f), json.ToJsonString());
        await f.Restart(); Assert.Equal(MutationRules.CreatedAt(row.Id) + MutationRules.Lifetime, f.Grants.Uploads.All.Single().OperationExpiresAt);
        clock.Advance(TimeSpan.FromHours(25)); Assert.Equal(FileStatus.StaleReference, (await OperationWrite(f, row, bytes, StorageRules.MaxReadRange)).Status);
    }
}
