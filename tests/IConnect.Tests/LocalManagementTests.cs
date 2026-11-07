using IConnect.AppModel;
using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using System.Text.Json;
using Xunit;

namespace IConnect.Tests;

public sealed class LocalManagementTests : IDisposable
{
    private readonly string sandbox, source, destination, profile;
    private const string Name = "habari-文書.txt";
    public LocalManagementTests()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "Directory.Build.props"))) repo = repo.Parent;
        sandbox = Path.Combine(repo!.FullName, ".local", "local-management-tests", ProtocolRules.NewToken());
        source = Path.Combine(sandbox, "source"); destination = Path.Combine(sandbox, "destination"); profile = Path.Combine(sandbox, "profile");
        Directory.CreateDirectory(source); Directory.CreateDirectory(destination);
        Directory.CreateDirectory(Path.Combine(source, "Reports", "empty-空"));
        File.WriteAllText(Path.Combine(source, Name), "Generated local owner data.\nSalama!\n");
        File.WriteAllText(Path.Combine(source, "Reports", "nested.txt"), "Nested fixture");
    }
    private async Task<LocalCopySelection> Capture(string name = Name, string? from = null, bool rootOnly = false)
    {
        using var browser = new LocalFileLocations(profile); var location = browser.Add(from ?? source);
        var root = (await browser.RequestAsync(new() { Operation = FileOperation.ListRoots }, default)).Entries.Single(x => x.GrantId == location.Id);
        var entry = rootOnly ? root : (await browser.RequestAsync(new() { Operation = FileOperation.ListDirectory, NodeId = root.Id }, default)).Entries.Single(x => x.Name == name);
        return await browser.CaptureBatchItemAsync(entry);
    }
    private static void Succeeded(MutationInfo receipt) { Assert.Equal(MutationPhase.ChangeSucceeded, receipt.Phase); Assert.Equal(FileStatus.Ok, receipt.Status); }
    [Fact]
    public async Task Offline_empty_vault_cleanup_preserves_restored_folder_and_receipt()
    {
        var selected = await Capture("Reports");
        using (var manager = new WindowsLocalManagement(profile))
        {
            Succeeded(manager.Confirm(manager.ReviewDelete(selected)));
            var row = Assert.Single(manager.Deleted);
            Succeeded(manager.Restore(row.Item.Id));
            var review = manager.ReviewEmptyVault(row.Item.Id);
            Assert.NotNull(review.Token);
            manager.RemoveEmptyVault(review.Token);
            Assert.False(Directory.Exists(Path.GetDirectoryName(row.SavedLocation)));
            Assert.True(Directory.Exists(Path.Combine(selected.SourceLocation, "empty-空")));
            Assert.Equal("Nested fixture", File.ReadAllText(Path.Combine(selected.SourceLocation, "nested.txt")));
        }
        using var reopened = new WindowsLocalManagement(profile);
        var record = Assert.Single(reopened.Deleted);
        Assert.Equal(DeletedState.DeletedRestored, record.Item.State);
        Assert.Null(reopened.ReviewEmptyVault(record.Item.Id).Token);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Offline_alternate_restore_preserves_the_file_or_folder_and_records_the_destination(bool folder)
    {
        var selected = await Capture(folder ? "Reports" : Name);
        var output = Path.Combine(destination, "restored-空");
        using (var manager = new WindowsLocalManagement(profile))
        {
            Succeeded(manager.Confirm(manager.ReviewDelete(selected)));
            var row = Assert.Single(manager.Deleted);
            var review = manager.ReviewAlternateRestore(row.Item.Id, destination, "restored-空");
            Assert.Equal(output, review.Destination);
            Succeeded(manager.ConfirmAlternateRestore(review.Token));
            Assert.Throws<IOException>(() => manager.ConfirmAlternateRestore(review.Token));
            Assert.False(Path.Exists(row.SavedLocation));
        }
        using var reopened = new WindowsLocalManagement(profile);
        var restored = Assert.Single(reopened.Deleted);
        Assert.Equal(output, restored.RestoredLocation);
        Assert.Equal("Recorded object found", reopened.InspectDeleted(restored.Item.Id).RestoredStatus);
        Assert.False(Path.Exists(selected.SourceLocation));
        if (folder)
        {
            Assert.True(Directory.Exists(Path.Combine(output, "empty-空")));
            Assert.Equal("Nested fixture", File.ReadAllText(Path.Combine(output, "nested.txt")));
        }
        else Assert.Equal("Generated local owner data.\nSalama!\n", File.ReadAllText(output));
    }
    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task Reviewed_rename_and_same_volume_move_preserve_real_contents_and_receipts(bool folder, bool move)
    {
        var selected = await Capture(folder ? "Reports" : Name); var events = new List<WindowsActivityEvent>();
        using (var manager = new WindowsLocalManagement(profile) { Activity = events.Add })
        {
            var review = move ? manager.ReviewMove(selected, destination, "renamed-空") : manager.ReviewRename(selected, "renamed-空");
            Succeeded(manager.Confirm(review)); Assert.Throws<IOException>(() => manager.Confirm(review));
            var output = Path.Combine(move ? destination : source, "renamed-空");
            if (folder) { Assert.True(Directory.Exists(Path.Combine(output, "empty-空"))); Assert.Equal("Nested fixture", File.ReadAllText(Path.Combine(output, "nested.txt"))); }
            else Assert.Contains("Salama!", File.ReadAllText(output));
            Assert.False(Path.Exists(selected.SourceLocation)); Assert.Single(manager.History);
        }
        using var reopened = new WindowsLocalManagement(profile); var record = Assert.Single(reopened.History);
        Assert.Equal(MutationPhase.ChangeSucceeded, record.Receipt.Phase); Assert.Equal(selected.SourceLocation, record.Source);
        Assert.Empty(new StorageGrantStore(profile).All); Assert.False(File.Exists(Path.Combine(profile, "trusted-peers.json")));
        var activity = Assert.Single(events); Assert.Empty(activity.Peer); Assert.Equal(WindowsActivityKind.LocalFileChange, activity.Kind);
        var serialized = JsonSerializer.Serialize(events); Assert.DoesNotContain(source, serialized); Assert.DoesNotContain(Name, serialized); Assert.DoesNotContain(record.Receipt.Id, serialized);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task New_folder_is_no_replace_and_existing_contents_survive(bool collision)
    {
        if (collision) { Directory.CreateDirectory(Path.Combine(source, "new-空")); File.WriteAllText(Path.Combine(source, "new-空", "kept.txt"), "kept"); }
        var selection = await Capture(rootOnly: true);
        using var manager = new WindowsLocalManagement(profile); var review = manager.ReviewCreateFolder(selection, "new-空");
        var result = manager.Confirm(review); Assert.Equal(collision ? MutationPhase.ChangeFailed : MutationPhase.ChangeSucceeded, result.Phase);
        Assert.True(Directory.Exists(Path.Combine(source, "new-空"))); if (collision) Assert.Equal("kept", File.ReadAllText(Path.Combine(source, "new-空", "kept.txt")));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Rename_and_move_collisions_keep_both_original_items(bool move)
    {
        var selection = await Capture(); var target = move ? destination : source; File.WriteAllText(Path.Combine(target, "kept.txt"), "existing");
        using var manager = new WindowsLocalManagement(profile); var review = move ? manager.ReviewMove(selection, destination, "kept.txt") : manager.ReviewRename(selection, "kept.txt");
        Assert.Equal(MutationPhase.ChangeFailed, manager.Confirm(review).Phase); Assert.Equal("existing", File.ReadAllText(Path.Combine(target, "kept.txt"))); Assert.True(File.Exists(selection.SourceLocation));
    }
    [Theory]
    [InlineData(FileOperation.Rename)] [InlineData(FileOperation.Move)] [InlineData(FileOperation.Delete)]
    public async Task Changed_source_after_review_is_refused_before_any_filesystem_effect(FileOperation operation)
    {
        var selection = await Capture(); using var manager = new WindowsLocalManagement(profile);
        var review = operation == FileOperation.Rename ? manager.ReviewRename(selection, "new.txt") : operation == FileOperation.Move ? manager.ReviewMove(selection, destination) : manager.ReviewDelete(selection);
        File.AppendAllText(selection.SourceLocation, "owner changed it"); Assert.ThrowsAny<IOException>(() => manager.Confirm(review));
        Assert.Contains("owner changed it", File.ReadAllText(selection.SourceLocation)); Assert.Empty(Directory.GetFiles(destination)); Assert.Empty(manager.Deleted);
    }
    [Fact]
    public async Task Replaced_source_parent_and_destination_are_not_adopted_by_path()
    {
        var selection = await Capture(); using var manager = new WindowsLocalManagement(profile); var review = manager.ReviewMove(selection, destination);
        Directory.Move(destination, destination + "-old"); Directory.CreateDirectory(destination);
        Assert.ThrowsAny<IOException>(() => manager.Confirm(review)); Assert.True(File.Exists(selection.SourceLocation)); Assert.Empty(Directory.GetFiles(destination));
        review = manager.ReviewRename(selection, "renamed.txt"); Directory.Move(source, source + "-old"); Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source, Name), "replacement");
        Assert.ThrowsAny<IOException>(() => manager.Confirm(review)); Assert.Equal("replacement", File.ReadAllText(Path.Combine(source, Name))); Assert.False(File.Exists(Path.Combine(source, "renamed.txt")));
    }
    [Theory]
    [InlineData(FileOperation.Rename)] [InlineData(FileOperation.Move)] [InlineData(FileOperation.Delete)]
    public async Task Shortcut_roots_are_protected_from_management(FileOperation operation)
    {
        var selection = await Capture(rootOnly: true); using var manager = new WindowsLocalManagement(profile);
        Assert.Throws<IOException>(() => operation == FileOperation.Rename ? manager.ReviewRename(selection, "renamed") : operation == FileOperation.Move ? manager.ReviewMove(selection, destination) : manager.ReviewDelete(selection));
        Assert.True(Directory.Exists(source)); Assert.Empty(manager.History); Assert.Empty(manager.Deleted);
    }
    [Fact]
    public async Task Folder_cannot_move_into_itself_or_a_descendant()
    {
        var selection = await Capture("Reports"); using var manager = new WindowsLocalManagement(profile);
        Assert.Throws<IOException>(() => manager.ReviewMove(selection, Path.Combine(source, "Reports")));
        Assert.Throws<IOException>(() => manager.ReviewMove(selection, Path.Combine(source, "Reports", "empty-空")));
        Assert.True(File.Exists(Path.Combine(source, "Reports", "nested.txt")));
    }
    [Fact]
    public async Task Changed_review_and_cancelled_confirmation_have_no_authority_and_no_effect()
    {
        var selection = await Capture(); using var manager = new WindowsLocalManagement(profile); var review = manager.ReviewRename(selection, "new.txt");
        Assert.Throws<IOException>(() => manager.Confirm(review with { Destination = Path.Combine(destination, "other.txt") }));
        Assert.Throws<IOException>(() => manager.Confirm(review)); review = manager.ReviewRename(selection, "new.txt");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); Assert.Throws<OperationCanceledException>(() => manager.Confirm(review, cancelled.Token));
        Assert.Throws<IOException>(() => manager.Confirm(review)); Assert.True(File.Exists(selection.SourceLocation)); Assert.Empty(manager.History);
    }
    [Fact]
    public async Task Restart_discards_unconfirmed_review_and_manager_has_one_native_lease()
    {
        var selection = await Capture(); LocalManagementReview review;
        using (var manager = new WindowsLocalManagement(profile)) { review = manager.ReviewRename(selection, "new.txt"); Assert.ThrowsAny<IOException>(() => new WindowsLocalManagement(profile)); }
        using var reopened = new WindowsLocalManagement(profile); Assert.Throws<IOException>(() => reopened.Confirm(review)); Assert.True(File.Exists(selection.SourceLocation));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Saved_items_restore_after_restart_and_shortcut_removal_without_remote_authority(bool folder)
    {
        var selected = await Capture(folder ? "Reports" : Name); string id;
        using (var manager = new WindowsLocalManagement(profile))
        {
            Succeeded(manager.Confirm(manager.ReviewDelete(selected))); var row = Assert.Single(manager.Deleted); id = row.Item.Id;
            Assert.False(Path.Exists(selected.SourceLocation)); Assert.True(Path.Exists(row.SavedLocation));
        }
        using (var shortcuts = new LocalFileLocations(profile)) shortcuts.Remove(shortcuts.All.Single().Id);
        using var reopened = new WindowsLocalManagement(profile); Succeeded(reopened.Restore(id));
        Assert.True(Path.Exists(selected.SourceLocation)); if (folder) Assert.True(Directory.Exists(Path.Combine(selected.SourceLocation, "empty-空")));
        Assert.Equal("Restored or original confirmed", Assert.Single(reopened.Deleted).State); Assert.Empty(new StorageGrantStore(profile).All);
    }
    [Fact]
    public async Task Restore_collision_keeps_saved_and_existing_file_then_allows_a_new_name()
    {
        var selected = await Capture(); using var manager = new WindowsLocalManagement(profile); Succeeded(manager.Confirm(manager.ReviewDelete(selected)));
        var saved = Assert.Single(manager.Deleted); File.WriteAllText(selected.SourceLocation, "existing replacement");
        Assert.NotEqual(MutationPhase.ChangeSucceeded, manager.Restore(saved.Item.Id).Phase); Assert.True(File.Exists(saved.SavedLocation)); Assert.Equal("existing replacement", File.ReadAllText(selected.SourceLocation));
        Succeeded(manager.Restore(saved.Item.Id, "restored.txt")); Assert.Contains("Salama!", File.ReadAllText(Path.Combine(source, "restored.txt")));
    }
    [Fact]
    public async Task Permanent_delete_requires_exact_confirmation_and_fresh_unchanged_contents()
    {
        var selected = await Capture("Reports"); using var manager = new WindowsLocalManagement(profile); Succeeded(manager.Confirm(manager.ReviewDelete(selected)));
        var row = Assert.Single(manager.Deleted); var review = manager.ReviewPurge(row.Item.Id);
        Assert.Throws<IOException>(() => manager.Purge(review.Token, "delete")); Assert.True(Directory.Exists(row.SavedLocation));
        File.WriteAllText(Path.Combine(row.SavedLocation, "later.txt"), "new data after review");
        Assert.Throws<IOException>(() => manager.Purge(review.Token, "DELETE")); Assert.True(File.Exists(Path.Combine(row.SavedLocation, "later.txt")));
        var fresh = manager.ReviewPurge(row.Item.Id); Assert.Equal(2, fresh.Files); Assert.True(manager.Purge(fresh.Token, "DELETE").Complete);
        Assert.False(Directory.Exists(row.SavedLocation)); Assert.Throws<IOException>(() => manager.Purge(fresh.Token, "DELETE")); Assert.Equal("Permanently deleted", Assert.Single(manager.Deleted).State);
    }
    [Fact]
    public async Task Location_confirmation_reconciles_real_saved_copy_without_deleting_it()
    {
        var selected = await Capture(); using var manager = new WindowsLocalManagement(profile); Succeeded(manager.Confirm(manager.ReviewDelete(selected)));
        var row = Assert.Single(manager.Deleted); var inspection = manager.InspectDeleted(row.Item.Id); Assert.True(inspection.CanConfirm);
        manager.ConfirmLocations(inspection.Token); Assert.True(File.Exists(row.SavedLocation)); Assert.Throws<IOException>(() => manager.ConfirmLocations(inspection.Token));
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Missing_or_corrupt_directory_references_block_changes_but_keep_owner_recovery(bool missing)
    {
        var selected = await Capture();
        using (var manager = new WindowsLocalManagement(profile))
        {
            Succeeded(manager.Confirm(manager.ReviewCreateFolder(await Capture(rootOnly: true), "created")));
            Succeeded(manager.Confirm(manager.ReviewDelete(selected)));
        }
        var path = Path.Combine(profile, "local-management", "directories.json"); if (missing) File.Delete(path); else File.WriteAllText(path, "{damaged");
        using var reopened = new WindowsLocalManagement(profile); var issue = Assert.Single(reopened.RecoveryIssues); Assert.False(issue.CanArchive);
        Assert.Throws<IOException>(() => reopened.ReviewRename(selected, "changed"));
        var row = Assert.Single(reopened.Deleted); Succeeded(reopened.Restore(row.Item.Id)); Assert.True(File.Exists(selected.SourceLocation));
    }
    [Fact]
    public async Task Damaged_operation_journal_can_be_preserved_without_erasing_files_or_saved_items()
    {
        var selected = await Capture(); using (var manager = new WindowsLocalManagement(profile)) Succeeded(manager.Confirm(manager.ReviewDelete(selected)));
        File.WriteAllText(Path.Combine(profile, "local-management", "file-operations", "journal.json"), "{broken");
        using var reopened = new WindowsLocalManagement(profile); var issue = Assert.Single(reopened.RecoveryIssues); Assert.True(issue.CanArchive);
        reopened.Preserve(issue.Id); Assert.Empty(reopened.RecoveryIssues); Assert.Equal(1, reopened.PreservedRecords);
        var row = Assert.Single(reopened.Deleted); Succeeded(reopened.Restore(row.Item.Id)); reopened.ClearPreserved(); Assert.Equal(0, reopened.PreservedRecords); Assert.True(File.Exists(selected.SourceLocation));
    }
    [Fact]
    public async Task Changed_catalog_during_session_is_refused_without_mutating_source()
    {
        var selection = await Capture(); using var manager = new WindowsLocalManagement(profile); var review = manager.ReviewRename(selection, "new.txt");
        File.AppendAllText(Path.Combine(profile, "local-management", "directories.json"), " ");
        Assert.Throws<IOException>(() => manager.Confirm(review)); Assert.True(File.Exists(selection.SourceLocation));
    }
    [Fact]
    public async Task Own_profile_and_folder_containing_it_cannot_be_moved_or_deleted()
    {
        using var manager = new WindowsLocalManagement(profile);
        var selectedProfile = await Capture("profile", sandbox); Assert.ThrowsAny<IOException>(() => manager.ReviewDelete(selectedProfile));
        var ancestor = await Capture(Path.GetFileName(sandbox), Path.GetDirectoryName(sandbox));
        var review = manager.ReviewDelete(ancestor); Assert.ThrowsAny<IOException>(() => manager.Confirm(review)); Assert.True(Directory.Exists(profile));
        var selected = await Capture(); Assert.ThrowsAny<IOException>(() => manager.ReviewMove(selected, profile)); Assert.True(File.Exists(selected.SourceLocation));
    }
    [Fact]
    public async Task Folder_with_hard_link_is_refused_without_touching_either_name()
    {
        var outside = Path.Combine(destination, "outside.txt"); File.WriteAllText(outside, "outside data"); var link = Path.Combine(source, "Reports", "linked.txt");
        Assert.True(CreateHardLinkW(link, outside, 0)); var selected = await Capture("Reports"); using var manager = new WindowsLocalManagement(profile);
        var review = manager.ReviewDelete(selected); Assert.ThrowsAny<IOException>(() => manager.Confirm(review)); Assert.Equal("outside data", File.ReadAllText(outside)); Assert.True(File.Exists(link)); Assert.Empty(manager.Deleted);
    }
    [Fact]
    public async Task Local_controller_runs_review_change_refresh_and_recovery_without_a_peer()
    {
        await using var browser = new LocalExplorerController(profile); await browser.AddAsync(source); await browser.BrowseAsync(Assert.Single(browser.Rows).Entry);
        browser.Select(browser.Rows.Single(x => x.Entry.Name == Name).Entry.Id); var selection = await browser.CaptureManagementAsync();
        var review = await browser.ManageAsync(manager => manager.ReviewRename(selection, "renamed.txt")); Succeeded(await browser.ManageAsync(manager => manager.Confirm(review)));
        await browser.RefreshAfterManagementAsync(); await browser.RefreshManagementAsync(); Assert.True(browser.ManagementLoaded); Assert.Single(browser.ManagementHistory); Assert.Empty(browser.ManagementNotice);
        browser.Select(browser.Rows.Single(x => x.Entry.Name == "renamed.txt").Entry.Id); selection = await browser.CaptureManagementAsync();
        review = await browser.ManageAsync(manager => manager.ReviewDelete(selection)); Succeeded(await browser.ManageAsync(manager => manager.Confirm(review))); await browser.RefreshManagementAsync();
        var row = Assert.Single(browser.DeletedItems); Succeeded(await browser.ManageAsync(manager => manager.Restore(row.Item.Id))); await browser.RefreshAfterManagementAsync();
        Assert.Contains(browser.Rows, x => x.Entry.Name == "renamed.txt"); Assert.False(browser.ManagementBusy); Assert.Empty(new StorageGrantStore(profile).All);
    }
    [Fact]
    public async Task Local_controller_keeps_browsing_when_management_history_needs_review()
    {
        using (var manager = new WindowsLocalManagement(profile)) { }
        File.WriteAllText(Path.Combine(profile, "local-management", "file-operations", "journal.json"), "{broken");
        await using var browser = new LocalExplorerController(profile); await browser.AddAsync(source); await browser.BrowseAsync(Assert.Single(browser.Rows).Entry); await browser.RefreshManagementAsync();
        Assert.True(browser.Healthy); Assert.Contains(browser.Rows, x => x.Entry.Name == Name); Assert.Single(browser.ManagementRecoveryIssues); Assert.Empty(browser.ManagementHistory);
    }
    [Fact]
    public async Task Management_observer_failure_cannot_release_work_early_and_shutdown_waits_for_it()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var browser = new LocalExplorerController(profile); browser.Changed += () => throw new InvalidOperationException("Generated observer failure");
        var work = browser.ManageAsync(_ => { entered.SetResult(); release.Task.GetAwaiter().GetResult(); return 42; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.True(browser.ManagementBusy);
        await Assert.ThrowsAsync<IOException>(() => browser.ManageAsync(_ => 0)); var closing = browser.DisposeAsync().AsTask(); Assert.False(closing.IsCompleted);
        release.SetResult(); Assert.Equal(42, await work.WaitAsync(TimeSpan.FromSeconds(10))); await closing.WaitAsync(TimeSpan.FromSeconds(10));
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string name, string existing, nint security);
    public void Dispose()
    {
        var full = Path.GetFullPath(sandbox); if (!full.Contains("\\.local\\local-management-tests\\", StringComparison.Ordinal)) throw new IOException("Unexpected test cleanup target.");
        Directory.Delete(full, true);
    }
}
