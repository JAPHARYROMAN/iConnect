using IConnect.Core;
using IConnect.Storage.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;

namespace IConnect.Desktop;

public sealed partial class MainWindow
{
    private string localQuery = "";
    private int localCopyPage;
    private void RenderLocalFiles()
    {
        var browser = Model.LocalFiles;
        Page.Children.Add(Heading("On this computer"));
        Page.Children.Add(Body("Browse folders you choose on this computer. Local shortcuts work offline and do not share files with a device."));
        RenderLocalCopies();
        RenderLocalBatchCopies();
        RenderLocalManagement();
        if (!browser.Healthy)
        {
            Page.Children.Add(Body(browser.Error));
            Page.Children.Add(Action("Reset local shortcuts…", async () =>
            {
                if (await ShowAsync(Dialog("Reset local folder shortcuts?", Body("This preserves the damaged shortcut record for recovery. Files, retained copy jobs and device sharing approvals are kept."), "Reset shortcuts")) == ContentDialogResult.Primary)
                    await browser.ResetAsync();
            })); return;
        }
        Page.Children.Add(Row(Action("Choose local folder…", async () =>
        {
            var picker = new Windows.Storage.Pickers.FolderPicker(); picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync(); if (folder is not null) await browser.AddAsync(folder.Path);
        }, !browser.Busy), Action("Refresh", () => browser.BrowseAsync(browser.Breadcrumbs.LastOrDefault()), !browser.Busy)));
        var crumbs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        crumbs.Children.Add(Action("Local folders", () => browser.BrowseAsync(), !browser.Busy));
        foreach (var entry in browser.Breadcrumbs) crumbs.Children.Add(Action(entry.Name, () => browser.BrowseAsync(entry), !browser.Busy));
        Page.Children.Add(new ScrollViewer { Content = crumbs, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        if (browser.Busy) Page.Children.Add(Row(new ProgressRing { IsActive = true, Width = 20, Height = 20 }, Action("Cancel local discovery", () => { browser.Cancel(); return Task.CompletedTask; })));
        if (browser.Error.Length != 0) Page.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Message = browser.Error });
        if (browser.Notice.Length != 0) Page.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Message = browser.Notice });
        if (browser.Breadcrumbs.Count == 0)
        {
            if (browser.Locations.Count == 0) Page.Children.Add(Body("Choose a folder to inspect its files. No local folders are saved yet."));
            foreach (var location in browser.Locations)
            {
                var root = browser.Rows.FirstOrDefault(x => x.Entry.GrantId == location.Id)?.Entry;
                Page.Children.Add(Card(location.Label, location.Path, Body(root is null ? "Unavailable. Refresh or choose this folder again to review its current identity." : "Local shortcut · not a sharing approval"),
                    Row(Action("Open local folder", () => browser.BrowseAsync(root), root is not null && !browser.Busy), Action("Remove shortcut…", async () =>
                    {
                        if (await ShowAsync(Dialog("Remove local shortcut?", Body("Files, sharing approvals and retained local copy jobs are kept. Only this shortcut is removed."), "Remove shortcut")) == ContentDialogResult.Primary)
                            await browser.RemoveAsync(location.Id);
                    }, !browser.Busy))));
            }
            return;
        }
        var query = new TextBox { Header = "Search local filenames in this folder and descendants", Text = localQuery, MaxLength = 128, IsEnabled = !browser.Busy };
        query.TextChanged += (_, _) => localQuery = query.Text; Page.Children.Add(query);
        Page.Children.Add(Row(Action("Search local scope", () => browser.SearchAsync(localQuery), !browser.Busy), Action("Clear search", () => browser.BrowseAsync(browser.Breadcrumbs.Last()), !browser.Busy)));
        var sort = new ComboBox { Header = "Sort loaded local items", ItemsSource = Enum.GetNames<ExplorerSort>(), SelectedIndex = (int)browser.Sort };
        sort.SelectionChanged += (_, _) => { if (sort.SelectedIndex >= 0 && sort.SelectedIndex != (int)browser.Sort) browser.SetSort((ExplorerSort)sort.SelectedIndex, browser.Descending); };
        var descending = new CheckBox { Content = "Descending", IsChecked = browser.Descending, VerticalAlignment = VerticalAlignment.Bottom };
        descending.Click += (_, _) => browser.SetSort(browser.Sort, descending.IsChecked == true);
        Page.Children.Add(Row(sort, descending, Action(browser.Layout == ExplorerLayout.List ? "Grid view" : "List view", () => { browser.SetLayout(browser.Layout == ExplorerLayout.List ? ExplorerLayout.Grid : ExplorerLayout.List); return Task.CompletedTask; })));
        Page.Children.Add(Body($"{browser.Count:N0} loaded local items · page {browser.Page + 1} / {browser.Pages}"));
        if (browser.Count == 0 && !browser.Busy) Page.Children.Add(Body(browser.Notice.Length != 0 ? "Discovery was incomplete. Review the notice above." : browser.Query.Length == 0 ? "No accessible items in this local folder." : "No loaded filenames match this query."));
        ListViewBase list = browser.Layout == ExplorerLayout.Grid ? new GridView { SelectionMode = ListViewSelectionMode.Multiple, MaxHeight = 400 } : new ListView { SelectionMode = ListViewSelectionMode.Multiple, MaxHeight = 400 };
        AutomationProperties.SetName(list, "Files on this computer");
        foreach (var row in browser.Rows)
        {
            var entry = row.Entry;
            var content = Column(Body(entry.Name, 15), Body(entry.IsDirectory ? "Folder" : $"{entry.Size:N0} bytes · {row.Type}", 12), Body(browser.Query.Length == 0 ? DateTimeOffset.FromUnixTimeMilliseconds(entry.ModifiedAt).ToLocalTime().ToString("g") : row.RelativePath, 12));
            ContentControl item = browser.Layout == ExplorerLayout.Grid ? new GridViewItem { Width = 220, MinHeight = 160 } : new ListViewItem { MinHeight = 110 };
            item.HorizontalContentAlignment = HorizontalAlignment.Stretch; item.VerticalContentAlignment = VerticalAlignment.Center;
            item.Tag = entry.Id; item.Content = content; AutomationProperties.SetName(item, entry.Name); list.Items.Add(item);
            if (browser.SelectedIds.Contains(entry.Id)) list.SelectedItems.Add(item);
        }
        var selectionSummary = Body($"{browser.SelectedIds.Count} selected · select up to 128 files and folders across loaded pages"); var restoring = false;
        list.SelectionChanged += (_, _) =>
        {
            if (restoring) return;
            var visible = browser.Rows.Select(x => x.Entry.Id).ToHashSet();
            var wanted = browser.SelectedIds.Where(x => !visible.Contains(x)).Concat(list.SelectedItems.Cast<FrameworkElement>().Select(x => (string)x.Tag)).ToArray();
            if (wanted.Length > 128)
            {
                restoring = true; list.SelectedItems.Clear(); foreach (FrameworkElement item in list.Items) if (browser.SelectedIds.Contains((string)item.Tag)) list.SelectedItems.Add(item); restoring = false;
                selectionSummary.Text = "Selection limit reached: 128 files and folders."; return;
            }
            browser.SelectMany(wanted); selectionSummary.Text = $"{wanted.Length} selected · choose one item for preview or details";
        }; Page.Children.Add(selectionSummary); Page.Children.Add(list);
        Page.Children.Add(Row(Action("Open selected item", async () =>
        {
            var row = browser.Selected ?? throw new InvalidOperationException("Select a local item first.");
            if (row.Entry.IsDirectory) await browser.BrowseAsync(row.Entry); else await browser.InspectAsync(true);
        }, !browser.Busy), Action("Item details", async () =>
        {
            await browser.InspectAsync(false); var entry = browser.Details; if (entry is null) return;
            var dialog = Dialog("Local item details", Column(Body(entry.Name), Body(entry.IsDirectory ? "Folder" : $"{entry.Size:N0} bytes"), Body($"Modified: {DateTimeOffset.FromUnixTimeMilliseconds(entry.ModifiedAt):u}"), Body("This is a local view. Sharing permissions are managed separately.")), "Done");
            dialog.CloseButtonText = ""; dialog.DefaultButton = ContentDialogButton.Primary; await ShowAsync(dialog);
        }, !browser.Busy)));
        Page.Children.Add(Action("Copy selected file on this computer…", async () =>
        {
            var selected = await browser.CaptureCopyAsync();
            var choice = await ReviewLocalCopy(selected.Name, selected.Size, selected.SourceLocation); if (choice is null) return;
            var id = await browser.PrepareCopyAsync(selected); await PickLocalCopyDestination(id, choice.Value);
        }, !browser.Busy && browser.Copies is not null));
        Page.Children.Add(Action("Plan selected files and folders…", async () =>
        {
            var selected = await browser.CaptureBatchSelectionAsync(); await browser.PrepareBatchAsync(selected);
            RenderLocalFilesAfterCopy();
        }, !browser.Busy && browser.BatchCopies is { Planning: false }));
        Page.Children.Add(Row(Action("Previous page", () => { browser.SetPage(browser.Page - 1); return Task.CompletedTask; }, browser.Page > 0 && !browser.Busy),
            Action("Next page", () => { browser.SetPage(browser.Page + 1); return Task.CompletedTask; }, browser.Page + 1 < browser.Pages && !browser.Busy)));
        AddPreview(browser.FilePreview);
    }
    private async Task<DownloadCollision?> ReviewLocalCopy(string name, long size, string source)
    {
        var policy = new ComboBox { Header = "If a destination name exists", ItemsSource = new[] { "Keep both files", "Skip this copy", "Stop for review" }, SelectedIndex = 0 };
        var dialog = Dialog("Copy this file on this computer?", Column(Body($"{name} · {size:N0} bytes"), Body("Source: " + source),
            Body("The original stays intact. No pairing or coordinator is required. Choose an available local folder; synchronization software may upload folders you choose."),
            Body("The new file is marked untrusted and must pass Windows attachment checks and byte verification before completion. Copying continues while the app is open; closing pauses it. Restarted jobs wait for your action."), policy), "Choose destination…");
        if (await ShowAsync(dialog) != ContentDialogResult.Primary) return null;
        return policy.SelectedIndex switch { 1 => DownloadCollision.Skip, 2 => DownloadCollision.Fail, _ => DownloadCollision.KeepBoth };
    }
    private async Task PickLocalCopyDestination(string id, DownloadCollision collision)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker(); picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var destination = await picker.PickSingleFolderAsync();
        if (destination is not null) await Task.Run(() => Model.LocalFiles.Copies!.ChooseDestination(id, destination.Path, collision));
        RenderLocalFilesAfterCopy();
    }
    private void RenderLocalFilesAfterCopy() => Render();
    private void RenderLocalCopies()
    {
        var browser = Model.LocalFiles; var copies = browser.Copies;
        if (copies is null) { Page.Children.Add(Body(browser.CopyIssue)); return; }
        var jobs = copies.Jobs; var issues = copies.RecoveryIssues;
        if (jobs.Count == 0 && issues.Count == 0 && copies.PreservedRecords == 0) return;
        Page.Children.Add(Heading("Copies on this computer"));
        Page.Children.Add(Body("Copy jobs are independent of folder shortcuts and sharing approvals. Removing a record keeps the original and completed copy. New files remain untrusted until verification succeeds."));
        foreach (var issue in issues)
            Page.Children.Add(Card("Local copy progress needs review", issue.Message,
                Action("Preserve damaged copy metadata…", async () =>
                {
                    if (await ShowAsync(Dialog("Preserve damaged copy progress?", Body("Only the exact damaged metadata is moved aside. Sources, partial files and published copies are kept. Missing source access requires a fresh source selection."), "Preserve metadata")) == ContentDialogResult.Primary)
                        await Task.Run(() => copies.Preserve(issue.Id));
                }, issue.CanArchive && jobs.All(x => !copies.Active(x.Id)))));
        if (copies.PreservedRecords > 0) Page.Children.Add(Action($"Remove {copies.PreservedRecords:N0} preserved copy metadata records…", async () =>
        {
            if (await ShowAsync(Dialog("Remove preserved copy metadata?", Body("This removes separately preserved metadata only. Active records, originals, partial files and published copies are kept."), "Remove metadata")) == ContentDialogResult.Primary)
                await Task.Run(copies.ClearPreserved);
        }, jobs.All(x => !copies.Active(x.Id))));
        var pages = Math.Max(1, (jobs.Count + 19) / 20); localCopyPage = Math.Clamp(localCopyPage, 0, pages - 1);
        foreach (var item in jobs.Skip(localCopyPage * 20).Take(20))
        {
            var row = item.Transfer; var active = copies.Active(item.Id); var controls = new StackPanel { Spacing = 8 };
            controls.Children.Add(Body("Source: " + item.SourceLocation));
            if (item.MoveDestination is { } moveDestination) controls.Children.Add(Body("Move destination: " + moveDestination));
            if (row is null && item.MoveDestination is not null)
                controls.Children.Add(Action("Start the recorded move copy", () => Task.Run(() => copies.StartVerifiedMoveCopy(item.Id)), issues.Count == 0));
            else if (row is null) controls.Children.Add(Action("Choose destination for this draft…", async () =>
            { var policy = await ReviewLocalCopy(item.Name, item.Size, item.SourceLocation); if (policy is not null) await PickLocalCopyDestination(item.Id, policy.Value); }, issues.Count == 0));
            else
            {
                DownloadJob Snapshot() => copies.FileProgress(row.Id) ?? row;
                controls.Children.Add(ProgressBody(() =>
                {
                    var current = Snapshot();
                    return $"Destination: {current.DisplayPath}\n{current.Offset:N0} / {current.Size:N0} bytes · {current.Phase}";
                }));
                controls.Children.Add(ProgressBody(() => browser.CopyPayloadText(Snapshot())));
                if (row.FinalName.Length != 0) controls.Children.Add(Body("Recorded destination name: " + row.FinalName));
                if (row.Error.Length != 0) controls.Children.Add(Body(row.Error));
                if (copies.FinishingMove(item.Id)) controls.Children.Add(Body("Checking the original and verified destination…"));
                else if (active) controls.Children.Add(Action("Pause local copy", () => { copies.Pause(item.Id); return Task.CompletedTask; }));
                else if (row.Phase is not (DownloadPhase.Completed or DownloadPhase.Canceled or DownloadPhase.Skipped))
                    controls.Children.Add(Action("Resume and verify local copy", () => { copies.Resume(item.Id); return Task.CompletedTask; }, issues.Count == 0));
                if (!active && row.FinalName.Length != 0 && row.Hash.Length == 64 && row.Offset == row.Size)
                    controls.Children.Add(Action("Recheck only the recorded copy", () => { copies.Recheck(item.Id); return Task.CompletedTask; }, issues.Count == 0));
                if (!active && item.MoveDestination is not null && row.Phase == DownloadPhase.Completed)
                    controls.Children.Add(Action("Review original for recovery…", () => FinishLocalMove(item.Id),
                        issues.Count == 0 && browser.Management is not null && !browser.ManagementBusy));
            }
            if (row?.Phase is DownloadPhase.Completed or DownloadPhase.Canceled or DownloadPhase.Skipped)
                controls.Children.Add(Action("Remove local copy record…", async () =>
                {
                    if (await ShowAsync(Dialog("Remove this local copy record?", Body("Original and destination files are kept. Only saved progress and its source reference are removed."), "Remove record")) == ContentDialogResult.Primary)
                        await Task.Run(() => copies.Remove(item.Id));
                }, !active));
            else controls.Children.Add(Action("Cancel local copy…", async () =>
            {
                if (await ShowAsync(Dialog("Cancel this local copy?", Body("Only this job's identified partial file is eligible for cleanup. Originals and published files are kept. Uncertain cleanup remains available for review."), "Cancel copy")) == ContentDialogResult.Primary)
                    await copies.Cancel(item.Id);
            }));
            Page.Children.Add(RefreshableCard($"local-file/{item.Id}", item.Name, $"{item.Size:N0} bytes", controls));
        }
        Page.Children.Add(Row(Action("Previous local copies", () => { localCopyPage--; RenderLocalFilesAfterCopy(); return Task.CompletedTask; }, localCopyPage > 0),
            Body($"{localCopyPage + 1} / {pages}"), Action("Next local copies", () => { localCopyPage++; RenderLocalFilesAfterCopy(); return Task.CompletedTask; }, localCopyPage + 1 < pages)));
    }
}
