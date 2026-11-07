namespace IConnect.Core;

public enum PayloadRateState { Measuring, Available, Waiting, Stopped, Received }
public enum PayloadProgressKind { Receiving, Sending, LocalCopy, BatchSending }
public sealed record PayloadRateEstimate(PayloadRateState State, long? BytesPerSecond = null, long? RemainingSeconds = null)
{
    public string Describe(PayloadProgressKind kind, bool configuredLimit = false)
    {
        if (kind == PayloadProgressKind.Receiving) return ReceivingText(configuredLimit);
        var sending = kind is PayloadProgressKind.Sending or PayloadProgressKind.BatchSending;
        var label = kind switch { PayloadProgressKind.BatchSending => "batch acknowledgement", PayloadProgressKind.Sending => "acknowledged send", _ => "local copy" };
        var later = sending ? "Receiver verification and saving can take longer." : "Verification and saving can take longer.";
        return State switch
        {
            PayloadRateState.Received => (sending ? "Payload acknowledged. " : "Payload copied. ") + later,
            PayloadRateState.Stopped => $"Recent {label} rate is unavailable while payload copying is stopped.",
            PayloadRateState.Waiting => $"Waiting for fresh {label} progress. Time remaining is unavailable.",
            _ => BytesPerSecond is { } rate && RemainingSeconds is { } seconds
                ? $"Recent {label} progress: {Rate(rate)}. About {Duration(seconds)} of payload remains at this rate{(configuredLimit ? " and the configured limit" : "")}. {later}"
                : $"Measuring recent {label} progress…"
        };
    }
    public string Text => ReceivingText(true);
    private string ReceivingText(bool configuredLimit) => State switch
    {
        PayloadRateState.Received => "Payload received. Verification and saving may still take time.",
        PayloadRateState.Stopped => "Recent payload rate is unavailable while this file is stopped.",
        PayloadRateState.Waiting => "Waiting for fresh payload progress. Time remaining is unavailable.",
        _ => BytesPerSecond is { } rate && RemainingSeconds is { } seconds
            ? $"Recent payload progress: {Rate(rate)}. About {Duration(seconds)} of payload remains at this rate{(configuredLimit ? " and the configured limit" : "")}. Verification and saving can take longer."
            : "Measuring recent payload progress…"
    };
    public string ApprovalWarning(long? sourceSeconds) => RemainingSeconds is { } remaining && sourceSeconds is { } source && remaining >= source
        ? "The recent payload estimate reaches source expiry. Review the limit or a fresh source; approval is not extended automatically." : "";
    public string DescribeStage() => State switch
    {
        PayloadRateState.Available when BytesPerSecond is { } rate && RemainingSeconds is { } seconds =>
            $"Recent rate {Rate(rate)}. About {Duration(seconds)} for the remaining bytes in this step.",
        PayloadRateState.Received => "All bytes processed. Checking the result.",
        PayloadRateState.Waiting => "Waiting for fresh progress. Time remaining is unavailable.",
        _ => "Measuring recent progress…"
    };
    private static string Rate(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):F1} GiB/s" : bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):F1} MiB/s" : bytes >= 1024 ? $"{bytes / 1024d:F1} KiB/s" : $"{bytes} B/s";
    private static string Duration(long seconds) => seconds >= 86400 ? $"{seconds / 86400} d {seconds / 3600 % 24} h" : seconds >= 3600 ? $"{seconds / 3600} h {seconds / 60 % 60} min" : seconds >= 60 ? $"{seconds / 60} min {seconds % 60} s" : $"{seconds} s";
}

/// <summary>Memory-only recent payload progress, never authority, durable state or a billing meter.</summary>
public sealed class PayloadRateTracker
{
    private sealed class Row(string scope, long size, long offset, long now)
    {
        internal string Scope = scope;
        internal long Size = size, Offset = offset, LastClock = now, LastProgress = now;
        internal bool Waiting;
        internal readonly List<(long Time, long Offset)> Points = [(now, offset)];
    }
    private readonly object gate = new();
    private readonly Dictionary<string, Row> rows = [];
    public int Count { get { lock (gate) return rows.Count; } }
    private static void Validate(string id, string scope, long size, long offset, long now)
    {
        if (id.Length is < 1 or > 128 || scope.Length is < 1 or > 512 || size is < 0 or > StorageRules.MaxTransferSize || offset < 0 || offset > size || now < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), "Use bounded payload observations and a monotonic timestamp.");
    }
    private Row Reset(string id, string scope, long size, long offset, long now)
    {
        if (!rows.ContainsKey(id) && rows.Count >= 128) rows.Remove(rows.MinBy(x => x.Value.LastClock).Key);
        var row = new Row(scope, size, offset, now); rows[id] = row; return row;
    }
    public void Observe(string id, string scope, long size, long offset, bool receiving, long now)
    {
        Validate(id, scope, size, offset, now);
        lock (gate)
        {
            if (!receiving || offset == size) { rows.Remove(id); return; }
            if (!rows.TryGetValue(id, out var row) || row.Scope != scope || row.Size != size || offset < row.Offset || now < row.LastClock)
            { Reset(id, scope, size, offset, now); return; }
            row.LastClock = now;
            if (offset == row.Offset) return;
            if (row.Waiting || now - row.LastProgress > 10_000) { Reset(id, scope, size, offset, now); return; }
            row.Offset = offset; row.LastProgress = now;
            var last = row.Points[^1];
            if (now - last.Time >= 250) row.Points.Add((now, offset));
            while (row.Points.Count > 2 && row.Points[1].Time <= now - 20_000) row.Points.RemoveAt(0);
            while (row.Points.Count > 96) row.Points.RemoveAt(0);
        }
    }
    public PayloadRateEstimate Read(string id, string scope, long size, long offset, bool receiving, long now, long limit = 0, long? remainingPayload = null)
    {
        Validate(id, scope, size, offset, now); TransferNetworkPreferences.Validate(new(limit));
        if (remainingPayload < 0 || remainingPayload > size) throw new ArgumentOutOfRangeException(nameof(remainingPayload));
        lock (gate)
        {
            if (offset == size) { rows.Remove(id); return new(PayloadRateState.Received); }
            if (!receiving) { rows.Remove(id); return new(PayloadRateState.Stopped); }
            if (!rows.TryGetValue(id, out var row) || row.Scope != scope || row.Size != size || offset < row.Offset || now < row.LastClock)
            { Reset(id, scope, size, offset, now); return new(PayloadRateState.Measuring); }
            if (now - row.LastProgress > 10_000) { row.Waiting = true; row.Points.Clear(); return new(PayloadRateState.Waiting); }
            if (row.Waiting || row.Points.Count < 2) return new(PayloadRateState.Measuring);
            var first = row.Points[0]; var last = row.Points[^1]; var elapsed = last.Time - first.Time; var bytes = last.Offset - first.Offset;
            if (elapsed < 2000 || bytes <= 0) return new(PayloadRateState.Measuring);
            var rate = bytes * 1000 / elapsed; if (rate == 0) return new(PayloadRateState.Measuring);
            var expected = limit == 0 ? rate : Math.Min(rate, limit); var remaining = remainingPayload ?? size - offset;
            return new(PayloadRateState.Available, rate, (remaining + expected - 1) / expected);
        }
    }
    public void Retain(IReadOnlySet<string> ids) { lock (gate) foreach (var id in rows.Keys.Where(x => !ids.Contains(x)).ToArray()) rows.Remove(id); }
    public void Clear() { lock (gate) rows.Clear(); }
}
