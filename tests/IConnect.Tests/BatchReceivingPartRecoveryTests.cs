using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using Xunit;

namespace IConnect.Tests;

public sealed partial class BatchPlanAdmissionTests
{
    [Fact]
    public async Task Scoped_receipts_protect_actual_parts_and_only_archived_history_makes_them_candidates_for_owner_review()
    {
        await using var f = new Fixture(checks: new AcceptedFiles()); var bytes = "actual batch part"u8.ToArray(); var h = await ReadyDirectories(f, FilePlan(bytes));
        await f.Call(ChildRequest(f, h, 0)); await f.Call(ChildRequest(f, h, 1, FileOperation.PrepareBatchFile)); await WriteFile(f, h, bytes);
        var target = Path.Combine(f.Destination, "folder"); var part = Assert.Single(Directory.GetFiles(target, "*.part"));
        var token = ProtocolRules.NewToken(); var candidate = Path.Combine(target, ".iconnect-transfer-" + token + ".part"); File.WriteAllText(candidate, "generated unidentified data");
        await using var files = new WindowsDownloadManager(f.Profile); await using var folders = new WindowsBatchDownloads(f.Profile, files);
        var owner = new string('b', 64) + "." + ProtocolRules.NewToken(); using var manager = new WindowsReceivingPartRecovery(f.Grants, files, folders, () => owner, () => true);
        var selected = manager.Select(target); Assert.Equal(1, selected.Files); Assert.Equal(Path.GetFileName(candidate), Assert.Single(manager.Inspect(selected.Id, 0)).Name);
        Assert.True(manager.ConfirmRemoval(manager.ReviewRemoval(selected.Id))); Assert.False(File.Exists(candidate)); Assert.Equal(bytes, File.ReadAllBytes(part));
        f.Grants.BatchPlans.ArchiveHistory(); selected = manager.Select(target); Assert.Equal(1, selected.Files);
        Assert.True(manager.ConfirmRemoval(manager.ReviewRemoval(selected.Id))); Assert.False(File.Exists(part)); Assert.Equal(1, f.Grants.BatchPlans.PreservedRecords);
        Assert.True(Directory.Exists(target)); Assert.Empty(f.Grants.Uploads.All);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Changed_or_uncommitted_scoped_journals_end_cleanup_permission_and_keep_both_private_files(bool sidecar)
    {
        await using var f = new Fixture(checks: new AcceptedFiles()); var bytes = "actual batch part"u8.ToArray(); var h = await ReadyDirectories(f, FilePlan(bytes));
        await f.Call(ChildRequest(f, h, 0)); await f.Call(ChildRequest(f, h, 1, FileOperation.PrepareBatchFile)); await WriteFile(f, h, bytes);
        var target = Path.Combine(f.Destination, "folder"); var part = Assert.Single(Directory.GetFiles(target, "*.part"));
        var candidate = Path.Combine(target, ".iconnect-transfer-" + ProtocolRules.NewToken() + ".part"); File.WriteAllText(candidate, "generated candidate");
        await using var files = new WindowsDownloadManager(f.Profile); await using var folders = new WindowsBatchDownloads(f.Profile, files);
        var owner = new string('b', 64) + "." + ProtocolRules.NewToken(); using var manager = new WindowsReceivingPartRecovery(f.Grants, files, folders, () => owner, () => true);
        var selection = manager.Select(target); var review = manager.ReviewRemoval(selection.Id);
        var journal = Assert.Single(Directory.GetFiles(Path.Combine(f.Profile, "incoming-batch-plans"), "*.upload.json"));
        if (sidecar) journal += ".new"; File.AppendAllText(journal, "generated changed scoped metadata");
        Assert.ThrowsAny<Exception>(() => manager.ConfirmRemoval(review)); Assert.True(File.Exists(candidate)); Assert.Equal(bytes, File.ReadAllBytes(part));
        Assert.Null(manager.Progress); Assert.ThrowsAny<Exception>(() => manager.Inspect(selection.Id, 0));
    }
    [Fact]
    public async Task A_published_file_renamed_to_an_unrelated_partial_name_is_protected_by_its_native_identity()
    {
        await using var f = new Fixture(checks: new AcceptedFiles()); var bytes = "actual published file"u8.ToArray(); var h = await ReadyDirectories(f, FilePlan(bytes));
        await f.Call(ChildRequest(f, h, 0)); await f.Call(ChildRequest(f, h, 1, FileOperation.PrepareBatchFile)); await WriteFile(f, h, bytes);
        var done = await FinishFile(f, h); Assert.Equal(UploadPhase.UploadCompleted, done.Phase); var target = Path.Combine(f.Destination, "folder");
        var disguised = Path.Combine(target, ".iconnect-transfer-" + ProtocolRules.NewToken() + ".part"); File.Move(Path.Combine(target, done.FinalName), disguised);
        var candidate = Path.Combine(target, ".iconnect-transfer-" + ProtocolRules.NewToken() + ".part"); File.WriteAllText(candidate, "generated candidate");
        await using var files = new WindowsDownloadManager(f.Profile); await using var folders = new WindowsBatchDownloads(f.Profile, files);
        var owner = new string('b', 64) + "." + ProtocolRules.NewToken(); using var manager = new WindowsReceivingPartRecovery(f.Grants, files, folders, () => owner, () => true);
        var selection = manager.Select(target); Assert.Equal(1, selection.Files); Assert.True(manager.ConfirmRemoval(manager.ReviewRemoval(selection.Id)));
        Assert.False(File.Exists(candidate)); Assert.Equal(bytes, File.ReadAllBytes(disguised));
    }
}
