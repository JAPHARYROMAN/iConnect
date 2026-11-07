using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using IConnect.Core;
using static IConnect.Preview.Windows.SandboxNative;

namespace IConnect.Preview.Windows;

/// <summary>One capability-free AppContainer, two inherited pipe ends, and a kill-on-close job per render.</summary>
internal sealed class PreviewSandbox(string executable, TimeSpan? deadline = null, Action<int>? started = null)
{
    // This semaphore also bounds concurrent input/output/native heaps across local and remote viewers.
    private static readonly SemaphoreSlim Gate = new(1);
    internal async Task<byte[]> ExchangeAsync(byte[] request, int maxResponse, CancellationToken cancellation)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.Length, PreviewRules.MaxBytes + 32);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxResponse, PreviewRules.RenderSide * PreviewRules.RenderSide * 4 + 64);
        await Gate.WaitAsync(cancellation);
        try { return await Task.Run(() => Exchange(request, maxResponse, cancellation), CancellationToken.None); }
        finally { Gate.Release(); }
    }
    private byte[] Exchange(byte[] request, int maxResponse, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var path = Path.GetFullPath(executable); var folder = Path.GetDirectoryName(path)!;
        if (Path.GetFileName(folder) != "PreviewWorker" || !File.Exists(path)) throw new IOException("The preview worker is missing. Repair the installation.");
        var profile = "iConnect.Preview.v1." + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(WindowsIdentity.GetCurrent().User!.Value)))[..20];
        var result = CreateAppContainerProfile(profile, "iConnect preview", "Isolated image and PDF decoding", 0, 0, out var sid);
        if (result == unchecked((int)0x800700b7)) result = DeriveAppContainerSidFromAppContainerName(profile, out sid);
        Marshal.ThrowExceptionForHR(result);
        nint attributes = 0, capabilities = 0, handles = 0, environment = 0;
        ProcessInfo process = default; var attributesReady = false;
        using var input = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        using var output = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        using var job = CreateJobObjectW(0, 0);
        try
        {
            RequireNetworkIsolation(new SecurityIdentifier(sid));
            // Never grant access to a profile, source folder, or arbitrary path supplied by a peer.
            var tree = new DirectoryInfo(folder);
            if ((tree.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("The preview installation is redirected.");
            var count = 0;
            foreach (var item in tree.EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, MaxRecursionDepth = 8 }))
                if (++count > 4096 || (item.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("The preview installation is redirected or oversized.");
            var acl = tree.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            tree.SetAccessControl(acl);
            if (job.IsInvalid) Fail();
            var limits = new ExtendedLimits { Basic = new() { Flags = JobKillOnClose | JobProcessMemory | JobMemory | JobActiveProcesses | JobProcessTime | JobDieOnException, ActiveProcesses = 1, ProcessTime = TimeSpan.FromSeconds(10).Ticks },
                ProcessMemory = 384u * 1024 * 1024, JobMemory = 384u * 1024 * 1024 };
            if (!SetInformationJobObject(job, JobExtendedLimits, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())) Fail();
            // Clipboard, atoms, desktop/display/system changes and shutdown. USER handles remain available to WinRT COM.
            uint uiLimits=0xfe; if(!SetJobUiLimits(job,JobUiRestrictions,ref uiLimits,sizeof(uint))) Fail();
            nuint size = 0; InitializeProcThreadAttributeList(0, 2, 0, ref size);
            attributes = Marshal.AllocHGlobal(checked((int)size));
            if (!InitializeProcThreadAttributeList(attributes, 2, 0, ref size)) Fail(); attributesReady = true;
            capabilities = Marshal.AllocHGlobal(Marshal.SizeOf<SecurityCapabilities>());
            Marshal.StructureToPtr(new SecurityCapabilities { Sid = sid }, capabilities, false);
            if (!UpdateProcThreadAttribute(attributes, 0, AttributeCapabilities, capabilities, (nuint)Marshal.SizeOf<SecurityCapabilities>(), 0, 0)) Fail();
            handles = Marshal.AllocHGlobal(2 * nint.Size);
            Marshal.WriteIntPtr(handles, input.ClientSafePipeHandle.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, nint.Size, output.ClientSafePipeHandle.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(attributes, 0, AttributeHandleList, handles, (nuint)(2 * nint.Size), 0, 0)) Fail();
            // Only OS profile paths and runtime settings are allowed; never inherit application secrets or diagnostic settings.
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            environment = Marshal.StringToHGlobalUni($"DOTNET_EnableDiagnostics=0\0DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1\0LOCALAPPDATA={Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}\0PATH={windows}\\System32\0SystemRoot={windows}\0TEMP={folder}\0TMP={folder}\0USERPROFILE={Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}\0WINDIR={windows}\0\0");
            var startup = new StartupInfoEx { Startup = new() { Size = (uint)Marshal.SizeOf<StartupInfoEx>(), Flags = UseStandardHandles,
                Input = input.ClientSafePipeHandle.DangerousGetHandle(), Output = output.ClientSafePipeHandle.DangerousGetHandle(), Error = output.ClientSafePipeHandle.DangerousGetHandle() }, Attributes = attributes };
            if (!CreateProcessW(path, new StringBuilder('"' + path + '"'), 0, 0, true, ExtendedStartup | CreateNoWindow | CreateSuspended | UnicodeEnvironment, environment, folder, ref startup, out process)) Fail();
            // No instruction, including the CLR loader, executes before containment and token validation.
            if (!AssignProcessToJobObject(job, process.Process)) Fail();
            if (!OpenProcessToken(process.Process, TokenQuery, out var token)) Fail();
            using (token) if (!GetTokenInformation(token, TokenIsAppContainer, out var isolated, sizeof(int), out _) || isolated != 1) throw new IOException("The preview worker is not isolated.");
            input.DisposeLocalCopyOfClientHandle(); output.DisposeLocalCopyOfClientHandle();
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timer.CancelAfter(deadline ?? TimeSpan.FromSeconds(15));
            using var registration = timer.Token.Register(() => TerminateJobObject(job, 1));
            if (ResumeThread(process.Thread) == uint.MaxValue) Fail();
            started?.Invoke((int)process.Pid);
            // Separate tasks prevent an input/output pipe deadlock; termination closes both child ends.
            var write = Task.Run(() => { try { input.Write(request); } finally { input.Dispose(); } });
            byte[] response; using var bytes = new MemoryStream(); var buffer = new byte[32 * 1024];
            try
            {
                int read;
                while ((read = output.Read(buffer)) != 0)
                {
                    if (bytes.Length + read > maxResponse) throw new InvalidDataException("The preview worker exceeded its output limit.");
                    bytes.Write(buffer, 0, read);
                }
                response = bytes.ToArray();
                if (WaitForSingleObject(process.Process, 1000) != 0) throw new IOException("The preview worker did not exit.");
                if (!GetExitCodeProcess(process.Process, out var exit) || exit != 0) throw new InvalidDataException("The file could not be previewed within the renderer limits.", new Win32Exception(unchecked((int)exit)));
            }
            catch (Exception) when (timer.IsCancellationRequested)
            {
                cancellation.ThrowIfCancellationRequested(); throw new TimeoutException("The preview renderer exceeded its time limit.");
            }
            finally
            {
                TerminateJobObject(job, 1); try { write.GetAwaiter().GetResult(); } catch (IOException) { }
                CryptographicOperations.ZeroMemory(buffer); if(bytes.TryGetBuffer(out var raw)) CryptographicOperations.ZeroMemory(raw.AsSpan());
            }
            cancellation.ThrowIfCancellationRequested();
            if (timer.IsCancellationRequested) throw new TimeoutException("The preview renderer exceeded its time limit.");
            return response;
        }
        catch (Exception) when (cancellation.IsCancellationRequested) { throw new OperationCanceledException(cancellation); }
        finally
        {
            if (process.Process != 0) { TerminateProcess(process.Process, 1); WaitForSingleObject(process.Process, 2000); CloseHandle(process.Process); }
            if (process.Thread != 0) CloseHandle(process.Thread);
            if (attributesReady) DeleteProcThreadAttributeList(attributes);
            if (attributes != 0) Marshal.FreeHGlobal(attributes);
            if (capabilities != 0) Marshal.FreeHGlobal(capabilities);
            if (handles != 0) Marshal.FreeHGlobal(handles);
            if (environment != 0) Marshal.FreeHGlobal(environment);
            FreeSid(sid);
        }
    }
    private static void Fail([System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        var error = Marshal.GetLastWin32Error(); throw new Win32Exception(error, $"The isolated preview worker could not start (step {line}, Windows {error}).");
    }
    private static void RequireNetworkIsolation(SecurityIdentifier sid)
    {
        var status=NetworkIsolationGetAppContainerConfig(out var count,out var entries);
        try
        {
            if(status!=0) throw new IOException("Windows network isolation could not be verified. Previews are unavailable.",new Win32Exception((int)status));
            for(uint index=0;index<count;index++)
            {
                var item=Marshal.PtrToStructure<SidAttributes>(entries+checked((int)index*Marshal.SizeOf<SidAttributes>()));
                if(sid.Equals(new SecurityIdentifier(item.Sid))) throw new IOException("The preview sandbox has a Windows loopback exemption. Remove that exemption before using previews.");
            }
        }
        finally
        {
            // NetworkIsolationGetAppContainerConfig uses the process heap for the array and each SID.
            if(entries!=0)
            {
                for(uint index=0;index<count;index++) { var item=Marshal.PtrToStructure<SidAttributes>(entries+checked((int)index*Marshal.SizeOf<SidAttributes>())); HeapFree(GetProcessHeap(),0,item.Sid); }
                HeapFree(GetProcessHeap(),0,entries);
            }
        }
    }
}
