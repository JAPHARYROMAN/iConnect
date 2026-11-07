using System.Buffers.Binary;
using System.Text;
using IConnect.Protocol;

namespace IConnect.Core;

public enum PreviewKind { Text, Png, Jpeg, Pdf }
public sealed record PreviewInput(PreviewKind Kind, string Name, byte[] Bytes, bool EndOfFile);
public sealed record PreviewDimensions(int Width, int Height);

public static class PreviewRules
{
    public const int MaxBytes = 8 * 1024 * 1024, MaxPixels = 16 * 1024 * 1024, MaxSide = 8192, RenderSide = 1280, MaxPages = 128;
    public static PreviewKind Kind(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    { ".png" => PreviewKind.Png, ".jpg" or ".jpeg" => PreviewKind.Jpeg, ".pdf" => PreviewKind.Pdf, _ => PreviewKind.Text };
    public static PreviewDimensions Dimensions(int width, int height)
    {
        if (width is < 1 or > MaxSide || height is < 1 or > MaxSide || (long)width * height > MaxPixels)
            throw new InvalidDataException("This image exceeds the preview dimension limit. Save it to open with another application.");
        return new(width, height);
    }
    public static PreviewDimensions? Probe(PreviewKind kind, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 1 or > MaxBytes) throw new InvalidDataException("This file exceeds the preview size limit or is empty.");
        if (kind == PreviewKind.Pdf)
        {
            if (bytes.Length < 9 || !bytes[..5].SequenceEqual("%PDF-"u8) || !((bytes[5] == '1' && bytes[6] == '.' && bytes[7] is >= (byte)'0' and <= (byte)'7') || bytes[5..8].SequenceEqual("2.0"u8)) || bytes[8] is not (10 or 13 or 32))
                throw new InvalidDataException("This file does not have a supported PDF header.");
            return null;
        }
        if (kind == PreviewKind.Png)
        {
            if (bytes.Length < 33 || !bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) || BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]) != 13 || !bytes[12..16].SequenceEqual("IHDR"u8))
                throw new InvalidDataException("This file does not have a supported PNG header.");
            var width = BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]); var height = BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]);
            if (width > int.MaxValue || height > int.MaxValue) throw new InvalidDataException("Unsupported image dimensions.");
            return Dimensions((int)width, (int)height);
        }
        if (kind == PreviewKind.Jpeg)
        {
            if (bytes.Length < 4 || bytes[0] != 0xff || bytes[1] != 0xd8) throw new InvalidDataException("This file does not have a supported JPEG header.");
            var at = 2; var markers = 0;
            while (at < Math.Min(bytes.Length, 256 * 1024) && ++markers <= 4096)
            {
                if (bytes[at++] != 0xff) break;
                var fill = 0; while (at < bytes.Length && bytes[at] == 0xff && ++fill <= 32) at++;
                if (at >= bytes.Length || fill > 32) break;
                var marker = bytes[at++];
                if (marker is 0 or 0xda or 0xd9 or 0xd8) break;
                if (marker is 0x01 or >= 0xd0 and <= 0xd7) continue;
                if (at + 2 > bytes.Length) break;
                var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[at..]);
                if (length < 2 || at + length > bytes.Length) break;
                if (marker is 0xc0 or 0xc2)
                {
                    if (length < 8 || bytes[at + 2] != 8 || bytes[at + 7] is not (1 or 3) || length != 8 + 3 * bytes[at + 7]) break;
                    return Dimensions(BinaryPrimitives.ReadUInt16BigEndian(bytes[(at + 5)..]), BinaryPrimitives.ReadUInt16BigEndian(bytes[(at + 3)..]));
                }
                if (marker is >= 0xc0 and <= 0xcf && marker is not (0xc4 or 0xc8 or 0xcc)) break;
                at += length;
            }
            throw new InvalidDataException("This JPEG structure is unsupported or exceeds the metadata limit.");
        }
        return null;
    }
    public static string Text(PreviewInput input)
    {
        if (input.Bytes.AsSpan().Contains((byte)0)) throw new InvalidDataException("Preview supports PNG, JPEG, PDF and UTF-8 plain text. Save this file to open it with another application.");
        var decoder = new UTF8Encoding(false, true).GetDecoder(); var chars = new char[input.Bytes.Length];
        decoder.Convert(input.Bytes, chars, input.EndOfFile, out _, out var used, out _);
        return new string(chars.Take(used).Where(c => !char.IsControl(c) || c is '\n' or '\r' or '\t').ToArray());
    }
    public static Task<PreviewInput> FetchAsync(FileEntry entry, Func<FileRequest, CancellationToken, Task<FileResponse>> request, CancellationToken ct) => FetchCoreAsync(entry,request,Kind(entry.Name),ct);
    public static Task<PreviewInput> FetchTextAsync(FileEntry entry, Func<FileRequest, CancellationToken, Task<FileResponse>> request, CancellationToken ct) => FetchCoreAsync(entry,request,PreviewKind.Text,ct);
    private static async Task<PreviewInput> FetchCoreAsync(FileEntry entry, Func<FileRequest, CancellationToken, Task<FileResponse>> request, PreviewKind kind, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (entry.IsDirectory || entry.Size < 0 || (entry.Permissions & (uint)StoragePermission.Read) == 0) throw new IOException("Choose an approved readable file.");
        if (kind != PreviewKind.Text && entry.Size is < 1 or > MaxBytes) throw new IOException("Image and PDF previews support files up to 8 MiB. Save this file to open it separately.");
        void Metadata(FileResponse response)
        {
            StorageRules.ValidateResponse(response);
            if (response.Status != FileStatus.Ok) throw new IOException(response.Message);
            var current = response.Entries.SingleOrDefault();
            if (current is null || current.Id != entry.Id || current.GrantId != entry.GrantId || current.Version != entry.Version || current.Size != entry.Size || current.Name != entry.Name || current.IsDirectory || (current.Permissions & (uint)StoragePermission.Read) == 0)
                throw new IOException("This file changed or is no longer approved. Refresh it before previewing.");
        }
        Metadata(await request(new() { Operation = FileOperation.GetMetadata, NodeId = entry.Id }, ct));
        var length = checked((int)Math.Min(entry.Size, kind == PreviewKind.Text ? StorageRules.MaxReadRange : MaxBytes)); var output = new byte[length]; var offset = 0; var eof = entry.Size == 0; var requests = 0;
        while (offset < length)
        {
            ct.ThrowIfCancellationRequested(); if (++requests > 512) throw new IOException("The preview source returned too many short ranges.");
            var response = await request(new() { Operation = FileOperation.ReadRange, NodeId = entry.Id, ExpectedVersion = entry.Version, Offset = offset, Length = (uint)Math.Min(StorageRules.MaxReadRange, length - offset) }, ct);
            Metadata(response); var count = response.Content.Length;
            if (count < 1 || count > Math.Min(StorageRules.MaxReadRange, length - offset) || response.EndOfFile != (offset + count == entry.Size)) throw new IOException("The preview source returned inconsistent file ranges.");
            response.Content.Span.CopyTo(output.AsSpan(offset, count)); offset += count; eof = response.EndOfFile;
        }
        Metadata(await request(new() { Operation = FileOperation.GetMetadata, NodeId = entry.Id }, ct));
        ct.ThrowIfCancellationRequested();
        if (kind != PreviewKind.Text) _ = Probe(kind, output);
        return new(kind, entry.Name, output, eof);
    }
}
