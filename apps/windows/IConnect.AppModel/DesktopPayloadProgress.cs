using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using System.Diagnostics;

namespace IConnect.AppModel;

public sealed partial class DesktopController
{
    private readonly PayloadRateTracker payloadRates = new();
    private readonly PayloadRateTracker outgoingPayloadRates = new();
    private readonly long payloadOrigin = Stopwatch.GetTimestamp();
    private long payloadLimit;
    private int payloadPulsePending;
    private System.Threading.Timer? payloadPulse;
    private long PayloadNow => Stopwatch.GetElapsedTime(payloadOrigin).Ticks / TimeSpan.TicksPerMillisecond;
    private string PayloadScope(DownloadJob row) => $"{row.PeerId}/{row.Ticket}/{row.SourceVersion}/{Volatile.Read(ref generation)}/{Volatile.Read(ref payloadLimit)}";
    private void ObservePayloadProgress(DownloadJob row)
    {
        if (disposed) return;
        try { payloadRates.Observe(row.Id, PayloadScope(row), row.Size, row.Offset, row.Phase == DownloadPhase.Downloading, PayloadNow); }
        catch (ArgumentException) { payloadRates.Clear(); }
    }
    public PayloadRateEstimate DownloadObservedEstimate(DownloadJob row) => payloadRates.Read(row.Id, PayloadScope(row), row.Size, row.Offset,
        row.Phase == DownloadPhase.Downloading, PayloadNow, Volatile.Read(ref payloadLimit));
    public string? DownloadFinalizationText(DownloadJob row) => downloads.ReadFinalization(row.Id)?.Describe();
    private string OutgoingPayloadScope(OutgoingPayloadProgress progress) => $"{progress.Run}/{Volatile.Read(ref generation)}/{Volatile.Read(ref payloadLimit)}";
    private void ObserveOutgoingPayloadProgress(OutgoingPayloadProgress progress)
    {
        if (disposed) return;
        outgoingPayloadRates.Observe(progress.Id, OutgoingPayloadScope(progress), progress.Size, progress.Offset, progress.Active, PayloadNow);
    }
    public PayloadRateEstimate OutgoingObservedEstimate(OutgoingCopy row)
    {
        var progress = outgoing.Payload(row.Id);
        if (row.Phase != SendingPhase.Sending || progress is null || progress.ReceiverId != row.RemoteId || progress.ReceiverEpoch != row.RemoteEpoch || progress.Size != row.Size)
            return new(row.Offset == row.Size && row.Phase is SendingPhase.Sending or SendingPhase.Verifying or SendingPhase.Completed ? PayloadRateState.Received : PayloadRateState.Stopped);
        return outgoingPayloadRates.Read(progress.Id, OutgoingPayloadScope(progress), progress.Size, progress.Offset, progress.Active, PayloadNow, Volatile.Read(ref payloadLimit));
    }
    public string OutgoingPayloadText(OutgoingCopy row) => row.Phase switch
    {
        SendingPhase.Preparing => "Preparing a private snapshot. Payload time remaining is available after sending starts.",
        SendingPhase.Ready => "Ready for explicit sending. No recent send rate is available.",
        SendingPhase.Completed => "The receiver confirmed a verified copy.",
        SendingPhase.Verifying => "Payload acknowledged. The receiver is verifying or saving; completion time is unavailable.",
        SendingPhase.Sending when row.Offset == row.Size && row.RemoteId.Length != 0 => "Payload acknowledged. Waiting for the receiver to verify and save; completion time is unavailable.",
        SendingPhase.Sending when outgoing.Payload(row.Id) is null => "Checking the private source or receiver receipt. Payload time remaining is unavailable.",
        _ => OutgoingObservedEstimate(row).Describe(PayloadProgressKind.Sending, Volatile.Read(ref payloadLimit) != 0)
    };
    public OutgoingBatchPayload? BatchPayload(string id) => PeerAuthenticated
        ? outgoingBatchSender.ReadPayload(id, Volatile.Read(ref payloadLimit)) : null;
    public IReadOnlyList<IncomingBatchFileProgress> IncomingBatchFileProgress(IncomingBatchPlan plan) =>
        PeerAuthenticated && plan.Peer == ActivePeerId ? grants.BatchPlans.ReadFileProgress(plan, Volatile.Read(ref payloadLimit)) : [];
    public string IncomingBatchFileStatus(IncomingBatchFileProgress file) => file.Finalization?.Describe() ?? (file.Phase switch
    {
        UploadPhase.UploadVerifying => "Verifying the saved file.",
        UploadPhase.UploadFinalizing => "Finishing saving and checking the file.",
        _ when file.Offset == file.Size => new PayloadRateEstimate(PayloadRateState.Received).Text,
        _ => (file.Estimate ?? new(PayloadRateState.Waiting)).Describe(PayloadProgressKind.Receiving, Volatile.Read(ref payloadLimit) != 0)
    });
    public string? IncomingPayloadText(IncomingUpload row)
    {
        if (row.Phase != UploadPhase.UploadReceiving) return grants.Uploads.ReadFinalization(row)?.Describe();
        if (row.Offset == row.Size) return new PayloadRateEstimate(PayloadRateState.Received).Text;
        if (!PeerAuthenticated || row.Peer != ActivePeerId) return null;
        var limit = Volatile.Read(ref payloadLimit);
        return grants.Uploads.ReadPayload(row, limit)?.Describe(PayloadProgressKind.Receiving, limit != 0);
    }
    private void ResetPayloadRateSettings()
    {
        Volatile.Write(ref payloadLimit, TransferNetwork?.Preferences.BytesPerSecond ?? 0);
        payloadRates.Clear(); outgoingPayloadRates.Clear(); outgoingBatchSender?.ClearPayloadRates(); grants.Uploads.ClearPayloadRates(); grants.BatchPlans.ClearFilePayloadRates();
    }
    private void StartPayloadPulse()
    {
        ResetPayloadRateSettings();
        var incoming = grants.Uploads;
        var incomingBatches = grants.BatchPlans;
        payloadPulse = new(_ =>
        {
            if (disposed || (!downloads.HasFinalizationProgress && !downloads.Jobs.Any(x => x.Phase == DownloadPhase.Downloading) && !downloadSources.Preparing && !outgoing.CopyingPayload &&
                !outgoing.HasPreparationProgress && !outgoingBatches.HasPreparationProgress && !outgoingBatchSender.HasPayload &&
                !LocalFiles.CopyingPayload && !LocalFiles.FinalizingCopies && !incoming.HasPayloadProgress && !incoming.HasFinalizationProgress &&
                !incomingBatches.HasFileProgress) || Interlocked.CompareExchange(ref payloadPulsePending, 1, 0) != 0) return;
            try { dispatch(() => { try { if (!disposed) { payloadRates.Retain(downloads.Jobs.Select(x => x.Id).ToHashSet()); Notify(DesktopChange.Payload); } } finally { Volatile.Write(ref payloadPulsePending, 0); } }); }
            catch (Exception) { Volatile.Write(ref payloadPulsePending, 0); }
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }
    private void StopPayloadPulse() { payloadPulse?.Dispose(); payloadPulse = null; payloadRates.Clear(); outgoingPayloadRates.Clear(); }
}
