# Created-folder receipts and reconnect recovery

Either native client can select a successfully created remote folder as an incoming-copy destination after reconnecting. The receiving device resolves its durable creation receipt to the same recorded folder and returns a fresh opaque reference. This is a prerequisite for outgoing folder transfers; the complete outgoing folder/batch queue remains unfinished in the [completion audit](completion.md).

## Behavior

In remote file-operation history, **Use this created folder for sending** selects that folder in the sending destination list. The action checks the current receiving approval, Write/Create permissions, session and receipt before updating the selection. It does not send a file by itself. The existing private single-file sender can subsequently send into the selected child folder. Nested creation and empty folders remain supported.

A repeated successful creation request now returns the recorded folder reference as well as its mutation receipt. Reconnect recovery instead uses the new optional `created-folders-v1` capability and `RESOLVE_CREATED_DIRECTORY` operation. Both native peers advertise the capability only when their actual file host supports it. A peer without that capability is rejected locally before a recovery RPC. Older basic creation and receipt-status operations remain available independently.

The client helper binds the operation ID, mutation-history epoch, approved grant and originally requested name. It validates the response phase/status, resulting name, exact single directory entry, parent reference and Create permission. Recovery does not fetch a fresh history namespace after an epoch mismatch. A future batch sender must persist this intent before issuing creation, including when the creation acknowledgement is lost.

## Identity and permission boundary

Windows receipts save the native identity of the newly created directory with the successful result. Resolution reopens the approved root, recorded parent and exact child through the existing handle-relative containment boundary, then compares their recorded identities. Replacing the child or parent, moving the original away, or reusing its filename cannot recover the replacement.

Android receipts save the created provider document ID, exact parent ancestor IDs, ancestor names and current grant/pairing bindings. Resolution verifies the current tree, provider ancestry, parent names and result name before issuing a reference. Creation rejects a returned pre-existing ID, an unexpected item/parent or a nonempty returned folder. Parent identity/depth limits are checked before calling the provider. Receipt ancestry is bounded to 64 identities and 64 KiB of identity text; creating a child beyond the supported depth is refused before the effect.

These checks depend on the provider honoring its document identity and ancestry contract. Android requires newly allocated document IDs that remain unchanged and allows providers to alter a requested display name. iConnect records and checks that returned name. This does not promise atomic no-replace rename or transactional provider creation. See the [DocumentsProvider creation contract](https://developer.android.com/reference/android/provider/DocumentsProvider#createDocument(java.lang.String,java.lang.String,java.lang.String)).

Both receivers recheck current peer, grant, Create permission, exclusions, history epoch and stored identity. Revocation, a replaced parent, a moved folder or a changed approval denies recovery. Native filesystem identities and provider URIs/IDs remain local; the remote response contains an opaque reference and permitted display metadata.

Pending, uncertain, legacy or damaged receipts cannot be upgraded by searching for a matching name. An uncertain provider call may already have created a folder; it is kept for owner review, and replay cannot create another folder automatically. Retained successful receipts without the new identity fields remain readable as history but cannot resolve a folder. Existing bounded mutation receipt retention and quotas still apply; this is not permanent directory bookmarking.

## Receive-only destinations

Creation without List permission always chooses a fresh suffixed name, including the first creation. This prevents the requested name from being used as an existence probe. A directory in a receive-only grant reports no modification time and a stable identity-derived version, so added owner content does not expose directory activity through recovered references. Directory listing, ordinary metadata reads and file reads remain denied. Recovery discloses only the requesting peer's own successful creation receipt, subject to current authorization.

## Verification

On November 3, 2026, the current-source regression checks passed:

- Windows/.NET Release build: zero warnings/errors; **250 tests passed** (242 main and 8 isolated-preview tests). The 14 new cases include nine forged-descriptor variants. Focused storage/native RPC coverage passed all 42 tests before the full run.
- Android debug, instrumentation and unsigned release builds: successful; **23 JVM tests passed**. Lint reported zero errors and 52 existing advisories.
- Owned Android emulator: **112 device tests passed** in 129.24 seconds, including four new created-folder cases. The installed app APK was verified against build SHA-256 `6e6bea59d96b3a8dc45c49ba299da7cb80ceeed78e31bb8b1474835c03a6de8a` before this run.
- Self-contained Windows desktop publish: successful, with compiled XAML, resource index, attachment helper and isolated preview helper/runtime assets checked against the CI requirements. Git whitespace validation passed.

Checks exercise cold service recovery, actual nested bytes, empty child directories, denied replacement/revocation/exclusion, foreign and legacy receipts, uncertain provider effects, inbox metadata and malformed responses. Android additionally verifies that the depth limit refuses creation before the provider adds a folder. Native authenticated same-platform peers exercise capability negotiation and actual transfer into the recovered directory; this is separate from Windows–Android interoperability. The new native history action builds, but its complete connected visual/accessibility journey remains a gate.

## Remaining work

Outgoing folder/batch sending still needs private immutable folder snapshots, bounded manifests and aggregate size accounting, scoped batch receipts and reservations, deterministic child operations, queue/recovery UI, and full-capacity tests. Existing standalone receipt limits (128 mutations and 32 incoming uploads per peer) cannot establish support for a 4,096-item outgoing batch. Download-side batch support remains separate. Complete these requirements before claiming the full sending workflow.

The operator-run [physical-network kit](physical-network-validation.md) includes this recovery flow for execution after implementation. Fresh Windows–Android interoperability, physical provider/device coverage and native accessibility of the new history action remain required. Automatic approval review previously rejected starting the local coordinator as **blocked by policy**; independent-platform tests do not close that cross-platform gate.
