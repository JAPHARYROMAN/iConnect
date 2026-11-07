# Preserved activity backups

The Windows Activity page offers **Manage preserved activity backups…** for metadata retained by **Preserve and reset activity**. The Android counterpart is now implemented in source, with device verification still pending. Clearing active history or turning recording off keeps these backups until a separate removal decision. Windows visual/accessibility verification remains open.

## Windows review and removal

The manager lists eligible activity-only archives in 50-entry pages, with the most recent file modification time, file count, bytes and a short archive identifier. It does not display or export their raw contents. Other recovery metadata is excluded. Active, mixed, linked, redirected, oversized or unrecognized recovery folders are kept and counted separately.

An owner selects one backup, reviews its exact file count and size, then types `REMOVE` in a second confirmation. Permission is single-use, current-process only and expires after two monotonic minutes. The review binds the current local owner-policy context, profile/shelf/archive native identities, exact filenames, native file versions and SHA-256 of every selected file. No network request or pairing approval can authorize this operation.

Confirmation opens all selected files for mutation relative to the held archive before deleting any of them, and rechecks their hashes and versions. The native handles protect the reviewed objects. A replacement location, altered bytes, changed entry set, hard link, reparse point or additional data stream refuses cleanup. Extra streams are checked on both the metadata files and archive directory through the held handles. Filesystem support for stream enumeration is required; an unsupported check preserves the object. [Windows stream-information contract](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_stream_info).

Each decision covers one archive containing one to three exact activity-journal family files: `activity.json`, `activity.json.new` and `activity.json.bak`. The shelf scan is bounded to 256 entries; each file review is bounded to 64 MiB and uses a 64 KiB buffer. Oversized or unexpected content remains for separate local inspection. No recursive deletion or caller-supplied path is accepted.

The current activity store serializes access to its recovery inventory. Archive identifiers still needed by an incomplete preserve/reset operation are protected until that operation finishes. New archives never inherit an earlier removal review. Current activity, pairing, folder approvals, transfer records, private payloads and unrelated recovery archives are outside this manager's removal scope.

Navigation, window deactivation/hiding, shutdown and owner reset invalidate the native UI review. The worker also checks the continuing screen context during reads and before each deletion. If withdrawal or an I/O failure occurs after a reviewed file has been removed, that removal is not rolled back. Remaining files require a fresh review, including after restart. This is metadata cleanup, not secure erasure of storage media or other copies.

The two-minute deadline also applies while confirmation is running. Both implementations recheck it during file hashing and before removing metadata. Expiry preserves files that have not yet been removed; a new review is required to continue. Two Windows regressions reproduced the earlier behavior, which allowed cleanup to continue after its initial expiry check. Both cases fail on the earlier implementation and pass with the repeated checks. The equivalent Android regression is prepared but has not yet run.

## Android implementation checkpoint

Android adds the same local activity-only inventory, 50-entry pages, one-backup review and typed `REMOVE` confirmation. The activity writer serializes these operations with journal writes and preserve/reset. Failed or canceled backup queries return an operation error without marking healthy current activity as damaged. A selected archive still referenced by unfinished recovery stays protected.

Reviews bind the current owner context, root/shelf/archive device and inode identities, exact filenames, native file versions and SHA-256 hashes. They are single-use, expire after two monotonic minutes and are invalidated by foreground withdrawal, screen closure, shutdown and owner reset. Caller cancellation is checked during reads and before each removal. Limits remain 256 shelf entries, one to three recognized files per archive and 64 MiB per file. Symlinks, hard links, mixed contents and changed or unexpected entries are kept.

Android holds read descriptors while checking metadata, but its final unlink uses a path. The design relies on app-private storage and the serialized activity writer for normal application mutations; it does not establish handle-based deletion or protection from a hostile actor with the same UID or root access. Interruption can leave a partially removed archive whose remaining files require a new review. No recursive removal is used by the manager.

The debug, instrumentation-test and unsigned release APKs build successfully in 12 minutes 38 seconds. All 85 JVM tests pass with no failures or skips; lint reports zero errors and 71 warnings. Build evidence is retained in `.tools/phase116-android-build.log`, with separate APK snapshots and hashes in `.tools/phase116-apk-hashes.json`. The earlier baseline application and expanded expiry test APK are preserved separately in `.tools/phase116-baseline-apk-hashes.json`.

Twelve Android backup instrumentation cases are prepared for selection, forged/replayed reviews, changed bytes with restored size/time, expiry/owner/foreground changes, manager recreation, symlinks/hard links, shelf replacement, an interrupted recovery completion, partial cleanup, paging and caller cancellation. These include expiry after confirmation begins and after the first removal, a controller owner-reset/recreation journey, and a native typed-confirmation journey covering Keep, backgrounding and screen disposal. They compile but have not yet run. The existing 521-case and three-case large-file sequence uses the earlier fixed APK and is not interrupted or counted as validation of this new feature. Android device/UI execution, physical-provider, maximum-inventory responsiveness and broader concurrent-change evidence remain open.

The reusable [Android activity-backup validator](../../tools/test-android-activity-backups.ps1) accepts candidate application/test APKs, an explicitly named disposable emulator and the known installed application/test hashes. An optional baseline APK pair must reproduce the specific expiry rejection failure before candidate installation. The candidate then runs the expiry case and 34 selected activity, controller, UI, connection and mutation cases, requiring exact pass counts and no skipped cases. Installed APK hashes are checked before and after verification. A mismatched emulator or artifact, active instrumentation or incomplete result stops the run without starting the next stage.

Four generated fake-ADB cases verify refusal of a physical-device serial, a mismatched AVD, active instrumentation and an unexpected installed APK hash. None reaches an install, app-stop or instrumentation command. Evidence is retained under `.tools/phase116-validator-guards`. These checks do not establish real-device execution. The preceding 521-case suite passed, but its post-suite APK pull failed and stopped the dependent queues before installation. The [recovered sequence](provider-range-reads-and-stop.md) revalidated both installed APKs and resumed the large-file matrix. The candidate run still requires that matrix's exact completed result and an inactive emulator before installation.

## Windows verification

The final Windows Release desktop build passes with zero warnings/errors. All **869 selected main regression cases** pass in 4 minutes 54 seconds with no failures or skips. All ten focused backup cases pass. The same three separate capacity exclusions and separate large-file suite remain in use; the change does not claim those separate jobs as local passes.

The ten generated-NTFS/controller backup cases cover:

- Selected-backup removal, canceled/forged/replayed reviews, preservation of active activity, unrelated transfer metadata and mixed unknown content.
- Changed bytes with the original size and write time restored.
- Review expiry, owner change, screen withdrawal, manager recreation and disposal.
- Expiry after confirmation begins and after the first deletion, with remaining metadata requiring a new review.
- Shelf/archive replacement with matching names and file bytes.
- Added file and directory data streams and a hard link to an independent owner file.
- A real preserve/reset operation interrupted by a locked sidecar; its partially archived journal remains protected until reset completes.
- Cancellation after the first deletion, retention of remaining metadata and a fresh-manager review.
- A 51-backup controller inventory across two pages, owner-policy reset and the deliberate removal of only the selected backup.

Evidence is retained in `.tools/phase116-windows-baseline.log`, `.tools/test-results/phase116-expiry-baseline.trx`, `.tools/phase116-windows-focused.log`, `.tools/phase116-windows-main.log`, `.tools/test-results/phase116-windows-main.trx` and `.tools/phase116-windows-desktop-build.log`. The model and filesystem tests execute the actual removal paths; the native UI is compiled but has not been operated visually. Windows GUI automation remains stopped following the user's Escape. Keyboard, focus, screen-reader, installed-package and broader filesystem/concurrency matrices remain required.

The manager is available for local privacy review without granting or restoring peer storage authority. It does not make activity complete or tamper-evident, repair a transfer receipt or adopt a backup as trusted history. See [local history and retention](phase-51-local-activity-retention.md), [privacy and local data](../privacy.md) and the [completion audit](completion.md).
