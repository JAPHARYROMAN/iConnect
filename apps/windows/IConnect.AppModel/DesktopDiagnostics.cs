using System.Text;
using System.Text.Json;
using IConnect.Core;
using IConnect.Storage.Windows;

namespace IConnect.AppModel;

public sealed record DiagnosticReportReview(string Id, string Json)
{ public string FileName => "iConnect-diagnostics-" + Id + ".json"; }

public sealed partial class DesktopController
{
    private readonly object diagnosticGate = new();
    private sealed record DiagnosticPending(DiagnosticReportReview Value, string Scope, long Expires);
    private DiagnosticPending? diagnosticPending;
    private string DiagnosticScope => identity.Id + "." + OwnerPolicyEpoch + "." + (trust.OwnerBinding?.Healthy == true);
    private static object DiagnosticPhases<T, TPhase>(IReadOnlyList<T> rows, Func<T, TPhase> phase, Func<T, bool>? active = null) where TPhase : struct, Enum =>
        new { total = rows.Count, active = active is null ? (int?)null : rows.Count(active), phases = rows.GroupBy(x => Enum.GetName(phase(x)) ?? "unknown")
            .OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal) };
    public Task<DiagnosticReportReview> ReviewDiagnostics() => Task.Run(() =>
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        // Never serialize controller/state/receipt objects, settings, logs or exceptions.
        var issues = TransferRecovery;
        var availability = Availability.Preferences;
        var snapshot = new
        {
            schema = 1, product = "iConnect", platform = "windows",
            appVersion = typeof(DesktopController).Assembly.GetName().Version?.ToString() ?? "0.0.0.0",
            windowsVersion = Environment.OSVersion.Version.ToString(), generatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), bestEffortSnapshot = true,
            connection = new
            {
                coordinator = Connected ? "connected" : Connecting ? "connecting" : "disconnected",
                direct = PeerAuthenticated ? "authenticated" : PeerConnecting ? "connecting" : "disconnected",
                coordinatorConfigured = !string.IsNullOrWhiteSpace(Settings.Coordinator), stunConfigured = !string.IsNullOrWhiteSpace(Settings.Stun),
                diagnosticReported = Diagnostic.Length != 0
            },
            policy = new { ownerHealthy = OwnerPolicyHealthy, pairedDevices = Peers.Count, approvedLocations = Grants.Count, revocationPending = pendingRevocations.Count != 0 },
            availability = new { reconnectEnabled = availability.AutoReconnect, reconnectPaused = availability.Paused,
                mutualPresenceEnabled = Settings.SharePresence, backgroundAvailabilityEnabled = availability.RunInBackground, signInEnabled = availability.StartAtSignIn },
            work = new
            {
                downloads = DiagnosticPhases(downloads.Jobs, x => x.Phase), folderDownloads = DiagnosticPhases(Batches, x => x.Phase),
                privateSends = DiagnosticPhases(OutgoingCopies, x => x.Phase), privateBatches = DiagnosticPhases(PrivateOutgoingBatches, x => x.Phase),
                batchSends = DiagnosticPhases(OutgoingBatchSends, x => x.Phase), incoming = DiagnosticPhases(IncomingUploads, x => x.Phase),
                incomingBatches = DiagnosticPhases(IncomingBatchPlans, x => x.Phase)
            },
            recovery = new
            {
                files = issues.Count(x => x.Area == "Files"), folderDownloads = issues.Count(x => x.Area == "Batches"), sourceTickets = issues.Count(x => x.Area == "Sending"),
                fileOperations = issues.Count(x => x.Area == "File operations"), deletedItems = issues.Count(x => x.Area == "Deleted items"),
                incoming = issues.Count(x => x.Area == "Incoming copies"), incomingBatches = issues.Count(x => x.Area == "Batch plans"),
                privateSends = issues.Count(x => x.Area == "Outgoing copies"), privateBatches = issues.Count(x => x.Area == "Private batches"),
                batchSends = issues.Count(x => x.Area == "Batch sending"), availability = issues.Count(x => x.Area == "Availability")
            },
            localActivity = new { retainedEntries = Activity.Count, retentionDays = ActivityRetentionDays, needsReview = ActivityIssue.Length != 0, droppedThisRun = DroppedActivityEvents, entriesIncluded = false }
        };
        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
        if (Encoding.UTF8.GetByteCount(json) > 32 * 1024) throw new IOException("The diagnostic snapshot exceeds its size limit.");
        var value = new DiagnosticReportReview(ProtocolRules.NewToken(), json);
        lock (diagnosticGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this); diagnosticPending = new(value, DiagnosticScope, Environment.TickCount64 + 120_000);
        }
        return value;
    });
    public void CancelDiagnosticReview() { lock (diagnosticGate) diagnosticPending = null; }
    public Task<string> ExportDiagnostics(DiagnosticReportReview review, string destination) => Task.Run(() =>
    {
        lock (diagnosticGate)
        {
            var captured = diagnosticPending; diagnosticPending = null;
            ObjectDisposedException.ThrowIf(disposed, this);
            if (captured is null || captured.Value != review || captured.Scope != DiagnosticScope || captured.Expires < Environment.TickCount64)
                throw new IOException("This diagnostic review changed, expired or was already used. Prepare a fresh report.");
        }
        var bytes = Encoding.UTF8.GetBytes(review.Json);
        using var root = StorageGrantStore.OpenLocalDestination(destination); trust.OwnerBinding?.RequirePublicLocation(root.Info.CanonicalPath);
        // Create-only through the held native folder. A collision never replaces bytes.
        using var file = root.CreateFile(review.FileName); file.Write(0, bytes); file.SetLength(bytes.Length); file.Flush();
        var actual = new byte[bytes.Length]; var offset = 0;
        while (offset < actual.Length)
        {
            var read = RandomAccess.Read(file.Handle, actual.AsSpan(offset), offset); if (read == 0) throw new IOException("Diagnostic report read-back failed."); offset += read;
        }
        if (!bytes.AsSpan().SequenceEqual(actual) || RandomAccess.GetLength(file.Handle) != bytes.Length) throw new IOException("Diagnostic report read-back failed. A report file may remain.");
        return Path.Combine(Path.GetFullPath(destination), review.FileName);
    });
}
