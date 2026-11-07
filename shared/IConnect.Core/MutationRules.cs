using Google.Protobuf;
using IConnect.Protocol;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace IConnect.Core;

public static class MutationRules
{
    public const string Capability = "mutations-v1";
    public const string CreatedFoldersCapability = "created-folders-v1";
    public const long Lifetime = 24 * 60 * 60 * 1000;
    public static bool IsChange(FileOperation operation) => operation is FileOperation.CreateDirectory or FileOperation.Rename or FileOperation.Move;
    public static bool IsMutationRpc(FileOperation operation) => IsChange(operation) || operation is FileOperation.MutationStatus or FileOperation.MutationContext or FileOperation.ListMutations or FileOperation.ResolveCreatedDirectory;
    public static string NewId()
    {
        var bytes = RandomNumberGenerator.GetBytes(32); BinaryPrimitives.WriteInt64BigEndian(bytes, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return ProtocolRules.Base64Url(bytes);
    }
    public static long CreatedAt(string id)
    {
        if (!ProtocolRules.TokenPattern().IsMatch(id)) throw new InvalidDataException("Use a current file-operation identifier.");
        var bytes = ProtocolRules.Decode64(id); return bytes.Length == 32 ? BinaryPrimitives.ReadInt64BigEndian(bytes) : throw new InvalidDataException("Invalid file-operation identifier.");
    }
    public static void RequireCurrent(string id, long now)
    {
        var at = CreatedAt(id);
        if (at <= 0 || at < now - Lifetime || at > now + 5 * 60 * 1000) throw new StorageException(FileStatus.StaleReference, "This operation identifier expired or the clocks disagree. Refresh and review a new operation.");
    }
    public static string Fingerprint(FileRequest request)
    {
        var copy = request.Clone(); copy.RequestId = copy.MutationId = copy.MutationEpoch = "";
        return Convert.ToHexStringLower(SHA256.HashData(copy.ToByteArray()));
    }
    public static void Validate(MutationInfo info)
    {
        if (!ProtocolRules.TokenPattern().IsMatch(info.Id) || !(IsChange(info.Operation) || RecoveryRules.IsChange(info.Operation)) || !Enum.IsDefined(info.Phase) || !Enum.IsDefined(info.Status) ||
            info.Message.Length > 512 || info.Message.Any(char.IsControl) || info.ResultingName.Length > 255 || info.ResultingName.Any(char.IsControl) || info.RequestedName.Length is < 1 or > 255 || info.RequestedName.Any(char.IsControl) ||
            info.CreatedAt is < 0 or > StorageRules.MaxTimestamp || CreatedAt(info.Id) != info.CreatedAt ||
            info.Phase == MutationPhase.ChangeSucceeded && info.Status != FileStatus.Ok)
            throw new InvalidDataException("The file-operation receipt is invalid.");
    }
}
