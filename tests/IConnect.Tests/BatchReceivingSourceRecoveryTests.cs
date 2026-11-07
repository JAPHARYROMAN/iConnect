using IConnect.Core;
using IConnect.Protocol;
using IConnect.Connectivity;
using IConnect.Storage.Windows;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace IConnect.Tests;

public sealed class BatchReceivingSourceRecoveryTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly DownloadSourceRecoveryTests.Fixture Data;
        internal readonly string Peer;
        internal WindowsBatchDownloads Batches;
        internal string Batch = "";
        internal int Index;
        internal bool Current = true;
        internal Fixture(string? servicePeer = null, string? receivingPeer = null) { Data = new(servicePeer); Peer = receivingPeer ?? Data.Peer; Batches = new(Data.Profile, Data.Receiver); }
        internal BatchDownload Parent => Batches.Jobs.Single(x => x.Id == Batch);
        internal DownloadJob Child => Data.Receiver.Jobs.Single(x => x.Id == Parent.Items[Index].DownloadId);
        internal string OriginalFile => Path.Combine(Data.Source, "generated-文書.bin");
        internal async Task Pause(bool nested = false, bool beforePreparation = false)
        {
            if (nested) { Directory.CreateDirectory(Path.Combine(Data.Source, "folder")); File.Move(OriginalFile, Path.Combine(Data.Source, "folder", Path.GetFileName(OriginalFile))); }
            var root = (await Data.Request(new() { Operation = FileOperation.ListRoots }, default)).Entries.Single();
            var selected = (await Data.Request(new() { Operation = FileOperation.ListDirectory, NodeId = root.Id }, default)).Entries.Single();
            var plan = await TransferManifests.BuildAsync([new(selected, [selected.Name])], Data.Request, default); Index = Array.FindIndex(plan.Items, x => !x.Directory);
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<FileResponse> Rpc(FileRequest request, CancellationToken ct)
            {
                if (beforePreparation && request.Operation == FileOperation.PrepareDownload) throw new IOException("Generated unavailable preparation");
                if (request.Operation == FileOperation.DownloadRead && request.Offset >= 1024 * 1024) { reached.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
                return await Data.Request(request, ct);
            }
            Batch = Batches.Start(Peer, plan, Data.Destination, DownloadCollision.KeepBoth, Rpc);
            if (!beforePreparation) { await reached.Task.WaitAsync(TimeSpan.FromSeconds(20)); Batches.Pause(Batch); }
            await Batches.WaitAsync(Batch).WaitAsync(TimeSpan.FromSeconds(20)); Assert.Equal(beforePreparation ? 0 : 1024 * 1024, Child.Offset);
        }
        internal async Task<FileEntry> Selected()
        {
            var root = (await Data.Request(new() { Operation = FileOperation.ListRoots }, default)).Entries.Single();
            var row = (await Data.Request(new() { Operation = FileOperation.ListDirectory, NodeId = root.Id }, default)).Entries.Single();
            return row.IsDirectory ? (await Data.Request(new() { Operation = FileOperation.ListDirectory, NodeId = row.Id }, default)).Entries.Single() : row;
        }
        internal async Task ChangeMetadata()
        {
            var row = await Selected(); var path = File.Exists(OriginalFile) ? OriginalFile : Path.Combine(Data.Source, "folder", Path.GetFileName(OriginalFile));
            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1)); Assert.NotEqual(row.Version, (await Selected()).Version);
        }
        internal async Task<BatchDownloadSourceReview> Review(TransferManifests.Request? rpc = null, CancellationToken ct = default) =>
            await Batches.PrepareSourceReviewAsync(Batch, Index, Peer, await Selected(), rpc ?? Data.Request, () => Current, ct);
        internal async Task Restart()
        { await Batches.DisposeAsync(); await Data.Receiver.DisposeAsync(); Data.Receiver = new(Data.Profile, new WindowsAttachmentChecks(), clock: Data.Time); Batches = new(Data.Profile, Data.Receiver); }
        internal async Task Finish()
        {
            await Batches.Resume(Batch, Data.Request).WaitAsync(TimeSpan.FromSeconds(25)); Assert.Equal("", Parent.Error); Assert.Equal(BatchPhase.Completed, Parent.Phase);
            var file = Parent.Items[Index]; var destination = Index == 0 ? Data.Destination : Path.Combine(Data.Destination, Parent.Items[0].LocalName);
            Assert.Equal(Data.Bytes, File.ReadAllBytes(Path.Combine(destination, file.LocalName))); Assert.Empty(Data.Receiver.Jobs);
        }
        public async ValueTask DisposeAsync() { await Batches.DisposeAsync(); await Data.DisposeAsync(); }
    }
    [Fact]
    public async Task Expired_source_and_changed_metadata_keep_frozen_manifest_part_prefix_and_folder_bindings_after_restart()
    {
        await using var f = new Fixture(); await f.Pause(nested: true); var original = f.Child;
        var sourcePlan = JsonSerializer.Serialize(f.Parent.Items.Select(x => x.Source)); var folder = f.Parent.Items[0];
        f.Data.Time.Advance(TimeSpan.FromHours(25)); await f.ChangeMetadata(); var review = await f.Review(); await f.Batches.ConfirmSourceReviewAsync(review);
        Assert.Equal(sourcePlan, JsonSerializer.Serialize(f.Parent.Items.Select(x => x.Source))); Assert.Equal(folder, f.Parent.Items[0]);
        var next = f.Child; Assert.NotEqual(original.Ticket, next.Ticket); Assert.NotEqual(original.SourceVersion, next.SourceVersion);
        Assert.Equal(original.Offset, next.Offset); Assert.Equal(original.PrefixHash, next.PrefixHash); Assert.Equal(original.TemporaryIdentity, next.TemporaryIdentity);
        Assert.Equal(original.RootIdentity, next.RootIdentity); Assert.Equal(original.Collision, next.Collision); Assert.Equal(original.Phase, next.Phase);
        var receipt = Assert.IsType<BatchSourceApproval>(f.Parent.Items[f.Index].SourceApproval); Assert.Equal(original.SourceVersion, receipt.OriginalVersion); Assert.Equal(next.SourceVersion, receipt.ApprovedVersion);
        await f.Restart(); await f.Finish();
    }
    [Fact]
    public async Task Same_version_renewal_does_not_resume_or_publish_and_keeps_collision_choice()
    {
        await using var f = new Fixture(); await f.Pause(); var child = f.Child;
        File.WriteAllText(Path.Combine(f.Data.Destination, child.Name), "kept original destination"); f.Data.Time.Advance(TimeSpan.FromHours(25));
        var review = await f.Review(); await f.Batches.ConfirmSourceReviewAsync(review);
        Assert.Equal(child.Phase, f.Child.Phase); Assert.Equal(child.Offset, f.Child.Offset); Assert.Single(Directory.EnumerateFiles(f.Data.Destination), x => !Path.GetFileName(x).StartsWith(".iconnect-"));
        await f.Finish(); Assert.NotEqual(child.Name, f.Parent.Items[0].LocalName); Assert.Equal("kept original destination", File.ReadAllText(Path.Combine(f.Data.Destination, child.Name)));
    }
    [Fact]
    public async Task A_changed_last_byte_is_refused_despite_matching_name_size_and_saved_prefix()
    {
        await using var f = new Fixture(); await f.Pause(); var child = f.Child; var parent = JsonSerializer.Serialize(f.Parent);
        using (var file = new FileStream(f.OriginalFile, FileMode.Open, FileAccess.Write)) { file.Position = f.Data.Bytes.Length - 1; file.WriteByte((byte)(f.Data.Bytes[^1] ^ 1)); }
        await Assert.ThrowsAsync<IOException>(() => f.Review()); Assert.Equal(child, f.Child); Assert.Equal(parent, JsonSerializer.Serialize(f.Parent));
    }
    [Fact]
    public async Task Standalone_prepare_and_standalone_confirmation_cannot_bypass_original_parent()
    {
        await using var f = new Fixture(); await f.Pause();
        await Assert.ThrowsAsync<IOException>(async () => await f.Data.Receiver.PrepareSourceReviewAsync(f.Child.Id, f.Data.Peer, await f.Selected(), f.Data.Request, () => true));
        var review = await f.Review(); await Assert.ThrowsAsync<IOException>(() => f.Data.Receiver.ConfirmSourceReviewAsync(review.Source));
        await f.Batches.ConfirmSourceReviewAsync(review); Assert.NotNull(f.Parent.Items[f.Index].SourceApproval);
    }
    [Fact]
    public async Task Cancelled_review_leaves_both_journals_unchanged_and_releases_only_unused_lease()
    {
        await using var f = new Fixture(); await f.Pause(); var child = f.Child; var parent = JsonSerializer.Serialize(f.Parent); var review = await f.Review();
        await Assert.ThrowsAsync<IOException>(() => f.Batches.Resume(f.Batch, f.Data.Request)); await f.Batches.CancelSourceReviewAsync(review.Id);
        Assert.Equal(child, f.Child); Assert.Equal(parent, JsonSerializer.Serialize(f.Parent)); await Assert.ThrowsAsync<IOException>(() => f.Batches.ConfirmSourceReviewAsync(review));
        var fresh = f.Data.Prepared.Last(); Assert.Equal(FileStatus.AccessDenied, (await f.Data.Request(new() { Operation = FileOperation.DownloadStatus, TransferId = fresh }, default)).Status);
        Assert.Equal(FileStatus.Ok, (await f.Data.Request(new() { Operation = FileOperation.DownloadStatus, TransferId = child.Ticket }, default)).Status);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expired_review_or_changed_connection_does_not_write_a_parent_receipt(bool disconnect)
    {
        await using var f = new Fixture(); await f.Pause(); var child = f.Child; var review = await f.Review();
        if (disconnect) f.Current = false; else f.Data.Time.Advance(TimeSpan.FromMinutes(6));
        await Assert.ThrowsAsync<IOException>(() => f.Batches.ConfirmSourceReviewAsync(review)); Assert.Equal(child, f.Child); Assert.Null(f.Parent.Items[0].SourceApproval);
    }
    [Fact]
    public async Task Parent_cancellation_invalidates_pending_review_and_cleans_only_owned_parts()
    {
        await using var f = new Fixture(); await f.Pause(); var review = await f.Review(); await f.Batches.Cancel(f.Batch).WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<IOException>(() => f.Batches.ConfirmSourceReviewAsync(review)); Assert.Equal(BatchPhase.Canceled, f.Parent.Phase); Assert.Empty(f.Data.Receiver.Jobs);
    }
    [Fact]
    public async Task Parent_cancellation_drains_inflight_source_preparation()
    {
        await using var f = new Fixture(); await f.Pause(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<FileResponse> Request(FileRequest request, CancellationToken ct)
        {
            if (request.Operation == FileOperation.DownloadStatus) { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
            var response = await f.Data.Request(request, ct);
            if (request.Operation == FileOperation.PrepareDownload) { response.Download.State = DownloadState.DownloadPreparing; response.Download.Sha256 = Google.Protobuf.ByteString.Empty; response.Download.PreparedBytes = 0; }
            return response;
        }
        var preparing = f.Review(Request); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); await f.Batches.Cancel(f.Batch).WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparing); Assert.Equal(BatchPhase.Canceled, f.Parent.Phase);
    }
    [Fact]
    public async Task Shutdown_during_status_confirmation_does_not_adopt_or_lose_the_checkpoint()
    {
        await using var f = new Fixture(); await f.Pause(); var child = f.Child; var confirming = false; var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<FileResponse> Request(FileRequest request, CancellationToken ct) { if (confirming && request.Operation == FileOperation.DownloadStatus) { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); } return await f.Data.Request(request, ct); }
        var review = await f.Review(Request); confirming = true; var task = f.Batches.ConfirmSourceReviewAsync(review); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await f.Batches.DisposeAsync(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task); Assert.Equal(child, f.Child);
    }
    [Fact]
    public async Task Failed_child_persistence_keeps_previous_approved_version_recoverable_after_parent_intent_and_restart()
    {
        await using var f = new Fixture(); await f.Pause(); await f.ChangeMetadata(); await f.Batches.ConfirmSourceReviewAsync(await f.Review()); var retained = f.Child;
        await f.ChangeMetadata(); var review = await f.Review(); var blocker = Path.Combine(f.Data.Profile, "downloads", retained.Id + ".json.new"); Directory.CreateDirectory(blocker);
        try { await Assert.ThrowsAnyAsync<IOException>(async () => { try { await f.Batches.ConfirmSourceReviewAsync(review); } catch (UnauthorizedAccessException ex) { throw new IOException("Generated child-journal failure", ex); } }); }
        finally { Directory.Delete(blocker); }
        Assert.Equal(retained, f.Child); Assert.Equal(retained.SourceVersion, f.Parent.Items[0].SourceApproval!.PreviousVersion);
        await f.Restart(); await f.Batches.ConfirmSourceReviewAsync(await f.Review()); await f.Finish();
    }
    [Fact]
    public async Task Destination_folder_replacement_refuses_approval_without_touching_either_folder()
    {
        await using var f = new Fixture(); await f.Pause(nested: true); var child = f.Child; var review = await f.Review(); var path = Path.Combine(f.Data.Destination, f.Parent.Items[0].LocalName);
        Directory.Move(path, path + "-kept"); Directory.CreateDirectory(path);
        await Assert.ThrowsAnyAsync<IOException>(() => f.Batches.ConfirmSourceReviewAsync(review)); Assert.Equal(child, f.Child); Assert.Null(f.Parent.Items[f.Index].SourceApproval);
        Assert.True(Directory.Exists(path + "-kept")); Assert.Empty(Directory.EnumerateFileSystemEntries(path));
    }
    [Fact]
    public async Task Missing_parent_after_restart_cannot_turn_retained_child_into_standalone_authority()
    {
        await using var f = new Fixture(); await f.Pause(); var child = f.Child; await f.Batches.DisposeAsync(); await f.Data.Receiver.DisposeAsync();
        File.Delete(Path.Combine(f.Data.Profile, "batches", f.Batch + ".json"));
        f.Data.Receiver = new(f.Data.Profile, new WindowsAttachmentChecks(), clock: f.Data.Time); f.Batches = new(f.Data.Profile, f.Data.Receiver);
        await Assert.ThrowsAsync<IOException>(async () => await f.Batches.PrepareSourceReviewAsync(f.Batch, 0, f.Data.Peer, await f.Selected(), f.Data.Request, () => true));
        await Assert.ThrowsAsync<IOException>(async () => await f.Data.Receiver.PrepareSourceReviewAsync(child.Id, f.Data.Peer, await f.Selected(), f.Data.Request, () => true)); Assert.Equal(child, Assert.Single(f.Data.Receiver.Jobs));
    }
    [Fact]
    public async Task Empty_checkpoint_without_part_identity_can_approve_changed_metadata_then_pause_and_resume_new_native_part()
    {
        await using var f = new Fixture(); await f.Pause(beforePreparation: true); Assert.Empty(f.Child.Hash); Assert.Empty(f.Child.TemporaryIdentity);
        await f.ChangeMetadata(); var review = await f.Review(); Assert.False(review.Source.OriginalHashKnown); await f.Batches.ConfirmSourceReviewAsync(review);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<FileResponse> Request(FileRequest request, CancellationToken ct) { if (request.Operation == FileOperation.DownloadRead && request.Offset >= 1024 * 1024) { reached.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); } return await f.Data.Request(request, ct); }
        var resumed = f.Batches.Resume(f.Batch, Request); await reached.Task.WaitAsync(TimeSpan.FromSeconds(15)); f.Batches.Pause(f.Batch); await resumed;
        Assert.NotEmpty(f.Child.TemporaryIdentity); await f.Restart(); await f.Finish();
    }
    [Fact]
    public async Task Local_batch_cannot_receive_remote_source_authority()
    {
        await using var f = new Fixture(); await f.Pause(); await using var local = new WindowsBatchDownloads(f.Data.Profile, f.Data.Receiver, true, f.Data.Receiver.OwnershipGate);
        await Assert.ThrowsAsync<IOException>(async () => await local.PrepareSourceReviewAsync(f.Batch, f.Index, f.Data.Peer, await f.Selected(), f.Data.Request, () => true));
    }
    [Fact]
    public async Task Parent_journal_failure_precedes_child_adoption_and_preserves_the_original_receiving_record()
    {
        await using var f = new Fixture(); await f.Pause(); var child = f.Child; var parent = JsonSerializer.Serialize(f.Parent); var review = await f.Review();
        var blocker = Path.Combine(f.Data.Profile, "batches", f.Batch + ".json.new"); Directory.CreateDirectory(blocker);
        try { var error = await Record.ExceptionAsync(() => f.Batches.ConfirmSourceReviewAsync(review)); Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString()); }
        finally { Directory.Delete(blocker); }
        Assert.Equal(child, f.Child); Assert.Equal(parent, JsonSerializer.Serialize(f.Parent));
    }
    [Fact]
    public async Task Corrupt_prefix_after_source_approval_is_still_refused_before_publication()
    {
        await using var f = new Fixture(); await f.Pause(); await f.Batches.ConfirmSourceReviewAsync(await f.Review());
        var part = Directory.GetFiles(f.Data.Destination, ".iconnect-transfer-*.part").Single(); using (var stream = new FileStream(part, FileMode.Open, FileAccess.Write)) stream.WriteByte((byte)(f.Data.Bytes[0] ^ 1));
        await f.Batches.Resume(f.Batch, f.Data.Request); Assert.Equal(BatchPhase.CompletedWithIssues, f.Parent.Phase); Assert.Equal(DownloadPhase.Failed, f.Child.Phase);
        Assert.DoesNotContain(Directory.EnumerateFiles(f.Data.Destination), x => !Path.GetFileName(x).StartsWith(".iconnect-"));
    }
    [Fact]
    public async Task Native_authenticated_batch_source_renewal_keeps_the_original_parent_and_finishes_one_verified_copy()
    {
        using var hostIdentity = DeviceIdentity.Ephemeral("Generated batch source"); using var guestIdentity = DeviceIdentity.Ephemeral("Generated batch receiver");
        await using var f = new Fixture(guestIdentity.Id, hostIdentity.Id); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(65)); var id = ProtocolRules.NewToken();
        using var host = new PeerSession(hostIdentity, guestIdentity.PublicKey, id, true, Path.Combine(f.Data.Sandbox, "host-inbox"), _ => { }, loopbackOnly: true, fileService: f.Data.Service);
        using var guest = new PeerSession(guestIdentity, hostIdentity.PublicKey, id, false, Path.Combine(f.Data.Sandbox, "guest-inbox"), _ => { }, loopbackOnly: true);
        var hostRun = host.RunAsync(timeout.Token); var guestRun = guest.RunAsync(timeout.Token);
        async Task Bridge(PeerSession from, PeerSession to) { await foreach (var signal in from.Rtc.Signals.ReadAllAsync(timeout.Token)) if (signal.Kind == "candidate") to.Rtc.AddCandidate(signal.Body, signal.Mid); else to.Rtc.SetDescription(signal.Body, signal.Kind); }
        var bridges = new[] { Bridge(host, guest), Bridge(guest, host) };
        try
        {
            host.Rtc.Offer(); await Task.WhenAll(host.Authenticated, guest.Authenticated).WaitAsync(timeout.Token); f.Data.Transport = guest.RequestFilesAsync;
            await f.Pause(); var previous = f.Child; f.Data.Time.Advance(TimeSpan.FromHours(25));
            Assert.Equal(FileStatus.AccessDenied, (await f.Data.Request(new() { Operation = FileOperation.DownloadStatus, TransferId = previous.Ticket }, default)).Status);
            await f.ChangeMetadata();
            await f.Batches.ConfirmSourceReviewAsync(await f.Review()); Assert.Equal(previous.TemporaryIdentity, f.Child.TemporaryIdentity); Assert.Equal(hostIdentity.Id, f.Child.PeerId);
            await f.Finish(); Assert.Single(Directory.GetFiles(f.Data.Destination));
        }
        finally { timeout.Cancel(); host.Dispose(); guest.Dispose(); foreach (var task in bridges.Append(hostRun).Append(guestRun)) try { await task; } catch (Exception ex) when (ex is IOException or OperationCanceledException) { } }
    }
    [Theory]
    [InlineData("Hash")]
    [InlineData("Child")]
    [InlineData("OriginalVersion")]
    public async Task Invalid_parent_receipt_preserves_changed_version_child_and_blocks_authority_after_restart(string field)
    {
        await using var f = new Fixture(); await f.Pause(); await f.ChangeMetadata(); await f.Batches.ConfirmSourceReviewAsync(await f.Review()); var child = f.Child;
        await f.Batches.DisposeAsync(); await f.Data.Receiver.DisposeAsync(); var path = Path.Combine(f.Data.Profile, "batches", f.Batch + ".json");
        var json = JsonNode.Parse(File.ReadAllText(path))!; json["Items"]![0]!["SourceApproval"]![field] = field == "Child" ? ProtocolRules.NewToken() : new string('f', field == "Hash" ? 1 : 64);
        var retained = json.ToJsonString(); File.WriteAllText(path, retained);
        f.Data.Receiver = new(f.Data.Profile, new WindowsAttachmentChecks(), clock: f.Data.Time); f.Batches = new(f.Data.Profile, f.Data.Receiver);
        Assert.Empty(f.Batches.Jobs); Assert.Single(f.Batches.RecoveryIssues); Assert.Equal(retained, File.ReadAllText(path)); Assert.Equal(child, Assert.Single(f.Data.Receiver.Jobs));
        await Assert.ThrowsAsync<IOException>(async () => await f.Batches.PrepareSourceReviewAsync(f.Batch, f.Index, f.Peer, await f.Selected(), f.Data.Request, () => true));
        await Assert.ThrowsAsync<IOException>(async () => await f.Data.Receiver.PrepareSourceReviewAsync(child.Id, f.Peer, await f.Selected(), f.Data.Request, () => true));
    }
}
