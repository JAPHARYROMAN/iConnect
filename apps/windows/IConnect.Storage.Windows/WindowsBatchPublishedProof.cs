using IConnect.Core;
using IConnect.Protocol;

namespace IConnect.Storage.Windows;

public sealed partial class WindowsBatchSender
{
    public Task VerifyPublishedAsync(string id, WindowsUploadSender.Request request, CancellationToken ct = default) =>
        VerifyPublishedAsync(id, request, ct, cancelReceiver: false);

    public Task VerifyPublishedAsync(string id, WindowsUploadSender.Request request, CancellationToken ct, bool cancelReceiver)
    {
        lock (gate)
        {
            Ready(); var row = rows[id]; Authorize(row);
            if (row.Archived || row.Purging || row.Phase != BatchSendingPhase.Completed || row.Header is not { Length: > 0 } ||
                row.ReceiverOutcomes is not { Length: > 0 } || row.ReceiverOutcomes.Any(x => x != 1))
                throw new InvalidOperationException("Choose a fully copied completed batch with its original receiver history.");
            if (work.ContainsKey(id) || work.Count >= 2) throw new InvalidOperationException("This batch or two outgoing batch controls are already active.");
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct); var active = new Work(cancellation);
            var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            work.Add(id, active);
            active.Task = Task.Run(async () =>
            {
                try { await RequirePublishedBatchProof(row, request, cancellation.Token, cancelReceiver); result.TrySetResult(); }
                catch (OperationCanceledException) { result.TrySetCanceled(cancellation.Token); }
                catch (Exception ex) { result.TrySetException(ex); }
                finally { lock (gate) work.Remove(id); cancellation.Dispose(); NotifyChanged(); }
            });
            NotifyChanged(); return Observe();
            async Task Observe() { await active.Task; await result.Task; }
        }
    }

    private async Task RequirePublishedBatchProof(OutgoingBatchSend row, WindowsUploadSender.Request request, CancellationToken ct, bool cancelReceiver)
    {
        var header = Header(row); var challenge = ProtocolRules.NewToken();
        var started = System.Diagnostics.Stopwatch.GetTimestamp(); var verified = false;
        try
        {
            while (System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromMinutes(10))
            {
                ct.ThrowIfCancellationRequested(); Authorize(row);
                var response = await request(new()
                {
                    RequestId = ProtocolRules.NewToken(), Operation = FileOperation.VerifyPublishedBatch, TransferId = header.Id,
                    MutationEpoch = header.Epoch, BatchHeader = header.Clone(), MutationId = challenge
                }, ct);
                ct.ThrowIfCancellationRequested(); Authorize(row); StorageRules.ValidateResponse(response);
                if (response.Status != FileStatus.Ok) throw new IOException(response.Message);
                var receipt = UploadBatchRules.CheckReceipt(header, response);
                if (receipt.Phase != UploadBatchPlanPhase.UploadPlanCompleted || !receipt.ItemOutcomes.Span.SequenceEqual(row.ReceiverOutcomes) ||
                    row.EffectsStarted && !receipt.EffectsStarted || response.PublishedBatchCheck is not { } check || check.Challenge != challenge || check.Canceled)
                    throw new InvalidDataException("The receiver did not return a matching fresh check for this whole batch.");
                if (check.Verified) { verified = true; return; }
                await Task.Delay(300, ct);
            }
            throw new IOException("The receiver did not finish its saved-batch check before the time limit.");
            }
        finally { if (cancelReceiver && !verified) await CancelPublishedBatchProof(row, header, challenge, request); }
    }

    private async Task CancelPublishedBatchProof(OutgoingBatchSend row, UploadBatchHeader header, string challenge, WindowsUploadSender.Request request)
    {
        // Cleanup has its own short deadline after the caller's wait has ended.
        // Failure to acknowledge stopping cannot replace the original outcome.
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            while (true)
            {
                stopping.Token.ThrowIfCancellationRequested(); Authorize(row);
                var response = await request(new()
                {
                    RequestId = ProtocolRules.NewToken(), Operation = FileOperation.CancelPublishedBatchCheck, TransferId = header.Id,
                    MutationEpoch = header.Epoch, BatchHeader = header.Clone(), MutationId = challenge
                }, stopping.Token).WaitAsync(stopping.Token);
                StorageRules.ValidateResponse(response);
                var receipt = UploadBatchRules.CheckReceipt(header, response);
                if (receipt.Phase != UploadBatchPlanPhase.UploadPlanCompleted || !receipt.ItemOutcomes.Span.SequenceEqual(row.ReceiverOutcomes) ||
                    row.EffectsStarted && !receipt.EffectsStarted || response.PublishedBatchCheck is not { } check || check.Challenge != challenge || check.Verified)
                    return;
                if (check.Canceled) return;
                await Task.Delay(100, stopping.Token);
            }
        }
        catch (Exception) { }
    }
}
