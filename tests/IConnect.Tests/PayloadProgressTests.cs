using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using Xunit;

namespace IConnect.Tests;

public sealed class PayloadProgressTests
{
    [Fact]
    public async Task Observing_native_receiving_progress_never_changes_checkpoint_source_expiry_or_publication()
    {
        await using var f = new DownloadSourceRecoveryTests.Fixture(); var tracker = new PayloadRateTracker(); long elapsed = 0; var reads = 0;
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Transport = async (request, ct) =>
        {
            if (request.Operation == FileOperation.DownloadRead)
            {
                if (request.Offset >= 262144) { reached.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
                Interlocked.Add(ref elapsed, 500); Interlocked.Increment(ref reads); await Task.Delay(120, ct);
            }
            return await f.Service.ExecuteAsync(f.Peer, request, ct);
        };
        static string Scope(DownloadJob row) => row.PeerId + "/" + row.Ticket + "/" + row.SourceVersion;
        f.Receiver.Changed += row => tracker.Observe(row.Id, Scope(row), row.Size, row.Offset, row.Phase == DownloadPhase.Downloading, Interlocked.Read(ref elapsed));
        f.Id = f.Receiver.Start(f.Peer, await f.Selected(), f.Destination, DownloadCollision.KeepBoth, f.Request);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10)); var job = f.Job; var count = reads;
        var estimate = tracker.Read(job.Id, Scope(job), job.Size, job.Offset, true, elapsed);
        Assert.Equal(262144L, job.Offset); Assert.Equal(65536L, estimate.BytesPerSecond); Assert.Equal(29L, estimate.RemainingSeconds);
        Assert.Equal(PayloadRateState.Waiting, tracker.Read(job.Id, Scope(job), job.Size, job.Offset, true, elapsed + 10001).State);
        Assert.Equal(count, reads); Assert.Equal(job, f.Job); Assert.DoesNotContain(Directory.EnumerateFiles(f.Destination), x => !Path.GetFileName(x).StartsWith(".iconnect-"));
        f.Receiver.Pause(f.Id); await f.Receiver.WaitAsync(f.Id); Assert.Equal(0, tracker.Count); Assert.Equal(job.Offset, f.Job.Offset); Assert.Equal(job.SourceExpiresAt, f.Job.SourceExpiresAt);
        f.Transport = null; await f.Finish();
    }
}
