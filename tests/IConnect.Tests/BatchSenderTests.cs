using Google.Protobuf;
using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using Xunit;

namespace IConnect.Tests;

public sealed partial class BatchSenderTests
{
    private sealed class Accepted : IAttachmentChecks
    { public Task CheckAsync(string path, string identity, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; } }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string workspace;
        public string Root { get; }
        public string Profile => Path.Combine(Root, "sender");
        public string Original => Path.Combine(Root, "original");
        public string Destination => Path.Combine(Root, "received");
        public readonly byte[] Bytes = Enumerable.Range(0, 98_317).Select(x => (byte)(x * 31 % 251)).ToArray();
        public readonly UploadAuthority Authority = new(new string('a', 64), ProtocolRules.NewToken(), new string('b', 64), ProtocolRules.NewToken());
        public bool Allowed = true;
        public string? RecoveryOwnerOverride;
        public readonly OutgoingCopyResources Resources = OutgoingCopyResources.Isolated(OutgoingCopyResources.Batch);
        public WindowsBatchSources Sources;
        public WindowsBatchSender Sender;
        public StorageGrantStore Receiver;
        public WindowsFileService Service;
        public int Calls, Begins, Directories, Writes;
        public long PayloadNow = 10_000;
        public Fixture(bool capacity = false, int extras = 0)
        {
            var path = new DirectoryInfo(AppContext.BaseDirectory); while (path is not null && !File.Exists(Path.Combine(path.FullName, "Directory.Build.props"))) path = path.Parent;
            workspace = path!.FullName; Root = Path.Combine(workspace, ".local", "batch-sender-tests", ProtocolRules.NewToken());
            Directory.CreateDirectory(Original); Directory.CreateDirectory(Destination);
            if (capacity)
            {
                Bytes = [17, 31, 47]; for (var index = 0; index < 4095; index++) File.WriteAllBytes(Path.Combine(Original, $"file-{index:D4}.bin"), Bytes);
            }
            else
            {
                Directory.CreateDirectory(Path.Combine(Original, "empty"));
                File.WriteAllBytes(Path.Combine(Original, "file.bin"), Bytes); File.WriteAllBytes(Path.Combine(Original, "zero.bin"), []);
                for (var index = 0; index < extras; index++) File.WriteAllBytes(Path.Combine(Original, $"extra-{index:D4}.bin"), [17, 31, 47]);
            }
            Sources = OpenSources(); Sender = OpenSender(); Receiver = OpenReceiver();
            Receiver.Add(Authority.Owner, Destination, StoragePermission.Contribute); Service = new(Receiver);
        }
        public bool Current(UploadAuthority authority) => Allowed && authority == Authority;
        private WindowsBatchSources OpenSources() => new(Profile, Current, resources: Resources,
            recoveryOwner: () => RecoveryOwnerOverride ?? Authority.Owner + "." + Authority.OwnerEpoch);
        private WindowsBatchSender OpenSender() => new(Profile, Sources, Current, localOwner: authority => authority == Authority,
            recoveryOwner: () => RecoveryOwnerOverride ?? Authority.Owner + "." + Authority.OwnerEpoch, payloadNow: () => Interlocked.Read(ref PayloadNow));
        private StorageGrantStore OpenReceiver() => new(Path.Combine(Root, "receiver")) { AttachmentChecks = new Accepted() };
        public async Task ReopenSender() { await Sender.DisposeAsync(); Sender = OpenSender(); }
        public async Task ReopenPrivateSources(Action? change = null)
        {
            await Sender.DisposeAsync(); await Sources.DisposeAsync(); change?.Invoke(); Sources = OpenSources(); Sender = OpenSender();
        }
        public async Task ReopenReceiver() { Service.Dispose(); await Receiver.BatchPlans.WhenFilesIdleAsync(); Receiver = OpenReceiver(); Service = new(Receiver); }
        public async Task<FileResponse> Request(FileRequest value, CancellationToken ct)
        {
            Calls++; if (value.Operation == FileOperation.BeginUploadBatch) Begins++; if (value.Operation == FileOperation.CreateBatchDirectory) Directories++; if (value.Operation == FileOperation.WriteBatchFile) Writes++;
            value = value.Clone(); value.RequestId = ProtocolRules.NewToken(); return await Service.ExecuteAsync(Authority.Owner, value, ct);
        }
        public async Task<FileEntry> Target()
        { var response = await Request(new() { Operation = FileOperation.UploadContext }, default); Assert.Equal(FileStatus.Ok, response.Status); return response.Entries.Single(); }
        public async Task<string> Queue()
        { var source = await Sources.PrepareAsync([Original], Authority, UploadCollision.UploadKeepBoth); return Sender.Queue(source); }
        public async ValueTask DisposeAsync()
        {
            await Sender.DisposeAsync(); await Sources.DisposeAsync(); Service.Dispose(); await Receiver.BatchPlans.WhenFilesIdleAsync();
            var expected = Path.GetFullPath(Path.Combine(workspace, ".local", "batch-sender-tests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Root).StartsWith(expected, StringComparison.OrdinalIgnoreCase) || !UploadBatchRules.CanonicalToken(Path.GetFileName(Root))) throw new IOException("Fixture cleanup escaped the workspace.");
            Directory.Delete(Root, true);
        }
    }

    [Fact]
    public async Task Batch_payload_estimate_uses_the_current_file_acknowledgements_and_restarts_from_the_receiver_checkpoint()
    {
        await using var f = new Fixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = deadline.Token;
        var id = await f.Queue();
        var target = await f.Target();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const int range = StorageRules.MaxReadRange;
        var refreshes = 0;
        f.Sender.MetadataChanged += () => Interlocked.Increment(ref refreshes);

        async Task<FileResponse> Withhold(FileRequest request, CancellationToken token)
        {
            var response = await f.Request(request, token);
            if (request.Operation != FileOperation.WriteBatchFile) return response;
            if (request.Offset == 0)
            {
                first.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            else
            {
                second.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            return response;
        }

        var sending = f.Sender.SendAsync(id, target, Withhold, ct);
        await first.Task.WaitAsync(ct);
        var baseline = Assert.IsType<OutgoingBatchPayload>(f.Sender.ReadPayload(id));
        Assert.Equal("file.bin", baseline.Name);
        Assert.Equal(f.Bytes.Length, baseline.Size);
        Assert.Equal(0, baseline.Offset);
        Assert.Equal(PayloadRateState.Measuring, baseline.Estimate.State);
        var initialRefreshes = Volatile.Read(ref refreshes);
        Assert.True(initialRefreshes > 0);
        Interlocked.Add(ref f.PayloadNow, 2500);
        release.TrySetResult();
        await second.Task.WaitAsync(ct);
        var measured = Assert.IsType<OutgoingBatchPayload>(f.Sender.ReadPayload(id));
        Assert.Equal(range, measured.Offset);
        Assert.Equal(range, f.Sender.ReadCheckpoint(id, measured.Index));
        Assert.Null(f.Sender.ReadCheckpoint(id, measured.Index + 1));
        Assert.Equal(initialRefreshes, Volatile.Read(ref refreshes));
        Assert.Equal(PayloadRateState.Available, measured.Estimate.State);
        Assert.Equal(range * 1000L / 2500, measured.Estimate.BytesPerSecond);
        Interlocked.Add(ref f.PayloadNow, 10_001);
        Assert.Equal(PayloadRateState.Waiting, f.Sender.ReadPayload(id)!.Estimate.State);
        f.Sender.ClearPayloadRates();
        Assert.Equal(PayloadRateState.Measuring, f.Sender.ReadPayload(id)!.Estimate.State);

        f.Sender.Pause(id);
        Assert.Null(f.Sender.ReadPayload(id));
        await sending.WaitAsync(ct);
        Assert.True(Volatile.Read(ref refreshes) > initialRefreshes);
        Assert.Equal(range, Assert.Single(f.Sender.All).Offset);
        await f.ReopenSender();
        Assert.Null(f.Sender.ReadPayload(id));
        var checkedResume = false;
        var checkedFinish = false;
        async Task<FileResponse> VerifyResume(FileRequest request, CancellationToken token)
        {
            if (request.Operation == FileOperation.WriteBatchFile && !checkedResume)
            {
                var resumed = Assert.IsType<OutgoingBatchPayload>(f.Sender.ReadPayload(id));
                Assert.Equal(range * 2, resumed.Offset);
                Assert.Equal(PayloadRateState.Measuring, resumed.Estimate.State);
                checkedResume = true;
            }
            if (request.Operation == FileOperation.FinishBatchFile && request.BatchItemIndex == measured.Index)
            {
                var finished = Assert.IsType<OutgoingBatchPayload>(f.Sender.ReadPayload(id));
                Assert.Equal(f.Bytes.Length, finished.Offset);
                Assert.Equal(PayloadRateState.Received, finished.Estimate.State);
                checkedFinish = true;
            }
            return await f.Request(request, token);
        }
        await f.Sender.SendAsync(id, null, VerifyResume, ct);
        Assert.True(checkedResume && checkedFinish);
        Assert.Equal(BatchSendingPhase.Completed, Assert.Single(f.Sender.All).Phase);
        Assert.Null(f.Sender.ReadPayload(id));
        Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Destination, "original", "file.bin")));
    }

    [Fact]
    public async Task Batch_forecast_carries_rate_across_files_and_excludes_a_skipped_branch_on_resume()
    {
        await using var f = new Fixture();
        var first = Path.Combine(f.Original, "first.bin");
        var blocked = Path.Combine(f.Original, "blocked");
        var second = Path.Combine(f.Original, "second.bin");
        var later = Path.Combine(f.Original, "later.bin");
        Directory.CreateDirectory(blocked);
        Directory.CreateDirectory(Path.Combine(f.Destination, "blocked"));
        File.WriteAllBytes(first, new byte[8192]);
        File.WriteAllBytes(Path.Combine(blocked, "excluded.bin"), new byte[40_000]);
        File.WriteAllBytes(second, new byte[4096]);
        File.WriteAllBytes(later, new byte[1024]);
        var source = await f.Sources.PrepareAsync([first, blocked, second, later], f.Authority, UploadCollision.UploadSkip);
        var id = f.Sender.Queue(source);
        var manifest = f.Sources.ReadManifest(source);
        int Index(string name) => manifest.Items.Select((item, index) => (item, index)).Single(x => x.item.Name == name).index;
        var firstIndex = Index("first.bin");
        var secondIndex = Index("second.bin");
        var sampledSecond = false;

        async Task<FileResponse> Observe(FileRequest request, CancellationToken token)
        {
            if (request.Operation == FileOperation.WriteBatchFile)
            {
                Assert.NotEqual((uint)Index("excluded.bin"), request.BatchItemIndex);
                if (request.BatchItemIndex == secondIndex)
                {
                    var current = Assert.IsType<OutgoingBatchPayload>(f.Sender.ReadPayload(id));
                    Assert.Equal(4096 + 1024, current.BatchRemaining);
                    Assert.Equal(PayloadRateState.Measuring, current.BatchEstimate.State);
                }
                Interlocked.Add(ref f.PayloadNow, request.BatchItemIndex == firstIndex ? 1000 : 1500);
            }
            if (request.Operation == FileOperation.FinishBatchFile)
            {
                var current = Assert.IsType<OutgoingBatchPayload>(f.Sender.ReadPayload(id));
                Assert.Equal(PayloadRateState.Received, current.Estimate.State);
                if (request.BatchItemIndex == firstIndex)
                    Assert.Equal(40_000 + 4096 + 1024, current.BatchRemaining);
                if (request.BatchItemIndex == secondIndex)
                {
                    Assert.Equal(1024, current.BatchRemaining);
                    Assert.Equal(PayloadRateState.Available, current.BatchEstimate.State);
                    Assert.Equal((8192 + 4096) * 1000L / 2500, current.BatchEstimate.BytesPerSecond);
                    Assert.Equal(1, current.BatchEstimate.RemainingSeconds);
                    sampledSecond = true;
                    throw new IOException("Fixture interruption before saving the second file.");
                }
            }
            return await f.Request(request, token);
        }

        await f.Sender.SendAsync(id, await f.Target(), Observe);
        Assert.True(sampledSecond);
        Assert.Equal(BatchSendingPhase.Review, Assert.Single(f.Sender.All).Phase);
        Assert.Null(f.Sender.ReadPayload(id));
        await f.ReopenSender();
        var reconciled = false;
        await f.Sender.SendAsync(id, null, (request, token) =>
        {
            if (request.Operation == FileOperation.FinishBatchFile && request.BatchItemIndex == secondIndex)
            {
                var current = Assert.IsType<OutgoingBatchPayload>(f.Sender.ReadPayload(id));
                Assert.Equal(1024, current.BatchRemaining);
                Assert.Equal(PayloadRateState.Measuring, current.BatchEstimate.State);
                reconciled = true;
            }
            return f.Request(request, token);
        });
        Assert.True(reconciled);
        Assert.Equal(BatchSendingPhase.Completed, Assert.Single(f.Sender.All).Phase);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(f.Destination, "blocked")));
        Assert.Equal(1024, new FileInfo(Path.Combine(f.Destination, "later.bin")).Length);
    }

    [Fact]
    public async Task Real_private_batch_sends_empty_nested_and_verified_files_without_reopening_changed_originals()
    {
        await using var f = new Fixture(); var id = await f.Queue(); var target = await f.Target(); File.WriteAllText(Path.Combine(f.Original, "file.bin"), "owner changed the original");
        await f.Sender.SendAsync(id, target, f.Request); var row = Assert.Single(f.Sender.All); Assert.Equal(BatchSendingPhase.Completed, row.Phase); Assert.Equal(row.Items, row.Next);
        Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Destination, "original", "file.bin"))); Assert.Empty(File.ReadAllBytes(Path.Combine(f.Destination, "original", "zero.bin"))); Assert.True(Directory.Exists(Path.Combine(f.Destination, "original", "empty")));
        Assert.Equal("owner changed the original", File.ReadAllText(Path.Combine(f.Original, "file.bin"))); Assert.Empty(f.Receiver.Uploads.All); Assert.Equal(0, f.Receiver.BatchPlans.ReservedBytes(null));
        Assert.All(row.Outcomes!, value => Assert.Equal(1, value)); Assert.Equal(row.Outcomes, row.ReceiverOutcomes); Assert.Equal(1, f.Begins);
        var metadata = File.ReadAllText(Path.Combine(f.Profile, "outgoing-batch-jobs", id + ".json")); Assert.DoesNotContain(f.Original, metadata);
        await f.ReopenSender(); Assert.Empty(f.Sender.RecoveryIssues); Assert.Equal(BatchSendingPhase.Completed, Assert.Single(f.Sender.All).Phase);
        for (var index = 0; index < row.Items; index++) Assert.Equal(UploadBatchItemPhase.BatchItemSucceeded, f.Sender.ItemResult(id, index).Phase);
    }
    [Fact]
    public async Task Lost_range_reply_and_both_host_restarts_resume_the_original_intent_at_the_receivers_checkpoint()
    {
        await using var f = new Fixture(); var id = await f.Queue(); var target = await f.Target(); var lost = false;
        async Task<FileResponse> Losing(FileRequest value, CancellationToken ct) { var response = await f.Request(value, ct); if (value.Operation == FileOperation.WriteBatchFile && !lost) { lost = true; throw new IOException("Fixture lost an accepted range reply."); } return response; }
        await f.Sender.SendAsync(id, target, Losing); var interrupted = Assert.Single(f.Sender.All); Assert.True(lost); Assert.Equal(BatchSendingPhase.Review, interrupted.Phase); var header = interrupted.Header!.ToArray();
        await f.ReopenSender(); await f.ReopenReceiver(); await f.Sender.SendAsync(id, null, f.Request);
        var done = Assert.Single(f.Sender.All); Assert.Equal(BatchSendingPhase.Completed, done.Phase); Assert.Equal(header, done.Header); Assert.Equal(1, f.Begins); Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Destination, "original", "file.bin")));
        Assert.Single(Directory.EnumerateDirectories(f.Destination)); Assert.Equal(4, f.Writes);
    }
    [Fact]
    public async Task Lost_begin_reply_checks_the_durable_plan_instead_of_starting_another_batch()
    {
        await using var f = new Fixture(); var id = await f.Queue(); var target = await f.Target();
        async Task<FileResponse> Losing(FileRequest value, CancellationToken ct) { var response = await f.Request(value, ct); if (value.Operation == FileOperation.BeginUploadBatch) throw new IOException("Fixture lost admission reply."); return response; }
        await f.Sender.SendAsync(id, target, Losing); Assert.Equal(BatchSendingPhase.Review, Assert.Single(f.Sender.All).Phase); Assert.Single(f.Receiver.BatchPlans.All);
        await f.ReopenSender(); await f.Sender.SendAsync(id, null, f.Request); Assert.Equal(BatchSendingPhase.Completed, Assert.Single(f.Sender.All).Phase); Assert.Equal(1, f.Begins); Assert.Single(f.Receiver.BatchPlans.All);
    }
    [Fact]
    public async Task A_missing_receipt_for_a_saved_intent_never_silently_replays_begin()
    {
        await using var f = new Fixture(); var id = await f.Queue(); var target = await f.Target(); var attempts = 0;
        Task<FileResponse> NeverArrived(FileRequest value, CancellationToken ct) { if (value.Operation == FileOperation.BeginUploadBatch) { attempts++; throw new IOException("Fixture request never arrived."); } return f.Request(value, ct); }
        await f.Sender.SendAsync(id, target, NeverArrived); var header = Assert.Single(f.Sender.All).Header!.ToArray(); Assert.Empty(f.Receiver.BatchPlans.All);
        await f.ReopenSender(); await f.Sender.SendAsync(id, null, NeverArrived); Assert.Equal(1, attempts); Assert.Equal(BatchSendingPhase.Review, Assert.Single(f.Sender.All).Phase); Assert.Equal(header, Assert.Single(f.Sender.All).Header); Assert.Empty(f.Receiver.BatchPlans.All);
    }
    [Fact]
    public async Task A_lost_directory_reply_uses_its_original_status_and_does_not_create_another_folder()
    {
        await using var f = new Fixture(); var id = await f.Queue(); var target = await f.Target(); var lost = false;
        async Task<FileResponse> Losing(FileRequest value, CancellationToken ct) { var response = await f.Request(value, ct); if (value.Operation == FileOperation.CreateBatchDirectory && !lost) { lost = true; throw new IOException("Fixture lost directory reply."); } return response; }
        await f.Sender.SendAsync(id, target, Losing); Assert.True(Assert.Single(f.Sender.All).Attempted); Assert.Single(Directory.EnumerateDirectories(f.Destination));
        await f.ReopenSender(); await f.Sender.SendAsync(id, null, f.Request); Assert.Equal(BatchSendingPhase.Completed, Assert.Single(f.Sender.All).Phase); Assert.Equal(2, f.Directories); Assert.Single(Directory.EnumerateDirectories(f.Destination));
    }
    [Fact]
    public async Task Changed_private_snapshots_are_refused_before_any_receiver_context_or_effect()
    {
        await using var f = new Fixture(); var id = await f.Queue(); var target = await f.Target(); var row = Assert.Single(f.Sender.All); var manifest = f.Sources.ReadManifest(row.Source); var index = manifest.Items.IndexOf(manifest.Items.Single(x => x.Name == "file.bin"));
        File.WriteAllBytes(Path.Combine(f.Profile, "outgoing-batch-copies", row.Source, index + ".bin"), [9]); var before = f.Calls;
        await f.Sender.SendAsync(id, target, f.Request); Assert.Equal(before, f.Calls); Assert.Equal(BatchSendingPhase.Review, Assert.Single(f.Sender.All).Phase); Assert.Empty(Directory.EnumerateFileSystemEntries(f.Destination));
    }
    [Fact]
    public async Task Substituted_file_descriptors_stop_before_payload_and_explicit_stop_keeps_created_items()
    {
        await using var f = new Fixture(); var id = await f.Queue(); var target = await f.Target();
        async Task<FileResponse> Substituting(FileRequest value, CancellationToken ct) { var response = await f.Request(value, ct); if (value.Operation == FileOperation.PrepareBatchFile) response.Upload.Sha256 = ByteString.CopyFrom(new byte[32]); return response; }
        await f.Sender.SendAsync(id, target, Substituting); Assert.Equal(BatchSendingPhase.Review, Assert.Single(f.Sender.All).Phase); Assert.Equal(0, f.Writes); var before = Directory.EnumerateDirectories(f.Destination).ToArray();
        await f.Sender.StopAsync(id, f.Request); var stopped = Assert.Single(f.Sender.All); Assert.Equal(BatchSendingPhase.Stopped, stopped.Phase); Assert.NotEmpty(stopped.ReceiverOutcomes!); Assert.Equal(before, Directory.EnumerateDirectories(f.Destination).ToArray()); Assert.Equal(0, f.Receiver.BatchPlans.ReservedBytes(null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Sender.SendAsync(id, null, f.Request));
    }
    [Fact]
    public async Task Pairing_withdrawal_ends_sending_and_refuses_remote_stop_under_the_old_authority()
    {
        await using var f = new Fixture(); var id = await f.Queue(); var target = await f.Target();
        async Task<FileResponse> Revoking(FileRequest value, CancellationToken ct) { var response = await f.Request(value, ct); if (value.Operation == FileOperation.WriteBatchFile) f.Allowed = false; return response; }
        await f.Sender.SendAsync(id, target, Revoking); Assert.Equal(BatchSendingPhase.Review, Assert.Single(f.Sender.All).Phase); var count = f.Calls;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Sender.StopAsync(id, f.Request)); Assert.Equal(count, f.Calls); Assert.Equal(1, f.Writes);
    }
    [Fact]
    public async Task Pause_releases_the_private_reader_and_explicit_resume_uses_the_same_plan()
    {
        await using var f = new Fixture(); var id = await f.Queue(); var target = await f.Target(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<FileResponse> Holding(FileRequest value, CancellationToken ct) { var response = await f.Request(value, ct); if (value.Operation == FileOperation.WriteBatchFile) { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); } return response; }
        var sending = f.Sender.SendAsync(id, target, Holding); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); await f.Sender.PauseAndWaitAsync(id); await sending;
        Assert.Equal(BatchSendingPhase.Paused, Assert.Single(f.Sender.All).Phase); Assert.False(f.Sender.Active(id)); await f.Sender.SendAsync(id, null, f.Request); Assert.Equal(BatchSendingPhase.Completed, Assert.Single(f.Sender.All).Phase); Assert.Equal(1, f.Begins);
    }
    [Fact]
    public async Task A_lost_parent_completion_reply_recovers_without_resending_any_item()
    {
        await using var f = new Fixture(); var id = await f.Queue(); var target = await f.Target();
        async Task<FileResponse> Losing(FileRequest value, CancellationToken ct) { var response = await f.Request(value, ct); if (value.Operation == FileOperation.FinishUploadBatch) throw new IOException("Fixture lost final batch reply."); return response; }
        await f.Sender.SendAsync(id, target, Losing); Assert.Equal(BatchSendingPhase.Review, Assert.Single(f.Sender.All).Phase); var writes = f.Writes; var directories = f.Directories;
        await f.ReopenSender(); await f.Sender.SendAsync(id, null, f.Request); Assert.Equal(BatchSendingPhase.Completed, Assert.Single(f.Sender.All).Phase); Assert.Equal(writes, f.Writes); Assert.Equal(directories, f.Directories); Assert.Equal(1, f.Begins);
    }
    [Fact]
    public async Task Every_item_reply_must_keep_the_original_parent_header_as_well_as_its_child_descriptor()
    {
        await using var f = new Fixture(); var id = await f.Queue(); var target = await f.Target();
        async Task<FileResponse> Substituting(FileRequest value, CancellationToken ct) { var response = await f.Request(value, ct); if (value.Operation == FileOperation.PrepareBatchFile) response.Batch.Header.GrantId = ProtocolRules.NewToken(); return response; }
        await f.Sender.SendAsync(id, target, Substituting); Assert.Equal(BatchSendingPhase.Review, Assert.Single(f.Sender.All).Phase); Assert.Equal(0, f.Writes);
    }
    [Fact]
    public async Task A_lost_stop_reply_restarts_only_the_saved_stop_and_preserves_the_source()
    {
        await using var f = new Fixture(); var id = await f.Queue(); var target = await f.Target();
        async Task<FileResponse> Pausing(FileRequest value, CancellationToken ct) { var response = await f.Request(value, ct); if (value.Operation == FileOperation.PrepareBatchFile) throw new IOException("Fixture paused before payload."); return response; }
        await f.Sender.SendAsync(id, target, Pausing); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<FileResponse> LostStop(FileRequest value, CancellationToken ct) { var response = await f.Request(value, ct); if (value.Operation == FileOperation.StopUploadBatch) { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); } return response; }
        var stopping = f.Sender.StopAsync(id, LostStop); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); f.Sender.Pause(id); await stopping;
        Assert.Equal(BatchSendingPhase.Stopping, Assert.Single(f.Sender.All).Phase); await f.ReopenSender(); await Assert.ThrowsAsync<InvalidOperationException>(() => f.Sender.SendAsync(id, null, f.Request));
        await f.Sender.StopAsync(id, f.Request); Assert.Equal(BatchSendingPhase.Stopped, Assert.Single(f.Sender.All).Phase); Assert.Single(f.Sources.All); Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Original, "file.bin")));
    }
    [Fact]
    public async Task Returned_rows_cannot_mutate_saved_intents_and_missing_item_results_block_cold_recovery()
    {
        await using var f = new Fixture(); var id = await f.Queue(); await f.Sender.SendAsync(id, await f.Target(), f.Request); var row = Assert.Single(f.Sender.All); var header = row.Header!.ToArray();
        row.Header![0] ^= 1; row.Outcomes![0] = 0; row.ReceiverOutcomes![0] = 0; Assert.Equal(header, Assert.Single(f.Sender.All).Header); Assert.Equal(1, Assert.Single(f.Sender.All).Outcomes![0]);
        File.Delete(Path.Combine(f.Profile, "outgoing-batch-jobs", id + ".0.json")); await f.ReopenSender(); Assert.NotEmpty(f.Sender.RecoveryIssues); Assert.True(File.Exists(Path.Combine(f.Destination, "original", "file.bin"))); Assert.Single(f.Sources.All);
    }
    [Fact]
    public async Task Maximum_valid_unicode_result_names_and_messages_fit_bounded_private_receipts()
    {
        await using var f = new Fixture(); var id = await f.Queue(); var name = new string('文', 255); var message = new string('書', 512);
        async Task<FileResponse> International(FileRequest value, CancellationToken ct)
        {
            var response = await f.Request(value, ct);
            if (response.Upload is { Phase: UploadPhase.UploadCompleted }) { response.Upload.FinalName = name; response.Upload.Message = message; response.BatchItem.ResultingName = name; response.BatchItem.Message = message; }
            return response;
        }
        await f.Sender.SendAsync(id, await f.Target(), International); var row = Assert.Single(f.Sender.All); Assert.Equal(BatchSendingPhase.Completed, row.Phase);
        var manifest = f.Sources.ReadManifest(row.Source); var index = manifest.Items.IndexOf(manifest.Items.Single(x => x.Name == "file.bin")); var receipt = f.Sender.ItemResult(id, index);
        Assert.Equal(name, receipt.Name); Assert.Equal(message, receipt.Message); await f.ReopenSender(); Assert.Empty(f.Sender.RecoveryIssues); Assert.Equal(receipt, f.Sender.ItemResult(id, index));
    }
    [Fact]
    public async Task Altered_retained_terminal_outcomes_block_cold_sender_recovery_and_keep_actual_copies()
    {
        await using var f = new Fixture(); var id = await f.Queue(); await f.Sender.SendAsync(id, await f.Target(), f.Request);
        Assert.Equal(BatchSendingPhase.Completed, Assert.Single(f.Sender.All).Phase);
        var path = Path.Combine(f.Profile, "outgoing-batch-jobs", id + ".json");
        var saved = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        var outcomes = Convert.FromBase64String(saved["Row"]!["ReceiverOutcomes"]!.GetValue<string>()); outcomes[0] = 3;
        saved["Row"]!["ReceiverOutcomes"] = Convert.ToBase64String(outcomes); File.WriteAllText(path, saved.ToJsonString());
        await f.ReopenSender(); Assert.NotEmpty(f.Sender.RecoveryIssues); Assert.Empty(f.Sender.All);
        Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Destination, "original", "file.bin"))); Assert.Single(f.Sources.All);
    }
    [Fact]
    public async Task Destination_display_labels_are_kept_without_interpreting_them_as_file_names()
    {
        await using var f = new Fixture(); var id = await f.Queue(); var target = await f.Target(); target.Name = "Work: Receive / Inbox";
        await f.Sender.SendAsync(id, target, f.Request); Assert.Equal(BatchSendingPhase.Completed, Assert.Single(f.Sender.All).Phase);
        await f.ReopenSender(); Assert.Empty(f.Sender.RecoveryIssues); Assert.Equal(target.Name, Assert.Single(f.Sender.All).Destination);
    }
    [Fact]
    public async Task Full4096ItemSenderPublishesPrivateBytesAndReopensItsCompleteResultInventory()
    {
        await using var f = new Fixture(capacity: true); var id = await f.Queue(); await f.Sender.SendAsync(id, await f.Target(), f.Request);
        var row = Assert.Single(f.Sender.All); Assert.True(row.Phase == BatchSendingPhase.Completed, row.Phase + ": " + row.Message); Assert.Equal(4096, row.Next); Assert.Equal(4095 * 3, row.Bytes);
        Assert.All(row.Outcomes!, value => Assert.Equal(1, value)); Assert.Equal(4095, Directory.EnumerateFiles(Path.Combine(f.Destination, "original")).Count()); Assert.Equal(0, f.Receiver.BatchPlans.ReservedBytes(null));
        await f.ReopenSender(); await f.ReopenReceiver(); Assert.Empty(f.Sender.RecoveryIssues); Assert.Empty(f.Receiver.BatchPlans.RecoveryIssues);
        var last = f.Sender.ItemResult(id, 4095); Assert.Equal(UploadBatchItemPhase.BatchItemSucceeded, last.Phase); Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Destination, "original", last.Name))); Assert.Equal(BatchSendingPhase.Completed, Assert.Single(f.Sender.All).Phase);
        Assert.Equal(4095, Directory.EnumerateFiles(f.Original).Count()); Assert.Single(f.Sources.All); Assert.Empty(f.Receiver.Uploads.All);
    }
}
