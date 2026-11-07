using System.Diagnostics;
using IConnect.Core;
using IConnect.Protocol;

namespace IConnect.Storage.Windows;

public sealed record OutgoingMoveReview(string Token, string CopyId, string Source, string Destination, string FinalName, long Size);

public sealed partial class WindowsUploadSender
{
    private sealed record MoveApproval(OutgoingMoveReview Review, OutgoingCopy Copy, long Expires);
    private readonly Dictionary<string, MoveApproval> moveReviews = [];

    private static void ValidateMove(OutgoingCopy row)
    {
        if (row.MoveSource is { } source)
        {
            StorageRules.ValidateName(source.Name);
            if (source.DisplayPath.Length is < 4 or > 8192 || !char.IsAsciiLetter(source.DisplayPath[0]) ||
                source.DisplayPath[1] != ':' || source.DisplayPath[2] != '\\' || source.DisplayPath.Any(char.IsControl) ||
                !Path.GetFullPath(source.DisplayPath).Equals(source.DisplayPath, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(source.DisplayPath) != source.Name ||
                !SecureWindowsDirectory.IsLocalVolumePath(source.ParentPath) ||
                source.ParentIdentity.Length is < 1 or > 256 || source.Identity.Length is < 1 or > 256 ||
                source.ParentIdentity.Any(char.IsControl) || source.Identity.Any(char.IsControl) || !Hex(source.Version))
                throw new InvalidDataException("Invalid original file binding for a move.");
        }
        if (row.SourceRecoveryId.Length == 0)
        {
            if (row.SourceRecovered) throw new InvalidDataException("Missing source recovery transaction.");
        }
        else if (row.MoveSource is null || row.Phase != SendingPhase.Completed || !ProtocolRules.TokenPattern().IsMatch(row.SourceRecoveryId) ||
            MutationRules.CreatedAt(row.SourceRecoveryId) is < 0 or > StorageRules.MaxTimestamp)
            throw new InvalidDataException("Invalid source recovery transaction.");
    }

    private void RequirePublicMoveSource(string canonicalPath)
    {
        using var profile = SecureWindowsDirectory.OpenRoot(Path.GetDirectoryName(directory)!);
        var segments = canonicalPath[49..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (SecureWindowsDirectory.IsWithin(canonicalPath, profile.Info.CanonicalPath) ||
            StorageGrantStore.IsSensitiveSegments(segments) || StorageGrantStore.IsSystemTree(segments))
            throw new IOException("Choose an original outside private and operating-system locations.");
    }

    private SecureWindowsDirectory OpenMoveSource(OutgoingCopy row, bool mutation)
    {
        var source = row.MoveSource!;
        using var volume = SecureWindowsDirectory.OpenRoot(Path.GetPathRoot(source.DisplayPath)!);
        var segments = source.DisplayPath[3..].Split('\\');
        using var parent = SecureWindowsDirectory.OpenPath(volume.Info.CanonicalPath, volume.Info.Identity, segments[..^1], true);
        if (parent.Info.Identity != source.ParentIdentity ||
            !parent.Info.CanonicalPath.Equals(source.ParentPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The original file's parent folder changed.");
        var file = SecureWindowsDirectory.OpenPath(volume.Info.CanonicalPath, volume.Info.Identity, segments, false, mutation: mutation);
        try
        {
            RequirePublicMoveSource(file.Info.CanonicalPath);
            if (file.Info.Identity != source.Identity || file.Info.Version != source.Version || file.Info.Size != row.Size)
                throw new IOException("The original file changed after move preparation.");
            return file;
        }
        catch { file.Dispose(); throw; }
    }

    private OutgoingCopy MoveReady(string id)
    {
        var row = CompletedCopy(id);
        if (work.ContainsKey(id) || row.MoveSource is null || row.SourceRecovered)
            throw new IOException("Choose an idle completed move whose original still needs recovery.");
        return row;
    }
    private static void RequireNoRecoveryRecord(OutgoingCopy row, WindowsLocalManagement management)
    {
        if (row.SourceRecoveryId.Length != 0 && management.HasSourceRecovery(row.SourceRecoveryId))
            throw new IOException("This move already has a source recovery record. Review it under local saved items.");
    }

    public OutgoingMoveReview ReviewMove(string id, WindowsLocalManagement management)
    {
        OutgoingCopy row;
        lock (gate) row = MoveReady(id);
        RequireNoRecoveryRecord(row, management);
        lock (gate)
        {
            if (MoveReady(id) != row) throw new IOException("The move copy changed. Review it again.");
            RequireDurableMove(row);
            using var original = OpenMoveSource(row, mutation: false);
            var value = new OutgoingMoveReview(ProtocolRules.NewToken(), id, row.MoveSource!.DisplayPath, row.Destination, row.FinalName, row.Size);
            moveReviews[id] = new(value, row, Environment.TickCount64 + 5 * 60_000);
            return value;
        }
    }

    public Task<MutationInfo> FinishMoveAsync(OutgoingMoveReview value, WindowsLocalManagement management,
        Request request, Func<bool> sessionCurrent, CancellationToken ct = default)
    {
        lock (gate)
        {
            if (!moveReviews.Remove(value.CopyId, out var approval) || approval.Review != value || approval.Expires < Environment.TickCount64)
                throw new IOException("This move review expired or was used. Review the original and published copy again.");
            var row = MoveReady(value.CopyId);
            if (row != approval.Copy) throw new IOException("The move copy changed after review.");
            return StartReviewed(row.Id, ct, async token =>
            {
                Current();
                RequireNoRecoveryRecord(row, management);
                RequireDurableMove(row);
                // The original stays under this same write- and delete-denying handle until the recovery rename.
                using var original = OpenMoveSource(row, mutation: true);
                WindowsDownloadManager.VerifyFile(original, row.Size, row.Hash, token);
                Current();
                await RequirePublishedProofAsync(row, request, token);
                var checkedAt = Stopwatch.GetTimestamp();
                Current();
                RequireDurableMove(row);
                var pending = Put(row with { SourceRecoveryId = MutationRules.NewId(),
                    Message = "Source recovery was requested. Review local saved items for its outcome." });
                var result = management.RecoverVerifiedOutgoingFile(row.MoveSource!, original, pending.SourceRecoveryId,
                    () =>
                    {
                        Current();
                        if (Stopwatch.GetElapsedTime(checkedAt) >= TimeSpan.FromSeconds(10))
                            throw new IOException("The published-copy check expired before source recovery.");
                    }, token);
                if (result.Phase == MutationPhase.ChangeSucceeded)
                    Put(pending with { SourceRecovered = true, Message = "The published copy was verified and the original is in local recovery." });
                return result;

                void Current()
                {
                    token.ThrowIfCancellationRequested();
                    RequireAuthority(row);
                    if (!sessionCurrent()) throw new IOException("The authenticated receiving session changed. The original was kept.");
                }
            });
        }
    }

    private void RequireDurableMove(OutgoingCopy row)
    {
        lock (gate)
        {
            if (File.Exists(path + ".new") || Directory.Exists(path + ".new") || File.Exists(path + ".bak") || Directory.Exists(path + ".bak") ||
                StateRecovery.Read<OutgoingCopy[]>(path, 2 * 1024 * 1024).SingleOrDefault(x => x.Id == row.Id) != row)
                throw new IOException("The saved move progress changed. Reopen and review it.");
        }
    }
}
