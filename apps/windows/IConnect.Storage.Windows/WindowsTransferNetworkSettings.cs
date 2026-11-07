using IConnect.Core;
using System.Text.Json;

namespace IConnect.Storage.Windows;

/// <summary>Owner settings; damaged preferences pause remote content without blocking local browsing.</summary>
public sealed class WindowsTransferNetworkSettings : IDisposable
{
    private sealed record Saved(int Schema, TransferNetworkPreferences Preferences);
    private readonly object gate = new();
    private readonly SecureWindowsDirectory profile, root, lease;
    private StateRecovery recovery;
    private readonly string path;
    private string version = "";
    private bool disposed;
    public FileTrafficPolicy Policy { get; } = new();
    public TransferNetworkPreferences Preferences { get; private set; } = new();
    public string Issue { get; private set; } = "";
    public bool Missing { get; private set; }
    public bool Healthy => Issue.Length == 0;
    public bool CanReset { get { lock (gate) return Missing || recovery.Needed && recovery.Issues.All(x => x.CanArchive); } }
    public int PreservedRecords { get { lock (gate) return recovery.ArchivedCount; } }
    public event Action? Changed;
    public WindowsTransferNetworkSettings(string directory)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory); var drive = Path.GetPathRoot(directory)!;
        if (drive.Length != 3 || !char.IsAsciiLetter(drive[0])) throw new IOException("Choose a normal local profile directory.");
        using var volume = SecureWindowsDirectory.OpenRoot(drive);
        profile = SecureWindowsDirectory.OpenPrivatePath(volume.Info.CanonicalPath, volume.Info.Identity, directory[drive.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries));
        try
        {
            try { root = profile.CreateDirectory("transfer-network"); }
            catch (StorageException ex) when (ex.NativeError is 80 or 183) { root = profile.OpenChild("transfer-network", true); }
            try
            {
                var firstOwner = true;
                try { lease = root.CreateFile("owner.lock"); }
                catch (StorageException ex) when (ex.NativeError is 80 or 183) { firstOwner = false; lease = root.OpenWritableMetadataChild("owner.lock"); }
                path = Path.Combine(root.Info.CanonicalPath, "settings.json"); recovery = new(root.Info.CanonicalPath);
                try
                {
                    if (File.Exists(path + ".new") || File.Exists(path + ".bak")) throw new IOException("Uncommitted transfer network settings need review.");
                    if (File.Exists(path))
                    {
                        var saved = StateRecovery.Read<Saved>(path, 32 * 1024); if (saved.Schema != 1 || saved.Preferences is null) throw new InvalidDataException("Invalid transfer network settings.");
                        TransferNetworkPreferences.Validate(saved.Preferences); using var file = root.OpenChild("settings.json"); version = file.Info.Version; Preferences = saved.Preferences;
                    }
                    else if (firstOwner) Save(new());
                    else { Missing = true; throw new IOException("Previously saved transfer network settings are missing."); }
                    Policy.Configure(Preferences);
                }
                catch (Exception ex) when (StateRecovery.IsRecordFailure(ex)) { Block(); }
            }
            catch { lease?.Dispose(); root.Dispose(); throw; }
        }
        catch { profile.Dispose(); throw; }
    }
    private void Block()
    {
        Issue = "Transfer network settings could not be validated or saved. Remote file content is paused; local files, browsing and transfer receipts were kept. Preserve and reset these settings to continue.";
        if (!recovery.Needed) recovery.Add(path, Issue); Policy.Configure(Preferences, healthy: false);
    }
    private void Save(TransferNetworkPreferences value)
    {
        try
        {
            if (Issue.Length != 0) throw new IOException(Issue);
            if (File.Exists(path + ".new") || File.Exists(path + ".bak")) throw new IOException("Transfer network settings changed outside this owner.");
            if (version.Length != 0) { using var current = root.OpenChild("settings.json"); if (current.Info.Version != version) throw new IOException("Transfer network settings changed outside this owner."); }
            else if (File.Exists(path)) throw new IOException("Unreviewed transfer network settings were kept.");
            using (var output = root.CreateFile("settings.json.new")) { var bytes = JsonSerializer.SerializeToUtf8Bytes(new Saved(1, value)); output.Write(0, bytes); output.Flush(); }
            AtomicStateFile.Commit(path + ".new", path); using var saved = root.OpenChild("settings.json"); version = saved.Info.Version;
            Preferences = value; Policy.Configure(value);
        }
        catch (Exception ex) when (StateRecovery.IsRecordFailure(ex)) { Block(); throw new IOException(Issue, ex); }
    }
    public void Set(TransferNetworkPreferences value)
    {
        TransferNetworkPreferences.Validate(value); lock (gate) { ObjectDisposedException.ThrowIf(disposed, this); Save(value); Notify(); }
    }
    public void PreserveAndReset()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this); if (!CanReset) throw new IOException("Restore access to the damaged settings and restart before resetting them.");
            if (Missing)
            {
                if (File.Exists(path) || File.Exists(path + ".new") || File.Exists(path + ".bak")) throw new IOException("Transfer network settings reappeared. Restart and review them before resetting.");
                recovery = new(root.Info.CanonicalPath); Missing = false;
            }
            else foreach (var issue in recovery.Issues.ToArray()) recovery.Archive(issue.Id);
            version = ""; Issue = ""; Save(new()); Notify();
        }
    }
    public void ClearPreserved()
    { lock (gate) { ObjectDisposedException.ThrowIf(disposed, this); recovery.ClearArchives(); Notify(); } }
    private void Notify() { if (Changed is not { } listeners) return; foreach (Action listener in listeners.GetInvocationList()) try { listener(); } catch (Exception) { } }
    public void Dispose()
    { lock (gate) { if (disposed) return; disposed = true; Policy.Configure(Preferences, healthy: false); lease.Dispose(); root.Dispose(); profile.Dispose(); } }
}
