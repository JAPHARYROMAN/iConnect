namespace IConnect.AppModel;

public sealed partial class DesktopController
{
    private readonly LocalActivityBackups activityBackups;
    public Task<ActivityBackupInventory> InspectActivityBackups(int start = 0, Func<bool>? continuing = null) =>
        Task.Run(() => activityBackups.Inspect(start, continuing));
    public Task<ActivityBackupRemovalReview> ReviewActivityBackupRemoval(string backup, Func<bool>? continuing = null) =>
        Task.Run(() => activityBackups.Review(backup, continuing));
    public Task ConfirmActivityBackupRemoval(ActivityBackupRemovalReview review, Func<bool>? continuing = null) => Task.Run(() =>
    {
        try { activityBackups.Confirm(review, continuing); }
        finally { Notify(DesktopChange.History); }
    });
    public void CancelActivityBackupReview() => activityBackups.CancelReview();
}
