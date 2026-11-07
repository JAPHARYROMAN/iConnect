using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using IConnect.Protocol;

namespace IConnect.Core;

/// <summary>Validated copy plans. Component names never authorize filesystem access.</summary>
public static class UploadBatchRules
{
    public const int MaxItems = 4096, MaxRoots = 128, MaxDepth = 32, MaxPathChars = 2048, MaxEncodedBytes = 4 * 1024 * 1024;
    public const int MaxPlans = 8, MaxPeerPlans = 4;
    public const string Capability = "upload-batches-v1";
    public static bool IsRpc(FileOperation operation) => operation is >= FileOperation.UploadBatchContext and <= FileOperation.StopUploadBatch;
    public static bool HasManifest(UploadBatchPlanPhase phase) => phase is UploadBatchPlanPhase.UploadPlanReady or UploadBatchPlanPhase.UploadPlanStopping or UploadBatchPlanPhase.UploadPlanStopped or UploadBatchPlanPhase.UploadPlanCompleted;
    public static bool IsTerminal(UploadBatchPlanPhase phase) => phase is UploadBatchPlanPhase.UploadPlanCanceled or UploadBatchPlanPhase.UploadPlanStopped or UploadBatchPlanPhase.UploadPlanCompleted;
    public static void ValidateReceipt(UploadBatchReceipt receipt)
    {
        if (receipt.Header is null) throw new InvalidDataException("A batch receipt needs its original intent.");
        ValidateHeader(receipt.Header);
        if (!Enum.IsDefined(receipt.Phase) || receipt.ManifestOffset > receipt.Header.EncodedLength ||
            HasManifest(receipt.Phase) && receipt.ManifestOffset != receipt.Header.EncodedLength ||
            receipt.Phase == UploadBatchPlanPhase.UploadPlanCanceled && receipt.ManifestOffset != 0 || receipt.EffectsStarted && !HasManifest(receipt.Phase) ||
            receipt.Phase is UploadBatchPlanPhase.UploadPlanStopping or UploadBatchPlanPhase.UploadPlanStopped && !receipt.EffectsStarted ||
            receipt.Message.Length > 512 || receipt.Message.Any(char.IsControl)) throw new InvalidDataException("Invalid batch admission receipt.");
        ValidateOutcomes(receipt.Phase, receipt.Header.ItemCount, receipt.ItemOutcomes.Span);
    }
    public static void ValidateOutcomes(UploadBatchPlanPhase phase, uint count, ReadOnlySpan<byte> outcomes)
    {
        var terminal = phase is UploadBatchPlanPhase.UploadPlanStopped or UploadBatchPlanPhase.UploadPlanCompleted;
        if (terminal ? outcomes.Length != count : outcomes.Length != 0) throw new InvalidDataException("Invalid batch outcome count.");
        foreach (var outcome in outcomes)
            if (outcome > (byte)UploadBatchItemPhase.BatchItemReview || phase == UploadBatchPlanPhase.UploadPlanCompleted && outcome is 0 or 4)
                throw new InvalidDataException("A completed batch contains an unresolved item.");
    }
    public static UploadBatchReceipt CheckReceipt(UploadBatchHeader original, FileResponse response)
    {
        ValidateHeader(original); StorageRules.ValidateResponse(response);
        if (response.Status != FileStatus.Ok) throw new StorageException(response.Status, response.Message.Length == 0 ? "The batch admission needs review." : response.Message);
        if (response.Batch is null || response.MutationEpoch != original.Epoch || !response.Batch.Header.Equals(original))
            throw new InvalidDataException("The receiver changed the original batch intent or history namespace.");
        return response.Batch;
    }
    public static void ValidateItemReceipt(UploadBatchItemReceipt receipt)
    {
        if (receipt.Header is null) throw new InvalidDataException("A child receipt needs its parent intent.");
        ValidateHeader(receipt.Header);
        if (receipt.Index >= receipt.Header.ItemCount || !CanonicalToken(receipt.Id) || MutationRules.CreatedAt(receipt.Id) != 0 || !Enum.IsDefined(receipt.Phase) ||
            receipt.Message.Length > 512 || receipt.Message.Any(char.IsControl) || receipt.ResultingName.Length > 255 ||
            receipt.Phase == UploadBatchItemPhase.BatchItemSucceeded && receipt.ResultingName.Length == 0) throw new InvalidDataException("Invalid scoped batch child receipt.");
        if (receipt.ResultingName.Length != 0) StorageRules.ValidateName(receipt.ResultingName);
    }
    public static UploadBatchItemReceipt CheckItemReceipt(string peer, UploadBatchHeader original, int index, bool directory, FileResponse response)
    {
        ValidateHeader(original); StorageRules.ValidateResponse(response);
        if (response.Status != FileStatus.Ok) throw new StorageException(response.Status, response.Message.Length == 0 ? "The batch item needs review." : response.Message);
        var item = response.BatchItem;
        if (item is null || response.MutationEpoch != original.Epoch || !item.Header.Equals(original) || item.Index != index || item.IsDirectory != directory || item.Id != ChildId(peer, original, index, directory))
            throw new InvalidDataException("The receiver changed the scoped batch item binding.");
        return item;
    }
    // The descriptor is retained from the validated original manifest, never from a reply.
    public static UploadInfo CheckFileReceipt(string peer, UploadBatchHeader original, int index, UploadInfo descriptor, FileResponse response)
    {
        UploadRules.Validate(descriptor); var item = CheckItemReceipt(peer, original, index, false, response); var file = response.Upload;
        var phase = file?.Phase switch { UploadPhase.UploadCompleted => UploadBatchItemPhase.BatchItemSucceeded, UploadPhase.UploadSkipped => UploadBatchItemPhase.BatchItemSkipped,
            UploadPhase.UploadCanceled => UploadBatchItemPhase.BatchItemFailed, UploadPhase.UploadReview => UploadBatchItemPhase.BatchItemReview, _ => UploadBatchItemPhase.BatchItemPending };
        if (file is null || descriptor.Id != ChildId(peer, original, index, false) || file.Id != descriptor.Id || file.Name != descriptor.Name || file.Size != descriptor.Size ||
            !file.Sha256.Equals(descriptor.Sha256) || file.Collision != descriptor.Collision || item.Phase != phase ||
            item.ResultingName != (file.Phase == UploadPhase.UploadCompleted ? file.FinalName : "")) throw new InvalidDataException("The receiver changed the batch file descriptor or outcome.");
        return file;
    }

    public static bool CanonicalToken(string value) => ProtocolRules.TokenPattern().IsMatch(value) && ProtocolRules.Base64Url(ProtocolRules.Decode64(value)) == value;
    public static void ValidateHeader(UploadBatchHeader header)
    {
        if (!CanonicalToken(header.Id) || !CanonicalToken(header.Epoch) || !CanonicalToken(header.GrantId) ||
            MutationRules.CreatedAt(header.Id) is < 0 or > StorageRules.MaxTimestamp || header.EncodedLength is < 1 or > MaxEncodedBytes ||
            header.ItemCount is < 1 or > MaxItems || header.TotalBytes is < 0 or > StorageRules.MaxTransferSize || header.Sha256.Length != 32)
            throw new InvalidDataException("Invalid outgoing-batch intent.");
        var known = new UploadBatchHeader { Id = header.Id, Epoch = header.Epoch, GrantId = header.GrantId, EncodedLength = header.EncodedLength,
            ItemCount = header.ItemCount, TotalBytes = header.TotalBytes, Sha256 = header.Sha256 };
        if (!header.ToByteArray().AsSpan().SequenceEqual(known.ToByteArray())) throw new InvalidDataException("Unsupported outgoing-batch intent fields.");
    }
    public static long Validate(UploadBatchManifest manifest)
    {
        if (!Enum.IsDefined(manifest.Collision) || manifest.Items.Count is < 1 or > MaxItems) throw new InvalidDataException("Invalid outgoing-batch plan.");
        var depth = new int[manifest.Items.Count]; var pathChars = new int[manifest.Items.Count]; var names = new HashSet<(int, string)>(); var roots = 0; long bytes = 0;
        for (var index = 0; index < manifest.Items.Count; index++)
        {
            var item = manifest.Items[index];
            if (item.Parent < -1 || item.Parent >= index || item.Name.Length is < 1 or > 255 || !ValidText(item.Name) || item.Name.Any(char.IsControl) ||
                item.Size is < 0 or > StorageRules.MaxTransferSize || item.Error.Length > 512 || !ValidText(item.Error) || item.Error.Any(char.IsControl) ||
                !names.Add((item.Parent, CollisionKey(item.Name)))) throw new InvalidDataException("Invalid or ambiguous outgoing-batch item.");
            if (item.Parent < 0) { if (++roots > MaxRoots) throw new InvalidDataException("Too many selected batch roots."); depth[index] = 1; }
            else
            {
                var parent = manifest.Items[item.Parent];
                if (!parent.IsDirectory || parent.Error.Length != 0) throw new InvalidDataException("A batch item has no usable parent directory.");
                depth[index] = depth[item.Parent] + 1;
            }
            pathChars[index] = item.Name.Length + (item.Parent < 0 ? 0 : pathChars[item.Parent]);
            if (depth[index] > MaxDepth || pathChars[index] > MaxPathChars || item.IsDirectory && (item.Size != 0 || item.Sha256.Length != 0) ||
                item.Error.Length != 0 && item.Sha256.Length != 0 || !item.IsDirectory && item.Error.Length == 0 && item.Sha256.Length != 32)
                throw new InvalidDataException("Invalid outgoing-batch depth or content descriptor.");
            if (item.Error.Length == 0)
            {
                try { StorageRules.ValidateName(item.Name); }
                catch (StorageException ex) { throw new InvalidDataException("An outgoing-batch name cannot be represented safely.", ex); }
                if (!item.IsDirectory)
                {
                    if (item.Size > StorageRules.MaxTransferSize - bytes) throw new InvalidDataException("The outgoing batch exceeds its aggregate byte limit.");
                    bytes += item.Size;
                }
            }
        }
        if (manifest.CalculateSize() > MaxEncodedBytes) throw new InvalidDataException("The outgoing-batch manifest exceeds its encoding limit.");
        if (!manifest.ToByteArray().AsSpan().SequenceEqual(KnownFields(manifest).ToByteArray())) throw new InvalidDataException("Unsupported outgoing-batch fields.");
        return bytes;
    }
    public static byte[] Encode(UploadBatchManifest manifest) { Validate(manifest); return CanonicalBytes(manifest); }
    public static UploadBatchManifest Decode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length is < 1 or > MaxEncodedBytes) throw new InvalidDataException("Invalid batch encoding length.");
        Preflight(encoded);
        UploadBatchManifest manifest;
        try { manifest = UploadBatchManifest.Parser.ParseFrom(encoded); }
        catch (InvalidProtocolBufferException ex) { throw new InvalidDataException("Invalid batch encoding.", ex); }
        Validate(manifest);
        if (!encoded.SequenceEqual(CanonicalBytes(manifest))) throw new InvalidDataException("Unsupported or noncanonical batch encoding.");
        return manifest;
    }
    public static UploadBatchManifest Decode(UploadBatchHeader header, ReadOnlySpan<byte> encoded)
    {
        ValidateHeader(header);
        if (encoded.Length != header.EncodedLength || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(encoded), header.Sha256.Span))
            throw new InvalidDataException("The outgoing-batch manifest checksum or length changed.");
        var manifest = Decode(encoded);
        if (manifest.Items.Count != header.ItemCount || Validate(manifest) != header.TotalBytes) throw new InvalidDataException("The outgoing-batch manifest changed its declared totals.");
        return manifest;
    }
    // Count and bound fields before protobuf can allocate repeated objects or strings.
    private static void Preflight(ReadOnlySpan<byte> encoded)
    {
        var at = 0; var items = 0; var collisionSeen = false;
        while (at < encoded.Length)
        {
            var tag = ReadVarint(encoded, ref at);
            if (tag == 16) { if (collisionSeen || ReadVarint(encoded, ref at) > 2) throw new InvalidDataException("Unsupported batch collision choice."); collisionSeen = true; continue; }
            if (tag != 10 || collisionSeen || ++items > MaxItems) throw new InvalidDataException("Unsupported batch fields or item count.");
            var item = ReadField(encoded, ref at, 2500); var position = 0; ulong previous = 0;
            while (position < item.Length)
            {
                var field = ReadVarint(item, ref position);
                if (field <= previous) throw new InvalidDataException("Noncanonical batch item fields."); previous = field;
                switch (field)
                {
                    case 8: if (ReadVarint(item, ref position) > uint.MaxValue) throw new InvalidDataException("Invalid batch parent."); break;
                    case 18: ReadField(item, ref position, 255 * 3); break;
                    case 24: if (ReadVarint(item, ref position) > 1) throw new InvalidDataException("Invalid directory flag."); break;
                    case 32: if (ReadVarint(item, ref position) > StorageRules.MaxTransferSize) throw new InvalidDataException("Invalid file size."); break;
                    case 42: ReadField(item, ref position, 32); break;
                    case 50: ReadField(item, ref position, 512 * 3); break;
                    default: throw new InvalidDataException("Unsupported batch item fields.");
                }
            }
        }
        if (items == 0) throw new InvalidDataException("The outgoing batch is empty.");
    }
    private static ReadOnlySpan<byte> ReadField(ReadOnlySpan<byte> bytes, ref int at, int limit)
    {
        var length = ReadVarint(bytes, ref at);
        if (length > (ulong)limit || length > (ulong)(bytes.Length - at)) throw new InvalidDataException("A batch field exceeds its bounds.");
        var field = bytes.Slice(at, (int)length); at += (int)length; return field;
    }
    private static ulong ReadVarint(ReadOnlySpan<byte> bytes, ref int at)
    {
        ulong value = 0;
        for (var shift = 0; shift < 70; shift += 7)
        {
            if (at >= bytes.Length) throw new InvalidDataException("The batch encoding ended unexpectedly.");
            var next = bytes[at++];
            if (shift == 63 && (next & 0xfe) != 0) throw new InvalidDataException("The batch encoding contains an overflowing integer.");
            value |= (ulong)(next & 0x7f) << shift;
            if ((next & 0x80) == 0) return value;
        }
        throw new InvalidDataException("The batch encoding contains an invalid integer.");
    }
    public static UploadBatchHeader Header(string id, string epoch, string grant, UploadBatchManifest manifest)
    {
        var bytes = Encode(manifest);
        var header = new UploadBatchHeader { Id = id, Epoch = epoch, GrantId = grant, ItemCount = (uint)manifest.Items.Count,
            TotalBytes = Validate(manifest), EncodedLength = (uint)bytes.Length, Sha256 = ByteString.CopyFrom(SHA256.HashData(bytes)) };
        ValidateHeader(header); return header;
    }
    public static string ChildId(string peer, UploadBatchHeader header, int index, bool directory)
    {
        ValidateHeader(header);
        if (peer.Length != 64 || peer.Any(c => !char.IsAsciiHexDigitLower(c)) || index < 0 || index >= header.ItemCount) throw new InvalidDataException("Invalid batch child binding.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes("iConnect/upload-batch-child/v1\0")); hash.AppendData(Convert.FromHexString(peer));
        foreach (var value in new[] { header.Id, header.Epoch, header.GrantId }) hash.AppendData(ProtocolRules.Decode64(value));
        hash.AppendData(header.Sha256.Span);
        Span<byte> suffix = stackalloc byte[5]; BinaryPrimitives.WriteInt32BigEndian(suffix, index); suffix[4] = directory ? (byte)1 : (byte)2; hash.AppendData(suffix);
        var result = hash.GetHashAndReset(); result.AsSpan(0, 8).Clear();
        // Timestamp zero is deliberately outside ordinary new-operation freshness.
        // Batch hosts must route these IDs only through their retained parent receipt.
        return ProtocolRules.Base64Url(result);
    }
    private static UploadBatchManifest KnownFields(UploadBatchManifest source)
    {
        var result = new UploadBatchManifest { Collision = source.Collision };
        foreach (var item in source.Items) result.Items.Add(new UploadBatchItem { Parent = item.Parent, Name = item.Name, IsDirectory = item.IsDirectory,
            Size = item.Size, Sha256 = item.Sha256, Error = item.Error });
        return result;
    }
    // This protocol defines its field order and default omission explicitly.
    // Generated message serialization is not a canonical-format guarantee.
    private static byte[] CanonicalBytes(UploadBatchManifest manifest)
    {
        using var buffer = new MemoryStream(); using var output = new CodedOutputStream(buffer, leaveOpen: true);
        foreach (var item in manifest.Items)
        {
            var length = (item.Parent == 0 ? 0 : 1 + CodedOutputStream.ComputeSInt32Size(item.Parent)) +
                (item.Name.Length == 0 ? 0 : 1 + CodedOutputStream.ComputeStringSize(item.Name)) + (item.IsDirectory ? 2 : 0) +
                (item.Size == 0 ? 0 : 1 + CodedOutputStream.ComputeInt64Size(item.Size)) +
                (item.Sha256.Length == 0 ? 0 : 1 + CodedOutputStream.ComputeBytesSize(item.Sha256)) +
                (item.Error.Length == 0 ? 0 : 1 + CodedOutputStream.ComputeStringSize(item.Error));
            output.WriteTag(1, WireFormat.WireType.LengthDelimited); output.WriteLength(length);
            if (item.Parent != 0) { output.WriteTag(1, WireFormat.WireType.Varint); output.WriteSInt32(item.Parent); }
            if (item.Name.Length != 0) { output.WriteTag(2, WireFormat.WireType.LengthDelimited); output.WriteString(item.Name); }
            if (item.IsDirectory) { output.WriteTag(3, WireFormat.WireType.Varint); output.WriteBool(true); }
            if (item.Size != 0) { output.WriteTag(4, WireFormat.WireType.Varint); output.WriteInt64(item.Size); }
            if (item.Sha256.Length != 0) { output.WriteTag(5, WireFormat.WireType.LengthDelimited); output.WriteBytes(item.Sha256); }
            if (item.Error.Length != 0) { output.WriteTag(6, WireFormat.WireType.LengthDelimited); output.WriteString(item.Error); }
        }
        if (manifest.Collision != UploadCollision.UploadFail) { output.WriteTag(2, WireFormat.WireType.Varint); output.WriteEnum((int)manifest.Collision); }
        output.Flush(); return buffer.ToArray();
    }
    private static string CollisionKey(string name)
    {
        var key = new StringBuilder();
        foreach (var rune in name.EnumerateRunes()) key.Append(Rune.ToUpperInvariant(rune).ToString());
        return key.ToString();
    }
    private static bool ValidText(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i])) { if (++i >= value.Length || !char.IsLowSurrogate(value[i])) return false; }
            else if (char.IsLowSurrogate(value[i])) return false;
        }
        return true;
    }
}

/// <summary>Bounded control-message assembly; no destination effects or durable acknowledgement.</summary>
public sealed class UploadBatchAssembly : IDisposable
{
    private readonly UploadBatchHeader header;
    private readonly byte[] bytes;
    private bool disposed;
    public int Offset { get; private set; }
    public UploadBatchAssembly(UploadBatchHeader header)
    {
        UploadBatchRules.ValidateHeader(header); this.header = header.Clone(); bytes = new byte[header.EncodedLength];
    }
    public int Add(int offset, ReadOnlySpan<byte> chunk)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (offset < 0 || offset >= bytes.Length || offset % StorageRules.MaxReadRange != 0 || chunk.Length != Math.Min(StorageRules.MaxReadRange, bytes.Length - offset) || offset > Offset)
            throw new InvalidDataException("Invalid outgoing-batch chunk or sequence.");
        if (offset < Offset)
        {
            if (offset + chunk.Length > Offset || !chunk.SequenceEqual(bytes.AsSpan(offset, chunk.Length))) throw new InvalidDataException("A repeated batch chunk changed.");
            return Offset;
        }
        chunk.CopyTo(bytes.AsSpan(offset)); Offset += chunk.Length; return Offset;
    }
    public UploadBatchManifest Complete()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Offset != bytes.Length) throw new InvalidDataException("The outgoing-batch manifest is incomplete.");
        try
        {
            return UploadBatchRules.Decode(header, bytes);
        }
        catch { Dispose(); throw; }
    }
    public void Dispose() { if (disposed) return; disposed = true; CryptographicOperations.ZeroMemory(bytes); }
}
