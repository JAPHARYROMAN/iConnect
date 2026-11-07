using IConnect.AppModel;
using IConnect.Core;
using Microsoft.Win32;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Xunit;

namespace IConnect.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class BackgroundAvailabilityTests : IDisposable
{
    private readonly string root;
    private readonly string profile;
    private readonly string registryPath = @"Software\iConnect\Tests\" + ProtocolRules.NewToken();
    public BackgroundAvailabilityTests()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Directory.Build.props"))) repository = repository.Parent;
        root = Path.Combine(repository!.FullName, ".local", "availability-tests", ProtocolRules.NewToken()[..12]);
        profile = Path.Combine(root, "profile"); Directory.CreateDirectory(profile);
    }
    [Fact]
    public void Fresh_profile_and_damaged_settings_cannot_start_background_access()
    {
        var state = new DesktopAvailability(profile);
        Assert.True(state.Preferences.Paused); Assert.False(state.Preferences.RunInBackground); Assert.False(state.AutomaticAllowed);
        Assert.Throws<InvalidDataException>(() => state.SetOptions(false, true, true));
        var path = Path.Combine(profile, "availability", "preferences.json"); File.WriteAllText(path, "damaged settings");
        var restarted = new DesktopAvailability(profile);
        Assert.False(restarted.Healthy); Assert.False(restarted.AutomaticAllowed); Assert.False(restarted.Preferences.RunInBackground);
        Assert.Throws<IOException>(() => restarted.Resume()); Assert.Equal("damaged settings", File.ReadAllText(path));
        restarted.ArchiveRecovery(Assert.Single(restarted.RecoveryIssues).Id);
        Assert.True(restarted.Healthy); Assert.True(restarted.Preferences.Paused); Assert.False(restarted.Preferences.StartAtSignIn);
        Assert.Equal(1, restarted.PreservedRecords);
        Assert.Equal("damaged settings", File.ReadAllText(Directory.GetFiles(Path.Combine(profile, "availability", "recovery"), "preferences.json", SearchOption.AllDirectories).Single()));
    }
    [Fact]
    public void Durable_pause_survives_failed_preferences_commit_and_resume_requires_explicit_success()
    {
        var state = new DesktopAvailability(profile); state.SetOptions(true, false, true); state.Resume();
        Assert.True(state.AutomaticAllowed);
        var blocker = Path.Combine(profile, "availability", "preferences.json.new"); Directory.CreateDirectory(blocker);
        Assert.True(Record.Exception(state.Pause) is IOException or UnauthorizedAccessException);
        Assert.False(state.AutomaticAllowed); Assert.True(state.Preferences.Paused);
        Assert.True(new DesktopAvailability(profile).Preferences.Paused);
        Directory.Delete(blocker);
        var restarted = new DesktopAvailability(profile); Assert.False(restarted.AutomaticAllowed);
        restarted.Resume(); Assert.True(restarted.AutomaticAllowed);
        Assert.True(new DesktopAvailability(profile).AutomaticAllowed);
    }
    [Fact]
    public void Unremovable_pause_marker_cannot_be_bypassed_by_saving_unpaused_preferences()
    {
        var state = new DesktopAvailability(profile); state.SetOptions(true, false, true);
        var marker = Path.Combine(profile, "availability", "pause.json.new"); Directory.CreateDirectory(marker);
        Assert.Throws<IOException>(state.Resume);
        Assert.True(state.Preferences.Paused); Assert.False(state.AutomaticAllowed);
        Assert.True(new DesktopAvailability(profile).Preferences.Paused);
        Directory.Delete(marker); state.Resume(); Assert.True(state.AutomaticAllowed);
    }
    private sealed class Clock : TimeProvider
    {
        private long now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => now;
        public void Advance(int seconds) => now += TimeSpan.FromSeconds(seconds).Ticks;
    }
    [Fact]
    public async Task Reconnect_backs_off_and_rechecks_pause_network_sleep_and_current_trust()
    {
        var state = new DesktopAvailability(profile); state.SetOptions(true, false, true);
        var clock = new Clock(); var calls = 0; var peerCalls = 0; var connected = false; var peerBusy = false; var trusted = new List<string>();
        var peer = new string('a', 64); state.RememberPeer(peer);
        var agent = new BackgroundReconnect(state, () => new(connected, false, false, peerBusy, true, trusted),
            () => { ++calls; throw new IOException("Fixture network unavailable"); }, id => { Assert.Equal(peer, id); ++peerCalls; peerBusy = true; return Task.CompletedTask; }, clock);
        await agent.TickAsync(); Assert.Equal(0, calls); // Default pause survives enabling options.
        state.Resume(); await agent.TickAsync(); await agent.TickAsync(); Assert.Equal(1, calls);
        clock.Advance(1); await agent.TickAsync(); Assert.Equal(2, calls);
        clock.Advance(1); await agent.TickAsync(); Assert.Equal(2, calls);
        clock.Advance(1); await agent.TickAsync(); Assert.Equal(3, calls);
        agent.NetworkAvailable = false; clock.Advance(100); await agent.TickAsync(); Assert.Equal(3, calls);
        agent.NetworkAvailable = true; agent.SystemAvailable = false; await agent.TickAsync(); Assert.Equal(3, calls);
        agent.SystemAvailable = true; connected = true; await agent.TickAsync(); Assert.Equal(0, peerCalls); // A saved ID alone is no authority.
        trusted.Add(peer); await agent.TickAsync(); Assert.Equal(1, peerCalls);
        clock.Advance(100); await agent.TickAsync(); Assert.Equal(1, peerCalls); // One active handshake.
        peerBusy = false; trusted.Clear(); await agent.TickAsync(); Assert.Equal(1, peerCalls);
        state.Pause(); trusted.Add(peer); await agent.TickAsync(); Assert.Equal(1, peerCalls);
    }
    [Fact]
    public async Task Pause_during_an_attempt_prevents_follow_on_attempts()
    {
        var state = new DesktopAvailability(profile); state.SetOptions(true, false, true); state.Resume();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        var agent = new BackgroundReconnect(state, () => new(false, false, false, false, true, []),
            () => { ++calls; return release.Task; }, _ => Task.CompletedTask);
        var running = agent.TickAsync(); await agent.TickAsync(); Assert.Equal(1, calls);
        state.Pause(); release.SetResult(); await running; await agent.TickAsync(); Assert.Equal(1, calls);
        Assert.False(new DesktopAvailability(profile).AutomaticAllowed);
    }
    [Fact]
    public async Task Signalling_context_refresh_waits_for_idle_and_preserves_owner_intent()
    {
        var state = new DesktopAvailability(profile); state.SetOptions(true, false, true); state.Resume();
        var authenticated = true; var connected = true; var refreshes = 0; var connects = 0; var clock = new Clock();
        var agent = new BackgroundReconnect(state, () => new(connected, false, authenticated, false, true, [], true),
            () => { ++connects; return Task.CompletedTask; }, _ => throw new InvalidOperationException("No peer was selected."), clock,
            () => { ++refreshes; connected = false; return Task.CompletedTask; });
        await agent.TickAsync(); Assert.Equal(0, refreshes); // Active authenticated sessions are not interrupted.
        authenticated = false; await agent.TickAsync(); Assert.Equal(1, refreshes); Assert.True(state.AutomaticAllowed);
        clock.Advance(1); await agent.TickAsync(); Assert.Equal(1, connects);
        state.Pause(); clock.Advance(100); await agent.TickAsync(); Assert.Equal(1, connects);
    }

    [Fact]
    public async Task Activation_pipe_is_user_owned_and_exposes_only_the_fixed_open_command()
    {
        var calls = 0; await using var activation = new DesktopActivation(profile);
        activation.Start(() => { ++calls; return Task.CompletedTask; });
        using (var identity = WindowsIdentity.GetCurrent())
        using (var client = new NamedPipeClientStream(".", activation.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await client.ConnectAsync(2000);
            var security = client.GetAccessControl(); Assert.True(security.AreAccessRulesProtected);
            Assert.Equal(identity.User, security.GetOwner(typeof(SecurityIdentifier)));
            var rule = Assert.Single(security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>());
            Assert.Equal(identity.User, rule.IdentityReference); Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            await client.WriteAsync("PATH?"u8.ToArray());
            Assert.Equal(0, await client.ReadAsync(new byte[1]));
        }
        Assert.Equal(0, calls); Assert.True(await DesktopActivation.TryOpenAsync(profile)); Assert.Equal(1, calls);
        for (var i = 0; i < 12; i++) Assert.True(await DesktopActivation.TryOpenAsync(profile));
        Assert.Equal(13, calls);
        Assert.True(Record.Exception(() => new DesktopActivation(profile)) is IOException or UnauthorizedAccessException);
        Assert.Equal(activation.PipeName, DesktopActivation.NameFor(profile.ToUpperInvariant() + Path.DirectorySeparatorChar));
    }
    [Fact]
    public async Task Stalled_activation_client_is_evicted_without_blocking_future_opens()
    {
        var calls = 0; await using var activation = new DesktopActivation(profile); activation.Start(() => { ++calls; return Task.CompletedTask; });
        using var stalled = new NamedPipeClientStream(".", activation.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await stalled.ConnectAsync(2000); await stalled.WriteAsync("O"u8.ToArray());
        Assert.True(await DesktopActivation.TryOpenAsync(profile)); Assert.Equal(1, calls);
    }
    [Fact]
    public void Sign_in_registration_is_quoted_profile_scoped_and_never_changes_an_unexpected_value()
    {
        var executable = Path.Combine(root, "app with spaces.exe"); File.WriteAllText(executable, "Not executable; registry-only fixture.");
        var startup = new WindowsStartupRegistration(profile, executable, registryPath);
        Assert.False(startup.Enabled); startup.SetEnabled(true); Assert.True(startup.Enabled);
        var arguments = ParseWindowsCommand(startup.Command);
        Assert.Equal(executable, arguments[0]);
        Assert.Equal(profile, DesktopLaunch.Parse(arguments.Skip(1).ToArray()).Profile);
        Assert.True(DesktopLaunch.Parse(arguments.Skip(1).ToArray()).Background);
        Assert.True(DesktopLaunch.Parse(arguments.Skip(1).ToArray()).SignIn);
        using (var key = Registry.CurrentUser.CreateSubKey(registryPath))
        {
            key.SetValue("unrelated", "kept"); key.SetValue(startup.ValueName, "unexpected command");
            Assert.Throws<IOException>(() => startup.SetEnabled(false));
            Assert.Equal("unexpected command", key.GetValue(startup.ValueName));
            key.SetValue(startup.ValueName, startup.Command, RegistryValueKind.ExpandString);
            Assert.False(startup.Enabled); Assert.Throws<IOException>(() => startup.SetEnabled(true));
            key.SetValue(startup.ValueName, startup.Command, RegistryValueKind.String);
        }
        startup.SetEnabled(false); Assert.False(startup.Enabled);
        using var remaining = Registry.CurrentUser.OpenSubKey(registryPath); Assert.Equal("kept", remaining!.GetValue("unrelated"));
        var tooLong = new WindowsStartupRegistration(Path.Combine(root, new string('x', 240)), executable, registryPath);
        Assert.Throws<InvalidDataException>(() => tooLong.SetEnabled(true));
        Assert.Equal(@"C:\", DesktopLaunch.Parse(ParseWindowsCommand(new WindowsStartupRegistration(@"C:\", executable, registryPath).Command).Skip(1).ToArray()).Profile);
    }
    private static string[] ParseWindowsCommand(string command)
    {
        var pointer = CommandLineToArgvW(command, out var count); Assert.NotEqual(IntPtr.Zero, pointer);
        try { return Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size))!).ToArray(); }
        finally { LocalFree(pointer); }
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(registryPath, false); // Newly allocated fixture branch, never the real Run key.
        Directory.Delete(root, true); // Newly allocated fixture root under repository .local.
    }
}
