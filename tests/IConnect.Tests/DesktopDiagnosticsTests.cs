using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IConnect.AppModel;
using IConnect.Core;
using IConnect.Protocol;
using IConnect.Storage.Windows;
using Xunit;

namespace IConnect.Tests;

public sealed partial class DesktopModelTests
{
    private const string DiagnosticSecret = "NEVER-EXPORT-PRIVATE-74bd5ef";
    private string DiagnosticOutput()
    { var output = Path.Combine(directory, "generated-report-output"); Directory.CreateDirectory(output); return output; }
    [Fact]
    public async Task Diagnostic_report_uses_only_allowlisted_counts_and_enum_states_without_settings_receipts_content_or_raw_errors()
    {
        using var phone = DeviceIdentity.Ephemeral(DiagnosticSecret); SeedOwnerTrust(phone);
        LocalState.Write(Path.Combine(state, "activity.json"), new[] { new ActivityEntry(ProtocolRules.NewToken(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "Files", DiagnosticSecret, phone.Id) });
        var original = Path.Combine(directory, DiagnosticSecret); Directory.CreateDirectory(original);
        var file = Path.Combine(original, DiagnosticSecret + ".txt"); File.WriteAllText(file, DiagnosticSecret + " content");
        await using var model = new DesktopController(state, action => action());
        model.SaveSettings(new DesktopSettings(DiagnosticSecret, "https://never-export-private-74bd5ef.example", "stun:never-export-private-74bd5ef.example:3478"));
        await model.AddGrantAsync(phone.Id, original, DiagnosticSecret, StoragePermission.ReadOnly, [], null, true);
        await model.PrepareOutgoingFile(phone.Id, file); await model.PrepareOutgoingBatch(phone.Id, [original], UploadCollision.UploadKeepBoth);
        typeof(DesktopController).GetProperty(nameof(DesktopController.Diagnostic))!.SetValue(model, DiagnosticSecret + " raw exception");
        var grants = model.Grants.Select(x => x.Id).ToArray(); var output = DiagnosticOutput(); var review = await model.ReviewDiagnostics();
        Assert.Empty(Directory.GetFiles(output));
        var json = review.Json; using var parsed = JsonDocument.Parse(json); var root = parsed.RootElement;
        Assert.Equal(1, root.GetProperty("schema").GetInt32()); Assert.Equal("windows", root.GetProperty("platform").GetString());
        Assert.Equal(1, root.GetProperty("work").GetProperty("privateSends").GetProperty("total").GetInt32());
        Assert.Equal(1, root.GetProperty("work").GetProperty("privateBatches").GetProperty("total").GetInt32());
        Assert.True(root.GetProperty("connection").GetProperty("diagnosticReported").GetBoolean());
        Assert.False(root.GetProperty("localActivity").GetProperty("entriesIncluded").GetBoolean());
        foreach (var secret in new[] { DiagnosticSecret, phone.Id, model.DeviceId, original, file, model.Settings.Coordinator, model.Settings.Stun,
            Assert.Single(model.OutgoingCopies).Hash, Assert.Single(model.PrivateOutgoingBatches).Hash }) Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);
        var path = await model.ExportDiagnostics(review, output); Assert.Equal(Encoding.UTF8.GetBytes(json), File.ReadAllBytes(path));
        Assert.Equal(grants, model.Grants.Select(x => x.Id)); Assert.False(model.Connected); Assert.False(model.PeerAuthenticated);
        Assert.Equal(DiagnosticSecret + " content", File.ReadAllText(file));
        await Assert.ThrowsAsync<IOException>(() => model.ExportDiagnostics(review, output)); Assert.Single(Directory.GetFiles(output));
    }
    [Fact]
    public async Task Canceled_and_forged_diagnostic_reviews_never_write_files_and_cannot_be_replayed()
    {
        await using var model = new DesktopController(state, action => action()); var output = DiagnosticOutput();
        var review = await model.ReviewDiagnostics(); model.CancelDiagnosticReview();
        await Assert.ThrowsAsync<IOException>(() => model.ExportDiagnostics(review, output)); Assert.Empty(Directory.GetFiles(output));
        review = await model.ReviewDiagnostics(); await Assert.ThrowsAsync<IOException>(() => model.ExportDiagnostics(review with { Json = DiagnosticSecret }, output));
        await Assert.ThrowsAsync<IOException>(() => model.ExportDiagnostics(review, output)); Assert.Empty(Directory.GetFiles(output));
        Assert.Empty(model.Grants); Assert.Empty(model.Peers); Assert.False(model.Connected);
    }
    [Fact]
    public async Task Diagnostic_collision_keeps_owner_bytes_and_private_profile_cannot_be_an_export_destination()
    {
        await using var model = new DesktopController(state, action => action()); var output = DiagnosticOutput(); var review = await model.ReviewDiagnostics();
        var existing = Path.Combine(output, review.FileName); File.WriteAllText(existing, DiagnosticSecret);
        await Assert.ThrowsAnyAsync<IOException>(() => model.ExportDiagnostics(review, output)); Assert.Equal(DiagnosticSecret, File.ReadAllText(existing));
        review = await model.ReviewDiagnostics(); await Assert.ThrowsAsync<StorageException>(() => model.ExportDiagnostics(review, state));
        Assert.False(File.Exists(Path.Combine(state, review.FileName))); Assert.Equal(DiagnosticSecret, File.ReadAllText(existing));
    }
    [Fact]
    public async Task Policy_reset_and_controller_reopen_end_existing_diagnostic_reviews()
    {
        var output = DiagnosticOutput(); DiagnosticReportReview review;
        await using (var model = new DesktopController(state, action => action()))
        {
            review = await model.ReviewDiagnostics(); await model.ResetOwnerPolicyAsync();
            await Assert.ThrowsAsync<IOException>(() => model.ExportDiagnostics(review, output));
            review = await model.ReviewDiagnostics();
        }
        await using (var model = new DesktopController(state, action => action()))
        {
            await Assert.ThrowsAsync<IOException>(() => model.ExportDiagnostics(review, output)); Assert.Empty(Directory.GetFiles(output));
            var fresh = await model.ReviewDiagnostics(); Assert.True(File.Exists(await model.ExportDiagnostics(fresh, output)));
        }
    }
    [Fact]
    public async Task A_blocked_owner_can_export_status_counts_without_exporting_the_private_recovery_reason()
    {
        await using var model = new DesktopController(state, action => action()); var binding = new WindowsOwnerBinding(state, model.DeviceId);
        binding.RejectMetadata(DiagnosticSecret); Assert.False(model.OwnerPolicyHealthy);
        var review = await model.ReviewDiagnostics(); using var parsed = JsonDocument.Parse(review.Json);
        Assert.False(parsed.RootElement.GetProperty("policy").GetProperty("ownerHealthy").GetBoolean()); Assert.DoesNotContain(DiagnosticSecret, review.Json);
        var path = await model.ExportDiagnostics(review, DiagnosticOutput()); Assert.Equal(Encoding.UTF8.GetBytes(review.Json), File.ReadAllBytes(path));
        Assert.False(model.OwnerPolicyHealthy); Assert.Empty(model.Grants); Assert.False(model.Connected);
    }
}
