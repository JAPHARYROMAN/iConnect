using System.Text.Json.Nodes;
using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using Xunit;

namespace IConnect.Tests;

public sealed class CreatedDirectoryTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; }
        public string Destination => Path.Combine(Root, "destination");
        public string Profile => Path.Combine(Root, "profile");
        public const string Peer = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        public StorageGrantStore Grants { get; private set; }
        public StorageGrant Grant { get; }
        public WindowsFileService Service { get; private set; }
        public Fixture(StoragePermission permission = StoragePermission.Contribute)
        {
            var repo = new DirectoryInfo(AppContext.BaseDirectory);
            while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "Directory.Build.props"))) repo = repo.Parent;
            Root = Path.Combine(repo!.FullName, ".local", "created-directory-tests", ProtocolRules.NewToken()); Directory.CreateDirectory(Destination);
            Grants = new(Profile); Grant = Grants.Add(Peer, Destination, permission); Service = new(Grants);
        }
        public Task<FileResponse> Request(FileRequest value, CancellationToken cancellation = default, string peer = Peer)
        { var request = value.Clone(); request.RequestId = ProtocolRules.NewToken(); return Service.ExecuteAsync(peer, request, cancellation); }
        public async Task<FileRequest> Creation(string name, FileEntry? parent = null)
        {
            parent ??= (await Request(new() { Operation = FileOperation.UploadContext })).Entries.Single();
            var context = await Request(new() { Operation = FileOperation.MutationContext });
            return new() { Operation = FileOperation.CreateDirectory, DestinationId = parent.Id, Name = name, MutationId = MutationRules.NewId(), MutationEpoch = context.MutationEpoch };
        }
        public CreatedDirectoryReceipt Receipt(FileRequest request) => new(request.MutationId, request.MutationEpoch, Grant.Id, request.Name);
        public async Task Reopen() { Service.Dispose(); await Grants.Uploads.WhenIdleAsync(); Grants = new(Profile); Service = new(Grants); }
        public async ValueTask DisposeAsync() { Service.Dispose(); await Grants.Uploads.WhenIdleAsync(); Directory.Delete(Root, true); }
    }
    [Fact]
    public async Task Saved_folder_identity_rebinds_after_restart_and_accepts_a_private_source_snapshot()
    {
        await using var f = new Fixture(); var creation = await f.Creation("bundle"); var made = await f.Request(creation);
        var original = CreatedDirectoryReferences.Check(f.Receipt(creation), made); Assert.True(original.IsDirectory); Assert.False(original.IsRoot);
        await f.Reopen(); var entry = await CreatedDirectoryReferences.ResolveAsync(f.Receipt(creation), (r, c) => f.Request(r, c), default);
        Assert.NotEqual(original.Id, entry.Id); Assert.Equal("bundle", entry.Name);
        var nestedRequest = await f.Creation("empty", entry); Assert.Equal(FileStatus.Ok, (await f.Request(nestedRequest)).Status);
        var bytes = "Generated private source snapshot.\n"u8.ToArray(); var source = Path.Combine(f.Root, "source.txt"); File.WriteAllBytes(source, bytes);
        var authority = new UploadAuthority(new string('b', 64), ProtocolRules.NewToken(), new string('c', 64), ProtocolRules.NewToken());
        await using var sender = new WindowsUploadSender(Path.Combine(f.Root, "sender"), value => value == authority);
        var id = await sender.PrepareAsync(source, authority); await sender.SendAsync(id, entry, (r, c) => f.Request(r, c));
        var complete = sender.All.Single(); Assert.Equal(SendingPhase.Completed, complete.Phase);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(f.Destination, entry.Name, complete.FinalName))); Assert.Equal(bytes, File.ReadAllBytes(source));
        Assert.True(Directory.Exists(Path.Combine(f.Destination, entry.Name, "empty")));
        Assert.Single(Directory.GetFiles(Path.Combine(f.Destination, entry.Name)));
    }
    [Fact]
    public async Task Same_name_replacement_is_not_adopted_and_repeated_creation_has_no_new_effect()
    {
        await using var f = new Fixture(); var request = await f.Creation("bundle"); Assert.Equal(FileStatus.Ok, (await f.Request(request)).Status);
        Directory.Move(Path.Combine(f.Destination, "bundle"), Path.Combine(f.Destination, "kept-original")); Directory.CreateDirectory(Path.Combine(f.Destination, "bundle"));
        File.WriteAllText(Path.Combine(f.Destination, "bundle", "owner.txt"), "keep"); await f.Reopen();
        var recover = new FileRequest { Operation = FileOperation.ResolveCreatedDirectory, MutationId = request.MutationId, MutationEpoch = request.MutationEpoch };
        Assert.Equal(FileStatus.StaleReference, (await f.Request(recover)).Status); Assert.Equal(FileStatus.StaleReference, (await f.Request(request)).Status);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(f.Destination, "bundle", "owner.txt"))); Assert.Equal(2, Directory.GetDirectories(f.Destination).Length);
    }
    [Fact]
    public async Task Replaced_parent_and_revoked_excluded_or_foreign_authority_cannot_recover_a_reference()
    {
        await using var f = new Fixture(); var outer = await f.Creation("outer"); var parent = (await f.Request(outer)).Entries.Single();
        var request = await f.Creation("inside", parent); Assert.Equal(FileStatus.Ok, (await f.Request(request)).Status);
        var recover = new FileRequest { Operation = FileOperation.ResolveCreatedDirectory, MutationId = request.MutationId, MutationEpoch = request.MutationEpoch };
        Assert.Equal(FileStatus.NotFound, (await f.Request(recover, peer: new string('f', 64))).Status);
        Assert.Equal(FileStatus.StaleReference, (await f.Request(withEpoch())).Status);
        FileRequest withEpoch() { var value = recover.Clone(); value.MutationEpoch = ProtocolRules.NewToken(); return value; }
        f.Grants.Update(f.Grant.Id, StoragePermission.ReadOnly, [], null, true); Assert.Equal(FileStatus.AccessDenied, (await f.Request(recover)).Status);
        f.Grants.Update(f.Grant.Id, StoragePermission.Contribute, ["outer/inside"], null, true); Assert.Equal(FileStatus.AccessDenied, (await f.Request(recover)).Status);
        f.Grants.Update(f.Grant.Id, StoragePermission.Contribute, [], null, true);
        Directory.Move(Path.Combine(f.Destination, "outer"), Path.Combine(f.Destination, "kept-outer")); Directory.CreateDirectory(Path.Combine(f.Destination, "outer"));
        Assert.Equal(FileStatus.StaleReference, (await f.Request(recover)).Status);
        f.Grants.Remove(f.Grant.Id); Assert.Equal(FileStatus.AccessDenied, (await f.Request(recover)).Status);
    }
    [Fact]
    public async Task Legacy_and_uncertain_receipts_never_recover_by_name()
    {
        await using var f = new Fixture(); var request = await f.Creation("old"); Assert.Equal(FileStatus.Ok, (await f.Request(request)).Status);
        var path = Path.Combine(f.Profile, "file-operations", "journal.json"); var document = JsonNode.Parse(File.ReadAllText(path))!;
        var row = document["Records"]!.AsArray().Single()!.AsObject(); row.Remove("ResultIdentity"); row.Remove("RequestedName"); File.WriteAllText(path, document.ToJsonString());
        var recover = new FileRequest { Operation = FileOperation.ResolveCreatedDirectory, MutationId = request.MutationId, MutationEpoch = request.MutationEpoch };
        Assert.Equal(FileStatus.Conflict, (await f.Request(recover)).Status);
        row["Phase"] = (int)MutationPhase.ChangePending; row["ResultName"] = ""; File.WriteAllText(path, document.ToJsonString());
        Assert.Equal(FileStatus.Conflict, (await f.Request(recover)).Status); Assert.Equal(FileStatus.Ok, (await f.Request(new() { Operation = FileOperation.MutationStatus, MutationId = request.MutationId, MutationEpoch = request.MutationEpoch })).Status);
        Assert.Single(Directory.GetDirectories(f.Destination));
    }
    [Fact]
    public async Task Receive_only_folder_names_and_versions_do_not_reveal_existing_destination_activity()
    {
        await using var f = new Fixture(StoragePermission.Write | StoragePermission.Create); Directory.CreateDirectory(Path.Combine(f.Destination, "bundle"));
        var request = await f.Creation("bundle"); var response = await f.Request(request); var entry = CreatedDirectoryReferences.Check(f.Receipt(request), response);
        Assert.NotEqual("bundle", entry.Name); Assert.Equal("bundle", response.Mutation.RequestedName); Assert.Equal(0, entry.ModifiedAt);
        Assert.Equal(FileStatus.AccessDenied, (await f.Request(new() { Operation = FileOperation.ListDirectory, NodeId = entry.Id })).Status);
        Assert.Equal(FileStatus.AccessDenied, (await f.Request(new() { Operation = FileOperation.GetMetadata, NodeId = entry.Id })).Status);
        File.WriteAllText(Path.Combine(f.Destination, entry.Name, "owner-added.txt"), "private activity"); await f.Reopen();
        var recovered = await CreatedDirectoryReferences.ResolveAsync(f.Receipt(request), (r, c) => f.Request(r, c), default);
        Assert.Equal(0, recovered.ModifiedAt); Assert.Equal(entry.Version, recovered.Version); Assert.Equal(entry.Name, recovered.Name);
        Assert.True(Directory.Exists(Path.Combine(f.Destination, "bundle")));
    }
    [Theory]
    [InlineData("epoch")]
    [InlineData("id")]
    [InlineData("grant")]
    [InlineData("name")]
    [InlineData("requested")]
    [InlineData("phase")]
    [InlineData("file")]
    [InlineData("parent")]
    [InlineData("count")]
    public async Task Client_rejects_changed_created_folder_descriptors(string attack)
    {
        await using var f = new Fixture(); var request = await f.Creation("bundle"); var response = await f.Request(request);
        var receipt = f.Receipt(request); var entry = response.Entries[0];
        switch (attack)
        {
            case "epoch": response.MutationEpoch = ProtocolRules.NewToken(); break;
            case "id": response.Mutation.Id = MutationRules.NewId(); response.Mutation.CreatedAt = MutationRules.CreatedAt(response.Mutation.Id); break;
            case "grant": entry.GrantId = ProtocolRules.NewToken(); break;
            case "name": entry.Name = "different"; break;
            case "requested": response.Mutation.RequestedName = "different"; break;
            case "phase": response.Mutation.Phase = MutationPhase.ChangeReview; break;
            case "file": entry.IsDirectory = false; break;
            case "parent": entry.ParentId = entry.Id; break;
            case "count": response.Entries.Add(entry.Clone()); break;
        }
        Assert.Throws<InvalidDataException>(() => CreatedDirectoryReferences.Check(receipt, response));
    }
}
