# Retaining the additional-copy decision when sender history is lost

The private snapshot now retains whether a receiver copy may already exist. Previously, the additional-copy requirement depended on surviving sender rows. Purging every row, or losing that entire store, could leave a ready private snapshot looking unused. Both native queue screens and sender engines now retain the additional-copy requirement independently of those rows.

## Ordering and storage

Before the first `BEGIN_UPLOAD_BATCH` request, each sender saves its original intent, then synchronously commits `MayHaveReceiverCopies` / `mayHaveReceiverCopies` in the matching private snapshot. That commit checks the snapshot's original authority, manifest hash and ready state. Failure prevents admission. Receiver context discovery before this point is read-only. Resuming a retained intent continues to check the original receipt; the flag does not authorize replaying admission.

The flag is monotonic for the lifetime of a private snapshot. Archive, sender-history purge, local stop and sender-store recreation do not clear it. Source cleanup remains a separate reviewed operation. No original path, sharing permission or new receiver capability is added by this metadata.

Upgrading a snapshot prepared under an earlier local identity preserves that identity and its sending denial. It does not adopt the snapshot for the current pairing. Local owner cleanup remains available after the normal identity/policy checks; the Android screen identifies the earlier identity and disables sending and plan inspection for it.

Private source metadata now uses schema 2 and requires a correctly typed boolean. Missing or malformed schema-2 state blocks the source store, preserves its payload and prevents remote requests. Old ready snapshots are upgraded conservatively with the flag set, including when no usable sender history survives. This expresses uncertainty about prior copies; it does not assert that a copy succeeded. Valid retained sender headers also backfill the matching source before history can be discarded. A new schema-2 snapshot whose job stops before admission stays unmarked.

Android source-parent writes also refuse an existing atomic `.new` or `.bak` sibling, including symbolic links. They preserve the unexpected entry and block the store instead of overwriting it during the copy-history commit. The existing Windows exclusive-create behavior already refuses an occupied `.new` entry.

## Native behavior

Both private-source cards explain when receiver copies may exist. Plan review then offers an explicit additional-copy confirmation even if the sender queue is empty. The storage layer checks the same condition, so stale UI state or a direct controller call cannot silently bypass it. Queueing an additional copy creates a fresh local request; it does not send until destination review. Existing receiver files remain intact under the selected collision behavior.

## Verification and limits

The new native storage cases cover the flag's presence before admission, complete history purge and cold source reopen, removal of the complete sender store, legacy upgrade with and without surviving history, earlier local identities, malformed/missing state, unstarted local stop and a blocked metadata commit. They use actual private snapshots and receiving storage.

- Windows Release solution build passed with no warnings or errors. The final selected main regression passed **405 cases in 1 minute 38 seconds**, using the same three separate 4,096-item exclusions as phase 41. The ten new cases are included in that run. Evidence: `.tools/phase42-windows-final-build.log`, `.tools/phase42-windows-owner-build.log`, `.tools/phase42-windows-owner-main.log`.
- Android debug, instrumentation and unsigned release APK builds passed, along with **39 JVM tests** and debug lint. Lint reported no errors and 67 existing advisories. Evidence: `.tools/phase42-android-owner-build.log` and the generated reports.
- The **eight new Android device cases passed in 9.981 seconds** on the owned headless emulator. Installed APK hashes were checked against the built files before testing. Evidence: `.tools/phase42-android-copy-history.log`.
- The final Android regression passed **255 cases in 266.457 seconds** on those same APKs, including all eight new cases and the existing native screen journeys. It uses the same four coordinator-dependent class exclusions and four separate full-capacity exclusions as phase 41. Evidence: `.tools/phase42-android-regression.log`.

The application APK SHA-256 is `fe62041deef6ca7228fcffcf07cfbbeb375f2f1ee7425f171934bcee63ff0f3f`; the test APK SHA-256 is `fc74a61a7bd4f91ae7d5377788ca5743dba6d7b9f5999bbf7375c6c7f961a2b6`. Installed hashes matched before and after the final device runs. The owned emulator was stopped afterward. These are development APKs, not signed production releases.

This does not reconstruct missing intents or receiver receipts, grant permission to stop an unidentified old plan, or repair a damaged private source store. It also does not detect coordinated rollback of both stores to an earlier valid profile snapshot. Those owner-recovery and rollback gates remain in the [completion audit](completion.md). Batch capability advertisement, fresh cross-platform workflow checks, Windows visual/accessibility checks, and the operator-run [physical-network test](physical-network-validation.md) remain pending.
