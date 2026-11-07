using IConnect.AppModel;
using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace IConnect.Desktop;

public sealed partial class MainWindow
{
    private int historyPage;
    private int incomingUploadPage;
    private string fileQuery = "";
    private readonly Dictionary<string, int> batchResultPages = [];
    private int recoveryPage;
    private static string RecoveryState(DeletedState state) => state switch { DeletedState.DeletedStored => "Saved for recovery", DeletedState.DeletedRestored => "Restored", _ => "Needs review" };
    private void RenderHome()
    {
        Page.Children.Add(Heading("Your files, within reach"));
        Page.Children.Add(Body("Connect your devices and access only the locations you have approved."));
        if (Model.TransferRecovery.Count != 0 || Model.OrphanedBatchFiles.Count != 0)
            Page.Children.Add(Card("Saved progress needs review", "Some transfer records need an owner decision. Partial files and saved copies have been kept.", Go("Review saved progress", "transfers")));
        Page.Children.Add(Card(Model.Settings.DeviceName, Model.Status,
            Model.Connected || Model.Connecting || Model.Availability.AutomaticAllowed ? Action("Pause availability", Model.PauseAvailabilityAsync, id: "pause-availability") :
                Model.Settings.Coordinator.Length == 0 ? Go("Set up connection", "settings") : Action("Connect", Model.ResumeAvailabilityAsync, accent: true, id: "connect-coordinator")));
        Page.Children.Add(Card("Trusted devices", Model.Peers.Count == 0 ? "Pair your phone to get started. Both devices must approve the verification code."
            : $"{Model.Peers.Count} paired device{(Model.Peers.Count == 1 ? "" : "s")}. Open the phone app before starting a connection.",
            Row(Go("Manage devices", "devices"), Action("Add device", Model.CreateInvitationAsync, Model.Connected, id: "add-device"))));
        Page.Children.Add(Card("Shared by this computer", Model.Grants.Count == 0 ? "No drives or folders are approved. Pairing never shares storage automatically."
            : $"{Model.Grants.Count} storage approval{(Model.Grants.Count == 1 ? "" : "s")}. Review permissions or remove access at any time.", Go("Review shared locations", "shares")));
        Page.Children.Add(Card("Phone files", "Browse the folders approved on your connected phone. The phone must remain available.",
            Action("Browse phone", async () => { Navigate("files"); await Model.BrowseAsync(); }, Model.PeerAuthenticated)));
    }
    private void RenderDevices()
    {
        Page.Children.Add(Heading("Devices"));
        Page.Children.Add(Body("Trust is saved on this computer. File access is approved separately for each device."));
        Page.Children.Add(Row(Action("Add device", Model.CreateInvitationAsync, Model.Connected, true, "add-device"),
            Model.Connected || Model.Connecting || Model.Availability.AutomaticAllowed ? Action("Pause availability", Model.PauseAvailabilityAsync) : Action("Connect coordinator", Model.ResumeAvailabilityAsync)));
        if (Model.Peers.Count == 0) Page.Children.Add(Card("No paired devices", "Connect to the coordinator, select Add device, then scan the QR invitation in iConnect on Android."));
        foreach (var peer in Model.Peers)
        {
            var connected = Model.PeerAuthenticated && Model.ActivePeerId == peer.Id;
            Page.Children.Add(Card(peer.Name, connected ? "Authenticated direct connection" : Model.PeerAvailability(peer.Id),
                Selectable(peer.Id), Row(Action(connected ? "Reconnect" : "Connect to device", () => Model.ConnectDeviceAsync(peer.Id), Model.Connected),
                Action("Browse files", async () => { Navigate("files"); await Model.BrowseAsync(); }, connected),
                Action("Revoke device", () => ConfirmRevocationAsync(peer)))));
        }
    }
    private void RenderShares()
    {
        Page.Children.Add(Heading("Shared locations"));
        Page.Children.Add(Body("Approve a folder or explicitly choose an entire drive for one trusted device. Windows access rules and sensitive-location exclusions still apply."));
        Page.Children.Add(Row(Action("Share a folder", PickFolderAsync, Model.Peers.Count != 0, true, "share-folder"),
            Action("Share a drive", PickDriveAsync, Model.Peers.Count != 0)));
        if (Model.Peers.Count == 0) Page.Children.Add(Card("Pair a device first", "A storage approval belongs to one trusted device.", Go("Open devices", "devices")));
        else if (Model.Grants.Count == 0) Page.Children.Add(Card("Nothing is shared", "Choose a location deliberately. New approvals start with read-only access."));
        RenderLocalRecovery();
        foreach (var grant in Model.Grants)
        {
            var peer = Model.Peers.FirstOrDefault(x => x.Id == grant.PeerId);
            var expired = grant.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var profile = grant.Permissions == StoragePermission.ReadOnly ? "Read-only" :
                grant.Permissions == StoragePermission.List ? "Folder listing only" :
                grant.Permissions == StoragePermission.None ? "No allowed actions" : "Custom permissions";
            var description = $"{peer?.Name ?? "Unpaired device"} · {(expired ? "Expired" : grant.Enabled ? "Enabled" : "Paused")} · {profile}";
            Page.Children.Add(Card(grant.Label, description, Selectable(grant.DisplayPath),
                grant.ExpiresAt is { } at ? Body("Expires " + DateTimeOffset.FromUnixTimeMilliseconds(at).ToLocalTime().ToString("g")) : Body("No scheduled expiry"),
                Row(Action("Edit permissions", () => EditGrantAsync(grant, null)), Action("Stop sharing", async () =>
                {
                    if (await ShowAsync(Dialog("Stop sharing " + grant.Label + "?", Body("The approved device will lose access to this location."), "Stop sharing")) == ContentDialogResult.Primary)
                        await Model.RemoveGrantAsync(grant.Id);
                }))));
        }
    }
    private void RenderFiles()
    {
        Page.Children.Add(Heading("Phone files"));
        var peer = Model.Peers.FirstOrDefault(x => x.Id == Model.ActivePeerId);
        Page.Children.Add(Body(Model.PeerAuthenticated ? $"Browsing {peer?.Name ?? "connected phone"}. Only approved locations are available."
            : "Connect to your phone with iConnect open, then browse its approved folders."));
        if (!Model.PeerAuthenticated) { Page.Children.Add(Go("Choose a device", "devices")); return; }
        var crumbs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        crumbs.Children.Add(Action("Locations", () => Model.BrowseAsync(), !Model.Browsing));
        foreach (var entry in Model.Breadcrumbs) crumbs.Children.Add(Action(entry.Name, () => Model.BrowseAsync(entry), !Model.Browsing));
        Page.Children.Add(new ScrollViewer { Content = crumbs, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var folder = Model.Breadcrumbs.LastOrDefault();
        var query = new TextBox { Header = "Search filenames in this folder and descendants", Text = fileQuery, MaxLength = 128, IsEnabled = folder is not null && !Model.Browsing };
        query.TextChanged += (_, _) => fileQuery = query.Text;
        Page.Children.Add(query);
        Page.Children.Add(Row(Action("Search approved scope", () => Model.SearchFilesAsync(fileQuery), folder is not null && !Model.Browsing),
            Action("Clear search / refresh", () => Model.BrowseAsync(folder), !Model.Browsing),
            Action("Cancel discovery", () => { Model.CancelDiscovery(); return Task.CompletedTask; }, Model.Browsing)));
        var sort = new ComboBox { Header = "Sort all loaded results", ItemsSource = Enum.GetNames<ExplorerSort>(), SelectedIndex = (int)Model.FileSort };
        sort.SelectionChanged += (_, _) => { if (sort.SelectedIndex >= 0 && sort.SelectedIndex != (int)Model.FileSort) Model.SetFileSort((ExplorerSort)sort.SelectedIndex, Model.SortDescending); };
        var descending = new CheckBox { Content = "Descending", IsChecked = Model.SortDescending, VerticalAlignment = VerticalAlignment.Bottom };
        descending.Click += (_, _) => Model.SetFileSort(Model.FileSort, descending.IsChecked == true);
        Page.Children.Add(Row(sort, descending, Action(Model.FileLayout == ExplorerLayout.List ? "Grid view" : "List view", () =>
            { Model.SetFileLayout(Model.FileLayout == ExplorerLayout.List ? ExplorerLayout.Grid : ExplorerLayout.List); return Task.CompletedTask; })));
        Page.Children.Add(Body($"{Model.FileCount:N0} loaded items · page {Model.FilePage + 1} / {Model.FilePageCount}" + (Model.SearchQuery.Length == 0 ? "" : $" · Filename search: {Model.SearchQuery}")));
        if (Model.DiscoveryNotice.Length != 0) Page.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Message = Model.DiscoveryNotice });
        if (Model.MutationMessage.Length != 0) Page.Children.Add(Body(Model.MutationMessage));
        if (Model.PendingMove is { } moving) Page.Children.Add(Card("Choose a destination for " + moving.Name,
            "Open an approved destination folder, then confirm Move here. Existing items are never replaced.", Row(Action("Move here…", async () =>
            {
                if (await ShowAsync(Dialog("Move " + moving.Name + " here?", Body("The item leaves its current folder only if the same-volume move succeeds. An existing destination is kept. Review recent changes after an interrupted response."), "Move here")) == ContentDialogResult.Primary) await Model.MoveHereAsync();
            }, folder is not null && !Model.Browsing), Action("Cancel move", () => { Model.CancelMove(); return Task.CompletedTask; }))));
        Page.Children.Add(Action("Review recent changes on this device", Model.RefreshMutationsAsync, !Model.Browsing));
        Page.Children.Add(Row(Action("Review deleted items on this device", () => Model.RefreshDeletedAsync(), !Model.Browsing),
            Action("More deleted items", () => Model.RefreshDeletedAsync(true), Model.MoreRemoteDeleted && !Model.Browsing)));
        foreach (var item in Model.RemoteDeleted)
            Page.Children.Add(Card(item.Name, RecoveryState(item.State) + " · " + item.Message, Action("Restore…", async () =>
            {
                var name = new TextBox { Header = "Restore name", Text = item.Name, MaxLength = 255 };
                if (await ShowAsync(Dialog("Restore " + item.Name + "?", Column(Body("Restore to the original parent on the connected device. An existing destination is kept."), name), "Restore")) == ContentDialogResult.Primary)
                    await Model.RestoreDeletedAsync(item.Id, name.Text);
            }, item.CanRestore && !Model.Browsing)));
        if (Model.RecentMutations.Count != 0)
        {
            var receipts = new StackPanel { Spacing = 8 };
            foreach (var change in Model.RecentMutations)
            {
                var operation = change.Operation switch { FileOperation.CreateDirectory => "Create folder", FileOperation.Rename => "Rename", FileOperation.Delete => "Delete to recovery", FileOperation.Restore => "Restore", _ => "Move" };
                var outcome = change.Phase switch { MutationPhase.ChangeSucceeded => "Completed", MutationPhase.ChangeFailed => "Kept existing item", _ => "Needs review" };
                receipts.Children.Add(Body($"{operation} · {change.RequestedName} · {outcome}\n{change.Message}"));
                if (change.Operation == FileOperation.CreateDirectory && change.Phase == MutationPhase.ChangeSucceeded)
                    receipts.Children.Add(Action("Use this created folder for sending", () => Model.UseCreatedFolderForSending(change.Id), !Model.Browsing && Model.PeerAuthenticated));
            }
            Page.Children.Add(new Expander { Header = $"Recent changes ({Model.RecentMutations.Count})", Content = receipts, HorizontalAlignment = HorizontalAlignment.Stretch });
        }
        Page.Children.Add(Row(Action("Refresh", () => Model.BrowseAsync(folder), !Model.Browsing), Action("New folder", async () =>
        {
            var name = new TextBox { Header = "Folder name", MaxLength = 255 };
            if (await ShowAsync(Dialog("Create phone folder", name, "Create folder")) == ContentDialogResult.Primary) await Model.CreateFolderAsync(name.Text);
        }, !Model.Browsing && folder is not null && (folder.Permissions & (uint)StoragePermission.Create) != 0)));
        if (Model.Browsing) Page.Children.Add(new ProgressBar { IsIndeterminate = true });
        if (Model.FileError.Length != 0) Page.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Message = Model.FileError });
        if (Model.Entries.Count == 0 && !Model.Browsing) Page.Children.Add(Card("No items to show", !Model.DiscoveryComplete ? "Discovery was incomplete. Review the notice above and retry."
            : Model.SearchQuery.Length != 0 ? "No loaded filenames match this search." : folder is null ? "Approve a phone folder in the Android app, then refresh Locations." : "This folder has no accessible items."));
        if (Model.Entries.Count > 0 || Model.SelectedFiles.Count > 0)
        {
            Page.Children.Add(Body("Select across result pages to copy together. Selection is retained while sorting or searching this scope; navigation/refresh clears it."));
            var selectedCount = Body($"{Model.SelectedFiles.Count} selected, including other pages");
            Page.Children.Add(Row(selectedCount, Action("Clear selection", () => { Model.ClearFileSelection(); Render(); return Task.CompletedTask; })));
            ListViewBase list = Model.FileLayout == ExplorerLayout.Grid
                ? new GridView { SelectionMode = ListViewSelectionMode.Multiple, IsItemClickEnabled = false, MaxHeight = 420 }
                : new ListView { SelectionMode = ListViewSelectionMode.Multiple, IsItemClickEnabled = false, MaxHeight = 420 };
            AutomationProperties.SetName(list, "Approved phone files");
            foreach (var result in Model.FileRows)
            {
                var entry = result.Entry;
                var name = new TextBlock { Text = entry.Name, FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis };
                var detail = Body(entry.IsDirectory ? "Folder" : $"{entry.Size:N0} bytes · {DateTimeOffset.FromUnixTimeMilliseconds(entry.ModifiedAt).ToLocalTime():g}", 12);
                var icon = new SymbolIcon(entry.IsDirectory ? Symbol.Folder : Symbol.Document) { Margin = new Thickness(0, 6, 8, 0) };
                var path = Body(Model.SearchQuery.Length == 0 ? result.Type : result.RelativePath, 12);
                var row = Model.FileLayout == ExplorerLayout.Grid ? Column(icon, name, detail, path) : Row(icon, Column(name, detail, path));
                row.Tag = entry;
                ContentControl item = Model.FileLayout == ExplorerLayout.Grid ? new GridViewItem { Width = 220, MinHeight = 155 } : new ListViewItem { MinHeight = 90 };
                item.Content = row; item.Tag = entry; item.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                AutomationProperties.SetName(item, (entry.IsDirectory ? "Folder " : "File ") + entry.Name); list.Items.Add(item);
            }
            async Task OpenSelected()
            {
                if (Model.SelectedFiles.Count != 1) throw new InvalidOperationException("Select one item to open.");
                var entry = Model.SelectedFiles[0].Entry;
                await RunAsync(() => entry.IsDirectory ? Model.BrowseAsync(entry) : Model.PreviewAsync(entry));
            }
            foreach (var item in list.Items.OfType<FrameworkElement>().Where(x => Model.SelectedFiles.Any(row => row.Entry.Id == ((FileEntry)x.Tag).Id))) list.SelectedItems.Add(item);
            list.SelectionChanged += async (_, args) => await RunAsync(() =>
            {
                foreach (var item in args.RemovedItems.OfType<FrameworkElement>()) Model.SelectFile(((FileEntry)item.Tag).Id, false);
                try { foreach (var item in args.AddedItems.OfType<FrameworkElement>()) Model.SelectFile(((FileEntry)item.Tag).Id, true); }
                catch
                {
                    foreach (var item in args.AddedItems.OfType<FrameworkElement>().Where(x => !Model.SelectedFiles.Any(row => row.Entry.Id == ((FileEntry)x.Tag).Id))) list.SelectedItems.Remove(item);
                    throw;
                }
                finally { selectedCount.Text = $"{Model.SelectedFiles.Count} selected, including other pages"; }
                return Task.CompletedTask;
            });
            Page.Children.Add(list);
            Page.Children.Add(Action("Open selected item", OpenSelected, !Model.Browsing));
            Page.Children.Add(Row(Action("Rename selected…", async () =>
            {
                var entry = Model.SelectedFiles.Count == 1 ? Model.SelectedFiles[0].Entry : null;
                if (entry is null || entry.IsRoot || (entry.Permissions & (uint)StoragePermission.Rename) == 0) throw new InvalidOperationException("Select one item with rename permission.");
                var name = new TextBox { Header = "New name", Text = entry.Name, MaxLength = 255 };
                if (await ShowAsync(Dialog("Rename " + entry.Name + "?", Column(Body("An existing destination is kept. This changes the name on the connected device."), name), "Rename")) == ContentDialogResult.Primary) await Model.RenameSelectedAsync(name.Text);
            }, !Model.Browsing), Action("Move selected…", () => { Model.BeginMove(); return Task.CompletedTask; }, !Model.Browsing)));
            Page.Children.Add(Action("Delete selected to recovery…", async () =>
            {
                var entry = Model.SelectedFiles.Count == 1 ? Model.SelectedFiles[0].Entry : null;
                if (entry is null || entry.IsRoot || (entry.Permissions & (uint)StoragePermission.Delete) == 0) throw new InvalidOperationException("Select one item with delete permission.");
                if (await ShowAsync(Dialog("Delete " + entry.Name + " to recovery?", Body("The item leaves its current folder and is kept in iConnect recovery on the same volume. Review deleted items to restore it. No permanent deletion is performed."), "Delete to recovery")) == ContentDialogResult.Primary)
                    await Model.DeleteSelectedAsync(entry);
            }, !Model.Browsing));
            Page.Children.Add(Action("Selected item details", async () =>
            {
                if (Model.SelectedFiles.Count != 1) throw new InvalidOperationException("Select one item for details.");
                var metadata = await Model.FileDetailsAsync(Model.SelectedFiles[0].Entry);
                var permissions = new[] { (StoragePermission.List, "list folders"), (StoragePermission.Read, "read files"), (StoragePermission.Create, "create folders"),
                    (StoragePermission.Write, "write"), (StoragePermission.Rename, "rename"), (StoragePermission.Move, "move"), (StoragePermission.Delete, "delete"), (StoragePermission.Replace, "replace") };
                var details = Dialog("Item details", Column(Body(metadata.Name), Body(metadata.IsDirectory ? "Folder" : $"File · {metadata.Size:N0} bytes"),
                    Body($"Modified: {DateTimeOffset.FromUnixTimeMilliseconds(metadata.ModifiedAt):u}"),
                    Body("Location permissions: " + string.Join(", ", permissions.Where(x => (metadata.Permissions & (uint)x.Item1) != 0).Select(x => x.Item2)))), "Done");
                details.CloseButtonText = ""; details.DefaultButton = ContentDialogButton.Primary; await ShowAsync(details);
            }, !Model.Browsing));
            Page.Children.Add(Action("Copy selected files and folders…", async () =>
            {
                var selected = Model.SelectedFiles.Select(x => x.Entry).ToArray();
                if (selected.Length == 0) throw new InvalidOperationException("Select at least one file or folder.");
                var plan = await Model.PlanBatchAsync(selected);
                var picker = new Windows.Storage.Pickers.FolderPicker(); picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                var destination = await picker.PickSingleFolderAsync(); if (destination is null) return;
                var collision = new ComboBox { Header = "If a destination name exists", ItemsSource = new[] { "Keep both with a numbered name", "Skip the item or folder", "Fail the item or folder" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
                var manifest = plan.Manifest;
                var summary = Column(Body($"{manifest.Files:N0} files · {manifest.Directories:N0} folders · {manifest.Bytes:N0} bytes"),
                    Body("Empty folders are included. Files are verified before completion. Existing folders are kept separate; they are never merged or replaced. Cancel keeps completed files and created folders."), collision);
                var failures = manifest.Items.Count(x => x.Error.Length != 0);
                if (failures != 0) summary.Children.Add(Body($"{failures:N0} items cannot be copied and will appear as failed results."));
                if (await ShowAsync(Dialog("Copy selected items to this computer?", summary, "Start copy")) != ContentDialogResult.Primary) return;
                var policy = collision.SelectedIndex switch { 1 => DownloadCollision.Skip, 2 => DownloadCollision.Fail, _ => DownloadCollision.KeepBoth };
                Model.StartBatch(plan, destination.Path, policy); Model.ClearFileSelection(); Navigate("transfers");
            }, !Model.Browsing));
            Page.Children.Add(Action("Save selected file to this computer", async () =>
            {
                var entry = Model.SelectedFiles.Count == 1 ? Model.SelectedFiles[0].Entry : null;
                if (entry is null || entry.IsDirectory) throw new InvalidOperationException("Select a file first.");
                var sourcePeer = Model.ActivePeerId;
                var picker = new Windows.Storage.Pickers.FolderPicker(); picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                var destination = await picker.PickSingleFolderAsync();
                if (destination is null) return;
                var keepBoth = Dialog("Save " + entry.Name + "?", Body("The file will be verified before it receives its final name. An existing file is kept; a numbered name is used if needed."), "Save file");
                if (await ShowAsync(keepBoth) == ContentDialogResult.Primary)
                {
                    if (Model.ActivePeerId != sourcePeer) throw new InvalidOperationException("The connected device changed. Select the file again.");
                    Model.DownloadFile(entry, destination.Path, DownloadCollision.KeepBoth); Navigate("transfers");
                }
            }));
        }
        Page.Children.Add(Row(Action("Previous page", () => { Model.SetFilePage(Model.FilePage - 1); return Task.CompletedTask; }, Model.FilePage > 0 && !Model.Browsing),
            Body($"Page {Model.FilePage + 1} / {Model.FilePageCount}"), Action("Next page", () => { Model.SetFilePage(Model.FilePage + 1); return Task.CompletedTask; }, Model.FilePage + 1 < Model.FilePageCount && !Model.Browsing)));
        AddPreview(Model.FilePreview);
    }
    private async Task ChooseOutgoingFileAsync(bool move)
    {
        var peers = Model.Peers.ToArray();
        if (peers.Length == 0) throw new InvalidOperationException("Pair a device before preparing a send.");
        var peerChoice = new ComboBox { Header = "Receiving device", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var peer in peers) peerChoice.Items.Add(peer.Name + " · " + peer.Id[..8]);
        peerChoice.SelectedIndex = Math.Max(0, Array.FindIndex(peers, x => x.Id == Model.ActivePeerId));
        var detail = move
            ? "Send a private copy to a compatible receiver. After it completes, a separate review and fresh verification can move the original into local recovery."
            : "This creates an app-private copy. Connect and choose a receiving location afterward.";
        if (await ShowAsync(Dialog(move ? "Prepare a file for a move?" : "Prepare a local file for sending?",
            Column(Body(detail), peerChoice), "Choose file")) != ContentDialogResult.Primary) return;
        var selectedPeer = peers[peerChoice.SelectedIndex].Id;
        var picker = new Windows.Storage.Pickers.FileOpenPicker(); picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var selected = await picker.PickSingleFileAsync(); if (selected is null) return;
        if (move) await Model.PrepareOutgoingMove(selectedPeer, selected.Path);
        else await Model.PrepareOutgoingFile(selectedPeer, selected.Path);
    }

    private async Task ReviewOutgoingMoveAsync(OutgoingCopy copy)
    {
        var review = await Model.ReviewOutgoingMoveAsync(copy.Id);
        var detail = Column(Body("Original file"), Selectable(review.Source), Body("Receiving device"), Selectable(Model.BatchPeerName(copy.Authority)),
            Body("Receiving folder and published name"), Selectable(review.Destination), Selectable(review.FinalName),
            Body($"iConnect will verify both copies of this {review.Size:N0}-byte file and move the original into local recovery."));
        if (await ShowAsync(Dialog("Finish this verified move?", detail, "Move original into recovery")) == ContentDialogResult.Primary)
            await ShowLocalResult(await Model.FinishOutgoingMoveAsync(review));
    }

    private void RenderTransfers()
    {
        Page.Children.Add(Heading("Transfers"));
        Page.Children.Add(Body("Downloads are verified before completion. Paused and interrupted files retain a local checkpoint; reconnect to the same trusted device to resume."));
        if (Model.SourcePreparationBusy) Page.Children.Add(Row(Body("Preparing or checking the selected fresh source. Receiving progress is retained; no destination bytes are being copied by this review."),
            Action("Cancel source review", () => { Model.CancelSourcePreparation(); return Task.CompletedTask; })));
        if (Model.IncomingWindowPreparationBusy) Page.Children.Add(Row(Body("Checking the retained incoming checkpoint. Confirmation only changes its write window; the sender must resume separately."),
            Action("Cancel incoming-window check", () => { Model.CancelIncomingWindowPreparation(); return Task.CompletedTask; })));
        RenderServingSources();
        RenderReceivingPartRecovery();
        Page.Children.Add(Heading("Send a local file"));
        Page.Children.Add(Body("Prepare a private snapshot for a paired device, then select its approved receiving location. The original stays on this computer. Up to eight private snapshots share a 16 GiB reservation. Queued sends require deliberate resume."));
        var canPrepare = Model.OwnerPolicyHealthy && Model.Peers.Count != 0;
        Page.Children.Add(Row(Action("Choose local file to prepare…", () => ChooseOutgoingFileAsync(move: false), canPrepare),
            Action("Prepare a file for a move…", () => ChooseOutgoingFileAsync(move: true), canPrepare)));
        Page.Children.Add(Action("Load approved receiving locations", Model.LoadReceivingLocations, Model.PeerAuthenticated));
        foreach (var location in Model.ReceivingLocations) Page.Children.Add(Action("Use receiving location: " + location.Name, () => { Model.SelectReceivingLocation(location.Id); return Task.CompletedTask; }, Model.PeerAuthenticated));
        Page.Children.Add(Body(Model.ReceivingLocation is { } target ? "Selected receiving location: " + target.Name : Model.Breadcrumbs.LastOrDefault() is { } openFolder ? "Open remote folder: " + openFolder.Name : "No receiving location selected."));
        if (Model.ReceivingLocation is not null) Page.Children.Add(Action("Use the open browser folder instead", () => { Model.SelectReceivingLocation(null); return Task.CompletedTask; }));
        if (Model.OrphanOutgoingCopies != 0) Page.Children.Add(Action("Remove orphaned private snapshots…", async () =>
        { if (await ShowAsync(Dialog("Remove orphaned private snapshots?", Body("Only private outgoing snapshots without a local record are removed. Original files and any receiver copies are kept. These private snapshots cannot be resumed afterward."), "Remove private snapshots")) == ContentDialogResult.Primary) await Model.RemoveOrphanOutgoing(); }));
        foreach (var copy in Model.OutgoingCopies)
        {
            var active = Model.OutgoingActive(copy.Id);
            var title = copy.MoveSource is null ? copy.Name : "Move copy · " + copy.Name;
            OutgoingCopy CurrentCopy() => Model.OutgoingSnapshot(copy.Id) ?? copy;
            Page.Children.Add(RefreshableCard($"send/{copy.Id}", title, () => $"{copy.Phase} · {CurrentCopy().Offset:N0} / {copy.Size:N0} bytes", Body(copy.Message), Body(copy.Destination.Length == 0 ? "Destination not selected yet." : "Receiving location: " + copy.Destination),
                ProgressBody(() => Model.OutgoingPayloadText(CurrentCopy())),
                Body(copy.FinalName.Length == 0 ? "Receiver has not confirmed a final name." : "Receiver name: " + copy.FinalName),
                Action(copy.Phase == SendingPhase.Completed ? "Cancel receiver check" : "Pause this send", () => { Model.PauseOutgoing(copy.Id); return Task.CompletedTask; }, active),
                Action(copy.RemoteId.Length == 0 ? "Send to selected destination" : "Resume / check receiver receipt", () => Model.SendOutgoing(copy.Id),
                    !active && copy.Hash.Length != 0 && copy.Phase != SendingPhase.Completed && Model.PeerAuthenticated && Model.ActivePeerId == copy.Authority.Peer &&
                    (copy.RemoteId.Length != 0 || ((Model.ReceivingLocation ?? Model.Breadcrumbs.LastOrDefault())?.Permissions & 12) == 12)),
                Action("Verify published copy…", async () =>
                {
                    await Model.VerifyPublishedOutgoingAsync(copy.Id);
                    await ShowAsync(Dialog("Published copy verified", Body("The receiving device reread the recorded file and confirmed its bytes during this check."), "Done"));
                }, !active && copy.Phase == SendingPhase.Completed && Model.PublishedUploadCheckSupported && Model.ActivePeerId == copy.Authority.Peer),
                copy.MoveSource is null || copy.SourceRecovered ? Body(copy.SourceRecovered ? "Review the original in local saved items." : "") :
                    Action("Review original for recovery…", () => ReviewOutgoingMoveAsync(copy),
                        !active && copy.Phase == SendingPhase.Completed && Model.PublishedUploadCheckSupported &&
                        Model.ActivePeerId == copy.Authority.Peer && !Model.LocalFiles.ManagementBusy),
                Action("Change receiving filename…", async () =>
                { var name = new TextBox { Header = "New copy name", Text = copy.Name, MaxLength = 255 }; if (await ShowAsync(Dialog("Receiving filename", name, "Use name")) == ContentDialogResult.Primary) await Model.RenameOutgoing(copy.Id, name.Text); }, !active && copy.Hash.Length != 0 && copy.RemoteId.Length == 0),
                Action("Prepare another receiver copy…", async () =>
                { if (await ShowAsync(Dialog("Prepare another receiver copy?", Body("An earlier receiver copy may already exist or still need review. This deliberately starts a new request from the retained private snapshot. Existing files are kept; select the destination again."), "Prepare another copy")) == ContentDialogResult.Primary) await Model.AnotherOutgoing(copy.Id); }, !active && copy.Hash.Length != 0 && copy.RemoteId.Length != 0 && copy.SourceRecoveryId.Length == 0),
                Action("Remove private snapshot…", async () =>
                { if (await ShowAsync(Dialog("Remove private send copy?", Body("This stops local sending and removes only this private snapshot and queue record. The original source and all receiver copies are kept. An interrupted receiver copy may still need review on that device."), "Remove private snapshot")) == ContentDialogResult.Primary) await Model.RemoveOutgoing(copy.Id); }, !active)));
        }
        RenderOutgoingBatches();
        RenderBatchSenderRecovery();
        RenderIncomingBatchHistory();
        if (Model.IncomingBatchPlans.Count != 0 || Model.TransferRecovery.Any(x => x.Area == "Batch plans"))
        {
            Page.Children.Add(Heading("Incoming batch plans"));
            var reserved = Model.IncomingBatchPlans.Where(x => !IConnect.Core.UploadBatchRules.IsTerminal(x.Phase)).Sum(x => x.Bytes);
            Page.Children.Add(Body(Model.TransferRecovery.Any(x => x.Area == "Batch plans") ? "Saved batch-plan metadata needs local review. Incoming reservations cannot be verified." :
                $"Retained plans: {Model.IncomingBatchPlans.Count}. Incoming-copy quota reserved: {reserved:N0} bytes. " +
                (Model.IncomingBatchPlans.Any(x => x.EffectsStarted) ? "Destination items may already exist. Existing items are kept when history is archived." : "Destination items have not been created by these plans.")));
            Page.Children.Add(Action("Archive / reset batch-plan history…", async () =>
            {
                if (await ShowAsync(Dialog("Archive batch-plan history?", Body("Connections will pause. Batch metadata is preserved in this computer's private profile. Destination files, folders and unfinished transfer parts are kept; reservations are released and earlier requests lose receipt lookup. Keep the archived receipts to identify unfinished parts safely. Review existing copies before deliberately starting another batch."), "Archive and pause")) == ContentDialogResult.Primary)
                    await Model.ArchiveIncomingBatchPlans();
            }, Model.OwnerPolicyHealthy));
        }
        Page.Children.Add(Action("Archive / reset incoming-copy history…", async () =>
        {
            if (await ShowAsync(Dialog("Archive incoming-copy history?", Body("Connections will pause. Current incoming metadata is preserved in this computer's private profile, and a fresh receipt context is created. Partial and published files are kept. Earlier sends lose automatic receipt lookup; inspect their destinations before deliberately starting another copy. This also resets incoming receipt quotas."), "Archive and pause")) == ContentDialogResult.Primary)
                await Model.ArchiveIncomingHistory();
        }));
        var incoming = Model.IncomingUploads;
        if (incoming.Count != 0)
        {
            var pages = Math.Max(1, (incoming.Count + 31) / 32); incomingUploadPage = Math.Clamp(incomingUploadPage, 0, pages - 1);
            Page.Children.Add(Heading("Incoming copies from paired devices"));
            Page.Children.Add(Body("Each copy requires current Write and Create approval. Existing files are kept. Files are marked untrusted before publication; completion also requires Windows attachment checks and another byte verification. A published file can still need security review. Limits: 32 receipts per peer, 128 overall, and 16 GiB per peer / 64 GiB overall of retained daily receipts and pending reservations."));
            Page.Children.Add(Row(Action("Previous incoming copies", () => { incomingUploadPage--; Render(); return Task.CompletedTask; }, incomingUploadPage > 0),
                Body($"Page {incomingUploadPage + 1} / {pages}"), Action("Next incoming copies", () => { incomingUploadPage++; Render(); return Task.CompletedTask; }, incomingUploadPage + 1 < pages)));
            foreach (var item in incoming.Skip(incomingUploadPage * 32).Take(32))
            {
                IncomingUpload Snapshot() => Model.IncomingSnapshot(item.Id) ?? item;
                var payload = ProgressBody(() => Model.IncomingPayloadText(Snapshot()));
                Page.Children.Add(RefreshableCard($"receive/{item.Id}", item.Name, () =>
                {
                    var current = Snapshot();
                    return $"{current.Phase} · {current.Offset:N0} / {current.Size:N0} bytes";
                }, Body(item.Message), payload,
                    Selectable(item.Location), Body(item.FinalName.Length == 0 ? "Not published under a final name." : "Destination name: " + item.FinalName),
                    Body("Write window ends: " + DateTimeOffset.FromUnixTimeMilliseconds(item.OperationExpiresAt).ToLocalTime().ToString("g") + ". After expiry, the receiving owner can review this same receipt. Device approvals and their expiry still apply."),
                    Action("Review this write window…", () => ReviewIncomingOperationAsync(item), Model.OwnerPolicyHealthy && !Model.IncomingWindowPreparationBusy &&
                        (item.Phase == UploadPhase.UploadReceiving || item.FinalName.Length != 0 && item.Phase is UploadPhase.UploadFinalizing or UploadPhase.UploadReview)),
                    Body(item.SecurityPolicy == 1 ? "Windows attachment checks finished. This file remains untrusted; a successful check is not a malware-free guarantee." : item.Phase == UploadPhase.UploadCompleted ? "Earlier receipt: Windows attachment checks were not recorded. Treat the file as untrusted." : "Windows attachment checks are not complete."),
                    Action("Check published copy security…", async () =>
                    {
                        if (await ShowAsync(Dialog("Check this recorded copy?", Column(Body("This rechecks only the recorded local file and keeps device approvals unchanged. Windows security providers can alter or remove the file. A passing check does not guarantee safety."), Selectable(item.Location), Body(item.FinalName)), "Check copy")) == ContentDialogResult.Primary)
                            await Model.CheckIncomingSecurity(item.Id);
                    }, Model.OwnerPolicyHealthy && item.FinalName.Length != 0 && item.Offset == item.Size && item.Phase is not (UploadPhase.UploadVerifying or UploadPhase.UploadFinalizing)),
                    Action("Stop and discard this incoming partial…", async () =>
                    {
                        if (await ShowAsync(Dialog("Discard incoming partial?", Body("This stops incoming writes for this receipt and removes only its identified private partial in the displayed destination. Original source files, published files and other destination files are kept. The paired device must start a new copy if needed."), "Discard partial")) == ContentDialogResult.Primary)
                            await Model.DiscardIncomingPartial(item.Id);
                    }, !UploadRules.IsTerminal(item.Phase) && item.FinalName.Length == 0)));
            }
        }
        var recovery = Model.TransferRecovery;
        if (Model.PreservedProgressRecords != 0) Page.Children.Add(Card("Preserved progress backups", $"{Model.PreservedProgressRecords} local recovery folders. Private partial files and saved copies are stored separately.",
            Action("Remove preserved metadata backups…", async () =>
            {
                if (await ShowAsync(Dialog("Permanently remove preserved metadata?", Body("This removes only archived progress metadata and cannot be undone. Private partial files, copied files, folders and live queue records are kept. You will lose these archived details for future review."), "Remove metadata backups")) == ContentDialogResult.Primary)
                    await Model.ClearPreservedProgress();
            })));
        if (recovery.Count != 0)
        {
            Page.Children.Add(Heading("Review saved progress"));
            Page.Children.Add(Body("Some progress records could not be validated. Affected queues are paused. Archiving preserves damaged metadata locally; it keeps partial files, copied files and folders. Unreadable progress cannot be resumed automatically."));
            recoveryPage = Math.Clamp(recoveryPage, 0, (recovery.Count - 1) / 20);
            Page.Children.Add(Row(Action("Previous recovery items", () => { --recoveryPage; Render(); return Task.CompletedTask; }, recoveryPage > 0),
                Body($"Recovery page {recoveryPage + 1} / {(recovery.Count + 19) / 20}"), Action("Next recovery items", () => { ++recoveryPage; Render(); return Task.CompletedTask; }, (recoveryPage + 1) * 20 < recovery.Count)));
            foreach (var entry in recovery.Skip(recoveryPage * 20).Take(20))
                Page.Children.Add(Card(entry.Area + " progress needs review", entry.Issue.Message, Action("Archive progress record…", async () =>
                {
                    if (await ShowAsync(Dialog("Archive unreadable progress?", Body("The damaged metadata is preserved in local recovery storage. Partial and saved files are kept. This record will no longer occupy its queue; you may need to start a new copy. Sending-ticket recovery requires the receiving device to start a new transfer."), "Archive progress")) == ContentDialogResult.Primary)
                        await Model.ArchiveTransferRecovery(entry.Area, entry.Issue.Id);
                }, entry.Issue.CanArchive)));
        }
        foreach (var child in Model.OrphanedBatchFiles)
            Page.Children.Add(Card("File belongs to a missing batch", child.Name + " · " + child.Phase,
                Body("Its original batch is unavailable. It cannot become a standalone or background transfer. Cancel cleans only its verified private partial; completed files are kept."),
                Row(Action("Cancel / clean private partial", () => Model.CancelDownload(child.Id), child.Phase != DownloadPhase.Completed),
                    Action("Remove file record", () => { Model.RemoveDownloadRecord(child.Id); return Task.CompletedTask; }, child.Phase is DownloadPhase.Completed or DownloadPhase.Canceled or DownloadPhase.Skipped))));
        if (Model.Downloads.Count == 0 && Model.Batches.Count == 0 && Model.OrphanedBatchFiles.Count == 0 && recovery.Count == 0) Page.Children.Add(Card("No downloads", "Select files or folders in Phone files and copy them to a chosen folder.", Go("Browse phone files", "files")));
        foreach (var batch in Model.Batches)
        {
            var results = new StackPanel { Spacing = 8 };
            var activeFile = Model.BatchFiles(batch.Id).FirstOrDefault();
            if (activeFile is not null)
            {
                DownloadJob Snapshot() => Model.DownloadSnapshot(activeFile.Id) ?? activeFile;
                results.Children.Add(ProgressBody(() =>
                {
                    var current = Snapshot();
                    return $"Current file: {current.Name} · {current.Phase} · {current.Offset:N0} / {current.Size:N0} bytes";
                }));
                results.Children.Add(ProgressBody(() => Model.DownloadFinalizationText(Snapshot()), 12));
                results.Children.Add(ProgressBody(() =>
                {
                    var current = Snapshot();
                    return current.Offset < current.Size ? Model.DownloadObservedEstimate(current).Text : null;
                }, 12));
                results.Children.Add(ProgressBody(() =>
                {
                    var current = Snapshot(); var lifetime = Model.DownloadEstimate(current);
                    return lifetime.OutlastsApproval ? null : Model.DownloadObservedEstimate(current).ApprovalWarning(lifetime.SourceSeconds);
                }, 12));
            }
            foreach (var file in Model.BatchFiles(batch.Id).Where(x => x.Phase is DownloadPhase.Paused or DownloadPhase.Interrupted && x.FinalName.Length == 0 && !(x.Offset == x.Size && x.Hash.Length == 64)))
            {
                var index = Array.FindIndex(batch.Items, x => x.DownloadId == file.Id); if (index < 0) continue;
                results.Children.Add(ProgressBody(() => Model.DownloadEstimate(file).PayloadText, 12));
                results.Children.Add(ProgressBody(() => Model.DownloadEstimate(file).ApprovalText, 12));
                results.Children.Add(ProgressBody(() => Model.DownloadEstimate(file).Warning, 12));
                results.Children.Add(Action("Review selected fresh source for " + file.Name + "…", () => ReviewBatchDownloadSourceAsync(batch, index, file),
                    batch.Phase is BatchPhase.Paused or BatchPhase.Interrupted && Model.PeerAuthenticated && Model.ActivePeerId == batch.PeerId && !Model.SourcePreparationBusy,
                    id: $"batch-fresh-source/{file.Id}"));
            }
            var pageCount = Math.Max(1, (batch.Items.Length + 49) / 50);
            var page = Math.Clamp(batchResultPages.GetValueOrDefault(batch.Id), 0, pageCount - 1);
            results.Children.Add(Row(Action("Previous results", () => { batchResultPages[batch.Id] = page - 1; Render(); return Task.CompletedTask; }, page > 0),
                Body($"Page {page + 1} / {pageCount}"), Action("Next results", () => { batchResultPages[batch.Id] = page + 1; Render(); return Task.CompletedTask; }, page + 1 < pageCount)));
            foreach (var item in batch.Items.Skip(page * 50).Take(50)) results.Children.Add(Body($"{string.Join(" / ", item.Source.SourcePath.DefaultIfEmpty(item.Source.Name))} · {item.Phase}" +
                (item.LocalName.Length != 0 ? $" · {(item.Phase == BatchItemPhase.Completed ? "Saved" : "Destination")} name: {item.LocalName}" : "") + (item.Error.Length != 0 ? $" · {item.Error}" : ""), 12));
            var phase = batch.Phase == BatchPhase.CompletedWithIssues ? "Completed with issues" : batch.Phase.ToString();
            Page.Children.Add(RefreshableCard($"batch-download/{batch.Id}", "Folder / file batch", $"{phase} · {batch.Completed:N0} / {batch.Items.Length:N0} items · {batch.Issues:N0} issues · {batch.VerifiedBytes:N0} verified bytes",
                Body(batch.DisplayPath), Body(batch.Error),
                Row(Action("Pause batch", () => { Model.PauseBatch(batch.Id); return Task.CompletedTask; }, batch.Phase == BatchPhase.Running),
                    Action("Resume batch", () => { _ = RunAsync(() => Model.ResumeBatch(batch.Id)); return Task.CompletedTask; }, batch.Phase is BatchPhase.Paused or BatchPhase.Interrupted)),
                Action("Cancel / clean up partial files", () => Model.CancelBatch(batch.Id), batch.Phase != BatchPhase.Completed || !Model.CanRemoveBatch(batch.Id)),
                    Action("Remove batch record", async () =>
                    {
                        if (await ShowAsync(Dialog("Remove this batch record?", Body("Saved files and created folders are kept. Only this local record is removed."), "Remove record")) == ContentDialogResult.Primary) Model.RemoveBatch(batch.Id);
                    }, Model.CanRemoveBatch(batch.Id)),
                RefreshableExpander($"batch-download/{batch.Id}", "Item results", new ScrollViewer { Content = results, MaxHeight = 340 })));
        }
        foreach (var job in Model.Downloads)
        {
            DownloadJob Snapshot() => Model.DownloadSnapshot(job.Id) ?? job;
            var active = Model.DownloadFinalizationText(job) is not null || job.Phase is DownloadPhase.Preparing or DownloadPhase.Downloading or DownloadPhase.Verifying or DownloadPhase.Finalizing;
            Page.Children.Add(RefreshableCard($"download/{job.Id}", job.Name, () =>
                {
                    var current = Snapshot();
                    return $"{current.Phase} · {current.Offset:N0} / {current.Size:N0} bytes";
                }, DownloadProgressBar(Snapshot), Body(job.DisplayPath),
                ProgressBody(() => Model.DownloadFinalizationText(Snapshot())),
                ProgressBody(() =>
                {
                    var current = Snapshot();
                    return current.Offset < current.Size ? Model.DownloadObservedEstimate(current).Text : null;
                }),
                ProgressBody(() => Model.DownloadEstimate(Snapshot()).PayloadText),
                ProgressBody(() =>
                {
                    var current = Snapshot();
                    return current.Offset < current.Size || current.Hash.Length != 64 ? Model.DownloadEstimate(current).ApprovalText
                        : "Recorded payload can be checked locally without fetching more source bytes.";
                }),
                ProgressBody(() =>
                {
                    var current = Snapshot(); var estimate = Model.DownloadEstimate(current);
                    return estimate.Warning.Length != 0 ? estimate.Warning : Model.DownloadObservedEstimate(current).ApprovalWarning(estimate.SourceSeconds);
                }),
                job.Error.Length == 0 ? Body(job.FinalName.Length == 0 ? "" : "Saved name: " + job.FinalName) : Body(job.Error),
                Body(job.SecurityPolicy == 1 ? "Windows attachment checks finished. The file remains untrusted; installed security providers determine scanning behavior." : job.Phase == DownloadPhase.Completed ? "Earlier receipt: Windows attachment checks were not recorded. Treat the file as untrusted." : "Completion also requires Windows attachment checks. A final-name file may still need review."),
                Action("Check published copy security…", async () =>
                {
                    if (await ShowAsync(Dialog("Check this recorded copy?", Column(Body("This rechecks the identified local file without fetching a new copy. Windows security providers can alter or remove the file. A passing check does not guarantee safety."), Selectable(job.DisplayPath), Body(job.FinalName)), "Check copy")) == ContentDialogResult.Primary)
                        await Model.CheckDownloadSecurity(job.Id);
                }, Model.OwnerPolicyHealthy && !active && job.FinalName.Length != 0 && job.Offset == job.Size && job.Hash.Length == 64),
                Action("Review fresh source from selected phone file…", () => ReviewDownloadSourceAsync(job),
                    Model.OwnerPolicyHealthy && Model.PeerAuthenticated && !Model.SourcePreparationBusy && job.PeerId == Model.ActivePeerId && job.Phase is DownloadPhase.Paused or DownloadPhase.Interrupted && (job.Offset != job.Size || job.Hash.Length != 64) && job.FinalName.Length == 0),
                Row(Action("Pause", () => { Model.PauseDownload(job.Id); return Task.CompletedTask; }, active),
                    Action("Resume", () => { _ = RunAsync(() => Model.ResumeDownload(job.Id)); return Task.CompletedTask; }, !active && job.Phase is DownloadPhase.Paused or DownloadPhase.Interrupted),
                    Action(job.Phase == DownloadPhase.Canceled ? "Retry cleanup" : "Cancel", () => Model.CancelDownload(job.Id),
                        job.Phase is not (DownloadPhase.Completed or DownloadPhase.Canceled or DownloadPhase.Skipped) ||
                            job.Phase == DownloadPhase.Canceled && job.Error.Length != 0),
                    Action("Remove record", async () =>
                    {
                        if (await ShowAsync(Dialog("Remove this transfer record?", Body("The saved file is kept. Only this local transfer record is removed."), "Remove record")) == ContentDialogResult.Primary)
                            Model.RemoveDownloadRecord(job.Id);
                    }, job.Phase is DownloadPhase.Completed or DownloadPhase.Skipped || job.Phase == DownloadPhase.Canceled && job.Error.Length == 0))));
        }
    }
    private void RenderHistory()
    {
        Page.Children.Add(Heading("Activity"));
        Page.Children.Add(Body("Saved only on this computer. At most 500 entries are kept; age retention is applied when iConnect opens or saves activity. Pairing codes, invitations and file content are excluded. Activity can be incomplete after storage failures and does not authorize access."));
        Page.Children.Add(Body("Incoming requests use fixed labels. An accepted finalization request does not prove publication; a separate completed-publication event is recorded after the durable result. Repeated events can be grouped. Filenames, paths and raw errors are excluded."));
        if (Model.DroppedActivityEvents > 0) Page.Children.Add(Body($"{Model.DroppedActivityEvents} events were not recorded in this run. History may be incomplete."));
        var retention = new ComboBox { Header = "Local activity retention", HorizontalAlignment = HorizontalAlignment.Stretch };
        var periods = new[] { 0, 1, 7, 30, 90 };
        foreach (var days in periods) retention.Items.Add(days == 0 ? "Off · clear retained activity" : $"{days} days");
        retention.SelectedIndex = Array.IndexOf(periods, Model.ActivityRetentionDays);
        Page.Children.Add(retention);
        Page.Children.Add(Action("Save activity retention…", async () =>
        {
            var days = periods[retention.SelectedIndex];
            if (await ShowAsync(Dialog("Change local activity retention?", Body(days == 0 ? "Remove retained local activity and stop recording new entries. Files, transfer receipts and sharing settings stay intact." : $"Keep at most 500 entries for {days} days. Older activity is removed; files, transfer receipts and sharing settings stay intact."), "Apply retention")) == ContentDialogResult.Primary) await Model.ConfigureActivity(days);
        }, Model.ActivityIssue.Length == 0));
        if (Model.ActivityIssue.Length != 0) Page.Children.Add(Card("Activity history needs review", Model.ActivityIssue,
            Action("Preserve and reset activity…", async () =>
            {
                if (await ShowAsync(Dialog("Preserve unreadable activity?", Body("Preserve this activity journal and its uncommitted sidecars in private recovery storage, then open an empty history with 30-day retention. Trust, sharing, transfers and original files stay intact."), "Preserve and reset")) == ContentDialogResult.Primary) await Model.PreserveAndResetActivity();
            }, Model.CanResetActivity)));
        Page.Children.Add(Action("Clear activity", async () =>
        {
            if (await ShowAsync(Dialog("Clear local activity?", Body("This removes the saved activity entries from this computer."), "Clear activity")) == ContentDialogResult.Primary) await Model.ClearActivity();
        }, Model.Activity.Count != 0 && Model.ActivityIssue.Length == 0));
        Page.Children.Add(Action("Manage preserved activity backups…", ManageActivityBackupsAsync));
        if (Model.Activity.Count == 0) Page.Children.Add(Card("No recorded activity", "Connection, device and permission actions will appear here."));
        historyPage = Math.Clamp(historyPage, 0, Math.Max(0, (Model.Activity.Count - 1) / 50));
        Page.Children.Add(Row(Action("Previous page", () => { --historyPage; Render(); return Task.CompletedTask; }, historyPage > 0),
            Body($"Page {historyPage + 1} of {Math.Max(1, (Model.Activity.Count + 49) / 50)}"),
            Action("Next page", () => { ++historyPage; Render(); return Task.CompletedTask; }, (historyPage + 1) * 50 < Model.Activity.Count)));
        foreach (var entry in Model.Activity.Skip(historyPage * 50).Take(50)) Page.Children.Add(Card(entry.Category, entry.Description,
            Body(DateTimeOffset.FromUnixTimeMilliseconds(entry.At).ToLocalTime().ToString("g") + (entry.PeerId is { Length: 64 } peer ? " · device " + peer[..8] : ""), 12)));
    }
    private void RenderSettings()
    {
        Page.Children.Add(Heading("Settings"));
        Page.Children.Add(Card("Windows identity and approvals", Model.OwnerPolicyHealthy ? "Trust and remote folder approvals are bound to this Windows identity and owner generation." : Model.OwnerPolicyNotice,
            Action("Reset Windows trust and approvals…", async () =>
            {
                if (await ShowAsync(Dialog("Reset Windows trust and storage approvals?",
                    Body("This pauses remote access and archives earlier trust and approval metadata. Pair each device and approve its folders again. Received copies, local folders and private transfer staging are kept. Optional background and sign-in preferences are disabled."),
                    "Archive and reset")) == ContentDialogResult.Primary) await Model.ResetOwnerPolicyAsync();
            })));
        Page.Children.Add(Body("Connection settings belong to this computer. Pause availability before changing them."));
        var editable = !Model.Connected && !Model.Connecting && !Model.Availability.AutomaticAllowed;
        var name = new TextBox { Header = "Device name", Text = Model.Settings.DeviceName, MaxLength = 64, IsEnabled = editable };
        var server = new TextBox { Header = "Coordinator address", Text = Model.Settings.Coordinator, PlaceholderText = "https://your-coordinator.example", IsEnabled = editable };
        var stun = new TextBox { Header = "STUN address (optional)", Text = Model.Settings.Stun, PlaceholderText = "stun:host:3478", IsEnabled = editable };
        var presence = new CheckBox { Content = "Share availability with paired devices", IsChecked = Model.Settings.SharePresence, IsEnabled = editable };
        Page.Children.Add(Card("Connection", "Both devices use the same HTTPS coordinator. A configured STUN service can help establish a direct connection across networks.", name, server, stun, presence,
            Body("Availability is off initially. When enabled, the coordinator receives your paired device IDs and reports only mutual opt-ins. Reports expire and do not authenticate a device or grant file access. Pause before changing this option."),
            Action("Save settings", () => { Model.SaveSettings(new DesktopSettings(name.Text.Trim(), server.Text.Trim(), stun.Text.Trim(), presence.IsChecked == true)); return Task.CompletedTask; }, editable, true, "save-settings")));
        Page.Children.Add(Card("Availability", "While connected, paired devices can request access within their grants. Pausing ends active access; trust and approvals remain saved.",
            Model.Connected || Model.Connecting || !Model.Availability.Preferences.Paused ? Action("Pause availability", Model.PauseAvailabilityAsync) : Action("Connect", Model.ResumeAvailabilityAsync, Model.Settings.Coordinator.Length != 0)));
        var preferences = Model.Availability.Preferences;
        var background = new CheckBox { Content = "Keep iConnect running in the notification area when I close its window", IsChecked = preferences.RunInBackground };
        var signIn = new CheckBox { Content = "Start iConnect in the notification area when I sign in to Windows", IsChecked = preferences.StartAtSignIn };
        signIn.IsEnabled = background.IsChecked == true;
        background.Checked += (_, _) => signIn.IsEnabled = true;
        background.Unchecked += (_, _) => { signIn.IsChecked = false; signIn.IsEnabled = false; };
        var automatic = new CheckBox { Content = "Reconnect automatically to the coordinator and last selected paired device", IsChecked = preferences.AutoReconnect };
        Page.Children.Add(Card("Windows background availability",
            "These options are off initially. Connect starts availability; Pause stops retries and remains saved across restarts. Closing to the notification area keeps approved access and transfers active. Quit ends this run. Automatic reconnect may resume on the next launch unless paused.",
            background, signIn, automatic, Action("Save availability options", async () =>
            {
                var enableBackground = background.IsChecked == true;
                var enableSignIn = signIn.IsChecked == true;
                var enableReconnect = automatic.IsChecked == true;
                if (enableBackground && (!activationAvailable || tray is null)) throw new IOException("The local activation or notification-area control is unavailable. Restart iConnect before enabling background mode.");
                if (enableSignIn && startup is null && packageStartup is null) throw new IOException("Sign-in startup is unavailable from this installation.");
                if (packageStartup is null)
                {
                    Model.SetAvailabilityOptions(enableBackground, enableSignIn, enableReconnect);
                    startup?.SetEnabled(enableSignIn);
                }
                else if (enableSignIn)
                {
                    var taskState = await packageStartup.RequestEnableAsync();
                    if (taskState is not (Windows.ApplicationModel.StartupTaskState.Enabled or Windows.ApplicationModel.StartupTaskState.EnabledByPolicy))
                        throw new IOException("Windows did not enable iConnect at sign-in. Review Windows Startup settings.");
                    Model.SetAvailabilityOptions(enableBackground, true, enableReconnect);
                }
                else
                {
                    Model.SetAvailabilityOptions(enableBackground, false, enableReconnect);
                    packageStartup.Disable();
                }
                reconnect?.Reset(); ApplyTrayPreference(); Render();
            }), Body(StartupStatus()), Body(reconnect?.Notice ?? ""),
            Body(Model.Availability.Error.Length == 0 ? backgroundError : Model.Availability.Error), QuitButton()));
        Page.Children.Add(Card("This device's identity", "Compare the complete identity when reviewing a device. A device whose identity changes must be paired again.", Selectable(Model.DeviceId)));
        RenderTransferNetworkControls();
        Page.Children.Add(Card("Diagnostics", Model.Diagnostic.Length == 0 ? "No connection error reported. Direct connectivity depends on both networks; a failed direct path has no relay fallback." : Model.Diagnostic,
            Action("Review diagnostic report…", ExportDiagnosticsAsync)));
    }
    private string StartupStatus()
    {
        try
        {
            if (packageStartup is not null) return packageStartup.State switch
            {
                Windows.ApplicationModel.StartupTaskState.Enabled => "Windows has enabled iConnect at sign-in.",
                Windows.ApplicationModel.StartupTaskState.EnabledByPolicy => "Windows policy has enabled the package's sign-in task. iConnect still requires its saved sign-in preference.",
                Windows.ApplicationModel.StartupTaskState.DisabledByUser or Windows.ApplicationModel.StartupTaskState.DisabledByPolicy => "The package's sign-in task is disabled in Windows Startup settings or by policy.",
                _ => "The package's sign-in task is disabled."
            };
            return startup?.Enabled == true ? "A sign-in entry is registered for this Windows user and profile. Windows Startup settings may separately disable it." : "No matching sign-in entry is registered for this profile.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.Runtime.InteropServices.COMException)
        { return "Sign-in registration could not be read. Review it in Windows Startup settings."; }
    }
}
