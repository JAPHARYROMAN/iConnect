using System.Security.Cryptography;
using System.Text;
using IConnect.Protocol;

namespace IConnect.Core;

[Flags]
public enum StoragePermission : uint
{
    None = 0, List = 1, Read = 2, Write = 4, Create = 8, Rename = 16,
    Move = 32, Delete = 64, Replace = 128,
    ReadOnly = List | Read,
    Contribute = ReadOnly | Write | Create,
    Manage = Contribute | Rename | Move | Delete | Replace
}

public sealed class StorageException(FileStatus status, string message) : IOException(message)
{
    public FileStatus Status { get; } = status;
    public int? NativeError { get; init; }
}

public interface IFileService : IDisposable
{
    Task<FileResponse> ExecuteAsync(string peerId, FileRequest request, CancellationToken cancellationToken);
}

public static class StorageRules
{
    public const int PageSize = 32;
    public const int MaxReadRange = 32 * 1024;
    public const int MaxIssuedNodes = 16_384;
    public const long MinTimestamp = -62_135_596_800_000;
    public const long MaxTimestamp = 253_402_300_799_999;
    public const long MaxTransferSize = 8L * 1024 * 1024 * 1024 * 1024;
    public const string DownloadCapability = "downloads-v1";

    public static void ValidateName(string name)
    {
        if (name.Length is < 1 or > 255 || name is "." or ".." || name.EndsWith(' ') || name.EndsWith('.') ||
            name.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c)) ||
            name.StartsWith(".iconnect-", StringComparison.OrdinalIgnoreCase))
            throw new StorageException(FileStatus.InvalidRequest, "Choose a normal file or folder name.");
        try { _ = new UTF8Encoding(false, true).GetBytes(name); }
        catch (EncoderFallbackException) { throw new StorageException(FileStatus.Unsupported, "The name contains unsupported characters."); }
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) &&
                (stem[3] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3')))
            throw new StorageException(FileStatus.InvalidRequest, "Reserved device names are unavailable.");
    }

    public static void Require(StoragePermission granted, StoragePermission required)
    {
        if ((granted & required) != required)
            throw new StorageException(FileStatus.AccessDenied, "This device does not have permission for this action.");
    }

    public static void ValidateResponse(FileResponse response)
    {
        if (response.Batch is not null) UploadBatchRules.ValidateReceipt(response.Batch);
        if (response.BatchItem is not null) UploadBatchRules.ValidateItemReceipt(response.BatchItem);
        if (response.Upload is not null) UploadRules.Validate(response.Upload);
        if (response.PublishedUploadCheck is { } check &&
            (!ProtocolRules.TokenPattern().IsMatch(check.Challenge) || response.Upload?.Phase != UploadPhase.UploadCompleted))
            throw new InvalidDataException("Invalid published-copy check.");
        if (response.PublishedBatchCheck is { } batchCheck &&
            (response.Status != FileStatus.Ok || !UploadBatchRules.CanonicalToken(batchCheck.Challenge) || response.Batch is not { Phase: UploadBatchPlanPhase.UploadPlanCompleted } batch ||
             batch.ItemOutcomes.Length == 0 || batch.ItemOutcomes.Any(x => x != 1) || response.MutationEpoch != batch.Header.Epoch))
            throw new InvalidDataException("Invalid published-batch check.");
        if (response.Uploads.Count > PageSize) throw new InvalidDataException("The incoming-transfer response is too large.");
        foreach (var upload in response.Uploads) UploadRules.Validate(upload);
        if (response.MutationEpoch.Length != 0 && !ProtocolRules.TokenPattern().IsMatch(response.MutationEpoch) || response.Mutations.Count > 32)
            throw new InvalidDataException("The file-operation response is invalid.");
        if (response.Mutation is not null) MutationRules.Validate(response.Mutation);
        foreach (var mutation in response.Mutations) MutationRules.Validate(mutation);
        if (response.DeletedItems.Count > 32) throw new InvalidDataException("The recovery response is too large.");
        foreach (var item in response.DeletedItems)
        {
            if (!ProtocolRules.TokenPattern().IsMatch(item.Id) || item.Name.Length is < 1 or > 255 || item.Name.Any(char.IsControl) ||
                item.Size < 0 || item.DeletedAt is < 0 or > MaxTimestamp || MutationRules.CreatedAt(item.Id) != item.DeletedAt || !Enum.IsDefined(item.State) ||
                item.Message.Length > 512 || item.Message.Any(char.IsControl) || item.RestoredName.Length > 255 || item.RestoredName.Any(char.IsControl) || item.CanRestore && item.State != DeletedState.DeletedStored)
                throw new InvalidDataException("The recovery item is invalid.");
        }
        if (!ProtocolRules.TokenPattern().IsMatch(response.RequestId) || !Enum.IsDefined(response.Status) ||
            response.Message.Length > 512 || response.Message.Any(char.IsControl) ||
            response.Entries.Count > PageSize || response.Content.Length > MaxReadRange ||
            (response.Continuation.Length != 0 && !ProtocolRules.TokenPattern().IsMatch(response.Continuation)))
            throw new InvalidDataException("The remote file response is invalid.");
        foreach (var entry in response.Entries)
        {
            if (!ProtocolRules.TokenPattern().IsMatch(entry.Id) || !ProtocolRules.TokenPattern().IsMatch(entry.GrantId) ||
                (entry.ParentId.Length != 0 && !ProtocolRules.TokenPattern().IsMatch(entry.ParentId)) ||
                entry.Name.Length is < 1 or > 255 || entry.Name.Any(char.IsControl) || entry.Size < 0 ||
                entry.ModifiedAt is < MinTimestamp or > MaxTimestamp ||
                entry.Version.Length != 64 || entry.Version.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
                (entry.Permissions & ~(uint)StoragePermission.Manage) != 0 ||
                (entry.IsRoot && (!entry.IsDirectory || entry.ParentId.Length != 0)))
                throw new InvalidDataException("The remote file metadata is invalid.");
        }
        if (response.Download is { } download)
        {
            if (!ProtocolRules.TokenPattern().IsMatch(download.Id) || download.Size is < 0 or > MaxTransferSize ||
                download.Version.Length != 64 || download.Version.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
                !Enum.IsDefined(download.State) || download.ExpiresAt is < 0 or > MaxTimestamp ||
                download.PreparedBytes < 0 || download.PreparedBytes > download.Size ||
                (download.State == DownloadState.DownloadReady ? download.Sha256.Length != 32 : download.Sha256.Length != 0))
                throw new InvalidDataException("The remote download descriptor is invalid.");
            try { ValidateName(download.Name); }
            catch (StorageException) { throw new InvalidDataException("The remote download name is unsupported."); }
        }
    }

    public static string Version(string fileIdentity, long size, long modified) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{fileIdentity}:{size}:{modified}")));
}
