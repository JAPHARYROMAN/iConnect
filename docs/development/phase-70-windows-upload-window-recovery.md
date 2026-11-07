# Windows incoming upload write-window recovery

An ordinary incoming upload has a 24-hour write window. Its status and cancellation remain available after expiry, but writing and finalization require a current window. Windows now lets the receiving owner review one expired receipt and explicitly approve a new window for that same copy. The sender then resumes the existing receipt separately. The [Android counterpart](phase-71-android-upload-window-recovery.md) records its later implementation and verification.

## Owner workflow

In Transfers, each ordinary incoming receipt displays its write-window deadline. **Review this write window…** checks an idle, identified receiving partial or recorded publication. The review shows the filename, destination, retained checkpoint, earlier deadline and exact proposed deadline. Cancel preserves the journal and destination bytes. Confirmation rechecks the bytes and persists only the reviewed window timestamp; it does not send a request, start verification, create another copy or change the recorded publication decision.

The window ends 24 hours after preparation of the review, including time spent reviewing. A review lasts at most five minutes on both the wall and monotonic clocks; rollback of either clock rejects it. A new review is available only after expiry. Read-only status cannot extend a deadline. A confirmed window survives application restart, while unconfirmed offers remain memory-only and are single-use. Completed receipts and their reserved bytes are retained through the approved window, so the next incoming copy cannot immediately remove a renewed copy's completion evidence.

## Authority and retained bytes

The original receipt ID, peer, source hash, destination binding, temporary native identity, checkpoint chain, collision policy and publication fields remain intact. Review and confirmation bind the exact row, incoming-store epoch, receiving grant and native file version. Each pass rehashes the entire acknowledged prefix using the original chain, independently of the receiver's cached prefix check. Held native handles prevent replacement and concurrent writers during the check. Long reads run outside the owner/store locks and recheck context between bounded ranges. Confirmation makes the normal receiver recheck its prefix on explicit resume too.

Approval changes invalidate pending reviews even if the original grant is restored. Owner changes, disconnect, cancellation, history rotation, changed metadata/files and active verification reject earlier context. Current Write and Create permission, exclusions, grant expiry, resource quotas and Windows attachment checks still apply. Reviews are bounded to 128 memory-only offers and one byte check per incoming store. No new protocol operation or peer capability is added; remote requests cannot prepare or confirm owner reviews. Scoped batch uploads retain their parent authority and cannot use this ordinary-copy workflow.

Unknown partials, stopped copies, incomplete ownership and uncertain publication keep their existing recovery decisions. A recorded publication can receive a reviewed window only when its retained bytes can be identified and checked; subsequent finalization still reconciles that same native file. Legacy metadata without the new timestamp keeps the original deadline. Unsupported stored timestamps block the queue and preserve bytes.

## Verification

The final Release solution builds in **9.70s with zero warnings/errors**. All **58 focused checks** pass in **3s**, including **34 new cases** across storage recovery, authenticated native channels and the actual offline controller. All **757 selected main Windows tests** pass in **3m 14s**, with the same three capacity exclusions. Eight recorded production/test DLL hashes match before/after.

The encrypted native case expires the receiver's clock after the first acknowledged range, observes denial on the next write, confirms a local owner review without any peer request, then explicitly resumes the same receipt. It verifies one preparation request, one final file, matching bytes and an intact original. The offline controller journey checks cancellation, disconnect invalidation and confirmation while disconnected, with no transfer started. Storage cases cover exact cancellation preservation, restart, single-use/tampered offers, both clock cutoffs and rollback, native part/folder replacement, same-version byte tampering, restored grants, grant expiry, another peer, owner reset, interrupted checking, history rotation, recorded publication, empty/missing checkpoints, malformed authority and legacy metadata. Renewed completion evidence and its quota reservation survive a subsequent incoming preparation.

The Android application was unchanged and its previous verification remains the [phase-69 baseline](phase-69-observed-receiving-estimates.md). Windows GUI automation remains stopped after the user's Escape. No standalone coordinator or alternate launcher is used. Physical step 44 is [prepared for the user](physical-network-validation.md), not run; injected-clock tests do not establish natural-expiry field results.

## Remaining work

Complete paired-controller and visual/accessibility journeys, natural-expiry field evidence, real clock/long-running/lifecycle/power/crash/upgrade/provider/volume and full-capacity/performance matrices remain open. This does not complete broader source-slot/history cleanup, estimates for other directions/stages, hardened operations, signing/update/uninstall or independent security review; see the [completion audit](completion.md).
