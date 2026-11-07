namespace IConnect.Core;

public sealed record TransferTimeEstimate(long RemainingBytes, long? MinimumPayloadSeconds, long? SourceSeconds, bool OutlastsApproval)
{
    public static TransferTimeEstimate Create(long size, long offset, long bytesPerSecond, long sourceExpiresAt, long now)
    {
        if (size is < 0 or > StorageRules.MaxTransferSize || offset < 0 || offset > size || sourceExpiresAt is < 0 or > StorageRules.MaxTimestamp || now is < 0 or > StorageRules.MaxTimestamp)
            throw new ArgumentOutOfRangeException(nameof(size), "Choose a bounded transfer and timestamp.");
        TransferNetworkPreferences.Validate(new(bytesPerSecond));
        var remaining = size - offset;
        long? minimum = remaining == 0 ? 0 : bytesPerSecond == 0 ? null : (remaining + bytesPerSecond - 1) / bytesPerSecond;
        long? source = sourceExpiresAt == 0 ? null : Math.Max(0, (sourceExpiresAt - now) / 1000);
        return new(remaining, minimum, source, remaining != 0 && source is { } seconds && (seconds == 0 || minimum >= seconds));
    }
    public string PayloadText => RemainingBytes == 0 ? "Payload received. Verification and security checks may still take time." : MinimumPayloadSeconds is { } seconds
        ? $"At the configured limit, remaining payload needs at least {Duration(seconds)} when this file has the whole directional budget. Other work, network and verification can take longer."
        : "No configured limit. A reliable completion-time estimate is unavailable.";
    public string ApprovalText => SourceSeconds is null ? "Source expiry is not yet known." : SourceSeconds == 0 ? "The peer-reported source approval has expired. Review a fresh source before requesting more bytes."
        : $"Peer-reported source approval remaining: about {Duration(SourceSeconds.Value)}. Device clocks may differ.";
    public string Warning => OutlastsApproval ? "The current payload estimate reaches or exceeds source expiry. Review the limit or a fresh source; approval is not extended automatically." : "";
    private static string Duration(long seconds) => seconds >= 86400 ? $"{seconds / 86400:N0} d {seconds / 3600 % 24} h" : seconds >= 3600 ? $"{seconds / 3600} h {seconds / 60 % 60} min" : seconds >= 60 ? $"{seconds / 60} min {seconds % 60} s" : $"{seconds} s";
}
