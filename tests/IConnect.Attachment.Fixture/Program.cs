using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

// Test-only protocol peer. Never copied into the product's installation.
if (args.Contains("--child")) return 0;
using var input = new BinaryReader(Console.OpenStandardInput());
if (input.ReadUInt32() != 0x31414349) return 1;
string Read() { var count = input.ReadInt32(); if (count is < 1 or > 32_768) throw new IOException(); var bytes = input.ReadBytes(count); if (bytes.Length != count) throw new IOException(); return Encoding.UTF8.GetString(bytes); }
var path = Read(); var expected = Read(); if (input.BaseStream.ReadByte() != -1) return 1;
var name = Path.GetFileName(path); var hr = 0;
if (name.StartsWith("fixture-hang")) Thread.Sleep(Timeout.Infinite);
if (name.StartsWith("fixture-negative")) hr = unchecked((int)0x800700e1);
if (name.StartsWith("fixture-crash")) return 2;
if (name.StartsWith("fixture-spawn"))
{
    try { using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { ArgumentList = { "--child" }, UseShellExecute = false, CreateNoWindow = true }); hr = unchecked((int)0x80004005); }
    catch (System.ComponentModel.Win32Exception) { }
}
if (name.StartsWith("fixture-handle-"))
{
    var handle = nint.Parse(Path.GetFileNameWithoutExtension(name).Substring("fixture-handle-".Length));
    if (Native.GetFileInformationByHandle(handle, out var info) && $"{info.Volume:x8}:{info.High:x8}{info.Low:x8}" == expected) hr = unchecked((int)0x80070005);
}
using var output = new BinaryWriter(Console.OpenStandardOutput()); output.Write(0x31414349u); output.Write(hr);
if (name.StartsWith("fixture-overflow")) output.Write((byte)1);
output.Flush(); return 0;

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct Time { public uint Low, High; }
    [StructLayout(LayoutKind.Sequential)] internal struct Info { public uint Attributes; public Time Created, Accessed, Written; public uint Volume, SizeHigh, SizeLow, Links, High, Low; }
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetFileInformationByHandle(nint file, out Info info);
}
