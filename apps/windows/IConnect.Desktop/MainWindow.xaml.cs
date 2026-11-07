using IConnect.AppModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Microsoft.UI.Text;
using System.Net.NetworkInformation;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel;

namespace IConnect.Desktop;

public sealed partial class MainWindow : Window
{
    private DesktopController? controller;
    private string currentPage = "home";
    private readonly SemaphoreSlim dialogGate = new(1);
    private ContentDialog? activeDialog;
    private bool closing;
    private bool allowClose;
    private readonly bool activationAvailable;
    private TrayHost? tray;
    private WindowsStartupRegistration? startup;
    private readonly StartupTask? packageStartup;
    private BackgroundReconnect? reconnect;
    private DispatcherQueueTimer? availabilityTimer;
    private TransferNetworkMonitor? transferNetworkMonitor;
    private bool hidden;
    private bool shutdownFailed;
    private string backgroundError = "";
    private readonly HashSet<Task> pendingActions = [];
    private DesktopController Model => controller ?? throw new InvalidOperationException("The local profile is unavailable.");

    public MainWindow(string state, bool development, bool activationAvailable, string? launchError = null,
        bool packaged = false, StartupTask? packageStartup = null)
    {
        this.activationAvailable = activationAvailable;
        this.packageStartup = packageStartup;
        InitializeComponent(); Title = "iConnect";
        Activated += (_, args) =>
        {
            windowActive = args.WindowActivationState != WindowActivationState.Deactivated;
            if (!windowActive) { pendingFocus = null; ClearPreviews(); }
        };
        var display = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
        AppWindow.Resize(new SizeInt32(Math.Min(1360, display.WorkArea.Width - 40), Math.Min(900, display.WorkArea.Height - 40)));
        AppWindow.Closing += async (_, args) =>
        {
            if (allowClose) return;
            args.Cancel = true;
            if (closing) return;
            if (CanHide) { activeDialog?.Hide(); ClearPreviews(); hidden = true; AppWindow.Hide(); return; }
            await QuitAsync();
        };
        if (launchError is not null) { Navigation.IsEnabled = false; Page.Children.Add(Heading("iConnect could not start")); Page.Children.Add(Body(launchError)); return; }
        try
        {
            controller = new DesktopController(state, action => DispatcherQueue.TryEnqueue(() => action()), development, new IConnect.Preview.Windows.WindowsPreviewRenderer());
            controller.Changed += ControllerChanged;
            transferNetworkMonitor = new(cost => controller.NetworkTraffic.ObserveNetwork(cost));
            controller.InvitationCreated += uri => _ = RunAsync(() => ShowInvitationAsync(uri));
            controller.PairingRequested += pairing => { activeDialog?.Hide(); _ = RunAsync(() => ShowPairingAsync(pairing)); };
            reconnect = new BackgroundReconnect(Model.Availability,
                () => new(Model.Connected, Model.Connecting || Model.PairingActive, Model.PeerAuthenticated, Model.PeerConnecting, Model.Settings.Coordinator.Length != 0 && Model.OwnerPolicyHealthy, Model.Peers.Select(x => x.Id).ToArray(), Model.SignallingRefreshNeeded),
                Model.ConnectAsync, Model.ConnectDeviceAsync, refreshSignalling: Model.DisconnectAsync);
            if (!packaged)
            {
                try { startup = new WindowsStartupRegistration(state, Environment.ProcessPath!); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { backgroundError = ex.Message; }
            }
            try { tray = new TrayHost(WinRT.Interop.WindowNative.GetWindowHandle(this), TrayAction); ApplyTrayPreference(); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or DllNotFoundException) { backgroundError = "The notification-area control is unavailable. The app will remain visible. " + ex.Message; }
            Navigation.SelectedItem = Navigation.MenuItems[0]; Render();
        }
        catch (Exception ex)
        {
            Navigation.IsEnabled = false;
            Page.Children.Add(Heading("iConnect could not open"));
            Page.Children.Add(Body("Another instance may already be using this local profile. Close it and try again. Preserve damaged local settings for recovery before making changes."));
            Page.Children.Add(Body(ex.Message));
        }
    }
    private void ControllerChanged(DesktopChange change)
    {
        if (change == DesktopChange.Payload) { RefreshProgress(); return; }
        ApplyTrayPreference(); UpdateTray();
        StatusText.Text = Model.Status;
        if ((change & DesktopChange.Files) != 0 && currentPage == "files" ||
            (change & DesktopChange.LocalFiles) != 0 && currentPage == "local" ||
            (change & DesktopChange.Transfers) != 0 && currentPage == "transfers" ||
            (change & DesktopChange.Grants) != 0 && currentPage == "shares" ||
            (change & DesktopChange.History) != 0 && currentPage == "history" ||
            (change & DesktopChange.Settings) != 0 && currentPage == "settings" ||
            (change & (DesktopChange.Status | DesktopChange.Devices)) != 0 && currentPage is "home" or "devices") Render();
    }
    private bool CanHide => !shutdownFailed && controller is not null && Model.OwnerPolicyHealthy && Model.Availability.Healthy && Model.Availability.Preferences.RunInBackground && activationAvailable && tray?.Installed == true;
    private void ApplyTrayPreference()
    {
        if (controller is null || tray is null) return;
        tray.SetEnabled(Model.OwnerPolicyHealthy && Model.Availability.Healthy && Model.Availability.Preferences.RunInBackground && activationAvailable);
        if (hidden && !CanHide) ShowVisible();
    }
    private void UpdateTray() => tray?.Update(Model.PeerAuthenticated ? "Connected directly" : Model.Connected ? "Coordinator connected" :
        Model.Connecting ? "Connecting" : Model.Availability.Preferences.Paused ? "Paused" : "Disconnected",
        Model.Connected || Model.Connecting || !Model.Availability.Preferences.Paused);
    public void StartRuntime(bool background)
    {
        if (controller is null) return;
        availabilityTimer = DispatcherQueue.CreateTimer(); availabilityTimer.Interval = TimeSpan.FromSeconds(1);
        availabilityTimer.Tick += async (_, _) =>
        {
            if (closing || reconnect is null) return;
            reconnect.NetworkAvailable = NetworkInterface.GetIsNetworkAvailable();
            await reconnect.TickAsync();
        };
        availabilityTimer.Start(); UpdateTray();
        if (background && CanHide) { ClearPreviews(); hidden = true; AppWindow.Hide(); }
    }
    public void ShowVisible()
    {
        if (closing) return;
        hidden = false;
        AppWindow.Show();
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter && presenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized) presenter.Restore();
        Activate();
    }
    private void TrayAction(TrayCommand command)
    {
        switch (command)
        {
            case TrayCommand.Open: ShowVisible(); break;
            case TrayCommand.ToggleAvailability:
                _ = RunAsync(Model.Connected || Model.Connecting || !Model.Availability.Preferences.Paused ? Model.PauseAvailabilityAsync : Model.ResumeAvailabilityAsync); break;
            case TrayCommand.Quit: _ = QuitAsync(); break;
            case TrayCommand.Suspended:
                if (reconnect is not null) { reconnect.SystemAvailable = false; reconnect.Reset(); }
                _ = RunAsync(Model.DisconnectAsync); break;
            case TrayCommand.Resumed:
                if (reconnect is not null) { reconnect.SystemAvailable = true; reconnect.Reset(); } break;
            case TrayCommand.TrayUnavailable:
                backgroundError = "The notification-area icon is unavailable. iConnect stays visible so you can pause or quit.";
                ShowVisible(); break;
        }
    }
    private async Task QuitAsync()
    {
        if (closing) return;
        closing = true; availabilityTimer?.Stop(); transferNetworkMonitor?.Dispose();
        activityBackupDialogEpoch++;
        controller?.CancelActivityBackupReview();
        if (reconnect is not null) reconnect.SystemAvailable = false;
        activeDialog?.Hide();
        try
        {
            controller?.LocalFiles.CancelBatchPlanning();
            controller?.LocalFiles.BatchCopies?.CancelVerification();
            if (controller is not null) await controller.DisconnectAsync();
            try { await Task.WhenAll(pendingActions.ToArray()); } catch { /* Individual actions already report failures. */ }
            if (controller is not null) await controller.DisposeAsync();
            tray?.Dispose(); allowClose = true; Close();
        }
        catch (Exception ex)
        {
            closing = false; shutdownFailed = true; ShowVisible();
            Notification.IsOpen = true; Notification.Severity = InfoBarSeverity.Error;
            Notification.Title = "Shutdown needs another attempt"; Notification.Message = ex.Message + " Choose Quit iConnect again.";
        }
    }
    private Button QuitButton()
    {
        var button = new Button { Content = "Quit iConnect" };
        button.Click += async (_, _) => await QuitAsync(); return button;
    }
    private void NavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (controller is null) return;
        ClearPreviews();
        currentPage = args.IsSettingsSelected ? "settings" : (args.SelectedItem as NavigationViewItem)?.Tag?.ToString() ?? "home";
        if (currentPage == "local" && Model.LocalFiles.Healthy && !Model.LocalFiles.Busy) _ = RunAsync(() => Model.LocalFiles.BrowseAsync());
        Render();
    }
    private void ClearPreviews()
    {
        activityBackupDialogEpoch++;
        controller?.CancelActivityBackupReview();
        controller?.FilePreview.Clear(); controller?.LocalFiles.FilePreview.Clear();
    }
    private void Navigate(string tag)
    {
        Navigation.SelectedItem = tag == "settings" ? Navigation.SettingsItem :
            Navigation.MenuItems.OfType<NavigationViewItem>().Single(x => (string)x.Tag == tag);
    }
    private void Render()
    {
        if (closing || controller is null) return;
        var focus = BeginPageRefresh();
        liveProgress.Clear();
        Page.Children.Clear(); Page.Spacing = currentPage is "files" or "local" ? 8 : 20; StatusText.Text = Model.Status;
        if (!Model.OwnerPolicyHealthy) Page.Children.Add(Card("Windows trust needs owner review", Model.OwnerPolicyNotice,
            Go("Review Windows identity", "settings")));
        switch (currentPage)
        {
            case "devices": RenderDevices(); break;
            case "files": RenderFiles(); break;
            case "local": RenderLocalFiles(); break;
            case "transfers": RenderTransfers(); break;
            case "shares": RenderShares(); break;
            case "history": RenderHistory(); break;
            case "settings": RenderSettings(); break;
            default: RenderHome(); break;
        }
        RestoreRefreshFocus(focus);
    }
    private static TextBlock Heading(string value) => new() { Text = value, FontSize = 30, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private static TextBlock Body(string value, double size = 14) => new() { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap };
    private static TextBlock Selectable(string value) => new() { Text = value, IsTextSelectionEnabled = true,
        TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Mono"), FontSize = 12 };
    private static StackPanel Column(params UIElement[] children)
    {
        var result = new StackPanel { Spacing = 12 };
        foreach (var child in children) result.Children.Add(child);
        return result;
    }
    private static StackPanel Row(params UIElement[] children)
    {
        var result = new StackPanel { Spacing = 12, Orientation = Orientation.Horizontal };
        foreach (var child in children) result.Children.Add(child);
        return result;
    }
    private static Border Card(string title, string text, params UIElement[] actions) => Card(title, Body(text), actions);
    private static Border Card(string title, UIElement subtitle, params UIElement[] actions) => new()
    {
        Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
        BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"], BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8), Padding = new Thickness(22),
        Child = Column([new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap }, subtitle, .. actions])
    };
    private Button Action(string label, Func<Task> action, bool enabled = true, bool accent = false, string? id = null)
    {
        var button = new Button { Content = label, IsEnabled = enabled };
        if (accent) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        if (id is not null) AutomationProperties.SetAutomationId(button, id);
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try { await RunAsync(action); }
            finally { if (button.IsLoaded) button.IsEnabled = enabled; }
        };
        return button;
    }
    private Button Go(string label, string page) => Action(label, () => { Navigate(page); return Task.CompletedTask; });
    private async Task RunAsync(Func<Task> action)
    {
        if (closing) return;
        Task? pending = null;
        try { pending = action(); pendingActions.Add(pending); await pending; }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (closing) return;
            Notification.Severity = InfoBarSeverity.Error; Notification.Title = "The action could not finish";
            Notification.Message = ex.Message; Notification.IsOpen = true;
        }
        finally { if (pending is not null) pendingActions.Remove(pending); }
    }
    private ContentDialog Dialog(string title, UIElement content, string primary) => new()
    {
        XamlRoot = Content.XamlRoot, Title = title, Content = content, PrimaryButtonText = primary,
        CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
    };
    private async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        if (closing) return ContentDialogResult.None;
        await dialogGate.WaitAsync();
        try { if (closing) return ContentDialogResult.None; activeDialog = dialog; return await dialog.ShowAsync(); }
        finally { if (ReferenceEquals(activeDialog, dialog)) activeDialog = null; dialogGate.Release(); }
    }
}
