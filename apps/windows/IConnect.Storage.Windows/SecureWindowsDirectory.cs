using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using IConnect.Core;
using IConnect.Protocol;
using Microsoft.Win32.SafeHandles;

namespace IConnect.Storage.Windows;

// Remote names are opened one component at a time relative to an already open
// directory. Handles deny delete sharing so ancestors cannot move during an op.
public sealed class SecureWindowsDirectory : IDisposable
{
    public static bool IsLocalVolumePath(string value)
    {
        const string prefix = "\\\\?\\Volume{";
        return value.Length is >= 49 and <= 8192 && value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParseExact(value.AsSpan(prefix.Length, 36), "D", out _) && value[prefix.Length + 36] == '}' && value[prefix.Length + 37] == '\\' &&
            value[(prefix.Length + 38)..].Split('\\', StringSplitOptions.RemoveEmptyEntries).All(x => x is not ("." or "..") && !x.Any(char.IsControl) && !x.Contains(':') && !x.Contains('/'));
    }
    public sealed record DirectoryName(string Name, bool IsDirectory);
    public sealed record Snapshot(string Identity, string CanonicalPath, bool IsDirectory, long Size,
        long ModifiedAt, uint Attributes, uint Links, long WriteTimestamp)
    {
        public string Version => StorageRules.Version(Identity, Size, WriteTimestamp);
    }
    private const uint ReadAttributes = 0x80, Synchronize = 0x100000, DeleteAccess = 0x10000;
    private const uint ShareRead = 1, ShareWrite = 2;
    private const uint OpenReparsePoint = 0x00200000, SynchronousIo = 0x20;
    private readonly List<SafeFileHandle> ancestors;
    public SafeFileHandle Handle { get; }
    public Snapshot Info { get; }

    private SecureWindowsDirectory(SafeFileHandle handle, List<SafeFileHandle> parents)
    {
        Handle = handle; ancestors = parents;
        try { Info = Inspect(handle); }
        catch { Dispose(); throw; }
        if ((Info.Attributes & (uint)FileAttributes.ReparsePoint) != 0 || (!Info.IsDirectory && Info.Links != 1))
        {
            Dispose();
            throw new StorageException(FileStatus.AccessDenied, "Links and redirected locations are not shared.");
        }
    }

    public static SecureWindowsDirectory OpenRoot(string localPath)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows storage requires Windows.");
        var handle = Native.CreateFileW(localPath, 1 | 0x20 | ReadAttributes | Synchronize, ShareRead | ShareWrite,
            IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); ThrowLastError(); }
        var root = new SecureWindowsDirectory(handle, []);
        if (!root.Info.IsDirectory) { root.Dispose(); throw new StorageException(FileStatus.InvalidRequest, "Choose a folder or drive."); }
        return root;
    }

    public static SecureWindowsDirectory OpenPath(string canonicalRoot, string rootIdentity,
        IReadOnlyList<string> segments, bool directory, bool writable = false, bool mutation = false, bool readData = true)
        => OpenPathCore(canonicalRoot, rootIdentity, segments, directory, writable, mutation, readData, false);

    // Only locally derived profile components may use reserved internal names.
    internal static SecureWindowsDirectory OpenPrivatePath(string canonicalRoot, string rootIdentity, IReadOnlyList<string> segments)
        => OpenPathCore(canonicalRoot, rootIdentity, segments, true, false, false, false, true);

    private static SecureWindowsDirectory OpenPathCore(string canonicalRoot, string rootIdentity,
        IReadOnlyList<string> segments, bool directory, bool writable, bool mutation, bool readData, bool internalNames)
    {
        var root = OpenRoot(canonicalRoot);
        if (root.Info.Identity != rootIdentity)
        { root.Dispose(); throw new StorageException(FileStatus.StaleReference, "The approved storage location has changed."); }
        if (segments.Count == 0)
        {
            if (writable || mutation || !directory) { root.Dispose(); throw new StorageException(FileStatus.AccessDenied, "This action cannot target an approved root."); }
            return root;
        }
        var parents = new List<SafeFileHandle>();
        try
        {
            var current = root;
            for (var i = 0; i < segments.Count; i++)
            {
                if (!internalNames) StorageRules.ValidateName(segments[i]);
                var last = i == segments.Count - 1;
                var child = OpenRelative(current.Handle, segments[i], last ? directory : true,
                    writable && last, mutation && last, readData: readData, internalTemporary: internalNames);
                parents.Add(current.Handle);
                current.ancestors.Clear(); // transfer handle ownership to the final result
                current = new SecureWindowsDirectory(child, []);
            }
            current.ancestors.AddRange(parents);
            if (!IsWithin(current.Info.CanonicalPath, canonicalRoot))
            { current.Dispose(); throw new StorageException(FileStatus.AccessDenied, "The target is outside the approved location."); }
            return current;
        }
        catch
        {
            root.Handle.Dispose();
            foreach (var parent in parents) parent.Dispose();
            throw;
        }
    }

    public static bool IsWithin(string candidate, string root) =>
        candidate.Equals(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    public static SecureWindowsDirectory OpenBoundPath(string rootPath, string rootIdentity,
        IReadOnlyList<(string Name, string Identity)> segments)
    {
        var opened = new List<SecureWindowsDirectory>();
        try
        {
            var current = OpenRoot(rootPath); opened.Add(current);
            if (current.Info.Identity != rootIdentity || !current.Info.CanonicalPath.Equals(rootPath, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The selected destination changed.");
            foreach (var segment in segments)
            {
                current = current.OpenChild(segment.Name, true); opened.Add(current);
                if (current.Info.Identity != segment.Identity) throw new IOException("A destination folder was replaced. Review it locally before starting a new copy.");
            }
            foreach (var ancestor in opened.Take(opened.Count - 1))
            { current.ancestors.Add(ancestor.Handle); ancestor.ancestors.Clear(); }
            opened.Clear(); return current;
        }
        finally { foreach (var item in opened) item.Dispose(); }
    }

    private static SafeFileHandle OpenRelative(SafeFileHandle parent, string name, bool directory,
        bool writable, bool mutation, bool create = false, bool readData = true, bool internalTemporary = false, bool attachmentAnchor = false)
    {
        if (!internalTemporary) StorageRules.ValidateName(name);
        var text = Marshal.StringToHGlobalUni(name);
        var unicode = Marshal.AllocHGlobal(Marshal.SizeOf<Native.UnicodeString>());
        try
        {
            Marshal.StructureToPtr(new Native.UnicodeString
            { Length = checked((ushort)(name.Length * 2)), MaximumLength = checked((ushort)(name.Length * 2 + 2)), Buffer = text }, unicode, false);
            var attributes = new Native.ObjectAttributes
            {
                Length = Marshal.SizeOf<Native.ObjectAttributes>(), RootDirectory = parent.DangerousGetHandle(), ObjectName = unicode
            };
            var access = ReadAttributes | Synchronize | (directory ? 1u | 0x20u : readData ? 1u : 0u) |
                (writable ? 2u | 4u : 0u) | (mutation ? DeleteAccess : 0u);
            var status = Native.NtCreateFile(out var result, access, ref attributes, out _, IntPtr.Zero,
                (uint)(directory ? FileAttributes.Directory : FileAttributes.Normal),
                attachmentAnchor || !writable && !mutation && (directory || !readData) ? ShareRead | ShareWrite : ShareRead,
                create ? 2u : 1u, OpenReparsePoint | SynchronousIo | (directory ? 1u : 0x40u), IntPtr.Zero, 0);
            GC.KeepAlive(parent);
            if (status < 0)
            {
                result?.Dispose();
                ThrowWin32(checked((int)Native.RtlNtStatusToDosError(status)));
            }
            return result!;
        }
        finally { Marshal.FreeHGlobal(unicode); Marshal.FreeHGlobal(text); }
    }

    public SecureWindowsDirectory CreateDirectory(string name)
    {
        if (!Info.IsDirectory) throw new StorageException(FileStatus.InvalidRequest, "Choose a destination folder.");
        return new SecureWindowsDirectory(OpenRelative(Handle, name, true, false, false, true), []);
    }

    public SecureWindowsDirectory CreateFile(string name) =>
        new(OpenRelative(Handle, name, false, true, true, true), []);

    public SecureWindowsDirectory OpenTransferFile(string id, bool create)
    {
        if (!Info.IsDirectory || !ProtocolRules.TokenPattern().IsMatch(id)) throw new InvalidDataException("Invalid temporary-file owner.");
        return new SecureWindowsDirectory(OpenRelative(Handle, ".iconnect-transfer-" + id + ".part", false, true, true, create, internalTemporary: true), []);
    }
    // Owner recovery may inspect a reserved temporary name without requesting
    // write access. Mutation pins the exact file against writers and replacement.
    internal SecureWindowsDirectory OpenReceivingPart(string id, bool mutation = false)
    {
        if (!Info.IsDirectory || !ProtocolRules.TokenPattern().IsMatch(id)) throw new InvalidDataException("Invalid temporary-file name.");
        return new(OpenRelative(Handle, ".iconnect-transfer-" + id + ".part", false, false, mutation,
            readData: mutation, internalTemporary: true), []);
    }
    public SecureWindowsDirectory OpenChild(string name, bool directory = false) =>
        new(OpenRelative(Handle, name, directory, false, false), []);
    public SecureWindowsDirectory OpenMutableChild(string name) =>
        new(OpenRelative(Handle, name, false, false, true), []);
    internal SecureWindowsDirectory OpenWritableMetadataChild(string name) =>
        new(OpenRelative(Handle, name, false, true, true), []);
    // READ_DATA participates in delete-sharing checks; READ_ATTRIBUTES alone
    // cannot pin a path while an attachment handler opens it by name.
    internal SecureWindowsDirectory OpenAttachmentAnchor(string name) =>
        new(OpenRelative(Handle, name, false, false, false, attachmentAnchor: true), []);
    internal SecureWindowsDirectory OpenManagementChild(string name, bool directory, bool mutation = false) =>
        new(OpenRelative(Handle, name, directory, false, mutation, readData: false), []);
    internal SecureWindowsDirectory OpenRecoveryDirectory(string name, bool create = false, bool mutation = false)
    {
        if (!name.StartsWith(".iconnect-trash-", StringComparison.Ordinal) || !ProtocolRules.TokenPattern().IsMatch(name[16..]))
            throw new InvalidDataException("Invalid recovery directory.");
        return new(OpenRelative(Handle, name, true, false, mutation, create, internalTemporary: true), []);
    }
    internal SecureWindowsDirectory OpenRecoveryItem(string name, bool directory, bool mutation = false)
    {
        if (!ProtocolRules.TokenPattern().IsMatch(name)) throw new InvalidDataException("Invalid recovery item.");
        return new(OpenRelative(Handle, name, directory, false, mutation, readData: false), []);
    }
    internal bool HasDataStreams()
    {
        const int fileStreamInfo = 7, endOfFile = 38, bufferSize = 1024;
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            // Any stream, or a stream list too large to inspect, prevents cleanup.
            if (Native.GetFileInformationByHandleEx(Handle, fileStreamInfo, buffer, bufferSize)) return true;
            var error = Marshal.GetLastWin32Error();
            if (error == endOfFile) return false;
            throw new StorageException(FileStatus.IoError, "The recovery folder's data streams could not be checked. The folder was kept.")
                { NativeError = error };
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    public void Write(long offset, ReadOnlySpan<byte> data) => RandomAccess.Write(Handle, data, offset);
    public void RequireOnlyDefaultDataStream()
    {
        const int streamInfo = 7, endOfFile = 38, bufferSize = 1024, nameOffset = 24;
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            if (!Native.GetFileInformationByHandleEx(Handle, streamInfo, buffer, bufferSize))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == endOfFile && (Info.IsDirectory || Info.Size == 0)) return;
                throw new IOException("Private metadata data streams could not be verified. Its contents were kept.");
            }
            var next = Marshal.ReadInt32(buffer); var length = Marshal.ReadInt32(buffer, 4);
            if (Info.IsDirectory || next != 0 || length != 14 || Marshal.ReadInt64(buffer, 8) != Info.Size ||
                Marshal.PtrToStringUni(IntPtr.Add(buffer, nameOffset), length / 2) != "::$DATA")
                throw new IOException("Private metadata has additional or unverified data streams. Its contents were kept.");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    public void SetLength(long size) => RandomAccess.SetLength(Handle, size);
    public void Flush() => RandomAccess.FlushToDisk(Handle);

    // A fixed stream on this already held, single-link file. No caller can
    // supply an ADS name or use this operation to reach another object.
    internal void MarkUntrusted()
        => MarkUntrusted(Handle, Info.Identity);
    internal static void MarkUntrusted(SafeFileHandle handle, string identity)
    {
        var current = Inspect(handle);
        if (current.Identity != identity || current.IsDirectory || current.Links != 1 || (current.Attributes & 0x400) != 0)
            throw new IOException("The received file cannot be identified for origin marking.");
        using var stream = Native.CreateFileW(current.CanonicalPath + ":Zone.Identifier", 1 | 2 | ReadAttributes | Synchronize,
            ShareRead | ShareWrite | 4, IntPtr.Zero, 4, OpenReparsePoint, IntPtr.Zero);
        if (stream.IsInvalid) ThrowLastError();
        if (!Native.GetFileInformationByHandle(stream, out var details)) ThrowLastError();
        if ($"{details.VolumeSerialNumber:x8}:{details.FileIndexHigh:x8}{details.FileIndexLow:x8}" != current.Identity || details.Links != 1)
            throw new IOException("The origin stream belongs to another object.");
        var length = RandomAccess.GetLength(stream);
        if (length > 16 * 1024) throw new IOException("Existing origin metadata exceeds its limit and was kept.");
        var raw = new byte[(int)length]; var offset = 0;
        while (offset < raw.Length) { var read = RandomAccess.Read(stream, raw.AsSpan(offset), offset); if (read == 0) throw new IOException("Origin metadata changed."); offset += read; }
        Encoding encoding = new UTF8Encoding(false, true); var preamble = 0;
        if (raw.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) { encoding = new UnicodeEncoding(false, true, true); preamble = 2; }
        else if (raw.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) { encoding = new UnicodeEncoding(true, true, true); preamble = 2; }
        else if (raw.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) { encoding = new UTF8Encoding(true, true); preamble = 3; }
        string text;
        try { text = encoding.GetString(raw, preamble, raw.Length - preamble).TrimEnd('\0'); }
        catch (DecoderFallbackException e) { throw new IOException("Existing origin metadata has an unsupported encoding and was kept.", e); }
        if (text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'))) throw new IOException("Existing origin metadata needs review and was kept.");
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList(); var zone = false; var found = false; var marked = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith('['))
            {
                if (zone && !marked) { lines.Insert(i++, "ZoneId=4"); marked = true; }
                zone = line.Equals("[ZoneTransfer]", StringComparison.OrdinalIgnoreCase);
                if (zone && found) throw new IOException("Duplicated origin sections were kept for review.");
                found |= zone;
            }
            else if (zone && line.Split('=', 2)[0].Trim().Equals("ZoneId", StringComparison.OrdinalIgnoreCase))
            { lines[i] = "ZoneId=4"; marked = true; }
        }
        if (!found) { lines.Add("[ZoneTransfer]"); lines.Add("ZoneId=4"); }
        else if (!marked) lines.Add("ZoneId=4");
        var encoded = encoding.GetPreamble().Concat(encoding.GetBytes(string.Join("\r\n", lines).Trim('\r', '\n') + "\r\n")).ToArray();
        if (encoded.Length > 16 * 1024) throw new IOException("Origin metadata exceeds its limit and was kept.");
        RandomAccess.Write(stream, encoded, 0); RandomAccess.SetLength(stream, encoded.Length); RandomAccess.FlushToDisk(stream);
        var confirmed = new byte[encoded.Length]; offset = 0;
        while (offset < confirmed.Length) { var read = RandomAccess.Read(stream, confirmed.AsSpan(offset), offset); if (read == 0) throw new IOException("Origin metadata did not persist."); offset += read; }
        if (!encoded.AsSpan().SequenceEqual(confirmed) || RandomAccess.GetLength(stream) != encoded.Length)
            throw new IOException("Origin metadata did not persist. This copy remains unfinished.");
    }
    public void Delete()
    {
        // Only a handle opened with DELETE access can use this operation.
        var memory = Marshal.AllocHGlobal(1);
        try
        {
            Marshal.WriteByte(memory, 1);
            var status = Native.NtSetInformationFile(Handle, out _, memory, 1, 13);
            if (status < 0) ThrowWin32(checked((int)Native.RtlNtStatusToDosError(status)));
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    public void RenameTo(SecureWindowsDirectory destination, string name)
    {
        StorageRules.ValidateName(name);
        if (!destination.Info.IsDirectory) throw new StorageException(FileStatus.InvalidRequest, "Choose a destination folder.");
        var bytes = Encoding.Unicode.GetBytes(name);
        var size = 22 + bytes.Length;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            for (var i = 0; i < size; i++) Marshal.WriteByte(buffer, i, 0);
            Marshal.WriteIntPtr(buffer, 8, destination.Handle.DangerousGetHandle());
            Marshal.WriteInt32(buffer, 16, bytes.Length);
            Marshal.Copy(bytes, 0, buffer + 20, bytes.Length);
            var status = Native.NtSetInformationFile(Handle, out _, buffer, checked((uint)size), 10);
            if (status < 0) ThrowWin32(checked((int)Native.RtlNtStatusToDosError(status)));
            GC.KeepAlive(destination);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public int Read(long offset, byte[] destination)
    {
        if (Info.IsDirectory || offset < 0) throw new StorageException(FileStatus.InvalidRequest, "Select a file to read.");
        return RandomAccess.Read(Handle, destination, offset);
    }

    public IEnumerable<DirectoryName> EnumerateNames(CancellationToken ct)
    {
        if (!Info.IsDirectory) throw new StorageException(FileStatus.InvalidRequest, "Select a directory.");
        const int capacity = 64 * 1024;
        var buffer = Marshal.AllocHGlobal(capacity);
        try
        {
            var restart = true;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                // FILE_FULL_DIR_INFO: fixed header is 68 bytes followed by UTF-16 name.
                if (!Native.GetFileInformationByHandleEx(Handle, restart ? 15 : 14, buffer, capacity))
                {
                    if (Marshal.GetLastWin32Error() == 18) yield break;
                    ThrowLastError();
                }
                restart = false;
                var position = 0;
                while (true)
                {
                    var length = Marshal.ReadInt32(buffer, position + 60);
                    if (length is < 0 or > capacity - 68 || position + 68 + length > capacity || length % 2 != 0)
                        throw new StorageException(FileStatus.IoError, "The directory returned invalid metadata.");
                    var name = Marshal.PtrToStringUni(buffer + position + 68, length / 2)!;
                    if (name is not ("." or "..")) yield return new(name, (Marshal.ReadInt32(buffer, position + 56) & 0x10) != 0);
                    var next = Marshal.ReadInt32(buffer, position);
                    if (next == 0) break;
                    if (next < 68 || position + next >= capacity)
                        throw new StorageException(FileStatus.IoError, "The directory returned invalid metadata.");
                    position += next;
                }
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public static Snapshot Inspect(SafeFileHandle handle)
    {
        if (!Native.GetFileInformationByHandle(handle, out var info)) ThrowLastError();
        var path = new StringBuilder(32_768);
        if (Native.GetFinalPathNameByHandleW(handle, path, path.Capacity, 1) == 0) ThrowLastError();
        var identity = $"{info.VolumeSerialNumber:x8}:{info.FileIndexHigh:x8}{info.FileIndexLow:x8}";
        var size = checked((long)(((ulong)info.SizeHigh << 32) | info.SizeLow));
        var writeTimestamp = ((long)info.LastWrite.High << 32) | info.LastWrite.Low;
        var modified = DateTimeOffset.FromFileTime(writeTimestamp).ToUnixTimeMilliseconds();
        return new(identity, path.ToString(), (info.Attributes & 0x10) != 0, size, modified, info.Attributes, info.Links, writeTimestamp);
    }

    public void Dispose()
    {
        Handle.Dispose();
        foreach (var parent in ancestors) parent.Dispose();
        ancestors.Clear();
    }

    // Transfer a validated held path to its final directory without releasing ancestor pins.
    internal static SecureWindowsDirectory TakeHeldPath(List<SecureWindowsDirectory> held)
    {
        var result = held[^1];
        for (var i = 0; i < held.Count - 1; i++)
        { result.ancestors.AddRange(held[i].ancestors); held[i].ancestors.Clear(); result.ancestors.Add(held[i].Handle); }
        held.Clear(); return result;
    }

    private static void ThrowLastError() => ThrowWin32(Marshal.GetLastWin32Error());
    private static void ThrowWin32(int error)
    {
        var status = error switch
        {
            2 or 3 => FileStatus.NotFound,
            5 or 1314 => FileStatus.AccessDenied,
            80 or 183 => FileStatus.Conflict,
            32 or 33 => FileStatus.Conflict,
            50 or 1 => FileStatus.Unsupported,
            112 or 39 => FileStatus.ResourceExhausted,
            _ => FileStatus.IoError
        };
        var message = error switch
        {
            2 or 3 => "The file or folder is unavailable.",
            5 or 1314 => "Windows denied access to this location.",
            80 or 183 => "A file or folder with that name already exists.",
            32 or 33 => "The file or folder is in use. Close other operations and retry.",
            112 or 39 => "The destination has insufficient free space.",
            _ => $"The filesystem operation failed (Windows error {error})."
        };
        throw new StorageException(status, message) { NativeError = error };
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct FileTime { public uint Low, High; }
        [StructLayout(LayoutKind.Sequential)] public struct FileInformation
        {
            public uint Attributes; public FileTime Created, LastAccess, LastWrite;
            public uint VolumeSerialNumber, SizeHigh, SizeLow, Links, FileIndexHigh, FileIndexLow;
        }
        [StructLayout(LayoutKind.Sequential)] public struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }
        [StructLayout(LayoutKind.Sequential)] public struct ObjectAttributes
        { public int Length; public IntPtr RootDirectory, ObjectName; public uint Attributes; public IntPtr SecurityDescriptor, SecurityQualityOfService; }
        [StructLayout(LayoutKind.Sequential)] public struct IoStatus { public IntPtr Status, Information; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("ntdll.dll")] public static extern int NtCreateFile(out SafeFileHandle handle, uint access,
            ref ObjectAttributes attributes, out IoStatus io, IntPtr allocation, uint fileAttributes, uint share,
            uint disposition, uint options, IntPtr ea, uint eaLength);
        [DllImport("ntdll.dll")] public static extern uint RtlNtStatusToDosError(int status);
        [DllImport("ntdll.dll")] public static extern int NtSetInformationFile(SafeFileHandle handle,
            out IoStatus io, IntPtr buffer, uint length, int informationClass);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, IntPtr buffer, int size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder buffer, int size, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, IntPtr buffer, uint size);
    }
}
