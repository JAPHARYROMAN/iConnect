using IConnect.AppModel;
using IConnect.Connectivity;
using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace IConnect.Tests;

[SupportedOSPlatform("windows")]
public sealed class WindowsOwnerBindingTests : IDisposable
{
    private readonly string root;
    private readonly string profile;
    private readonly string documents;
    private readonly DeviceIdentity owner = DeviceIdentity.Ephemeral("Owner fixture");
    private readonly DeviceIdentity peer = DeviceIdentity.Ephemeral("Peer fixture");
    public WindowsOwnerBindingTests()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "Directory.Build.props"))) repo = repo.Parent;
        root = Path.Combine(repo!.FullName, ".local", "owner-policy-tests", ProtocolRules.NewToken());
        profile = Path.Combine(root, "profile"); documents = Path.Combine(root, "documents"); Directory.CreateDirectory(documents);
        File.WriteAllText(Path.Combine(documents, "keep.txt"), "Keep these real bytes across identity recovery.");
    }
    private ProofClient.TrustedPeer Pinned => new(peer.Id, peer.Descriptor.Name, Convert.ToBase64String(peer.PublicKey));
    private FileRequest Request(FileOperation operation, string node = "") => new() { RequestId = ProtocolRules.NewToken(), Operation = operation, NodeId = node };
    [Fact]
    public async Task Authority_survives_the_same_identity_and_old_live_scopes_fail_after_key_change()
    {
        using var trust = new TrustedPeerStore(profile, owner.Id); trust.Add(Pinned);
        var grants = new StorageGrantStore(profile, ownerBinding: trust.OwnerBinding); var grant = grants.Add(peer.Id, documents);
        using var files = new WindowsFileService(grants);
        Assert.Single((await files.ExecuteAsync(peer.Id, Request(FileOperation.ListRoots), default)).Entries);
        using var changed = DeviceIdentity.Ephemeral("Replacement identity");
        var replacement = new WindowsOwnerBinding(profile, changed.Id);
        Assert.False(replacement.Healthy); Assert.False(trust.OwnerReady); Assert.Empty(trust.All); Assert.Empty(grants.ForPeer(peer.Id));
        Assert.Equal(FileStatus.AccessDenied, (await files.ExecuteAsync(peer.Id, Request(FileOperation.ListRoots), default)).Status);
        Assert.Throws<StorageException>(() => grants.Get(grant.Id, peer.Id));
        Assert.Throws<StorageException>(() => trust.Add(Pinned));
        Assert.Equal("Keep these real bytes across identity recovery.", File.ReadAllText(Path.Combine(documents, "keep.txt")));
    }
    [Fact]
    public void Owner_confirmed_reset_archives_authority_and_stale_objects_cannot_authorize_the_new_epoch()
    {
        using var trust = new TrustedPeerStore(profile, owner.Id); trust.Add(Pinned);
        var oldGrants = new StorageGrantStore(profile, ownerBinding: trust.OwnerBinding); var previous = oldGrants.Add(peer.Id, documents);
        var epoch = trust.OwnerBinding!.Epoch;
        trust.ResetOwnerPolicy(); Assert.True(trust.OwnerReady); Assert.Empty(trust.All); Assert.NotEqual(epoch, trust.OwnerBinding.Epoch);
        Assert.Throws<StorageException>(() => oldGrants.Get(previous.Id, peer.Id));
        var current = new StorageGrantStore(profile, ownerBinding: trust.OwnerBinding); Assert.Empty(current.All);
        trust.Add(Pinned); var fresh = current.Add(peer.Id, documents); Assert.NotEqual(previous.Id, fresh.Id);
        Assert.Throws<StorageException>(() => oldGrants.Add(peer.Id, documents));
        Assert.True(trust.OwnerBinding.PreservedRecords >= 3);
        var saved = Directory.GetFiles(Path.Combine(profile, "recovery"), "storage-grants.json", SearchOption.AllDirectories).Single();
        Assert.Contains(previous.Id, File.ReadAllText(saved)); Assert.DoesNotContain(fresh.Id, File.ReadAllText(saved));
    }
    [Fact]
    public void Restoring_old_grant_records_alone_cannot_reactivate_approvals()
    {
        using var trust = new TrustedPeerStore(profile, owner.Id); trust.Add(Pinned);
        var grants = new StorageGrantStore(profile, ownerBinding: trust.OwnerBinding); var previous = grants.Add(peer.Id, documents);
        var path = Path.Combine(profile, "storage-grants.json"); var before = File.ReadAllBytes(path);
        trust.ResetOwnerPolicy(); trust.Add(Pinned); File.WriteAllBytes(path, before);
        var restored = new StorageGrantStore(profile, ownerBinding: trust.OwnerBinding);
        Assert.Empty(restored.All); Assert.False(trust.OwnerReady); Assert.Throws<StorageException>(() => restored.Get(previous.Id, peer.Id));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(new WindowsOwnerBinding(profile, owner.Id).Healthy); // Reopening a component cannot clear a failed-authority latch.
    }
    [Fact]
    public void Legacy_authority_requires_review_and_missing_bound_records_are_not_treated_as_fresh()
    {
        using (var legacy = new TrustedPeerStore(profile)) legacy.Add(Pinned);
        var legacyGrants = new StorageGrantStore(profile); legacyGrants.Add(peer.Id, documents);
        var bytes = File.ReadAllBytes(Path.Combine(profile, "peers.json"));
        using var bound = new TrustedPeerStore(profile, owner.Id);
        Assert.False(bound.OwnerReady); Assert.Empty(bound.All); Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(profile, "peers.json")));
        bound.ResetOwnerPolicy(); Assert.True(bound.OwnerReady); Assert.Empty(bound.All);
        File.Delete(Path.Combine(profile, "peers.json"));
        Assert.False(new WindowsOwnerBinding(profile, owner.Id).Healthy);
    }
    [Fact]
    public void Recovery_intent_survives_failed_reset_and_retry_keeps_content()
    {
        using var trust = new TrustedPeerStore(profile, owner.Id); trust.Add(Pinned);
        var grants = new StorageGrantStore(profile, ownerBinding: trust.OwnerBinding); grants.Add(peer.Id, documents);
        var blocker = Path.Combine(profile, "storage-grants.json.new"); Directory.CreateDirectory(blocker);
        Assert.Throws<IOException>(trust.ResetOwnerPolicy); Assert.False(trust.OwnerReady);
        Assert.True(File.Exists(Path.Combine(profile, "owner-reset.json")));
        Assert.False(new WindowsOwnerBinding(profile, owner.Id).Healthy);
        Assert.Equal("Keep these real bytes across identity recovery.", File.ReadAllText(Path.Combine(documents, "keep.txt")));
        Directory.Delete(blocker); trust.ResetOwnerPolicy(); Assert.True(trust.OwnerReady); Assert.Empty(trust.All);
        Assert.False(File.Exists(Path.Combine(profile, "owner-reset.json")));
        Assert.Empty(new StorageGrantStore(profile, ownerBinding: trust.OwnerBinding).All);
    }
    [Fact]
    public void A_preloaded_unbound_store_cannot_resurrect_legacy_records_when_attached_to_a_session()
    {
        var preloaded = new StorageGrantStore(profile); var old = preloaded.Add(peer.Id, documents);
        using var trust = new TrustedPeerStore(profile, owner.Id); Assert.False(trust.OwnerReady);
        trust.ResetOwnerPolicy(); trust.Add(Pinned);
        using var files = new WindowsFileService(preloaded); files.BindOwnerPolicy(trust.OwnerBinding!);
        Assert.Empty(preloaded.All); Assert.Throws<StorageException>(() => preloaded.Get(old.Id, peer.Id));
    }
    [Fact]
    public void An_identity_change_during_an_authorized_read_withholds_its_result()
    {
        var binding = new WindowsOwnerBinding(profile, owner.Id); var epoch = binding.Epoch;
        using var changed = DeviceIdentity.Ephemeral("Different key");
        Assert.Throws<StorageException>(() => binding.Authorized(epoch, () =>
        {
            _ = new WindowsOwnerBinding(profile, changed.Id); return "must not be returned to the old peer";
        }));
    }
    [Fact]
    public async Task Actual_Cng_key_loss_blocks_restored_profile_until_explicit_repair()
    {
        var desktop = Path.Combine(root, "desktop");
        var keyName = "iConnect.Desktop.v1." + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(desktop.ToUpperInvariant())));
        try
        {
            string original;
            using (var identity = DeviceIdentity.OpenWindows(keyName, "Desktop fixture"))
            {
                original = identity.Id;
                using var trust = new TrustedPeerStore(desktop, original); trust.Add(Pinned);
                new StorageGrantStore(desktop, ownerBinding: trust.OwnerBinding).Add(peer.Id, documents);
                var availability = new DesktopAvailability(desktop); availability.SetOptions(true, false, true); availability.Resume();
            }
            using (var lost = CngKey.Open(keyName)) lost.Delete();
            await using var model = new DesktopController(desktop, action => action());
            Assert.NotEqual(original, model.DeviceId); Assert.False(model.OwnerPolicyHealthy); Assert.Empty(model.Peers); Assert.Empty(model.Grants);
            await Assert.ThrowsAsync<StorageException>(model.ResumeAvailabilityAsync);
            await model.ResetOwnerPolicyAsync(); Assert.True(model.OwnerPolicyHealthy); Assert.Empty(model.Peers); Assert.Empty(model.Grants);
            Assert.True(model.Availability.Preferences.Paused); Assert.False(model.Availability.Preferences.AutoReconnect); Assert.False(model.Availability.Preferences.RunInBackground);
            Assert.Equal("Keep these real bytes across identity recovery.", File.ReadAllText(Path.Combine(documents, "keep.txt")));
        }
        finally { if (CngKey.Exists(keyName)) { using var key = CngKey.Open(keyName); key.Delete(); } }
    }
    [Fact]
    public async Task Ancestor_grants_hide_private_profiles_and_cannot_move_or_prepare_their_contents()
    {
        var container = Path.Combine(root, "container"); var nestedProfile = Path.Combine(container, "private-profile");
        using var trust = new TrustedPeerStore(nestedProfile, owner.Id); trust.Add(Pinned);
        var grants = new StorageGrantStore(nestedProfile, ownerBinding: trust.OwnerBinding);
        Assert.Equal(FileStatus.AccessDenied, Assert.Throws<StorageException>(() => grants.Add(peer.Id, nestedProfile)).Status);
        Directory.CreateDirectory(Path.Combine(nestedProfile, "child"));
        Assert.Equal(FileStatus.AccessDenied, Assert.Throws<StorageException>(() => grants.Add(peer.Id, Path.Combine(nestedProfile, "child"))).Status);
        var grant = grants.Add(peer.Id, root, StoragePermission.Manage);
        using var files = new WindowsFileService(grants);
        var shared = (await files.ExecuteAsync(peer.Id, Request(FileOperation.ListRoots), default)).Entries.Single();
        var first = await files.ExecuteAsync(peer.Id, Request(FileOperation.ListDirectory, shared.Id), default);
        var folder = first.Entries.Single(x => x.Name == "container");
        Assert.Empty((await files.ExecuteAsync(peer.Id, Request(FileOperation.ListDirectory, folder.Id), default)).Entries);
        var epoch = (await files.ExecuteAsync(peer.Id, Request(FileOperation.MutationContext), default)).MutationEpoch;
        var rename = new FileRequest { RequestId = ProtocolRules.NewToken(), Operation = FileOperation.Rename, NodeId = folder.Id,
            Name = "moved-container", ExpectedVersion = folder.Version, MutationId = MutationRules.NewId(), MutationEpoch = epoch };
        Assert.Equal(FileStatus.AccessDenied, (await files.ExecuteAsync(peer.Id, rename, default)).Status);
        Assert.True(Directory.Exists(nestedProfile)); Assert.False(Directory.Exists(Path.Combine(root, "moved-container")));
        var recoveryEpoch = (await files.ExecuteAsync(peer.Id, Request(FileOperation.RecoveryContext), default)).MutationEpoch;
        var deletion = new FileRequest { RequestId = ProtocolRules.NewToken(), Operation = FileOperation.Delete, NodeId = folder.Id,
            ExpectedVersion = folder.Version, MutationId = MutationRules.NewId(), MutationEpoch = recoveryEpoch };
        Assert.Equal(FileStatus.AccessDenied, (await files.ExecuteAsync(peer.Id, deletion, default)).Status);
        Assert.True(Directory.Exists(nestedProfile));
        await using var sources = new DownloadSourceStore(grants, nestedProfile);
        Assert.Throws<StorageException>(() => sources.Prepare(peer.Id, grant.Id, ["container", "private-profile", "peers.json"], "", "", 0, "test"));
    }
    [Fact]
    public async Task Previously_saved_profile_grants_cannot_expose_records_after_upgrade()
    {
        using var trust = new TrustedPeerStore(profile, owner.Id); trust.Add(Pinned);
        var grants = new StorageGrantStore(profile, ownerBinding: trust.OwnerBinding); var grant = grants.Add(peer.Id, documents);
        using var privateFolder = SecureWindowsDirectory.OpenRoot(profile);
        var prior = grant with { DisplayPath = profile, CanonicalPath = privateFolder.Info.CanonicalPath, RootIdentity = privateFolder.Info.Identity };
        var path = Path.Combine(profile, "storage-grants.json");
        var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        document["Records"]![grant.Id] = System.Text.Json.JsonSerializer.SerializeToNode(prior); File.WriteAllText(path, document.ToJsonString());
        var upgraded = new StorageGrantStore(profile, ownerBinding: trust.OwnerBinding);
        Assert.Throws<StorageException>(() => upgraded.Get(grant.Id, peer.Id)); Assert.Empty(upgraded.ForPeer(peer.Id));
        using var files = new WindowsFileService(upgraded);
        Assert.Empty((await files.ExecuteAsync(peer.Id, Request(FileOperation.ListRoots), default)).Entries);
    }
    [Fact]
    public async Task Reserved_private_profile_names_work_and_profile_substitution_blocks_cached_access()
    {
        var reserved = Path.Combine(root, ".iconnect-private-state"); var binding = new WindowsOwnerBinding(reserved, owner.Id);
        var grants = new StorageGrantStore(reserved, ownerBinding: binding); grants.Add(peer.Id, documents);
        using var files = new WindowsFileService(grants);
        Assert.Single((await files.ExecuteAsync(peer.Id, Request(FileOperation.ListRoots), default)).Entries);
        for (var attempt = 0; ; attempt++)
        {
            try { Directory.Move(reserved, Path.Combine(root, "moved-private-state")); break; }
            catch (IOException ex) when (attempt < 5 && (ex.HResult & 0xffff) is 5 or 32 or 33)
            { await Task.Delay(20 << attempt); } // Retry only brief OS access/sharing failures during fixture substitution.
        }
        Directory.CreateDirectory(reserved);
        Assert.Equal(FileStatus.AccessDenied, (await files.ExecuteAsync(peer.Id, Request(FileOperation.ListRoots), default)).Status);
    }
    [Fact]
    public void Private_profile_ancestors_remain_held_during_authorized_work()
    {
        var binding = new WindowsOwnerBinding(profile, owner.Id);
        binding.Authorized(binding.Epoch, () =>
        {
            Assert.True(Record.Exception(() => Directory.Move(profile, Path.Combine(root, "moved"))) is IOException or UnauthorizedAccessException);
            return true;
        });
        Assert.True(Directory.Exists(profile));
    }

    public void Dispose()
    { owner.Dispose(); peer.Dispose(); Directory.Delete(root, true); } // This fixture's newly allocated .local subtree only.
}
