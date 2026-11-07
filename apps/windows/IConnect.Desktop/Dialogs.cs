using IConnect.Connectivity;
using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace IConnect.Desktop;

public sealed partial class MainWindow
{
    private async Task ConfirmRevocationAsync(ProofClient.TrustedPeer peer)
    {
        var dialog = Dialog("Revoke " + peer.Name + "?", Column(Body("This ends active access, forgets this device's identity, and removes its Windows storage approvals. Pair again to reconnect."), Selectable(peer.Id)), "Revoke device");
        if (await ShowAsync(dialog) == ContentDialogResult.Primary) await Model.RevokeAsync(peer.Id);
    }
    private async Task PickFolderAsync()
    {
        var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) await EditGrantAsync(null, folder.Path);
    }
    private async Task PickDriveAsync()
    {
        var drives = DriveInfo.GetDrives().Where(x => x.DriveType is DriveType.Fixed or DriveType.Removable && x.IsReady).ToArray();
        var choice = new ComboBox { Header = "Drive", PlaceholderText = "Choose a drive", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var drive in drives) choice.Items.Add(drive.Name);
        var dialog = Dialog("Choose an entire drive", Column(Body("A drive approval includes its accessible folders. You can exclude paths and limit permissions before approving."), choice), "Review permissions");
        if (await ShowAsync(dialog) == ContentDialogResult.Primary && choice.SelectedIndex >= 0)
            await EditGrantAsync(null, drives[choice.SelectedIndex].RootDirectory.FullName);
    }
    private async Task EditGrantAsync(StorageGrant? grant, string? path)
    {
        var peers = Model.Peers.ToArray();
        var peerChoice = new ComboBox { Header = "Approved device", HorizontalAlignment = HorizontalAlignment.Stretch,
            PlaceholderText = "Choose a paired device", IsEnabled = grant is null };
        foreach (var peer in peers) peerChoice.Items.Add(peer.Name);
        peerChoice.SelectedIndex = grant is null ? peers.Length == 1 ? 0 : -1 : Array.FindIndex(peers, x => x.Id == grant.PeerId);
        var localPath = grant?.DisplayPath ?? path ?? "";
        var label = new TextBox { Header = "Location name", MaxLength = 80,
            Text = grant?.Label ?? (Path.GetFileName(localPath.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } leaf ? leaf : localPath) };
        var enabled = new ToggleSwitch { Header = "Access", IsOn = grant?.Enabled ?? true, OnContent = "Enabled", OffContent = "Paused" };
        var checks = new Dictionary<StoragePermission, CheckBox>();
        var permissions = new StackPanel { Spacing = 6 };
        var available = StoragePermission.List | StoragePermission.Read | StoragePermission.Write | StoragePermission.Create | StoragePermission.Rename | StoragePermission.Move | StoragePermission.Delete;
        foreach (var (permission, description) in new[] { (StoragePermission.List, "List folders"), (StoragePermission.Read, "Read files"),
            (StoragePermission.Create, "Create files and folders"), (StoragePermission.Rename, "Rename items"), (StoragePermission.Move, "Move items"),
            (StoragePermission.Write, "Receive new file copies (also requires Create)"), (StoragePermission.Delete, "Move items to iConnect recovery"), (StoragePermission.Replace, "Replace existing files") })
        {
            var initial = ((grant?.Permissions ?? StoragePermission.ReadOnly) & permission) != 0;
            var check = new CheckBox { Content = description, IsChecked = initial, IsEnabled = (available & permission) != 0 || initial };
            if (!check.IsEnabled) ToolTipService.SetToolTip(check, "Unavailable for this location.");
            checks.Add(permission, check); permissions.Children.Add(check);
        }
        var expiry = new ComboBox { Header = "Expiry", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var text in new[] { "No expiry", "30 minutes from now", "1 day from now", "7 days from now", "Keep current expiry" }) expiry.Items.Add(text);
        expiry.SelectedIndex = grant?.ExpiresAt is null ? 0 : 4;
        var exclusions = new TextBox { Header = "Excluded paths", PlaceholderText = "One path relative to this location per line",
            Text = string.Join(Environment.NewLine, grant?.Exclusions ?? []), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90, MaxLength = 8192 };
        void Profile(StoragePermission mask) { foreach (var item in checks) item.Value.IsChecked = (mask & item.Key) != 0; }
        var profiles = Row(Action("Read only", () => { Profile(StoragePermission.ReadOnly); return Task.CompletedTask; }),
            Action("Contribute", () => { Profile(StoragePermission.Contribute); return Task.CompletedTask; }),
            Action("Receive-only inbox", () => { Profile(StoragePermission.Write | StoragePermission.Create); return Task.CompletedTask; }));
        var form = Column(Body("These permissions apply only to the selected device."), Selectable(localPath),
            peerChoice, label, enabled, profiles, Body("Receive-only locations can accept new copies without browsing or reading existing files. Received names include unique suffixes. Copies are bounded by incoming quotas; replacement stays unavailable."),
            Body("Allowed actions"), permissions, expiry, exclusions);
        var dialog = Dialog(grant is null ? "Approve storage access" : "Edit storage access", new ScrollViewer { Content = form, MaxHeight = 520 }, grant is null ? "Approve access" : "Save permissions");
        if (await ShowAsync(dialog) != ContentDialogResult.Primary) return;
        if (peerChoice.SelectedIndex < 0) throw new InvalidDataException("Choose a paired device before approving access.");
        var mask = checks.Where(x => x.Value.IsChecked == true).Aggregate(StoragePermission.None, (value, x) => value | x.Key);
        DateTimeOffset? expiration = expiry.SelectedIndex switch { 1 => DateTimeOffset.UtcNow.AddMinutes(30), 2 => DateTimeOffset.UtcNow.AddDays(1),
            3 => DateTimeOffset.UtcNow.AddDays(7), 4 => grant?.ExpiresAt is { } at ? DateTimeOffset.FromUnixTimeMilliseconds(at) : null, _ => null };
        var excluded = exclusions.Text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (grant is null) await Model.AddGrantAsync(peers[peerChoice.SelectedIndex].Id, localPath, label.Text, mask, excluded, expiration, enabled.IsOn);
        else await Model.UpdateGrantAsync(grant.Id, mask, excluded, expiration, enabled.IsOn, label.Text);
    }
    private async Task ShowInvitationAsync(string uri)
    {
        using var data = QRCodeGenerator.GenerateQrCode(uri, QRCodeGenerator.ECCLevel.M);
        using var code = new PngByteQRCode(data);
        using var bytes = new MemoryStream(code.GetGraphic(6));
        var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(bytes.AsRandomAccessStream());
        var text = new TextBox { Text = uri, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 100 };
        var dialog = new ContentDialog { XamlRoot = Content.XamlRoot, Title = "Pair your phone", CloseButtonText = "Done",
            Content = Column(Body("Scan this invitation in iConnect on Android. It expires in two minutes and can be claimed once."),
                new Image { Source = bitmap, Width = 280, Height = 280 }, text,
                Action("Copy invitation", () => { var package = new DataPackage(); package.SetText(uri); Clipboard.SetContent(package); return Task.CompletedTask; })) };
        await ShowAsync(dialog);
    }
    private async Task ShowPairingAsync(Pairing pairing)
    {
        var phone = pairing.Guest;
        var verification = new TextBox { Header = "Code shown on the other device", MaxLength = 6, PlaceholderText = "6 digits", InputScope = new Microsoft.UI.Xaml.Input.InputScope
            { Names = { new Microsoft.UI.Xaml.Input.InputScopeName { NameValue = Microsoft.UI.Xaml.Input.InputScopeNameValue.Number } } } };
        var dialog = Dialog("Approve " + phone.Name + "?", Column(Body("Compare the code on both devices. Approve only the device you intended to pair. Storage sharing remains a separate decision."),
            new TextBlock { Text = ProtocolRules.VerificationCode(pairing), FontSize = 40, FontFamily = new FontFamily("Cascadia Mono") },
            Selectable(ProtocolRules.DeviceId(phone.PublicKey.ToByteArray())), verification), "Approve device");
        dialog.CloseButtonText = "Reject";
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (verification.Text != ProtocolRules.VerificationCode(pairing) || pairing.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            { args.Cancel = true; verification.Description = "Enter the matching code before the invitation expires."; }
        };
        if (await ShowAsync(dialog) == ContentDialogResult.Primary) await Model.ApproveAsync(verification.Text);
        else if (Model.Connected) await Model.RejectAsync();
    }
}
