# Fresh checks of completed received batches

Windows receivers implement `VERIFY_PUBLISHED_BATCH` (operation 46), negotiated separately as `published-batch-check-v1`. Windows and Android senders expose **Verify saved batch** for a completed, fully successful send when the original paired receiver advertises this capability. Android receivers do not advertise it yet. Existing peers can continue using their earlier batch operations.

This checks the destination at the time of the request. It leaves original sources, private snapshots, published copies and durable receipts intact. It does not authorize source removal, establish file safety, or complete cross-device folder moves.

## Authority and receipt binding

The request carries its original complete batch header, batch ID, receiver history epoch and a new canonical challenge. It cannot select an individual item, destination, alternate name, upload descriptor, payload or byte range. The receiver requires the original peer and pairing generation, current owner authority, current Write and Create approval, retained detailed history, completed parent phase and a successful outcome for every item. Stopped, partly skipped, source-error, retiring and compacted batches cannot produce a successful check.

Pending and successful responses include the same challenge and completed parent receipt. Both implementations validate the wire response. Senders compare the entire header, history epoch, retained outcomes, challenge and effect barrier before accepting success. An old completion receipt alone is insufficient. A check from another live session or challenge cannot collect the result. Collection consumes the result; polling again starts fresh work after resource release.

## Windows destination check

The worker reads the retained manifest, directory ownership records and each file's scoped publication journal. File records must retain the original native file identity and completed attachment-policy version. Actual published names, including collision and blind-inbox names, determine the paths; manifest names never adopt a replacement destination.

The original destination root and parent identities are checked. Items open in manifest parent order, keeping at most the bounded manifest's 4,096 destination handles plus the root chain. Files must retain their native identities, regular single-link metadata, recorded lengths and SHA-256 values. Files remain open without sharing writes or deletion until collection or expiry. Directory membership must exactly match the batch's actual saved child names and kinds, including empty folders. Unrelated objects in the approved destination root are permitted; additions inside a created batch folder fail the check.

Before returning success, the receiver checks current authority again, validates private context and receipt versions, rechecks access to every saved path and enumerates created folders again. No response returns file contents or local paths. The two existing incoming-verification slots are shared with ordinary and batch file publication. Work and collection share a ten-minute-plus-thirty-second deadline; a finished result has at most thirty seconds for collection. Senders stop polling after ten minutes. Scope closure, permission changes and verification pause cancel the check. History archival and item retirement wait for its worker to release resources.

Stopping the sender's wait cancels local polling. Disconnecting ends the receiver's session scope. If the session stays connected, an in-flight receiver check can remain busy until its existing deadline or collection expiry, or until the receiver pauses verification. Explicit per-challenge remote cancellation remains a follow-up. A second challenge cannot take over that outstanding result.

Private metadata still relies on the established local owner boundary. This is a point-in-time observation, not a cross-device atomic transaction or a promise that destination contents can never change afterward. The Android provider-side checker and a source-recovery transaction remain required before folder moves can be offered.

## Verification checkpoint

The final selected Windows regression passes 905 cases with zero failures or skips, including 23 new receiver, sender and protocol cases and four existing native upload-engine controls. The desktop Release build has zero warnings or errors. Both native folder modes now verify the completed batch in both directions before withdrawing their receive-only inboxes. The pinned source-built transport also passes its nine standard fixtures, eight additional native cases and the directory-junction regression, with the expected loaded-module SHA-256. These are Windows-to-Windows controls; they do not establish Android interoperability.

Receiver cases cover preserved metadata and files, actual collision and blind names, changed bytes, identical-byte file replacement, directory replacement, extra/missing children, hard links, held-file write/delete refusal, collection membership checks, shared slots, session withdrawal, foreign pairing, permission withdrawal, malformed intent, mixed outcomes and retired history. File and directory metadata are inspected again when the result is collected. Sender cases reject old receipts, changed challenges, headers, epochs, outcomes and effect barriers, and exercise cancellation followed by a real receiver check.

A deterministic regression converts an already-open empty received directory into a junction after verification finishes and before collection. The earlier implementation incorrectly returned success. The collection metadata recheck now returns Conflict and preserves both the junction and its target; the test restores only its owned junction afterward. All 27 focused cases pass after this correction. The pinned source-transport run also passes the junction regression alongside its nine standard fixtures and eight additional native cases.

Android debug, instrumentation and unsigned Release APKs compile. All 86 JVM tests pass with zero failures/skips; lint has zero errors and 71 warnings. The new provider-backed instrumentation case covers sender challenge/parent validation and cancellation; negative cases explicitly refuse to count cancellation or timeout as validation evidence. It is prepared for execution after the currently owned device validation sequence; compilation does not prove it ran. Full 4,096-item check capacity, clock/expiry and filesystem matrices, native Windows–Android execution, visual/keyboard/screen-reader journeys, independent security review and release gates remain open.

## Manual journey

1. Pair the devices and approve an incoming Windows folder. Use an account-owned disposable dataset with nested folders, an empty folder, a zero-byte file and a Unicode filename. Complete a batch send from Android or Windows into that Windows receiver.
2. In **Batch queue and saved history**, choose **Verify saved batch** on the completed send. Confirm the reported success, then compare all source and destination hashes. Originals and saved history must remain intact.
3. Add an owner file inside a created batch folder, then check again. The check must refuse and preserve that file. Repeat with changed bytes, an identical-byte replacement, a missing empty folder and withdrawn receiving permission.
4. Stop waiting for an active check, disconnect, and reconnect. No old result may be accepted from the earlier session; a new check must begin under the current pairing and approval.
5. Repeat with a blind Write/Create inbox and a pre-existing root-name collision. Verify actual receipt names, keyboard focus, cancellation, screen-reader status and both native controller lifecycles. Record the exact builds and results.

The user's physical mobile-data/separate-Windows-network test remains a prepared field-validation requirement. None of the local checks substitutes for it.
