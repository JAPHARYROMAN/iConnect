using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using Xunit;

namespace IConnect.Tests;

public sealed partial class WindowsStorageTests : IDisposable
{
    private readonly string sandbox;
    private readonly string root;
    private readonly string peer = new('a', 64);
    private readonly StorageGrantStore grants;
    private readonly WindowsFileService files;
    public WindowsStorageTests()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Directory.Build.props"))) repository = repository.Parent;
        sandbox = Path.Combine(repository!.FullName, ".local", "storage-tests", ProtocolRules.NewToken());
        root = Path.Combine(sandbox, "shared");
        Directory.CreateDirectory(root);
        grants = new StorageGrantStore(Path.Combine(sandbox, "state"));
        files = new WindowsFileService(grants);
    }

    private async Task<FileResponse> Request(FileOperation operation, string node = "", string destination = "", string name = "", string version = "", long offset = 0, uint length = 0, string continuation = "", string? asPeer = null)
    {
        var request = new FileRequest
        {
            RequestId = ProtocolRules.NewToken(), Operation = operation, NodeId = node, DestinationId = destination,
            Name = name, ExpectedVersion = version, Offset = offset, Length = length, Continuation = continuation
        };
        if (MutationRules.IsChange(operation))
        {
            var context = await files.ExecuteAsync(asPeer ?? peer, new() { RequestId = ProtocolRules.NewToken(), Operation = FileOperation.MutationContext }, default);
            request.MutationId = MutationRules.NewId(); request.MutationEpoch = context.MutationEpoch;
        }
        return await files.ExecuteAsync(asPeer ?? peer, request, CancellationToken.None);
    }
    private async Task<FileEntry> Root(StoragePermission permissions = StoragePermission.ReadOnly, string[]? exclusions = null)
    {
        grants.Add(peer, root, permissions, exclusions: exclusions);
        var response = await Request(FileOperation.ListRoots);
        Assert.Equal(FileStatus.Ok, response.Status);
        return Assert.Single(response.Entries);
    }

    [Fact]
    public async Task Storage_is_private_until_granted_and_grants_are_per_peer()
    {
        Assert.Empty((await Request(FileOperation.ListRoots)).Entries);
        var shared = await Root();
        Assert.True(shared.IsDirectory && shared.IsRoot);
        Assert.DoesNotContain(root, shared.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty((await Request(FileOperation.ListRoots, asPeer: new string('b', 64))).Entries);
        Assert.Equal(FileStatus.AccessDenied, (await Request(FileOperation.ListDirectory, shared.Id, asPeer: new string('b', 64))).Status);
    }

    [Fact]
    public async Task Failed_policy_persistence_cannot_create_a_live_grant()
    {
        Directory.CreateDirectory(Path.Combine(sandbox, "state", "storage-grants.json.new"));
        var error = Record.Exception(() => grants.Add(peer, root));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Empty(grants.All);
        Assert.Empty((await Request(FileOperation.ListRoots)).Entries);
    }

    [Fact]
    public async Task Unicode_files_are_listed_and_read_through_opaque_references()
    {
        var expected = Encoding.UTF8.GetBytes("Habari! This is an actual file.\n");
        File.WriteAllBytes(Path.Combine(root, "ripoti-ya-mwaka-文書.txt"), expected);
        Directory.CreateDirectory(Path.Combine(root, "Picha"));
        var shared = await Root();
        var listing = await Request(FileOperation.ListDirectory, shared.Id);
        Assert.Equal(FileStatus.Ok, listing.Status);
        Assert.Equal(2, listing.Entries.Count);
        var document = Assert.Single(listing.Entries, x => !x.IsDirectory);
        Assert.Equal("ripoti-ya-mwaka-文書.txt", document.Name);
        var content = await Request(FileOperation.ReadRange, document.Id, version: document.Version, length: 1024);
        Assert.Equal(FileStatus.Ok, content.Status);
        Assert.Equal(expected, content.Content.ToByteArray());
        Assert.True(content.EndOfFile);
    }

    [Fact]
    public async Task Revocation_permission_changes_and_exclusions_apply_to_existing_references()
    {
        File.WriteAllText(Path.Combine(root, "notes.txt"), "private");
        var shared = await Root(StoragePermission.Manage);
        var document = Assert.Single((await Request(FileOperation.ListDirectory, shared.Id)).Entries);
        grants.Update(shared.GrantId, StoragePermission.List, [], null, true);
        Assert.Equal(FileStatus.AccessDenied, (await Request(FileOperation.ReadRange, document.Id, version: document.Version, length: 10)).Status);
        grants.Update(shared.GrantId, StoragePermission.Manage, ["notes.txt"], null, true);
        Assert.Equal(FileStatus.AccessDenied, (await Request(FileOperation.GetMetadata, document.Id)).Status);
        grants.Remove(shared.GrantId);
        Assert.Equal(FileStatus.AccessDenied, (await Request(FileOperation.ListDirectory, shared.Id)).Status);
    }

    [Fact]
    public async Task Exclusions_and_credential_directories_are_not_enumerated()
    {
        Directory.CreateDirectory(Path.Combine(root, ".ssh"));
        Directory.CreateDirectory(Path.Combine(root, "excluded"));
        File.WriteAllText(Path.Combine(root, "visible.txt"), "visible");
        var shared = await Root(exclusions: ["excluded"]);
        var response = await Request(FileOperation.ListDirectory, shared.Id);
        Assert.Equal(FileStatus.Ok, response.Status);
        Assert.Equal("visible.txt", Assert.Single(response.Entries).Name);
    }

    [Theory]
    [InlineData("..\\outside")]
    [InlineData("../outside")]
    [InlineData("C:\\outside")]
    [InlineData("CON")]
    [InlineData("file.txt:secret")]
    [InlineData("folder.")]
    [InlineData(".iconnect-private")]
    public async Task Mutations_reject_traversal_device_stream_and_private_names(string name)
    {
        var shared = await Root(StoragePermission.Manage);
        Assert.Equal(FileStatus.InvalidRequest, (await Request(FileOperation.CreateDirectory, destination: shared.Id, name: name)).Status);
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    [Fact]
    public async Task Read_only_grants_cannot_create_or_rename()
    {
        File.WriteAllText(Path.Combine(root, "note.txt"), "hello");
        var shared = await Root();
        var item = Assert.Single((await Request(FileOperation.ListDirectory, shared.Id)).Entries);
        Assert.Equal(FileStatus.AccessDenied, (await Request(FileOperation.CreateDirectory, destination: shared.Id, name: "new")).Status);
        Assert.Equal(FileStatus.AccessDenied, (await Request(FileOperation.Rename, item.Id, name: "renamed.txt", version: item.Version)).Status);
    }

    [Fact]
    public async Task Create_rename_and_move_operate_on_the_validated_file_without_overwrite()
    {
        File.WriteAllText(Path.Combine(root, "note.txt"), "hello");
        var shared = await Root(StoragePermission.Manage);
        var item = Assert.Single((await Request(FileOperation.ListDirectory, shared.Id)).Entries);
        var created = await Request(FileOperation.CreateDirectory, destination: shared.Id, name: "Archive");
        Assert.Equal(FileStatus.Ok, created.Status);
        var directory = Assert.Single(created.Entries);
        var renamed = await Request(FileOperation.Rename, item.Id, name: "ripoti.txt", version: item.Version);
        Assert.True(renamed.Status == FileStatus.Ok, renamed.Message);
        var renamedItem = Assert.Single(renamed.Entries);
        var moved = await Request(FileOperation.Move, renamedItem.Id, directory.Id, version: renamedItem.Version);
        Assert.True(moved.Status == FileStatus.Ok, moved.Message);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(root, "Archive", "ripoti.txt")));
        Assert.False(File.Exists(Path.Combine(root, "note.txt")));
        var collision = await Request(FileOperation.CreateDirectory, destination: shared.Id, name: "Archive");
        Assert.Equal(FileStatus.Conflict, collision.Status);
    }

    [Fact]
    public async Task A_replaced_file_or_modified_version_cannot_be_mutated_using_a_stale_reference()
    {
        var path = Path.Combine(root, "note.txt");
        File.WriteAllText(path, "original");
        var shared = await Root(StoragePermission.Manage);
        var item = Assert.Single((await Request(FileOperation.ListDirectory, shared.Id)).Entries);
        File.WriteAllText(path, "a changed source");
        Assert.Equal(FileStatus.StaleReference, (await Request(FileOperation.Rename, item.Id, name: "oops.txt", version: item.Version)).Status);
        File.Move(path, path + ".old");
        File.WriteAllText(path, "replacement");
        Assert.Equal(FileStatus.StaleReference, (await Request(FileOperation.GetMetadata, item.Id)).Status);
        Assert.Equal("replacement", File.ReadAllText(path));
    }

    [Fact]
    public async Task Directory_pages_are_bounded_and_root_references_are_reused()
    {
        for (var i = 0; i < 95; i++) File.WriteAllText(Path.Combine(root, $"file-{i:D3}.txt"), "x");
        var shared = await Root();
        Assert.Equal(shared.Id, Assert.Single((await Request(FileOperation.ListRoots)).Entries).Id);
        var names = new HashSet<string>();
        var continuation = "";
        do
        {
            var page = await Request(FileOperation.ListDirectory, shared.Id, continuation: continuation);
            Assert.Equal(FileStatus.Ok, page.Status);
            Assert.InRange(page.Entries.Count, 0, StorageRules.PageSize);
            foreach (var item in page.Entries) Assert.True(names.Add(item.Name));
            continuation = page.Continuation;
        } while (continuation.Length > 0);
        Assert.Equal(95, names.Count);
    }

    [Fact]
    public async Task Hard_links_are_not_exported_as_regular_files()
    {
        var outside = Path.Combine(sandbox, "outside.txt");
        File.WriteAllText(outside, "outside the grant");
        Assert.True(CreateHardLinkW(Path.Combine(root, "alias.txt"), outside, IntPtr.Zero));
        var shared = await Root();
        var response = await Request(FileOperation.ListDirectory, shared.Id);
        Assert.Equal(FileStatus.Ok, response.Status);
        Assert.Empty(response.Entries);
    }

    [Fact]
    public void An_open_ancestor_cannot_be_renamed_during_a_relative_operation()
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, "nested")).FullName;
        File.WriteAllText(Path.Combine(directory, "note.txt"), "hello");
        using var approved = SecureWindowsDirectory.OpenRoot(root);
        using var held = SecureWindowsDirectory.OpenPath(approved.Info.CanonicalPath, approved.Info.Identity, ["nested", "note.txt"], false);
        Assert.Throws<IOException>(() => Directory.Move(directory, directory + "-changed"));
    }

    [Fact]
    public async Task Junctions_cannot_export_or_replace_a_previously_issued_directory()
    {
        var outside = Directory.CreateDirectory(Path.Combine(sandbox, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "outside");
        var original = Directory.CreateDirectory(Path.Combine(root, "nested")).FullName;
        File.WriteAllText(Path.Combine(original, "visible.txt"), "inside");
        var shared = await Root(StoragePermission.Manage);
        var issued = Assert.Single((await Request(FileOperation.ListDirectory, shared.Id)).Entries);
        Directory.Move(original, original + "-old");
        CreateJunction(original, outside);
        var changed = await Request(FileOperation.ListDirectory, issued.Id);
        Assert.Equal(FileStatus.AccessDenied, changed.Status);
        var listing = await Request(FileOperation.ListDirectory, shared.Id);
        Assert.DoesNotContain(listing.Entries, x => x.Name == "nested");
        Assert.Equal("outside", File.ReadAllText(Path.Combine(outside, "secret.txt")));
        // Remove only the link; never recurse through its target during teardown.
        Directory.Delete(original, false);
    }

    [Fact]
    public async Task Grant_changes_release_an_incomplete_directory_cursor()
    {
        for (var i = 0; i < 40; i++) File.WriteAllText(Path.Combine(root, $"file{i}.txt"), "x");
        var shared = await Root();
        var first = await Request(FileOperation.ListDirectory, shared.Id);
        Assert.NotEmpty(first.Continuation);
        grants.Remove(shared.GrantId);
        Directory.Move(root, root + "-renamed");
        Assert.Equal(FileStatus.AccessDenied, (await Request(FileOperation.ListDirectory, shared.Id, continuation: first.Continuation)).Status);
    }

    [Fact]
    public async Task Replacing_the_approved_root_does_not_authorize_the_replacement()
    {
        var shared = await Root();
        Directory.Move(root, root + "-original");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "not-approved.txt"), "new root");
        Assert.Empty((await Request(FileOperation.ListRoots)).Entries);
        Assert.Equal(FileStatus.StaleReference, (await Request(FileOperation.ListDirectory, shared.Id)).Status);
    }

    private async Task<FileRequest> ChangeRequest(FileOperation operation, string destination = "", string name = "", FileEntry? source = null)
    {
        var context = await Request(FileOperation.MutationContext);
        Assert.Equal(FileStatus.Ok, context.Status);
        return new() { RequestId = ProtocolRules.NewToken(), Operation = operation, MutationId = MutationRules.NewId(),
            MutationEpoch = context.MutationEpoch, DestinationId = destination, Name = name, NodeId = source?.Id ?? "", ExpectedVersion = source?.Version ?? "" };
    }
    private Task<FileResponse> ExecuteChange(FileRequest request, WindowsFileService? service = null) => (service ?? files).ExecuteAsync(peer, request, default);
    private string MutationJournal => Path.Combine(sandbox, "state", "file-operations", "journal.json");
    private void SimulateUnacknowledgedEffect(string id)
    {
        var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(MutationJournal))!;
        var row = document["Records"]!.AsArray().Single(x => x!["Id"]!.GetValue<string>() == id)!;
        row["Phase"] = (int)MutationPhase.ChangePending; row["ResultName"] = "";
        File.WriteAllText(MutationJournal, document.ToJsonString());
    }

    [Fact]
    public async Task Folder_creation_replay_survives_service_restart_without_new_effects()
    {
        var shared = await Root(StoragePermission.Manage);
        var create = await ChangeRequest(FileOperation.CreateDirectory, shared.Id, "Once");
        Assert.Equal(MutationPhase.ChangeSucceeded, (await ExecuteChange(create)).Mutation.Phase);
        using var restarted = new WindowsFileService(new StorageGrantStore(Path.Combine(sandbox, "state")));
        create.RequestId = ProtocolRules.NewToken();
        var repeated = await ExecuteChange(create, restarted); // Old session node is deliberately unknown to this service.
        Assert.Equal(FileStatus.Ok, repeated.Status);
        Assert.Equal(create.MutationId, repeated.Mutation.Id);
        Assert.Single(repeated.Entries);
        Assert.Single(Directory.GetDirectories(root));
        create.Name = "Different";
        Assert.Equal(FileStatus.Conflict, (await ExecuteChange(create, restarted)).Status);
        Assert.False(Directory.Exists(Path.Combine(root, "Different")));
    }

    [Fact]
    public async Task Rename_and_move_receipts_reconcile_completed_identity_without_repeating_effects()
    {
        File.WriteAllText(Path.Combine(root, "original.txt"), "preserve me");
        Directory.CreateDirectory(Path.Combine(root, "Target"));
        var shared = await Root(StoragePermission.Manage);
        var entries = (await Request(FileOperation.ListDirectory, shared.Id)).Entries;
        var rename = await ChangeRequest(FileOperation.Rename, name: "renamed.txt", source: entries.Single(x => !x.IsDirectory));
        var renamed = await ExecuteChange(rename);
        Assert.Equal(FileStatus.Ok, renamed.Status);
        SimulateUnacknowledgedEffect(rename.MutationId);
        using var restarted = new WindowsFileService(new StorageGrantStore(Path.Combine(sandbox, "state")));
        Assert.Equal(MutationPhase.ChangeSucceeded, (await ExecuteChange(rename, restarted)).Mutation.Phase);
        var move = await ChangeRequest(FileOperation.Move, entries.Single(x => x.IsDirectory).Id, source: renamed.Entries.Single());
        Assert.Equal(FileStatus.Ok, (await ExecuteChange(move)).Status);
        SimulateUnacknowledgedEffect(move.MutationId);
        Assert.Equal(MutationPhase.ChangeSucceeded, (await ExecuteChange(move, restarted)).Mutation.Phase);
        Assert.Equal("preserve me", File.ReadAllText(Path.Combine(root, "Target", "renamed.txt")));
        Assert.False(File.Exists(Path.Combine(root, "renamed.txt")));
    }

    [Fact]
    public async Task Unacknowledged_create_and_replaced_rename_targets_require_review()
    {
        File.WriteAllText(Path.Combine(root, "source.txt"), "original");
        var shared = await Root(StoragePermission.Manage);
        var source = (await Request(FileOperation.ListDirectory, shared.Id)).Entries.Single();
        var create = await ChangeRequest(FileOperation.CreateDirectory, shared.Id, "Maybe created");
        await ExecuteChange(create); SimulateUnacknowledgedEffect(create.MutationId);
        Assert.Equal(MutationPhase.ChangeReview, (await ExecuteChange(create)).Mutation.Phase);
        Assert.Single(Directory.GetDirectories(root));
        var rename = await ChangeRequest(FileOperation.Rename, name: "target.txt", source: source);
        Assert.Equal(FileStatus.Ok, (await ExecuteChange(rename)).Status);
        SimulateUnacknowledgedEffect(rename.MutationId);
        // Keep the original object alive at another name so NTFS cannot reuse its file identity.
        File.Move(Path.Combine(root, "target.txt"), Path.Combine(root, "original-object.txt"));
        File.WriteAllText(Path.Combine(root, "target.txt"), "replacement");
        Assert.Equal(MutationPhase.ChangeReview, (await ExecuteChange(rename)).Mutation.Phase);
        Assert.Equal("replacement", File.ReadAllText(Path.Combine(root, "target.txt")));
    }

    [Fact]
    public async Task Intent_save_failure_blocks_changes_and_corrupt_receipt_archive_invalidates_old_context()
    {
        var shared = await Root(StoragePermission.Manage);
        var create = await ChangeRequest(FileOperation.CreateDirectory, shared.Id, "Safe");
        var blocker = MutationJournal + ".new";
        Directory.CreateDirectory(blocker);
        Assert.Equal(FileStatus.AccessDenied, (await ExecuteChange(create)).Status);
        Assert.False(Directory.Exists(Path.Combine(root, "Safe")));
        Directory.Delete(blocker);
        Assert.Equal(FileStatus.Ok, (await ExecuteChange(create)).Status);
        File.WriteAllText(MutationJournal, "damaged");
        Assert.Equal(FileStatus.IoError, (await Request(FileOperation.MutationContext)).Status);
        var issue = Assert.Single(grants.MutationRecoveryIssues);
        grants.ArchiveMutationRecovery(issue.Id);
        Assert.Equal(FileStatus.StaleReference, (await ExecuteChange(create)).Status);
        Assert.Single(Directory.GetDirectories(root));
    }

    [Fact]
    public async Task Receipts_are_peer_scoped_and_revocation_applies_to_recorded_outcomes()
    {
        var shared = await Root(StoragePermission.Manage);
        var create = await ChangeRequest(FileOperation.CreateDirectory, shared.Id, "Private change");
        await ExecuteChange(create);
        var query = new FileRequest { RequestId = ProtocolRules.NewToken(), Operation = FileOperation.MutationStatus,
            MutationId = create.MutationId, MutationEpoch = create.MutationEpoch };
        Assert.Equal(FileStatus.NotFound, (await files.ExecuteAsync(new string('b', 64), query, default)).Status);
        grants.Remove(shared.GrantId);
        Assert.Equal(FileStatus.AccessDenied, (await ExecuteChange(query)).Status);
        Assert.Equal(FileStatus.AccessDenied, (await ExecuteChange(create)).Status);
        var list = await ExecuteChange(new() { RequestId = ProtocolRules.NewToken(), Operation = FileOperation.ListMutations, MutationEpoch = create.MutationEpoch });
        Assert.Empty(list.Mutations);
    }

    [Fact]
    public async Task Expired_requests_and_protected_descendants_cannot_be_moved()
    {
        Directory.CreateDirectory(Path.Combine(root, "Container", ".ssh"));
        var shared = await Root(StoragePermission.Manage);
        var source = (await Request(FileOperation.ListDirectory, shared.Id)).Entries.Single();
        var rename = await ChangeRequest(FileOperation.Rename, name: "Moved credentials", source: source);
        Assert.Equal(FileStatus.AccessDenied, (await ExecuteChange(rename)).Status);
        Assert.True(Directory.Exists(Path.Combine(root, "Container", ".ssh")));
        var create = await ChangeRequest(FileOperation.CreateDirectory, shared.Id, "Expired");
        var bytes = RandomNumberGenerator.GetBytes(32);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(bytes, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - MutationRules.Lifetime - 60000);
        create.MutationId = ProtocolRules.Base64Url(bytes);
        Assert.Equal(FileStatus.StaleReference, (await ExecuteChange(create)).Status);
        Assert.False(Directory.Exists(Path.Combine(root, "Expired")));
    }

    private static void CreateJunction(string link, string target)
    {
        Directory.CreateDirectory(link);
        var substitute = Encoding.Unicode.GetBytes("\\??\\" + target);
        var print = Encoding.Unicode.GetBytes(target);
        var data = new byte[16 + substitute.Length + 2 + print.Length + 2];
        BitConverter.GetBytes(0xA0000003u).CopyTo(data, 0);
        BitConverter.GetBytes(checked((ushort)(data.Length - 8))).CopyTo(data, 4);
        BitConverter.GetBytes(checked((ushort)substitute.Length)).CopyTo(data, 10);
        BitConverter.GetBytes(checked((ushort)(substitute.Length + 2))).CopyTo(data, 12);
        BitConverter.GetBytes(checked((ushort)print.Length)).CopyTo(data, 14);
        substitute.CopyTo(data, 16);
        print.CopyTo(data, 16 + substitute.Length + 2);
        using var handle = CreateFileW(link, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        Assert.False(handle.IsInvalid);
        Assert.True(DeviceIoControl(handle, 0x000900A4, data, data.Length, IntPtr.Zero, 0, out _, IntPtr.Zero),
            $"Could not create the fixture junction: {Marshal.GetLastWin32Error()}");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string name, string existing, IntPtr security);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint control, byte[] input, int inputLength,
        IntPtr output, int outputLength, out int returned, IntPtr overlapped);

    public void Dispose()
    {
        files.Dispose();
        // Sandbox belongs to this fixture, inside the repository's ignored .local tree.
        if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true);
    }
}
