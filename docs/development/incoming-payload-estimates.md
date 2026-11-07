# Incoming upload payload estimates

On both devices, the incoming copy card can show a recent rate and approximate time for the remaining file bytes. The estimate starts from the first newly saved range in the current session. A retry that matches already saved bytes leaves the measurement alone. Reconnecting or renewing the write window keeps the receipt and its saved offset, and the next new range starts a fresh measurement.

The estimate uses the receiver's monotonic clock and the current payload limit when one is configured. After ten seconds without new progress, the card waits for a fresh measurement. A finished payload has its own status while the receiver verifies the file, runs applicable security checks and saves the copy. Those stages can extend completion time.

Use [step 63 of the physical-network checklist](physical-network-validation.md) to check the receiving display and final file in each direction.

The Windows NTFS and Android provider tests replay an already saved range, let the rate expire, reconnect and verify the final bytes. The Windows Release build passed with zero warnings and errors. All 75 focused tests and 853 selected Windows tests passed. The selected run excluded the three capacity cases. Android built the debug, test and unsigned release packages. All 84 JVM and 51 emulator tests passed. Android lint reported zero errors and 71 warnings. The first emulator run had two UI failures and an unrelated Messages dialog. The rerun passed after the dialog was cleared, with the same APK hashes.

## After the payload arrives

The incoming copy and batch history cards show the receiving device's current verification or saving step. On Windows, the app measures the saved-file SHA-256 pass and the reread after attachment checks. On Android, it measures private verification, writing to the provider document and reading it back. Each measured step starts at zero and estimates only its remaining bytes from its own recent rate. The receiver's network limit applies to network payload reception. The later steps use local measurements.

Windows publication and attachment checks, and Android provider creation and sync, have a status with time remaining unavailable. A byte pass can also wait for fresh progress. When its byte counter reaches the file size, the display still waits for the result. The completed receipt continues to depend on the file identity, matching digest and applicable security or provider checks. Step 65 covers the display and resulting files on paired devices. [Physical-network checklist](physical-network-validation.md)

The Windows Release build passed with zero warnings or errors. All 37 focused and 855 selected Windows tests passed. Android debug, test and unsigned release builds passed along with 85 JVM tests, three focused emulator tests and 107 selected emulator tests. Lint reported zero errors and 71 warnings. Focused cases cover a fresh rate per byte pass, stale rates, opaque attachment and provider steps, and cancellation while saving. The binaries and installed APKs matched their recorded hashes.

Both separate 4,096-item receiver publication cases passed. Windows published 4,095 files under one folder. Android published 3,968 files under 128 provider folders. Both reopened the final plan, checked its outcomes and verified the saved bytes of the last file. The other two capacity cases per platform were outside these runs. Physical Windows–Android and accessibility checks remain open.

## Files downloaded from the remote explorer

The Downloads and folder batch cards show the active file's local verification and saving steps after the network payload arrives. Windows shows byte progress during local SHA-256 passes and status during publication and attachment checks. Android shows byte progress during staging verification, copying into the selected provider and reading the provider copy back. When a single Android download reaches **Ready to save**, its verification progress clears. **Save verified file** starts a fresh set of local measurements.

Android stops a provider write if the staged file changes length during saving. The possibly partial provider document remains attached to the interrupted receipt. Checking that receipt again only reads the recorded document. Use [step 66](physical-network-validation.md) to check the remote-explorer workflow in both directions.

The Windows Release build and all 86 focused and 855 selected Windows tests passed. Android debug, test and unsigned release builds passed, with 85 JVM tests, two focused device tests and all 118 selected device tests. Lint reported zero errors and 71 warnings. The Windows fixture pauses a blocked attachment check and confirms its stage clears. The Android fixture grows the staged file while the new provider document is opening. Saving stops before writing beyond the expected length, and read-only reconciliation preserves the partial document. Binary and installed APK hashes matched before and after testing. The selected Windows run excluded three capacity cases.

## Offline copies

**On this computer** and **On this phone** show the active verification and saving step for an offline file copy. A local batch shows its current file above the paged item results. Byte-based steps use the local processing rate, independent of peer settings. Windows attachment checks and Android provider preparation and sync show status while time remaining is unavailable. The stage clears when the copy finishes or stops. Android also clears the display when the app leaves the foreground. [Offline verification in step 67](physical-network-validation.md)

The final Windows Release build passed with zero warnings or errors. All 83 focused and 855 selected Windows tests passed. The selected run excluded three capacity cases. A direct-session replay case failed in the first wider run. All four related cases passed in isolation and again in the final selected run. Android debug, test and unsigned release builds passed, with 85 JVM tests, four focused emulator tests and 137 selected emulator tests. Lint reported zero errors and 71 warnings.

The native fixtures hold Windows attachment checks and Android provider opens to inspect the current step before release. They verify saved bytes, stage withdrawal and retained Android receipts after foreground loss. The Android Compose test checks that measured writing gives way to an unestimated sync step. The binaries and installed APKs matched their recorded hashes.

## Windows keyboard focus during progress

The periodic progress refresh updates existing status text on **Transfers** and **On this computer**. The refresh changes a label only when its text or visibility changes. Durable receipt and permission updates can still rebuild the page. A rebuild restores an open batch results panel and can restore keyboard focus to the same enabled action on the same record. If a dialog opens, the user moves focus, or the matching action disappears, the pending focus restore is skipped. The focus lookup uses the page's XAML root. [Windows App SDK FocusManager](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.input.focusmanager)

[Step 68](physical-network-validation.md) checks focus, paging and dialogs during live transfers and offline copies.

The Windows Release build passed with zero warnings or errors. All 126 focused and 856 selected tests passed. The selected run excluded three capacity cases. A generated NTFS download pauses its source response and verifies that the actual periodic update requests a text refresh without requesting a page rebuild. Keyboard and screen-reader behavior in the Windows app remains a manual check.

## Windows byte updates during receiving

Windows downloads, local copies and incoming uploads update their byte counts through the periodic progress refresh while receiving is active. The refresh reads the latest live download offset or saved upload checkpoint. Progress bars and status text update in place. Completion, pause, errors and other changes to receipt metadata still refresh the page and its available actions. Incoming batch files use the same distinction for their scoped journals. [Receiving controls check in step 69](physical-network-validation.md)

The Windows Release build passed with zero warnings or errors. All 221 focused and 856 selected tests passed. The native fixtures confirm that download bytes can advance without a page rebuild request, that pausing sends a receipt change, and that an ordinary upload sends a receipt change at the final byte boundary. A separate 4,096-item receiver test published 4,095 files and recovered the completed plan after reopening. Both remaining capacity cases were outside this run. The Windows binary hashes matched before and after testing.
