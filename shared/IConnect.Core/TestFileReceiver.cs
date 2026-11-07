using System.Security.Cryptography;
using IConnect.Protocol;

namespace IConnect.Core;

// Phase 0 only accepts a bounded synthetic file, never a remote filesystem path.
public sealed class TestFileReceiver : IDisposable
{
    private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly Stream destination;
    private readonly TransferStart start;
    private long offset;
    private bool finished;

    public TestFileReceiver(TransferStart start, Stream destination)
    {
        if (!ProtocolRules.TokenPattern().IsMatch(start.TransferId) || start.Size is < 0 or > ProtocolRules.MaxTestFileBytes ||
            start.Sha256.Length != 32 || start.Name != "iconnect-test.bin")
            throw new InvalidDataException("Only a bounded test file is accepted in Phase 0.");
        this.start = start;
        this.destination = destination;
    }

    public long Append(TransferChunk chunk)
    {
        if (finished || chunk.TransferId != start.TransferId || chunk.Offset != offset ||
            chunk.Data.Length is < 1 or > ProtocolRules.ChunkBytes || offset + chunk.Data.Length > start.Size)
            throw new InvalidDataException("Invalid or out-of-order file chunk.");
        var data = chunk.Data.Span;
        destination.Write(data);
        hash.AppendData(data);
        return offset += data.Length;
    }

    public byte[] Finish(string transferId)
    {
        if (finished || transferId != start.TransferId || offset != start.Size)
            throw new InvalidDataException("Transfer is incomplete.");
        finished = true;
        var digest = hash.GetHashAndReset();
        if (!CryptographicOperations.FixedTimeEquals(digest, start.Sha256.Span))
            throw new InvalidDataException("File checksum mismatch.");
        destination.Flush();
        return digest;
    }

    public void Dispose() => hash.Dispose();
}
