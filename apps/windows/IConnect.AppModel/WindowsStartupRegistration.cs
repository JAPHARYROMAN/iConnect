using Microsoft.Win32;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace IConnect.AppModel;

/// <summary>One exact per-profile HKCU Run value; never overwrites or removes an unexpected command.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsStartupRegistration
{
    private readonly string registryPath;
    public string ValueName { get; }
    public string Command { get; }
    public WindowsStartupRegistration(string profile, string executable, string registryPath = @"Software\Microsoft\Windows\CurrentVersion\Run")
    {
        var state = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile)); var exe = Path.GetFullPath(executable);
        if (state.StartsWith(@"\\", StringComparison.Ordinal) || exe.StartsWith(@"\\", StringComparison.Ordinal) ||
            !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(exe) || (state + exe).Any(char.IsControl))
            throw new InvalidDataException("Sign-in startup requires an installed local executable and local profile.");
        this.registryPath = registryPath;
        ValueName = "iConnect." + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(state.ToUpperInvariant())))[..24];
        Command = Quote(exe) + " --background --sign-in --state-directory " + Quote(state);
    }
    public bool Enabled
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(registryPath); return key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string == Command && key.GetValueKind(ValueName) == RegistryValueKind.String; }
    }
    public void SetEnabled(bool enabled)
    {
        if (enabled && Command.Length > 260) throw new InvalidDataException("The sign-in command exceeds Windows' 260-character Run limit. Install iConnect and its profile at shorter paths.");
        using var key = Registry.CurrentUser.CreateSubKey(registryPath, true);
        var previous = key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (previous is not null && (previous is not string text || text != Command || key.GetValueKind(ValueName) != RegistryValueKind.String))
            throw new IOException("This profile's sign-in entry was changed outside iConnect. Review it in Windows Startup settings before replacing it.");
        if (enabled) key.SetValue(ValueName, Command, RegistryValueKind.String); else key.DeleteValue(ValueName, false);
        key.Flush();
        if (Enabled != enabled) throw new IOException("The sign-in preference could not be verified.");
    }
    private static string Quote(string value)
    {
        var result = new StringBuilder("\""); var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { ++slashes; continue; }
            if (character == '"') { result.Append('\\', slashes * 2 + 1).Append(character); slashes = 0; continue; }
            result.Append('\\', slashes).Append(character); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
}
