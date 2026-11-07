using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace IConnect.Preview.Windows;

internal static class SandboxNative
{
    internal const uint JobKillOnClose=0x2000, JobProcessMemory=0x100, JobMemory=0x200, JobActiveProcesses=0x8, JobProcessTime=0x2, JobDieOnException=0x400;
    internal const uint CreateSuspended=0x4, CreateNoWindow=0x8000000, ExtendedStartup=0x80000, UnicodeEnvironment=0x400, UseStandardHandles=0x100;
    internal const int AttributeCapabilities=0x20009, AttributeHandleList=0x20002, TokenIsAppContainer=29, TokenQuery=8, JobExtendedLimits=9, JobUiRestrictions=4;
    [StructLayout(LayoutKind.Sequential)] internal struct SecurityCapabilities { public nint Sid, Capabilities; public uint Count, Reserved; }
    [StructLayout(LayoutKind.Sequential)] internal struct SidAttributes { public nint Sid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] internal struct StartupInfo
    {
        public uint Size; public nint Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
        public ushort Show, ReservedLength; public nint ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct StartupInfoEx { public StartupInfo Startup; public nint Attributes; }
    [StructLayout(LayoutKind.Sequential)] internal struct ProcessInfo { public nint Process, Thread; public uint Pid, Tid; }
    [StructLayout(LayoutKind.Sequential)] internal struct BasicLimits
    {
        public long ProcessTime, JobTime; public uint Flags; public nuint MinWorkingSet, MaxWorkingSet;
        public uint ActiveProcesses; public nuint Affinity; public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] internal struct ExtendedLimits
    {
        public BasicLimits Basic; public IoCounters Io; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] internal static extern int CreateAppContainerProfile(string name, string display, string description, nint capabilities, uint count, out nint sid);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] internal static extern int DeriveAppContainerSidFromAppContainerName(string name, out nint sid);
    [DllImport("advapi32.dll")] internal static extern nint FreeSid(nint sid);
    [DllImport("FirewallAPI.dll")] internal static extern uint NetworkIsolationGetAppContainerConfig(out uint count, out nint entries);
    [DllImport("kernel32.dll")] internal static extern nint GetProcessHeap();
    [DllImport("kernel32.dll")] internal static extern bool HeapFree(nint heap, uint flags, nint memory);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool InitializeProcThreadAttributeList(nint attributes, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool UpdateProcThreadAttribute(nint attributes, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returned);
    [DllImport("kernel32.dll")] internal static extern void DeleteProcThreadAttributeList(nint attributes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool CreateProcessW(string application, StringBuilder command, nint processSecurity, nint threadSecurity,
        bool inheritHandles, uint flags, nint environment, string directory, ref StartupInfoEx startup, out ProcessInfo process);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern SafeFileHandle CreateJobObjectW(nint security, nint name);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimits limits, uint size);
    [DllImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)] internal static extern bool SetJobUiLimits(SafeFileHandle job, int kind, ref uint limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool AssignProcessToJobObject(SafeFileHandle job, nint process);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool TerminateJobObject(SafeFileHandle job, uint code);
    [DllImport("kernel32.dll")] internal static extern bool TerminateProcess(nint process, uint code);
    [DllImport("kernel32.dll")] internal static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll")] internal static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll")] internal static extern bool GetExitCodeProcess(nint process, out uint code);
    [DllImport("kernel32.dll")] internal static extern bool CloseHandle(nint handle);
    [DllImport("advapi32.dll", SetLastError = true)] internal static extern bool OpenProcessToken(nint process, uint access, out SafeFileHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] internal static extern bool GetTokenInformation(SafeFileHandle token, int kind, out int value, int size, out int returned);
}
