# Android transfer lifecycle and storage-access ownership

Implemented 3 November 2026 (Africa/Nairobi). This extends [Android receiving](phase-4-android-receiving.md). The [full completion audit](completion.md) remains authoritative for unfinished product and release work.

## Explicit background work

An owner can select **Continue this transfer in background** for an already active receiving or publication job. The authorization covers that job ID in the current process and session. It is not a global always-on preference and is not persisted across process death. Notification permission and an enabled transfer notification channel are required. The UI must receive the service's acknowledgement before leaving; otherwise work pauses normally.

`PhoneController` is now owned by `IConnectApplication`, so a started service can retain the same authenticated channel and receiver when an activity stops. The non-exported `TransferService` accepts only an outstanding, random, process-local authorization token. Invalid, obsolete or null starts do not create a connection, recreate a controller or authorize jobs. It returns `START_NOT_STICKY`, has no boot receiver, and performs no unattended reconnect or rescheduling.

While the UI is inactive, only the explicitly selected receiving/publication jobs continue. Other receiving jobs pause. Incoming file RPCs are denied before provider access and checked again before a response is sent, preventing queued results from exposing phone storage after availability ends. Active provider operations and source preparation are canceled. New connection offers and new diagnostic transfers are refused. Opening the UI restores ordinary approved browsing; pairing and per-peer grants still determine access.

The notification shows aggregate progress and a pause action. It omits names, paths, peer details and invitation material, and supplies a generic public version for the lock screen. Pause stops selected work; losing the service while the UI is inactive ends the channel. The service stops automatically when selected jobs finish or stop. It uses a timeout-bounded partial wake lock and releases it on destruction, rate-limits notification updates, and checks notification availability during execution. An application timeout and the platform `onTimeout` callback pause work rather than silently restarting it. Process termination, force-stop, provider withdrawal and network failure can still interrupt a transfer; durable records support deliberate recovery after reopening the app.

This implementation uses the Android `dataSync` foreground-service type for the existing live WebRTC session and local publication. It does not create a permanently available phone server. Android's [foreground-service types](https://developer.android.com/develop/background-work/services/fgs/service-types), [timeout rules](https://developer.android.com/develop/background-work/services/fgs/timeout), and [user-initiated job guidance](https://developer.android.com/develop/background-work/background-tasks/uidt) remain distribution and compatibility requirements. A future resumable connection scheduler must not use OS restart as a way to reopen storage availability without owner action.

## Independent Android permission ownership

`AndroidSafAccess` tracks independent leases for each sharing approval, the selected download destination, and each persisted download record. These leases retain OS access only; they cannot authorize a peer or widen a peer's policy. Read/write bits are retained only while an owner needs them. Removing one sharing approval no longer releases a folder that a local download still needs. Clearing or replacing the selected destination preserves older queued jobs' destinations. Removing the last applicable record releases the underlying persisted permission; saved provider files are left intact.

Each queue reconstructs its own lease namespace from its validated journal records on opening and reconciles abandoned leases in that namespace. Legacy sharing preferences, destination selection and the earlier lease registry migrate into private JSON state. Legacy destination selection is not repeatedly reimported after being cleared. A missing Android permission is never manufactured by journal recovery: the owner must select the folder again through Android.

Per-peer policy instances share one process-local policy and lock for the app's private storage directory. Updates commit before the lock is released, with rollback on failure. A second instance cannot overwrite another instance's changes or resurrect a removed grant. Live grant checks use this shared state, avoiding a disk parse for every 32 KiB source chunk.

Approval policy, access ownership and receiving journals use bounded `AtomicFile` state with explicit descriptor sync and read-back before publishing their in-memory result. This avoids treating a failed cached preference commit as a successful approval and catches failed atomic-file promotion. Receiver journals allow up to 64 KiB, including bounded provider URIs. The [AtomicFile API](https://developer.android.com/reference/android/util/AtomicFile) requires caller synchronization; the app supplies it. These checks do not establish universal power-loss or full-disk recovery at every crash point.

Backup remains disabled, with explicit cloud-backup and device-transfer exclusions for the credential- and device-protected app domains. This keeps pairing policy, source tickets and private staging from being intentionally restored onto another installation through these configured modes. The packaged configuration is checked; real manufacturer backup/restore and newer-platform transfer behavior still require field validation. See [Android backup configuration](https://developer.android.com/identity/data/autobackup).

## Verification

The final Android debug/test APK build, four JVM rules tests and lint passed. Lint reports zero errors and 31 advisories, including existing dependency updates and style suggestions. The Windows interoperability runner rebuilt with zero warnings/errors; production Windows code was unchanged in this phase.

Thirty-one Android instrumentation tests passed in **6.362 seconds**. In addition to existing storage, source-ticket, receiving, Keystore and activity coverage, the tests establish:

- Peer revocation and destination clearing preserve a queued job's OS access across manager restart; removing the last record releases it.
- Changing the selected destination preserves an older job's destination, and separate read/write owners release only unused permission bits.
- Multiple policy instances observe each other's updates and cannot resurrect a removed approval.
- Legacy policy/destination migration occurs once; a cleared destination stays cleared.
- Injected failures at both approval-policy and access-registry writes leave the existing on-disk approval intact and create no live new peer approval.
- Inactive availability denies old file references without widening or deleting their grants.
- Source preparation canceled before worker execution can restart and become ready after availability returns.
- Obsolete service intents do not create background availability; the service is non-exported and declares the expected type.
- The packaged backup exclusions cover each configured private-storage domain in cloud and device-transfer modes.

The real activity/service/notification interoperability fixture passed in **37.851 seconds**. It held an actual 2,097,169-byte Windows source range to make the lifecycle transition deterministic, authorized that receiving job, stopped the Android activity, and proved an unselected job paused. Windows then reported `BACKGROUND_BROWSING_DENIED` for a phone-root request. After releasing the source gate, the selected download completed and the service/connection stopped automatically. A separately authorized, deliberately stalled provider save was paused through its actual notification action. Explicitly saving another copy then produced the exact expected bytes through SAF. Both the provider and source gate exist only in disposable test fixtures.

The ordinary mutually authenticated two-way interoperability fixture also passed in **34.399 seconds**: Android saved and compared the 2,097,169-byte Windows file (`2ce3172ab5811f22d8e2d9d45d2c71397e38a636b4a5fd5a09517b7454fb2368`), Windows received the actual 42-byte phone file (`e26a25141c8dfacf5e9276534024b8c48c81195c9dd5b7227b8fd662ad5ce9c9`), and both synthetic diagnostics passed. The final queued-source cancellation regression is covered by the 31-test device suite above.

To repeat the background check, follow the existing [interop setup](verification.md#reproduce-the-interoperability-check), using `com.iconnect.proof.BackgroundInteropTest` instead of `InteropTest`. The test APK grants its own notification permission on the disposable emulator and uses real activity lifecycle transitions. Compare/approve both fixture codes, connect from the runner, and wait for `BACKGROUND_READY`. Run `phone-denied`, require `BACKGROUND_BROWSING_DENIED`, then run `release-background`. Require `BACKGROUND_DOWNLOAD_VERIFIED`, `NOTIFICATION_PAUSE_VERIFIED`, `BACKGROUND_SAF_BYTES_VERIFIED` and `OK (1 test)`. No personal folder is automatically approved.

## Remaining scope

This phase initially covered receiving and local publication. Explicitly selected phone-source sending was subsequently added in [phase 6](phase-6-android-sending.md). Broader API 29–36/device coverage, actual platform timeouts, force-stop/process-death recovery, notification withdrawal, power/network transitions, provider compatibility, damaged-state repair and every write/crash boundary remain unproven. Identity/trust settings outside the new policy/access stores still need production lifecycle and persistence review.

The Compose controls and real activity/service lifecycle have automated coverage, but a complete visual, screen-reader and physical-phone workflow review is pending. This phase does not finish folder/batch transfer, full file management, advanced explorers/previews, Android audit history, Windows background hosting, distribution signing or the independent-network/security release gates.
