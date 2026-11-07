using IConnect.AppModel;
using Microsoft.UI.Xaml.Controls;

namespace IConnect.Desktop;

public sealed partial class MainWindow
{
    private int activityBackupDialogEpoch;
    private async Task ManageActivityBackupsAsync()
    {
        var epoch = ++activityBackupDialogEpoch;
        bool Current() => !closing && !hidden && currentPage == "history" && epoch == activityBackupDialogEpoch;
        var page = 0; var notice = "";
        try
        {
            while (Current())
            {
                ActivityBackupEntry? selected = null; ContentDialog? listing = null;
                var items = new StackPanel { Spacing = 10 }; var loading = 0;
                async Task LoadPage()
                {
                    var load = ++loading; selected = null; if (listing is not null) listing.IsPrimaryButtonEnabled = false;
                    var inventory = await Model.InspectActivityBackups(page * 50, Current);
                    if (!Current() || load != loading) return;
                    items.Children.Clear();
                    items.Children.Add(Body($"{inventory.Total:N0} activity backups · page {page + 1}"));
                    if (inventory.KeptFolders > 0) items.Children.Add(Body($"{inventory.KeptFolders:N0} active, unrecognized or unsupported recovery folders are kept. Backups used by current recovery cannot be removed here."));
                    if (notice.Length != 0) items.Children.Add(Body(notice));
                    items.Children.Add(Row(Action("Previous activity backups", async () => { page--; await LoadPage(); }, page > 0),
                        Action("Next activity backups", async () => { page++; await LoadPage(); }, (page + 1) * 50 < inventory.Total)));
                    var choices = new ComboBox { Header = "Choose a preserved activity backup", HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch };
                    foreach (var entry in inventory.Entries)
                        choices.Items.Add(new ComboBoxItem { Content = $"{DateTimeOffset.FromUnixTimeMilliseconds(entry.ModifiedAt).ToLocalTime():g} · {entry.Files} files · {entry.Bytes:N0} bytes · {entry.Id[..8]}", Tag = entry });
                    choices.SelectionChanged += (_, _) =>
                    { selected = (choices.SelectedItem as ComboBoxItem)?.Tag as ActivityBackupEntry; if (listing is not null) listing.IsPrimaryButtonEnabled = selected is not null; };
                    items.Children.Add(choices);
                    if (choices.Items.Count != 0) choices.SelectedIndex = 0;
                    else items.Children.Add(Body("No removable activity backups are on this page. Current history and other metadata stores are kept."));
                }
                await LoadPage(); if (!Current()) return;
                listing = Dialog("Preserved activity backups", new ScrollViewer { MaxHeight = 460, Content = Column(
                    Body("Preserve and reset keeps earlier activity metadata separately. Clearing current history or turning recording off does not remove these copies. Review one activity-only backup at a time; transfer history, trust and other recovery archives are excluded."), items) }, "Review selected backup");
                listing.CloseButtonText = "Done"; listing.IsPrimaryButtonEnabled = selected is not null;
                if (await ShowAsync(listing) != ContentDialogResult.Primary || selected is null || !Current()) return;
                var review = await Model.ReviewActivityBackupRemoval(selected.Id, Current);
                var typed = new TextBox { Header = "Type REMOVE to confirm permanent removal", MaxLength = 6 };
                var confirmation = Dialog("Remove this preserved activity backup?", Column(
                    Body($"Permanently remove the exact reviewed {review.Files} metadata files ({review.Bytes:N0} bytes) in backup {review.Backup[..8]}. This cannot be undone. Current activity, transfer receipts, original files and other backups are kept. Changed files or a review older than two minutes require a fresh decision. If cleanup is interrupted, already removed metadata stays removed and remaining contents require review."), typed), "Remove activity backup");
                confirmation.IsPrimaryButtonEnabled = false;
                typed.TextChanged += (_, _) => confirmation.IsPrimaryButtonEnabled = typed.Text == "REMOVE";
                if (!Current()) return;
                if (await ShowAsync(confirmation) == ContentDialogResult.Primary)
                {
                    await Model.ConfirmActivityBackupRemoval(review, Current);
                    notice = "The reviewed activity backup was removed. Current history and other stores were kept.";
                }
                else Model.CancelActivityBackupReview();
                page = 0;
            }
        }
        finally
        {
            Model.CancelActivityBackupReview();
            if (epoch == activityBackupDialogEpoch) activityBackupDialogEpoch++;
        }
    }
}
