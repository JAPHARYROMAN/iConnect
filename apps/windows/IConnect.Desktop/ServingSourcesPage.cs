using IConnect.Protocol;
using Microsoft.UI.Xaml.Controls;

namespace IConnect.Desktop;

public sealed partial class MainWindow
{
    private int servingSourcePage;
    private void RenderServingSources()
    {
        var sources = Model.ServingSources; if (sources.Count == 0) return;
        Page.Children.Add(Heading("Prepared reads from this computer"));
        Page.Children.Add(Body("These approvals let paired devices download an existing shared file. Stopping one approval frees its slot and keeps original files and copies already received. Shared-folder permissions stay unchanged and can allow a new request; pause or revoke that folder to end its wider access. Expired approvals are also removed when preparing another source or reopening iConnect."));
        Page.Children.Add(Row(Action("Refresh prepared sources", () => { Model.RefreshServingSources(); return Task.CompletedTask; }),
            Action("Remove expired source approvals", async () => { await Model.RemoveExpiredServingSources(); }, Model.OwnerPolicyHealthy && sources.Any(x => x.Expired))));
        var pages = Math.Max(1, (sources.Count + 31) / 32); servingSourcePage = Math.Clamp(servingSourcePage, 0, pages - 1);
        Page.Children.Add(Row(Action("Previous prepared sources", () => { servingSourcePage--; Render(); return Task.CompletedTask; }, servingSourcePage > 0),
            Body($"Page {servingSourcePage + 1} / {pages} · {sources.Count} retained approvals"),
            Action("Next prepared sources", () => { servingSourcePage++; Render(); return Task.CompletedTask; }, servingSourcePage + 1 < pages)));
        foreach (var source in sources.Skip(servingSourcePage * 32).Take(32))
        {
            var status = source.Expired ? "Expired" : source.State == DownloadState.DownloadFailed ? "Preparation failed" : source.State == DownloadState.DownloadReady ? "Ready for a paired device" : source.Preparing ? "Preparing" : "Preparation stopped";
            Page.Children.Add(RefreshableCard($"serving/{source.Id}", source.Name, $"{status} · {source.Size:N0} bytes",
                Body($"Source preparation: {source.Prepared:N0} / {source.Size:N0} bytes. This does not measure bytes received by the other device."),
                Body("Paired receiving device"), Selectable(source.Peer),
                Body("Approval ends: " + DateTimeOffset.FromUnixTimeMilliseconds(source.ExpiresAt).ToLocalTime().ToString("g")),
                Action("Stop this prepared read…", async () =>
                {
                    if (await ShowAsync(Dialog("Stop this prepared read?", Column(Body(source.Name), Body("Paired receiving device"), Selectable(source.Peer),
                        Body("This ends only this prepared approval. Original files and any received copies are kept. The receiver may need a freshly reviewed source to continue. Folder permissions are unchanged and can still allow another request.")), "Stop prepared read")) == ContentDialogResult.Primary)
                        await Model.StopServingSource(source.Id);
                }, Model.OwnerPolicyHealthy)));
        }
    }
}
