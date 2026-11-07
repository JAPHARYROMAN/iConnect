using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using System.Security.Cryptography;
using Xunit;

namespace IConnect.Tests;

public sealed partial class UploadSenderTests
{
    [Fact]
    public async Task Acknowledged_payload_has_a_fresh_run_and_pause_does_not_rewrite_the_receiver_checkpoint()
    {
        await using var f = new Fixture(); await f.Initialize(); var id = await f.Sender.PrepareAsync(f.SourcePath, f.Authority);
        var rates = new PayloadRateTracker(); long now = 0; var writes = 0;
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Sender.PayloadChanged += p => rates.Observe(p.Id, p.Run, p.Size, p.Offset, p.Active, now);
        var send = f.Sender.SendAsync(id, f.Target, async (request, ct) =>
        {
            if (request.Operation == FileOperation.UploadWrite && ++writes == 5) { reached.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
            var reply = await f.Request(request, ct); if (request.Operation == FileOperation.UploadWrite) now += 500; return reply;
        });
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var progress = Assert.IsType<OutgoingPayloadProgress>(f.Sender.Payload(id)); var before = f.Sender.All.Single();
        var journal = Path.Combine(f.SenderPath, "outgoing-uploads", "journal.json"); var hash = SHA256.HashData(File.ReadAllBytes(journal));
        Assert.Equal(4L * StorageRules.MaxReadRange, progress.Offset); Assert.True(f.Sender.CopyingPayload);
        var value = rates.Read(id, progress.Run, progress.Size, progress.Offset, true, now, 65536);
        Assert.Equal(2L * StorageRules.MaxReadRange, value.BytesPerSecond);
        Assert.Equal((progress.Size - progress.Offset + 65535) / 65536, value.RemainingSeconds);
        Assert.Equal(PayloadRateState.Waiting, rates.Read(id, progress.Run, progress.Size, progress.Offset, true, now + 10001).State);
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(journal))); Assert.Equal(before, f.Sender.All.Single());
        f.Sender.Pause(id); await send.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(f.Sender.Payload(id)); Assert.False(f.Sender.CopyingPayload); Assert.Equal(0, rates.Count);
        Assert.Equal(progress.Offset, f.Grants.Uploads.All.Single().Offset);
        var nextReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumed = f.Sender.SendAsync(id, null, async (request, ct) =>
        { if (request.Operation == FileOperation.UploadWrite) { nextReached.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); } return await f.Request(request, ct); });
        await nextReached.Task.WaitAsync(TimeSpan.FromSeconds(10)); var next = Assert.IsType<OutgoingPayloadProgress>(f.Sender.Payload(id));
        Assert.NotEqual(progress.Run, next.Run); Assert.Equal(progress.Offset, next.Offset);
        Assert.Null(rates.Read(id, next.Run, next.Size, next.Offset, true, now).RemainingSeconds);
        f.Sender.Pause(id); await resumed.WaitAsync(TimeSpan.FromSeconds(10));
        await f.Sender.SendAsync(id, null, f.Request);
        var done = f.Sender.All.Single(); Assert.Equal(SendingPhase.Completed, done.Phase); Assert.Null(f.Sender.Payload(id));
        Assert.Equal(f.Bytes, File.ReadAllBytes(Path.Combine(f.Destination, done.FinalName))); Assert.Equal(f.Bytes, File.ReadAllBytes(f.SourcePath));
        Assert.DoesNotContain(next.Run, File.ReadAllText(journal));
    }

    [Fact]
    public async Task Lost_acknowledgement_is_not_observed_and_retained_receipt_bytes_only_set_the_resume_baseline()
    {
        await using var f = new Fixture(); await f.Initialize(); var id = await f.Sender.PrepareAsync(f.SourcePath, f.Authority);
        var observations = new List<OutgoingPayloadProgress>(); f.Sender.PayloadChanged += p => observations.Add(p);
        await f.Sender.SendAsync(id, f.Target, async (request, ct) =>
        { var reply = await f.Request(request, ct); if (request.Operation == FileOperation.UploadWrite) throw new IOException("Lost accepted reply fixture."); return reply; });
        Assert.Equal(StorageRules.MaxReadRange, f.Grants.Uploads.All.Single().Offset);
        Assert.All(observations, p => Assert.Equal(0L, p.Offset)); Assert.Null(f.Sender.Payload(id));
        observations.Clear(); var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var send = f.Sender.SendAsync(id, null, async (request, ct) =>
        { if (request.Operation == FileOperation.UploadWrite) { reached.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); } return await f.Request(request, ct); });
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var baseline = Assert.Single(observations); Assert.True(baseline.Active); Assert.Equal(StorageRules.MaxReadRange, baseline.Offset);
        var rates = new PayloadRateTracker(); rates.Observe(id, baseline.Run, baseline.Size, baseline.Offset, true, 0);
        Assert.Null(rates.Read(id, baseline.Run, baseline.Size, baseline.Offset, true, 2000).BytesPerSecond);
        f.Sender.Pause(id); await send.WaitAsync(TimeSpan.FromSeconds(10));
        await f.Sender.SendAsync(id, null, f.Request); Assert.Equal(SendingPhase.Completed, f.Sender.All.Single().Phase);
    }

    [Fact]
    public async Task Invalid_acknowledgement_cannot_publish_progress_and_throwing_observers_cannot_change_transfer_authority()
    {
        await using var f = new Fixture(); await f.Initialize(); var id = await f.Sender.PrepareAsync(f.SourcePath, f.Authority);
        var observations = new List<OutgoingPayloadProgress>();
        f.Sender.PayloadChanged += _ => throw new InvalidOperationException("Failing display observer fixture.");
        f.Sender.PayloadChanged += p => observations.Add(p);
        await f.Sender.SendAsync(id, f.Target, async (request, ct) =>
        { var reply = await f.Request(request, ct); if (request.Operation == FileOperation.UploadWrite) reply.Upload.Offset++; return reply; });
        Assert.Equal(SendingPhase.Review, f.Sender.All.Single().Phase); Assert.All(observations, p => Assert.Equal(0L, p.Offset));
        Assert.Null(f.Sender.Payload(id)); Assert.Equal(StorageRules.MaxReadRange, f.Grants.Uploads.All.Single().Offset);
        await f.Sender.SendAsync(id, null, f.Request); Assert.Equal(SendingPhase.Completed, f.Sender.All.Single().Phase);
        Assert.Equal(f.Bytes, File.ReadAllBytes(f.SourcePath));
    }
}
