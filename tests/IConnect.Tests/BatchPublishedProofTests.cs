using Google.Protobuf;
using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using Xunit;

namespace IConnect.Tests;

public sealed partial class BatchSenderTests
{
    [Theory]
    [InlineData("match")] [InlineData("old-receipt")] [InlineData("challenge")]
    [InlineData("epoch")] [InlineData("header")] [InlineData("mixed")] [InlineData("effect")]
    public async Task Saved_batch_check_requires_fresh_evidence_for_the_exact_completed_parent(string kind)
    {
        await using var f = new Fixture(); var id = await f.Queue();
        await f.Sender.SendAsync(id, await f.Target(), f.Request);
        var row = Assert.Single(f.Sender.All); Assert.Equal(BatchSendingPhase.Completed, row.Phase);
        var receiptPath = Directory.GetFiles(f.Profile, id + ".json", SearchOption.AllDirectories).Single();
        var before = File.ReadAllBytes(receiptPath); var calls = new List<string>();
        async Task<FileResponse> Verify(FileRequest request, CancellationToken ct)
        {
            Assert.Equal(FileOperation.VerifyPublishedBatch, request.Operation);
            Assert.Equal(UploadBatchHeader.Parser.ParseFrom(row.Header), request.BatchHeader);
            calls.Add(request.MutationId);
            var response = await f.Request(new() { Operation = FileOperation.UploadBatchStatus, TransferId = request.TransferId, MutationEpoch = request.MutationEpoch }, ct);
            if (kind == "old-receipt") return response;
            response.PublishedBatchCheck = new() { Challenge = kind == "challenge" ? ProtocolRules.NewToken() : request.MutationId, Verified = calls.Count > 1 };
            if (kind == "epoch") response.MutationEpoch = ProtocolRules.NewToken();
            if (kind == "header") response.Batch.Header.Sha256 = ByteString.CopyFrom(new byte[32]);
            if (kind == "mixed") response.Batch.ItemOutcomes = ByteString.CopyFrom(Enumerable.Repeat((byte)2, row.Items).ToArray());
            if (kind == "effect") response.Batch.EffectsStarted = false;
            return response;
        }
        if (kind == "match") { await f.Sender.VerifyPublishedAsync(id, Verify); Assert.Equal(2, calls.Count); Assert.Single(calls.Distinct()); }
        else await Assert.ThrowsAsync<InvalidDataException>(() => f.Sender.VerifyPublishedAsync(id, Verify));
        Assert.Equal(before, File.ReadAllBytes(receiptPath)); Assert.True(File.Exists(Path.Combine(f.Original, "file.bin")));
        Assert.False(f.Sender.Active(id));
    }
    [Fact]
    public async Task Saved_batch_check_uses_real_receivers_and_can_be_canceled_without_changing_completed_history()
    {
        await using var f = new Fixture(); var id = await f.Queue();
        await f.Sender.SendAsync(id, await f.Target(), f.Request);
        var row = Assert.Single(f.Sender.All); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checking = f.Sender.VerifyPublishedAsync(id, async (request, token) =>
        { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException(); });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(f.Sender.Active(id)); f.Sender.Pause(id);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checking);
        await f.Sender.VerifyPublishedAsync(id, f.Request);
        Assert.Equal(BatchSendingPhase.Completed, Assert.Single(f.Sender.All).Phase);
        Assert.Equal(row.Header, Assert.Single(f.Sender.All).Header);
        Assert.True(File.Exists(Path.Combine(f.Original, "file.bin")));
    }
}
