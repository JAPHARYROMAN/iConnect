using Microsoft.UI.Xaml.Controls;

namespace IConnect.Desktop;

public sealed partial class MainWindow
{
    private async Task ContinueSourceArchiveRemovalAsync(string id)
    {
        while (!closing && currentPage == "transfers")
        { if (await Model.ContinuePrivateBatchArchiveRemoval(id)) return; await Task.Yield(); }
    }
    private async Task InspectSourceArchiveAsync(string id)
    {
        var page = 0;
        while (!closing && currentPage == "transfers")
        {
            var entries = await Model.InspectPrivateBatchArchive(id, page * 50);
            var content = Column(Body("Local inventory only. Preserved records are not restored as sending instructions."), Body($"Page {page + 1}"));
            foreach (var entry in entries) content.Children.Add(Selectable($"{entry.Path} · {(entry.Directory ? "Folder" : entry.Bytes.ToString("N0") + " bytes")}{(entry.Recognized ? "" : " · Unrecognized name")}"));
            if (entries.Count == 0) content.Children.Add(Body("No more archive entries."));
            var dialog = Dialog("Preserved private snapshots", content, "Next entries");
            dialog.IsPrimaryButtonEnabled = entries.Count == 50; dialog.SecondaryButtonText = "Previous entries"; dialog.IsSecondaryButtonEnabled = page > 0;
            var result = await ShowAsync(dialog);
            if (result == ContentDialogResult.Primary) page++;
            else if (result == ContentDialogResult.Secondary) page--;
            else return;
        }
    }
    private void RenderBatchSourceRecovery()
    {
        var blocked = Model.TransferRecovery.Any(x => x.Area == "Private batches");
        var archives = Model.PrivateBatchRecoveryArchives;
        if (!blocked && archives.Count == 0) return;
        Page.Children.Add(Heading("Private batch snapshot recovery"));
        Page.Children.Add(Body("Preserve unreadable private snapshots before opening an empty preparation store. Preserved files remain charged to outgoing storage limits. Originals and receiver copies stay intact."));
        if (blocked) Page.Children.Add(BatchAction("Pause batch work and review snapshot recovery…", async () =>
        {
            var review = await Model.ReviewPrivateBatchStoreReset();
            if (await ShowAsync(Dialog("Preserve the unreadable private snapshots?", Column(
                Body($"Preserve {review.Files:N0} files in {review.Directories:N0} folders ({review.Bytes:N0} bytes), including {review.UnrecognizedEntries:N0} unrecognized entries, in a private archive."),
                Body("These snapshots will no longer be available for sending or resuming. Retained sender jobs and receiver copies stay intact; original receiver plans can still be stopped when their controls are available. Preserved records cannot be adopted as new snapshots. Future private batches will require explicit additional-copy review, including after archive removal."),
                Body("Unreadable ownership cannot assign bytes to a peer. Preserved bytes count against every peer's outgoing quota until separately reviewed cleanup.")), "Preserve snapshots and reset")) == ContentDialogResult.Primary)
                await Model.ConfirmPrivateBatchStoreReset(review);
        }, Model.OwnerPolicyHealthy));
        foreach (var archive in archives)
        {
            Page.Children.Add(Card("Preserved private snapshots · " + archive.Id[..8], archive.Removing ? $"Reviewed removal: {archive.Removed:N0} / {archive.ReviewedEntries:N0} entries" : "Preserved files remain charged to outgoing storage limits.",
                Selectable(Model.OwnerPolicyHealthy ? Model.PrivateBatchRecoveryLocation(archive.Id) : "Review the current local identity before inspecting preserved snapshots."),
                BatchAction("Inspect preserved snapshot entries…", () => InspectSourceArchiveAsync(archive.Id), Model.OwnerPolicyHealthy && !archive.Removing),
                BatchAction(archive.Removing ? "Continue reviewed snapshot removal" : "Review preserved snapshot removal…", async () =>
                {
                    if (archive.Removing) { await ContinueSourceArchiveRemovalAsync(archive.Id); return; }
                    var review = await Model.ReviewPrivateBatchArchiveRemoval(archive.Id);
                    if (await ShowAsync(Dialog("Permanently remove these preserved snapshots?", Column(
                        Body($"Delete the reviewed {review.Files:N0} files and {review.Directories:N0} folders ({review.Bytes:N0} bytes), including {review.UnrecognizedEntries:N0} unrecognized entries. Inspect them before deleting."),
                        Body("This cannot be undone. Current private snapshots, sender controls, originals and receiver files stay intact. Removal runs in bounded batches. Restarting the app requires fresh review of the remaining entries.")), "Remove reviewed snapshots")) == ContentDialogResult.Primary && !await Model.ConfirmPrivateBatchArchiveRemoval(review))
                        await ContinueSourceArchiveRemovalAsync(archive.Id);
                }, Model.OwnerPolicyHealthy)));
        }
    }
}
