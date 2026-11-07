using IConnect.Storage.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace IConnect.Desktop;

public sealed partial class MainWindow
{
    private string receivingPartNotice = "";
    private bool ReceivingPartScreenOpen() => !closing && currentPage == "transfers";
    private async Task ContinueReceivingPartCleanupAsync()
    {
        while (ReceivingPartScreenOpen())
        {
            if (await Model.ContinueReceivingPartRemoval(ReceivingPartScreenOpen))
            { receivingPartNotice = "Reviewed cleanup finished. Choose the folder again to inspect remaining or newly appearing files."; return; }
            await Task.Yield();
        }
    }
    private async Task InspectReceivingPartsAsync(ReceivingPartFolder folder)
    {
        var page = 0; IReadOnlyList<ReceivingPartEntry> entries = []; var items = new StackPanel { Spacing = 8 };
        async Task ShowPage()
        {
            entries = await Model.InspectReceivingParts(folder.Id, page * 50); items.Children.Clear();
            items.Children.Add(Body($"Page {page + 1} · {folder.Location}"));
            items.Children.Add(Row(Action("Previous partial files", async () => { page--; await ShowPage(); }, page > 0),
                Action("Next partial files", async () => { page++; await ShowPage(); }, entries.Count == 50)));
            if (entries.Count == 0) items.Children.Add(Body("No more supported unreferenced partial files."));
            foreach (var entry in entries) items.Children.Add(Selectable($"{entry.Name} · {entry.Bytes:N0} bytes"));
        }
        await ShowPage();
        var dialog = Dialog("Unidentified destination-side partial files", new ScrollViewer { MaxHeight = 460, Content = Column(
            Body("Local inventory only. Names do not prove where these bytes came from. Current receiving receipts in this profile protect their filenames and recorded native file identities. Other installations and manually renamed files may be unidentified."), items) }, "Done");
        await ShowAsync(dialog);
    }
    private async Task ReviewReceivingPartRemovalAsync(ReceivingPartFolder folder)
    {
        var review = await Model.ReviewReceivingPartRemoval(folder.Id);
        var typed = new TextBox { Header = "Type REMOVE to confirm permanent removal", MaxLength = 6 };
        var dialog = Dialog("Permanently remove reviewed partial files?", Column(Selectable(folder.Location),
            Body($"Remove the exact reviewed {review.Files:N0} files ({review.Bytes:N0} bytes) from this folder. Filenames alone do not establish that they are abandoned transfers; inspect the candidate list first. Removal cannot be undone, and archived transfers cannot resume from these bytes. Current receipts in this profile stay protected. New or changed files require another review. Restarting iConnect ends this permission."), typed), "Remove reviewed partial files");
        dialog.IsPrimaryButtonEnabled = false;
        typed.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = typed.Text == "REMOVE";
        if (await ShowAsync(dialog) != ContentDialogResult.Primary) return;
        if (await Model.ConfirmReceivingPartRemoval(review, ReceivingPartScreenOpen))
            receivingPartNotice = "Reviewed cleanup finished. Choose the folder again to inspect remaining or newly appearing files.";
        else await ContinueReceivingPartCleanupAsync();
    }
    private void RenderReceivingPartRecovery()
    {
        var actions = new List<UIElement>();
        actions.Add(BatchAction("Choose a destination folder to inspect…", async () =>
        {
            var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync(); if (folder is null) return;
            if (await ShowAsync(Dialog("Inspect destination-side partial files?", Column(Selectable(folder.Path),
                Body("Connections and transfer work will pause. Inspect only this selected folder, without descending into child folders. No sharing approval is created. Current receiving receipts stay protected; unreadable or changed ownership metadata blocks cleanup. Inspection keeps every file.")), "Pause and inspect")) != ContentDialogResult.Primary) return;
            await Model.SelectReceivingPartFolder(folder.Path); receivingPartNotice = "Folder inspected. Review filenames and bytes before deciding about permanent removal.";
        }, Model.OwnerPolicyHealthy));
        if (Model.ReceivingPartFolder is { } selected)
        {
            actions.Add(Selectable(selected.Location));
            actions.Add(Body($"Last selection scan: {selected.Files:N0} candidate files · {selected.Bytes:N0} bytes. Current counts are rechecked during inspection and review."));
            if (Model.ReceivingPartCleanup is { } progress)
            {
                actions.Add(Body($"Reviewed cleanup: {progress.Removed:N0} / {progress.ReviewedFiles:N0} files"));
                actions.Add(BatchAction("Continue reviewed partial-file cleanup", ContinueReceivingPartCleanupAsync, !Model.Connected && !Model.Connecting));
            }
            else
            {
                actions.Add(BatchAction("Inspect candidate partial files…", () => InspectReceivingPartsAsync(selected), !Model.Connected && !Model.Connecting));
                actions.Add(BatchAction("Review partial-file removal…", () => ReviewReceivingPartRemovalAsync(selected), !Model.Connected && !Model.Connecting));
            }
        }
        if (receivingPartNotice.Length != 0) actions.Add(Body(receivingPartNotice));
        Page.Children.Add(Card("Destination-side partial-file recovery",
            "Unidentified files with iConnect temporary filenames can remain after receiving history is lost or archived. Choose their exact folder for a local review. Cleanup never adopts a filename as receiving authority and removes at most 64 reviewed files per call.", actions.ToArray()));
    }
}
