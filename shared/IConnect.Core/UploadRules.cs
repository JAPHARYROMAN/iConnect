using IConnect.Protocol;

namespace IConnect.Core;

public static class UploadRules
{
    public const string Capability = "uploads-v1";
    public const string PublishedCheckCapability = "published-upload-check-v1";
    public const long PeerQuota = 16L * 1024 * 1024 * 1024;
    public const long TotalQuota = PeerQuota * 4;
    public static bool IsRpc(FileOperation operation) =>
        operation is >= FileOperation.UploadContext and <= FileOperation.ListUploads or FileOperation.VerifyPublishedUpload;
    public static bool IsTerminal(UploadPhase phase) => phase is UploadPhase.UploadCompleted or UploadPhase.UploadCanceled or UploadPhase.UploadSkipped;
    public static void Validate(UploadInfo info)
    {
        try { StorageRules.ValidateName(info.Name); }
        catch (StorageException ex) { throw new InvalidDataException("Unsupported incoming-copy name.", ex); }
        if (!ProtocolRules.TokenPattern().IsMatch(info.Id) || MutationRules.CreatedAt(info.Id) is < 0 or > StorageRules.MaxTimestamp ||
            info.Size is < 0 or > StorageRules.MaxTransferSize || info.Offset < 0 || info.Offset > info.Size || info.Sha256.Length != 32 ||
            !Enum.IsDefined(info.Phase) || !Enum.IsDefined(info.Collision) || info.Message.Length > 512 || info.Message.Any(char.IsControl) ||
            info.FinalName.Length != 0 && info.Phase is not (UploadPhase.UploadFinalizing or UploadPhase.UploadCompleted or UploadPhase.UploadReview) ||
            info.Phase == UploadPhase.UploadCompleted && (info.Offset != info.Size || info.FinalName.Length == 0))
            throw new InvalidDataException("Invalid incoming-transfer descriptor.");
        if (info.FinalName.Length != 0) { try { StorageRules.ValidateName(info.FinalName); } catch (StorageException ex) { throw new InvalidDataException("Unsupported incoming-copy destination name.", ex); } }
    }
}
