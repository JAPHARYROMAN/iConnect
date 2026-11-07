using Google.Protobuf;
using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using System.Runtime.InteropServices;
using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace IConnect.Tests;

public sealed partial class BatchPlanAdmissionTests
{
    private static async Task<(UploadBatchHeader Header, string Folder, string File)> PublishBatchForCheck(Fixture f, bool blind = false)
    {
        if (blind) f.Grants.Update(f.Grant.Id, StoragePermission.Write | StoragePermission.Create, [], null, true);
        // The root collision forces receipt-based paths for ordinary as well as blind reception.
        Directory.CreateDirectory(Path.Combine(f.Destination, "folder"));
        var bytes = new byte[] { 7, 8, 9 };
        var manifest = FilePlan(bytes);
        manifest.Items.Add(new UploadBatchItem { Parent = 0, Name = "empty", IsDirectory = true });
        var h = await ReadyDirectories(f, manifest);
        var folder = (await f.Call(ChildRequest(f, h, 0))).BatchItem.ResultingName;
        await f.Call(ChildRequest(f, h, 1, FileOperation.PrepareBatchFile)); await WriteFile(f, h, bytes);
        var file = await FinishFile(f, h);
        await f.Call(ChildRequest(f, h, 2));
        var done = (await f.Call(f.Request(h, FileOperation.FinishUploadBatch))).Batch;
        Assert.All(done.ItemOutcomes, x => Assert.Equal((byte)1, x));
        return (h, folder, file.FinalName);
    }
    private static FileRequest BatchCheckRequest(Fixture f, UploadBatchHeader h)
    {
        var request = f.Request(h, FileOperation.VerifyPublishedBatch);
        request.BatchHeader = h.Clone(); request.MutationId = ProtocolRules.NewToken(); return request;
    }
    private static async Task<FileResponse> CollectBatchCheck(Fixture f, FileRequest request)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var response = await f.Raw(request);
            if (response.Status != FileStatus.Ok || response.PublishedBatchCheck.Verified) return response;
            await Task.Delay(20, timeout.Token);
        }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Published_batch_check_uses_actual_saved_names_and_preserves_all_receipts(bool blind)
    {
        await using var f = new Fixture(checks: new AcceptedFiles());
        var saved = await PublishBatchForCheck(f, blind);
        var history = Path.Combine(f.Profile, "incoming-batch-plans");
        var before = Directory.GetFiles(history).ToDictionary(x => Path.GetFileName(x), File.ReadAllBytes);
        var request = BatchCheckRequest(f, saved.Header);
        var pending = await f.Call(request);
        Assert.False(pending.PublishedBatchCheck.Verified);
        var result = await CollectBatchCheck(f, request);
        Assert.Equal(FileStatus.Ok, result.Status); StorageRules.ValidateResponse(result);
        Assert.True(result.PublishedBatchCheck.Verified); Assert.Equal(request.MutationId, result.PublishedBatchCheck.Challenge);
        Assert.Equal(saved.Header, result.Batch.Header); Assert.Empty(result.Content); Assert.Empty(result.Entries);
        await f.Grants.BatchPlans.WhenFilesIdleAsync();
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(history).Select(Path.GetFileName).Order());
        foreach (var pair in before) Assert.Equal(pair.Value, File.ReadAllBytes(Path.Combine(history, pair.Key!)));
        Assert.Equal(new byte[] { 7, 8, 9 }, File.ReadAllBytes(Path.Combine(f.Destination, saved.Folder, saved.File)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(f.Destination, "folder")));
        Assert.False((await f.Call(request)).PublishedBatchCheck.Verified);
    }
    [Theory]
    [InlineData("bytes")] [InlineData("file-identity")] [InlineData("folder-identity")]
    [InlineData("extra-child")] [InlineData("missing-child")] [InlineData("hardlink")]
    public async Task Published_batch_check_refuses_changed_destination_without_adopting_or_removing_it(string change)
    {
        await using var f = new Fixture(checks: new AcceptedFiles()); var saved = await PublishBatchForCheck(f);
        var folder = Path.Combine(f.Destination, saved.Folder); var file = Path.Combine(folder, saved.File);
        switch (change)
        {
            case "bytes": File.WriteAllBytes(file, [9, 8, 7]); break;
            case "file-identity": File.Move(file, Path.Combine(f.Root, "old-file")); File.WriteAllBytes(file, [7, 8, 9]); break;
            case "folder-identity": Directory.Move(folder, Path.Combine(f.Root, "old-folder")); Directory.CreateDirectory(folder); break;
            case "extra-child": File.WriteAllText(Path.Combine(folder, "owner.txt"), "keep"); break;
            case "missing-child": Directory.Delete(Path.Combine(folder, "empty")); break;
            case "hardlink": Assert.True(PublishedBatchHardLink(Path.Combine(f.Root, "owner-alias"), file, IntPtr.Zero)); break;
        }
        var request = BatchCheckRequest(f, saved.Header);
        Assert.False((await f.Call(request)).PublishedBatchCheck.Verified);
        var result = await CollectBatchCheck(f, request);
        Assert.Equal(FileStatus.Conflict, result.Status); Assert.Null(result.PublishedBatchCheck);
        await f.Grants.BatchPlans.WhenFilesIdleAsync();
        Assert.True(Directory.Exists(folder));
        if (change == "extra-child") Assert.Equal("keep", File.ReadAllText(Path.Combine(folder, "owner.txt")));
        Assert.Equal(UploadBatchPlanPhase.UploadPlanCompleted, Assert.Single(f.Grants.BatchPlans.All).Phase);
    }
    [Fact]
    public async Task Published_batch_check_requires_original_intent_current_pairing_and_current_grant()
    {
        await using var f = new Fixture(owner: true, checks: new AcceptedFiles()); var saved = await PublishBatchForCheck(f);
        var request = BatchCheckRequest(f, saved.Header);
        Assert.Equal(FileStatus.AccessDenied, (await f.Raw(request, new string('d', 64))).Status);
        var malformed = request.Clone(); malformed.BatchHeader.TotalBytes++;
        Assert.Equal(FileStatus.InvalidRequest, (await f.Raw(malformed)).Status);
        malformed = request.Clone(); malformed.BatchItemIndex = 0;
        Assert.Equal(FileStatus.InvalidRequest, (await f.Raw(malformed)).Status);
        Assert.False((await f.Call(request)).PublishedBatchCheck.Verified);
        var foreignChallenge = request.Clone(); foreignChallenge.MutationId = ProtocolRules.NewToken();
        Assert.Equal(FileStatus.Conflict, (await f.Raw(foreignChallenge)).Status);
        f.Grants.Update(f.Grant.Id, StoragePermission.ReadOnly, [], null, true);
        Assert.Equal(FileStatus.AccessDenied, (await f.Raw(request)).Status);
        await f.Grants.BatchPlans.WhenFilesIdleAsync();
        f.Grants.Update(f.Grant.Id, StoragePermission.Contribute, [], null, true);
        f.Pairing = ProtocolRules.NewToken(); await f.Reopen();
        Assert.Equal(FileStatus.AccessDenied, (await f.Raw(request)).Status);
    }
    [Fact]
    public async Task Published_batch_check_refuses_incomplete_or_mixed_outcomes_and_retired_history()
    {
        await using var f = new Fixture(checks: new AcceptedFiles());
        var h = await ReadyDirectories(f, FilePlan([1]));
        Assert.Equal(FileStatus.Conflict, (await f.Raw(BatchCheckRequest(f, h))).Status);
        await StopPlan(f, h);
        Assert.Equal(FileStatus.Conflict, (await f.Raw(BatchCheckRequest(f, h))).Status);
        var manifest = new UploadBatchManifest { Collision = UploadCollision.UploadSkip };
        manifest.Items.Add(new UploadBatchItem { Parent = -1, Name = "unavailable.bin", Error = "Source unavailable." });
        var mixed = await ReadyDirectories(f, manifest);
        await f.Call(f.Request(mixed, FileOperation.FinishUploadBatch));
        Assert.Equal(FileStatus.Conflict, (await f.Raw(BatchCheckRequest(f, mixed))).Status);
        var saved = await PublishBatchForCheck(f);
        var review = f.Grants.BatchPlans.ReviewRetirement(saved.Header.Id);
        if (!f.Grants.BatchPlans.ConfirmRetirement(review))
            while (!f.Grants.BatchPlans.ContinueRetirement(saved.Header.Id)) { }
        Assert.Equal(FileStatus.NotFound, (await f.Raw(BatchCheckRequest(f, saved.Header))).Status);
    }
    [Fact]
    public async Task Published_batch_check_shares_verification_slots_and_ends_when_its_session_closes()
    {
        await using var f = new Fixture(checks: new AcceptedFiles()); var saved = await PublishBatchForCheck(f);
        var request = BatchCheckRequest(f, saved.Header);
        using (var first = f.Grants.ReserveIncomingVerification())
        using (var second = f.Grants.ReserveIncomingVerification())
        {
            Assert.NotNull(first); Assert.NotNull(second);
            Assert.Equal(FileStatus.ResourceExhausted, (await f.Raw(request)).Status);
        }
        Assert.False((await f.Call(request)).PublishedBatchCheck.Verified);
        using var otherSession = new WindowsFileService(f.Grants);
        var otherRequest = request.Clone(); otherRequest.RequestId = ProtocolRules.NewToken();
        Assert.Equal(FileStatus.Conflict, (await otherSession.ExecuteAsync(Fixture.Peer, otherRequest, default)).Status);
        f.Service.Dispose(); await f.Grants.BatchPlans.WhenFilesIdleAsync();
        var restarted = await otherSession.ExecuteAsync(Fixture.Peer, otherRequest, default);
        Assert.Equal(FileStatus.Ok, restarted.Status); Assert.False(restarted.PublishedBatchCheck.Verified);
    }
    [Fact]
    public async Task Published_batch_check_pins_saved_files_and_rechecks_folder_membership_at_collection()
    {
        await using var f = new Fixture(checks: new AcceptedFiles()); var saved = await PublishBatchForCheck(f);
        var request = BatchCheckRequest(f, saved.Header); await f.Call(request);
        var path = Path.Combine(f.Destination, saved.Folder, saved.File);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            try { using var writer = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); }
            catch (IOException) { break; }
            await Task.Delay(10, deadline.Token);
        }
        Assert.Throws<IOException>(() => File.Move(path, Path.Combine(f.Root, "replacement")));
        var added = Path.Combine(f.Destination, saved.Folder, "owner-added.txt"); File.WriteAllText(added, "keep");
        var result = await CollectBatchCheck(f, request);
        Assert.Equal(FileStatus.Conflict, result.Status); Assert.Null(result.PublishedBatchCheck);
        await f.Grants.BatchPlans.WhenFilesIdleAsync(); Assert.Equal("keep", File.ReadAllText(added));
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PublishedBatchHardLink(string link, string target, IntPtr attributes);

    [Fact]
    public async Task Published_batch_check_refuses_a_directory_converted_to_a_junction_before_collection()
    {
        await using var f = new Fixture(checks: new AcceptedFiles()); var saved = await PublishBatchForCheck(f);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Time.TimestampRead = () => finished.TrySetResult();
        var request = BatchCheckRequest(f, saved.Header); await f.Call(request);
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10)); f.Time.TimestampRead = null;
        var target = Directory.CreateDirectory(Path.Combine(f.Root, "owner-target")).FullName;
        var empty = Path.Combine(f.Destination, saved.Folder, "empty");
        using (var handle = PublishedBatchOpenDirectory(empty, 0x40000000, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero))
        {
            Assert.False(handle.IsInvalid);
            var substitute = Encoding.Unicode.GetBytes(@"\??\" + target); var print = Encoding.Unicode.GetBytes(target);
            var buffer = new byte[16 + substitute.Length + 2 + print.Length + 2];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0xa0000003);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), checked((ushort)(buffer.Length - 8)));
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), checked((ushort)substitute.Length));
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), checked((ushort)(substitute.Length + 2)));
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), checked((ushort)print.Length));
            substitute.CopyTo(buffer, 16); print.CopyTo(buffer, 16 + substitute.Length + 2);
            Assert.True(PublishedBatchSetReparse(handle, 0x900a4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero),
                "Could not convert the owned empty directory: " + Marshal.GetLastWin32Error());
        }
        FileResponse result;
        try
        {
            Assert.True((File.GetAttributes(empty) & FileAttributes.ReparsePoint) != 0);
            result = await CollectBatchCheck(f, request);
            await f.Grants.BatchPlans.WhenFilesIdleAsync(); Assert.True(Directory.Exists(target));
            Assert.True((File.GetAttributes(empty) & FileAttributes.ReparsePoint) != 0);
        }
        finally
        {
            // Restore only this test's junction to an ordinary empty directory before fixture cleanup.
            f.Service.Dispose(); await f.Grants.BatchPlans.WhenFilesIdleAsync();
            using var handle = PublishedBatchOpenDirectory(empty, 0x40000000, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            var tag = new byte[8]; BinaryPrimitives.WriteUInt32LittleEndian(tag, 0xa0000003);
            Assert.True(PublishedBatchSetReparse(handle, 0x900ac, tag, tag.Length, IntPtr.Zero, 0, out _, IntPtr.Zero),
                "Could not restore the owned test directory: " + Marshal.GetLastWin32Error());
        }
        Assert.Equal(FileStatus.Conflict, result.Status); Assert.Null(result.PublishedBatchCheck);
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle PublishedBatchOpenDirectory(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PublishedBatchSetReparse(SafeFileHandle handle, uint control, byte[] input, int inputLength, IntPtr output, int outputLength, out uint returned, IntPtr overlapped);
}
