using System.ComponentModel;
using System.Runtime.InteropServices;

namespace IConnect.Desktop;

internal enum TrayCommand { Open, ToggleAvailability, Quit, Suspended, Resumed, TrayUnavailable }

/// <summary>UI-thread notification-area control. No elevated helper or separate file-operation IPC.</summary>
internal sealed class TrayHost : IDisposable
{
    private const uint Callback = 0x8000 + 41;
    private readonly IntPtr window;
    private readonly Action<TrayCommand> command;
    private readonly Native.SubclassProc procedure;
    private readonly uint taskbarCreated;
    private bool enabled;
    private bool disposed;
    private bool canPause;
    private string tip = "iConnect · Paused";
    public bool Installed { get; private set; }
    public TrayHost(IntPtr window, Action<TrayCommand> command)
    {
        this.window = window; this.command = command; procedure = WindowProc;
        taskbarCreated = Native.RegisterWindowMessageW("TaskbarCreated");
        if (taskbarCreated == 0 || !Native.SetWindowSubclass(window, procedure, 41, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    private Native.NotifyIconData Data() => new()
    {
        Size = (uint)Marshal.SizeOf<Native.NotifyIconData>(), Window = window, Id = 41, Flags = 1 | 2 | 4 | 0x80,
        CallbackMessage = Callback, Icon = Native.LoadIconW(IntPtr.Zero, (IntPtr)32512), Tip = tip,
        Info = "", InfoTitle = ""
    };
    public void SetEnabled(bool value)
    {
        if (disposed || value == enabled) return;
        enabled = value;
        if (value) Add(); else { var data = Data(); Native.Shell_NotifyIconW(2, data); Installed = false; }
    }
    private void Add()
    {
        var data = Data(); Installed = Native.Shell_NotifyIconW(0, data);
        if (Installed) { data.Version = 4; if (!Native.Shell_NotifyIconW(4, data)) { Native.Shell_NotifyIconW(2, data); Installed = false; } }
        if (!Installed) command(TrayCommand.TrayUnavailable);
    }
    public void Update(string status, bool allowPause)
    {
        canPause = allowPause; var updated = "iConnect · " + status;
        if (updated == tip) return;
        tip = updated.Length <= 127 ? updated : updated[..127];
        if (Installed && !Native.Shell_NotifyIconW(1, Data())) { Installed = false; command(TrayCommand.TrayUnavailable); }
    }
    private IntPtr WindowProc(IntPtr handle, uint message, UIntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr reference)
    {
        try
        {
            if (message == taskbarCreated && enabled) { Installed = false; Add(); }
            else if (message == Callback && enabled)
            {
                var notification = (uint)((long)lParam & 0xffff);
                if (notification is 0x400 or 0x401 or 0x203) command(TrayCommand.Open);
                else if (notification is 0x7b or 0x205) ShowMenu();
                return IntPtr.Zero;
            }
            else if (message == 0x218)
            {
                if ((uint)wParam == 4) command(TrayCommand.Suspended);
                else if ((uint)wParam is 7 or 18) command(TrayCommand.Resumed);
            }
            else if (message == 0x16 && wParam != UIntPtr.Zero) command(TrayCommand.Suspended);
        }
        catch { command(TrayCommand.TrayUnavailable); } // An unmanaged callback must never unwind into the window procedure.
        return Native.DefSubclassProc(handle, message, wParam, lParam);
    }
    private void ShowMenu()
    {
        var menu = Native.CreatePopupMenu(); if (menu == IntPtr.Zero) return;
        try
        {
            Native.AppendMenuW(menu, 0, 1, "Open iConnect");
            Native.AppendMenuW(menu, 0, 2, canPause ? "Pause availability" : "Connect");
            Native.AppendMenuW(menu, 0x800, 0, null);
            Native.AppendMenuW(menu, 0, 3, "Quit iConnect");
            Native.GetCursorPos(out var point); Native.SetForegroundWindow(window);
            var selected = Native.TrackPopupMenuEx(menu, 0x100 | 0x80 | 0x2, point.X, point.Y, window, IntPtr.Zero);
            Native.PostMessageW(window, 0, UIntPtr.Zero, IntPtr.Zero);
            if (selected is >= 1 and <= 3) command(selected switch { 1 => TrayCommand.Open, 2 => TrayCommand.ToggleAvailability, _ => TrayCommand.Quit });
        }
        finally { Native.DestroyMenu(menu); }
    }
    public void Dispose()
    {
        if (disposed) return;
        SetEnabled(false); disposed = true; Native.RemoveWindowSubclass(window, procedure, 41);
    }
    private static class Native
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate IntPtr SubclassProc(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr reference);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal sealed class NotifyIconData
        {
            internal uint Size; internal IntPtr Window; internal uint Id; internal uint Flags; internal uint CallbackMessage; internal IntPtr Icon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Tip = "";
            internal uint State = 0, StateMask = 0;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Info = "";
            internal uint Version = 0;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string InfoTitle = "";
            internal uint InfoFlags = 0; internal Guid Item = Guid.Empty; internal IntPtr BalloonIcon = IntPtr.Zero;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct Point { internal int X, Y; }
        [DllImport("comctl32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowSubclass(IntPtr window, SubclassProc procedure, UIntPtr id, UIntPtr reference);
        [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool RemoveWindowSubclass(IntPtr window, SubclassProc procedure, UIntPtr id);
        [DllImport("comctl32.dll")] internal static extern IntPtr DefSubclassProc(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool Shell_NotifyIconW(uint operation, [In] NotifyIconData data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr LoadIconW(IntPtr instance, IntPtr name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern uint RegisterWindowMessageW(string name);
        [DllImport("user32.dll")] internal static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string? text);
        [DllImport("user32.dll")] internal static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr window, IntPtr parameters);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DestroyMenu(IntPtr menu);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PostMessageW(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);
    }
}
