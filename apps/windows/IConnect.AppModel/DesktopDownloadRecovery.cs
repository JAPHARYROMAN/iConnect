using IConnect.Core;
using IConnect.Storage.Windows;

namespace IConnect.AppModel;

public sealed partial class DesktopController
{
    private CancellationTokenSource? sourcePreparation;
    public bool SourcePreparationBusy => sourcePreparation is not null;
    public void CancelSourcePreparation() => sourcePreparation?.Cancel();
    public TransferTimeEstimate DownloadEstimate(DownloadJob job) => TransferTimeEstimate.Create(job.Size, job.Offset,
        TransferNetwork?.Preferences.BytesPerSecond ?? 0, job.SourceExpiresAt, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    public async Task<DownloadSourceReview> ReviewDownloadSource(string id)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (sourcePreparation is not null) throw new IOException("Finish the current source preparation first.");
        var job = downloads.Jobs.Single(x => x.Id == id); _ = RequirePeer(job.PeerId);
        if (SelectedFiles.Count != 1 || SelectedFiles[0].Entry.IsDirectory) throw new IOException("Select one current source file in the phone explorer, then return to this download.");
        var selected = SelectedFiles[0].Entry.Clone(); var peer = job.PeerId; var current = generation; var captured = RequireClient().CaptureFileRequester(peer);
        bool Current() => !disposed && current == generation && PeerAuthenticated && ActivePeerId == peer && OwnerPolicyHealthy && trust.TryGet(peer, out _);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(connectionLifetime!.Token); sourcePreparation = cancellation; Notify(DesktopChange.Transfers);
        try { return await downloads.PrepareSourceReviewAsync(id, peer, selected, (value, ct) => captured(value, ct), Current, cancellation.Token); }
        finally { sourcePreparation = null; if (!disposed) Notify(DesktopChange.Transfers); }
    }
    public async Task ApproveDownloadSource(DownloadSourceReview review)
    {
        trust.RequireCurrentOwner();
        if (sourcePreparation is not null) throw new IOException("Finish the current source review first.");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(connectionLifetime?.Token ?? CancellationToken.None);
        sourcePreparation = cancellation; Notify(DesktopChange.Transfers);
        try
        {
            await downloads.ConfirmSourceReviewAsync(review, cancellation.Token);
            Record("Transfers", "A fresh source was approved for a retained download. Resume remains explicit.");
        }
        finally { sourcePreparation = null; if (!disposed) Notify(DesktopChange.Transfers); }
    }
    public Task CancelDownloadSourceReview(string id) => downloads.CancelSourceReviewAsync(id);
    public async Task<BatchDownloadSourceReview> ReviewBatchDownloadSource(string batch, int item)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (sourcePreparation is not null) throw new IOException("Finish the current source preparation first.");
        var row = batches.Jobs.Single(x => x.Id == batch); _ = RequirePeer(row.PeerId);
        if (SelectedFiles.Count != 1 || SelectedFiles[0].Entry.IsDirectory) throw new IOException("Select one current source file in the phone explorer, then return to this batch item.");
        var selected = SelectedFiles[0].Entry.Clone(); var peer = row.PeerId; var current = generation; var captured = RequireClient().CaptureFileRequester(peer);
        bool Current() => !disposed && current == generation && PeerAuthenticated && ActivePeerId == peer && OwnerPolicyHealthy && trust.TryGet(peer, out _);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(connectionLifetime!.Token); sourcePreparation = cancellation; Notify(DesktopChange.Transfers);
        try { return await batches.PrepareSourceReviewAsync(batch, item, peer, selected, (value, ct) => captured(value, ct), Current, cancellation.Token); }
        finally { sourcePreparation = null; if (!disposed) Notify(DesktopChange.Transfers); }
    }
    public async Task ApproveBatchDownloadSource(BatchDownloadSourceReview review)
    {
        trust.RequireCurrentOwner();
        if (sourcePreparation is not null) throw new IOException("Finish the current source review first.");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(connectionLifetime?.Token ?? CancellationToken.None);
        sourcePreparation = cancellation; Notify(DesktopChange.Transfers);
        try { await batches.ConfirmSourceReviewAsync(review, cancellation.Token); Record("Transfers", "A retained batch item's fresh source was approved. Resume the original batch explicitly."); }
        finally { sourcePreparation = null; if (!disposed) Notify(DesktopChange.Transfers); }
    }
    public Task CancelBatchDownloadSourceReview(string id) => batches.CancelSourceReviewAsync(id);
}
