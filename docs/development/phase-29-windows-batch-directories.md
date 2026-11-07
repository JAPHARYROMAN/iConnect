# Windows scoped batch-directory effects

Windows now creates real directories from an accepted batch manifest and retains identity-bound receipts for each directory. This is progress toward outgoing folder transfers. Android currently has the matching child-receipt contract and validation, but its directory-effect host and both platforms' scoped file uploads remain unfinished. The full `upload-batches-v1` capability remains unadvertised.

## Scoped operations

`CREATE_BATCH_DIRECTORY` and `UPLOAD_BATCH_ITEM_STATUS` require an explicit item index, the retained parent batch ID and its current history epoch. The item must be a usable directory in the accepted manifest. The receiver derives its child ID from the peer, complete parent intent, index and kind. Client helpers validate the same binding; a missing index does not silently select root item zero.

An item can create descendants only through successful retained parent-directory receipts. The host opens the approved native root and destination, holds all ancestors, then walks the recorded created directories by name and native identity. All handles stay held through the operation. Moved/replaced folders, changed exclusions or revoked Write/Create authority deny the operation. No matching existing folder can be adopted as a batch parent.

Creation journals the selected name and parent identity before calling the native no-replace primitive. Success retains the returned native directory identity before acknowledgement. Replay verifies that identity and returns the same receipt. An unfinished intent becomes **Needs review** and cannot create another directory automatically. An acknowledgement or metadata-commit failure therefore cannot trigger another copy by filename search.

Keep-both selects another name, skip records a skipped item, and fail records the conflict. Existing folders are never merged. Descendants of an unconfirmed/skipped parent cannot be created. Receive-only grants always choose a fresh suffixed name; listing stays denied and status exposes no directory activity metadata.

## Durable attempt ownership

Per-item receipts alone are insufficient: losing a leaf receipt could otherwise make a completed operation look new. Each batch therefore has a separate bounded attempt index. The index is created before the parent's effects-started marker, and an item marker is durably committed after its pending receipt and before filesystem creation.

The index binds the batch and manifest hash and includes a SHA-256 checksum of its identity and bitmap. Missing indexes, corrupt bits, missing receipts for marked items and successful receipts without a matching marker fail closed. This detects accidental metadata damage; it is not a defense against an attacker who controls the owning OS account and can rewrite all private state. Complete same-key rollback remains a separate application gate.

The parent receipt records that effects may have started. Remote cancellation no longer releases an active batch's reservations blindly. Deliberate local metadata archiving preserves destination folders, archives the journals and rotates the namespace; old requests stay stale. This review action still requires coordination with file-child workers before those workers are enabled.

## Resource bounds

There are at most 4,096 child receipts per accepted plan. Each receipt is limited to 32 KiB and directory-child metadata to 4 MiB per batch, plus its small attempt index. The existing eight-plan / four-per-peer limits and shared incoming-byte reservations remain in force. Startup recognizes bounded child filenames and checks their logical parent identities and ownership markers. Orphaned/missing ownership metadata blocks incoming writes for review.

Only two accepted manifests are cached. A native metadata-file version check invalidates a changed cached plan; scope, header and destination bindings are checked before reuse. This avoids rereading and decoding a 4 MiB manifest for every child operation without treating the cache as authority.

## Verification

The Windows tests create **4,096 actual directories**, reopen the receipt store and verify the final directory through its retained receipt. They also cover nested empty structure, all collision modes, receive-only naming/status, same-name substitution, pending/lost-result review, blocked result commits, missing parent/leaf receipts, missing attempt indexes and corrupt attempt bits.

The .NET regression passed **313 tests (305 main and 8 preview)**. A subsequent receipt-binding test brought the focused `UploadBatchRulesTests` run to **33 passing tests**; that addition was not represented as another full regression. The final solution build reported zero warnings and errors. Android's protocol compatibility checks passed **36 JVM tests**, debug/instrumentation/release APK builds and lint. Android directory implementation starts in the next increment.

These results do not establish file-byte transfer, Android directory creation, full native sending queues or fresh Windows–Android interoperability. Computer Use remains stopped after the user's Escape input; the earlier Windows fixture window is left untouched.

## Remaining work

Implement the equivalent Android provider-backed directory effects, then scoped file-child staging/publication, aggregate scheduling, private outgoing preparation and complete native plan/queue/recovery journeys. Preserve the existing Windows origin/attachment checks and Android uncertain-publication rules. Add actual file-batch capacity, lifecycle/crash/provider and physical-network evidence before enabling the feature or claiming the full application complete. The [completion audit](completion.md) retains that scope.
