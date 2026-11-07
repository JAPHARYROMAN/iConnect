using IConnect.Core;
using IConnect.Protocol;

namespace IConnect.Storage.Windows;

public sealed record LocalMoveDestination(string Directory, string CanonicalPath, string Identity, bool AcrossVolumes);
public sealed record LocalMoveCompletion(string Id, string CopyId, string Source, string Destination, long Size);

public sealed partial class WindowsLocalCopies
{
    private sealed record MoveTarget(string Directory, string CanonicalPath, string Identity, string Name);
    private sealed record MoveReview(LocalMoveCompletion Value, DownloadJob Copy, long Expires);
    private readonly Dictionary<string, MoveReview> moveReviews = [];
    private readonly HashSet<string> finishingMoves = [];

    public string PrepareVerifiedMove(LocalCopySelection selection, LocalMoveDestination destination, string name)
    {
        if (selection.IsDirectory || selection.Segments.Length == 0)
            throw new IOException("Select a file inside a local folder for a verified move.");
        StorageRules.ValidateName(name);
        using var target = OpenMoveDestination(new(destination.Directory, destination.CanonicalPath, destination.Identity, name));
        using var original = SecureWindowsDirectory.OpenPath(selection.Grant.CanonicalPath, selection.Grant.RootIdentity, selection.Segments, false);
        if (original.Info.Identity != selection.Identity || original.Info.Version != selection.Version || original.Info.Size != selection.Size)
            throw new IOException("The original changed. Refresh and review it again.");
        RequireOutsidePrivateProfile(original.Info.CanonicalPath);
        return Prepare(selection, new(destination.Directory, destination.CanonicalPath, destination.Identity, name));
    }

    public string StartVerifiedMoveCopy(string id)
    {
        lock (gate)
        {
            Healthy();
            var source = sources[id];
            RequireRecord(source);
            var target = source.Move ?? throw new IOException("This copy has no reviewed move destination.");
            if (receiver.Jobs.Any(x => x.Id == id)) throw new IOException("Use the recorded progress for this move.");
            using var destination = OpenMoveDestination(target);
            return StartCopy(source, target.Directory, DownloadCollision.Fail, target.Identity);
        }
    }

    public LocalMoveCompletion ReviewMoveCompletion(string id)
    {
        lock (gate)
        {
            var (source, copy) = MoveReady(id);
            using var original = Open(source);
            var value = new LocalMoveCompletion(ProtocolRules.NewToken(), id, source.Display, Path.Combine(copy.DisplayPath, copy.FinalName), copy.Size);
            moveReviews[id] = new(value, copy, Environment.TickCount64 + 5 * 60_000);
            return value;
        }
    }

    public bool FinishingMove(string id) { lock (gate) return finishingMoves.Contains(id); }

    public MutationInfo FinishVerifiedMove(LocalMoveCompletion value, WindowsLocalManagement management, CancellationToken cancellation = default)
    {
        Source source;
        DownloadJob copy;
        string sourceRecordVersion;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (!moveReviews.Remove(value.CopyId, out var review) || review.Value != value || review.Expires < Environment.TickCount64)
                throw new IOException("This move review expired or was used. Review the original and destination again.");
            (source, copy) = MoveReady(value.CopyId);
            if (review.Copy != copy) throw new IOException("The copy changed. Review its current receipt.");
            sourceRecordVersion = versions[source.Id];
            finishingMoves.Add(source.Id);
            controls.Add(finished.Task);
        }
        Notify();
        try
        {
            Current();
            var name = source.Id + ".json";
            using var sourceRecord = sourceRoot.OpenChild(name);
            using var copyRecord = downloadsRoot.OpenChild(name);
            RequireSettledRecord(sourceRecord);
            RequireSettledRecord(copyRecord);
            if (sourceRecord.Info.Version != sourceRecordVersion || StateRecovery.Read<DownloadJob>(copyRecord.Info.CanonicalPath, 256 * 1024) != copy)
                throw new IOException("The saved move progress changed. Restart and review the records.");

            using var destination = OpenMoveDestination(source.Move!);
            using var published = destination.OpenChild(copy.FinalName);
            if (published.Info.Identity != copy.TemporaryIdentity || published.Info.Identity == source.Identity &&
                SameVolume(published.Info.CanonicalPath, source.Root))
                throw new IOException("The recorded destination file changed.");
            WindowsDownloadManager.VerifyFile(published, copy, cancellation);
            Current();
            return management.RecoverVerifiedOriginal(source.Root, source.RootIdentity, source.Segments, source.Display,
                source.Identity, copy, () =>
                {
                    Current();
                    RequireSettledRecord(sourceRecord);
                    RequireSettledRecord(copyRecord);
                }, cancellation);

            void Current()
            {
                cancellation.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(Volatile.Read(ref closed), this);
            }
        }
        finally
        {
            lock (gate) { finishingMoves.Remove(source.Id); controls.Remove(finished.Task); finished.SetResult(); }
            Notify();
        }
    }

    private (Source Source, DownloadJob Copy) MoveReady(string id)
    {
        Healthy();
        if (Active(id) || !sources.TryGetValue(id, out var source) || source.Move is not { } target)
            throw new IOException("Choose a completed, idle move copy.");
        RequireRecord(source);
        var copy = receiver.Jobs.SingleOrDefault(x => x.Id == id);
        if (copy is null || copy.Phase != DownloadPhase.Completed || copy.SecurityPolicy != ReceivedFileSecurity.PolicyVersion ||
            copy.SourceNode != id || copy.Ticket != id || copy.SourceVersion != source.Version || copy.Size != source.Size ||
            copy.Offset != copy.Size || copy.Hash.Length != 64 || copy.TemporaryIdentity.Length == 0 || copy.Name != target.Name ||
            copy.FinalName != target.Name || copy.RootIdentity != target.Identity || copy.RootPath != target.CanonicalPath ||
            copy.DisplayPath != target.Directory || copy.Collision != DownloadCollision.Fail)
            throw new IOException("Finish and verify the recorded destination copy before reviewing the original.");
        return (source, copy);
    }

    private void RequireIdleMove(string id)
    {
        if (finishingMoves.Contains(id)) throw new IOException("Wait for the current move verification to finish.");
    }

    private SecureWindowsDirectory OpenMoveDestination(MoveTarget target)
    {
        using var selected = StorageGrantStore.OpenLocalDestination(target.Directory);
        if (selected.Info.Identity != target.Identity || selected.Info.CanonicalPath != target.CanonicalPath)
            throw new IOException("The selected move destination changed. Review a fresh destination.");
        RequireOutsidePrivateProfile(selected.Info.CanonicalPath);
        using var volume = SecureWindowsDirectory.OpenRoot(target.CanonicalPath[..49]);
        var parts = target.CanonicalPath[49..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var directory = SecureWindowsDirectory.OpenPath(volume.Info.CanonicalPath, volume.Info.Identity, parts, true);
        if (directory.Info.Identity == target.Identity && directory.Info.CanonicalPath == target.CanonicalPath) return directory;
        directory.Dispose();
        throw new IOException("The selected move destination changed. Review a fresh destination.");
    }

    private void RequireOutsidePrivateProfile(string path)
    {
        var profile = root.Info.CanonicalPath[..root.Info.CanonicalPath.LastIndexOf('\\')];
        if (SecureWindowsDirectory.IsWithin(path, profile)) throw new IOException("Choose a location outside private iConnect storage.");
    }

    private static void RequireSettledRecord(SecureWindowsDirectory record)
    {
        var path = record.Info.CanonicalPath;
        if (File.Exists(path + ".new") || File.Exists(path + ".bak") || Directory.Exists(path + ".new") || Directory.Exists(path + ".bak"))
            throw new IOException("The saved move has ambiguous progress. Restart and review its records.");
    }

    internal static bool SameVolume(string first, string second) => first.AsSpan(0, 49).Equals(second.AsSpan(0, 49), StringComparison.OrdinalIgnoreCase);
}
