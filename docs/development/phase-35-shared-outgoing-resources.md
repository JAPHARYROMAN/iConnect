# Shared private outgoing resources

The native controllers now open their ordinary file sender and private batch preparer against one profile resource inventory. This closes the separate-budget gap documented in the earlier preparation phases. The complete batch sending queue remains under construction, and `upload-batches-v1` remains unadvertised.

## Reservations and disk admission

Both stores retain their complete declared private-copy sizes, including paused, completed and failed records, until the owner removes the copies. Together they enforce 16 GiB per peer and 64 GiB per profile. Existing stricter ordinary-file and per-store record limits still apply. Recovering saved inventory can retain an over-limit state; it does not silently discard copies to make room.

Both inventories must have loaded successfully before either admits a new copy, including a new zero-byte copy. Closing one manager retains its reservations and makes that inventory unknown until a new manager recovers it. A stale manager cannot close a replacement manager's inventory. Pools reject duplicate active store owners and use one canonical profile location.

Malformed ownership metadata, failed metadata commits and detected unowned private single-file copies make the relevant inventory uncertain. The other store then refuses new preparation. Reductions and owner cleanup can still proceed through their existing authority checks. Ordinary orphan cleanup explicitly reloads inventory; damaged batch ownership remains blocked for the later recovery workflow. No original source or receiver copy is deleted by this accounting layer.

Pending physical bytes are also shared. Batch progress reduces pending bytes as verified snapshots are committed; ordinary staging conservatively reserves its full length until preparation stops. Increasing pending reservations samples free disk space while holding the inventory admission lock and leaves a 64 MiB margin. This prevents two managers from separately passing a free-space check and then overbooking that same space. Other apps can still consume storage afterward; write failures preserve existing progress and copies for review. Metadata, manifests and filesystem overhead are not payload byte quotas.

The resource inventory never calls another manager while holding its lock. Its only admission callback reads filesystem free space. This avoids a manager-to-manager lock order through accounting.

## Worker lifetime

Two payload worker leases are shared across ordinary preparation, ordinary sends, batch preparation and retained batch source readers. Batch readers hold their lease through complete pre-send checksum verification and subsequent range reads. A third worker is refused before a new preparation record or private payload descriptor is created. Leases release once even if cancellation and disposal both run.

Windows now tracks its retained batch readers, refuses removal review while they are open and closes them during host shutdown. Android retains the equivalent descriptor tracking and closes its outgoing inventory after workers/readers stop. The ordinary Android sender rejects new mutations after shutdown, preventing a closed coroutine host from creating another retained copy. Disconnect and Android foreground withdrawal pause batch preparation alongside the existing transfer paths.

The controller exposes combined retained byte counts and private batch preparation state. Complete native queue actions, remote admission/intent recovery, per-plan receiver stop/completion accounting and owner recovery controls are still required before enabling batch sending.

## Verification

The Windows solution builds with zero warnings and errors. The final selected main regression passes **346 tests in 1 minute 11 seconds**, including all seven shared-resource cases. A separate 4,096-item / 4,095-file private snapshot/restart case passes in **36 seconds**, and all **eight isolated preview tests** pass in **nine seconds**. The unchanged receiver-capacity case was not repeated for this change. The first main run passed 345 cases and failed the existing temporary-reader checkpoint timing case while the Android build/emulator startup were active; the subsequent complete selected run passes. This does not establish the exact cause of that timing failure.

Android debug, instrumentation and unsigned release packaging pass, alongside **38 JVM tests** and lint with **zero errors and 67 advisories**. The free-space checks intentionally use actual usable space; the lint suggestions to include clearable cache do not change this conservative admission policy. Installed APK hashes match the build outputs. All **22 focused device tests** pass in **13.124 seconds**. They include both stores working together, shutdown/reopen, stale host closure, damaged batch ownership, orphan file cleanup, peer/global byte boundaries, idempotent worker leases and competing physical-space reservations, alongside the earlier private-source checks.

The broader Android regression passes **196 tests in 123.616 seconds**, including all 22 focused cases, native local/receiving screens, ordinary private sends, trust/revocation, lifecycle, previews and saved-progress recovery. It excludes four live-invitation classes and four separately tracked capacity cases. The separate current-build source-capacity case passes in **190.374 seconds**: 4,096 items, including 4,092 actual private files, copied, flushed, hashed and read back, followed by host reopening and a verified read of the final nonempty source. Original remote grants stay empty and descriptor assertions pass. All three device cohorts use the same app and instrumentation APKs. No unchanged receiver-capacity case or fresh cross-platform run was repeated for this change.

| Artifact | SHA-256 |
| --- | --- |
| App | `526d27d9b4547ec3c2d46bdc74ef5c6c510339bb8fadd8fd3a468ddf8ddc5b14` |
| Instrumentation | `5e3a7e361c0e861e14a56093de6dad679c399df9fd8c926f49a7e0cb688128cf` |

The owned emulator was stopped after checking its AVD identity. Windows Computer Use remains stopped, and the existing desktop fixture process was not controlled or replaced. The operator-run [physical network test kit](physical-network-validation.md) remains prepared for execution after implementation, including mixed file/folder concurrency and retained-copy accounting checks. No physical-phone or fresh Windows–Android result is claimed here.
