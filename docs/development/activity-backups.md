# Preserved activity backups on Windows

The Windows Activity page now offers **Manage preserved activity backups…**. This closes the Windows owner-control gap for metadata retained by **Preserve and reset activity**. Clearing active history or turning recording off still keeps these backups until a separate removal decision. The Android counterpart and Windows visual/accessibility verification remain open.

## Review and removal

The manager lists eligible activity-only archives in 50-entry pages, with the most recent file modification time, file count, bytes and a short archive identifier. It does not display or export their raw contents. Other recovery metadata is excluded. Active, mixed, linked, redirected, oversized or unrecognized recovery folders are kept and counted separately.

An owner selects one backup, reviews its exact file count and size, then types `REMOVE` in a second confirmation. Permission is single-use, current-process only and expires after two monotonic minutes. The review binds the current local owner-policy context, profile/shelf/archive native identities, exact filenames, native file versions and SHA-256 of every selected file. No network request or pairing approval can authorize this operation.

Confirmation opens all selected files for mutation relative to the held archive before deleting any of them, and rechecks their hashes and versions. The native handles protect the reviewed objects. A replacement location, altered bytes, changed entry set, hard link, reparse point or additional data stream refuses cleanup. Extra streams are checked on both the metadata files and archive directory through the held handles. Filesystem support for stream enumeration is required; an unsupported check preserves the object. [Windows stream-information contract](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_stream_info).

Each decision covers one archive containing one to three exact activity-journal family files: `activity.json`, `activity.json.new` and `activity.json.bak`. The shelf scan is bounded to 256 entries; each file review is bounded to 64 MiB and uses a 64 KiB buffer. Oversized or unexpected content remains for separate local inspection. No recursive deletion or caller-supplied path is accepted.

The current activity store serializes access to its recovery inventory. Archive identifiers still needed by an incomplete preserve/reset operation are protected until that operation finishes. New archives never inherit an earlier removal review. Current activity, pairing, folder approvals, transfer records, private payloads and unrelated recovery archives are outside this manager's removal scope.

Navigation, window deactivation/hiding, shutdown and owner reset invalidate the native UI review. The worker also checks the continuing screen context during reads and before each deletion. If withdrawal or an I/O failure occurs after a reviewed file has been removed, that removal is not rolled back. Remaining files require a fresh review, including after restart. This is metadata cleanup, not secure erasure of storage media or other copies.

## Verification

The final Windows Release desktop build passes with zero warnings/errors. All **867 selected main regression cases** pass in 4 minutes 4 seconds with no failures or skips. The same three separate capacity exclusions and separate large-file suite remain in use; the change does not claim those separate jobs as local passes.

The eight new generated-NTFS cases cover:

- Selected-backup removal, canceled/forged/replayed reviews, preservation of active activity, unrelated transfer metadata and mixed unknown content.
- Changed bytes with the original size and write time restored.
- Review expiry, owner change, screen withdrawal, manager recreation and disposal.
- Shelf/archive replacement with matching names and file bytes.
- Added file and directory data streams and a hard link to an independent owner file.
- A real preserve/reset operation interrupted by a locked sidecar; its partially archived journal remains protected until reset completes.
- Cancellation after the first deletion, retention of remaining metadata and a fresh-manager review.
- A 51-backup controller inventory across two pages, owner-policy reset and the deliberate removal of only the selected backup.

Evidence is retained in `.tools/phase113-windows-main.log`, `.tools/test-results/phase113-windows-main.trx` and `.tools/phase113-windows-build-final.log`. The model and filesystem tests execute the actual removal paths; the native UI is compiled but has not been operated visually. Windows GUI automation remains stopped following the user's Escape. Keyboard, focus, screen-reader, installed-package and broader filesystem/concurrency matrices remain required.

The manager is available for local privacy review without granting or restoring peer storage authority. It does not make activity complete or tamper-evident, repair a transfer receipt or adopt a backup as trusted history. See [local history and retention](phase-51-local-activity-retention.md), [privacy and local data](../privacy.md) and the [completion audit](completion.md).
