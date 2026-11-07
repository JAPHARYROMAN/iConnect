using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace IConnect.Attachment.Worker;

// Keep the entire volume-rooted name stable while a shell handler opens it.
// Shorten parents only: changing a file's extension could change its policy.
[SupportedOSPlatform("windows")]
internal sealed class AttachmentPathBinding : IDisposable
{
    private readonly List<SafeFileHandle> handles = [];
    private readonly string expected;
    public string CanonicalPath { get; }
    private AttachmentPathBinding(string path, string identity) { CanonicalPath = path; expected = identity; }

    public static AttachmentPathBinding Open(string path, string identity)
    {
        const string prefix = "\\\\?\\Volume{";
        const int rootLength = 49;
        if (path.Length is <= rootLength or > 8448 || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(path.AsSpan(prefix.Length, 36), "D", out _) || path[47] != '}' || path[48] != '\\' ||
            path[rootLength..].Split('\\').Any(x => x.Length == 0 || x is "." or ".." || x.Any(char.IsControl) || x.Contains(':') || x.Contains('/')) ||
            identity.Length != 25 || identity[8] != ':' || identity.Where((_, i) => i != 8).Any(c => !char.IsAsciiHexDigitLower(c)))
            throw new InvalidDataException("Invalid local attachment binding.");
        var binding = new AttachmentPathBinding(path, identity);
        try
        {
            // Directory READ_DATA and no DELETE sharing also prevent changing
            // an existing directory's short name (SetFileShortName requires DELETE).
            binding.Pin(path[..rootLength], directory: true);
            for (var index = path.IndexOf('\\', rootLength); index >= 0; index = path.IndexOf('\\', index + 1))
                binding.Pin(path[..index], directory: true);
            binding.Pin(path, directory: false);
            return binding;
        }
        catch { binding.Dispose(); throw; }
    }

    private void Pin(string path, bool directory)
    {
        // Parent writers must close first. Only the leaf admits scanner writes;
        // the caller reopens and rehashes that leaf after the handler finishes.
        var handle = CreateFileW(path, 1 | 0x80 | 0x100000, directory ? 1u : 3u, 0, 3, 0x00200000u | (directory ? 0x02000000u : 0), 0);
        try
        {
            if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info) || (info.Attributes & 0x400) != 0 ||
                ((info.Attributes & 0x10) != 0) != directory || !directory &&
                (info.Links != 1 || $"{info.Volume:x8}:{info.IndexHigh:x8}{info.IndexLow:x8}" != expected))
                throw new IOException("The attachment identity or parent directory changed.");
            handles.Add(handle);
        }
        catch { handle.Dispose(); throw; }
    }

    public string? TryShortParentPath()
    {
        var parent = CanonicalPath[..CanonicalPath.LastIndexOf('\\')];
        var buffer = new StringBuilder(8449);
        var count = GetShortPathNameW(parent, buffer, (uint)buffer.Capacity);
        if (count == 0 || count >= buffer.Capacity) return null;
        var alias = buffer.ToString() + CanonicalPath[CanonicalPath.LastIndexOf('\\')..];
        if (alias.Length >= 260 || alias.Length >= CanonicalPath.Length ||
            !alias.StartsWith(CanonicalPath[..49], StringComparison.OrdinalIgnoreCase)) return null;
        // Both names must open the original single-link object. All long-name
        // parents stay pinned until Save returns, so an alias cannot be reassigned.
        Pin(alias, directory: false);
        return alias;
    }

    public void Dispose() { for (var i = handles.Count - 1; i >= 0; i--) handles[i].Dispose(); handles.Clear(); }

    [StructLayout(LayoutKind.Sequential)] private struct FileTime { public uint Low, High; }
    [StructLayout(LayoutKind.Sequential)] private struct FileInfo
    {
        public uint Attributes; public FileTime Created, Accessed, Written;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInfo info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string path, StringBuilder buffer, uint count);
}
