using IConnect.Core;
using IConnect.Storage.Windows;
using System.Text.Json;
using Xunit;

namespace IConnect.Tests;

public sealed class TransferNetworkSettingsTests : IDisposable
{
    private readonly string sandbox;
    private string SettingsPath => Path.Combine(sandbox, "transfer-network", "settings.json");
    public TransferNetworkSettingsTests()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory); while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "Directory.Build.props"))) repo = repo.Parent;
        sandbox = Path.Combine(repo!.FullName, ".local", "network-settings-tests", ProtocolRules.NewToken()); Directory.CreateDirectory(sandbox);
    }
    [Fact]
    public void Defaults_are_offline_independent_and_preferences_survive_restart_without_sharing_authority()
    {
        using (var settings = new WindowsTransferNetworkSettings(sandbox)) { Assert.True(settings.Healthy); Assert.Equal(new(), settings.Preferences); settings.Policy.RequireContent(); settings.Set(new(65536, true)); Assert.Throws<StorageException>(settings.Policy.RequireContent); }
        using var reopened = new WindowsTransferNetworkSettings(sandbox); Assert.Equal(new(65536, true), reopened.Preferences); reopened.Policy.ObserveNetwork(TransferNetworkCost.Unrestricted); reopened.Policy.RequireContent();
        Assert.Empty(new StorageGrantStore(sandbox).All); Assert.False(File.Exists(Path.Combine(sandbox, "trusted-peers.json")));
        var data = File.ReadAllText(SettingsPath); Assert.DoesNotContain(sandbox, data); Assert.DoesNotContain("peer", data, StringComparison.OrdinalIgnoreCase);
    }
    [Theory]
    [InlineData("corrupt")] [InlineData("schema")] [InlineData("oversize")] [InlineData("staged")] [InlineData("invalid-rate")]
    public void Damaged_settings_pause_only_remote_content_and_support_explicit_preserve_reset(string damage)
    {
        using (var settings = new WindowsTransferNetworkSettings(sandbox)) settings.Set(new(65536, true));
        if (damage == "staged") File.WriteAllText(SettingsPath + ".new", "uncommitted");
        else File.WriteAllText(SettingsPath, damage switch { "schema" => "{\"Schema\":99,\"Preferences\":{}}", "oversize" => new string('x', 32769), "invalid-rate" => "{\"Schema\":1,\"Preferences\":{\"BytesPerSecond\":1}}", _ => "{broken" });
        var original = Path.Combine(sandbox, "original.txt"); File.WriteAllText(original, "owner data");
        using var reopened = new WindowsTransferNetworkSettings(sandbox); Assert.False(reopened.Healthy); Assert.True(reopened.CanReset); Assert.Throws<StorageException>(reopened.Policy.RequireContent);
        reopened.PreserveAndReset(); Assert.True(reopened.Healthy); Assert.Equal(new(), reopened.Preferences); reopened.Policy.RequireContent(); Assert.Equal(1, reopened.PreservedRecords);
        reopened.ClearPreserved(); Assert.Equal(0, reopened.PreservedRecords); Assert.Equal("owner data", File.ReadAllText(original));
    }
    [Fact]
    public void Concurrent_owner_and_changed_settings_are_refused_without_overwriting_the_other_record()
    {
        using var settings = new WindowsTransferNetworkSettings(sandbox); settings.Set(new(65536)); Assert.ThrowsAny<IOException>(() => new WindowsTransferNetworkSettings(sandbox));
        var other = JsonSerializer.Serialize(new { Schema = 1, Preferences = new TransferNetworkPreferences(131072, true) }); File.WriteAllText(SettingsPath, other);
        Assert.Throws<IOException>(() => settings.Set(new())); Assert.Equal(other, File.ReadAllText(SettingsPath)); Assert.False(settings.Healthy); Assert.Throws<StorageException>(settings.Policy.RequireContent);
    }
    [Fact]
    public void Invalid_owner_choice_does_not_change_saved_controls_and_observer_failure_does_not_prevent_save()
    {
        using var settings = new WindowsTransferNetworkSettings(sandbox); settings.Changed += () => throw new InvalidOperationException("Generated observer failure"); settings.Set(new(131072, true));
        var expected = File.ReadAllBytes(SettingsPath); Assert.Throws<InvalidDataException>(() => settings.Set(new(1))); Assert.Equal(expected, File.ReadAllBytes(SettingsPath)); Assert.True(settings.Healthy);
    }
    [Fact]
    public void Missing_saved_preferences_do_not_silently_enable_metered_content_and_reset_requires_them_to_remain_absent()
    {
        using (var settings = new WindowsTransferNetworkSettings(sandbox)) settings.Set(new(65536, true)); File.Delete(SettingsPath);
        using (var missing = new WindowsTransferNetworkSettings(sandbox))
        {
            Assert.True(missing.Missing); Assert.True(missing.CanReset); Assert.False(missing.Healthy); Assert.Throws<StorageException>(missing.Policy.RequireContent);
            File.WriteAllText(SettingsPath, "reappeared record"); Assert.Throws<IOException>(missing.PreserveAndReset); Assert.Equal("reappeared record", File.ReadAllText(SettingsPath)); File.Delete(SettingsPath);
            missing.PreserveAndReset(); Assert.True(missing.Healthy); Assert.False(missing.Missing); missing.Policy.RequireContent(); Assert.True(File.Exists(SettingsPath));
        }
        using var reopened = new WindowsTransferNetworkSettings(sandbox); Assert.True(reopened.Healthy); Assert.Equal(new(), reopened.Preferences);
    }
    [Fact]
    public async Task Damaged_network_preferences_do_not_block_an_actual_offline_local_file_copy()
    {
        var profile = Path.Combine(sandbox, "offline-profile"); var source = Path.Combine(sandbox, "generated-source"); var destination = Path.Combine(sandbox, "generated-destination");
        Directory.CreateDirectory(source); Directory.CreateDirectory(destination); File.WriteAllText(Path.Combine(source, "salama.txt"), "Generated offline copy with remote content paused.");
        using (var first = new WindowsTransferNetworkSettings(profile)) first.Set(new(65536, true)); File.WriteAllText(Path.Combine(profile, "transfer-network", "settings.json"), "{broken");
        using var controls = new WindowsTransferNetworkSettings(profile); Assert.Throws<StorageException>(controls.Policy.RequireContent);
        using var browser = new LocalFileLocations(profile); browser.Add(source);
        var root = Assert.Single((await browser.RequestAsync(new() { Operation = IConnect.Protocol.FileOperation.ListRoots }, default)).Entries);
        var entry = Assert.Single((await browser.RequestAsync(new() { Operation = IConnect.Protocol.FileOperation.ListDirectory, NodeId = root.Id }, default)).Entries); var selected = await browser.CaptureCopyAsync(entry);
        await using var copies = new WindowsLocalCopies(profile); var id = copies.Prepare(selected); copies.ChooseDestination(id, destination, DownloadCollision.KeepBoth); await copies.WaitAsync(id).WaitAsync(TimeSpan.FromSeconds(25));
        Assert.Equal(DownloadPhase.Completed, Assert.Single(copies.Jobs).Transfer!.Phase); Assert.Equal(File.ReadAllBytes(selected.SourceLocation), File.ReadAllBytes(Path.Combine(destination, "salama.txt"))); Assert.False(controls.Healthy);
    }
    public void Dispose()
    {
        var full = Path.GetFullPath(sandbox); if (!full.Contains("\\.local\\network-settings-tests\\", StringComparison.Ordinal)) throw new IOException("Unexpected test cleanup target."); Directory.Delete(full, true);
    }
}
