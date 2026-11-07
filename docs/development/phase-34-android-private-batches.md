# Android private batch preparation

Android now has a private folder-preparation engine matching the Windows source boundary. It is not yet connected to the complete native sending queue, and `upload-batches-v1` remains unadvertised. The source-capacity case and final broader regression pass. This document does not close the full sending-workflow gate.

## Local authority and snapshots

`PhoneLocalBrowser.captureBatch` captures only entries in the current visible catalog. Its source callbacks retain the same browser instance and revision. Navigation, foreground withdrawal or closure invalidates them. The single-file capture path still derives the selected kind from the catalog, so a caller cannot turn a directory into a file by changing its supplied entry. Local shortcuts remain separate from remote storage grants.

`AndroidBatchSources` discovers a bounded tree through that local service. Returned pages must belong to their original parent and grant; repeated items, ambiguous names, malformed ranges and excessive bounds are refused. Sensitive/unsupported names receive explicit errors without descendants. Directory changes and source permission/availability withdrawal stop preparation. The saved wire manifest contains only portable names, parent indexes, file lengths, digests and per-item errors; provider URIs, opaque source references, local paths and private inode identities are not included.

The engine records the private plan directory's native identity and each file's ownership before writing payload bytes. Source metadata/version and exact bounded ranges are checked. Each private file is flushed, closed, reopened through `O_NOFOLLOW`, and completely reread for SHA-256 before its final receipt is committed. Empty files and empty/nested directories remain in the canonical plan. Source paths and original access callbacks are not persisted: interrupted preparation retains a failed record and identified partial copies, and requires a deliberate new source selection.

A sender must open and retain a verified `SourceReader` before remote effects. Opening hashes the complete private file against the original manifest descriptor. Native identity, length, high-resolution modification/change timestamps and the current private pathname are checked for bounded reads. Android app-private storage supplies the UID boundary; this is not a Windows-style deny-write file sharing guarantee. Reader availability is a required callback, checked alongside current owner/pairing authority on every range. Cleanup refuses active readers, and shutdown closes them. SHA-256 establishes captured-byte integrity, not content safety.

Raw OS descriptors have explicit owners. Temporary reads/writes close through `useOsDescriptor`; a retained reader closes its own descriptor. Borrowed stream ownership is not used as a substitute for closing an `Os.open` result. No original document is reopened to transmit a prepared file.

## Bounds and recovery

There are eight private plans, at most four for one peer, two concurrent preparations, 16 GiB of retained private source bytes per peer and 64 GiB in aggregate. A complete private batch needs local storage for all its successful files. Admission reserves those bytes against other preparations and leaves a 64 MiB physical margin. Original source data is kept. Progress records are small, with committed prepared-file/byte counts; full manifests are not rewritten after each file.

The existing manifest limits remain: 128 selected roots, 4,096 total items, depth 32, 2,048 component characters and 4 MiB encoding. Parent/file metadata records have a 64 KiB read bound. There is one host file lock, and the local shortcut retains its existing framework access; preparation does not create one OS permission-registry row per file. A two-entry manifest cache checks native versions before reuse. Cold startup validates ready plans against their canonical manifest, exact expected private directory contents and file identities/sizes/versions. Missing, uncommitted, malformed or unowned parent metadata blocks new preparation.

Private removal uses a short-lived, single-use owner review. It captures all native file versions and checks the complete directory before deleting payload. Changed confirmations, added files, replacement directories and changed identities require another review. Current pairing authority is required for sending; the local owner can still review/remove identified private copies after revocation. This operation cannot remove original provider documents or receiver-side copies. Complete damaged/absent-object archive/recovery controls remain work for the native workflow.

The subsequent [shared outgoing resource phase](phase-35-shared-outgoing-resources.md) joins storage reservations and worker limits with single-file sending on both platforms. Exact remote intents and receipts, per-plan receiver stop/completion accounting, full queue/recovery controls, accessibility, large-file/provider/lifecycle and fresh cross-platform/physical-network evidence remain outstanding. The [completion audit](completion.md) retains those requirements; the [operator test kit](physical-network-validation.md) is prepared for after implementation.

## Verification

The first complete APK/JVM/lint build passed; the current build also passes debug, instrumentation and unsigned-release packaging with **38 JVM tests**, zero lint errors and 64 advisories. Installed APK SHA-256 values were checked against the build outputs.

All **12 initially selected device cases passed in 15.024 seconds**. They cover actual multi-range private bytes, nested/empty/zero-byte items, sensitive entries, unshared originals, original changes after preparation, changed sources during preparation, foreground withdrawal, new pairing generations, full-digest rejection even after receipt versions are rewritten, missing/damaged metadata, replacement private directories, single-use cleanup reviews, added unreviewed files, active readers, cancellation and one-host ownership. The resource checks require snapshot descriptors to be closed after operations.

The separate capacity case passes in **471.168 seconds** (7 minutes 51 seconds): exactly **4,096 items**, including **4,092 actual private files**, nested/empty directories, a zero-byte file and an explicit sensitive-directory error. Every successful file is copied, flushed, hashed and read back. The manager then reopens, validates retained metadata and reads the final nonempty source through a verified reader. Original remote grants remain empty. Live samples show zero or one active snapshot descriptor. This proves private preparation/restart through the fixture provider, not transmission or receiver publication.

The capacity run used the same app build as the final test APK; subsequent changes add only the interrupted-intent/unbound-folder and malformed-EOF cases. The final broader regression passes **188 tests in 185.565 seconds**, including all 14 focused source cases, existing single-file preparation/sending, local browser screens, previews, trust/revocation, reconnect/lifecycle, incoming-file resource checks and recovery. It excludes four live-invitation cases and four separately tracked capacity cases. These separate runs do not establish one combined all-device-case pass, complete native batch sending or fresh Windows–Android interoperability.

The owned emulator was stopped after verifying its AVD identity. Windows Computer Use remains stopped and its existing desktop fixture process was not controlled or replaced. Physical-provider, large-file, power-loss, installed-release and independent security evidence remain separate gates.

| Artifact | SHA-256 |
| --- | --- |
| App | `eecd68abb10db642c259e01a1216fc5adfa8f3d5146ab7dc323d6eacffaf05a1` |
| Capacity instrumentation | `599c0a6c6534ee8267c3a3fbaa893057babfde52a0ec61dd382aa1f42d406576` |
| Final instrumentation | `c2c760124e72c2d3520466dccc90b9c2f42411bb1876462542483ab51b7a30e3` |
