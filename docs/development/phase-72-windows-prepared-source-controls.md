# Windows prepared-source inventory and cleanup

Windows now exposes the retained approvals used when paired devices download shared files. In Transfers, **Prepared reads from this computer** shows paged source names, receiving-device IDs, sizes, preparation progress, state and expiry. Owners can stop one approval while disconnected, refresh the view or remove expired approvals. Original files, receiving checkpoints and published copies are kept. Folder grants remain unchanged and can allow another request; wider access still ends through the folder's pause/revocation controls.

## Storage and lifecycle

The existing limits remain eight approvals per peer, 128 overall, a 24-hour deadline and two simultaneous hash workers. Inventory returns bounded display metadata without grant IDs, source paths or hashes. A stop removes the selected ticket durably and waits for its preparation worker to finish. Independent tickets for the same file remain usable. Expiry cleanup removes only expired operational approvals and persists the result; validated expired records are also removed on reopen and before a new preparation is admitted.

A stopped ticket cannot be restarted through status. A receiver with saved bytes retains its checkpoint and can use the existing full-hash fresh-source review before explicit resume. Neither source cleanup nor UI refresh creates a receiving copy, extends permission or changes collision choices. Preparation progress is distinct from bytes received by a peer. The controller refreshes active preparation views on its existing bounded pulse and coalesces source-change dispatches.

## Authorization and failure handling

Preparing, checking, stopping and cleaning source approvals use consistent grant-before-source lock ordering. Before returning a range, Windows rechecks the exact retained ticket and current grant after reading the bytes. A source removed between initial lookup and publication therefore cannot return the old buffer. Local owner methods require current owner authorization; remote release remains peer-bound.

Source events run outside the store/authorization locks, and observer exceptions cannot change storage outcomes. A reentrant owner stop cannot leave a late preparation worker. Failed metadata writes block ticketed reads and preserve the original file and last saved metadata. Reopen treats uncommitted `.new`/`.bak` records as ambiguous even when a primary journal exists, and unsupported native ownership is preserved for local review. Failed stops are reported rather than acknowledged as durable; restore storage access and review current progress before continuing.

## Verification

The final Release solution builds in **8.63s with zero warnings/errors**. All **77 focused checks** pass in **15s**, including **17 new cases**, the actual offline controller and the extended authenticated native transfer journey. All **774 selected main Windows cases** pass in **2m 53s** with the same three capacity exclusions. Eight recorded production/test DLL hashes match before/after. Focused evidence covers independent tickets, slot reuse, preparation draining, durable/idempotent stop, a stale in-flight read, peer-bound release, expiry cleanup/reopen/admission, failed writes, ambiguous metadata, native ownership validation, observer failure/reentrancy and the retained receiver checkpoint completing through a reviewed fresh source.

Android code and its phase-71 verification baseline are unchanged. Windows GUI automation remains stopped after the user's Escape; compiled native controls and model tests do not establish visual, keyboard or screen-reader execution. No standalone coordinator or physical-network run is claimed.

## Remaining work

The [Android all-peer/offline counterpart](phase-73-android-prepared-source-controls.md) records its later implementation. Broader history/slot cleanup, full-capacity/performance and complete lifecycle/clock/power/crash/volume/accessibility/physical matrices remain open. Other-direction/stage/aggregate estimates, packaging, hardened operations and independent security review remain in the [completion audit](completion.md).
