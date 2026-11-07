using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using IConnect.Attachment.Worker;

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
        if (input.BaseStream.ReadByte() != -1)
            throw new InvalidDataException("Invalid local attachment binding.");
        using var binding = AttachmentPathBinding.Open(path, expected);
        var result = SaveAtPath(path, out var rejectedPath);
        if (rejectedPath && path.Length >= 260 && binding.TryShortParentPath() is { } alias)
            return SaveAtPath(alias, out _);
        return result;
    }
    [SupportedOSPlatform("windows")]
    private static int SaveAtPath(string path, out bool rejectedPath)
    {
        rejectedPath = false;
        var type = Type.GetTypeFromCLSID(new Guid("4125dd96-e03a-4103-8f70-e0597d803b9c"), true)!;
        var instance = (IAttachmentExecute)Activator.CreateInstance(type)!;
        try
        {
            var client = new Guid("dc94fdda-80ed-4fe4-b9e3-159032f15d74");
            var hr = instance.SetClientGuid(ref client); if (hr != 0) return hr;
            hr = instance.SetLocalPath(path);
            // A failed setter can leave the COM object with the rejected path.
            // A bounded alias retry uses a fresh instance and still requires Save.
            rejectedPath = hr == unchecked((int)0x80070057);
            if (hr != 0) return hr;
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
}
