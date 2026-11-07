using IConnect.AppModel;
using IConnect.Core;
using IConnect.Storage.Windows;
using System.Security.Cryptography;
using Xunit;

namespace IConnect.Tests;

public sealed partial class LocalCopyTests
{
    [Fact]
    public async Task Actual_local_payload_observations_stop_before_attachment_checks_and_do_not_advance_receipts()
    {
        var selection = await Capture("payload.bin"); var expected = File.ReadAllBytes(Path.Combine(source, "payload.bin"));
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var copies = new WindowsLocalCopies(profile, new Checks(async (_, _, ct) => { reached.TrySetResult(); await release.Task.WaitAsync(ct); }));
        long now = 0; var rates = new LocalPayloadRates(() => now); var observations = new List<DownloadJob>();
        copies.PayloadChanged += _ => throw new InvalidOperationException("Failing display observer fixture.");
        copies.PayloadChanged += row => { now += 1000; rates.Observe(row, false); observations.Add(row); };
        var id = copies.Prepare(selection); copies.ChooseDestination(id, destination, DownloadCollision.KeepBoth);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var before = copies.Jobs.Single().Transfer!;
        Assert.Equal(DownloadPhase.Finalizing, before.Phase); Assert.False(rates.Active);
        Assert.True(copies.HasFinalizationProgress);
        var finalization = Assert.IsType<IncomingFinalizationProgress>(copies.ReadFinalization(id));
        Assert.Equal(IncomingFinalizationStage.AttachmentCheck, finalization.Stage);
        Assert.Null(finalization.Estimate);
        Assert.Contains(observations, x => x.Phase == DownloadPhase.Downloading);
        Assert.Contains(observations, x => x.Phase == DownloadPhase.Verifying && x.Offset == x.Size);
        var metadata = Directory.EnumerateFiles(Path.Combine(profile, "local-copies", "receiving", "downloads"), "*.json").Single();
        var hash = SHA256.HashData(File.ReadAllBytes(metadata));
        for (var i = 0; i < 12; i++)
        { var value = rates.Read(before, false); Assert.Equal(PayloadRateState.Received, value.State); Assert.Null(value.RemainingSeconds); }
        Assert.Equal(before, copies.Jobs.Single().Transfer); Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(metadata)));
        Assert.False(File.Exists(Path.Combine(profile, "storage-grants.json"))); Assert.False(File.Exists(Path.Combine(profile, "trusted-peers.json")));
        release.TrySetResult(); var done = await Finish(copies, id); Assert.Equal(DownloadPhase.Completed, done.Phase);
        Assert.Equal(expected, File.ReadAllBytes(Path.Combine(source, "payload.bin"))); Assert.Equal(expected, File.ReadAllBytes(Path.Combine(destination, done.FinalName)));
        Assert.False(rates.Active);
        Assert.False(copies.HasFinalizationProgress);
        Assert.Null(copies.ReadFinalization(id));
    }
}

public sealed partial class DesktopModelTests
{
    [Fact]
    public async Task Controller_local_copy_estimates_remain_separate_from_remote_network_pause_and_survive_reopen_without_live_samples()
    {
        var documents = Path.Combine(directory, "payload-estimate-source"); var target = Path.Combine(directory, "payload-estimate-destination");
        Directory.CreateDirectory(documents); Directory.CreateDirectory(target); var expected = new byte[128 * 1024 + 7]; RandomNumberGenerator.Fill(expected);
        File.WriteAllBytes(Path.Combine(documents, "fixture.bin"), expected);
        string id;
        await using (var model = new DesktopController(state, action => action()))
        {
            model.TransferNetwork!.Set(new(65536, true)); Assert.Throws<StorageException>(model.NetworkTraffic.RequireContent);
            var browser = model.LocalFiles; await browser.AddAsync(documents); await browser.BrowseAsync(Assert.Single(browser.Rows).Entry);
            browser.Select(Assert.Single(browser.Rows).Entry.Id); var selection = await browser.CaptureCopyAsync(); var copies = browser.Copies!;
            id = copies.Prepare(selection); copies.ChooseDestination(id, target, DownloadCollision.KeepBoth);
            await copies.WaitAsync(id).WaitAsync(TimeSpan.FromSeconds(20)); var row = Assert.Single(copies.Jobs).Transfer!;
            Assert.Equal(DownloadPhase.Completed, row.Phase); Assert.False(browser.CopyingPayload);
            Assert.Null(browser.CopyObservedEstimate(row).RemainingSeconds); Assert.DoesNotContain("limit", browser.CopyPayloadText(row));
            Assert.Equal(expected, File.ReadAllBytes(Path.Combine(target, row.FinalName))); Assert.Empty(model.Grants); Assert.Empty(model.Peers); Assert.False(model.Connected);
        }
        await using var reopened = new DesktopController(state, action => action()); var saved = Assert.Single(reopened.LocalFiles.Copies!.Jobs);
        Assert.Equal(id, saved.Id); Assert.False(reopened.LocalFiles.CopyingPayload); Assert.Null(reopened.LocalFiles.CopyObservedEstimate(saved.Transfer!).BytesPerSecond);
        Assert.Empty(reopened.Grants); Assert.False(reopened.Connected);
    }
}
