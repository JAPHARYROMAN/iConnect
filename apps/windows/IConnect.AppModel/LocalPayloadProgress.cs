using IConnect.Core;
using IConnect.Storage.Windows;
using System.Diagnostics;

namespace IConnect.AppModel;

internal sealed class LocalPayloadRates(Func<long> now)
{
    private readonly PayloadRateTracker rates = new();
    internal bool Active => rates.Count != 0;
    private static string Id(DownloadJob row, bool batch) => (batch ? "batch/" : "file/") + row.Id;
    private static string Scope(DownloadJob row) => row.Ticket + "/" + row.SourceVersion;
    internal void Observe(DownloadJob row, bool batch) => rates.Observe(Id(row, batch), Scope(row), row.Size, row.Offset, row.Phase == DownloadPhase.Downloading, now());
    internal PayloadRateEstimate Read(DownloadJob row, bool batch) => rates.Read(Id(row, batch), Scope(row), row.Size, row.Offset, row.Phase == DownloadPhase.Downloading, now());
    internal void Clear() => rates.Clear();
}

public sealed partial class LocalExplorerController
{
    private static long LocalPayloadNow() => (long)(Stopwatch.GetTimestamp() * (1000d / Stopwatch.Frequency));
    private readonly LocalPayloadRates localPayloadRates = new(LocalPayloadNow);
    public bool CopyingPayload => !disposed && localPayloadRates.Active;
    public bool FinalizingCopies => !disposed && (Copies?.HasFinalizationProgress == true || BatchCopies?.HasFinalizationProgress == true);
    public PayloadRateEstimate CopyObservedEstimate(DownloadJob row, bool batch = false) => disposed
        ? new(PayloadRateState.Stopped) : localPayloadRates.Read(row, batch);
    public string CopyPayloadText(DownloadJob row, bool batch = false)
    {
        var finalization = disposed ? null : batch ? BatchCopies?.ReadFinalization(row.Id) : Copies?.ReadFinalization(row.Id);
        if (finalization is not null) return finalization.Describe();
        return row.Phase switch
        {
            DownloadPhase.Preparing => "Checking the selected local source and destination. Payload time remaining is unavailable.",
            DownloadPhase.Verifying => "Payload copied. Verifying local bytes.",
            DownloadPhase.Finalizing => "Saving the verified local copy.",
            DownloadPhase.Completed => "The verified local copy is saved.",
            _ => CopyObservedEstimate(row, batch).Describe(PayloadProgressKind.LocalCopy)
        };
    }
}
