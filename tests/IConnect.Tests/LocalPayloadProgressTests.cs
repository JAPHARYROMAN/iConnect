using IConnect.AppModel;
using IConnect.Core;
using IConnect.Storage.Windows;
using Xunit;

namespace IConnect.Tests;

public sealed class LocalPayloadProgressTests
{
    private const long M = 1024 * 1024;
    private static DownloadJob Job => new("local", "", "local.txt", "node", "version", 10 * M, "", "", "", DownloadCollision.KeepBoth,
        Ticket: "ticket", Phase: DownloadPhase.Downloading);
    [Fact]
    public void Local_payload_and_batch_child_samples_are_independent_and_have_no_network_cap()
    {
        long now = 0; var rates = new LocalPayloadRates(() => now); var job = Job;
        rates.Observe(job, false); now = 1000; rates.Observe(job with { Offset = M }, false); now = 2000; rates.Observe(job with { Offset = 2 * M }, false);
        var value = rates.Read(job with { Offset = 2 * M }, false); Assert.Equal(M, value.BytesPerSecond); Assert.Equal(8L, value.RemainingSeconds);
        Assert.DoesNotContain("limit", value.Describe(PayloadProgressKind.LocalCopy));
        rates.Observe(job with { Offset = 5 * M }, true); Assert.Null(rates.Read(job with { Offset = 5 * M }, true).RemainingSeconds);
        Assert.Equal(8L, rates.Read(job with { Offset = 2 * M }, false).RemainingSeconds);
    }
    [Fact]
    public void Local_pause_source_change_and_verification_do_not_reuse_payload_samples()
    {
        long now = 0; var rates = new LocalPayloadRates(() => now); var job = Job; rates.Observe(job, false);
        now = 2000; job = job with { Offset = 2 * M }; rates.Observe(job, false); Assert.NotNull(rates.Read(job, false).RemainingSeconds);
        rates.Observe(job with { Phase = DownloadPhase.Paused }, false); Assert.False(rates.Active);
        now = 100000; rates.Observe(job, false); Assert.Null(rates.Read(job, false).RemainingSeconds);
        now += 2000; job = job with { Offset = 4 * M }; rates.Observe(job, false); Assert.NotNull(rates.Read(job, false).RemainingSeconds);
        Assert.Null(rates.Read(job with { Ticket = "new-ticket" }, false).RemainingSeconds);
        rates.Observe(job with { Offset = job.Size, Phase = DownloadPhase.Verifying }, false); Assert.False(rates.Active);
        var done = rates.Read(job with { Offset = job.Size, Phase = DownloadPhase.Verifying }, false);
        Assert.Null(done.RemainingSeconds); Assert.Contains("Verification", done.Describe(PayloadProgressKind.LocalCopy));
        Assert.DoesNotContain("received", done.Describe(PayloadProgressKind.LocalCopy));
    }
    [Fact]
    public void Local_stall_and_clock_rollback_discard_estimates_without_mutating_the_transfer()
    {
        long now = 0; var rates = new LocalPayloadRates(() => now); var job = Job; rates.Observe(job, true);
        now = 2000; job = job with { Offset = 2 * M }; rates.Observe(job, true); var original = job;
        now = 12001; Assert.Equal(PayloadRateState.Waiting, rates.Read(job, true).State);
        now = 13000; rates.Observe(job with { Offset = 3 * M }, true); Assert.Null(rates.Read(job with { Offset = 3 * M }, true).RemainingSeconds);
        now = 1; Assert.Null(rates.Read(job, true).RemainingSeconds); Assert.Equal(original, job);
        rates.Clear(); Assert.False(rates.Active);
    }
    [Theory]
    [InlineData(PayloadProgressKind.Sending, "acknowledged", "Receiver")]
    [InlineData(PayloadProgressKind.LocalCopy, "local copy", "Verification")]
    public void Directional_estimates_describe_payload_and_do_not_claim_whole_copy_completion(PayloadProgressKind kind, string activity, string later)
    {
        var value = new PayloadRateEstimate(PayloadRateState.Available, M, 8);
        Assert.Contains(activity, value.Describe(kind)); Assert.Contains(later, value.Describe(kind));
        Assert.DoesNotContain("configured limit", value.Describe(kind));
        Assert.Contains("configured limit", value.Describe(kind, true));
        Assert.DoesNotContain("About", new PayloadRateEstimate(PayloadRateState.Received).Describe(kind));
    }
}
