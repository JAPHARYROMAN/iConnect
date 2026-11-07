using IConnect.Storage.Windows;

namespace IConnect.AppModel;

public sealed partial class DesktopController
{
    private CancellationTokenSource? incomingWindowPreparation;
    private Task incomingWindowTask = Task.CompletedTask;
    public bool IncomingWindowPreparationBusy => incomingWindowPreparation is not null;
    public void CancelIncomingWindowPreparation() => incomingWindowPreparation?.Cancel();
    private async Task<T> RunIncomingWindowReview<T>(Func<CancellationToken, Task<T>> action)
    {
        ObjectDisposedException.ThrowIf(disposed, this); trust.RequireCurrentOwner();
        if (IncomingWindowPreparationBusy) throw new IOException("Finish or cancel the current incoming-window check first.");
        using var cancellation = new CancellationTokenSource(); incomingWindowPreparation = cancellation; Notify(DesktopChange.Transfers);
        try { var task = action(cancellation.Token); incomingWindowTask = task; return await task; }
        finally { incomingWindowPreparation = null; if (!disposed) Notify(DesktopChange.Transfers); }
    }
    public Task<IncomingOperationReview> ReviewIncomingOperation(string id)
    {
        var current = generation;
        return RunIncomingWindowReview(ct => grants.Uploads.ReviewOperationAsync(id, () => !disposed && current == generation && OwnerPolicyHealthy, ct));
    }
    public Task ApproveIncomingOperation(IncomingOperationReview review) => RunIncomingWindowReview(async ct =>
    {
        await grants.Uploads.ConfirmOperationAsync(review, ct);
        Record("Transfers", "The owner approved a new write window for one existing incoming receipt. The sender must resume explicitly."); return true;
    });
    public void CancelIncomingOperationReview(string id) => grants.Uploads.CancelOperationReview(id);
    private async Task StopIncomingOperationReviews()
    {
        CancelIncomingWindowPreparation(); grants.Uploads.InvalidateOperationReviews();
        try { await incomingWindowTask; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or UnauthorizedAccessException) { }
    }
}
