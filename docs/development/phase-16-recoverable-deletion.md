# Windows recoverable deletion and private-profile containment

Subsequent work adds an [owner recovery manager](phase-17-owner-recovery-manager.md), including reviewed permanent deletion, location review/search/export and explicit partial recovery. The limits below describe this phase's original implementation.

Windows now supports permission-gated deletion into iConnect recovery and restoration to the original parent folder. Android can request these operations on an updated Windows host. Android SAF deletion remains unavailable. This is one part of the unfinished full application; see the [completion audit](completion.md).

## Owner and device workflows

New approvals remain read-only. In Windows **Shared locations → Edit permissions**, **Move items to iConnect recovery** enables the separate Delete permission. Remote restore also requires Create permission. Neither permission enables permanent deletion or replacement.

Select one non-root item in the remote explorer and choose **Delete selected to recovery…**. The confirmation explains that the item leaves its current folder and remains on the same volume. **Review deleted computer items** on Android, or **Review deleted items on this device** on Windows, retrieves bounded pages. Restore uses the original parent and offers an explicit new name; an existing destination is always kept.

On the hosting Windows computer, **Shared locations → Review deleted items on this computer** works offline and after the requesting peer or its grant is revoked. The owner can restore a confirmed saved copy, including after an explicit owner-policy reset. Original filesystem identities and current private-location restrictions still apply. A replaced or missing original parent is not recreated or followed automatically.

Restored records can be dismissed locally after the last operation's 24-hour replay window. Copies awaiting recovery or review cannot be dismissed through this control. There is no permanent purge, automatic emptying, retention expiry, or OS Recycle Bin integration in this implementation.

## Filesystem and journal boundary

Each delete creates a random reserved `.iconnect-trash-<operation-id>` folder directly under the approved root. An unrelated random slot name holds the item. App browsers and grant selection reject these reserved names. This is an application boundary; ordinary Windows filesystem access and original file ACLs remain in effect. The recovery folder is not an encrypted backup or a defense against software controlling the owning OS account.

The private `deleted-items/catalog.json` records the owner generation, peer and original grant, root/parent/item identities, original name and version, vault identity, operation fingerprint and result. Its intent is flushed and committed before a handle-relative, same-volume, no-replace rename. The source must still match the selected identity and version. Roots, excluded/private trees, redirects, hard links and oversized/deep management scans are refused. Existing readers can prevent deletion safely.

Pending operations are reconciled by object identity at their intended destination. A repeated identifier returns its recorded outcome and never repeats the filesystem effect, including after the item was restored. Unconfirmed outcomes become **Needs review**; the host does not guess that it is safe to repeat an action. Restore records its own intent before moving bytes and retains both copies on a name collision.

The catalog is limited to 8 MiB and 512 items globally, with 128 items per peer/owner generation. Recent operation receipts have global/per-peer limits of 512/128 and a 24-hour replay window. Listings return at most 32 rows per request. Management scans are bounded to 4,096 visited entries and 32 nested levels. Full storage or a failed metadata commit refuses further work. A failed initial commit may leave an empty reserved folder; user content has not moved at that point. Empty vaults can remain after successful restore.

Damaged metadata blocks recovery changes and appears in the Windows progress-recovery review. Archiving it preserves its metadata family and all vault bytes, but removes automatic restore links from the active catalog. Keep that archive for manual recovery: it contains the original root, parent, vault and slot information. Generic transfer-backup cleanup does not remove these recovery archives or saved content. Do not manually remove a nonempty vault without reviewing its contents.

The optional `recovery-v1` capability gates delete/restore/context/status/list RPCs separately from `mutations-v1`. Responses contain opaque IDs and display metadata, with bounded counts and validation on both platforms. They do not return private catalog paths or absolute Windows source paths. Earlier clients retain their existing browsing and transfer behavior. Android does not advertise a recovery host capability.

## Private profiles

Custom Windows profiles are bound to their actual volume and directory identities. Every authorized operation holds their ancestor chain and checks that the selected profile has not been substituted. This also supports internally reserved profile names without allowing remote callers to use reserved names.

The active profile and descendants cannot become shared roots, remote references, download sources or mutation targets, even when a broader ancestor was approved. Existing persisted grants and cached download authorization receive the same check. A directory containing private state cannot be renamed, moved or deleted through the management scan. This protects the current profile; other custom profiles and broader volume/concurrency behavior remain part of the security review.

## Verification and remaining work

Fifteen NTFS recovery tests cover actual bytes, restart and repeated requests, collision/new-name restore, revocation and owner reset, permissions/version/root boundaries, excluded descendants, changed parent/vault identities, active readers, write failures, uncertain intents, lost completion receipts, malformed catalogs and metadata-preserving archive. Five malformed-response cases run in .NET and the Android JVM suite validates equivalent wire metadata. Four profile tests cover ancestor grants, older persisted grants, profile substitution and held ancestors.

The authenticated Windows–Android emulator test reports `RECOVERABLE_DELETE_VERIFIED`: the real Android controller deletes and restores a Windows text fixture, then reads its original content. Existing explorer, real file/folder copies in both directions, creation/rename/move and matching-session failure checks also pass. The complete Android regression run passes 75 device/UI tests; five JVM tests pass and lint reports no errors.

The full Release .NET suite passes 146 tests and the Release solution builds with no warnings/errors. One initial full run hit a transient access denial while moving a generated profile in test setup; the isolated rerun passed. That setup now retries only bounded OS access/sharing failures, and the full rerun passes. The separate test proving that an active authorization holds the profile still requires the move to fail. The broader source of intermittent OS locking remains unproven.

A disposable native Windows fixture (`IConnect.DesktopFixture --deleted-local`) seeds one real saved copy and revokes its source grant. The app displays that copy, the restore disclosure and cancellation. Actual effects are verified by the NTFS tests and authenticated cross-device test. The full native deletion/restore confirmation, keyboard/screen-reader and physical-device journeys still need broader coverage.

Permanent purge and provider-safe Android deletion, recovery-location details/search/export, manual reconciliation UI, quota/low-disk and complete crash/concurrency/volume matrices remain unfinished. A complete same-key rollback of the matching owner records is still undetected without an external monotonic checkpoint. The user's [physical separate-network test](physical-network-validation.md) remains prepared for later execution.
