using IConnect.Core;
using Microsoft.UI.Xaml.Controls;

namespace IConnect.Desktop;

public sealed partial class MainWindow
{
    private void RenderTransferNetworkControls()
    {
        var controls = Model.TransferNetwork;
        if (controls is null) { Page.Children.Add(Card("Transfer network settings unavailable", Model.TransferNetworkIssue)); return; }
        var rates = new long[] { 0, 65536, 131072, 524288, 1048576, 5242880, 10485760, 67108864 };
        var labels = new[] { "Unlimited", "64 KiB/s", "128 KiB/s", "512 KiB/s", "1 MiB/s", "5 MiB/s", "10 MiB/s", "64 MiB/s" };
        var selected = Array.IndexOf(rates, controls.Preferences.BytesPerSecond);
        if (selected < 0) { rates = rates.Append(controls.Preferences.BytesPerSecond).ToArray(); labels = labels.Append(controls.Preferences.BytesPerSecond + " bytes/s (saved)").ToArray(); selected = rates.Length - 1; }
        var rate = new ComboBox { Header = "Remote file content limit per direction", ItemsSource = labels, SelectedIndex = selected, IsEnabled = controls.Healthy };
        var metered = new CheckBox { Content = "Pause remote file content on metered or unknown networks", IsChecked = controls.Preferences.PauseOnMetered, IsEnabled = controls.Healthy };
        var content = Column(Body("The limit is shared by remote files and previews in each direction on this computer. Status, cleanup and local copies remain available. Protocol overhead and already buffered data can still use the network; this is not a billing cap."),
            Body("Observed connected-network cost: " + controls.Policy.NetworkCost),
            Body("Cost checks consider all connected Windows profiles conservatively, including roaming and data restrictions. They do not prove which connection WebRTC uses. A reading older than five seconds is treated as unknown."), rate, metered,
            Action("Review transfer network settings…", async () =>
            {
                if (rate.SelectedIndex < 0) throw new InvalidOperationException("Choose a bandwidth limit.");
                var value = new TransferNetworkPreferences(rates[rate.SelectedIndex], metered.IsChecked == true);
                if (await ShowAsync(Dialog("Save transfer network controls?", Column(Body("Per direction: " + labels[rate.SelectedIndex]),
                    Body(value.PauseOnMetered ? "Remote content pauses when cost is metered, restricted or unknown. Review interrupted work and explicitly resume after the network is suitable." : "Remote content can use metered and unknown networks. Carrier or network charges may apply."),
                    Body("The limit applies to active and future requests. Already transmitted data cannot be recalled. This does not change sharing approvals or automatically resume interrupted jobs.")), "Save controls")) == ContentDialogResult.Primary)
                    controls.Set(value);
            }, controls.Healthy));
        if (!controls.Healthy) content.Children.Add(Card("Transfer network settings need review", controls.Issue, Action(controls.Missing ? "Review and reset missing network settings…" : "Preserve and reset transfer network settings…", async () =>
        {
            if (await ShowAsync(Dialog("Reset transfer network settings?", Body((controls.Missing ? "The earlier settings file is missing and cannot be preserved. Create a fresh settings file using defaults: " : "Preserve damaged settings metadata and use defaults: ") + "unlimited file bandwidth and metered-network pausing off. Files, receipts and sharing approvals are kept. Review interrupted transfers before resuming."), controls.Missing ? "Reset missing settings" : "Preserve and reset")) == ContentDialogResult.Primary)
                controls.PreserveAndReset();
        }, controls.CanReset)));
        if (controls.PreservedRecords != 0) content.Children.Add(Action("Remove preserved network-settings metadata…", async () =>
        {
            if (await ShowAsync(Dialog("Remove network-settings backups?", Body("Remove only preserved settings metadata. Current controls, files, receipts and sharing approvals are kept."), "Remove metadata backups")) == ContentDialogResult.Primary)
                controls.ClearPreserved();
        }));
        Page.Children.Add(Card("Transfer network controls", "These preferences belong to this Windows profile. Initially, bandwidth is unlimited and metered-network pausing is off.", content));
    }
}
