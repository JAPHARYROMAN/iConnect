using System.Text.Json;
using IConnect.AppModel;
using IConnect.Core;
using IConnect.Storage.Windows;
using Xunit;

namespace IConnect.Tests;

public sealed partial class DesktopModelTests
{
    [Fact]
    public void Legacy_activity_migrates_to_configurable_bounded_history_and_stricter_retention_is_persisted()
    {
        Directory.CreateDirectory(state); var old = new ActivityEntry(ProtocolRules.NewToken(), DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeMilliseconds(), "Files", "Generated earlier event.", null);
        var recent = old with { Id = ProtocolRules.NewToken(), At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Description = "Generated recent event." };
        LocalState.Write(Path.Combine(state, "activity.json"), new[] { old, recent }); var store = new LocalActivityStore(state);
        Assert.Equal(2, store.All.Count); store.Configure(1); Assert.Equal(recent.Id, Assert.Single(store.All).Id);
        var loaded = new LocalActivityStore(state); Assert.Equal(1, loaded.Days); Assert.Equal(recent, Assert.Single(loaded.All));
        Assert.Equal(2, LocalState.Read<JsonElement>(Path.Combine(state, "activity.json"), 2 * 1024 * 1024).GetProperty("Schema").GetInt32());
        Assert.Throws<ArgumentException>(() => store.Configure(365)); Assert.Equal(1, store.Days);
    }
    [Fact]
    public void Disabling_activity_clears_retained_entries_and_survives_new_events_and_restart()
    {
        var store = new LocalActivityStore(state); store.Add("Connections", "Generated connection."); store.Configure(0);
        store.Add("Devices", "Generated ignored action."); Assert.Empty(store.All); Assert.Equal(0, store.Days);
        var reopened = new LocalActivityStore(state); Assert.Equal(0, reopened.Days); reopened.Add("Files", "Still ignored."); Assert.Empty(reopened.All);
        reopened.Configure(7); reopened.Add("Files", "Generated new activity."); Assert.Single(new LocalActivityStore(state).All);
    }
    [Fact]
    public void Unreadable_activity_does_not_prevent_initialization_and_is_preserved_before_reset()
    {
        Directory.CreateDirectory(state); var path = Path.Combine(state, "activity.json"); File.WriteAllText(path, "generated malformed activity");
        var staged = path + ".new"; File.WriteAllText(staged, "generated uncommitted activity");
        var keep = Path.Combine(state, "unrelated-owner.txt"); File.WriteAllText(keep, "generated original owner bytes");
        var store = new LocalActivityStore(state); Assert.NotEmpty(store.Issue); Assert.True(store.CanReset);
        Assert.Throws<IOException>(() => store.Add("Files", "Must not overwrite metadata.")); Assert.Throws<IOException>(store.Clear);
        store.PreserveAndReset(); Assert.Empty(store.Issue); Assert.Equal(30, store.Days); Assert.Empty(store.All);
        var backups = Directory.GetFiles(Path.Combine(state, "recovery"), "activity.json*", SearchOption.AllDirectories);
        Assert.Equal(2, backups.Length); Assert.Contains(backups, x => File.ReadAllText(x) == "generated malformed activity"); Assert.Contains(backups, x => File.ReadAllText(x) == "generated uncommitted activity");
        Assert.Equal("generated original owner bytes", File.ReadAllText(keep)); store.Add("Recovery", "Activity reset completed."); Assert.Single(new LocalActivityStore(state).All);
    }
    [Fact]
    public void Changed_metadata_blocks_recording_without_overwriting_it_and_reset_requires_current_review()
    {
        var store = new LocalActivityStore(state); store.Add("Files", "Generated record."); var path = Path.Combine(state, "activity.json");
        File.AppendAllText(path, " changed outside its owner"); Assert.Throws<IOException>(() => store.Add("Files", "Must not overwrite."));
        Assert.EndsWith(" changed outside its owner", File.ReadAllText(path)); Assert.NotEmpty(store.Issue);
        File.AppendAllText(path, " changed after review"); Assert.Throws<IOException>(store.PreserveAndReset);
        Assert.EndsWith(" changed after review", File.ReadAllText(path)); var reopened = new LocalActivityStore(state); reopened.PreserveAndReset(); Assert.Empty(reopened.All);
    }
    [Fact]
    public void Invalid_peer_text_is_not_persisted_and_failed_stage_creation_keeps_original_activity()
    {
        var store = new LocalActivityStore(state); store.Add("Files", "Generated event.", "iconnect://private-pairing-secret"); Assert.Null(Assert.Single(store.All).PeerId);
        var before = File.ReadAllBytes(Path.Combine(state, "activity.json")); var blocker = Path.Combine(state, "activity.json.new"); Directory.CreateDirectory(blocker);
        Assert.Throws<IOException>(() => store.Add("Files", "Uncommitted record.")); Assert.Equal(before, File.ReadAllBytes(Path.Combine(state, "activity.json"))); Assert.NotEmpty(store.Issue);
        Directory.Delete(blocker); Assert.Single(new LocalActivityStore(state).All);
    }
    [Fact]
    public async Task Native_controller_retention_and_repair_leave_pairing_grants_and_files_untouched()
    {
        var keep = Path.Combine(directory, "generated-owner.txt"); File.WriteAllText(keep, "generated original");
        await using (var model = new DesktopController(state, action => action()))
        {
            model.SaveSettings(new DesktopSettings("Generated activity fixture")); Assert.NotEmpty(model.Activity);
            await model.ConfigureActivity(0); Assert.Empty(model.Activity); model.SaveSettings(new DesktopSettings("Generated activity fixture")); Assert.Empty(model.Activity);
            Assert.Empty(model.Grants); Assert.Empty(model.Peers); Assert.False(model.Connected);
        }
        File.WriteAllText(Path.Combine(state, "activity.json"), "generated unreadable history");
        await using (var model = new DesktopController(state, action => action()))
        {
            Assert.NotEmpty(model.ActivityIssue); Assert.True(model.CanResetActivity); await model.PreserveAndResetActivity(); Assert.Empty(model.ActivityIssue);
            Assert.Equal(30, model.ActivityRetentionDays); Assert.Empty(model.Grants); Assert.Empty(model.Peers); Assert.False(model.Connected);
        }
        Assert.Equal("generated original", File.ReadAllText(keep));
    }
}
