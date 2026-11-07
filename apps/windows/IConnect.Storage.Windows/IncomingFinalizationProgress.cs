using IConnect.Core;

namespace IConnect.Storage.Windows;

public enum IncomingFinalizationStage { Preparing, CheckingSavedBytes, Publishing, AttachmentCheck, CheckingPublishedBytes }

public sealed record IncomingFinalizationProgress(IncomingFinalizationStage Stage, long Size, long Offset, PayloadRateEstimate? Estimate)
{
    public string Describe()
    {
        var label = Stage switch
        {
            IncomingFinalizationStage.CheckingSavedBytes => "Verifying the saved bytes",
            IncomingFinalizationStage.Publishing => "Publishing the verified copy",
            IncomingFinalizationStage.AttachmentCheck => "Running Windows attachment checks",
            IncomingFinalizationStage.CheckingPublishedBytes => "Rereading the published copy",
            _ => "Preparing the saved copy for verification"
        };
        return Estimate is null ? label + ". Time remaining is unavailable."
            : $"{label} · {Offset:N0} / {Size:N0} bytes. {Estimate.DescribeStage()}";
    }
}

internal sealed class IncomingFinalizationTracker(string id, long size, Func<long> now)
{
    private readonly object gate = new();
    private readonly PayloadRateTracker rates = new();
    private IncomingFinalizationStage stage;
    private long offset;
    private string? measuredStage;

    internal void Begin(IncomingFinalizationStage next)
    {
        lock (gate)
        {
            stage = next; offset = 0; rates.Clear();
            measuredStage = next is IncomingFinalizationStage.CheckingSavedBytes or IncomingFinalizationStage.CheckingPublishedBytes
                ? next.ToString() : null;
            if (measuredStage is { } key) rates.Observe(id, key, size, 0, true, now());
        }
    }

    internal void Advance(long processed)
    {
        lock (gate)
        {
            offset = processed;
            if (measuredStage is { } key) rates.Observe(id, key, size, offset, true, now());
        }
    }

    internal IncomingFinalizationProgress Read()
    {
        lock (gate)
            return new(stage, size, offset, measuredStage is { } key ? rates.Read(id, key, size, offset, true, now()) : null);
    }
}
