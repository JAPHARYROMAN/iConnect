using IConnect.Storage.Windows;
using Microsoft.UI.Xaml.Controls;

namespace IConnect.Desktop;

public sealed partial class MainWindow
{
    private async Task ReviewIncomingOperationAsync(IncomingUpload item)
    {
        IncomingOperationReview? review = null;
        try
        {
            review = await Model.ReviewIncomingOperation(item.Id);
            var content = Column(Body(review.Name), Body($"{review.Checkpoint:N0} / {review.Size:N0} bytes at the retained checkpoint"),
                Body("Paired sending device"), Selectable(review.Peer),
                Body("Receiving folder"), Selectable(review.Location),
                Body(review.PublishedName.Length == 0 ? "The identified partial and its verified checkpoint are retained." : "Recorded publication: " + review.PublishedName + ". Its existing reconciliation remains required."),
                Body("Current window ended: " + DateTimeOffset.FromUnixTimeMilliseconds(review.CurrentExpiresAt).ToLocalTime().ToString("g")),
                Body("Proposed window ends: " + DateTimeOffset.FromUnixTimeMilliseconds(review.ProposedExpiresAt).ToLocalTime().ToString("g")),
                Body("This explicitly allows the same paired device to continue this receipt for up to 24 hours from review. Current Write and Create approval, exclusions and grant expiry still apply. Confirmation rechecks the retained bytes; it does not start a transfer, create another copy or change publication decisions. The sender must resume the existing receipt separately. This review expires in five minutes and ends when owner context changes."));
            if (await ShowAsync(Dialog("Review expired incoming write window", content, "Allow this receipt until the shown time")) == ContentDialogResult.Primary)
                await Model.ApproveIncomingOperation(review);
        }
        finally { if (review is not null) Model.CancelIncomingOperationReview(review.Id); }
    }
}
