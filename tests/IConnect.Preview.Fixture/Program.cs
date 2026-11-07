using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

// Test-only hostile worker. Never copied to the desktop installation.
using var input = new BinaryReader(Console.OpenStandardInput());
var mode = input.ReadInt32();
switch (mode)
{
    case 1:
        var path = input.ReadString(); var port = input.ReadInt32(); var fileDenied = false; var networkDenied = false; var childDenied = false;
        try { File.ReadAllBytes(path); } catch (UnauthorizedAccessException) { fileDenied = true; }
        try { using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp); using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), limit.Token); }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.AccessDenied) { networkDenied = true; }
        catch (OperationCanceledException) { networkDenied = true; }
        try { using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true }); if (child is not null) { child.WaitForExit(1000); childDenied = child.HasExited && child.ExitCode != 0; } }
        catch (System.ComponentModel.Win32Exception) { childDenied = true; }
        Console.OpenStandardOutput().Write(Encoding.ASCII.GetBytes($"{fileDenied},{networkDenied},{childDenied}")); return 0;
    case 2: while (true) Thread.Sleep(100);
    case 3: var block = new byte[64 * 1024]; for (var i = 0; i < 256; i++) Console.OpenStandardOutput().Write(block); return 0;
    case 4: var held = new List<byte[]>(); while (true) { var bytes = new byte[8 * 1024 * 1024]; bytes.AsSpan().Fill(1); held.Add(bytes); }
    case 6:
        var inherited=false;
        try
        {
            using var file=new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle((nint)input.ReadInt64(),false),FileAccess.Read);
            var prefix=new byte[32]; var read=file.Read(prefix); inherited=read>0 && Encoding.ASCII.GetString(prefix,0,read).StartsWith("private-inheritance-fixture",StringComparison.Ordinal);
        }
        catch(Exception) { }
        Console.OpenStandardOutput().WriteByte(inherited ? (byte)1 : (byte)0); return 0;
    default: return 2;
}
