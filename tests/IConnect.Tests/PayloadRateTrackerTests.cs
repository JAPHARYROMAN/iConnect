using IConnect.Core;
using IConnect.Storage.Windows;
using Xunit;

namespace IConnect.Tests;

public sealed class PayloadRateTrackerTests
{
    private const long M = 1024 * 1024, Size = 10 * M;
    [Fact]
    public void Incoming_finalization_times_only_the_current_local_byte_pass()
    {
        long now = 0;
        var tracker = new IncomingFinalizationTracker("receipt", Size, () => now);
        tracker.Begin(IncomingFinalizationStage.CheckingSavedBytes);
        now = 2000;
        tracker.Advance(2 * M);
        Assert.Equal(new PayloadRateEstimate(PayloadRateState.Available, M, 8), tracker.Read().Estimate);
        now = 12001;
        Assert.Equal(PayloadRateState.Waiting, tracker.Read().Estimate?.State);
        Assert.Equal(2 * M, tracker.Read().Offset);
        tracker.Begin(IncomingFinalizationStage.AttachmentCheck);
        Assert.Null(tracker.Read().Estimate);
        tracker.Begin(IncomingFinalizationStage.CheckingPublishedBytes);
        Assert.Equal(0, tracker.Read().Offset);
        Assert.Equal(PayloadRateState.Measuring, tracker.Read().Estimate?.State);
        tracker.Advance(Size);
        Assert.Equal(PayloadRateState.Received, tracker.Read().Estimate?.State);
        Assert.Null(tracker.Read().Estimate?.RemainingSeconds);
    }
    private static PayloadRateTracker Ready()
    {
        var tracker = new PayloadRateTracker(); tracker.Observe("job", "source", Size, 0, true, 0);
        tracker.Observe("job", "source", Size, M, true, 1000); tracker.Observe("job", "source", Size, 2 * M, true, 2000); return tracker;
    }
    [Fact] public void Recent_progress_uses_remaining_payload_and_rounds_up()
    { var value = Ready().Read("job", "source", Size + 1, 2 * M, true, 2000); Assert.Null(value.RemainingSeconds);
        var tracker = Ready(); value = tracker.Read("job", "source", Size, 2 * M + 1, true, 2000); Assert.Equal(M, value.BytesPerSecond); Assert.Equal(8L, value.RemainingSeconds); Assert.Equal(PayloadRateState.Available, value.State); }
    [Fact] public void Configured_cap_bounds_estimate_without_relabelling_the_observed_rate()
    { var value = Ready().Read("job", "source", Size, 2 * M, true, 2000, 65536); Assert.Equal(M, value.BytesPerSecond); Assert.Equal(128L, value.RemainingSeconds); }
    [Fact] public void Fewer_than_two_seconds_never_invents_a_rate()
    { var tracker = new PayloadRateTracker(); tracker.Observe("job", "source", Size, 0, true, 0); tracker.Observe("job", "source", Size, M, true, 1999); Assert.Null(tracker.Read("job", "source", Size, M, true, 1999).BytesPerSecond); }
    [Fact] public void Reading_and_repeated_equal_offsets_do_not_count_as_progress()
    { var tracker = new PayloadRateTracker(); tracker.Observe("job", "source", Size, 0, true, 0); tracker.Observe("job", "source", Size, 0, true, 2000); Assert.Null(tracker.Read("job", "source", Size, 0, true, 2000).RemainingSeconds); Assert.Equal(PayloadRateState.Waiting, tracker.Read("job", "source", Size, 0, true, 10001).State); }
    [Fact] public void Stalled_rate_disappears_and_does_not_span_the_idle_interval()
    { var tracker = Ready(); Assert.Equal(PayloadRateState.Waiting, tracker.Read("job", "source", Size, 2 * M, true, 12001).State); tracker.Observe("job", "source", Size, 3 * M, true, 13000); Assert.Null(tracker.Read("job", "source", Size, 3 * M, true, 13000).RemainingSeconds); tracker.Observe("job", "source", Size, 4 * M, true, 14000); tracker.Observe("job", "source", Size, 5 * M, true, 15000); Assert.Equal(5L, tracker.Read("job", "source", Size, 5 * M, true, 15000).RemainingSeconds); }
    [Fact] public void Pause_clears_samples_and_resume_starts_at_the_retained_offset()
    { var tracker = Ready(); tracker.Observe("job", "source", Size, 2 * M, false, 2100); Assert.Equal(0, tracker.Count); tracker.Observe("job", "source", Size, 2 * M, true, 60000); Assert.Null(tracker.Read("job", "source", Size, 2 * M, true, 60000).RemainingSeconds); }
    [Theory] [InlineData("other-peer")] [InlineData("other-ticket")] [InlineData("new-limit")] [InlineData("new-session")]
    public void A_new_source_or_setting_scope_forgets_old_activity(string scope)
    { var tracker = Ready(); Assert.Null(tracker.Read("job", scope, Size, 2 * M, true, 2000).RemainingSeconds); tracker.Observe("job", scope, Size, 3 * M, true, 3000); Assert.Null(tracker.Read("job", scope, Size, 3 * M, true, 3000).RemainingSeconds); }
    [Fact] public void Offset_rollback_and_monotonic_clock_rollback_reset_the_window()
    { var tracker = Ready(); Assert.Null(tracker.Read("job", "source", Size, M, true, 2100).RemainingSeconds); tracker = Ready(); Assert.Null(tracker.Read("job", "source", Size, 2 * M, true, 1900).RemainingSeconds); }
    [Fact] public void Ui_reads_do_not_make_a_later_delivered_progress_sample_look_like_clock_rollback()
    { var tracker = Ready(); Assert.NotNull(tracker.Read("job", "source", Size, 2 * M, true, 2500).RemainingSeconds); tracker.Observe("job", "source", Size, 3 * M, true, 2400); Assert.NotNull(tracker.Read("job", "source", Size, 3 * M, true, 2500).RemainingSeconds); }
    [Fact] public void Recent_window_forgets_earlier_slow_progress()
    { var tracker = new PayloadRateTracker(); const long total = 100 * M; for (var second = 0; second <= 40; second++) tracker.Observe("job", "source", total, (second <= 20 ? second : 20 + 2 * (second - 20)) * M, true, second * 1000); var value = tracker.Read("job", "source", total, 60 * M, true, 40000); Assert.Equal(2 * M, value.BytesPerSecond); Assert.Equal(20L, value.RemainingSeconds); }
    [Fact] public void Jobs_never_share_samples()
    { var tracker = Ready(); tracker.Observe("other", "source", Size, M, true, 2000); Assert.Null(tracker.Read("other", "source", Size, M, true, 2000).RemainingSeconds); Assert.Equal(M, tracker.Read("job", "source", Size, 2 * M, true, 2000).BytesPerSecond); }
    [Fact] public void Completed_or_zero_payload_clears_the_rate_without_claiming_verification()
    { var tracker = Ready(); var value = tracker.Read("job", "source", Size, Size, false, 2000); Assert.Equal(PayloadRateState.Received, value.State); Assert.Equal(0, tracker.Count); Assert.Contains("Verification", value.Text); Assert.Null(value.RemainingSeconds); Assert.Equal(PayloadRateState.Received, tracker.Read("zero", "source", 0, 0, true, 0).State); }
    [Fact] public void Peer_deadline_warning_is_advisory_and_handles_equality()
    { var value = Ready().Read("job", "source", Size, 2 * M, true, 2000); Assert.NotEmpty(value.ApprovalWarning(8)); Assert.Empty(value.ApprovalWarning(9)); Assert.Empty(value.ApprovalWarning(null)); }
    [Fact] public void Largest_supported_payload_is_overflow_safe()
    { var tracker = new PayloadRateTracker(); var total = StorageRules.MaxTransferSize; tracker.Observe("large", "source", total, 0, true, 0); tracker.Observe("large", "source", total, total / 2, true, 2000); Assert.Equal(2L, tracker.Read("large", "source", total, total / 2, true, 2000).RemainingSeconds); }
    [Fact] public void Storage_is_bounded_and_removed_records_can_be_pruned()
    { var tracker = new PayloadRateTracker(); for (var i = 0; i < 200; i++) tracker.Observe(i.ToString(), "source", Size, 0, true, i); Assert.Equal(128, tracker.Count); tracker.Retain(new HashSet<string> { "199" }); Assert.Equal(1, tracker.Count); tracker.Clear(); Assert.Equal(0, tracker.Count); }
    [Fact] public void Invalid_observations_and_limits_are_rejected()
    { var tracker = new PayloadRateTracker(); Assert.Throws<ArgumentOutOfRangeException>(() => tracker.Observe("", "source", Size, 0, true, 0)); Assert.Throws<ArgumentOutOfRangeException>(() => tracker.Observe("job", "source", -1, 0, true, 0)); Assert.Throws<ArgumentOutOfRangeException>(() => tracker.Observe("job", "source", Size, Size + 1, true, 0)); Assert.Throws<ArgumentOutOfRangeException>(() => tracker.Observe("job", "source", Size, 0, true, -1)); Assert.Throws<InvalidDataException>(() => tracker.Read("job", "source", Size, 0, true, 0, 1)); }
}
