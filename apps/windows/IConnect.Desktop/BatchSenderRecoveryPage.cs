using Microsoft.UI.Xaml.Controls;

namespace IConnect.Desktop;

public sealed partial class MainWindow
{
    private async Task ContinueSenderArchiveRemovalAsync(string id)
    {
        while (!closing && currentPage == "transfers")
        { if (await Model.ContinueBatchSenderArchiveRemoval(id)) return; await Task.Yield(); }
    }
    private void RenderBatchSenderRecovery()
    {
        var blocked = Model.TransferRecovery.Any(x => x.Area == "Batch sending");
        var archives = Model.OutgoingBatchRecoveryArchives;
        if (!blocked && archives.Count == 0) return;
        Page.Children.Add(Heading("Batch sender recovery"));
        Page.Children.Add(Body("Preserve an unreadable sender store before opening a fresh queue. Private snapshots, originals and receiver files stay intact. Lost or damaged intents cannot be reconstructed from this recovery action."));
        if (blocked) Page.Children.Add(BatchAction("Pause batch sends and review sender recovery…", async () =>
        {
            var review = await Model.ReviewBatchSenderReset();
            if (await ShowAsync(Dialog("Preserve the unreadable sender store?", Column(
                Body($"Preserve {review.Files:N0} files ({review.Bytes:N0} bytes), including {review.UnrecognizedFiles:N0} unrecognized files, in a private archive and open an empty sender queue."),
                Body("This loses the current queue's resume and stop controls. Receiver copies may already exist. Retained snapshots will require explicit additional-copy review. Their files and all receiver files stay intact. Inspect the preserved store before removing it.")), "Preserve and reset queue")) == ContentDialogResult.Primary)
                await Model.ConfirmBatchSenderReset(review);
        }, Model.OwnerPolicyHealthy));
        foreach (var archive in archives)
        {
            Page.Children.Add(Card("Preserved sender store · " + archive.Id[..8],
                archive.Removing ? $"Reviewed removal: {archive.Removed:N0} / {archive.ReviewedFiles:N0} files" : "Preserved files remain available for local inspection.",
                Selectable(Model.OutgoingBatchRecoveryLocation(archive.Id)),
                BatchAction(archive.Removing ? "Continue reviewed archive removal" : "Review preserved store removal…", async () =>
                {
                    if (archive.Removing) { await ContinueSenderArchiveRemovalAsync(archive.Id); return; }
                    var review = await Model.ReviewBatchSenderArchiveRemoval(archive.Id);
                    if (await ShowAsync(Dialog("Permanently remove this preserved sender store?", Column(
                        Body($"Delete the {review.Files:N0} reviewed files ({review.Bytes:N0} bytes) in this archive. {review.UnrecognizedFiles:N0} files have unrecognized names; inspect the archive before deleting them."),
                        Body("This cannot be undone. The active queue, private snapshots, originals and receiver files stay intact. Removal runs in bounded batches. After restarting the app, review the remaining archive files again.")), "Remove reviewed archive files")) == ContentDialogResult.Primary && !await Model.ConfirmBatchSenderArchiveRemoval(review))
                        await ContinueSenderArchiveRemovalAsync(archive.Id);
                }, Model.OwnerPolicyHealthy)));
        }
    }
}
