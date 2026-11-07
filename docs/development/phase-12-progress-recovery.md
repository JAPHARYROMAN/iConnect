# Saved-progress recovery without file loss

Implemented and locally verified 3 November 2026 (Africa/Nairobi). Receiving and batch queues on Windows and Android now load valid saved records while isolating unreadable, oversized or invalid records. A damaged progress record no longer stops application startup. Corrupt sending-ticket state is also isolated; it does not create storage authority or get silently overwritten by a new transfer. The [completion audit](completion.md) remains active.

## Owner decisions

**Review saved progress** identifies affected file, batch and sending records. The affected queue cannot start or resume work until its unreadable records are reviewed. Existing valid receipts remain visible. Discovery and text previews remain independent of damaged sending-ticket state.

**Archive progress record…** requires confirmation and moves the damaged metadata into the component's private `recovery/<random-id>/` folder. It preserves available base, backup and staged metadata files. The receiver never opens a destination from an unreadable record. Private partial bytes, published provider files, completed copies and created folders are kept. Unknown progress is not treated as complete or automatically reconstructed. A new copy may be needed; receivers using an archived source-ticket cache must prepare a new source transfer.

The app shows the number of preserved recovery folders and offers **Remove preserved metadata backups…** with a separate permanent-removal disclosure. This removes only eligible archived metadata and its empty container; live journals, staging files, provider copies and destination folders are outside this operation. Unexpected contents, symbolic links or redirected recovery locations are refused. Windows also refuses hard-linked metadata files. A partial failed archive's known container is retained for retry. This cleanup makes the bounded recovery shelf manageable on Android as well as Windows; there is no automatic purge.

## Ownership and failure boundaries

- Every receiving file retains its batch identifier. A missing or unreadable parent cannot promote a child to a standalone receiving, publication or background job. Both engines require the batch path to resume a batch child. The native apps expose missing-batch children separately for explicit owned-partial cleanup and record removal; completed file copies are kept.
- Android retains existing OS-access ownership while any receiving/batch metadata is unreadable. A valid record can retain its existing lease without inventing a missing framework permission. Archiving releases only the relevant manager/record lease and reconciles that manager's remaining leases; destination, sharing and other queued-job owners remain independent.
- Source-ticket recovery blocks new ticket preparation. A damaged cache has no active tickets, and local pause/revocation cleanup does not overwrite it or acquire new authority. Existing pinned-peer, grant and Android pairing-generation checks still apply after recovery.
- Archive decisions capture file identity and modification information. If metadata changes after review, the operation stops and asks for a restart/review. Windows uses relative handles and no-replace rename; Android validates private-file membership and regular-file type before moving metadata. No recursive destination traversal or provider deletion is involved.
- Receiving scans stop at 1,024 logical records plus an overflow indicator. Normal limits remain 128 file records and eight batch records. A scan overflow stays blocked for storage review/restart. Metadata read limits remain 256 KiB per Windows file record, 64 KiB per Android file record, 8 MiB per batch and 4 MiB per sending index. Recovery shelves allow 256 containers per component, displayed in pages of 20 issues. These are safety limits, not full-load performance evidence.

Android's atomic base/backup recovery is retained. A staged record without a committed base/backup becomes a review item. Failed archives can leave metadata already preserved in a container; retries verify earlier moves and do not delete destination data. Registry, profile-permission, inaccessible-storage and power-loss faults remain broader recovery work. The current archive store has no automatic import/restore or diagnostic-export flow.

## Verification

- Release solution builds with zero warnings/errors. The full .NET suite has 93 passing checks. Five new fault tests exercise mixed valid/damaged file queues, preserved metadata families and destination bytes, changed/uncommitted records, missing-batch child isolation, valid batch receipts and corrupt sending-state preparation. Metadata-backup cleanup refuses unexpected contents and preserves partial files.
- Android APKs, four JVM checks and lint build/pass (zero lint errors, 38 advisories). The full emulator suite passes 69 checks. Five new device tests cover private staging, retained/released framework leases, backup/staged records, changed-record refusal, missing-batch child isolation and damaged source caches. A native Compose accessibility-surface test starts the application with damaged receiving metadata, confirms archival, and verifies that private staging remains unchanged.
- The existing Android policy/storage/transfer/explorer checks passed apart from one initial new recovery assertion, which exposed empty containers being created before stale-record refusal. Validation now occurs before container creation; the corrected recovery tests pass. Existing batch restart, pairing-generation and activity-stop behavior remains covered.
- Actual Windows–Android interoperability passes after the loader changes: scoped explorer flows, a verified 2,097,169-byte Windows-to-Android receive/SAF save, a six-item phone-to-Windows folder, a real phone-file download and acknowledged transfers in both directions.
- A disposable native Windows fixture starts while disconnected with a damaged progress record. Its Transfers page displays the review action and confirmation; archival clears the issue while preserving the metadata locally. Backup deletion is verified through non-UI fault tests; the native Windows deletion control was not automated.

This work does not implement remote rename/delete, cross-device move or remote-destination upload. Those remain explicit file-management requirements. Full disk/locked storage, process-death/crash-point coverage, independent security review, physical-device/provider compatibility and release packaging remain completion gates.

## Windows files over 4 GiB

The large-file suite generates a 4,295,229,457-byte file for each journey. It embeds each block's 64-bit position in the generated bytes and computes the expected SHA-256 while writing. After publication it independently hashes both the original and destination, checks their lengths and requires exactly one destination file.

The download case pauses at the 4,294,967,296-byte checkpoint, reopens the receiver and resumes its original source ticket. The upload case loses a reply after the receiver accepts a range at that offset, reopens the sender and reconciles the original receiver receipt. The offline copy case pauses after crossing 2,147,483,648 bytes, reopens its queue and checks the actual saved checkpoint before completing the file. These offsets cross the unsigned and signed 32-bit limits respectively.

Local Release verification passed the upload case in 15 minutes 15 seconds and the download case in 2 minutes 18 seconds. The initial offline case had an exact-offset assertion that did not account for throttled progress notifications. The corrected case passed in 1 minute 20 seconds, reopening its 2,149,580,800-byte checkpoint and independently verifying the original and published file hashes. The test project built with zero warnings or errors.

The hosted Windows job for commit `ac84e75` passed all three cases in 6 minutes 52 seconds from a fresh checkout on 7 November 2026. Its Release test-project build had zero warnings or errors. [Hosted large-file job](https://github.com/JAPHARYROMAN/iConnect/actions/runs/42341929962/job/128311284621)

The complete workflow also passed the other 859 main Windows cases, 10 isolated preview cases and desktop publish-asset checks. The Android job completed debug, test APK and unsigned release builds, JVM tests and debug lint. [Complete workflow](https://github.com/JAPHARYROMAN/iConnect/actions/runs/42341929962)

Run the suite on Windows with at least 14 GiB free in the repository's filesystem. The fixtures use generated files under the repository's ignored `.local/large-file-tests` directory and remove their own files after their workers stop.

```powershell
dotnet restore tests/IConnect.Tests/IConnect.Tests.csproj --locked-mode
dotnet build tests/IConnect.Tests/IConnect.Tests.csproj -c Release --no-restore
dotnet test tests/IConnect.Tests/IConnect.Tests.csproj -c Release --no-build --no-restore `
    --filter 'FullyQualifiedName~LargeFileTests'
```

The hosted workflow runs these cases in a separate Windows job. Local execution used NTFS. Requests go through the in-process file service with an accepting attachment checker. Android providers, encrypted transport, installed security scanners and physical-device execution remain separate verification requirements. Step 71 of the [physical-network procedure](physical-network-validation.md) prepares the operator checks.

## Android large-file validation in progress

`LargeFileTest` adds generated 4,295,229,457-byte provider files for Android download, upload and offline-copy journeys. It is backed up on `codex/android-large-file-validation` while device verification continues. Debug and instrumentation APK builds pass; the three large-file cases have not yet completed successfully. The first download is progressing slowly on the disposable API 35 emulator, and provider-call overhead is under investigation. This work does not establish Android support above 4 GiB yet.
