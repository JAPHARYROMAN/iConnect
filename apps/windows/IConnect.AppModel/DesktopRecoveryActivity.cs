using IConnect.Storage.Windows;

namespace IConnect.AppModel;

public sealed partial class DesktopController
{
    private T ObserveRecovery<T>(WindowsActivityKind kind, Func<T> action)
    {
        try { var result = action(); activityWriter.Record(new(kind, result is bool done && !done ? WindowsActivityOutcome.Review : WindowsActivityOutcome.Completed)); return result; }
        catch (Exception) { activityWriter.Record(new(kind, WindowsActivityOutcome.Review)); throw; }
    }
    private void ObserveRecovery(WindowsActivityKind kind, Action action) => ObserveRecovery(kind, () => { action(); return true; });
    private async Task<T> ObserveRecoveryAsync<T>(WindowsActivityKind kind, Func<Task<T>> action)
    {
        try { var result = await action(); activityWriter.Record(new(kind, result is bool done && !done ? WindowsActivityOutcome.Review : WindowsActivityOutcome.Completed)); return result; }
        catch (Exception) { activityWriter.Record(new(kind, WindowsActivityOutcome.Review)); throw; }
    }
    private Task ObserveRecoveryAsync(WindowsActivityKind kind, Func<Task> action) => ObserveRecoveryAsync(kind, async () => { await action(); return true; });
    private static WindowsActivityKind HistoryActivity(BatchHistoryAction action) => action switch
    { BatchHistoryAction.Archive => WindowsActivityKind.SenderHistoryArchive, BatchHistoryAction.Restore => WindowsActivityKind.SenderHistoryRestore, _ => WindowsActivityKind.SenderHistoryPurge };
}
