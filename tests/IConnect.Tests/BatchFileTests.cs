using Google.Protobuf;
using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Xunit;

namespace IConnect.Tests;

public sealed partial class BatchPlanAdmissionTests
{
    private sealed class AcceptedFiles : IAttachmentChecks
    { public Task CheckAsync(string path, string identity, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; } }
    private sealed class HeldFiles : IAttachmentChecks
    {
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Active, Peak;
        public Action? Canceled;
        public async Task CheckAsync(string path, string identity, CancellationToken ct)
        {
            var active = Interlocked.Increment(ref Active); int previous;
            do { previous = Volatile.Read(ref Peak); if (previous >= active) break; } while (Interlocked.CompareExchange(ref Peak, active, previous) != previous);
            try { await Release.Task.WaitAsync(ct); } catch (OperationCanceledException) { Canceled?.Invoke(); throw; } finally { Interlocked.Decrement(ref Active); }
        }
    }
    private static UploadBatchManifest FilePlan(byte[] bytes, int files = 1, UploadCollision collision = UploadCollision.UploadKeepBoth)
    {
        var manifest = new UploadBatchManifest { Collision = collision };
        manifest.Items.Add(new UploadBatchItem { Parent = -1, Name = "folder", IsDirectory = true });
        for (var i = 0; i < files; i++) manifest.Items.Add(new UploadBatchItem { Parent = 0, Name = "file-" + i + ".bin", Size = bytes.Length, Sha256 = ByteString.CopyFrom(SHA256.HashData(bytes)) });
        return manifest;
    }
    private static async Task<UploadInfo> BatchFileStatus(Fixture f, UploadBatchHeader h, int index = 1) => (await f.Call(ChildRequest(f, h, index, FileOperation.UploadBatchItemStatus))).Upload;
    private static async Task<UploadInfo> FinishFile(Fixture f, UploadBatchHeader h, int index = 1)
    {
        await f.Call(ChildRequest(f, h, index, FileOperation.FinishBatchFile));
        await f.Grants.BatchPlans.WhenFilesIdleAsync(); return await BatchFileStatus(f, h, index);
    }
    private static async Task WriteFile(Fixture f, UploadBatchHeader h, byte[] bytes, int index = 1, int start = 0)
    {
        for (var offset = start; offset < bytes.Length; offset += StorageRules.MaxReadRange)
        {
            var request = ChildRequest(f, h, index, FileOperation.WriteBatchFile); request.Offset = offset;
            request.Content = ByteString.CopyFrom(bytes, offset, Math.Min(StorageRules.MaxReadRange, bytes.Length - offset));
            Assert.Equal(offset + request.Content.Length, (await f.Call(request)).Upload.Offset);
        }
    }
    [Fact]
    public async Task Batch_receiver_measures_only_new_saved_ranges_and_resets_on_disconnect()
    {
        await using var f = new Fixture(checks: new AcceptedFiles());
        const int range = StorageRules.MaxReadRange;
        const long limit = 64 * 1024;
        var bytes = Enumerable.Range(0, 4 * range + 7).Select(i => (byte)(i % 251)).ToArray();
        var h = await ReadyDirectories(f, FilePlan(bytes));
        await f.Call(ChildRequest(f, h, 0));
        await f.Call(ChildRequest(f, h, 1, FileOperation.PrepareBatchFile));
        IncomingBatchFileProgress Progress() => Assert.Single(f.Grants.BatchPlans.ReadFileProgress(
            f.Grants.BatchPlans.All.Single(x => x.Id == h.Id), limit));
        async Task WriteRange(int offset)
        {
            var request = ChildRequest(f, h, 1, FileOperation.WriteBatchFile);
            request.Offset = offset; request.Content = ByteString.CopyFrom(bytes, offset, range);
            Assert.True((await f.Call(request)).Upload.Offset >= offset + range);
        }

        Assert.Null(Progress().Estimate);
        await WriteRange(0);
        Assert.Equal(PayloadRateState.Measuring, Progress().Estimate?.State);
        f.Time.PayloadElapsed += TimeSpan.FromSeconds(2);
        await WriteRange(0);
        Assert.Equal(PayloadRateState.Measuring, Progress().Estimate?.State);
        await WriteRange(range);
        var active = Progress();
        Assert.Equal(1u, active.Index);
        Assert.Equal(2L * range, active.Offset);
        const long expectedRate = range / 2;
        Assert.Equal(new PayloadRateEstimate(PayloadRateState.Available, expectedRate,
            (bytes.Length - active.Offset + expectedRate - 1) / expectedRate), active.Estimate);
        f.Time.PayloadElapsed += TimeSpan.FromSeconds(11);
        await WriteRange(range);
        Assert.Equal(PayloadRateState.Waiting, Progress().Estimate?.State);
        f.Service.Dispose();
        Assert.Equal(2L * range, Progress().Offset);
        Assert.Null(Progress().Estimate);

        await f.Reopen();
        Assert.Equal(2L * range, (await BatchFileStatus(f, h)).Offset);
        await WriteRange(2 * range);
        Assert.Equal(PayloadRateState.Measuring, Progress().Estimate?.State);
        await WriteFile(f, h, bytes, start: 3 * range);
        Assert.Equal(bytes.Length, Progress().Offset);
        Assert.Null(Progress().Estimate);
        var done = await FinishFile(f, h);
        Assert.Equal(UploadPhase.UploadCompleted, done.Phase);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(f.Destination, "folder", done.FinalName)));
        Assert.Empty(f.Grants.BatchPlans.ReadFileProgress(f.Grants.BatchPlans.All.Single(x => x.Id == h.Id)));
    }

    [Fact]
    public async Task Scoped_file_resumes_after_restart_and_publishes_actual_verified_untrusted_bytes()
    {
        await using var f = new Fixture(); var bytes = Enumerable.Range(0, 98_317).Select(i => (byte)(i * 17 % 251)).ToArray(); var h = await ReadyDirectories(f, FilePlan(bytes));
        Assert.Equal(FileStatus.Conflict, (await f.Raw(ChildRequest(f, h, 1, FileOperation.PrepareBatchFile))).Status);
        await f.Call(ChildRequest(f, h, 0)); var prepared = await f.Call(ChildRequest(f, h, 1, FileOperation.PrepareBatchFile));
        Assert.False(prepared.BatchItem.IsDirectory); Assert.Equal(UploadPhase.UploadReceiving, prepared.Upload.Phase); Assert.Equal(0, MutationRules.CreatedAt(prepared.Upload.Id));
        var first = ChildRequest(f, h, 1, FileOperation.WriteBatchFile); first.Content = ByteString.CopyFrom(bytes, 0, StorageRules.MaxReadRange);
        await f.Call(first); Assert.Equal(StorageRules.MaxReadRange, (await f.Call(first)).Upload.Offset);
        first.Content = ByteString.CopyFrom(new byte[StorageRules.MaxReadRange]); Assert.Equal(FileStatus.Conflict, (await f.Raw(first)).Status);
        await f.Reopen(); Assert.Equal(StorageRules.MaxReadRange, (await BatchFileStatus(f, h)).Offset); await WriteFile(f, h, bytes, start: StorageRules.MaxReadRange);
        var done = await FinishFile(f, h); Assert.Equal(UploadPhase.UploadCompleted, done.Phase);
        var path = Path.Combine(f.Destination, "folder", done.FinalName); Assert.Equal(bytes, File.ReadAllBytes(path)); Assert.Contains("ZoneId=4", File.ReadAllText(path + ":Zone.Identifier"));
        Assert.Equal(ReceivedFileSecurity.PolicyVersion, new WindowsUploadsSnapshot(f, done.Id).SecurityPolicy);
        Assert.Empty(f.Grants.Uploads.All); await f.Reopen(); Assert.Equal(done, await BatchFileStatus(f, h));
    }
    // Read only the test's private scoped journal; no public ordinary-upload row may own this ID.
    private sealed class WindowsUploadsSnapshot
    {
        public int SecurityPolicy { get; }
        public WindowsUploadsSnapshot(Fixture f, string id)
        {
            var files = Directory.EnumerateFiles(Path.Combine(f.Profile, "incoming-batch-plans"), "*.upload.json");
            var cache = files.Select(file => JsonNode.Parse(File.ReadAllText(file))!).Single(value => value["Rows"]![0]!["Id"]!.GetValue<string>() == id);
            SecurityPolicy = cache["Rows"]![0]!["SecurityPolicy"]!.GetValue<int>();
        }
    }
    [Fact]
    public async Task File_descriptors_indices_and_parent_bindings_cannot_change_the_accepted_intent()
    {
        await using var f = new Fixture(checks: new AcceptedFiles()); var bytes = "salama"u8.ToArray(); var h = await ReadyDirectories(f, FilePlan(bytes)); await f.Call(ChildRequest(f, h, 0));
        var request = ChildRequest(f, h, 1, FileOperation.PrepareBatchFile); request.Upload = new() { Id = MutationRules.NewId(), Name = "other.bin", Size = 1, Sha256 = ByteString.CopyFrom(SHA256.HashData([1])) };
        Assert.Equal(FileStatus.InvalidRequest, (await f.Raw(request)).Status); Assert.Empty(Directory.EnumerateFiles(Path.Combine(f.Destination, "folder")));
        Assert.Equal(FileStatus.NotFound, (await f.Raw(ChildRequest(f, h, 1, FileOperation.UploadBatchItemStatus))).Status);
        f.Grants.Update(f.Grant.Id, StoragePermission.Contribute, ["folder/file-0.bin"], null, true);
        Assert.Equal(FileStatus.AccessDenied, (await f.Raw(ChildRequest(f, h, 1, FileOperation.PrepareBatchFile))).Status);
        Assert.False(File.Exists(Path.Combine(f.Profile, "incoming-batch-plans", h.Id + ".1.json"))); f.Grants.Update(f.Grant.Id, StoragePermission.Contribute, [], null, true);
        request = ChildRequest(f, h, 0, FileOperation.PrepareBatchFile); Assert.Equal(FileStatus.Unsupported, (await f.Raw(request)).Status);
        var prepared = await f.Call(ChildRequest(f, h, 1, FileOperation.PrepareBatchFile));
        var ordinary = new FileRequest { Operation = FileOperation.PrepareUpload, TransferId = prepared.Upload.Id, Upload = prepared.Upload.Clone(), DestinationId = (await f.Call(new() { Operation = FileOperation.UploadContext })).Entries.Single().Id,
            MutationEpoch = (await f.Call(new() { Operation = FileOperation.UploadContext })).MutationEpoch };
        ordinary.Upload.Phase = UploadPhase.UploadPending; ordinary.Upload.Message = ""; Assert.Equal(FileStatus.StaleReference, (await f.Raw(ordinary)).Status);
        var before = Directory.GetFiles(Path.Combine(f.Destination, "folder")); Directory.Move(Path.Combine(f.Destination, "folder"), Path.Combine(f.Destination, "moved")); Directory.CreateDirectory(Path.Combine(f.Destination, "folder"));
        Assert.Equal(FileStatus.StaleReference, (await f.Raw(ChildRequest(f, h, 1, FileOperation.UploadBatchItemStatus))).Status); Assert.Empty(Directory.GetFiles(Path.Combine(f.Destination, "folder"))); Assert.Single(before);
    }
    [Theory]
    [InlineData("journal")]
    [InlineData("binding")]
    [InlineData("index")]
    [InlineData("corrupt")]
    [InlineData("binding-corrupt")]
    [InlineData("manifest")]
    public async Task Missing_or_changed_file_ownership_blocks_runtime_and_cold_replay(string kind)
    {
        await using var f = new Fixture(checks: new AcceptedFiles()); var h = await ReadyDirectories(f, FilePlan([1, 2, 3])); await f.Call(ChildRequest(f, h, 0)); await f.Call(ChildRequest(f, h, 1, FileOperation.PrepareBatchFile));
        var directory = Path.Combine(f.Profile, "incoming-batch-plans");
        var path = Path.Combine(directory, kind is "journal" or "corrupt" ? h.Id + ".1.upload.json" : kind is "binding" or "binding-corrupt" ? h.Id + ".1.json" : kind == "manifest" ? h.Id + ".json" : h.Id + ".children.json");
        if (kind is "corrupt" or "binding-corrupt" or "manifest") File.WriteAllText(path, "{broken"); else File.Delete(path);
        Assert.NotEqual(FileStatus.Ok, (await f.Raw(ChildRequest(f, h, 1, FileOperation.PrepareBatchFile))).Status); Assert.NotEmpty(f.Grants.BatchPlans.RecoveryIssues);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(f.Destination, "folder"))); await f.Reopen(); Assert.NotEmpty(f.Grants.BatchPlans.RecoveryIssues);
        Assert.NotEqual(FileStatus.Ok, (await f.Raw(ChildRequest(f, h, 1, FileOperation.PrepareBatchFile))).Status); Assert.Single(Directory.EnumerateFiles(Path.Combine(f.Destination, "folder")));
        f.Grants.BatchPlans.ArchiveHistory(); Assert.Single(Directory.EnumerateFiles(Path.Combine(f.Destination, "folder")));
        Assert.Equal(FileStatus.StaleReference, (await f.Raw(ChildRequest(f, h, 1, FileOperation.UploadBatchItemStatus))).Status);
    }
    [Fact]
    public async Task Cancel_removes_only_the_identified_partial_and_never_restarts_the_same_child()
    {
        await using var f = new Fixture(checks: new AcceptedFiles()); var h = await ReadyDirectories(f, FilePlan([1, 2, 3])); await f.Call(ChildRequest(f, h, 0)); await f.Call(ChildRequest(f, h, 1, FileOperation.PrepareBatchFile));
        Assert.Equal(UploadPhase.UploadCanceled, (await f.Call(ChildRequest(f, h, 1, FileOperation.CancelBatchFile))).Upload.Phase); Assert.Empty(Directory.EnumerateFiles(Path.Combine(f.Destination, "folder")));
        Assert.Equal(UploadPhase.UploadCanceled, (await f.Call(ChildRequest(f, h, 1, FileOperation.PrepareBatchFile))).Upload.Phase); await f.Reopen(); Assert.Equal(UploadPhase.UploadCanceled, (await BatchFileStatus(f, h)).Phase);
    }
    [Fact]
    public async Task File_children_obey_current_permissions_exclusions_and_pairing_generation()
    {
        await using var f = new Fixture(owner: true, checks: new AcceptedFiles()); var h = await ReadyDirectories(f, FilePlan([1, 2, 3]));
        await f.Call(ChildRequest(f, h, 0)); await f.Call(ChildRequest(f, h, 1, FileOperation.PrepareBatchFile)); await WriteFile(f, h, [1, 2, 3]);
        f.Grants.Update(f.Grant.Id, StoragePermission.ReadOnly, [], null, true);
        Assert.Equal(FileStatus.AccessDenied, (await f.Raw(ChildRequest(f, h, 1, FileOperation.FinishBatchFile))).Status);
        f.Grants.Update(f.Grant.Id, StoragePermission.Contribute, ["folder"], null, true);
        Assert.Equal(FileStatus.AccessDenied, (await f.Raw(ChildRequest(f, h, 1, FileOperation.UploadBatchItemStatus))).Status);
        f.Grants.Update(f.Grant.Id, StoragePermission.Contribute, [], null, true); f.Pairing = ProtocolRules.NewToken(); await f.Reopen();
        Assert.Equal(FileStatus.AccessDenied, (await f.Raw(ChildRequest(f, h, 1, FileOperation.FinishBatchFile))).Status);
        Assert.Single(Directory.GetFiles(Path.Combine(f.Destination, "folder"))); Assert.Empty(Directory.GetFiles(Path.Combine(f.Destination, "folder"), "*.bin"));
    }
    [Theory]
    [InlineData(UploadCollision.UploadKeepBoth, UploadPhase.UploadCompleted)]
    [InlineData(UploadCollision.UploadSkip, UploadPhase.UploadSkipped)]
    [InlineData(UploadCollision.UploadFail, UploadPhase.UploadReview)]
    public async Task Scoped_file_collisions_preserve_owner_content(UploadCollision collision, UploadPhase expected)
    {
        await using var f = new Fixture(checks: new AcceptedFiles()); var bytes = "new bytes"u8.ToArray(); var h = await ReadyDirectories(f, FilePlan(bytes, collision: collision)); await f.Call(ChildRequest(f, h, 0));
        var original = Path.Combine(f.Destination, "folder", "file-0.bin"); File.WriteAllText(original, "owner content"); await f.Call(ChildRequest(f, h, 1, FileOperation.PrepareBatchFile)); await WriteFile(f, h, bytes);
        var done = await FinishFile(f, h); Assert.Equal(expected, done.Phase); Assert.Equal("owner content", File.ReadAllText(original));
        if (expected == UploadPhase.UploadCompleted) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(f.Destination, "folder", done.FinalName)));
    }
    [Fact]
    public async Task Batch_files_and_ordinary_uploads_share_two_verification_slots_and_stop_on_scope_close()
    {
        var checks = new HeldFiles(); await using var f = new Fixture(checks: checks);
        try
        {
            var bytes = new byte[] { 7 }; var h = await ReadyDirectories(f, FilePlan(bytes, 2)); await f.Call(ChildRequest(f, h, 0));
            var normalContext = await f.Call(new() { Operation = FileOperation.UploadContext }); var ordinary = new UploadInfo { Id = MutationRules.NewId(), Name = "ordinary.bin", Size = 1, Sha256 = ByteString.CopyFrom(SHA256.HashData(bytes)) };
            FileRequest Normal(FileOperation op) => new() { Operation = op, TransferId = ordinary.Id, MutationEpoch = normalContext.MutationEpoch };
            var prepare = Normal(FileOperation.PrepareUpload); prepare.DestinationId = normalContext.Entries.Single().Id; prepare.Upload = ordinary; await f.Call(prepare);
            var write = Normal(FileOperation.UploadWrite); write.Content = ByteString.CopyFrom(bytes); await f.Call(write); await f.Call(Normal(FileOperation.FinishUpload));
            await f.Call(ChildRequest(f, h, 1, FileOperation.PrepareBatchFile)); await WriteFile(f, h, bytes); await f.Call(ChildRequest(f, h, 1, FileOperation.FinishBatchFile));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)); while (Volatile.Read(ref checks.Active) != 2) await Task.Delay(10, deadline.Token);
            var plan = Assert.Single(f.Grants.BatchPlans.All);
            var batchCheck = Assert.Single(f.Grants.BatchPlans.ReadFileProgress(plan)).Finalization;
            var ordinaryCheck = f.Grants.Uploads.ReadFinalization(Assert.Single(f.Grants.Uploads.All));
            Assert.Equal(IncomingFinalizationStage.AttachmentCheck, batchCheck?.Stage);
            Assert.Equal(IncomingFinalizationStage.AttachmentCheck, ordinaryCheck?.Stage);
            Assert.Null(batchCheck!.Estimate);
            Assert.Null(ordinaryCheck!.Estimate);
            await f.Call(ChildRequest(f, h, 2, FileOperation.PrepareBatchFile)); await WriteFile(f, h, bytes, 2);
            Assert.Equal(FileStatus.ResourceExhausted, (await f.Raw(ChildRequest(f, h, 2, FileOperation.FinishBatchFile))).Status);
            Assert.Throws<IOException>(f.Grants.BatchPlans.ArchiveHistory);
            f.Service.Dispose(); await f.Grants.BatchPlans.WhenFilesIdleAsync(); Assert.Equal(1, Volatile.Read(ref checks.Active));
            Assert.All(f.Grants.BatchPlans.ReadFileProgress(plan), row => Assert.Null(row.Finalization));
            checks.Release.TrySetResult(); await f.Grants.Uploads.WhenIdleAsync(); await f.Reopen();
            Assert.Equal(UploadPhase.UploadReview, (await BatchFileStatus(f, h)).Phase);
            Assert.Equal(UploadPhase.UploadCompleted, (await FinishFile(f, h)).Phase); Assert.Equal(UploadPhase.UploadCompleted, (await FinishFile(f, h, 2)).Phase); Assert.Equal(2, checks.Peak);
        }
        finally { checks.Release.TrySetResult(); }
    }
    [Fact]
    public async Task Scoped_file_reservation_is_owned_once_by_its_parent_plan()
    {
        await using var f = new Fixture(checks: new AcceptedFiles()); var bytes = new byte[] { 1, 2, 3 }; var context = await f.Call(new() { Operation = FileOperation.UploadContext });
        var descriptor = new UploadInfo { Id = MutationRules.NewId(), Name = "reserved.bin", Size = UploadRules.PeerQuota - bytes.Length, Sha256 = ByteString.CopyFrom(new byte[32]) };
        await f.Call(new() { Operation = FileOperation.PrepareUpload, MutationEpoch = context.MutationEpoch, TransferId = descriptor.Id, DestinationId = context.Entries.Single().Id, Upload = descriptor });
        var h = await ReadyDirectories(f, FilePlan(bytes)); await f.Call(ChildRequest(f, h, 0)); await f.Call(ChildRequest(f, h, 1, FileOperation.PrepareBatchFile)); await WriteFile(f, h, bytes);
        Assert.Equal(bytes.Length, f.Grants.BatchPlans.ReservedBytes(Fixture.Peer)); Assert.Equal(UploadRules.PeerQuota - bytes.Length, f.Grants.Uploads.ReservedBytes(Fixture.Peer));
        Assert.Equal(UploadPhase.UploadCompleted, (await FinishFile(f, h)).Phase);
    }
    [Fact]
    public async Task Batch_destination_depth_limit_is_enforced_before_admission_and_the_boundary_reopens()
    {
        await using var f = new Fixture(checks: new AcceptedFiles()); var segments = Enumerable.Repeat("d", 64).ToArray(); var deep = Path.Combine(f.Destination, Path.Combine(segments)); Directory.CreateDirectory(Path.Combine(deep, "d"));
        using var selected = SecureWindowsDirectory.OpenPath(f.Grant.CanonicalPath, f.Grant.RootIdentity, segments, true);
        var target = new UploadDestination(f.Grant.Id, f.Grant.CanonicalPath, f.Grant.RootIdentity, segments, selected.Info.Identity); var manifest = FilePlan([1]);
        var epoch = (await f.Call(new() { Operation = FileOperation.UploadBatchContext })).MutationEpoch; var h = UploadBatchRules.Header(MutationRules.NewId(), epoch, f.Grant.Id, manifest);
        var begin = f.Request(h, FileOperation.BeginUploadBatch); begin.BatchHeader = h;
        using var tooDeep = selected.OpenChild("d", true); var invalid = target with { Path = [.. segments, "d"], Identity = tooDeep.Info.Identity };
        Assert.Equal(FileStatus.ResourceExhausted, Assert.Throws<StorageException>(() => f.Grants.WithAuthorization(() => f.Grants.BatchPlans.Execute(Fixture.Peer, "local", "depth-fixture", begin, () => invalid, default))).Status);
        Assert.Empty(f.Grants.BatchPlans.All);
        f.Grants.WithAuthorization(() => f.Grants.BatchPlans.Execute(Fixture.Peer, "local", "depth-fixture", begin, () => target, default));
        await f.Upload(h, manifest); await f.Call(f.Request(h, FileOperation.SealUploadBatch)); await f.Call(ChildRequest(f, h, 0));
        await f.Call(ChildRequest(f, h, 1, FileOperation.PrepareBatchFile));
        await f.Reopen(); Assert.Empty(f.Grants.BatchPlans.RecoveryIssues);
        Assert.Equal(UploadPhase.UploadReceiving, (await BatchFileStatus(f, h)).Phase);
    }
    [Fact]
    public async Task Full_4096_item_file_plan_keeps_real_parts_and_completed_receipts_beyond_ordinary_limits()
    {
        await using var f = new Fixture(checks: new AcceptedFiles()); var bytes = new byte[] { 17, 31, 47 }; var manifest = FilePlan(bytes, 4095); var h = await ReadyDirectories(f, manifest); await f.Call(ChildRequest(f, h, 0));
        for (var i = 1; i < 4096; i++)
        {
            await f.Call(ChildRequest(f, h, i, FileOperation.PrepareBatchFile)); await WriteFile(f, h, bytes, i);
            var done = await FinishFile(f, h, i); Assert.True(done.Phase == UploadPhase.UploadCompleted, $"Item {i}: {done.Phase}: {done.Message}");
        }
        Assert.Equal(4095, Directory.EnumerateFiles(Path.Combine(f.Destination, "folder")).Count()); Assert.Empty(f.Grants.Uploads.All);
        var ended = (await f.Call(f.Request(h, FileOperation.FinishUploadBatch))).Batch; Assert.Equal(UploadBatchPlanPhase.UploadPlanCompleted, ended.Phase);
        Assert.Equal(4096, ended.ItemOutcomes.Length); Assert.All(ended.ItemOutcomes, value => Assert.Equal(1, value)); Assert.Equal(0, f.Grants.BatchPlans.ReservedBytes(null));
        await f.Reopen(); Assert.Empty(f.Grants.BatchPlans.RecoveryIssues); Assert.Equal(UploadPhase.UploadCompleted, (await BatchFileStatus(f, h, 4095)).Phase);
        Assert.Equal(ended, (await f.Call(f.Request(h, FileOperation.FinishUploadBatch))).Batch);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(f.Destination, "folder", (await BatchFileStatus(f, h, 4095)).FinalName)));
    }
}
