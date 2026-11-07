using IConnect.Storage.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IConnect.Desktop;

public sealed partial class MainWindow
{
    private string localRecoveryQuery = "";
    private int localRecoveryPage;
    private void RenderLocalRecovery()
    {
        Page.Children.Add(Action("Review deleted items on this computer", Model.RefreshLocalDeletedAsync));
        if (!Model.LocalDeletedLoaded) return;
        if (Model.LocalDeleted.Count == 0) { Page.Children.Add(Body("No saved deleted items in this profile.")); return; }
        var query = new TextBox { Header = "Find a saved item or original location", Text = localRecoveryQuery, MaxLength = 256, MinWidth = 260 };
        Task Search() { localRecoveryQuery = query.Text.Trim(); localRecoveryPage = 0; Render(); return Task.CompletedTask; }
        query.KeyDown += async (_, args) => { if (args.Key == Windows.System.VirtualKey.Enter) { args.Handled = true; await Search(); } };
        var rows = Model.LocalDeleted.Where(x => x.Item.Name.Contains(localRecoveryQuery, StringComparison.OrdinalIgnoreCase) || x.OriginalLocation.Contains(localRecoveryQuery, StringComparison.OrdinalIgnoreCase)).ToArray();
        var pages = Math.Max(1, (rows.Length + 19) / 20); localRecoveryPage = Math.Clamp(localRecoveryPage, 0, pages - 1);
        var saved = new StackPanel { Spacing = 10 }; saved.Children.Add(Row(query, Action("Find saved items", Search)));
        saved.Children.Add(Body($"{rows.Length:N0} matching records · page {localRecoveryPage + 1} / {pages}. Saved copies have no automatic expiry."));
        foreach (var row in rows.Skip(localRecoveryPage * 20).Take(20))
        {
            var item = row.Item;
            saved.Children.Add(Card(item.Name, row.State + " · " + item.Message,
                Body("Original location"), Selectable(row.OriginalLocation), Body("Saved recovery location"), Selectable(row.SavedLocation),
                Row(Action("Restore…", async () =>
                {
                    var name = new TextBox { Header = "Restore name", Text = item.Name, MaxLength = 255 };
                    if (await ShowAsync(Dialog("Restore " + item.Name + "?", Column(Body("Restore to the original parent folder on this computer. An existing item is kept."), Selectable(row.OriginalLocation), name), "Restore")) == ContentDialogResult.Primary)
                        await Model.RestoreLocalDeletedAsync(item.Id, name.Text);
                }, item.CanRestore),
                    Action("Restore to another folder…", () => ReviewAlternateRestoreAsync(item.Id, item.Name, offline: false), item.CanRestore)),
                Row(Action("Review locations…", () => ReviewRecoveryLocationsAsync(item.Id)),
                    Action("Export recovery details…", () => ExportRecoveryAsync(item.Id))),
                Row(Action("Permanently delete saved copy…", () => ReviewPermanentDeleteAsync(item.Id), row.CanPurge),
                    Action("Restore remaining data…", () => ReviewRemainingRestoreAsync(item.Id), row.CanRestoreRemaining)),
                Row(Action("Review recovery folder…", () => ReviewEmptyVaultAsync(item.Id, offline: false), row.CanDismiss),
                    Action("Dismiss completed record…", () => DismissRecoveryRecordAsync(item.Id, offline: false), row.CanDismiss))));
        }
        saved.Children.Add(Row(Action("Previous saved items", () => { localRecoveryPage--; Render(); return Task.CompletedTask; }, localRecoveryPage > 0),
            Action("Next saved items", () => { localRecoveryPage++; Render(); return Task.CompletedTask; }, localRecoveryPage + 1 < pages)));
        Page.Children.Add(new Expander { Header = $"Saved deleted items ({Model.LocalDeleted.Count})", Content = saved, IsExpanded = true, HorizontalAlignment = HorizontalAlignment.Stretch });
    }
    private async Task ReviewEmptyVaultAsync(string id, bool offline)
    {
        var review = offline
            ? await Model.LocalFiles.ManageAsync(manager => manager.ReviewEmptyVault(id))
            : await Model.ReviewEmptyVaultAsync(id);
        if (review.Token is not { } token)
        {
            await ShowAsync(Dialog("Recovery folder absent", Column(
                Body("There is no recovery folder at this recorded path."), Selectable(review.Location)), "Done"));
            return;
        }
        var detail = Column(Selectable(review.Location),
            Body("iConnect will recheck the folder's identity and contents and remove it only if it is still empty. The recovery receipt is kept."));
        if (await ShowAsync(Dialog("Remove this empty recovery folder?", detail, "Remove empty folder")) != ContentDialogResult.Primary) return;
        if (offline) await RunLocalManagement(manager => { manager.RemoveEmptyVault(token); return true; });
        else await Model.RemoveEmptyVaultAsync(token);
        await ShowAsync(Dialog("Empty recovery folder removed", Selectable(review.Location), "Done"));
    }
    private async Task DismissRecoveryRecordAsync(string id, bool offline)
    {
        var detail = Body("Dismiss this settled receipt after its 24-hour retention period. To remove its empty recovery folder, review the folder while the receipt is still available.");
        if (await ShowAsync(Dialog("Dismiss settled recovery receipt?", detail, "Dismiss receipt")) != ContentDialogResult.Primary) return;
        if (offline) await RunLocalManagement(manager => { manager.Dismiss(id); return true; });
        else await Model.DismissLocalDeletedAsync(id);
    }
    private async Task ReviewAlternateRestoreAsync(string id, string originalName, bool offline)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker(); picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync(); if (folder is null) return;
        var directory = folder.Path;
        var name = new TextBox { Header = "Restore name", Text = originalName, MaxLength = 255 };
        var selected = Column(Selectable(directory), Body("Choose a name in this folder on the same volume as the saved item."), name);
        if (await ShowAsync(Dialog("Name the restored item", selected, "Review destination")) != ContentDialogResult.Primary) return;
        var restoreName = name.Text;
        var review = offline
            ? await Model.LocalFiles.ManageAsync(manager => manager.ReviewAlternateRestore(id, directory, restoreName))
            : await Model.ReviewAlternateRestoreAsync(id, directory, restoreName);
        var detail = Column(Body("Saved item"), Selectable(review.SavedLocation), Body("Restore destination"), Selectable(review.Destination),
            Body("An existing item at the destination will be kept. This review expires in five minutes."));
        if (await ShowAsync(Dialog("Restore to this folder?", detail, "Restore")) != ContentDialogResult.Primary) return;
        if (offline) await ShowLocalResult(await RunLocalManagement(manager => manager.ConfirmAlternateRestore(review.Token)));
        else await Model.ConfirmAlternateRestoreAsync(review.Token);
    }
    private async Task ReviewRecoveryLocationsAsync(string id)
    {
        var review = await Model.InspectLocalDeletedAsync(id);
        var content = Column(Body("This checks recorded filesystem identities. Confirm updates this record only; it does not move, restore or delete files."),
            Body("Original · " + review.OriginalStatus), Selectable(review.Item.OriginalLocation),
            Body("Saved copy · " + review.SavedStatus), Selectable(review.Item.SavedLocation), Body("Restore destination · " + review.RestoredStatus));
        if (review.Item.RestoredLocation.Length != 0) content.Children.Add(Selectable(review.Item.RestoredLocation));
        if (!review.CanConfirm && !review.IsAbsent)
            content.Children.Add(Body("Keep or export this record for manual review. An interrupted permanent deletion requires explicit review of remaining data."));
        var choice = await ShowRecoveryLocationsAsync(review, content);
        if (choice == ContentDialogResult.Primary) await Model.ConfirmLocalDeletedAsync(review.Token);
        if (choice == ContentDialogResult.Secondary) await ArchiveAbsentRecoveryAsync(review, offline: false);
    }
    private Task<ContentDialogResult> ShowRecoveryLocationsAsync(RecoveryInspection review, StackPanel content)
    {
        if (review.IsAbsent)
        {
            content.Children.Add(Body("The saved object was absent from its recorded locations. It may exist elsewhere."));
            if (!review.CanArchiveAbsent)
                content.Children.Add(Body("Keep this record while its operations are pending or within the 24-hour replay window."));
        }
        var dialog = Dialog("Review " + review.Item.Item.Name, content, "Confirm recorded locations");
        dialog.IsPrimaryButtonEnabled = review.CanConfirm;
        if (review.CanArchiveAbsent) dialog.SecondaryButtonText = "Archive missing record…";
        return ShowAsync(dialog);
    }
    private async Task ArchiveAbsentRecoveryAsync(RecoveryInspection review, bool offline)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker(); picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync(); if (folder is null) return;
        var directory = folder.Path;
        var target = System.IO.Path.Combine(directory, WindowsRecovery.RecoveryExportName(review.Item.Item.Id));
        var content = Column(Body("iConnect will recheck the recorded locations and write the private metadata here before removing the active record. Files and recovery folders are kept."), Selectable(target));
        if (await ShowAsync(Dialog("Archive this missing-item record?", content, "Export and archive")) != ContentDialogResult.Primary) return;
        var exported = offline
            ? await RunLocalManagement(manager => manager.ArchiveAbsent(review.Token, directory))
            : await Model.ArchiveAbsentLocalDeletedAsync(review.Token, directory);
        await ShowAsync(Dialog("Recovery metadata archived", Selectable(exported), "Done"));
    }
    private async Task ReviewPermanentDeleteAsync(string id)
    {
        var review = await Model.PrepareLocalPurgeAsync(id);
        var confirmation = new TextBox { Header = "Type DELETE to confirm", MaxLength = 6 };
        var content = Column(Body($"Permanently remove this saved copy: {review.Files:N0} files, {review.Folders:N0} folders, {review.Bytes:N0} bytes. This cannot be undone through iConnect and does not securely erase storage media."),
            Body("Saved copy to delete"), Selectable(review.SavedLocation), Body("Original location for reference"), Selectable(review.OriginalLocation),
            Body("The reviewed contents are checked again before deletion. A failure can leave a partially deleted copy that needs review. The review expires in five minutes."), confirmation);
        var dialog = Dialog("Permanently delete " + review.Name + "?", content, "Permanently delete saved copy"); dialog.IsPrimaryButtonEnabled = false;
        confirmation.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = confirmation.Text == "DELETE";
        if (await ShowAsync(dialog) == ContentDialogResult.Primary) await Model.PurgeLocalDeletedAsync(review.Token, confirmation.Text);
    }
    private async Task ReviewRemainingRestoreAsync(string id)
    {
        var review = await Model.PrepareRemainingRestoreAsync(id);
        var name = new TextBox { Header = "Restore name", Text = review.Name, MaxLength = 255 };
        var confirmation = new TextBox { Header = "Type RESTORE REMAINS to acknowledge incomplete contents", MaxLength = 15 };
        var dialog = Dialog("Restore remaining data from " + review.Name + "?", Column(
            Body($"Only the currently saved {review.Files:N0} files and {review.Folders:N0} folders ({review.Bytes:N0} bytes) can be restored. Earlier permanent deletion cannot be undone. This is an incomplete recovery."),
            Selectable(review.SavedLocation), Body("Restore to the original parent. An existing destination is kept."), Selectable(review.OriginalLocation), name, confirmation), "Restore remaining data");
        dialog.IsPrimaryButtonEnabled = false;
        confirmation.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = confirmation.Text == "RESTORE REMAINS";
        if (await ShowAsync(dialog) == ContentDialogResult.Primary) await Model.RestoreRemainingAsync(review.Token, name.Text, confirmation.Text);
    }
    private async Task ExportRecoveryAsync(string id)
    {
        if (await ShowAsync(Dialog("Export private recovery details?", Body("The JSON export contains original/saved paths, filesystem identities and peer identifiers. It contains no file content or private device key. Choose a local folder you control; existing exports are kept."), "Choose export folder")) != ContentDialogResult.Primary) return;
        var picker = new Windows.Storage.Pickers.FolderPicker(); picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync(); if (folder is null) return;
        var path = await Model.ExportLocalDeletedAsync(id, folder.Path);
        await ShowAsync(Dialog("Recovery details exported", Selectable(path), "Done"));
    }
}
