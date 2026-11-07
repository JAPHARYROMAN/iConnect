using IConnect.Core;
using IConnect.Storage.Windows;
using Microsoft.UI.Xaml.Controls;

namespace IConnect.Desktop;

public sealed partial class MainWindow
{
    private async Task ReviewDownloadSourceAsync(DownloadJob job)
    {
        DownloadSourceReview? review = null;
        try
        {
            review = await Model.ReviewDownloadSource(job.Id);
            var content = Column(Body(review.Name), Body($"{review.Size:N0} bytes · retained checkpoint {review.Checkpoint:N0} bytes"),
                Body("Existing destination"), Selectable(job.DisplayPath),
                Body(review.OriginalHashKnown ? "The fresh source matches the previously recorded full SHA-256. Local checkpoint bytes will be reverified on resume."
                    : "No earlier full hash was recorded. This reviewed source will supply it; unverified local bytes can be overwritten when resuming."),
                Body(review.OldVersion == review.NewVersion ? "Source metadata matches the earlier version." : "Source metadata differs from the earlier version. The prepared content comparison above still applies."),
                Body("Prepared SHA-256"), Selectable(review.Hash),
                Body("Reported source expiry: " + DateTimeOffset.FromUnixTimeMilliseconds(review.SourceExpiresAt).ToLocalTime().ToString("g")),
                Body("This review replaces only this download's source approval. Destination, private-part identity, checkpoint and collision choice are retained. It does not extend the old approval. Resume remains a separate action. The review expires in five minutes; device clocks may differ."));
            if (await ShowAsync(Dialog("Review fresh source for retained download", content, "Approve fresh source")) == ContentDialogResult.Primary)
                await Model.ApproveDownloadSource(review);
        }
        finally { if (review is not null) await Model.CancelDownloadSourceReview(review.Id); }
    }
    private async Task ReviewBatchDownloadSourceAsync(BatchDownload batch, int item, DownloadJob job)
    {
        BatchDownloadSourceReview? review = null;
        try
        {
            review = await Model.ReviewBatchDownloadSource(batch.Id, item); var source = review.Source;
            var content = Column(Body(review.Path), Body($"{source.Size:N0} bytes · retained checkpoint {source.Checkpoint:N0} bytes"),
                Body("Original batch destination"), Selectable(job.DisplayPath),
                Body(source.OriginalHashKnown ? "The prepared source matches the complete original SHA-256. The private prefix will be verified again on resume."
                    : "No original full checksum was saved. Only an empty checkpoint may adopt this selected source."),
                Body(source.OldVersion == source.NewVersion ? "Source metadata matches." : "Source metadata changed; the full content comparison above still applies."),
                Body("Prepared SHA-256"), Selectable(source.Hash),
                Body("Peer-reported source expiry: " + DateTimeOffset.FromUnixTimeMilliseconds(source.SourceExpiresAt).ToLocalTime().ToString("g")),
                Body("Approval keeps the original batch plan, destination folders, partial file, saved progress and collision choice. Only this file's source is approved. No destination files or folders are created. Resume the original batch separately. This review lasts five minutes on this connection; device clocks may differ."));
            if (await ShowAsync(Dialog("Review fresh source for batch item", content, "Approve source in original batch")) == ContentDialogResult.Primary)
                await Model.ApproveBatchDownloadSource(review);
        }
        finally { if (review is not null) await Model.CancelBatchDownloadSourceReview(review.Id); }
    }
}
