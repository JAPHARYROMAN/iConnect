# Android background sending

Implemented and verified 3 November 2026 (Africa/Nairobi). This extends [phase 5](phase-5-android-lifecycle.md) to phone-source transfers. The [full completion audit](completion.md) still contains substantial product and release work.

## Owner-selected source authority

When a computer prepares an approved phone file, Android shows that source ticket under **Sending from this phone**. The owner can explicitly allow that active sending transfer to continue with the existing notification-backed service. Approval captures both the opaque ticket and its current authenticated file-service scope. It cannot carry over to a different session, another ticket, or a newly prepared file. It is not persisted as background availability across process death.

While the UI is inactive, the selected ticket may use download status and bounded range reads. General browsing, metadata/range access through ordinary explorer references, new preparation, directory changes and other source tickets are denied. Existing peer-owned ticket release remains a cleanup-only operation; it returns no file data. Incoming requests are checked before provider access and again before sending a response. A concurrent owner pause also invalidates a range after provider I/O, so an old read cannot publish content under a withdrawn ticket.

The existing source boundaries still apply to every operation: pinned authenticated peer, current per-peer grant, actual Android access, approved tree/ancestry, exclusions, file identity/version and size. Selecting background sending does not create or widen a storage grant. Leaving the UI cancels unrelated provider operations and source preparation while preserving only selected source scopes. Sender state contains local display metadata, not a remote filesystem path.

## Pause, expiry and completion

The service supports receiving, sending and publication together. A source ending does not stop other selected work. When the final selected job finishes or stops, the service releases its wake lock and notification; the channel closes if the UI is inactive. The notification remains private/generic and uses indeterminate progress when sending, because only the receiver can verify its saved copy. The phone's read-byte statistic is not a receipt of verified destination completion.

Notification pause stops the selected source immediately and persists its paused flag in bounded, synced atomic source state. Status/read access remains denied until the owner selects **Allow this transfer to resume** in Android. The computer can then reconnect and resume its existing download using the same source ticket and expected checksum. Source mutation revisions prevent a delayed resume from overriding a newer pause or revocation. Canceling a sending record removes that ticket without deleting the original phone document.

Source activity uses a monotonic clock. Background authority expires after two minutes without advancing preparation or range progress. Status polling and repeated already-requested ranges do not extend that window. Provider/grant failure also retires active source work. The service rechecks selected source liveness while running. Cached tickets can remain for deliberate foreground recovery within their existing 24-hour lease, but an inactive cached ticket cannot start a background session by itself.

Revocation captures and blocks the old peer's existing source IDs before asynchronous cleanup. Cleanup cannot delete newer tickets subsequently created for that peer, and an older pending resume cannot undo the block. Controller shutdown drains accepted source-state writes and awaits source workers before releasing its runtime. Source cache loading validates identifiers, bounds, expiry, pause types and duplicate IDs. The wire protocol remains `downloads-v1`; these are local owner-policy and lifecycle changes, so existing compatible Windows receivers use the same prepare/status/read/release operations.

## Verification

Android debug/test APK assembly, four JVM rules tests and lint pass. Lint reports zero errors and 31 existing/development advisories. The Windows test runner rebuilds with zero warnings/errors; no production Windows transfer code changed in this phase.

The combined Android suite passed **35 tests in 6.542 seconds**. New cases establish selected-ticket isolation, denial of ordinary range reads and new preparation in the background, peer/session separation, persisted pause across source-store recreation, stale-resume rejection, idle expiry despite status/repeated-range polling, and cleanup that cannot remove a newer ticket. Existing storage, receiving, URI ownership, failed-commit, backup, Keystore and activity checks also pass.

The real **SendingInteropTest** passed in **90.704 seconds**. It used the actual activity, service, notification and authenticated native channel with a disposable provider's **2,097,169-byte** `kutuma.bin`:

1. Windows prepared a selected file and a separate unselected ticket. Android approved only the selected file for background sending, then stopped its activity.
2. Windows proved that roots, the other ticket's status/read, ordinary explorer range reads, and new preparation were denied.
3. The selected transfer completed; Windows compared every byte and reported SHA-256 `2ce3172ab5811f22d8e2d9d45d2c71397e38a636b4a5fd5a09517b7454fb2368`. The Android service ended automatically.
4. A second send was paused through its actual notification. Android persisted the pause, explicitly allowed resumption, and reconnected. Windows resumed the same ticket from its durable interrupted receiver and verified the same bytes/checksum again. Keep-both finalization preserved the first saved copy.

The existing background receiving/publication interoperability fixture was rerun successfully in **36.067 seconds**, including denied phone browsing, pausing an unselected receiver, automatic shutdown, notification pause during provider publication, and exact SAF-saved bytes. These are emulator/fixture results, not physical-phone or independent-network evidence.

To repeat sending, use the [interop setup](verification.md#reproduce-the-interoperability-check) with `com.iconnect.proof.SendingInteropTest`. Compare/approve both fixture codes, connect Windows, then run `start-phone-send` after `START_PHONE_SEND`. At `SENDING_BACKGROUND_READY`, run `phone-denied` and `sending-denied`; require both denial markers before `release-sending`. Require `REAL_BACKGROUND_PHONE_SEND_VERIFIED` in Windows. At `RECONNECT_FOR_PAUSE`, connect again, then use `start-phone-send` at `START_PHONE_SEND_AGAIN`. After `SENDING_NOTIFICATION_PAUSE_VERIFIED`, use `release-sending` and require `PHONE_SEND_INTERRUPTED`. Reconnect at `RECONNECT_FOR_RESUME`, then use `resume-phone-send` at `RESUME_PHONE_SEND`. Require a second matching Windows digest, `SENDING_SAME_TICKET_RESUMED` and `OK (1 test)`. All gates and automatic fixture approvals are test-only and use disposable data.

## Remaining work

Full visual/accessibility review of the sending controls, physical devices, independent networks, long-running/large files, platform timeout/force-stop/process-death behavior, provider faults and every persistence/crash boundary remain unproven. This adds narrowly scoped source continuation; it does not finish Android trust-store persistence/epoch handling, concurrent share/re-pair behavior, queue repair, full history/diagnostics or provider compatibility. Folder/batch transfers, full explorers and file management, Windows background hosting, packaging/signing and the remaining security/distribution gates are still required.
