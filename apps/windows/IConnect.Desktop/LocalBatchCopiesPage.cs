using IConnect.Storage.Windows;
using Microsoft.UI.Xaml.Controls;

namespace IConnect.Desktop;

public sealed partial class MainWindow
{
    private readonly Dictionary<string, int> localBatchPages = [];
    private int localBatchJobPage, localOrphanPage;
    private void RenderLocalBatchCopies()
    {
        var browser = Model.LocalFiles; var copies = browser.BatchCopies;
        if (copies is null) { Page.Children.Add(Body(browser.BatchCopyIssue)); return; }
        if (copies.Jobs.Count == 0 && copies.OrphanFiles.Count == 0 && copies.RecoveryIssues.Count == 0 && copies.PreservedRecords == 0 && !copies.Planning) return;
        var idle = !copies.Planning && copies.Jobs.All(x => !copies.Active(x.Id)) && copies.OrphanFiles.All(x => !copies.OrphanActive(x.Id));
        Page.Children.Add(Heading("File and folder batches on this computer"));
        Page.Children.Add(Body("Review a frozen plan of up to 4,096 items before choosing a destination. New source items are excluded. Original files stay intact. Closing the app pauses copies; resume them here after restart."));
        if (copies.Planning) Page.Children.Add(Row(new ProgressRing { IsActive = true, Width = 20, Height = 20 }, Action("Cancel local batch planning", () => { browser.CancelBatchPlanning(); return Task.CompletedTask; })));
        foreach (var issue in copies.RecoveryIssues)
            Page.Children.Add(Card("Local batch progress needs review", issue.Message, Action("Preserve damaged local batch metadata…", async () =>
            {
                if (await ShowAsync(Dialog("Preserve damaged local batch progress?", Body("Only the identified damaged metadata is moved aside. Sources, partials and completed files are kept. Missing source access needs a new source selection."), "Preserve metadata")) == ContentDialogResult.Primary)
                    await Task.Run(() => copies.Preserve(issue.Id));
            }, issue.CanArchive && idle)));
        if (copies.PreservedRecords > 0) Page.Children.Add(Action($"Remove {copies.PreservedRecords} preserved local batch metadata records…", async () =>
        {
            if (await ShowAsync(Dialog("Remove preserved metadata?", Body("Sources, active progress, partials and completed copies are kept."), "Remove metadata")) == ContentDialogResult.Primary) await Task.Run(copies.ClearPreserved);
        }, idle));
        var jobs = copies.Jobs; var jobPages = Math.Max(1, (jobs.Count + 7) / 8); localBatchJobPage = Math.Clamp(localBatchJobPage, 0, jobPages - 1);
        foreach (var job in jobs.Skip(localBatchJobPage * 8).Take(8))
        {
            var transfer = job.Transfer; var active = copies.Active(job.Id); var controls = new StackPanel { Spacing = 8 };
            controls.Children.Add(Body("Sources:\n" + string.Join("\n", job.Sources)));
            controls.Children.Add(Body($"{job.Files:N0} files · {job.Directories:N0} folders · {job.Bytes:N0} planned bytes · {job.Issues:N0} source issues"));
            var pages = Math.Max(1, (job.Items + 19) / 20); var page = Math.Clamp(localBatchPages.GetValueOrDefault(job.Id), 0, pages - 1);
            if (transfer is null)
            {
                if (job.HasSource) foreach (var item in copies.Page(job.Id, page)) controls.Children.Add(Body($"{item.Location} · {(item.Directory ? "Folder" : $"{item.Bytes:N0} bytes")}{(item.Error.Length == 0 ? "" : " · " + item.Error)}", 12));
                if (!job.CopyMayExist && job.MoveDestination.Length != 0)
                    controls.Children.Add(Action("Start reviewed folder move copy…", async () =>
                    {
                        if (await ShowAsync(Dialog("Start this folder move copy?", Selectable(job.MoveDestination), "Start copy")) == ContentDialogResult.Primary)
                            await Task.Run(() => copies.StartFolderMoveCopy(job.Id));
                    }, idle));
                else if (!job.CopyMayExist) controls.Children.Add(Action("Review destination for this plan…", () => PickLocalBatchDestination(job), copies.RecoveryIssues.Count == 0 && !copies.Planning));
                else
                {
                    var retained = copies.OrphanFiles.Count(x => x.BatchId == job.Id);
                    controls.Children.Add(Body($"Receiving history is missing. Copies may already exist. {retained:N0} retained file receipts need separate review below. This source cannot start another copy."));
                    controls.Children.Add(Action("Review removal of missing progress…", async () =>
                    {
                        var review = await Task.Run(() => copies.ReviewMissing(job.Id));
                        if (await ShowAsync(Dialog("Remove references to missing batch progress?", Body("The original receiving history is missing. Completed files and folders may remain. This removes only retained source and progress references. Originals, completed copies and unidentified partials stay intact. Inspect earlier destinations before making a fresh source selection for another copy."), "Remove references")) == ContentDialogResult.Primary)
                            await Task.Run(() => copies.ConfirmRemoveMissing(review));
                    }, idle && retained == 0 && copies.RecoveryIssues.Count == 0));
                }
            }
            else
            {
                controls.Children.Add(Body($"Destination: {transfer.DisplayPath}\n{transfer.Completed:N0} / {job.Items:N0} completed · {transfer.VerifiedBytes:N0} verified bytes · {transfer.Phase}"));
                if (transfer.Error.Length != 0) controls.Children.Add(Body(transfer.Error));
                var activeFile = transfer.Phase == BatchPhase.Running ? copies.ActiveFileProgress(job.Id) : null;
                if (activeFile is not null)
                {
                    DownloadJob Snapshot() => copies.FileProgress(activeFile.Id) ?? activeFile;
                    controls.Children.Add(ProgressBody(() =>
                    {
                        var current = Snapshot();
                        return $"Current file · {current.Name} · {current.Offset:N0} / {current.Size:N0} bytes";
                    }));
                    controls.Children.Add(ProgressBody(() => browser.CopyPayloadText(Snapshot(), true), 12));
                }
                foreach (var item in transfer.Items.Skip(page * 20).Take(20))
                {
                    controls.Children.Add(Body($"{item.Source.Name} · {item.Phase}{(item.LocalName.Length == 0 ? "" : " · " + item.LocalName)}{(item.Error.Length == 0 ? "" : " · " + item.Error)}", 12));
                    if (item.DownloadId.Length != 0 && item.DownloadId != activeFile?.Id && copies.FileProgress(item.DownloadId) is { } file)
                        controls.Children.Add(ProgressBody(() => browser.CopyPayloadText(copies.FileProgress(file.Id) ?? file, true), 12));
                }
                if (copies.Verifying(job.Id))
                    controls.Children.Add(Row(Body("Checking destination…"), Action("Cancel destination check", () => { copies.CancelVerification(); return Task.CompletedTask; })));
                else if (active) controls.Children.Add(Action("Pause local batch", () => { copies.Pause(job.Id); return Task.CompletedTask; }));
                else if (transfer.Phase is BatchPhase.Paused or BatchPhase.Interrupted)
                    controls.Children.Add(Action("Resume and verify local batch", () => { _ = copies.Resume(job.Id); return Task.CompletedTask; }, copies.RecoveryIssues.Count == 0));
                else if (transfer.HasBoundDestination)
                {
                    controls.Children.Add(Action("Verify completed destination…", () => VerifyLocalBatchDestinationAsync(copies, job.Id), idle));
                    if (job.MoveDestination.Length != 0)
                        controls.Children.Add(Action("Review original folder for recovery…", () => FinishLocalFolderMoveAsync(copies, job.Id), idle && !browser.ManagementBusy));
                }
                else if (transfer.Phase == BatchPhase.Completed)
                    controls.Children.Add(Body("This older receipt lacks destination file identities for a full destination check."));
                if (copies.CanRemove(job.Id)) controls.Children.Add(Action("Remove local batch record…", async () =>
                {
                    if (await ShowAsync(Dialog("Remove this local batch record?", Body("This removes progress and its source plan. Original and completed destination files and folders are kept."), "Remove record")) == ContentDialogResult.Primary)
                    { await Task.Run(() => copies.Remove(job.Id)); localBatchPages.Remove(job.Id); }
                }, idle));
            }
            controls.Children.Add(Row(Action("Previous batch items", () => { localBatchPages[job.Id] = page - 1; Render(); return Task.CompletedTask; }, page > 0),
                Body($"{page + 1} / {pages}"), Action("Next batch items", () => { localBatchPages[job.Id] = page + 1; Render(); return Task.CompletedTask; }, page + 1 < pages)));
            if (transfer is null ? !job.CopyMayExist : !copies.CanRemove(job.Id)) controls.Children.Add(Action("Cancel local batch…", async () =>
            {
                if (await ShowAsync(Dialog("Cancel this local batch?", Body("Completed files and folders remain. Only identified private partial files can be cleaned up. Uncertain cleanup stays available for review."), "Cancel batch")) == ContentDialogResult.Primary) await copies.Cancel(job.Id);
            }));
            Page.Children.Add(RefreshableCard($"local-batch/{job.Id}", "Local file and folder copy", transfer?.Phase.ToString() ?? (job.CopyMayExist ? "Receiving history missing · copies may exist" : "Draft awaiting destination"), controls));
        }
        if (jobPages > 1) Page.Children.Add(Row(Action("Previous local batch records", () => { localBatchJobPage--; Render(); return Task.CompletedTask; }, localBatchJobPage > 0), Body($"{localBatchJobPage + 1} / {jobPages}"), Action("Next local batch records", () => { localBatchJobPage++; Render(); return Task.CompletedTask; }, localBatchJobPage + 1 < jobPages)));
        RenderLocalOrphanReceipts(idle);
    }
    private async Task VerifyLocalBatchDestinationAsync(WindowsLocalBatchCopies copies, string id)
    {
        LocalBatchVerification result;
        try { result = await copies.VerifyCompletedAsync(id); }
        catch (OperationCanceledException) { return; }
        var detail = Column(Selectable(result.Destination),
            Body($"{result.Files:N0} files, {result.Folders:N0} folders, and {result.Bytes:N0} bytes matched the completed copy receipt during this check."));
        await ShowAsync(Dialog("Destination verified", detail, "Done"));
    }
    private async Task FinishLocalFolderMoveAsync(WindowsLocalBatchCopies copies, string id)
    {
        var review = await Task.Run(() => copies.ReviewFolderMove(id));
        var detail = Column(Body("Original folder"), Selectable(review.Source), Body("Verified copy"), Selectable(review.Destination),
            Body($"iConnect will check both trees with {review.Files:N0} files, {review.Folders:N0} folders and {review.Bytes:N0} bytes. The original will go into recovery on its own volume."));
        if (await ShowAsync(Dialog("Finish this verified folder move?", detail, "Move original into recovery")) != ContentDialogResult.Primary) return;
        var browser = Model.LocalFiles;
        try { await ShowLocalResult(await browser.ManageWorkAsync(manager => copies.FinishFolderMoveAsync(review, manager))); }
        finally { await browser.RefreshManagementAsync(); await browser.RefreshAfterManagementAsync(); }
    }
    private void RenderLocalOrphanReceipts(bool idle)
    {
        var copies = Model.LocalFiles.BatchCopies!; var receipts = copies.OrphanFiles; if (receipts.Count == 0) return;
        Page.Children.Add(Heading("Unattached local file receipts"));
        Page.Children.Add(Body("These file receipts have no matching item in the available batch history. They cannot resume source reads. Stop an interrupted receipt to clean up its identified private part, or recheck only a recorded published copy. Completed files remain when a receipt is removed."));
        var pages = Math.Max(1, (receipts.Count + 19) / 20); localOrphanPage = Math.Clamp(localOrphanPage, 0, pages - 1);
        foreach (var receipt in receipts.Skip(localOrphanPage * 20).Take(20))
        {
            var active = copies.OrphanActive(receipt.Id); var controls = new StackPanel { Spacing = 8 };
            controls.Children.Add(Body($"Destination: {receipt.DisplayPath}\n{receipt.Offset:N0} / {receipt.Size:N0} received bytes · {receipt.Phase}"));
            controls.Children.Add(ProgressBody(() => copies.ReadFinalization(receipt.Id)?.Describe(), 12));
            if (receipt.FinalName.Length != 0) controls.Children.Add(Body("Recorded copy: " + receipt.FinalName));
            if (receipt.Error.Length != 0) controls.Children.Add(Body(receipt.Error));
            if (active) controls.Children.Add(Action("Pause receipt verification", () => { copies.PauseOrphan(receipt.Id); return Task.CompletedTask; }));
            else
            {
                if (receipt.FinalName.Length != 0 && receipt.Hash.Length == 64 && receipt.Offset == receipt.Size && receipt.TemporaryIdentity.Length != 0)
                    controls.Children.Add(Action("Recheck only this recorded copy", () => { _ = copies.RecheckOrphanAsync(receipt.Id); return Task.CompletedTask; }, idle));
                if (copies.CanRemoveOrphan(receipt.Id)) controls.Children.Add(Action("Remove this local file receipt…", async () =>
                {
                    if (await ShowAsync(Dialog("Remove this unattached receipt?", Body("Only this finished or safely stopped receipt is removed. Originals and completed copies are kept. Missing batch progress stays available for a separate review."), "Remove receipt")) == ContentDialogResult.Primary) await Task.Run(() => copies.RemoveOrphan(receipt.Id));
                }, idle));
                else controls.Children.Add(Action("Stop and clean up this receipt…", async () =>
                {
                    if (await ShowAsync(Dialog("Stop this unattached local receipt?", Body("No source reads or new copies are allowed. Cleanup targets only the private part identified by this receipt. Published or uncertain files are kept for review."), "Stop receipt")) == ContentDialogResult.Primary) await copies.StopOrphanAsync(receipt.Id);
                }, idle));
            }
            Page.Children.Add(RefreshableCard($"local-orphan/{receipt.Id}", receipt.Name, "Missing matching batch item", controls));
        }
        Page.Children.Add(Row(Action("Previous unattached receipts", () => { localOrphanPage--; Render(); return Task.CompletedTask; }, localOrphanPage > 0), Body($"{localOrphanPage + 1} / {pages}"), Action("Next unattached receipts", () => { localOrphanPage++; Render(); return Task.CompletedTask; }, localOrphanPage + 1 < pages)));
    }
    private async Task PickLocalBatchDestination(LocalBatchCopyJob job)
    {
        var collision = new ComboBox { Header = "If a destination name exists", ItemsSource = new[] { "Keep both (new name)", "Skip the conflicting item or folder subtree", "Stop the conflicting item or folder subtree" }, SelectedIndex = 0 };
        var dialog = Dialog("Copy this reviewed local plan?", Column(Body($"{job.Items:N0} items · {job.Bytes:N0} bytes · {job.Issues:N0} source issues"),
            Body("Folders are created separately and never merged into existing folders. Unsupported source items are reported. Each copied file must pass byte verification and Windows attachment checks; copies are marked untrusted."),
            Body("Choose a normal local destination outside the selected source folders. Synchronization software may upload folders you choose. Restarted jobs wait for your action."), collision), "Choose destination…");
        if (await ShowAsync(dialog) != ContentDialogResult.Primary) return;
        var policy = collision.SelectedIndex switch { 1 => DownloadCollision.Skip, 2 => DownloadCollision.Fail, _ => DownloadCollision.KeepBoth };
        var picker = new Windows.Storage.Pickers.FolderPicker(); picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var destination = await picker.PickSingleFolderAsync();
        if (destination is not null) await Task.Run(() => Model.LocalFiles.BatchCopies!.ChooseDestination(job.Id, destination.Path, policy));
        Render();
    }
}
