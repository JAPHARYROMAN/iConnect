using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

// This broker calls Windows attachment services at ordinary-user privilege.
// It never launches the received file or displays a trust/policy prompt.
internal static class Program
{
    private const uint Magic = 0x31414349; // ICA1
    [STAThread]
    private static int Main()
    {
        if (!OperatingSystem.IsWindows()) return 1;
        var result = unchecked((int)0x80004005);
        try { result = Save(); }
        catch (Exception e) when (e is IOException or ArgumentException or COMException or InvalidOperationException)
        { result = e is COMException com ? com.HResult : unchecked((int)0x80004005); }
        using var output = new BinaryWriter(Console.OpenStandardOutput()); output.Write(Magic); output.Write(result); output.Flush();
        return 0;
    }
    [SupportedOSPlatform("windows")]
    private static int Save()
    {
        using var input = new BinaryReader(Console.OpenStandardInput(), new UTF8Encoding(false, true));
        if (input.ReadUInt32() != Magic) throw new InvalidDataException("Invalid request.");
        string Read(int maximum)
        {
            var length = input.ReadInt32(); if (length is < 1 || length > maximum) throw new InvalidDataException("Invalid request length.");
            var bytes = input.ReadBytes(length); if (bytes.Length != length) throw new EndOfStreamException();
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        var path = Read(32_768); var expected = Read(64);
        if (path.Length > 8448 || path.Any(char.IsControl) || path.Contains('/') || !path.StartsWith("\\\\?\\Volume{", StringComparison.OrdinalIgnoreCase) ||
            expected.Length != 25 || expected[8] != ':' || expected.Where((_, i) => i != 8).Any(c => !char.IsAsciiHexDigitLower(c)) || input.BaseStream.ReadByte() != -1)
            throw new InvalidDataException("Invalid local attachment binding.");
        using var guard = CreateFileW(path, 1 | 0x80 | 0x100000, 3, 0, 3, 0x00200000, 0);
        if (guard.IsInvalid || !GetFileInformationByHandle(guard, out var info) || (info.Attributes & 0x410) != 0 || info.Links != 1 ||
            $"{info.Volume:x8}:{info.IndexHigh:x8}{info.IndexLow:x8}" != expected) throw new IOException("The attachment identity changed.");
        var type = Type.GetTypeFromCLSID(new Guid("4125dd96-e03a-4103-8f70-e0597d803b9c"), true)!;
        var instance = (IAttachmentExecute)Activator.CreateInstance(type)!;
        try
        {
            var client = new Guid("dc94fdda-80ed-4fe4-b9e3-159032f15d74");
            var hr = instance.SetClientGuid(ref client); if (hr != 0) return hr;
            hr = instance.SetLocalPath(path); if (hr != 0) return hr;
            // A paired identity is not a verifiable web origin. Leaving Source and
            // Referrer unset retains the documented default Restricted Zone.
            return instance.Save();
        }
        finally { Marshal.FinalReleaseComObject(instance); }
    }
    [ComImport, Guid("73db1241-1e85-4581-8e4f-a81e1d0f8c57"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAttachmentExecute
    {
        [PreserveSig] int SetClientTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        [PreserveSig] int SetClientGuid(ref Guid client);
        [PreserveSig] int SetLocalPath([MarshalAs(UnmanagedType.LPWStr)] string path);
        [PreserveSig] int SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int SetSource([MarshalAs(UnmanagedType.LPWStr)] string source);
        [PreserveSig] int SetReferrer([MarshalAs(UnmanagedType.LPWStr)] string referrer);
        [PreserveSig] int CheckPolicy();
        [PreserveSig] int Prompt(nint window, uint prompt, out uint action);
        [PreserveSig] int Save();
        [PreserveSig] int Execute(nint window, [MarshalAs(UnmanagedType.LPWStr)] string? verb, out nint process);
        [PreserveSig] int SaveWithUI(nint window);
        [PreserveSig] int ClearClientState();
    }
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
}
