using IConnect.Core;
using IConnect.Storage.Windows;

namespace IConnect.AppModel;

public sealed record AvailabilityPreferences(bool RunInBackground = false, bool StartAtSignIn = false,
    bool AutoReconnect = false, bool Paused = true, string PeerId = "");

/// <summary>Owner preferences plus a separate durable pause barrier. A damaged profile never auto-connects.</summary>
public sealed class DesktopAvailability
{
    private readonly string path;
    private readonly string pausePath;
    private readonly StateRecovery recovery;
    private readonly object gate = new();
    private bool writeFailed;
    private AvailabilityPreferences preferences = new();
    private string error = "";
    public AvailabilityPreferences Preferences { get { lock (gate) return preferences; } private set => preferences = value; }
    public string Error { get { lock (gate) return error; } private set => error = value; }
    public bool Healthy { get { lock (gate) return !recovery.Needed && !writeFailed; } }
    public bool AutomaticAllowed { get { lock (gate) return Healthy && preferences.AutoReconnect && !preferences.Paused && !PauseExists; } }
    public IReadOnlyList<StateRecoveryIssue> RecoveryIssues { get { lock (gate) return recovery.Issues; } }
    public int PreservedRecords { get { lock (gate) return recovery.ArchivedCount; } }
    public void ClearPreservedRecords() { lock (gate) recovery.ClearArchives(); }
    private bool PauseExists => new[] { pausePath, pausePath + ".new", pausePath + ".bak" }.Any(x => File.Exists(x) || Directory.Exists(x));
    public DesktopAvailability(string profile)
    {
        var directory = Path.Combine(profile, "availability"); Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "preferences.json"); pausePath = Path.Combine(directory, "pause.json"); recovery = new(directory);
        try
        {
            if (!File.Exists(path) && (File.Exists(path + ".new") || File.Exists(path + ".bak"))) throw new InvalidDataException("Availability settings were not committed.");
            Preferences = File.Exists(path) ? StateRecovery.Read<AvailabilityPreferences>(path, 16 * 1024) : new();
            Validate(Preferences);
            if (PauseExists) Preferences = Preferences with { Paused = true };
        }
        catch (Exception ex) when (StateRecovery.IsRecordFailure(ex))
        {
            Preferences = new(); Error = "Availability settings need review. Automatic connections and background mode are disabled.";
            recovery.Add(path, Error);
        }
    }
    public static void Validate(AvailabilityPreferences value)
    {
        if (value.StartAtSignIn && !value.RunInBackground || value.PeerId is null ||
            value.PeerId.Length != 0 && (value.PeerId.Length != 64 || value.PeerId.Any(c => !char.IsAsciiHexDigitLower(c))))
            throw new InvalidDataException("Choose valid background options and a paired device.");
    }
    private void Save(AvailabilityPreferences value)
    {
        recovery.RequireHealthy(); Validate(value);
        try { LocalState.Write(path, value); Preferences = value; writeFailed = false; Error = ""; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { writeFailed = true; Error = "Availability could not be saved. Automatic reconnect is blocked in this run. Review these settings before restarting."; throw; }
    }
    public void SetOptions(bool background, bool startup, bool reconnect)
    { lock (gate) Save(Preferences with { RunInBackground = background, StartAtSignIn = startup, AutoReconnect = reconnect, Paused = Preferences.Paused || PauseExists }); }
    public void Pause()
    {
        lock (gate)
        {
            Preferences = Preferences with { Paused = true }; // Immediate in-memory barrier, even when disk writes fail.
            try { LocalState.Write(pausePath, new { Paused = true }); Save(Preferences); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { writeFailed = true; Error = "Access is paused now, but its saved state could not be committed. Review availability before restarting."; throw; }
        }
    }
    public void Resume()
    {
        lock (gate)
        {
            Save(Preferences with { Paused = false });
            try
            {
                foreach (var marker in new[] { pausePath, pausePath + ".new", pausePath + ".bak" })
                {
                    if (Directory.Exists(marker) || File.Exists(marker) && (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("The saved pause marker needs local storage review.");
                    File.Delete(marker);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Preferences = Preferences with { Paused = true }; writeFailed = true; Error = "The saved pause could not be cleared. Automatic access remains blocked."; throw; }
        }
    }
    public void RememberPeer(string id) { lock (gate) Save(Preferences with { PeerId = id }); }
    public void ForgetPeer(string id) { lock (gate) if (Preferences.PeerId == id) Save(Preferences with { PeerId = "" }); }
    public void ArchiveRecovery(string id)
    {
        lock (gate)
        {
            if (!recovery.Issues.Any(x => x.Id == id)) return;
            recovery.Archive(id); Preferences = new(); Save(Preferences); // Reset all optional authority, including startup consent.
        }
    }
}

public sealed record AvailabilitySnapshot(bool Connected, bool Connecting, bool PeerAuthenticated, bool PeerConnecting,
    bool Configured, IReadOnlyCollection<string> TrustedPeers, bool RefreshSignalling = false);

/// <summary>One bounded reconnect attempt per tick; every attempt rechecks current owner intent and trust.</summary>
public sealed class BackgroundReconnect(DesktopAvailability availability, Func<AvailabilitySnapshot> snapshot,
    Func<Task> connect, Func<string, Task> connectPeer, TimeProvider? clock = null, Func<Task>? refreshSignalling = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private bool busy;
    private int failures;
    private long lastAttempt;
    private TimeSpan delay;
    public bool SystemAvailable { get; set; } = true;
    public bool NetworkAvailable { get; set; } = true;
    public string Notice { get; private set; } = "";
    public void Reset() { failures = 0; delay = TimeSpan.Zero; lastAttempt = 0; Notice = ""; }
    public async Task TickAsync()
    {
        if (busy) return;
        if (!availability.AutomaticAllowed || !SystemAvailable || !NetworkAvailable) { Reset(); return; }
        var state = snapshot();
        if (!state.Configured || state.Connecting || state.PeerConnecting) return;
        var peer = availability.Preferences.PeerId;
        var rotate = state.Connected && state.RefreshSignalling && refreshSignalling is not null;
        if (state.Connected && (state.PeerAuthenticated || !rotate && (peer.Length == 0 || !state.TrustedPeers.Contains(peer)))) { Reset(); return; }
        if (delay != TimeSpan.Zero && time.GetElapsedTime(lastAttempt) < delay) return;
        busy = true;
        try
        {
            lastAttempt = time.GetTimestamp();
            delay = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(6, failures++))));
            Notice = state.Connected ? "Trying the last selected paired device…" : "Reconnecting to the coordinator…";
            if (rotate) await refreshSignalling!();
            else if (!state.Connected) await connect(); else await connectPeer(peer);
        }
        catch (Exception)
        { Notice = "Connection unavailable. Automatic retry uses a bounded delay; Pause stops retries."; }
        finally { busy = false; }
    }
}
