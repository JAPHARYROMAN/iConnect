using Google.Protobuf;
using IConnect.Core;
using IConnect.Protocol;
using System.Diagnostics;
using Xunit;

namespace IConnect.Tests;

public sealed class FileTrafficPolicyTests
{
    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref ticks, duration.Ticks);
        public Task Delay(TimeSpan duration, CancellationToken ct) { ct.ThrowIfCancellationRequested(); Advance(duration); return Task.CompletedTask; }
    }
    private sealed class Service : IFileService
    {
        public int Calls; public FileRequest? Last;
        public Task<FileResponse> ExecuteAsync(string peer, FileRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls); Last = request.Clone();
            return Task.FromResult(new FileResponse { RequestId = request.RequestId, Status = FileStatus.Ok,
                Content = request.Operation == FileOperation.ReadRange ? ByteString.CopyFrom(new byte[(int)request.Length]) : ByteString.Empty });
        }
        public void Dispose() { }
    }
    private sealed class HeldService(Task release, Action entered) : IFileService
    {
        public async Task<FileResponse> ExecuteAsync(string peer, FileRequest request, CancellationToken ct)
        { if (request.Operation == FileOperation.ListRoots) { entered(); await release.WaitAsync(ct); } return new() { RequestId = request.RequestId, Status = FileStatus.Ok }; }
        public void Dispose() { }
    }
    public static IEnumerable<object[]> ContentOperations() => new[] { FileOperation.ReadRange, FileOperation.DownloadRead, FileOperation.PrepareDownload,
        FileOperation.PrepareUpload, FileOperation.UploadWrite, FileOperation.BeginUploadBatch, FileOperation.WriteUploadManifest, FileOperation.SealUploadBatch,
        FileOperation.CreateBatchDirectory, FileOperation.PrepareBatchFile, FileOperation.WriteBatchFile }.Select(value => new object[] { value });
    public static IEnumerable<object[]> CleanupOperations() => new[] { FileOperation.ListRoots, FileOperation.GetMetadata, FileOperation.DownloadStatus,
        FileOperation.ReleaseDownload, FileOperation.CancelUpload, FileOperation.FinishUpload, FileOperation.UploadBatchStatus, FileOperation.StopUploadBatch,
        FileOperation.CancelUploadBatch, FileOperation.CancelBatchFile, FileOperation.FinishBatchFile, FileOperation.FinishUploadBatch, FileOperation.RecoveryStatus }.Select(value => new object[] { value });
    [Theory]
    [MemberData(nameof(ContentOperations))]
    public async Task Metered_incoming_content_is_refused_before_the_file_service_and_metadata_still_works(FileOperation operation)
    {
        var policy = new FileTrafficPolicy(); policy.Configure(new(PauseOnMetered: true)); policy.ObserveNetwork(TransferNetworkCost.Metered);
        var service = new Service(); FileRpcPeer? remote = null;
        using var owner = new FileRpcPeer("owner", frame => remote!.Handle(frame), service, policy);
        using var guest = remote = new FileRpcPeer("guest", frame => owner.Handle(frame));
        var response = await guest.RequestAsync(new() { Operation = operation, Length = 32 }, default);
        Assert.Equal(FileStatus.AccessDenied, response.Status); Assert.Empty(response.Content); Assert.Equal(0, service.Calls);
        Assert.Equal(FileStatus.Ok, (await guest.RequestAsync(new() { Operation = FileOperation.ListRoots }, default)).Status); Assert.Equal(1, service.Calls);
    }
    [Theory]
    [MemberData(nameof(CleanupOperations))]
    public async Task Status_and_cleanup_remain_available_when_network_settings_are_damaged(FileOperation operation)
    {
        var policy = new FileTrafficPolicy(); policy.Configure(new(PauseOnMetered: true), healthy: false);
        var service = new Service(); FileRpcPeer? remote = null;
        using var owner = new FileRpcPeer("owner", frame => remote!.Handle(frame), service, policy);
        using var guest = remote = new FileRpcPeer("guest", frame => owner.Handle(frame));
        Assert.Equal(FileStatus.Ok, (await guest.RequestAsync(new() { Operation = operation }, default)).Status); Assert.Equal(1, service.Calls);
    }
    [Theory]
    [InlineData(-1L)] [InlineData(1L)] [InlineData(65535L)] [InlineData(67108865L)]
    public void Invalid_bandwidth_limits_are_refused(long rate) => Assert.Throws<InvalidDataException>(() => new FileTrafficPolicy().Configure(new(rate)));
    [Fact]
    public async Task Outgoing_content_is_refused_before_transmission_but_local_metadata_requests_still_work()
    {
        var policy = new FileTrafficPolicy(); policy.Configure(new(PauseOnMetered: true)); var service = new Service(); FileRpcPeer? remote = null;
        using var owner = new FileRpcPeer("owner", frame => remote!.Handle(frame), service);
        using var guest = remote = new FileRpcPeer("guest", frame => owner.Handle(frame), traffic: policy);
        await Assert.ThrowsAsync<StorageException>(() => guest.RequestAsync(new() { Operation = FileOperation.ReadRange, Length = 16 }, default)); Assert.Equal(0, service.Calls);
        await guest.RequestAsync(new() { Operation = FileOperation.ListRoots }, default); Assert.Equal(1, service.Calls);
        policy.ObserveNetwork(TransferNetworkCost.Unrestricted); Assert.Equal(16, (await guest.RequestAsync(new() { Operation = FileOperation.ReadRange, Length = 16 }, default)).Content.Length);
    }
    [Fact]
    public void Unknown_and_stale_cost_fail_closed_only_when_owner_enables_metered_pausing()
    {
        var clock = new Clock(); var policy = new FileTrafficPolicy(clock, clock.Delay); policy.RequireContent();
        policy.Configure(new(PauseOnMetered: true)); Assert.Throws<StorageException>(policy.RequireContent);
        policy.ObserveNetwork(TransferNetworkCost.Unrestricted); policy.RequireContent(); clock.Advance(TimeSpan.FromSeconds(5)); policy.RequireContent();
        clock.Advance(TimeSpan.FromTicks(1)); Assert.Equal(TransferNetworkCost.Unknown, policy.NetworkCost); Assert.Throws<StorageException>(policy.RequireContent);
        policy.ObserveNetwork(TransferNetworkCost.Unrestricted); policy.RequireContent(); policy.ObserveNetwork(TransferNetworkCost.Metered); Assert.Throws<StorageException>(policy.RequireContent);
        policy.Configure(new()); policy.RequireContent();
    }
    [Fact]
    public async Task One_aggregate_lane_paces_concurrent_content_in_real_elapsed_time()
    {
        var policy = new FileTrafficPolicy(); policy.Configure(new(65536)); var watch = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => policy.PaceAsync(32768, true, default)));
        Assert.InRange(watch.Elapsed.TotalSeconds, 1.95, 20);
    }
    [Fact]
    public async Task Unlimited_content_does_not_delay_and_directional_payloads_use_the_expected_budget()
    {
        var clock = new Clock(); var policy = new FileTrafficPolicy(clock, clock.Delay);
        await policy.PaceAsync(32768, true, default); Assert.Equal(0, clock.GetTimestamp()); policy.Configure(new(65536));
        await policy.BeforeRequestAsync(new() { Operation = FileOperation.ReadRange, Length = 32768 }, true, default);
        Assert.InRange(clock.GetTimestamp(), TimeSpan.FromMilliseconds(500).Ticks, TimeSpan.FromMilliseconds(501).Ticks);
        await policy.BeforeRequestAsync(new() { Operation = FileOperation.UploadWrite, Content = ByteString.CopyFrom(new byte[32768]) }, false, default);
        Assert.InRange(clock.GetTimestamp(), TimeSpan.FromSeconds(1).Ticks, TimeSpan.FromMilliseconds(1001).Ticks);
    }
    [Fact]
    public async Task Changing_cost_or_lowering_bandwidth_is_observed_during_pacing()
    {
        var clock = new Clock(); FileTrafficPolicy? policy = null; var first = true;
        policy = new(clock, (duration, ct) => { clock.Advance(duration); if (first) { first = false; policy!.Configure(new(65536)); } return Task.CompletedTask; });
        policy.Configure(new(524288)); await policy.PaceAsync(32768, true, default);
        Assert.InRange(clock.GetTimestamp(), TimeSpan.FromMilliseconds(500).Ticks, TimeSpan.FromMilliseconds(501).Ticks);
        policy = new(clock, (duration, ct) => { clock.Advance(duration); policy!.ObserveNetwork(TransferNetworkCost.Metered); return Task.CompletedTask; });
        policy.Configure(new(65536, true)); policy.ObserveNetwork(TransferNetworkCost.Unrestricted);
        await Assert.ThrowsAsync<StorageException>(() => policy.PaceAsync(32768, false, default));
    }
    [Fact]
    public async Task Canceled_wait_has_no_effect_or_future_bandwidth_debt_and_disposal_cancels_rpc_waits()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var policy = new FileTrafficPolicy(TimeProvider.System, async (_, ct) => { entered.TrySetResult(); await release.Task.WaitAsync(ct); }); policy.Configure(new(65536));
        var sends = 0; var peer = new FileRpcPeer("owner", _ => ++sends, traffic: policy);
        var request = peer.RequestAsync(new() { Operation = FileOperation.UploadWrite, Content = ByteString.CopyFrom(new byte[32768]) }, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); peer.Dispose(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request); Assert.Equal(0, sends);
        policy.Configure(new()); await policy.PaceAsync(32768, true, default);
    }
    [Fact]
    public async Task Payload_bounds_and_payloads_attached_to_metadata_cannot_bypass_controls()
    {
        var policy = new FileTrafficPolicy();
        await Assert.ThrowsAsync<StorageException>(() => policy.BeforeRequestAsync(new() { Operation = FileOperation.ReadRange, Length = uint.MaxValue }, true, default));
        await Assert.ThrowsAsync<StorageException>(() => policy.BeforeRequestAsync(new() { Content = ByteString.CopyFrom(new byte[32769]) }, false, default));
        policy.Configure(new(PauseOnMetered: true));
        await Assert.ThrowsAsync<StorageException>(() => policy.BeforeRequestAsync(new() { Operation = FileOperation.ListRoots, Content = ByteString.CopyFromUtf8("unexpected payload") }, true, default));
    }
    [Fact]
    public async Task A_paced_request_is_an_immutable_snapshot_and_metadata_does_not_wait_behind_its_lane()
    {
        var clock = new Clock(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var policy = new FileTrafficPolicy(clock, async (duration, ct) => { entered.TrySetResult(); await release.Task.WaitAsync(ct); clock.Advance(duration); }); policy.Configure(new(65536));
        var service = new Service(); FileRpcPeer? remote = null;
        using var owner = new FileRpcPeer("owner", frame => remote!.Handle(frame), service);
        using var guest = remote = new FileRpcPeer("guest", frame => owner.Handle(frame), traffic: policy);
        var input = new FileRequest { Operation = FileOperation.ReadRange, Length = 32768, NodeId = "captured-node" }; var work = guest.RequestAsync(input, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); input.Operation = FileOperation.Delete; input.NodeId = "changed-node";
        await guest.RequestAsync(new() { Operation = FileOperation.ListRoots }, default).WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(1, service.Calls);
        release.SetResult(); Assert.Equal(32768, (await work.WaitAsync(TimeSpan.FromSeconds(5))).Content.Length); Assert.Equal("captured-node", service.Last!.NodeId); Assert.Equal(FileOperation.ReadRange, service.Last.Operation);
    }
    [Fact]
    public async Task Exhausted_rpc_credits_cannot_accumulate_prepaid_payloads_and_release_them_as_a_burst()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var policy = new FileTrafficPolicy(); policy.Configure(new(65536)); var times = new System.Collections.Concurrent.ConcurrentQueue<long>(); FileRpcPeer? remote = null;
        using var owner = new FileRpcPeer("owner", frame => remote!.Handle(frame), new HeldService(release.Task, () => entered.TrySetResult()));
        using var guest = remote = new FileRpcPeer("guest", frame => { if (frame.Kind == "file-request" && FileRequest.Parser.ParseFrom(frame.Payload).Operation == FileOperation.UploadWrite) times.Enqueue(Stopwatch.GetTimestamp()); owner.Handle(frame); }, traffic: policy);
        var metadata = Enumerable.Range(0, 16).Select(_ => guest.RequestAsync(new() { Operation = FileOperation.ListRoots }, default)).ToArray(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var content = Enumerable.Range(0, 4).Select(_ => guest.RequestAsync(new() { Operation = FileOperation.UploadWrite, Content = ByteString.CopyFrom(new byte[32768]) }, default)).ToArray();
        await Task.Delay(2200); Assert.Empty(times); release.SetResult(); await Task.WhenAll(metadata).WaitAsync(TimeSpan.FromSeconds(10)); await Task.WhenAll(content).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(4, times.Count); Assert.True(Stopwatch.GetElapsedTime(times.First(), times.Last()).TotalSeconds >= 1.45, "Content frames must remain paced after credits become available.");
    }
}
