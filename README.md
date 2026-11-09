# iConnect

Secure, accountless file access between Windows and Android.

**Current build: native desktop, approved storage and two-way file/folder receiving.** Both devices can browse approved storage and receive real files and selected folder batches with empty directories, durable checkpoints, SHA-256 verification and per-item results. Android supports explicitly selected single-file background receiving, sending and saving with notification controls; unrelated phone browsing ends when its UI closes. Folder batches pause when Android leaves the foreground. Windows adds opt-in notification-area availability, sign-in startup, durable pause and bounded reconnect. Full management, complete explorers and release packaging remain in progress; the [completion audit](docs/development/completion.md) tracks the complete scope.

Start with the [setup and first-copy guide](docs/getting-started.md). The [privacy and local-data guide](docs/privacy.md) explains coordinator metadata, device storage, permissions, history and cleanup. Package and field validation remain tracked separately from implemented features.

## What works in this prototype

- P-256 device identities: per-user Windows CNG keys and Android Keystore keys.
- Two-minute, single-use QR invitations with matching-code confirmation and signed approval from both devices.
- Local persistence of pinned peer public keys and local device revocation.
- File copies initiated by either device into approved destinations, including receive-only inboxes. Private source snapshots avoid sharing the original folder. Receivers require Write/Create, bounded chunks, durable receipts and SHA-256; Windows uses no-replace publication and Android separately verifies provider publication. See [phone-to-Windows incoming copies](docs/development/phase-23-incoming-copies.md) and [Windows-to-Android incoming copies](docs/development/phase-24-reverse-uploads.md). Fresh cross-platform upload validation remains pending.
- Windows receiving marks files with untrusted-origin metadata and requires bounded Windows attachment checks plus a second byte verification before new completion receipts. Recorded copies can be rechecked locally without restoring peer access. Files remain untrusted; see [platform behavior and verification limits](docs/development/phase-25-received-file-security.md).
- Successfully created remote folders can be selected for sending after reconnecting, using an identity-bound receipt and a fresh opaque reference. Replaced, revoked or uncertain folders require review; receive-only locations retain their browsing restrictions. See [created-folder recovery](docs/development/phase-26-created-folder-references.md). Native folder/batch send controls are implemented; complete cross-platform workflow validation remains open.
- Default-off paired-device availability reports with mutual opt-in, bounded scopes, expiring observations and separate direct-authentication status. Android withdraws reporting outside the foreground. See [availability behavior and verification limits](docs/development/phase-22-paired-availability.md).
- Android trust and folder approvals share an atomic policy, bound to the phone identity and a fresh pairing generation. Revocation has a durable retry journal; delayed approvals cannot reactivate a forgotten pairing.
- Signed WebRTC offer, answer, and ICE messages over a binary Protobuf WebSocket protocol. Either client can initiate; concurrent attempts use a deterministic collision rule, with late superseded signals ignored after validation. Fresh Windows–Android verification of the new phone-initiated path remains pending.
- Direct-only native WebRTC data channels, with device authentication bound to both DTLS fingerprints and fresh challenges.
- Mutual readiness acknowledgement after identity and direct-path checks, before file operations begin.
- Per-device Windows drive/folder grants with independent permissions, exclusions, expiry, volume/file identity binding, and handle-relative access.
- Owner-approved Android system-picker folder grants, persisted SAF access, per-device permissions and optional folder creation/incoming file copies where the provider supports them.
- Opaque remote references, bounded directory pages, breadcrumbs on Android, and text previews up to 32 KiB in both directions.
- Remote filename search within an approved folder, globally sorted loaded catalogs, list/grid views, live metadata and selection across 50-item display pages on both platforms. Capped discovery is explicitly marked incomplete.
- Independent offline local explorers on Windows and Android: choose local folder shortcuts, browse/search/sort, inspect metadata and preview files without creating a sharing approval.
- Windows offline folder creation, no-replace rename/same-volume move and recoverable deletion, with reviewed history, restore, location checks and typed permanent deletion. See [local management and verification limits](docs/development/phase-62-windows-local-management.md).
- Windows file and folder moves across local volumes copy to the reviewed destination first. A second review verifies both copies and moves the original into recovery. See steps 51 and 56 in the [physical test guide](docs/development/physical-network-validation.md).
- Android [offline folder creation](docs/development/phase-76-android-local-folder-creation.md) uses a separate Android picker approval, exact parent/name review and a single creation receipt. Uncertain provider results require inspection and never retry on reopen; removing a receipt leaves provider folders intact.
- Android single-file copies without pairing, including offline copies between available local providers, with the real folder picker, independent source access, checkpoint/SHA-256 verification, collision choices and reviewed provider-save recovery. See [offline copy behavior and validation](docs/development/phase-57-android-offline-copy.md).
- Android local file/folder batches with frozen provider selections, independent read access, reviewed destinations, no-merge folder collisions, foreground controls and uncertain-save recovery. See [local batch behavior and verification](docs/development/phase-61-android-local-batches.md).
- Windows single-file copies without pairing, with captured source identities, durable drafts/checkpoints, no-replace saves, Windows attachment checks and local receipts protected from unidentified-part cleanup. See [local copy behavior and verification limits](docs/development/phase-58-windows-local-copy.md).
- Windows local file and folder batches support multi-selection, frozen 4,096-item plans, reviewed destinations, no-merge folder collisions, verified receiving, restart controls and destination checks against completed receipts. See [batch behavior and verification limits](docs/development/phase-59-windows-local-batches.md).
- Recovery of lost Windows local batch history: separate unattached-receipt controls, source-free checks of recorded copies, durable copy decisions and reviewed reference removal. See [recovery behavior and verification](docs/development/phase-60-windows-local-batch-recovery.md).
- Windows remote-content bandwidth limits and optional metered/unknown-network pausing, shared across transfers and previews, with reviewed settings recovery and independent offline copies. See [network controls and limits](docs/development/phase-63-windows-transfer-network-controls.md).
- Android remote-content limits and optional metered/unknown-network pausing, with observation tied to the visible app or already-approved background work, reviewed settings recovery and independent local copies. See [Android network controls and verification](docs/development/phase-64-android-transfer-network-controls.md).
- Windows standalone receiving keeps reported source expiry, shows limit-based payload-time warnings and supports reviewed fresh-source approval while retaining its verified checkpoint and destination. See [source lifetime and recovery](docs/development/phase-65-windows-source-lifetime-recovery.md).
- Android standalone receiving adds source-expiry guards, payload-time warnings and a reviewed same-content source refresh that preserves the private checkpoint and SAF destination. Resume and provider saving stay explicit. See [Android source lifetime and recovery](docs/development/phase-66-android-source-lifetime-recovery.md).
- Windows receiving batches can renew a retained child's source through their original plan, with a parent approval receipt, preserved destination/checkpoint and explicit batch resume. See [batch source renewal and recovery](docs/development/phase-67-windows-batch-receiving-source-renewal.md).
- Android receiving batches add corresponding source reviews with native private-part and SAF destination bindings, separate source approval and explicit original-batch resume. See [Android batch source renewal](docs/development/phase-68-android-batch-receiving-source-renewal.md).
- Receiving views on both platforms show recent payload rates and approximate remaining-payload time, reset stale measurements and keep approval expiry independent. Android labels live bytes separately from its saved checkpoint. See [observed receiving estimates](docs/development/phase-69-observed-receiving-estimates.md).
- Ordinary uploads on [Windows](docs/development/phase-74-windows-send-local-payload-estimates.md) and [Android](docs/development/phase-75-android-send-local-payload-estimates.md) show acknowledged payload rates; offline copies and current local-batch files use separate local rates. Resume excludes earlier receipt bytes, and payload estimates remain separate from verification and saving. Android distinguishes live bytes from its saved checkpoint and clears local measurements when hidden.
- The receiving device shows a rate and remaining payload estimate for incoming copies based on newly saved bytes. The saved checkpoint stays visible through a stall or reconnect. [Incoming upload estimates](docs/development/incoming-payload-estimates.md)
- Private batch sends show acknowledged bytes and a recent estimate for the current file. A separate forecast covers the remaining planned batch payload using the recent acknowledgement rate across files. Pausing keeps the saved checkpoint visible, and each resume starts a new measurement. [Private batch send progress](docs/development/batch-payload-estimates.md)
- Receiving owners on [Windows](docs/development/phase-70-windows-upload-window-recovery.md) and [Android](docs/development/phase-71-android-upload-window-recovery.md) can review an expired ordinary upload's write window while retaining the original receipt, destination and verified checkpoint. Reviews identify the sending device, preserve current grant limits and leave resume explicit; Android provider decisions remain separate.
- Windows can ask the paired Android receiver to verify a completed published copy again. Android checks the recorded provider document and bytes for a fresh challenge. See [Windows-to-Android copies](docs/development/phase-24-reverse-uploads.md).
- Windows can prepare a single file for a move to Android. A second review requires a fresh destination check and verification of the bound Windows original before it moves into local recovery. See step 58 in the [physical test guide](docs/development/physical-network-validation.md).
- Android can verify a completed send to Windows. The receiver checks the recorded file identity and SHA-256 on the current authenticated session. See step 59 in the [physical test guide](docs/development/physical-network-validation.md).
- [Windows](docs/development/phase-72-windows-prepared-source-controls.md) and [Android](docs/development/phase-73-android-prepared-source-controls.md) list prepared reads for every receiving peer, with preparation progress, expiry and offline owner stop/cleanup controls. Stopping a ticket preserves originals, receiver copies and separate folder grants; Android inventory never authorizes background sending.
- Two-way 256 KiB test-file transfers, chunk acknowledgements, temporary destinations, and SHA-256 verification before completion.
- A Compose Android interface that ends general phone-storage browsing when it leaves the foreground. Selected receiving, sending and saving jobs can continue through an explicit notification-backed action.
- An Android receiving queue with pause/resume, private staging, checkpoint verification, destination selection, keep-both saves and explicit recovery for interrupted document-provider publication.
- A native WinUI desktop with devices/QR pairing, offline device revocation, phone explorer, folder creation, per-device Windows drive/folder permission editing, connection settings and bounded local activity.
- Peer/grant-bound source tickets on both platforms; real Windows receiving with bounded streaming, SHA-256 verification, restart/resume, no-replace finalization and transfer controls.
- Windows multi-select copying with a validated folder manifest, empty/nested directories, keep-both/skip/fail decisions, durable per-item receipts and destination identity checks on resume.
- Android multi-select receiving with a bounded manifest, nested provider folders, recorded ancestor IDs, pairing-generation-bound batches, collision decisions and explicit recovery for uncertain folder/file publication.
- Saved-progress recovery on both platforms: unreadable file/batch/sending metadata stays visible for review, partial and saved copies are preserved, and missing-batch children cannot become standalone transfers.
- Durable folder creation on both hosts and permission-gated no-replace rename/same-volume move on Windows, with recent change receipts and explicit review of uncertain outcomes. See [file-change behavior and limits](docs/development/phase-13-file-changes.md).
- Optional Windows background availability, explicit per-user sign-in startup, saved pause, reconnect and an activation-only local pipe. See [background behavior and verification](docs/development/phase-14-background-availability.md).
- Optional Android foreground reconnect bound to the current pairing and endpoint, saved pause, bounded retry, network/lifecycle limits and metadata-preserving settings reset. It leaves queued transfers for deliberate resume. See [Android reconnect behavior and verification limits](docs/development/phase-21-android-reconnect.md).
- Windows trust and storage approvals bound to the local CNG identity and owner generation, with metadata-preserving review/reset after key loss or inconsistent restores. See [owner recovery](docs/development/phase-15-windows-owner-recovery.md).
- Recoverable Windows deletion with durable receipts, no-replace restore, Android client controls and offline owner recovery after revocation. Custom Windows profiles remain private even inside an approved ancestor folder. See [recovery behavior and limits](docs/development/phase-16-recoverable-deletion.md).
- A Windows owner recovery manager with location checks, local search, metadata export, reviewed permanent deletion and explicit restoration of remaining data after a partial purge. Both Windows recovery lists support reviewed same-volume alternate restore, private archiving of missing records and empty recovery folder cleanup for settled receipts. See [owner recovery manager](docs/development/phase-17-owner-recovery-manager.md) and [manual validation](docs/development/physical-network-validation.md).
- PNG/JPEG and PDF previews on both platforms for approved remote files and independent local folders, with bounded input/output, isolated rendering, page navigation, zoom/rotation and foreground cleanup. Windows uses a capability-free AppContainer and constrained job; the UI accepts raw pixels. See [Android previews](docs/development/phase-18-bounded-previews.md) and [Windows previews](docs/development/phase-19-windows-previews.md).

There are no accounts, TURN servers, file relays, arbitrary remote paths, or coordinator file-upload API endpoints. Incoming copies use the authenticated direct channel. Windows-initiated uploads to Android, the broader recovery/fault matrix, Android file management and complete explorer interactions remain outstanding. Windows background availability still needs its physical lifecycle and installed-release validation. Android providers that cannot verify ancestry or offer seekable, known-size files return an explicit unsupported result. Safe rename on Android remains unavailable until no-replace guarantees can be established.

Local browsing opens from **On this computer** in Windows or **On this phone · local folders** in Android. Choose a local folder using the system picker. Local shortcuts and their read access are separate from device approvals; removing a shortcut keeps files and access still owned by downloads or sharing. See [local explorers](docs/development/phase-11-local-explorers.md) for bounds, recovery and verification.

If saved progress is damaged, use **Review saved progress → Archive progress record…**. This preserves unreadable metadata locally and keeps partial and saved files. **Remove preserved metadata backups…** is a separate permanent metadata-only cleanup decision. Source-ticket recovery requires the receiver to prepare a new transfer. See [saved-progress recovery](docs/development/phase-12-progress-recovery.md) for limits and remaining fault coverage.

If Windows reports changed/missing owner authority, review **Settings → Windows identity and approvals**. Reset archives earlier trust/approvals, keeps copies and local folders, and requires fresh pairing and folder approval. Nonempty profiles from earlier development builds require this review. A [physical-phone, separate-network test kit](docs/development/physical-network-validation.md) is ready for the operator to run after implementation.

## Run the native Windows app

Build with `./tools/dev.ps1 build`, then launch with:

```powershell
./tools/dev.ps1 desktop
```

Set the HTTPS coordinator and optional STUN address under **Settings**, then select **Connect**. **Devices → Add device** displays a short-lived QR invitation. Scan it on Android and approve the matching code on both devices. **Shared locations** approves a folder or an explicitly selected entire drive for one device, with separate permissions, exclusions, expiry and pause controls. No location is shared automatically.

Open iConnect on the phone and connect it to the same coordinator. Once paired, choose **Connect to device** in Windows or **Connect to computer** on Android. Android also offers an optional STUN field while disconnected; both transports validate the selected STUN URI. See [connection behavior and verification gaps](docs/development/phase-20-phone-connections.md). **Browse files** opens approved phone storage. Select a file and **Save selected file to this computer**, choose a destination folder, and confirm. **Transfers** shows progress and pause/resume/cancel. An existing destination is preserved with a numbered name. Resume missing data after reconnecting to the same trusted device; keep Android active. Windows can also preview PNG/JPEG, PDF and text, and create folders where permitted. **Revoke device** works offline, pauses availability and removes its Windows storage approvals. Closing Windows ends availability by default. **Settings → Windows background availability** can instead keep the agent in the notification area, register sign-in startup and reconnect automatically. **Pause** persists across restarts; **Quit iConnect** ends the current process. A normal second launch reopens its existing window.

For a folder or several items, use the explorer's selection boxes and **Copy selected files and folders…**. The app scans a bounded manifest, asks for a local destination, then shows file/folder counts, size and collision choices before copying. **Open selected item** navigates a single selected folder or previews one file. Existing destination folders are kept separate: keep both creates a numbered folder, while skip/fail leaves the existing branch untouched. **Transfers → Item results** exposes every item's outcome in pages. **Completed with issues** means some items were not copied. Pause/resume keeps checkpoints; cancel removes owned private partial files and keeps completed files and created directories. If folder creation stopped before its identity could be saved, review the destination and start a new copy rather than adopting an unverified folder. Keep Android open during discovery and general folder copying; automatic background authorization for a whole batch is not implemented.

The native development executable and its runtime files are generated beneath `apps/windows/IConnect.Desktop/bin/Debug/net10.0-windows10.0.26100.0/win-x64`. Keep the entire output directory together. It includes .NET and Windows App SDK runtime files and requires Windows 10 2004+ x64. The development profile normally lives in the current user's local application data under `iConnect/desktop`.

Use the [Windows MSIX release procedure](docs/development/windows-release.md) to package and sign the desktop app and run the installed update and uninstall checks.

For local development, start `./tools/dev.ps1 server`, then `./tools/dev.ps1 desktop --development`. This supplies a loopback address for a new profile; the server command remains restricted to loopback. `--state-directory <path>` can isolate disposable development state. The native desktop and console host use separate default profiles and identities.

## Run the console development host

Prerequisites: Windows x64 and .NET SDK 10.0.400 or a later patch in that feature band. If the SDK is missing, `./tools/bootstrap.ps1` downloads a project-local copy from Microsoft and verifies its published SHA-512 checksum. No system PATH change is needed.

From the repository root, start the coordinator in one PowerShell window:

```powershell
./tools/dev.ps1 server
```

In a second window, start the Windows host:

```powershell
./tools/dev.ps1 windows --name "My Windows PC"
```

Type `pair` in the host. It prints a short-lived invitation and the location of a QR SVG in the current user's local application data. Open that SVG to scan it from Android, or paste the printed invitation into the Android emulator. Invitation secrets are deliberately kept out of the repository.

After requesting pairing on Android, compare the six-digit codes. In Windows type `approve <code shown on Android>`. On Android enter the code shown on Windows and tap **Approve computer**. Both devices must approve before either saves trust.

Once both devices report paired, type `devices`, then `connect <Android device id>` on Windows. After the authenticated direct-connection status appears, type `send` on Windows or tap **Send 256 KiB test file** on Android. The receiver saves the verified synthetic file in its private prototype inbox. Each session accepts at most four test files; reconnect for more tests.

To approve Windows storage for the sole paired device in this console host, type `share "C:\path\to\folder"` or explicitly select an entire drive with `share C:\`. The default is read-only. `shares` lists approvals; `unshare <grant-id>` removes one. Sensitive/system locations, internal temporary names, redirected paths, and files with additional hard links are excluded. Pairing never creates a storage grant.

On Android, disconnect first, choose **Choose phone folder**, use the system folder picker, and explicitly approve read-only sharing with the paired computer. Keep iConnect open after reconnecting. **Allow creating folders** is a separate per-folder permission and requires an actual Android write grant. **Stop sharing** ends the active channel and removes that approval. Android's system picker restricts selectable storage locations.

**Forget computer** on Android ends the connection immediately and saves revocation before allowing another connection or pairing. If saving fails, access remains blocked in the running app: free storage space and use **Retry saving revocation** before closing it. **Reset pairing data…** is an explicit recovery action for damaged pairing data; it removes remote approvals while preserving phone files, local downloads and their destination. Earlier prototype policies and a changed phone identity require fresh pairing and fresh folder approvals. Saved legacy selections stay inactive and can be removed.

Windows can inspect phone approvals with `roots`, open a listed folder with `ls <folder-id>`, and preview a file with `cat <file-id> <version>` using values from the listing. `next <folder-id> <page-token>` requests another directory page. Android can select **Browse computer files** once connected. The console `cat` command previews bounded plain text. Native Windows and Android viewers also support PNG/JPEG and PDF. Viewing a preview is separate from a resumable, integrity-verified file transfer.

To download a real computer file on Android, disconnect and select **Downloads → Choose download folder** first. This destination approval does not share the folder remotely. Reconnect both devices, browse an approved computer folder, and select **Download** beneath a file. Keep iConnect open, or select **Continue this transfer in background** while that job is active and allow its notification. Wait for the background authorization to appear before leaving the app. Other jobs pause; phone browsing is denied while its UI is inactive. The notification can pause the selected work, and the service stops when it finishes. Android may interrupt background work; reopen the app and reconnect the same source to resume missing data.

Once **ready to save** appears, select **Save verified file**. This is a separate owner decision; receiving does not automatically publish a provider copy. An existing name is preserved with a numbered name, and saved bytes are verified before completion. A running save can also be selected for background continuation. A completely received staging file can be verified offline.

An interrupted provider save may leave a partial file. **Check saved copy** only reads the recorded copy; **Save another copy…** requires an explicit decision and creates a new file while keeping earlier copies untouched. Android cannot promise atomic publication for every document provider. The current adapter requires normal seekable files, persistent read/write access and verifiable folder ancestry. Choose the destination again if Android withdraws access. See [Android receiving](docs/development/phase-4-android-receiving.md) for the tested recovery cases and remaining limits.

For a folder or batch on Android, select up to 128 items across the loaded computer-storage result pages and choose **Copy selected files and folders…**. Review the manifest and collision choice, then start copying into the selected download folder. Folder batches automatically publish verified files into their recorded subfolders. Keep the app open: a batch child cannot receive single-file background authorization, and leaving the activity pauses the batch. Reopen, reconnect the same pairing and choose **Check saved copies / resume batch**. The per-item results retain every completed, skipped, failed or interrupted outcome. Unknown folder creation has a separate **Review / skip uncertain folder…** action; uncertain files require a deliberate new-copy decision. Cancel cleans owned private partials while preserving provider copies and created folders. See [Android folder batches](docs/development/phase-9-android-batches.md) for provider limitations and tested recovery.

Sharing approvals, the selected destination and queued download records retain independent Android folder-access leases. Revoking sharing does not authorize future peer requests and does not remove access still needed for local downloads. **Clear selected destination** releases the selection's lease; old queued jobs keep their own folder until their records are removed. See [Android lifecycle and access ownership](docs/development/phase-5-android-lifecycle.md) for current boundaries and verification.

For background sending, start a download from an approved phone folder in Windows. On Android, find that active file under **Sending from this phone** and select **Continue this sending transfer in background**. Wait for its notification authorization before leaving the UI. Only that source ticket may continue in its current authenticated session; browsing, new file preparation and other tickets remain blocked. The notification can pause sending. To resume, open Android, select **Allow this transfer to resume**, reconnect both devices, then resume the Windows download. **Cancel sending record** removes its ticket without deleting the phone file. Idle tickets lose background authorization after two minutes without new preparation/range progress. See [background sending](docs/development/phase-6-android-sending.md).

Other Windows commands: `reject`, `revoke <device id>`, and `quit`. Revoking a device removes its Windows storage grants and closes its session. Separate local hosts can use `--profile host-a` and `--profile host-b`. An optional `--stun stun:your-stun-host:3478` enables endpoint discovery; there is no default third-party STUN dependency and no TURN configuration. For release combinations and the `ready-v1` requirement, see [protocol compatibility](docs/development/protocol-compatibility.md).

## Build and run Android

Open `apps/android` in Android Studio, or use the Gradle wrapper. The project uses JDK 17 or 21, Android SDK 36, Build Tools 36, and Android 10+ as the device baseline. Set `JAVA_HOME` and `ANDROID_HOME` for your installation if necessary.

```powershell
./tools/dev.ps1 android
```

The APK is generated at `apps/android/app/build/outputs/apk/debug/app-debug.apk`. Install it on an emulator or development device using Android Studio or `adb install -r <apk>`.

Use the [Android release procedure](docs/development/android-release.md) to build and verify a versioned APK with the release owner's signing key, then test upgrade and uninstall behavior on a disposable device.

For the standard Android emulator, use `http://10.0.2.2:5078` as the coordinator. The debug app recognizes the Windows invitation's `localhost`/`127.0.0.1` origin as the emulator's host alias when the port matches. Debug HTTP is limited to these development addresses. Release builds require an HTTPS coordinator origin and do not use that alias rule.

For a physical phone or separate-network testing, configure a coordinator reachable by both devices over HTTPS with a normally trusted certificate. The [self-hosting guide](docs/development/coordinator-release.md) covers publishing, direct Kestrel TLS, service configuration and external health checks. Direct reachability depends on both networks, and the source device must remain awake and available.

## Build and test

Windows builds use the [corrected pinned source transport](docs/development/native-default-transport.md). The first `dev.ps1` build prepares it automatically and requires PowerShell 7.2+, Git, MSVC x64, CMake, Ninja and Perl in addition to the .NET SDK. Preparation compiles OpenSSL and libdatachannel, so allow time for that first build. Later builds verify and reuse the completed bundle. Direct `dotnet build` users should first run `./tools/prepare-native-transport.ps1`. Missing or altered native inputs stop the build.

```powershell
./tools/dev.ps1 build
./tools/dev.ps1 test
```

The Windows large-file cases generate files over 4 GiB and require at least 14 GiB free on the repository's filesystem. They exercise saved checkpoints, restart and complete file hashes for downloads, uploads and offline copies. CI runs them in a separate Windows job. [Large-file procedure and evidence](docs/development/phase-12-progress-recovery.md#windows-files-over-4-gib)

The .NET suite covers native encrypted sessions, pairing, real receiving/resume/integrity, Windows storage containment, authorization changes, malformed remote metadata, offline trust and the desktop controller's real coordinator/file flow. Android JVM/device tests cover protocol, Keystore, framework URI grants, source tickets, receiving checkpoints, provider publication recovery, access ownership, selected background work, pairing generations and folder batches. See [connectivity](docs/development/verification.md), [approved storage](docs/development/phase-1-storage.md), [native desktop](docs/development/phase-2-desktop.md), [durable downloads](docs/development/phase-3-downloads.md), [Android receiving](docs/development/phase-4-android-receiving.md), [Android lifecycle](docs/development/phase-5-android-lifecycle.md), [background sending](docs/development/phase-6-android-sending.md), [Android trust recovery](docs/development/phase-7-android-trust.md), [Windows folder batches](docs/development/phase-8-windows-batches.md), and [Android folder batches](docs/development/phase-9-android-batches.md) for exact evidence and limits. See [remote explorer catalogs](docs/development/phase-10-explorers.md) for search, sorting, selection, discovery bounds and the latest verification. Separate-network field testing remains outstanding; local success does not demonstrate internet NAT traversal or production security.

Native outgoing folder/batch preparation, sending and queue/history controls are implemented. Both receivers provide [durable plan admission and shared incoming reservations](docs/development/phase-28-batch-plan-admission.md), using the [bounded manifest contract](docs/development/phase-27-outgoing-batch-contracts.md). They also have [durable stop/completion primitives and bounded private cleanup](docs/development/phase-36-batch-lifecycle.md). Complete Windows–Android workflow, compatibility, recovery and physical-network verification remain required before treating batch support as release-ready.

The [Windows durable batch sender](docs/development/phase-37-windows-batch-sender.md) and [Android durable batch sender](docs/development/phase-38-android-batch-sender.md) retain original intents and item attempts, check receipts after lost replies/restart and send verified private files through scoped receivers. Both integrate with their controllers' session lifecycle. [Native batch controls](docs/development/phase-41-native-batch-controls.md) now expose preparation, reviewed queueing, paged plans/results, stop and separate private-copy/history cleanup. Complete workflow validation and owner recovery remain in progress.

[Outgoing batch history](docs/development/phase-39-outgoing-batch-history.md) now separates archived local receipts from active sender slots. Owner reviews support restoring the original intent, bounded resumable metadata cleanup and guarded private-source removal. Source originals and receiver copies remain intact.

[Retained copy review](docs/development/phase-42-retained-copy-review.md) keeps the additional-copy requirement in the private snapshot before receiver admission. Purging or losing the sender queue cannot silently make that snapshot look unused. Legacy ready snapshots are upgraded conservatively; earlier identities retain their sending restrictions.

[Windows sender-store recovery](docs/development/phase-43-windows-sender-store-recovery.md) and [Android sender-store recovery](docs/development/phase-44-android-sender-store-recovery.md) add owner-reviewed preservation of unreadable batch intents and item receipts, a fresh empty queue, and separate bounded removal of preserved stores. Private snapshots and receiver files are kept. Android also offers a paged local archive inventory and requires fresh removal review after restart.

[Windows private-source recovery](docs/development/phase-45-windows-private-source-recovery.md) and [Android private-source recovery](docs/development/phase-46-android-private-source-recovery.md) preserve unreadable snapshot metadata and payloads, keep their bytes charged to outgoing quotas, and open a fresh preparation store. Separate reviewed cleanup is bounded; retained sender controls remain available. Future private batches retain explicit additional-copy review after recovery, including after archive removal. Both native surfaces provide paged archive inspection and require fresh removal review after restart.

If an older valid source store is restored alone, its surviving recovery marker keeps additional-copy review required on both platforms. Restored snapshots retain their original identity and pairing restrictions.

[Android receiving-part recovery](docs/development/phase-47-android-receiving-part-recovery.md) replaces bulk orphan deletion with exact owner reviews, paged inspection and cleanup of at most 64 files per call. Current journal references protect their parts; new or changed files remain for fresh review, and restarting the app ends earlier deletion permission. Provider documents remain intact.

[Ordinary Android incoming-part recovery](docs/development/phase-48-android-ordinary-part-recovery.md) now uses the same bounded inspection and review flow, with checks for changed journals, uncommitted metadata and replaced private directories. Resetting receiver history ends earlier cleanup permission. Current incoming receipts continue to protect their parts after peer revocation.

[Windows destination-side partial recovery](docs/development/phase-49-windows-destination-part-recovery.md) adds owner-selected folder inspection, paged candidate lists and typed permanent-removal confirmation. Current file/folder download and upload receipts protect their temporary names and recorded native file identities. Cleanup removes at most 64 reviewed files per call, preserves new or changed files, and requires fresh folder selection after restart. A temporary-looking filename does not prove origin; unidentified bytes require the owner's decision.

[Reviewed diagnostics](docs/development/phase-50-reviewed-diagnostics.md) add an exact JSON preview and deliberate export on both platforms. Reports contain versions, connection states and aggregate queue/policy/recovery counts. Names, IDs, addresses, paths, filenames, content/hashes, pairing data, logs and raw errors are omitted. Reviews expire and cannot be replayed. Android's chosen document provider may sync the report; iConnect does not send it to support automatically.

[Local activity and retention](docs/development/phase-51-local-activity-retention.md) provide Off, 1-, 7-, 30- and 90-day choices on both platforms, with at most 500 active entries, confirmed clear and preservation/reset of unreadable history. Android adds fixed action/outcome records for selected device, serving and durable transfer events; filenames, paths, pairing secrets, file content/hashes and raw errors are excluded. History can be incomplete and grants no authority. Clear/off keeps separately preserved recovery copies, and diagnostic exports omit individual entries and peer identities.

**Activity → Manage preserved activity backups…** separately reviews recognized activity-only backups. Removing one backup requires typed confirmation; other recovery data and original files are kept. See [activity-backup controls and verification limits](docs/development/activity-backups.md).

[Serving and local-control activity](docs/development/phase-52-serving-and-control-activity.md) adds Windows request outcomes after permission checks and separate durable incoming-publication events. A bounded writer keeps journal I/O outside serving locks, exposes recording gaps and orders history controls with pending observations. Android records specific saved-settings, preservation/reset and reviewed-cleanup actions. A continuation needing more work is shown as requiring review. Entries use fixed labels and optional local peer identities/byte counts; they contain no request IDs, filenames, paths, review tokens or raw errors.

[Transfer and remote-change activity](docs/development/phase-53-transfer-and-change-activity.md) extends Windows outgoing/download and reviewed-recovery observations. Both platforms distinguish skipped files and batches completed with issues. Android confirms remote changes only with a matching successful receipt; a lost reply after dispatch requires review. History remains separate from filesystem and permission authority.

[Windows change and connection observations](docs/development/phase-54-windows-change-and-connection-observations.md) require matching succeeded receipts for remote changes, keep an unconfirmed move selected for review, and distinguish coordinator connectivity from authenticated direct access. Fixed connection outcomes come from typed states. Failing history/status observers cannot alter authentication or file effects.

[Android connection observations](docs/development/phase-55-android-connection-observations.md) distinguish coordinator registration from authenticated direct readiness, and owner cancellation from foreground pause or failure. Native history/status callbacks are contained, and a single close gate prevents duplicate terminal outcomes. Activity remains local and incomplete.

[Ended-session signaling](docs/development/phase-56-ended-session-signaling.md) keeps fresh late messages for known ended attempts away from disposed transports and successor sessions. Both clients retain the original validation and replay limits; ignored old IDs cannot restore file access.

[Receiver history](docs/development/phase-40-receiver-batch-history.md) separates active admission from retained final receipts. Reviewed item-history removal keeps compact parent outcomes that reject replay; a closed-only reset preserves backups and rotates the namespace. The native history controls are available on both platforms.

Windows has [scoped directory-child effects](docs/development/phase-29-windows-batch-directories.md), including native identity replay and a durable attempt index. [Android provider-backed directory effects](docs/development/phase-30-android-batch-directories.md) retain exact provider identities and never replay uncertain creation.

[Windows scoped batch files](docs/development/phase-31-windows-batch-files.md) reuse incoming checkpoints, no-replace publication and native attachment checks, with aggregate reservations and a shared two-worker limit. [Android scoped batch files](docs/development/phase-32-android-batch-files.md) have private staging and verified provider publication, with separate 4,096-item checkpoint/publication capacity passes and descriptor-leak regression checks. Private batch preparation on [Windows](docs/development/phase-33-windows-private-batches.md) and [Android](docs/development/phase-34-android-private-batches.md) passes separate 4,096-item real-snapshot/restart tests. Both controllers now use [shared outgoing byte reservations, disk admission and two worker slots](docs/development/phase-35-shared-outgoing-resources.md) for ordinary files and private batches. Compatible peers advertise folder-batch receiving on authenticated direct sessions. The owner must approve the destination before a queued send can start. See the paired-device procedure in [physical validation step 16](docs/development/physical-network-validation.md).

## Repository layout

| Location | Purpose |
| --- | --- |
| `apps/windows/IConnect.Proof` | Interactive Windows connectivity test host and QR generation. |
| `apps/windows/IConnect.Desktop` | Native WinUI Windows application; self-contained development output. |
| `apps/windows/IConnect.AppModel` | Desktop connection, permission, settings and local activity controller. |
| `apps/windows/IConnect.Connectivity` | Native WebRTC binding, pairing client, and authenticated test-file session. |
| `apps/windows/IConnect.Storage.Windows` | Owner grants and handle-relative Windows storage service. |
| `apps/android` | Kotlin / Jetpack Compose Android proof app. |
| `backend/IConnect.Rendezvous` | Bounded, ephemeral coordination service with no file API. |
| `shared/protocol` | Shared Protobuf wire contract. |
| `shared/IConnect.Core` | Identity, transcript, validation, framing, and test-file rules. |
| `tests/IConnect.Tests` | Security, coordinator, and real native connectivity tests. |
| `docs/development/phase-0.md` | Protocol notes, implementation boundaries, and remaining validation. |
| `docs/development/completion.md` | Full application requirements and remaining evidence. |

The [technical white paper](WHITEPAPER.md), [public edition](WHITEPAPER_PUBLIC.md), and [publication PDF](output/pdf/iConnect-Whitepaper-Public.pdf) describe the wider product direction. They are proposals, not a list of features already delivered by this prototype.
