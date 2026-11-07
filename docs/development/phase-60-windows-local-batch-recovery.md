# Recovery of Windows local batch progress

Local file and folder batches now distinguish unused drafts from work that may already have produced copies. Retained file receipts remain visible when the parent batch or its source plan is missing. Recovery never grants fresh source reads or silently creates another copy.

## Decisions before copying

New schema-3 source plans receive an unused-draft record in the native-pinned `copy-decisions` directory. Starting a destination first creates and flushes a `started` record, then removes the unused record, before launching the receiver. A crash at either boundary therefore cannot turn a possibly started source into a new draft. Only an available schema-3 source with its unchanged unused record and no evidence of receiving can choose its first destination.

The decision records are separate from receiving journals. If a parent receipt is lost after completion, the source cannot start again. If both receiving history and all decision records are lost while the source survives, absence of the unused record still requires review. Older schema-1/2 sources without a parent are conservatively treated as possibly copied, including genuinely unused older drafts. Existing valid parent batches can still resume through their original controls.

Decision inventory is bounded and checked through native handles. Changed, unexpected or redirected entries deny new work. Marker contents cannot grant source access. Source trees remain immutable and require the existing native identity/version checks. Observed parent/file receipts consume any unused record and retain a possible-copy decision. Orphan contexts count toward admission; losing receiving history cannot free all queue slots while source references remain.

This is not rollback-proof storage. Restoring coordinated older source/unused records while losing later decisions, hostile same-user interference, actual process/power-loss durability and supported upgrade/downgrade matrices remain separate completion requirements.

## Unattached file receipts

A receipt is unattached when no available parent batch item references its ID under the same batch ID. The native page displays bounded receipt pages, destination context, received bytes, publication name and errors. These receipts have three limited controls:

| Control | Effect |
| --- | --- |
| Stop and clean up | Stops the receipt and attempts cleanup only of its identified private part. Published or uncertain files remain. |
| Recheck recorded copy | Repeats byte/security checks against only the recorded final native identity. It cannot fetch source bytes, resume a source transfer or create another copy. |
| Remove receipt | Removes finished or safely stopped progress only. Original and published files remain. |

Controls validate current parent/file ownership journals before work. Attached receipts must use their batch controls. Concurrent rechecks share their task; incompatible actions require pausing and waiting. Receipt work prevents conflicting batch control and unidentified-part cleanup. Closing cancels verification and waits for workers/cleanup before releasing native store ownership. The batch receiving namespace rejects single-file receipts even when they are valid local-copy receipts.

Current receipts continue protecting private parts after parent loss. Stopping/removing a receipt changes receiving protection and invalidates earlier unidentified-part removal reviews. Damaged source preservation and archive removal cannot hide surviving file receipts or clear the possible-copy decision.

## Missing parent review

Missing receiving history is labeled as possible prior copies rather than an unused draft. A context cannot be removed until all its retained file receipts have been reviewed and removed. The final review is single-use, expires after five minutes, binds current ownership and native metadata, and cannot survive restart. Changed confirmations, source/decision changes or new receiving receipts require another review.

Confirmation removes only source/progress references and decision records. It neither discovers nor deletes completed files, created folders or unidentified partials. Users must inspect earlier destinations before making a fresh selection for another copy. A source plan cannot reuse this review as permission to start again. Normal terminal parent removal also retires its decision metadata.

The UI pages source contexts and unattached receipts separately. Local activity uses fixed recovery labels/outcomes without filenames, paths, receipt IDs or peer fingerprints. Windows GUI execution remains stopped after Escape; the new controls are compiled and core/native behavior is tested, while visual, picker, keyboard and screen-reader checks remain pending.

## Verification

The Release solution build passes with **zero warnings/errors in 12.63 seconds**. All **61 focused local-batch and receiving-part cases pass in 16 seconds**. New native cases exercise real paused parts, lost source/parent records, missing decision files, legacy plans, published-copy rechecks without the source, changed ownership, single-use/stale reviews, damaged-source preservation, queue admission, shutdown during verification, privacy of activity and receipt-purpose separation. A receiving-part journey confirms that an actual unattached part stays protected and that stopping it ends an older removal review.

The final selected Windows regression passes **all 565 cases in 3 minutes 51 seconds**, using the same three previously recorded 4,096-item capacity exclusions; no new exclusion was added. Storage, model, desktop, connectivity, proof and test DLL SHA-256 identities match before/after the run. Evidence: `.tools/phase60-verification-build.log`, `.tools/phase60-recovery-focused.log` (58 initial passing cases), `.tools/phase60-final-focused.log` (61 final passing cases), `.tools/phase60-final-regression.log` and `.tools/phase60-final-hashes-{before,after}.txt`. The earlier phase-59 intermittent receiver-history rename denial remains recorded in its [verification note](phase-59-windows-local-batches.md); it did not recur in this run, and native rename checks remain unchanged.

The physical test kit adds step 34 and remains **prepared, not run**. Android source and installed artifacts are unchanged. Android local folder batches, remaining explorer/management work, whole-store/orphan payload/history recovery, shared quota/performance, additional filesystems/volumes, lifecycle/accessibility, encrypted/physical workflow, independent security and signed release gates remain in the [completion audit](completion.md).
