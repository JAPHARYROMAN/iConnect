using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using System.Security.Cryptography;
using Xunit;

namespace IConnect.Tests;

public sealed class BatchSourceTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        public readonly string Root;
        private readonly string workspacePath;
        public string Profile => Path.Combine(Root, "profile");
        public string Selected => Path.Combine(Root, "chosen");
        public readonly UploadAuthority Authority = new(new string('a', 64), ProtocolRules.NewToken(), new string('b', 64), ProtocolRules.NewToken());
        public bool Current = true;
        public WindowsBatchSources Sources;
        public Fixture()
        {
            var workspace = new DirectoryInfo(AppContext.BaseDirectory); while (workspace is not null && !File.Exists(Path.Combine(workspace.FullName, "Directory.Build.props"))) workspace = workspace.Parent;
            workspacePath = workspace!.FullName; Root = Path.Combine(workspacePath, ".local", "batch-source-tests", ProtocolRules.NewToken()); Directory.CreateDirectory(Selected);
            Sources = Open();
        }
        private WindowsBatchSources Open() => new(Profile, authority => Current && authority == Authority);
        public async Task Reopen() { await Sources.DisposeAsync(); Sources = Open(); }
        public string Private(string id, string name) => Path.Combine(Profile, "outgoing-batch-copies", id, name);
        public async ValueTask DisposeAsync()
        {
            await Sources.DisposeAsync();
            var expected = Path.GetFullPath(Path.Combine(workspacePath, ".local", "batch-source-tests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Root).StartsWith(expected, StringComparison.OrdinalIgnoreCase) || !UploadBatchRules.CanonicalToken(Path.GetFileName(Root))) throw new IOException("Fixture cleanup escaped its owned workspace.");
            Directory.Delete(Root, true);
        }
    }
    [Fact]
    public async Task Nested_private_batch_preserves_empty_folders_hashes_and_original_bytes_after_restart()
    {
        await using var f = new Fixture(); Directory.CreateDirectory(Path.Combine(f.Selected, "empty")); Directory.CreateDirectory(Path.Combine(f.Selected, "nested"));
        var bytes = RandomNumberGenerator.GetBytes(1024 * 1024 + 17); File.WriteAllBytes(Path.Combine(f.Selected, "nested", "notes.bin"), bytes); File.WriteAllBytes(Path.Combine(f.Selected, "zero.bin"), []);
        var progress = new List<OutgoingBatchSource>();
        var refreshes = new List<OutgoingBatchSource>();
        f.Sources.Changed += () => progress.Add(Assert.Single(f.Sources.All));
        f.Sources.MetadataChanged += () => refreshes.Add(Assert.Single(f.Sources.All));
        var id = await f.Sources.PrepareAsync([f.Selected], f.Authority, UploadCollision.UploadKeepBoth);
        Assert.Contains(progress, x => x.Phase == BatchSourcePhase.Preparing && x.PreparedBytes == bytes.Length);
        Assert.DoesNotContain(refreshes, x => x.Phase == BatchSourcePhase.Preparing && x.PreparedBytes > 0 && x.ManifestIdentity.Length == 0);
        Assert.Contains(refreshes, x => x.Phase == BatchSourcePhase.Ready);
        var row = Assert.Single(f.Sources.All); Assert.Equal(BatchSourcePhase.Ready, row.Phase); Assert.Equal(5, row.Items); Assert.Equal(bytes.Length, row.Bytes);
        var manifest = f.Sources.ReadManifest(id); Assert.Equal(UploadCollision.UploadKeepBoth, manifest.Collision);
        Assert.Equal(-1, manifest.Items[0].Parent); Assert.Contains(manifest.Items, x => x.IsDirectory && x.Name == "empty");
        var index = manifest.Items.IndexOf(manifest.Items.Single(x => x.Name == "notes.bin"));
        Assert.Equal(SHA256.HashData(bytes), manifest.Items[index].Sha256.ToByteArray());
        using (var source = f.Sources.OpenFile(id, index)) Assert.Equal(bytes[..StorageRules.MaxReadRange], source.ReadRange(0, StorageRules.MaxReadRange));
        File.WriteAllText(Path.Combine(f.Selected, "nested", "notes.bin"), "owner changed original after staging");
        manifest.Items[index].Name = "caller-mutated.bin";
        Assert.Equal("notes.bin", f.Sources.ReadManifest(id).Items[index].Name);
        await f.Reopen(); Assert.Empty(f.Sources.RecoveryIssues); Assert.Equal("notes.bin", f.Sources.ReadManifest(id).Items[index].Name);
        var restored = new List<byte>();
        using (var source = f.Sources.OpenFile(id, index))
            for (var offset = 0; offset < bytes.Length; offset += StorageRules.MaxReadRange) restored.AddRange(source.ReadRange(offset, Math.Min(StorageRules.MaxReadRange, bytes.Length - offset)));
        Assert.Equal(bytes, restored.ToArray());
        Assert.Equal("owner changed original after staging", File.ReadAllText(Path.Combine(f.Selected, "nested", "notes.bin")));
    }
    [Fact]
    public async Task Changed_pairing_private_bytes_manifest_and_metadata_are_refused()
    {
        await using var f = new Fixture(); File.WriteAllBytes(Path.Combine(f.Selected, "file.bin"), [1, 2, 3]);
        var id = await f.Sources.PrepareAsync([f.Selected], f.Authority, UploadCollision.UploadFail);
        f.Current = false; Assert.Throws<InvalidOperationException>(() => f.Sources.ReadManifest(id)); f.Current = true;
        File.WriteAllBytes(f.Private(id, "1.bin"), [9, 9, 9]);
        Assert.Throws<InvalidDataException>(() => f.Sources.OpenFile(id, 1));
        await f.Reopen(); Assert.NotEmpty(f.Sources.RecoveryIssues); await Assert.ThrowsAsync<IOException>(() => f.Sources.PrepareAsync([f.Selected], f.Authority, UploadCollision.UploadFail));
    }
    [Fact]
    public async Task Damaged_parent_metadata_does_not_adopt_or_overwrite_retained_private_files()
    {
        await using var f = new Fixture(); File.WriteAllBytes(Path.Combine(f.Selected, "file.bin"), [1, 2, 3]);
        var id = await f.Sources.PrepareAsync([f.Selected], f.Authority, UploadCollision.UploadSkip);
        var path = Path.Combine(f.Profile, "outgoing-batch-copies", id + ".json"); File.WriteAllText(path, "{");
        Assert.Throws<IOException>(() => f.Sources.ReadManifest(id)); await f.Reopen(); Assert.NotEmpty(f.Sources.RecoveryIssues);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(f.Private(id, "1.bin"))); Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(f.Selected, "file.bin")));
    }
    [Fact]
    public async Task Private_application_profile_is_a_reported_exclusion_when_an_ancestor_is_selected()
    {
        await using var f = new Fixture(); File.WriteAllText(Path.Combine(f.Profile, "identity-secret.txt"), "test secret");
        var id = await f.Sources.PrepareAsync([f.Root], f.Authority, UploadCollision.UploadKeepBoth);
        var manifest = f.Sources.ReadManifest(id); var profile = Assert.Single(manifest.Items, x => x.Name == "profile");
        Assert.True(profile.IsDirectory); Assert.NotEmpty(profile.Error); Assert.DoesNotContain(manifest.Items, x => x.Name == "identity-secret.txt");
        Assert.Equal("test secret", File.ReadAllText(Path.Combine(f.Profile, "identity-secret.txt")));
    }
    [Fact]
    public async Task Cancelled_preparation_keeps_a_failed_record_and_never_changes_the_source()
    {
        await using var f = new Fixture(); File.WriteAllBytes(Path.Combine(f.Selected, "file.bin"), [1, 2, 3]);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Sources.PrepareAsync([f.Selected], f.Authority, UploadCollision.UploadFail, cancellation.Token));
        Assert.Equal(BatchSourcePhase.Failed, Assert.Single(f.Sources.All).Phase); await f.Reopen(); Assert.Equal(BatchSourcePhase.Failed, Assert.Single(f.Sources.All).Phase);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(f.Selected, "file.bin")));
    }
    [Fact]
    public async Task An_unbound_folder_beside_a_failed_intent_is_not_adopted_during_cold_load()
    {
        await using var f = new Fixture(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Sources.PrepareAsync([f.Selected], f.Authority, UploadCollision.UploadFail, cancellation.Token));
        var row = Assert.Single(f.Sources.All); Assert.Equal("", row.DirectoryIdentity);
        var path = f.Private(row.Id, "owner-added.txt"); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "keep");
        await f.Reopen(); Assert.NotEmpty(f.Sources.RecoveryIssues);
        await Assert.ThrowsAsync<IOException>(() => f.Sources.PrepareAsync([f.Selected], f.Authority, UploadCollision.UploadFail)); Assert.Equal("keep", File.ReadAllText(path));
    }
    [Fact]
    public async Task Modified_manifest_cannot_change_the_retained_intent()
    {
        await using var f = new Fixture(); File.WriteAllBytes(Path.Combine(f.Selected, "file.bin"), [1]);
        var id = await f.Sources.PrepareAsync([f.Selected], f.Authority, UploadCollision.UploadFail);
        var path = f.Private(id, "manifest.bin"); var bytes = File.ReadAllBytes(path); bytes[^1] ^= 1; File.WriteAllBytes(path, bytes);
        Assert.Throws<InvalidDataException>(() => f.Sources.ReadManifest(id));
        await f.Reopen(); Assert.NotEmpty(f.Sources.RecoveryIssues); Assert.True(File.Exists(f.Private(id, "1.bin")));
    }
    [Fact]
    public async Task Replaced_snapshot_folder_cannot_be_adopted_even_with_the_original_files()
    {
        await using var f = new Fixture(); File.WriteAllBytes(Path.Combine(f.Selected, "file.bin"), [1]);
        var id = await f.Sources.PrepareAsync([f.Selected], f.Authority, UploadCollision.UploadFail);
        var folder = Path.GetDirectoryName(f.Private(id, "manifest.bin"))!; var preserved = folder + ".preserved";
        Directory.Move(folder, preserved); Directory.CreateDirectory(folder);
        foreach (var path in Directory.EnumerateFiles(preserved)) File.Move(path, Path.Combine(folder, Path.GetFileName(path)));
        Assert.Throws<InvalidDataException>(() => f.Sources.ReadManifest(id)); Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(f.Private(id, "1.bin")));
    }
    [Fact]
    public async Task Reviewed_cleanup_is_single_use_and_keeps_originals_even_after_pairing_revocation()
    {
        await using var f = new Fixture(); File.WriteAllBytes(Path.Combine(f.Selected, "file.bin"), [1, 2, 3]);
        var id = await f.Sources.PrepareAsync([f.Selected], f.Authority, UploadCollision.UploadFail); f.Current = false;
        var review = f.Sources.ReviewRemoval(id); Assert.Equal(1, review.Files); Assert.Equal(3, review.Bytes);
        f.Sources.RemoveReviewed(review); Assert.Empty(f.Sources.All); Assert.False(Directory.Exists(Path.GetDirectoryName(f.Private(id, "manifest.bin"))));
        Assert.Throws<IOException>(() => f.Sources.RemoveReviewed(review)); Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(f.Selected, "file.bin")));
        await f.Reopen(); Assert.Empty(f.Sources.RecoveryIssues); Assert.Empty(f.Sources.All);
    }
    [Fact]
    public async Task A_changed_review_or_unreviewed_entry_cannot_remove_any_snapshot()
    {
        await using var f = new Fixture(); File.WriteAllBytes(Path.Combine(f.Selected, "file.bin"), [1, 2, 3]);
        var id = await f.Sources.PrepareAsync([f.Selected], f.Authority, UploadCollision.UploadFail); var review = f.Sources.ReviewRemoval(id);
        Assert.Throws<IOException>(() => f.Sources.RemoveReviewed(review with { Bytes = 4 }));
        review = f.Sources.ReviewRemoval(id); File.WriteAllText(f.Private(id, "owner-added.txt"), "keep");
        Assert.Throws<IOException>(() => f.Sources.RemoveReviewed(review)); Assert.Equal("keep", File.ReadAllText(f.Private(id, "owner-added.txt")));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(f.Private(id, "1.bin"))); Assert.Single(f.Sources.All);
    }
    [Fact]
    public async Task Verified_reader_holds_private_bytes_against_writers_and_checks_current_authority_per_range()
    {
        await using var f = new Fixture(); File.WriteAllBytes(Path.Combine(f.Selected, "file.bin"), [1, 2, 3]);
        var id = await f.Sources.PrepareAsync([f.Selected], f.Authority, UploadCollision.UploadFail);
        using (var source = f.Sources.OpenFile(id, 1))
        {
            Assert.Throws<IOException>(() => File.WriteAllBytes(f.Private(id, "1.bin"), [9, 9, 9]));
            Assert.Throws<InvalidOperationException>(() => f.Sources.ReviewRemoval(id));
            f.Current = false; Assert.Throws<InvalidOperationException>(() => source.ReadRange(0, 3)); f.Current = true;
            Assert.Equal(new byte[] { 1, 2, 3 }, source.ReadRange(0, 3));
        }
        File.WriteAllBytes(f.Private(id, "1.bin"), [9, 9, 9]); var changed = f.Sources.ReviewRemoval(id); f.Sources.RemoveReviewed(changed);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(f.Selected, "file.bin")));
    }
    [Fact]
    public async Task Native_owner_lease_prevents_a_second_manager_without_releasing_the_first()
    {
        await using var f = new Fixture(); Assert.Throws<StorageException>(() => new WindowsBatchSources(f.Profile, _ => true));
        var id = await f.Sources.PrepareAsync([f.Selected], f.Authority, UploadCollision.UploadFail); Assert.Single(f.Sources.ReadManifest(id).Items);
    }
    [Fact]
    public async Task Full_checksum_rejects_changed_private_bytes_even_if_the_timestamp_was_restored()
    {
        await using var f = new Fixture(); File.WriteAllBytes(Path.Combine(f.Selected, "file.bin"), [1, 2, 3]);
        var id = await f.Sources.PrepareAsync([f.Selected], f.Authority, UploadCollision.UploadFail); var path = f.Private(id, "1.bin"); var timestamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllBytes(path, [9, 9, 9]); File.SetLastWriteTimeUtc(path, timestamp);
        Assert.Throws<InvalidDataException>(() => f.Sources.OpenFile(id, 1)); Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(f.Selected, "file.bin")));
    }
    [Fact]
    public async Task Locked_and_sensitive_entries_have_explicit_errors_without_copying_their_contents()
    {
        await using var f = new Fixture(); Directory.CreateDirectory(Path.Combine(f.Selected, ".ssh")); File.WriteAllText(Path.Combine(f.Selected, ".ssh", "secret.txt"), "test secret");
        var locked = Path.Combine(f.Selected, "locked.bin"); File.WriteAllBytes(locked, [1, 2, 3]); File.WriteAllBytes(Path.Combine(f.Selected, "safe.bin"), [7]);
        using var owner = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var id = await f.Sources.PrepareAsync([f.Selected], f.Authority, UploadCollision.UploadFail); var manifest = f.Sources.ReadManifest(id);
        Assert.NotEmpty(Assert.Single(manifest.Items, x => x.Name == ".ssh").Error); Assert.NotEmpty(Assert.Single(manifest.Items, x => x.Name == "locked.bin").Error);
        Assert.DoesNotContain(manifest.Items, x => x.Name == "secret.txt"); Assert.Equal(1, Assert.Single(f.Sources.All).Bytes);
        var index = manifest.Items.IndexOf(manifest.Items.Single(x => x.Name == "safe.bin")); using var source = f.Sources.OpenFile(id, index); Assert.Equal(new byte[] { 7 }, source.ReadRange(0, 1));
    }
    [Fact]
    public async Task Oversized_item_plan_is_refused_before_private_parts_and_its_failed_record_can_be_reviewed()
    {
        await using var f = new Fixture(); for (var index = 0; index < 4096; index++) File.WriteAllBytes(Path.Combine(f.Selected, $"file-{index}.bin"), []);
        await Assert.ThrowsAsync<IOException>(() => f.Sources.PrepareAsync([f.Selected], f.Authority, UploadCollision.UploadFail));
        var row = Assert.Single(f.Sources.All); Assert.Equal(BatchSourcePhase.Failed, row.Phase); Assert.Equal("", row.DirectoryIdentity);
        var review = f.Sources.ReviewRemoval(row.Id); Assert.Equal(0, review.Files); f.Sources.RemoveReviewed(review); Assert.Empty(f.Sources.All); Assert.Equal(4096, Directory.EnumerateFiles(f.Selected).Count());
    }
    [Fact]
    public async Task Full4096ItemPrivateSnapshotReopensBeyondOrdinaryUploadReceiptLimits()
    {
        await using var f = new Fixture(); var bytes = new byte[] { 17, 31, 47 };
        for (var index = 0; index < 4095; index++) File.WriteAllBytes(Path.Combine(f.Selected, $"file-{index:D4}.bin"), bytes);
        var id = await f.Sources.PrepareAsync([f.Selected], f.Authority, UploadCollision.UploadKeepBoth);
        var row = Assert.Single(f.Sources.All); Assert.Equal(4096, row.Items); Assert.Equal(4095 * 3, row.Bytes); Assert.Equal(4096, f.Sources.ReadManifest(id).Items.Count);
        await f.Reopen(); Assert.Empty(f.Sources.RecoveryIssues); using (var source = f.Sources.OpenFile(id, 4095)) Assert.Equal(bytes, source.ReadRange(0, 3));
        Assert.Equal(4095, Directory.EnumerateFiles(f.Selected).Count());
    }
}
