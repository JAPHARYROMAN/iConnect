# Durable file download development milestone

This phase added real file receiving on Windows and source-ticket services on Windows and Android. Android receiving and provider destination publication were subsequently added in [phase 4](phase-4-android-receiving.md). The [full application objective](completion.md) remains active, including folder/batch transfers.

## Wire behavior and authorization

The additive `downloads-v1` capability introduces prepare, status, range-read and release operations within the existing bounded file RPC. Preparation returns an opaque ticket immediately while a bounded background worker computes SHA-256. Status polling reports preparation rather than requiring a long-running RPC. Both clients must be updated for this capability.

Tickets expire after 24 hours and bind the authenticated peer, existing grant, exact file identity/provider document, version and size. Private source state stores local references; none of those paths, provider IDs or tree URIs cross the channel. Each read rechecks the current grant, permissions, exclusions and source metadata. A stored ticket cannot override revocation, expiry or a changed source. Android source membership is checked through its approved SAF tree and current ancestry. Leaving the Android session cancels its pending preparation work.

Source hashing uses bounded buffers and at most two preparation workers. Source-ticket state is bounded globally and per peer. Completed Windows receives request ticket release; abandoned or unreachable tickets expire. The current limits are 128 source tickets globally and eight per peer. More extensive lifecycle/cleanup coverage remains part of the fault matrix.

## Windows receiving

The receiving queue persists private per-job records, with at most 128 records and two active workers. The user selects a normal local destination; private/system locations are rejected for remote content. Each worker keeps at most four 32 KiB range requests outstanding, pins its requester to one authenticated session/peer, and uses 64-bit offsets with an explicit 8 TiB protocol size bound. Those bounds are implementation limits, not measured large-file evidence.

A receive writes only to an exclusively created `.iconnect-transfer-<random>.part` file beneath the selected, identity-pinned directory. Relative Windows handles reject redirected or multiply linked files. The receiver flushes data before persisting progress. Checkpoints include a hash chain of the committed prefix; resuming rechecks the prefix and truncates unacknowledged trailing bytes. Reconnection uses the source ticket rather than a stale session browse ID.

Completion compares the streamed SHA-256, flushes and independently re-reads the destination, then uses a handle-relative no-replace rename. Existing files remain intact. Keep-both retries numbered names only for actual name collisions, while in-use/access errors are reported. Skip and fail are available to the receiving engine; the current WinUI save flow defaults to keep-both. Replace is not implemented.

Finalization intent is persisted before rename. A restart after rename but before the completion receipt can reconcile the existing destination by its original file identity and expected hash, without reading the source again. Cancel persists intent before cleanup and deletes only its own verified temporary object. A moved/unavailable destination or replaced partial is left for local review; cleanup failure does not block durable device revocation. Completed files are not deleted by cancel. Terminal records can be removed separately from saved files.

WinUI now exposes file/destination selection, a Transfers page, progress, pause/resume/cancel and record removal. Resume requires the same trusted source device for missing data; a fully received file can be verified locally. These new controls are connected to the tested engine, but the complete visual file-picker/transfer-control workflow still needs its own native UI verification.

## Verification — 2 November 2026

Locked restore, the Release solution build and 62 .NET tests pass. The suite includes an actual download over mutually authenticated native channels, 2 MiB-plus Unicode-named files, preserving an existing destination, pause/resume across receiver and source-service restart, corrupt network bytes, changed partial content, source changes, peer isolation, revocation, finalization receipt recovery, and cancellation with a moved destination. The rest of the previously established identity, pairing, storage and desktop tests also pass.

Android debug/test APK builds, JVM tests and lint pass. Ten combined Android instrumentation tests pass, including preparation checksum, actual provider bytes, ticket persistence, peer isolation and changed read permissions. The added ticket test found and fixed a stale preparation snapshot that could restart hashing after completion.

The real Windows/Android interoperability fixture passed in 33.782 seconds. Windows reported `REAL_PHONE_DOWNLOAD_VERIFIED` with SHA-256 `e26a25141c8dfacf5e9276534024b8c48c81195c9dd5b7227b8fd662ad5ce9c9` after receiving and finalizing the Android provider's actual `simu.txt` fixture. Both original synthetic checksum transfers also completed. This actual phone-source fixture is 42 bytes; the larger 2 MiB interruption cases use Windows fixtures. No >4 GiB end-to-end, physical-phone or independent-network result is claimed.

To repeat, follow the [interop setup](verification.md#reproduce-the-interoperability-check), build/install the current two Android APKs, and use `download-phone` in `IConnect.InteropRunner` after `FILE_BROWSING_VERIFIED`. Expect `REAL_PHONE_DOWNLOAD_VERIFIED` before completing the existing `send` step. All fixture data stays beneath ignored repository or test-APK private directories; personal folders are never automatically approved.

## Remaining work and recovery limits

Android receiving, destination selection and explicit SAF publication/recovery were subsequently added in [phase 4](phase-4-android-receiving.md). Folder manifests/batches, uploads to remote destinations, supported replace semantics, queue/network preferences and background transfer lifecycle are not finished. Source preparation and cancellation need broader provider and storage-failure coverage.

The Windows crash/fault matrix is incomplete: early temporary-file creation before its identity receipt, disk-full/state-write failures at every boundary, directory-entry power-loss durability, large files and concurrent filesystem mutation need further work. Unknown or replaced partials are never adopted or deleted merely because their names match. Existing tests establish the specific restart and finalization cases above, not universal crash recovery or production certification.

The Windows finalization primitive follows Microsoft's [FILE_RENAME_INFORMATION contract](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_file_rename_information). Android destination work must respect the separate [DocumentsProvider contract and capabilities](https://developer.android.com/reference/android/provider/DocumentsProvider); a portable no-replace rename guarantee must not be inferred from a preflight existence query.
