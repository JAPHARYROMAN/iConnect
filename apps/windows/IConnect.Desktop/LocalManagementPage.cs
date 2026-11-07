using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using Microsoft.UI.Xaml.Controls;

namespace IConnect.Desktop;

public sealed partial class MainWindow
{
    private int localChangePage, localDeletedPage;
    private void RenderLocalManagement()
    {
        var browser = Model.LocalFiles; var ready = !browser.Busy && !browser.ManagementBusy;
        if (browser.Management is null) { Page.Children.Add(Body(browser.ManagementIssue)); return; }
        var content = Column(Body("Review local changes before applying them. Existing destination items are kept. Moving an item into recovery keeps a saved copy on the same volume until you explicitly delete it."));
        content.Children.Add(Row(Action("New folder…", () => ReviewLocalChange(FileOperation.CreateDirectory), ready && browser.Breadcrumbs.Count != 0),
            Action("Rename selected…", () => ReviewLocalChange(FileOperation.Rename), ready),
            Action("Move selected…", () => ReviewLocalChange(FileOperation.Move), ready),
            Action("Move selected to recovery…", () => ReviewLocalChange(FileOperation.Delete), ready)));
        content.Children.Add(Action("Review local history and recovery", browser.RefreshManagementAsync, ready));
        if (browser.ManagementBusy) content.Children.Add(Body("Checking local management progress…"));
        if (browser.ManagementNotice.Length != 0) content.Children.Add(Body(browser.ManagementNotice));
        foreach (var issue in browser.ManagementRecoveryIssues)
            content.Children.Add(Card(issue.Record, issue.Message, Action("Preserve damaged history…", async () =>
            {
                if (await ShowAsync(Dialog("Preserve damaged local management history?", Body("Preserve this metadata for manual recovery and start a fresh journal. Existing files and saved recovery contents are kept. An archived recovery catalog no longer lists its saved items."), "Preserve history")) == ContentDialogResult.Primary)
                    await RunLocalManagement(manager => { manager.Preserve(issue.Id); return true; });
            }, ready && issue.CanArchive)));
        if (browser.ManagementPreserved != 0) content.Children.Add(Action("Remove preserved management metadata…", async () =>
        {
            if (await ShowAsync(Dialog("Remove preserved management metadata?", Body("Remove only archived metadata backups. File contents, active recovery items and current operation receipts are kept. Removed metadata cannot be recovered through iConnect."), "Remove metadata backups")) == ContentDialogResult.Primary)
                await RunLocalManagement(manager => { manager.ClearPreserved(); return true; });
        }, ready));
        if (browser.ManagementLoaded)
        {
            var pages = Math.Max(1, (browser.ManagementHistory.Count + 19) / 20); localChangePage = Math.Clamp(localChangePage, 0, pages - 1);
            content.Children.Add(Body($"Local change receipts · {browser.ManagementHistory.Count:N0} records · page {localChangePage + 1} / {pages}"));
            foreach (var change in browser.ManagementHistory.Skip(localChangePage * 20).Take(20))
                content.Children.Add(Card(change.Receipt.Operation.ToString(), change.Receipt.Phase + " · " + change.Receipt.Message, Selectable(change.Source), Selectable(change.Destination)));
            content.Children.Add(Row(Action("Earlier change page", () => { --localChangePage; Render(); return Task.CompletedTask; }, localChangePage > 0),
                Action("Next change page", () => { ++localChangePage; Render(); return Task.CompletedTask; }, localChangePage + 1 < pages)));
            var deletedPages = Math.Max(1, (browser.DeletedItems.Count + 19) / 20); localDeletedPage = Math.Clamp(localDeletedPage, 0, deletedPages - 1);
            content.Children.Add(Body($"Local saved items · {browser.DeletedItems.Count:N0} records · page {localDeletedPage + 1} / {deletedPages}. Saved copies have no automatic expiry."));
            foreach (var row in browser.DeletedItems.Skip(localDeletedPage * 20).Take(20))
            {
                var item = row.Item;
                content.Children.Add(Card(item.Name, row.State + " · " + item.Message, Body("Original location"), Selectable(row.OriginalLocation), Body("Saved recovery location"), Selectable(row.SavedLocation),
                    Row(Action("Restore…", async () =>
                    {
                        var name = new TextBox { Header = "Restore name", Text = item.Name, MaxLength = 255 };
                        if (await ShowAsync(Dialog("Restore " + item.Name + "?", Column(Body("Restore to the original parent folder. Existing items are kept."), Selectable(row.OriginalLocation), name), "Restore")) == ContentDialogResult.Primary)
                        { var restoreName = name.Text; await ShowLocalResult(await RunLocalManagement(manager => manager.Restore(item.Id, restoreName))); }
                    }, ready && item.CanRestore),
                        Action("Restore to another folder…", () => ReviewAlternateRestoreAsync(item.Id, item.Name, offline: true), ready && item.CanRestore),
                        Action("Review locations…", () => InspectLocalManagement(item.Id), ready)),
                    Row(Action("Permanently delete saved copy…", () => PurgeLocalManagement(item.Id, false), ready && row.CanPurge),
                        Action("Restore remaining data…", () => PurgeLocalManagement(item.Id, true), ready && row.CanRestoreRemaining)),
                    Row(Action("Review recovery folder…", () => ReviewEmptyVaultAsync(item.Id, offline: true), ready && row.CanDismiss),
                        Action("Dismiss settled receipt…", () => DismissRecoveryRecordAsync(item.Id, offline: true), ready && row.CanDismiss))));
            }
            content.Children.Add(Row(Action("Earlier saved-items page", () => { --localDeletedPage; Render(); return Task.CompletedTask; }, localDeletedPage > 0),
                Action("Next saved-items page", () => { ++localDeletedPage; Render(); return Task.CompletedTask; }, localDeletedPage + 1 < deletedPages)));
        }
        Page.Children.Add(RefreshableExpander("local-management", "Manage files on this computer", content));
    }
    private async Task<T> RunLocalManagement<T>(Func<WindowsLocalManagement, T> action)
    {
        try { return await Model.LocalFiles.ManageAsync(action); }
        finally { await Model.LocalFiles.RefreshManagementAsync(); await Model.LocalFiles.RefreshAfterManagementAsync(); }
    }
    private async Task ReviewLocalChange(FileOperation operation)
    {
        var browser = Model.LocalFiles; var selected = await browser.CaptureManagementAsync(operation == FileOperation.CreateDirectory);
        var name = new TextBox { Header = operation == FileOperation.CreateDirectory ? "New folder name" : "Destination name", Text = operation == FileOperation.CreateDirectory ? "New folder" : selected.Name, MaxLength = 255 };
        string? destination = null;
        if (operation == FileOperation.Move)
        {
            var picker = new Windows.Storage.Pickers.FolderPicker(); picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync(); if (folder is null) return; destination = folder.Path;
        }
        if (operation != FileOperation.Delete && await ShowAsync(Dialog("Choose local " + (operation == FileOperation.CreateDirectory ? "folder name" : "destination name"), Column(Selectable(selected.SourceLocation), name), "Review change")) != ContentDialogResult.Primary) return;
        var requestedName = name.Text;
        if (operation == FileOperation.Move)
        {
            var target = await browser.ManageAsync(manager => manager.InspectMoveDestination(selected, destination!));
            if (target.AcrossVolumes)
            {
                StorageRules.ValidateName(requestedName);
                if (selected.IsDirectory)
                {
                    var batches = browser.BatchCopies ?? throw new IOException(browser.BatchCopyIssue);
                    var folderMoveDetail = Column(Body("Original folder"), Selectable(selected.SourceLocation), Body("Destination"),
                        Selectable(Path.Combine(target.Directory, requestedName)),
                        Body("iConnect will copy and verify the folder first. Open the completed local batch to review moving the original into recovery."));
                    if (await ShowAsync(Dialog("Copy this folder for a move?", folderMoveDetail, "Start folder move copy")) == ContentDialogResult.Primary)
                    {
                        var id = await browser.PrepareFolderMoveAsync(selected, target, requestedName);
                        if (!closing) await Task.Run(() => batches.StartFolderMoveCopy(id));
                    }
                    return;
                }
                var copies = browser.Copies ?? throw new IOException(browser.CopyIssue);
                var content = Column(Body("Original"), Selectable(selected.SourceLocation), Body("Destination"),
                    Selectable(Path.Combine(target.Directory, requestedName)),
                    Body("iConnect will copy and verify the file first. After verification, open Copies on this computer to review moving the original into recovery."));
                if (await ShowAsync(Dialog("Copy this file for a move?", content, "Start move copy")) == ContentDialogResult.Primary)
                    await Task.Run(() => copies.StartVerifiedMoveCopy(copies.PrepareVerifiedMove(selected, target, requestedName)));
                return;
            }
        }
        var review = await browser.ManageAsync(manager => operation switch
        {
            FileOperation.CreateDirectory => manager.ReviewCreateFolder(selected, requestedName),
            FileOperation.Rename => manager.ReviewRename(selected, requestedName),
            FileOperation.Move => manager.ReviewMove(selected, destination!, requestedName),
            FileOperation.Delete => manager.ReviewDelete(selected),
            _ => throw new InvalidOperationException("Unsupported local action.")
        });
        var detail = Column(Body("Selected location"), Selectable(review.Source));
        if (operation == FileOperation.Delete) detail.Children.Add(Body("Move this item into a private recovery folder beside its original parent. iConnect retains a saved copy until explicit permanent deletion. Recovery requires this volume and its parent folder to remain accessible."));
        else { detail.Children.Add(Body("Destination")); detail.Children.Add(Selectable(review.Destination)); detail.Children.Add(Body("An existing destination is kept. This review expires in five minutes.")); }
        if (await ShowAsync(Dialog("Confirm local " + operation, detail, operation == FileOperation.Delete ? "Move to recovery" : "Apply reviewed change")) == ContentDialogResult.Primary)
            await ShowLocalResult(await RunLocalManagement(manager => manager.Confirm(review)));
    }
    private async Task FinishLocalMove(string id)
    {
        var copies = Model.LocalFiles.Copies ?? throw new IOException("Local copy progress is unavailable.");
        var review = await Task.Run(() => copies.ReviewMoveCompletion(id));
        var detail = Column(Body("Original"), Selectable(review.Source), Body("Verified copy"), Selectable(review.Destination),
            Body($"iConnect will check both files against the saved SHA-256 for the {review.Size:N0}-byte copy. If they match, it will move the original into recovery on its current volume."));
        if (await ShowAsync(Dialog("Finish the verified move?", detail, "Move original to recovery")) == ContentDialogResult.Primary)
            await ShowLocalResult(await RunLocalManagement(manager => copies.FinishVerifiedMove(review, manager)));
    }
    private async Task ShowLocalResult(MutationInfo receipt)
    {
        await ShowAsync(Dialog("Local change result", Column(Body(receipt.Phase.ToString()), Body(receipt.Message), Body("Refresh and inspect the recorded locations before deciding on another change if the outcome needs review.")), "Done"));
    }
    private async Task InspectLocalManagement(string id)
    {
        var review = await Model.LocalFiles.ManageAsync(manager => manager.InspectDeleted(id));
        var content = Column(Body("Original · " + review.OriginalStatus), Selectable(review.Item.OriginalLocation), Body("Saved copy · " + review.SavedStatus), Selectable(review.Item.SavedLocation), Body("Restored copy · " + review.RestoredStatus), Selectable(review.Item.RestoredLocation));
        var choice = await ShowRecoveryLocationsAsync(review, content);
        if (choice == ContentDialogResult.Primary) await RunLocalManagement(manager => manager.ConfirmLocations(review.Token));
        if (choice == ContentDialogResult.Secondary) await ArchiveAbsentRecoveryAsync(review, offline: true);
    }
    private async Task PurgeLocalManagement(string id, bool restoreRemains)
    {
        var review = await Model.LocalFiles.ManageAsync(manager => restoreRemains ? manager.ReviewRemainingRestore(id) : manager.ReviewPurge(id));
        var phrase = restoreRemains ? "RESTORE REMAINS" : "DELETE";
        var confirmation = new TextBox { Header = "Type " + phrase + " to confirm", MaxLength = 15 };
        var name = new TextBox { Header = "Restore name", Text = review.Name, MaxLength = 255 };
        var content = Column(Body($"Reviewed saved contents: {review.Files:N0} files, {review.Folders:N0} folders, {review.Bytes:N0} bytes."), Selectable(review.SavedLocation),
            Body(restoreRemains ? "Restore only the remaining data to the original parent. Earlier deletion cannot be undone. Existing items are kept." : "Permanently delete this saved copy. This cannot be undone through iConnect and does not securely erase storage media. Failure can leave partially deleted contents."), Selectable(review.OriginalLocation));
        if (restoreRemains) content.Children.Add(name); content.Children.Add(confirmation);
        var dialog = Dialog(restoreRemains ? "Restore incomplete local copy?" : "Permanently delete local saved copy?", content, restoreRemains ? "Restore remaining data" : "Permanently delete saved copy"); dialog.IsPrimaryButtonEnabled = false;
        confirmation.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = confirmation.Text == phrase;
        if (await ShowAsync(dialog) != ContentDialogResult.Primary) return;
        var restoreName = name.Text; var confirmed = confirmation.Text;
        if (restoreRemains) await ShowLocalResult(await RunLocalManagement(manager => manager.RestoreRemaining(review.Token, restoreName, confirmed)));
        else { var result = await RunLocalManagement(manager => manager.Purge(review.Token, confirmed)); await ShowAsync(Dialog("Local permanent deletion result", Body(result.Message), "Done")); }
    }
}
