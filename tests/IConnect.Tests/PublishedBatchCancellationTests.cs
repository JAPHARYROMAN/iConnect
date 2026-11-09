using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using Google.Protobuf;
using Xunit;

namespace IConnect.Tests;

public sealed partial class BatchPlanAdmissionTests
{
    private static async Task<FileResponse> CancelBatchCheck(Fixture f, FileRequest original)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var request = original.Clone(); request.Operation = FileOperation.CancelPublishedBatchCheck;
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            var response = await f.Raw(request);
            if (response.Status != FileStatus.Ok || response.PublishedBatchCheck?.Canceled == true) return response;
            Assert.False(response.PublishedBatchCheck?.Verified);
            await Task.Delay(10, deadline.Token);
        }
    }

    [Fact]
    public async Task Canceling_a_published_batch_check_releases_its_file_pins_and_preserves_all_history()
    {
        await using var f = new Fixture(checks: new AcceptedFiles()); var saved = await PublishBatchForCheck(f);
        var request = BatchCheckRequest(f, saved.Header);
        var history = Path.Combine(f.Profile, "incoming-batch-plans");
        var before = Directory.GetFiles(history).ToDictionary(x => Path.GetFileName(x)!, File.ReadAllBytes);
        var path = Path.Combine(f.Destination, saved.Folder, saved.File);
        await f.Call(request);
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                try { using var writer = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); }
                catch (IOException) { break; }
                await Task.Delay(10, deadline.Token);
            }
        }
        var canceled = await CancelBatchCheck(f, request);
        Assert.Equal(FileStatus.Ok, canceled.Status); StorageRules.ValidateResponse(canceled);
        Assert.True(canceled.PublishedBatchCheck.Canceled); Assert.False(canceled.PublishedBatchCheck.Verified);
        Assert.Equal(request.MutationId, canceled.PublishedBatchCheck.Challenge); Assert.Equal(saved.Header, canceled.Batch.Header);
        Assert.Empty(canceled.Content); Assert.Empty(canceled.Entries);
        await f.Grants.BatchPlans.WhenFilesIdleAsync().WaitAsync(TimeSpan.FromSeconds(3));
        using (File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) { }
        Assert.Equal(new byte[] { 7, 8, 9 }, File.ReadAllBytes(path));
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(history).Select(Path.GetFileName).Order());
        foreach (var entry in before) Assert.Equal(entry.Value, File.ReadAllBytes(Path.Combine(history, entry.Key!)));
    }

    [Theory]
    [InlineData("bad-challenge")] [InlineData("foreign-challenge")] [InlineData("header")] [InlineData("item")]
    [InlineData("destination")] [InlineData("node")] [InlineData("name")] [InlineData("version")]
    [InlineData("continuation")] [InlineData("query")] [InlineData("deleted")]
    [InlineData("offset")] [InlineData("length")] [InlineData("content")] [InlineData("upload")]
    [InlineData("epoch")] [InlineData("peer")]
    public async Task Canceling_a_batch_check_refuses_foreign_or_altered_requests_without_interrupting_it(string change)
    {
        await using var f = new Fixture(checks: new AcceptedFiles()); var saved = await PublishBatchForCheck(f);
        var request = BatchCheckRequest(f, saved.Header); var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Time.TimestampRead = () => ready.TrySetResult(); await f.Call(request);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5)); f.Time.TimestampRead = null;
        var bad = request.Clone(); bad.Operation = FileOperation.CancelPublishedBatchCheck;
        switch (change)
        {
            case "bad-challenge": bad.MutationId = "not-a-token"; break;
            case "foreign-challenge": bad.MutationId = ProtocolRules.NewToken(); break;
            case "header": bad.BatchHeader.TotalBytes++; break;
            case "item": bad.BatchItemIndex = 0; break;
            case "destination": bad.DestinationId = ProtocolRules.NewToken(); break;
            case "node": bad.NodeId = ProtocolRules.NewToken(); break;
            case "name": bad.Name = "another.bin"; break;
            case "version": bad.ExpectedVersion = new('a', 64); break;
            case "continuation": bad.Continuation = ProtocolRules.NewToken(); break;
            case "query": bad.Query = "another"; break;
            case "deleted": bad.DeletedId = ProtocolRules.NewToken(); break;
            case "offset": bad.Offset = 1; break;
            case "length": bad.Length = 1; break;
            case "content": bad.Content = ByteString.CopyFrom([1]); break;
            case "upload": bad.Upload = new(); break;
            case "epoch": bad.MutationEpoch = ProtocolRules.NewToken(); break;
        }
        var denied = await f.Raw(bad, change == "peer" ? new('b', 64) : Fixture.Peer);
        Assert.NotEqual(FileStatus.Ok, denied.Status); Assert.Null(denied.PublishedBatchCheck);
        var path = Path.Combine(f.Destination, saved.Folder, saved.File);
        Assert.Throws<IOException>(() => { using var writer = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); });
        Assert.True((await CancelBatchCheck(f, request)).PublishedBatchCheck.Canceled);
        Assert.Equal(new byte[] { 7, 8, 9 }, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Canceling_a_batch_check_cannot_take_over_another_live_session()
    {
        await using var f = new Fixture(checks: new AcceptedFiles()); var saved = await PublishBatchForCheck(f);
        var request = BatchCheckRequest(f, saved.Header); await f.Call(request);
        using var other = new WindowsFileService(f.Grants);
        var cancel = request.Clone(); cancel.Operation = FileOperation.CancelPublishedBatchCheck; cancel.RequestId = ProtocolRules.NewToken();
        Assert.Equal(FileStatus.Conflict, (await other.ExecuteAsync(Fixture.Peer, cancel, default)).Status);
        Assert.True((await CancelBatchCheck(f, request)).PublishedBatchCheck.Canceled);
        Assert.True((await CancelBatchCheck(f, request)).PublishedBatchCheck.Canceled);
        var fresh = await CollectBatchCheck(f, BatchCheckRequest(f, saved.Header));
        Assert.True(fresh.PublishedBatchCheck.Verified); Assert.False(fresh.PublishedBatchCheck.Canceled);
    }

    private sealed class QueuedCancellationOnly(WindowsFileService service) : IFileService
    {
        public Task<FileResponse> ExecuteAsync(string peer, FileRequest request, CancellationToken ct) => service.ExecuteAsync(peer, request, ct);
        public void Dispose() => service.Dispose();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Cancellation_frames_interrupt_collection_before_waiting_for_the_request_worker(bool earlyCancellation)
    {
        await using var f = new Fixture(checks: new AcceptedFiles()); var saved = await PublishBatchForCheck(f);
        var request = BatchCheckRequest(f, saved.Header); var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Time.TimestampRead = () => ready.TrySetResult(); await f.Call(request);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5)); f.Time.TimestampRead = null;
        var collecting = new TaskCompletionSource<FileResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopping = new TaskCompletionSource<FileResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var collect = request.Clone(); collect.RequestId = ProtocolRules.NewToken();
        var cancel = request.Clone(); cancel.RequestId = ProtocolRules.NewToken(); cancel.Operation = FileOperation.CancelPublishedBatchCheck;
        using var rpc = new FileRpcPeer(Fixture.Peer, frame =>
        {
            var response = FileResponse.Parser.ParseFrom(frame.Payload);
            if (response.RequestId == collect.RequestId) collecting.TrySetResult(response);
            else if (response.RequestId == cancel.RequestId) stopping.TrySetResult(response);
        }, earlyCancellation ? f.Service : new QueuedCancellationOnly(f.Service));
        var sent = 0;
        f.Time.TimestampRead = () => { if (Interlocked.Exchange(ref sent, 1) == 0) Assert.True(rpc.Handle(ProtocolRules.Pack("file-request", cancel))); };
        try
        {
            Assert.True(rpc.Handle(ProtocolRules.Pack("file-request", collect)));
            var result = await collecting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (earlyCancellation) { Assert.Equal(FileStatus.Conflict, result.Status); Assert.Null(result.PublishedBatchCheck); }
            else { Assert.Equal(FileStatus.Ok, result.Status); Assert.True(result.PublishedBatchCheck.Verified); }
            Assert.Equal(FileStatus.Ok, (await stopping.Task.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        }
        finally { f.Time.TimestampRead = null; }
        Assert.True((await CancelBatchCheck(f, request)).PublishedBatchCheck.Canceled);
        await f.Grants.BatchPlans.WhenFilesIdleAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(new byte[] { 7, 8, 9 }, File.ReadAllBytes(Path.Combine(f.Destination, saved.Folder, saved.File)));
    }
}
