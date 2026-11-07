# Windows sender-store recovery

Windows now offers a separate owner review for a blocked batch sender store. The action preserves the complete flat store under a private sibling archive and opens an empty queue. It never interprets damaged metadata as a replacement intent and never contacts a receiver. Retained ready private snapshots are conservatively marked for additional-copy review before the old queue is preserved.

## Review and preservation

The review captures every top-level file's native version, the store identity, the current local owner generation, file/byte counts and the count of unrecognized names. The confirmation is single-use and expires after two minutes on a monotonic clock. Confirmation rechecks the inventory and identity. It requires batch workers to be idle; the controller pauses those workers before review.

A separate profile-level lease remains held while the old store's handles are closed and its directory is renamed through the native parent handle. The whole directory is preserved by one same-volume rename. A fresh sender directory is then opened under the still-held profile anchor. The normal store lease is acquired again, and old in-memory intents, results and reviews are discarded. A restart between preservation and fresh directory creation opens a new empty queue and retains the archive. It does not reactivate archived intents.

The operation requires the current healthy local identity. Revoking a peer does not prevent local recovery. Changes to the local owner generation invalidate an existing review. Resetting the sender store preserves source snapshots, originals, received files and receiver-side plans; it loses the sender's usable resume and stop controls for the preserved intents. The native dialog states this before confirmation.

## Archive removal

At most eight preserved stores are admitted. Each archive has a separate review and removal action. The review shows its path, remaining file count, total bytes and unrecognized-file count. Unrecognized regular files are preserved by reset and are included explicitly in a subsequent archive-removal review. Nested folders, redirects, hard links, excessive entry counts and excessive store size are refused for local inspection.

Removal deletes at most 64 reviewed files per call, comparing their native versions before deletion. It never adds newly appearing files to an existing removal authorization. New or changed entries remain for fresh review. The active queue, private snapshots and receiver files are outside the removal scope. Native directory handles are closed before refreshing the list after deletion.

Removal progress is retained for the current process only. Leaving the Transfers page stops its loop after the current bounded call. Continuing in the same process uses the already reviewed file list. After an app restart, the remaining archive stays visible and requires a fresh owner review. Changing the owner generation ends the old authorization and also requires fresh review. This action deliberately does not resume deletion automatically.

## Verification and remaining work

The focused native storage tests use real Windows snapshots and received files. They cover preservation, stale/forged reviews, peer revocation, changed owner generation, archive admission limits, bounded removal, partial removal followed by restart, changed/new entries, aliases and nested folders, live worker refusal and preservation of a new current job.

The Release solution build passed with no warnings or errors. The selected main regression passed **419 cases in 2 minutes 37 seconds**, excluding the same three separate 4,096-item capacity cases as phase 42. Evidence: `.tools/phase43-windows-final-build.log` and `.tools/phase43-windows-main.log`.

A final **four-case matrix passed in 1 second** after extending the preservation test to cover a missing parent alongside its orphaned item receipts. It covers both damaged and missing parents with actual received-byte preservation, the equivalent filesystem state of interruption after preservation but before fresh queue creation, and the real desktop controller's offline owner workflow. This final test-only extension adds one distinct case to the 419-case regression; it does not change the production binaries. Evidence: `.tools/phase43-windows-final-matrix-build.log` and `.tools/phase43-windows-final-matrix.log`.

The first focused run passed five of nine cases and found that the archive directory handle was still open when the code refreshed the list after marking that directory for deletion. Four removal cases failed with access denied. Closing the handle before refresh corrected that behavior. The expanded focused run then passed all **12 cases in 1 minute 2 seconds**, followed by the broader validation above. Evidence: `.tools/phase43-windows-recovery.log` and `.tools/phase43-windows-recovery2.log`.

Windows UI automation remains stopped following the user's physical Escape; compilation and controller/domain evidence do not establish a visual or accessibility pass. Android code is unchanged by this phase and was not rerun. Its last verified build remains the phase-42 **255-case device / 39-case JVM** result.

The corresponding Android implementation is now documented in [Android sender-store recovery](phase-44-android-sender-store-recovery.md). Recovery of damaged private source stores, owner cleanup of unidentified receiver parts, coordinated profile rollback protection, full-capacity recovery performance, actual process/power-loss fault coverage, cross-platform batch verification and release gates remain in the [completion audit](completion.md). No batch capability is advertised by this phase. The [physical-network test](physical-network-validation.md) remains prepared for execution after implementation.
