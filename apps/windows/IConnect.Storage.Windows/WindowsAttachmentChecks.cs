using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace IConnect.Storage.Windows;

internal interface IAttachmentChecks
{
    Task CheckAsync(string path, string identity, CancellationToken cancellation);
}

/// <summary>Bounded ordinary-user broker for Windows Attachment Services. It does not execute files.</summary>
internal sealed class WindowsAttachmentChecks(string? executable = null, TimeSpan? deadline = null, Action<int>? started = null) : IAttachmentChecks
{
    private static readonly SemaphoreSlim Workers = new(2);
    private const uint Magic = 0x31414349;
    public async Task CheckAsync(string path, string identity, CancellationToken cancellation)
    {
        await Workers.WaitAsync(cancellation);
        try { await ExchangeAsync(path, identity, cancellation); }
        finally { Workers.Release(); }
    }
    private async Task ExchangeAsync(string path, string identity, CancellationToken cancellation)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var worker = Path.GetFullPath(executable ?? Path.Combine(AppContext.BaseDirectory, "AttachmentWorker", "IConnect.Attachment.Worker.exe"));
        if (Path.GetFileName(Path.GetDirectoryName(worker)) != "AttachmentWorker" || !File.Exists(worker))
            throw new IOException("The Windows attachment-check helper is missing. Repair the installation before completing this copy.");
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timer.CancelAfter(deadline ?? TimeSpan.FromMinutes(2));
        using var job = Native.CreateJobObjectW(0, 0);
        if (job.IsInvalid) throw new IOException("The Windows attachment-check job could not be created.", new Win32Exception(Marshal.GetLastWin32Error()));
        var limits = new Native.ExtendedLimits { Basic = new() { Flags = 0x2000 | 0x100 | 0x200 | 0x8 | 0x400, ActiveProcesses = 1 },
            ProcessMemory = 512u * 1024 * 1024, JobMemory = 512u * 1024 * 1024 };
        if (!Native.SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<Native.ExtendedLimits>()))
            throw new IOException("The Windows attachment-check limits could not be applied.", new Win32Exception(Marshal.GetLastWin32Error()));
        using var input = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        using var output = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        nint attributes = 0, handles = 0, environment = 0; var attributesReady = false; Native.ProcessInfo process = default;
        try
        {
            nuint size = 0; Native.InitializeProcThreadAttributeList(0, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(checked((int)size));
            if (!Native.InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw StartError(); attributesReady = true;
            handles = Marshal.AllocHGlobal(2 * nint.Size);
            Marshal.WriteIntPtr(handles, input.ClientSafePipeHandle.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, nint.Size, output.ClientSafePipeHandle.DangerousGetHandle());
            if (!Native.UpdateProcThreadAttribute(attributes, 0, 0x20002, handles, (nuint)(2 * nint.Size), 0, 0)) throw StartError();
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            environment = Marshal.StringToHGlobalUni($"DOTNET_EnableDiagnostics=0\0LOCALAPPDATA={Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}\0PATH={windows}\\System32\0SystemRoot={windows}\0TEMP={Path.GetTempPath()}\0TMP={Path.GetTempPath()}\0USERPROFILE={Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}\0WINDIR={windows}\0\0");
            var startup = new Native.StartupInfoEx { Startup = new() { Size = (uint)Marshal.SizeOf<Native.StartupInfoEx>(), Flags = 0x100,
                Input = input.ClientSafePipeHandle.DangerousGetHandle(), Output = output.ClientSafePipeHandle.DangerousGetHandle(), Error = output.ClientSafePipeHandle.DangerousGetHandle() }, Attributes = attributes };
            if (!Native.CreateProcessW(worker, new StringBuilder('"' + worker + '"'), 0, 0, true, 0x80000 | 0x8000000 | 0x4 | 0x400, environment, Path.GetDirectoryName(worker)!, ref startup, out process)) throw StartError();
            // No worker instruction runs before the job and pipe-only handle list apply.
            if (!Native.AssignProcessToJobObject(job, process.Process))
                throw new IOException("The Windows attachment-check helper could not be bounded.", new Win32Exception(Marshal.GetLastWin32Error()));
            input.DisposeLocalCopyOfClientHandle(); output.DisposeLocalCopyOfClientHandle();
            using var terminate = timer.Token.Register(() => Native.TerminateJobObject(job, 1));
            if (Native.ResumeThread(process.Thread) == uint.MaxValue) throw StartError();
            started?.Invoke((int)process.Pid);
            using var request = new MemoryStream();
            using (var writer = new BinaryWriter(request, Encoding.UTF8, true))
            {
                writer.Write(Magic);
                foreach (var value in new[] { path, identity }) { var bytes = Encoding.UTF8.GetBytes(value); if (bytes.Length is < 1 or > 32_768) throw new IOException("Attachment binding is too long."); writer.Write(bytes.Length); writer.Write(bytes); }
            }
            await input.WriteAsync(request.ToArray(), timer.Token); input.Dispose();
            var reply = new byte[8]; await output.ReadExactlyAsync(reply, timer.Token);
            if (await output.ReadAsync(new byte[1], timer.Token) != 0) throw new IOException("The Windows attachment-check response exceeded its bound.");
            if (Native.WaitForSingleObject(process.Process, 1000) != 0 || !Native.GetExitCodeProcess(process.Process, out var exit) || exit != 0 || BitConverter.ToUInt32(reply) != Magic)
                throw new IOException("Windows attachment checks did not return a valid result.");
            var result = BitConverter.ToInt32(reply, 4);
            if (result != 0) throw new AttachmentCheckException(result);
            cancellation.ThrowIfCancellationRequested();
        }
        catch (Exception e) when (timer.IsCancellationRequested)
        {
            cancellation.ThrowIfCancellationRequested();
            throw new IOException("Windows attachment checks exceeded their time limit. The file remains untrusted; review and retry explicitly.", e);
        }
        finally
        {
            Native.TerminateJobObject(job, 1);
            if (process.Process != 0) { Native.TerminateProcess(process.Process, 1); Native.WaitForSingleObject(process.Process, 2000); Native.CloseHandle(process.Process); }
            if (process.Thread != 0) Native.CloseHandle(process.Thread);
            if (attributesReady) Native.DeleteProcThreadAttributeList(attributes);
            if (attributes != 0) Marshal.FreeHGlobal(attributes);
            if (handles != 0) Marshal.FreeHGlobal(handles);
            if (environment != 0) Marshal.FreeHGlobal(environment);
        }
    }
    private static IOException StartError() => new("The Windows attachment-check helper could not start safely. Repair the installation.", new Win32Exception(Marshal.GetLastWin32Error()));
    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct StartupInfo
        {
            public uint Size; public nint Reserved, Desktop, Title; public uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
            public ushort Show, ReservedLength; public nint ReservedBytes, Input, Output, Error;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct StartupInfoEx { public StartupInfo Startup; public nint Attributes; }
        [StructLayout(LayoutKind.Sequential)] internal struct ProcessInfo { public nint Process, Thread; public uint Pid, Tid; }
        [StructLayout(LayoutKind.Sequential)] internal struct BasicLimits
        { public long ProcessTime, JobTime; public uint Flags; public nuint MinWorkingSet, MaxWorkingSet; public uint ActiveProcesses; public nuint Affinity; public uint Priority, Scheduling; }
        [StructLayout(LayoutKind.Sequential)] internal struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)] internal struct ExtendedLimits
        { public BasicLimits Basic; public IoCounters Io; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern SafeFileHandle CreateJobObjectW(nint security, nint name);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimits limits, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool AssignProcessToJobObject(SafeFileHandle job, nint process);
        [DllImport("kernel32.dll")] internal static extern bool TerminateJobObject(SafeFileHandle job, uint code);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool InitializeProcThreadAttributeList(nint attributes, int count, uint flags, ref nuint size);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool UpdateProcThreadAttribute(nint attributes, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returned);
        [DllImport("kernel32.dll")] internal static extern void DeleteProcThreadAttributeList(nint attributes);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool CreateProcessW(string application, StringBuilder command, nint processSecurity, nint threadSecurity,
            bool inheritHandles, uint flags, nint environment, string directory, ref StartupInfoEx startup, out ProcessInfo process);
        [DllImport("kernel32.dll")] internal static extern bool TerminateProcess(nint process, uint code);
        [DllImport("kernel32.dll")] internal static extern uint ResumeThread(nint thread);
        [DllImport("kernel32.dll")] internal static extern uint WaitForSingleObject(nint handle, uint milliseconds);
        [DllImport("kernel32.dll")] internal static extern bool GetExitCodeProcess(nint process, out uint code);
        [DllImport("kernel32.dll")] internal static extern bool CloseHandle(nint handle);
    }
}

internal sealed class AttachmentCheckException(int result) : IOException($"Windows attachment checks did not approve this save (0x{unchecked((uint)result):X8}). The file remains untrusted; no completion receipt was issued.")
{
    public int Result { get; } = result;
}

internal static class ReceivedFileSecurity
{
    internal const int PolicyVersion = 1;
    internal static async Task VerifyPublishedAsync(SecureWindowsDirectory root, string name, string identity, long size, string hash,
        IAttachmentChecks checks, Action requireCurrent, CancellationToken cancellation, Action? rereading = null, Action<long>? progress = null)
    {
        cancellation.ThrowIfCancellationRequested(); requireCurrent();
        using (var anchor = root.OpenAttachmentAnchor(name))
        {
            Require(anchor, identity, size); anchor.MarkUntrusted(); requireCurrent();
            await checks.CheckAsync(anchor.Info.CanonicalPath, identity, cancellation);
        }
        cancellation.ThrowIfCancellationRequested(); requireCurrent();
        // Exclusive data-read sharing rejects a writer left behind by a handler.
        using var file = root.OpenChild(name); Require(file, identity, size); file.MarkUntrusted();
        rereading?.Invoke();
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var bytes = new byte[32 * 1024]; var offset = 0L;
        while (offset < size)
        {
            cancellation.ThrowIfCancellationRequested(); requireCurrent(); var count = (int)Math.Min(bytes.Length, size - offset); var read = 0;
            while (read < count) { var n = RandomAccess.Read(file.Handle, bytes.AsSpan(read, count - read), offset + read); if (n <= 0) throw new IOException("The checked file became shorter."); read += n; }
            digest.AppendData(bytes, 0, count); offset += count; progress?.Invoke(offset);
        }
        if (!CryptographicOperations.FixedTimeEquals(digest.GetHashAndReset(), Convert.FromHexString(hash)))
            throw new InvalidDataException("The file changed during Windows attachment checks. It was kept untrusted and was not marked complete.");
        requireCurrent(); cancellation.ThrowIfCancellationRequested();
    }
    private static void Require(SecureWindowsDirectory file, string identity, long size)
    { if (file.Info.IsDirectory || file.Info.Identity != identity || file.Info.Links != 1 || file.Info.Size != size) throw new IOException("The received file changed before security checks finished."); }
}
