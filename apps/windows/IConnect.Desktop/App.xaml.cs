using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using IConnect.AppModel;
using System.Runtime.InteropServices;
using Windows.ApplicationModel;
using Windows.Storage;

namespace IConnect.Desktop;

public partial class App : Application
{
    private MainWindow? window;
    private DesktopActivation? activation;
    private const string StartupTaskId = "iConnectStartup";
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorNoPackage = 15700;
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref uint length, IntPtr name);

    public App() { InitializeComponent(); }
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        DesktopLaunch launch; string? launchError = null; var packaged = false;
        try
        {
            uint length = 0;
            var packageStatus = GetCurrentPackageFullName(ref length, IntPtr.Zero);
            packaged = packageStatus == ErrorInsufficientBuffer;
            if (packaged)
            {
                var signIn = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs()?.Kind == ExtendedActivationKind.StartupTask;
                var profile = Path.Combine(ApplicationData.Current.LocalFolder.Path, "desktop");
                launch = new(profile, Background: signIn, Development: false, SignIn: signIn);
            }
            else if (packageStatus == ErrorNoPackage) launch = DesktopLaunch.Parse(Environment.GetCommandLineArgs().Skip(1).ToArray());
            else throw new IOException("Windows package identity could not be determined.");
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or COMException or UnauthorizedAccessException)
        { launch = DesktopLaunch.Parse([]); launchError = ex.Message; }
        if (launch.SignIn && launchError is null)
        {
            try
            {
                var saved = new DesktopAvailability(launch.Profile);
                if (saved.Healthy && (!saved.Preferences.StartAtSignIn || !saved.Preferences.RunInBackground)) { Exit(); return; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { launchError = "Saved sign-in preferences could not be read. " + ex.Message; }
        }
        try { if (launchError is null) activation = new DesktopActivation(launch.Profile); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (launch.SignIn) { Exit(); return; }
            if (await DesktopActivation.TryOpenAsync(launch.Profile)) { Exit(); return; }
        }
        StartupTask? packageStartup = null;
        if (packaged && launchError is null)
        {
            try { packageStartup = await StartupTask.GetAsync(StartupTaskId); }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException or ArgumentException) { }
        }
        window = new MainWindow(launch.Profile, launch.Development, activation is not null, launchError, packaged, packageStartup);
        if (activation is not null)
        {
            activation.Start(() =>
            {
                var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!window.DispatcherQueue.TryEnqueue(() => { window.ShowVisible(); opened.TrySetResult(); })) opened.TrySetCanceled();
                return opened.Task;
            });
            window.Closed += async (_, _) => { if (activation is not null) await activation.DisposeAsync(); activation = null; };
        }
        window.Activate();
        window.StartRuntime(launch.Background);
    }
}
