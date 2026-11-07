using IConnect.Core;
using IConnect.Protocol;

namespace IConnect.Storage.Windows;

public sealed partial class WindowsUploads
{
    private sealed record IncomingPayloadPoint(string Scope, long Offset);
    private readonly object payloadGate = new();
    private readonly Dictionary<string, IncomingPayloadPoint> payloadPoints = [];
    private readonly PayloadRateTracker incomingRates = new();
    private readonly long payloadOrigin;
    private long PayloadNow => grants.Clock.GetElapsedTime(payloadOrigin).Ticks / TimeSpan.TicksPerMillisecond;

    public bool HasPayloadProgress { get { lock (payloadGate) return payloadPoints.Count != 0; } }
    public bool HasFinalizationProgress => verifying.Values.Any(x => x.Finalization is not null && !x.Cancellation.IsCancellationRequested);

    public IncomingFinalizationProgress? ReadFinalization(IncomingUpload row) => ReadFinalization(row.Id, row.Phase);

    private IncomingFinalizationProgress? ReadFinalization(string id, UploadPhase phase) =>
        (phase is UploadPhase.UploadVerifying or UploadPhase.UploadFinalizing) &&
        verifying.TryGetValue(id, out var work) && !work.Cancellation.IsCancellationRequested ? work.Finalization?.Read() : null;

    public PayloadRateEstimate? ReadPayload(IncomingUpload row, long limit = 0)
    {
        lock (payloadGate)
        {
            if (row.Phase != UploadPhase.UploadReceiving || row.OperationExpiresAt < grants.Clock.GetUtcNow().ToUnixTimeMilliseconds())
                return null;
            return ReadPayloadPoint(row.Id, row.Size, row.Offset, limit);
        }
    }

    private PayloadRateEstimate? ReadPayloadPoint(string id, long size, long offset, long limit) =>
        payloadPoints.TryGetValue(id, out var point) && point.Offset == offset
            ? incomingRates.Read(id, point.Scope, size, offset, true, PayloadNow, limit) : null;

    internal IncomingBatchFileProgress? ReadScopedProgress(string batch, uint index, long limit)
    {
        lock (payloadGate)
        {
            var row = cache.Rows.SingleOrDefault();
            if (row is null || row.Phase is not (UploadPhase.UploadReceiving or UploadPhase.UploadVerifying or UploadPhase.UploadFinalizing))
                return null;
            return new(batch, index, row.Name, row.Size, row.Offset, row.Phase,
                row.Phase == UploadPhase.UploadReceiving ? ReadPayloadPoint(row.Id, row.Size, row.Offset, limit) : null,
                ReadFinalization(row.Id, row.Phase));
        }
    }

    private void UpdatePayload(Row row, Row? previous, string? writeScope)
    {
        lock (payloadGate)
        {
            if (!string.IsNullOrEmpty(writeScope) && row.Offset < row.Size)
            {
                incomingRates.Observe(row.Id, writeScope, row.Size, row.Offset, true, PayloadNow);
                payloadPoints[row.Id] = new(writeScope, row.Offset);
            }
            else if ((row.Offset == row.Size || row.Phase != UploadPhase.UploadReceiving || previous?.OperationApprovedAt != row.OperationApprovedAt) &&
                payloadPoints.Remove(row.Id))
                incomingRates.Retain(payloadPoints.Keys.ToHashSet());
        }
    }

    public void ClearPayloadRates()
    {
        lock (payloadGate) { payloadPoints.Clear(); incomingRates.Clear(); }
    }

    internal void EndIncomingScope(string requestScope)
    {
        CancelPublishedChecks(requestScope);
        ClearPayloadScope(requestScope);
    }

    internal void ClearPayloadScope(string requestScope)
    {
        lock (payloadGate)
        {
            var ended = payloadPoints.Where(entry => entry.Value.Scope == requestScope).Select(entry => entry.Key).ToArray();
            if (ended.Length == 0) return;
            foreach (var id in ended) payloadPoints.Remove(id);
            incomingRates.Retain(payloadPoints.Keys.ToHashSet());
        }
    }
}
