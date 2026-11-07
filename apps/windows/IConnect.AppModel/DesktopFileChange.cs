using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;

namespace IConnect.AppModel;

/// <summary>One existing intent, with no automatic retry after dispatch.</summary>
internal static class DesktopFileChange
{
    internal static async Task<FileResponse> ExecuteAsync(FileRequest intent, string peer,
        Func<FileRequest, CancellationToken, Task<FileResponse>> request, Action<WindowsActivityEvent> activity, CancellationToken token)
    {
        if (!(MutationRules.IsChange(intent.Operation) || RecoveryRules.IsChange(intent.Operation))) throw new ArgumentException("Choose a supported file change.", nameof(intent));
        var sent = false; var finished = false;
        void Record(WindowsActivityOutcome outcome)
        { try { activity(new WindowsActivityEvent(WindowsActivityKind.FileChange, outcome, peer, Operation: intent.Operation).Sanitized()); } catch (Exception) { } }
        void Finish(WindowsActivityOutcome outcome) { if (!finished) { finished = true; Record(outcome); } }
        Record(WindowsActivityOutcome.Requested);
        try
        {
            var context = await request(new() { Operation = RecoveryRules.IsChange(intent.Operation) ? FileOperation.RecoveryContext : FileOperation.MutationContext }, token);
            if (context.Status != FileStatus.Ok)
            {
                var refused = Outcome(context, null); Finish(refused);
                if (refused == WindowsActivityOutcome.Review) throw new IOException("The change context returned an unexpected receipt. Review before repeating this action.");
                return context;
            }
            if (!ProtocolRules.TokenPattern().IsMatch(context.MutationEpoch)) throw new InvalidDataException("The change context is invalid. Refresh before trying again.");
            token.ThrowIfCancellationRequested();
            var dispatched = intent.Clone(); dispatched.MutationId = MutationRules.NewId(); dispatched.MutationEpoch = context.MutationEpoch;
            sent = true;
            var reply = await request(dispatched, token);
            var outcome = Outcome(reply, dispatched); Finish(outcome);
            if (outcome == WindowsActivityOutcome.Review) throw new IOException("The change did not return a confirmed matching receipt. Review recent changes before repeating it.");
            return reply;
        }
        catch (Exception error) { Finish(sent ? WindowsActivityOutcome.Review : error is OperationCanceledException ? WindowsActivityOutcome.Canceled : WindowsActivityOutcome.Failed); throw; }
    }
    private static WindowsActivityOutcome Outcome(FileResponse reply, FileRequest? dispatched)
    {
        var receipt = reply.Mutation; var valid = false;
        if (receipt is not null && dispatched is not null && receipt.Id == dispatched.MutationId && receipt.Operation == dispatched.Operation && receipt.Status == reply.Status &&
            (dispatched.Name.Length == 0 || receipt.RequestedName == dispatched.Name))
            try { MutationRules.Validate(receipt); valid = true; } catch (InvalidDataException) { }
        if (receipt is not null && !valid) return WindowsActivityOutcome.Review;
        if (valid && receipt!.Phase == MutationPhase.ChangeSucceeded && reply.Status == FileStatus.Ok) return WindowsActivityOutcome.Completed;
        if (receipt is { Phase: MutationPhase.ChangePending or MutationPhase.ChangeReview }) return WindowsActivityOutcome.Review;
        if (reply.Status is FileStatus.AccessDenied or FileStatus.Unsupported) return WindowsActivityOutcome.Refused;
        if (reply.Status != FileStatus.Ok || receipt is { Phase: MutationPhase.ChangeFailed }) return WindowsActivityOutcome.Failed;
        return WindowsActivityOutcome.Review;
    }
}
