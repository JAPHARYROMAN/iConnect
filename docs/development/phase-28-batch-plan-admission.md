# Durable incoming batch-plan admission

Windows and Android now admit and retain an outgoing batch's validated plan through the production file-service boundary. Admission stores private metadata and reserves incoming-copy quota. It creates **no destination files or folders**. Full outgoing folder/batch sending remains incomplete in the [completion audit](completion.md).

The protocol adds context, begin, manifest-write, seal, status and cancel operations. The future `upload-batches-v1` capability is deliberately not advertised. Both native clients refuse these calls before sending when the capability is absent; their authenticated native-peer tests cover this refusal. These receiver components are ready for child-transfer integration, not an enabled sending queue.

## Receipt and recovery behavior

Begin durably records the original header, peer, pairing/owner scope and exact approved destination before acknowledging the intent. A duplicate ID must describe the same header and destination. New IDs must pass ordinary freshness checks; an already retained intent can recover after that freshness window. Missing, changed or old-namespace receipts cannot silently start another copy. A scoped child ID cannot be minted as a fresh parent operation.

Manifest assembly uses the [bounded canonical contract](phase-27-outgoing-batch-contracts.md). Exact repeated chunks are idempotent. Assembly offsets are volatile: closing the owning file-service session or restarting a manager discards the buffer and status reports zero for an unfinished plan. Resend the original manifest under the retained header. Seal verifies length, SHA-256, item count, aggregate bytes, hierarchy and current name exclusions, then durably commits the accepted canonical bytes. Only afterward can it return `UPLOAD_PLAN_READY`. Client receipt helpers bind every response to the original header and history epoch.

Accepted manifests survive restart and are validated while loading. Incomplete or failed metadata commits do not become completed data transfers. Windows tests hold a metadata reader that denies replacement, interrupt seal, reopen the store and recover the original intent through its private staged file. Writable staging uses the native single-link/reparse checks. Android uses the existing synced atomic private-file writer and read-back checks. Damaged context, manifest or reservation records block incoming writes until local review; their contents are preserved.

## Authority boundary

Every plan operation checks current Write/Create permission and the original destination. Windows reopens the approved native root and parent and compares recorded identities. Android checks the same SAF tree, parent document IDs/names, current pairing approval and framework read/write permission. Moving/replacing a parent, revoking a grant, changing permissions or excluding a planned name denies admission/recovery. Plans do not acquire a new OS storage permission or restore a revoked grant.

Windows binds its receipt to the local owner generation and the pairing generation captured when `ProofClient` creates the authenticated file-service session. A later pairing cannot reuse the old receipt even if a matching grant record still exists. Android checks the current owner policy and grant pairing generation. Local browser shortcuts cannot admit remote batches. Receive-only destinations keep their listing and read restrictions; the admission response contains the sender's own header and progress, not native paths, provider URIs or directory contents.

## Shared capacity

There are at most eight retained plans globally and four per peer. Each private JSON receipt is bounded to 8 MiB; each embedded canonical manifest is bounded to 4 MiB and 4,096 items. Volatile assemblies share those same plan limits. Ready manifests are read and validated on load; the runtime catalog retains small receipt metadata rather than all manifest bodies.

Declared batch bytes share the existing 16 GiB per-peer / 64 GiB global incoming-copy reservation budget with standalone uploads. Both admission directions check the combined reservations, so separate queues cannot each take the full allowance. These are logical quotas, not a guarantee of free disk space. Android also checks private metadata headroom; actual storage errors fail closed on either platform. The 8 TiB manifest protocol bound does not override local quota approval.

Canceling an unstarted plan releases its byte reservation and keeps a canceled receipt. It cannot restart with the same ID. Retained receipts continue to occupy bounded slots until the owner archives the plan history. There is no automatic plan-history expiry or name-based adoption.

## Local review controls

Both transfer screens show retained plan counts and reserved quota, or an explicit metadata-review state. **Archive / reset batch-plan history…** opens a review dialog. Acceptance pauses connections, moves the private plan history into a bounded local backup, releases its reservations and creates a fresh namespace. Earlier requests remain stale. Destination files, source files and ordinary upload history are kept. Interrupted reset remains blocked and can be retried using the preserved archive.

The existing deliberate preserved-metadata cleanup includes these backups. Cleanup accepts only the expected bounded metadata filenames and regular single-link files; unexpected contents are kept for review. A review dialog can be canceled without modifying the receipts. This metadata-only action will need to coordinate with child-transfer work before that work is enabled.

## Verification

On November 3, 2026, the current-source checks passed:

- Windows/.NET Release: zero build warnings/errors and **300 tests passed** (292 main, 8 isolated-preview). Eighteen new admission cases and two new contract cases cover the new behavior. The final main suite took 56 seconds.
- Android debug, instrumentation and unsigned release builds: successful; **35 JVM tests passed**. Lint reported zero errors and 54 advisories, including two new suggestions about URI KTX style and conservative usable-space checks.
- Owned Android emulator: **127 device tests passed** in 149.303 seconds, including nine new admission cases and a native recovery-dialog cancellation case. The installed app matched APK SHA-256 `0c13d1f64d028dd7ec350ef5dff4e894c7748ce88afa4c9719f0144a3b987b32` before this run. The emulator was stopped afterward.
- Self-contained Windows publish: compiled XAML, resource index, attachment/preview workers and their runtimes were checked. Published component digests matched the current build. The native archive review dialog opened and was canceled; every fixture plan-metadata digest stayed unchanged. A subsequent final-build visual check was stopped by the user's physical Escape input, so further Windows UI interaction was left stopped.

Checks cover a real 4,096-item accepted manifest, restart and lost-session recovery, shared quotas, per-peer/global slot limits, changed headers/destinations, expiry, pairing/grant revocation, excluded names, foreground withdrawal, corrupt history and failed commits. These are plan-admission tests; they do not establish 4,096 actual file publications. The new data-transfer capability remains unadvertised.

## Remaining work

Implement scoped directory and file child receipts, deterministic replay handling, no-replace collision outcomes, private outgoing folder preparation, data scheduling and complete native sending/recovery queues. Integrate child work with reservations, cancellation and local metadata review before advertising batch support. Actual full-capacity file/folder effects, interrupted provider publication, native accessibility, performance/storage/lifecycle matrices and release gates remain required.

Fresh Windows–Android interoperability and physical-network validation remain separate gates. Automatic approval review previously rejected starting the local coordinator as **blocked by policy**. The operator-run [physical-network kit](physical-network-validation.md) remains prepared for execution after implementation; independent receiver evidence does not replace it. The full application goal remains active.
