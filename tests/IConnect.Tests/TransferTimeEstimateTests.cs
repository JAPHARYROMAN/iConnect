using IConnect.Core;
using Xunit;

namespace IConnect.Tests;

public sealed class TransferTimeEstimateTests
{
    [Fact]
    public void Estimate_states_payload_lower_bound_and_detects_an_insufficient_source_window()
    {
        var value = TransferTimeEstimate.Create(8 * 1024 * 1024 + 17, 1024 * 1024, 65536, 1_100_000, 1_000_000);
        Assert.Equal(113, value.MinimumPayloadSeconds); Assert.Equal(100, value.SourceSeconds); Assert.True(value.OutlastsApproval); Assert.Contains("at least", value.PayloadText); Assert.NotEmpty(value.Warning);
    }
    [Fact]
    public void Unknown_limit_or_expiry_does_not_invent_a_completion_time()
    {
        var unknown = TransferTimeEstimate.Create(200, 10, 0, 0, 1_000_000); Assert.Null(unknown.MinimumPayloadSeconds); Assert.Null(unknown.SourceSeconds); Assert.False(unknown.OutlastsApproval);
        var expired = TransferTimeEstimate.Create(200, 10, 0, 999_999, 1_000_000); Assert.True(expired.OutlastsApproval); Assert.Contains("expired", expired.ApprovalText);
    }
    [Fact]
    public void Full_and_zero_byte_payloads_have_no_remaining_payload_time_or_expiry_warning()
    {
        foreach (var size in new long[] { 0, 100 }) { var value = TransferTimeEstimate.Create(size, size, 65536, 999_999, 1_000_000); Assert.Equal(0, value.MinimumPayloadSeconds); Assert.False(value.OutlastsApproval); Assert.Contains("Verification", value.PayloadText); }
    }
    [Fact]
    public void Maximum_declared_transfer_is_bounded_and_uses_overflow_safe_rounding()
    { var value = TransferTimeEstimate.Create(StorageRules.MaxTransferSize, 0, 65536, StorageRules.MaxTimestamp, 0); Assert.Equal(134217728, value.MinimumPayloadSeconds); Assert.False(value.OutlastsApproval); }
    [Theory]
    [InlineData(-1, 0, 65536)] [InlineData(10, 11, 65536)] [InlineData(10, -1, 65536)]
    public void Invalid_inputs_are_refused(long size, long offset, long rate) => Assert.ThrowsAny<ArgumentException>(() => TransferTimeEstimate.Create(size, offset, rate, 0, 0));
    [Fact]
    public void Unsupported_rate_is_not_treated_as_an_available_estimate() => Assert.Throws<InvalidDataException>(() => TransferTimeEstimate.Create(10, 0, 1, 0, 0));
}
