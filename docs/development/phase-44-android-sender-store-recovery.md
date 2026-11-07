# Android sender-store recovery

Android now provides owner-reviewed recovery for an unreadable batch sender store. It preserves the entire flat metadata directory in an app-private sibling archive, then opens an empty sender queue. Originals, private source snapshots and receiver files remain outside that operation. The preserved records are available for local inventory inspection; they are never restored as sending instructions.

## Review and preservation

The controller pauses batch sends and waits for their cleanup callbacks before preparing a review. The review requires a blocked store, a healthy current local identity and foreground availability. It captures the store directory identity, every top-level file's native version, file and byte counts, unrecognized-name count, and current owner generation. Confirmation is single-use, expires after two minutes on a monotonic clock, and rechecks the inventory and owner generation.

A profile-level lease remains held while the inner store lease is closed and the whole directory is renamed. A new directory and inner lease are opened before the sender's old in-memory records and recovery state are cleared. An interrupted operation keeps its preserved files and blocks sending until reviewed. Reopening after preservation but before fresh directory creation opens an empty queue and retains the archive; archived intents do not reactivate.

Before preserving the store, recovery conservatively marks every retained ready private snapshot as possibly having receiver copies, provided its source store is healthy. This retains the additional-copy decision even when unreadable sender metadata cannot identify its source. It does not adopt an earlier identity's authority or reconstruct missing receiver controls. A damaged source store remains blocked for its own recovery.

The native dialog states that resetting the queue loses usable resume and stop controls for the preserved intents. It also states that receiver copies may exist and additional-copy review is required. Peer revocation does not prevent current-owner local recovery. Resetting local owner policy invalidates outstanding recovery reviews.

## Inspection and separate removal

The Transfers screen lists up to eight preserved stores. Owners can inspect names, byte sizes and recognized-name status in pages of 50 entries. Inspection reads the local inventory without interpreting its contents as live requests. Nested folders, symbolic links, hard links, redirected directories, excessive entry counts and excessive total size are refused and kept for local investigation.

Removing a preserved store requires a separate review of its exact remaining inventory. Unknown regular files are preserved during reset and explicitly counted in the removal review. Removal compares each reviewed file's native version before deleting it, processes at most 64 files per call, and never adds new or changed files to an existing authorization. The native loop yields between calls. Foreground withdrawal stops further work; the current process can continue the exact reviewed remainder. After restart, the remaining archive requires a fresh review. A change of owner generation also ends the old removal authorization.

The active sender queue, private snapshots, original provider documents and receiver-side plans are outside archive removal. A new current queue can coexist with removal of an older preserved store.

## Verification and limits

Debug and instrumentation APK assembly, unsigned release packaging, all **39 JVM tests**, and debug lint pass. Lint reports zero errors and 67 existing warnings. Build evidence: `.tools/phase44-android-build.log`.

The first focused device run passed **14 of 15 cases in 134.733 seconds**, including the actual Compose accessibility journey. The remaining case failed while its fixture attempted to create a hard link: Android returned `EACCES` before the recovery implementation could inspect it. The fixture now creates a permitted symbolic link. That corrected alias/nested-folder case passed in **1.011 seconds** on the final test APK. The production APK was unchanged by this fixture correction. Evidence: `.tools/phase44-android-recovery.log`, `.tools/phase44-android-final-test-build.log`, `.tools/phase44-android-alias.log`.

The focused cases cover damaged and missing parents with orphaned item receipts, actual received-byte and private-snapshot preservation, conservative copy decisions without receiver requests, stale and forged reviews, owner-policy reset, peer revocation, unknown files, bounded removal, changed/new archive entries, restart requiring fresh review, active-worker refusal, duplicate profile leases, preservation of a new active queue, archive limits, foreground withdrawal and the equivalent filesystem state of interruption after preservation but before fresh queue creation. The native screen case cancels both review dialogs, preserves unreadable metadata, inspects the inventory and removes only the reviewed archive while verifying private and original bytes remain intact and no sharing/session is enabled.

The final broad device regression passed **270 cases in 423.107 seconds**, including all 15 new recovery cases and the existing native screen journeys. It uses the same four coordinator-dependent interoperability-class exclusions and four separate full-capacity exclusions as phase 42. Evidence: `.tools/phase44-android-regression.log`.

The final application APK SHA-256 is `985b2f626ddc8ae28c844731656082f3d6a387d1fa0105b6590bd158e8f69afe`; the final test APK SHA-256 is `6efd07c1906c0616a43b71b72653b04d6e479bbddaeb7d1508b30b9e6209d102`. Installed APK hashes matched the built files before and after the final device runs. The owned emulator was stopped afterward, and the adb device list was empty. These are development APKs; the release APK is unsigned. Windows code was unchanged and its tests were not rerun for this Android-only phase.

This implementation uses app-private filesystem identities, no-follow file inspection and cooperative profile leases. It does not establish resistance to a hostile process with the same application UID, coordinated complete-profile rollback, real process/power-loss durability, or full-capacity recovery performance. The native hard-link count guard is present but was not exercised by this emulator because its sandbox denied fixture creation. Inventory review and inspection scan the bounded complete store; their largest-store latency remains unproven.

The [completion audit](completion.md) retains damaged private-source recovery, unidentified receiver-part management, full-capacity history/performance, encrypted cross-platform workflows, packaging, independent security review and the remaining release gates. No batch capability is advertised by this phase. Windows visual/accessibility verification remains stopped following the user's physical Escape. The [physical-network kit](physical-network-validation.md) remains prepared for the user to run after implementation.
