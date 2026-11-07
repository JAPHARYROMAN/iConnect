# Windows owner recovery manager

This extends [recoverable deletion](phase-16-recoverable-deletion.md) with local recovery-location details, search/paging, metadata export, identity review, reviewed permanent deletion, and explicit restoration of data left after an interrupted purge. It does not complete the wider [application goal](completion.md).

## Using the manager

In **Shared locations → Review deleted items on this computer**, each record shows its original and saved locations. Search matches the filename or original location; results display 20 records per page within the existing 512-record catalog limit. No device connection or surviving peer grant is required.

**Review locations…** compares the recorded object identity at its original, saved and recorded restore locations. The dialog reports a match, absence, replacement or unavailable location. Confirming changes metadata only. It can confirm a saved object, or an original/restored object when its saved slot is absent. When the vault and recorded parents are accessible but the saved object is absent at the checked locations, **Archive missing record…** rechecks those locations, writes a private JSON export, and removes the active record after the replay window. The archive records what iConnect observed at those paths. The object may exist elsewhere.

**Export recovery details…** explains that paths and peer identifiers are sensitive, then lets the owner choose a local folder. The no-replace JSON export contains one item and its receipts, including the identities and paths needed for manual review. It contains neither file contents nor private device keys. It confers no access rights and is not an authority-import format. Failed export writes clean up only the new export; an existing export is kept.

**Permanently delete saved copy…** prepares a review showing the exact saved location, file/folder counts and total file bytes. The owner must type `DELETE`. This affects only the saved copy, never an original or restored location. It is not secure media erasure. The review expires after five minutes, is single-use and is bound to the active owner generation, catalog epoch, item record and reviewed tree metadata. Changing the record, file identity, size, timestamp, attributes or tree contents requires a new review. Reviews do not survive restart, and only eight are retained in memory.

Permanent deletion is deliberately a local owner action. No new file RPC or remote purge capability was added. Intact copies remain compatible with the existing `recovery-v1` clients. Fully purged records are omitted from remote listings; historical delete receipts remain historical receipts and cannot repeat a delete. Copies on which permanent deletion was attempted require local review before any remaining data can be restored.

## Permanent-deletion boundary

Review is bounded to 4,096 entries, 32 nested levels and 8 TiB of file bytes. Execution reopens the original root, vault and item by recorded identity and captures the reviewed tree again. Known objects are opened with deletion authority and held through the operation. Regular handle-relative names, no-reparse/no-hard-link checks, current private-profile protection and normal Windows access checks still apply. A read-only file can cause an honest partial failure.

The owner journal is committed before any file deletion. Entries are removed by their already-open handles, children before parents. Directory deletion requires an empty directory, so an unreviewed new child is never traversed or removed. A changed or unavailable object blocks the operation. The code does not use path-based recursive deletion or a permanent-delete fallback in the remote delete operation.

A successful result is recorded only after every reviewed entry was accepted for deletion and its handle closed. Normal Windows deletion semantics apply, including other compatible open handles; this does not promise physical erasure. Failure or lost completion metadata leaves a visible review state. A restart never resumes permanent deletion automatically. A later attempt requires a fresh review of whatever remains.

**Restore remaining data…** is a separate local action. It counts the current remaining contents, requires the owner to type `RESTORE REMAINS`, validates that review again and performs the existing no-replace restore. The UI and record distinguish this from intact recovery: earlier deleted entries cannot be recovered by this operation. A collision keeps the saved remainder and existing destination for another reviewed decision. Ordinary remote restore stays unavailable for that partial copy.

Completed records remain available locally. Dismissal retains the 24-hour replay boundary, including the most recent purge attempt. Before dismissing a settled receipt, **Review recovery folder…** can remove its identity-bound empty vault after local confirmation. iConnect rechecks the vault for entries and data streams and uses Windows' nonrecursive directory disposition. The receipt and replay protection remain available after cleanup.

## Verification

Sixteen additional Windows tests exercise actual NTFS deletion, exact nested/empty-folder counts, original-copy preservation, wrong confirmation, single-use/expiry/eviction/restart/owner-reset boundaries, stale file/tree reviews, existing readers, failed intent writes, hard links, private profiles inside a saved tree, depth/entry limits, metadata export collisions, uncertain-location confirmation and partial restoration. The read-only failure test confirms that one saved file was removed while another remained; the journal survives restart and a fresh review can finish the purge. A real CNG-bound desktop-controller test runs location confirmation, metadata export and permanent deletion offline after retiring the earlier owner policy, and verifies the local activity receipt.

The full Release .NET suite passes 162 tests and the Release solution builds with zero warnings/errors. Android source and the wire schema are unchanged in this phase; the previous 75 device/UI and five JVM checks remain the Android baseline. A fresh authenticated Windows–Android emulator run passes the existing explorer, file/folder transfer, file-change, recoverable delete/restore and matching-session failure checks in both directions.

Native visual verification for the new manager is pending. The disposable `--deleted-local` fixture starts, but the Windows automation helper reported a minimized window and an input interruption during selection. Automation stopped without executing a permanent-delete confirmation. The prior phase's native restore review/cancel evidence does not prove the new dialogs or controls.

## Prepared native review test

Use generated fixture data only. Run `IConnect.DesktopFixture --deleted-local` from the built test project, then launch the Windows app with `--state-directory` and the printed `DESKTOP_STATE` folder. That profile has one real saved file and its original grant has been revoked.

1. Open **Shared locations**, load saved items, verify both locations, and search for `simu.txt`. A nonmatching query should show zero matches without losing the record.
2. Review locations, cancel, and verify no file moves. Repeat and confirm the recorded saved identity; this changes metadata only.
3. Review the export disclosure, choose a disposable local folder, and inspect the JSON. An existing export must be kept.
4. Open permanent deletion. Verify the location/counts, a disabled primary button before typing, and Cancel preserving the saved copy.
5. For a deliberate destructive fixture test, prepare a new review, type `DELETE`, confirm, and verify that only the generated saved copy is gone. Restart and verify the completed record. A repeated old review must not repeat any effect.
6. Exercise keyboard focus, text scaling, screen-reader labels, long paths and multiple matching names. Repeat using the partial-failure scenario covered by `RecoveryOwnerTests`, reviewing the incomplete-recovery warning and explicit remaining-data restore.

Full native confirmation and accessibility coverage, cross-volume recovery, vaults with missing catalog records, complete metadata-loss/crash/low-disk/capacity/concurrency/volume matrices and provider-safe Android deletion remain open. Steps 52 through 54 in the [manual validation guide](physical-network-validation.md) cover alternate restore, archiving a missing record and empty-vault cleanup. The separate-network procedure is prepared for the user to run after implementation.
