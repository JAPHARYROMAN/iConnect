using IConnect.AppModel;
using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace IConnect.Desktop;

public sealed partial class MainWindow
{
    private int outgoingBatchPage, receivedBatchPage;
    private bool batchUiBusy;
    private Button BatchAction(string label, Func<Task> action, bool enabled = true) => Action(label, async () =>
    {
        if (batchUiBusy) return;
        batchUiBusy = true; Render();
        try { await action(); }
        finally { batchUiBusy = false; if (!closing) Render(); }
    }, enabled && !batchUiBusy);
    private static string CollisionLabel(UploadCollision collision) => collision switch
    { UploadCollision.UploadKeepBoth => "Keep both copies", UploadCollision.UploadSkip => "Skip existing names", _ => "Report existing names as issues" };
    private static string BatchLabel(string label, string id) => label.Length == 0 ? "Saved batch · " + id[..8] : label;
    private UIElement BatchPaging(int page, int pages, Action<int> change) => Row(
        Action("Previous batches", () => { change(page - 1); Render(); return Task.CompletedTask; }, page > 0),
        Body($"Page {page + 1} / {pages}"),
        Action("Next batches", () => { change(page + 1); Render(); return Task.CompletedTask; }, page + 1 < pages));

    private async Task PrepareBatchAsync()
    {
        var peers = Model.Peers.ToArray();
        if (peers.Length == 0) throw new InvalidOperationException("Pair a device before preparing private copies.");
        var peerChoice = new ComboBox { Header = "Receiving device", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var peer in peers) peerChoice.Items.Add(peer.Name + " · " + peer.Id[..8]);
        peerChoice.SelectedIndex = Math.Max(0, Array.FindIndex(peers, x => x.Id == Model.ActivePeerId));
        var collision = new ComboBox { Header = "If a receiving name exists", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var choice in new[] { UploadCollision.UploadKeepBoth, UploadCollision.UploadSkip, UploadCollision.UploadFail }) collision.Items.Add(CollisionLabel(choice));
        collision.SelectedIndex = 0;
        var paths = new List<string>(); var selection = new StackPanel { Spacing = 8 };
        var dialog = Dialog("Prepare files and folders", Column(Body("Choose up to 128 roots. iConnect makes private copies of accessible files and nested or empty folders. Unavailable items remain visible as issues. Originals stay intact and unshared. Review the finished plan before queueing it."),
            peerChoice, collision, selection), "Prepare private batch");
        dialog.IsPrimaryButtonEnabled = false;
        void RefreshSelection()
        {
            selection.Children.Clear(); selection.Children.Add(Body($"{paths.Count} / 128 selected roots"));
            selection.Children.Add(Action("Add files…", async () =>
            {
                var picker = new FileOpenPicker(); picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                var files = await picker.PickMultipleFilesAsync();
                var added = files.Select(x => x.Path).Where(x => !paths.Contains(x, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (paths.Count + added.Length > UploadBatchRules.MaxRoots) throw new InvalidOperationException("Select at most 128 roots per batch.");
                paths.AddRange(added); RefreshSelection();
            }, paths.Count < UploadBatchRules.MaxRoots));
            selection.Children.Add(Action("Add a folder…", async () =>
            {
                var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                var folder = await picker.PickSingleFolderAsync();
                if (folder is not null && !paths.Contains(folder.Path, StringComparer.OrdinalIgnoreCase))
                { if (paths.Count == UploadBatchRules.MaxRoots) throw new InvalidOperationException("Select at most 128 roots per batch."); paths.Add(folder.Path); }
                RefreshSelection();
            }, paths.Count < UploadBatchRules.MaxRoots));
            foreach (var path in paths.ToArray()) selection.Children.Add(Column(Selectable(path), Action("Remove selected root", () =>
            { paths.Remove(path); RefreshSelection(); return Task.CompletedTask; })));
            dialog.IsPrimaryButtonEnabled = paths.Count > 0;
        }
        RefreshSelection();
        var form = dialog.Content; dialog.Content = null;
        dialog.Content = new ScrollViewer { Content = form, MaxHeight = 460 };
        if (await ShowAsync(dialog) != ContentDialogResult.Primary) return;
        await Model.PrepareOutgoingBatch(peers[peerChoice.SelectedIndex].Id, paths.ToArray(),
            collision.SelectedIndex switch { 1 => UploadCollision.UploadSkip, 2 => UploadCollision.UploadFail, _ => UploadCollision.UploadKeepBoth });
    }
    private async Task ReviewOutgoingFolderMoveAsync(OutgoingBatchSend row)
    {
        var manifest = await Model.InspectPrivateBatch(row.Source);
        if (!manifest.Items[0].IsDirectory || manifest.Items.Skip(1).Any(x => x.Parent < 0) || manifest.Items.Any(x => x.Error.Length != 0))
            throw new InvalidOperationException("Choose a fully copied batch containing one complete folder tree.");
        var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var selected = await picker.PickSingleFolderAsync(); if (selected is null) return;
        var review = await Model.ReviewOutgoingFolderMoveAsync(row.Id, selected.Path);
        var detail = Column(Body("iConnect will verify every original file, obtain a fresh check of the whole receiving folder and move the selected original into recovery on this Windows volume. The saved tree is checked again after that move. Concurrent changes or interruption can require local recovery review. There is no automatic expiry of the saved original."),
            Body("Original folder"), Selectable(review.Source), Body("Receiving device"), Selectable(Model.BatchPeerName(row.Authority)),
            Body("Receiving location"), Selectable(review.Destination + " / " + review.FinalName), Body($"{review.Files} files · {review.Folders} folders · {review.Bytes:N0} bytes"));
        try
        {
            if (await ShowAsync(Dialog("Finish this verified folder move?", detail, "Move original into recovery")) == ContentDialogResult.Primary)
                await ShowLocalResult(await Model.FinishOutgoingFolderMoveAsync(review));
        }
        finally { Model.CancelOutgoingFolderMoveReview(review); }
    }

    private async Task ReviewBatchPlanAsync(OutgoingBatchSource source)
    {
        var manifest = await Model.InspectPrivateBatch(source.Id); var page = 0;
        var items = new StackPanel { Spacing = 8 };
        void ShowPage()
        {
            items.Children.Clear(); var pages = Math.Max(1, (manifest.Items.Count + 49) / 50);
            items.Children.Add(Row(Action("Previous items", () => { page--; ShowPage(); return Task.CompletedTask; }, page > 0), Body($"Page {page + 1} / {pages}"),
                Action("Next items", () => { page++; ShowPage(); return Task.CompletedTask; }, page + 1 < pages)));
            foreach (var index in Enumerable.Range(page * 50, Math.Min(50, manifest.Items.Count - page * 50)))
            {
                var item = manifest.Items[index]; items.Children.Add(Body($"{index + 1}. {DesktopController.BatchItemPath(manifest, index)} · " +
                    (item.Error.Length != 0 ? "Issue: " + item.Error : item.IsDirectory ? "Folder" : $"{item.Size:N0} bytes")));
            }
        }
        ShowPage();
        var canQueue = Model.IsCurrentBatchAuthority(source.Authority) && !Model.OutgoingBatchSends.Any(x => x.Source == source.Id && !x.Archived && x.Phase is not (BatchSendingPhase.Completed or BatchSendingPhase.Stopped));
        var dialog = Dialog("Private batch plan", new ScrollViewer { MaxHeight = 460, Content = Column(Body(BatchLabel(source.Label, source.Id)),
            Body($"{manifest.Items.Count} items · {source.Bytes:N0} bytes · {CollisionLabel(manifest.Collision)} · {manifest.Items.Count(x => x.Error.Length != 0)} discovery issues"), items) }, "Review queueing batch…");
        dialog.IsPrimaryButtonEnabled = canQueue;
        if (await ShowAsync(dialog) != ContentDialogResult.Primary) return;
        var another = source.MayHaveReceiverCopies || Model.OutgoingBatchSends.Any(x => x.Source == source.Id && x.Header is { Length: > 0 });
        if (await ShowAsync(Dialog(another ? "Queue another batch copy?" : "Queue this private batch?", Body(
            (another ? "Earlier receiver copies may already exist. This deliberately creates a new request. " : "") +
            "Nothing is sent until you review an approved receiving destination. Originals and retained copies stay intact."), another ? "Queue another copy" : "Queue batch")) != ContentDialogResult.Primary) return;
        await Task.Run(() => Model.QueueOutgoingBatch(source.Id, another));
    }
    private async Task ReviewBatchHistoryAsync(OutgoingBatchSend row, BatchHistoryAction action)
    {
        var review = await Task.Run(() => Model.ReviewOutgoingBatchHistory(row.Id, action));
        var (title, text, confirm) = action switch
        {
            BatchHistoryAction.Archive => ("Archive this batch send?", "This frees a queue slot and keeps the receipts. Private snapshots and receiver files stay intact. Restore this same record before checking or stopping its original receiver plan.", "Archive send"),
            BatchHistoryAction.Restore => ("Restore this batch send?", "This restores the same request and receiver receipts. It does not send anything. A removed private snapshot prevents sending; the original receiver plan can still be stopped.", "Restore send"),
            _ => ("Purge saved batch history?", $"Remove only this job's metadata and {review.Results} item receipts. This loses its resume and stop controls. Private snapshots, originals and receiver files stay intact. Cleanup can be continued if interrupted.", "Purge history")
        };
        if (await ShowAsync(Dialog(title, Body(text), confirm)) != ContentDialogResult.Primary) return;
        if (!await Task.Run(() => Model.ConfirmOutgoingBatchHistory(review))) await ContinueBatchPurgeAsync(row.Id);
    }
    private async Task ContinueBatchPurgeAsync(string id)
    {
        while (!closing && currentPage == "transfers") { if (await Task.Run(() => Model.ContinueOutgoingBatchPurge(id))) return; await Task.Yield(); }
    }
    private async Task ContinueBatchRetirementAsync(string id)
    {
        while (!closing && currentPage == "transfers") { if (await Model.ContinueIncomingBatchRetirement(id)) return; await Task.Yield(); }
    }
    private async Task ShowBatchResultsAsync(OutgoingBatchSend row)
    {
        var page = 0; var loading = false; var items = new StackPanel { Spacing = 8 };
        async Task ShowPage(int requested)
        {
            if (loading) return; loading = true;
            try
            {
                var results = await Model.BatchResultPage(row.Id, requested * 50); page = requested; var pages = Math.Max(1, (row.Items + 49) / 50);
                items.Children.Clear(); items.Children.Add(Row(Action("Previous results", () => ShowPage(page - 1), page > 0), Body($"Page {page + 1} / {pages}"),
                    Action("Next results", () => ShowPage(page + 1), page + 1 < pages)));
                foreach (var item in results) { items.Children.Add(Body($"{item.Index + 1}. {item.Name} · {item.Phase}")); if (item.Message.Length != 0) items.Children.Add(Body(item.Message)); }
            }
            finally { loading = false; }
        }
        await ShowPage(0);
        var dialog = Dialog("Batch item results", new ScrollViewer { MaxHeight = 460, Content = Column(Body(BatchLabel(row.Label, row.Id)),
            Body("An issue or uncertain copy stays available for review. Copied means the receiver verified the bytes; received files still need your trust decision."), items) }, "Done");
        dialog.CloseButtonText = ""; await ShowAsync(dialog);
    }
    private void RenderOutgoingBatches()
    {
        Page.Children.Add(Heading("Send files and folders"));
        Page.Children.Add(Body("Prepare a private batch of up to 128 roots. Review the plan, queue it, then review the receiving location. Keep iConnect open while preparing and sending. Private batches share the outgoing storage quota and never create sharing approvals."));
        Page.Children.Add(BatchAction("Choose local files and folders…", PrepareBatchAsync, Model.OwnerPolicyHealthy && Model.Peers.Count != 0));
        if (!Model.PeerAuthenticated) Page.Children.Add(Body("Connect to the paired device to send or check its receipts."));
        else if (!Model.BatchReceiverSupported) Page.Children.Add(Body("The connected device has not enabled compatible folder/file batch receiving."));
        if (batchUiBusy) Page.Children.Add(new ProgressBar { IsIndeterminate = true });
        RenderBatchSourceRecovery();
        foreach (var source in Model.PrivateOutgoingBatches)
        {
            var active = Model.OutgoingBatchPreparing(source.Id); var actions = new List<UIElement> { Body(Model.BatchPeerName(source.Authority)), Body(source.Message) };
            if (source.MayHaveReceiverCopies) actions.Add(Body("Receiver copies may already exist. Queueing this snapshot again requires an explicit additional-copy decision, even if its send history was removed."));
            if (active) actions.Add(Action("Stop preparing this batch", () => { Model.PauseBatchPreparation(source.Id); return Task.CompletedTask; }));
            else
            {
                actions.Add(BatchAction("Review batch plan…", () => ReviewBatchPlanAsync(source), source.Phase == BatchSourcePhase.Ready));
                actions.Add(BatchAction("Review private-copy removal…", async () =>
                {
                    var review = await Task.Run(() => Model.ReviewOutgoingBatchRemoval(source.Id));
                    if (await ShowAsync(Dialog("Remove private batch copies?", Body($"Remove {review.Files} private files ({review.Bytes:N0} bytes) and this snapshot's manifest. Originals and receiver copies stay intact. Saved history loses its source for resuming. This cannot be undone."), "Remove private copies")) == ContentDialogResult.Primary)
                        await Task.Run(() => Model.RemoveOutgoingBatchReviewed(review));
                }));
            }
            Page.Children.Add(RefreshableCard($"batch-source/{source.Id}", BatchLabel(source.Label, source.Id), () =>
            {
                var latest = Model.OutgoingBatchSourceSnapshot(source.Id) ?? source;
                return $"{source.Phase} · {latest.PreparedFiles} files / {latest.PreparedBytes:N0} bytes prepared";
            }, actions.ToArray()));
        }
        Page.Children.Add(Heading("Batch queue and saved history")); var rows = Model.OutgoingBatchSends;
        var pages = Math.Max(1, (rows.Count + 9) / 10); outgoingBatchPage = Math.Clamp(outgoingBatchPage, 0, pages - 1);
        Page.Children.Add(BatchPaging(outgoingBatchPage, pages, x => outgoingBatchPage = x));
        if (rows.Count == 0) Page.Children.Add(Body("No queued or archived batch sends."));
        foreach (var row in rows.Skip(outgoingBatchPage * 10).Take(10))
        {
            var active = Model.OutgoingBatchActive(row.Id); var current = Model.IsCurrentBatchAuthority(row.Authority);
            var online = current && Model.PeerAuthenticated && Model.BatchReceiverSupported && Model.ActivePeerId == row.Authority.Peer;
            var actions = new List<UIElement> { Body(Model.BatchPeerName(row.Authority)), Body(row.Message), Body(row.Destination.Length == 0 ? "No receiver plan started." : "Original receiving location: " + row.Destination) };
            actions.Add(ProgressBody(() =>
            {
                var offset = Model.OutgoingBatchCheckpoint(row.Id, row.Next) ?? row.Offset;
                if (!online || Model.BatchPayload(row.Id) is not { } payload || payload.Index != row.Next || payload.Offset != offset)
                    return offset > 0 ? $"Saved item checkpoint · {offset:N0} bytes acknowledged" : null;
                var limited = Model.TransferNetwork?.Preferences.BytesPerSecond > 0;
                var text = $"Current file (item {payload.Index + 1} of {row.Items}) · {payload.Name} · {payload.Offset:N0} / {payload.Size:N0} bytes acknowledged\n" +
                    payload.Estimate.Describe(PayloadProgressKind.Sending, limited);
                return payload.BatchRemaining > payload.Size - payload.Offset
                    ? text + $"\nBatch payload still planned · up to {payload.BatchRemaining:N0} bytes\n" + payload.BatchEstimate.Describe(PayloadProgressKind.BatchSending, limited)
                    : text;
            }));
            if (row.Purging)
            {
                actions.Add(Body($"History cleanup: {row.PurgeCursor} / {row.PurgeCount} receipts")); actions.Add(BatchAction("Continue history cleanup", () => ContinueBatchPurgeAsync(row.Id)));
            }
            else
            {
                actions.Add(BatchAction("Review item results…", () => ShowBatchResultsAsync(row), !active));
                if (active && row.Phase == BatchSendingPhase.Completed) actions.Add(Body(Model.OutgoingFolderMoveActive(row.Id)
                    ? "Verifying the original and receiving copies for this move. Recovery may need local review if interrupted."
                    : "Waiting for the receiver's saved-batch check."));
                if (active) actions.Add(Action(row.Phase == BatchSendingPhase.Completed ?
                    Model.OutgoingFolderMoveActive(row.Id) ? "Stop folder move" : Model.PublishedBatchCheckCancellationSupported ? "Stop batch check" : "Stop waiting for batch check" : "Pause this batch send",
                    () => { Model.PauseOutgoingBatch(row.Id); return Task.CompletedTask; }));
                if (row.Archived)
                {
                    actions.Add(BatchAction("Review restoring send…", () => ReviewBatchHistoryAsync(row, BatchHistoryAction.Restore)));
                    actions.Add(BatchAction("Review history purge…", () => ReviewBatchHistoryAsync(row, BatchHistoryAction.Purge)));
                }
                else
                {
                    if (row.Phase == BatchSendingPhase.Completed && row.ReceiverOutcomes is { Length: > 0 } && row.ReceiverOutcomes.All(x => x == 1))
                    {
                        actions.Add(Action("Verify saved batch", async () =>
                        {
                            await Model.VerifyPublishedBatchAsync(row.Id);
                            await ShowAsync(Dialog("Saved batch verified", Body("The receiver checked the saved identities, file bytes and contents of every received folder at the time of this check. Received files remain untrusted."), "Close"));
                        }, !active && online && Model.PublishedBatchCheckSupported));
                        if (!row.SourceRecovered) actions.Add(BatchAction("Review moving an original folder…", () => ReviewOutgoingFolderMoveAsync(row),
                            !active && online && Model.PublishedBatchCheckSupported && Model.OutgoingBatchSourceSnapshot(row.Source)?.Phase == BatchSourcePhase.Ready));
                    }
                    if (row.Phase is not (BatchSendingPhase.Completed or BatchSendingPhase.Stopped or BatchSendingPhase.Stopping))
                    {
                        var target = Model.ReceivingLocation ?? Model.Breadcrumbs.LastOrDefault(); var started = row.Header is { Length: > 0 };
                        actions.Add(Action(started ? "Review original receipt / resume…" : "Review sending batch…", async () =>
                        {
                            if (await ShowAsync(Dialog(started ? "Check the original receiver plan?" : "Send this batch?", Body(started ?
                                $"Resume only the original request in {row.Destination}. Its saved receiver receipts determine what still needs copying. Uncertain copies require review; this does not start a new receiver plan." :
                                $"Send {row.Items} items ({row.Bytes:N0} bytes) into {target?.Name}. Originals remain intact. Existing names use the collision choice in the reviewed plan."), started ? "Check / resume" : "Send batch")) == ContentDialogResult.Primary)
                                await Model.SendOutgoingBatch(row.Id, target);
                        }, !active && online && (started || (target?.Permissions & 12) == 12)));
                    }
                    if (row.Phase is not (BatchSendingPhase.Completed or BatchSendingPhase.Stopped)) actions.Add(Action(row.Phase == BatchSendingPhase.Stopping ? "Continue original batch stop…" : "Review stopping batch…", async () =>
                    {
                        if (await ShowAsync(Dialog("Stop this batch?", Body("Pause local work and stop its original receiver plan. Published or uncertain receiver copies are kept. Pending private receiver staging is cleaned before the receiver confirms the stop. Reconnect to that device if cleanup is interrupted."), "Stop batch")) == ContentDialogResult.Primary) await Model.StopOutgoingBatch(row.Id);
                    }, current && (row.Header is not { Length: > 0 } || online)));
                    actions.Add(BatchAction("Review archiving send…", () => ReviewBatchHistoryAsync(row, BatchHistoryAction.Archive), !active));
                }
            }
            Page.Children.Add(RefreshableCard($"batch-send/{row.Id}", BatchLabel(row.Label, row.Id), $"{(row.Archived ? "Archived · " : "")}{row.Phase} · {row.Next} / {row.Items} items · {row.Bytes:N0} bytes", actions.ToArray()));
        }
    }
    private void RenderIncomingBatchHistory()
    {
        var rows = Model.IncomingBatchPlans; if (rows.Count == 0) return;
        Page.Children.Add(Heading("Received batch history"));
        Page.Children.Add(Body("Retiring a closed plan removes detailed local receipts, keeping its compact outcome and all received files. A closed-history reset ends old receipt lookup and preserves the old metadata in a private backup."));
        var pages = Math.Max(1, (rows.Count + 9) / 10); receivedBatchPage = Math.Clamp(receivedBatchPage, 0, pages - 1);
        Page.Children.Add(BatchPaging(receivedBatchPage, pages, x => receivedBatchPage = x));
        foreach (var row in rows.Skip(receivedBatchPage * 10).Take(10))
        {
            var actions = new List<UIElement> { Body($"Created {DateTimeOffset.FromUnixTimeMilliseconds(MutationRules.CreatedAt(row.Id)):u} · {row.Id[..8]}") };
            actions.Add(ProgressBody(() => string.Join("\n", Model.IncomingBatchFileProgress(row).Select(file =>
                $"Item {file.Index + 1} / {row.Items} · {file.Name} · {file.Offset:N0} / {file.Size:N0} bytes saved\n{Model.IncomingBatchFileStatus(file)}"))));
            if (row.Retired) actions.Add(Body("Compact receipt retained. Detailed item lookup is unavailable."));
            else if (row.Retiring)
            {
                actions.Add(Body($"Retiring detailed receipts · checkpoint {row.RetirementCursor} / {row.Items * 2}"));
                actions.Add(BatchAction("Continue receipt retirement", () => ContinueBatchRetirementAsync(row.Id)));
            }
            else if (UploadBatchRules.IsTerminal(row.Phase)) actions.Add(BatchAction("Review receipt retirement…", async () =>
            {
                var review = await Model.ReviewIncomingBatchRetirement(row.Id);
                if (await ShowAsync(Dialog("Retire detailed received-batch receipts?", Body($"Remove {review.MetadataFiles} metadata files ({review.MetadataBytes:N0} bytes). Received folders, files and the terminal outcome stay intact. This loses detailed item receipt lookup. Cleanup can be continued if interrupted."), "Retire detailed receipts")) == ContentDialogResult.Primary && !await Model.ConfirmIncomingBatchRetirement(review)) await ContinueBatchRetirementAsync(row.Id);
            }));
            Page.Children.Add(RefreshableCard($"batch-receive/{row.Id}", Model.IncomingBatchPeerName(row.Peer), $"{row.Items} items · {row.Bytes:N0} bytes · {row.Phase.ToString().Replace("UploadPlan", "", StringComparison.Ordinal)}", actions.ToArray()));
        }
        Page.Children.Add(BatchAction("Review closed-history reset…", async () =>
        {
            if (await ShowAsync(Dialog("Reset closed batch history?", Body("Preserve the closed receipt store in a private backup and start a new receiver context. Existing files stay intact. Earlier senders lose receipt lookup and must review existing copies before deliberately sending another batch."), "Preserve and reset")) == ContentDialogResult.Primary) await Model.ResetClosedBatchHistory();
        }, rows.All(x => UploadBatchRules.IsTerminal(x.Phase) && !x.Retiring)));
    }
}
