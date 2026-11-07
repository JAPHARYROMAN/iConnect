using IConnect.Protocol;

namespace IConnect.Core;

/// <summary>Persist this intent before creation. Recovery uses its receipt, never a name-based directory search.</summary>
public sealed record CreatedDirectoryReceipt(string Id, string Epoch, string GrantId, string RequestedName);

public static class CreatedDirectoryReferences
{
    public static void Validate(CreatedDirectoryReceipt receipt)
    {
        if (!ProtocolRules.TokenPattern().IsMatch(receipt.Id) || !ProtocolRules.TokenPattern().IsMatch(receipt.Epoch) || !ProtocolRules.TokenPattern().IsMatch(receipt.GrantId))
            throw new InvalidDataException("Invalid created-folder receipt binding.");
        StorageRules.ValidateName(receipt.RequestedName);
        if (MutationRules.CreatedAt(receipt.Id) is < 0 or > StorageRules.MaxTimestamp) throw new InvalidDataException("Invalid created-folder receipt time.");
    }
    public static FileEntry Check(CreatedDirectoryReceipt receipt, FileResponse response)
    {
        Validate(receipt); StorageRules.ValidateResponse(response);
        if (response.Status != FileStatus.Ok) throw new StorageException(response.Status, response.Message.Length == 0 ? "The created folder needs review." : response.Message);
        var mutation = response.Mutation; var entry = response.Entries.Count == 1 ? response.Entries[0] : null;
        if (response.MutationEpoch != receipt.Epoch || mutation is null || mutation.Id != receipt.Id || mutation.Operation != FileOperation.CreateDirectory ||
            mutation.Phase != MutationPhase.ChangeSucceeded || mutation.Status != FileStatus.Ok || mutation.RequestedName != receipt.RequestedName ||
            entry is null || !entry.IsDirectory || entry.IsRoot || entry.ParentId.Length == 0 || entry.ParentId == entry.Id || entry.GrantId != receipt.GrantId || entry.Name != mutation.ResultingName || (entry.Permissions & (uint)StoragePermission.Create) == 0)
            throw new InvalidDataException("The receiver changed the created-folder receipt or reference. Review the destination.");
        return entry;
    }
    public static async Task<FileEntry> ResolveAsync(CreatedDirectoryReceipt receipt, TransferManifests.Request request, CancellationToken cancellation)
    {
        Validate(receipt);
        return Check(receipt, await request(new() { Operation = FileOperation.ResolveCreatedDirectory, MutationId = receipt.Id, MutationEpoch = receipt.Epoch }, cancellation));
    }
}
