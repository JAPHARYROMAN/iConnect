# Outgoing batch manifest and child-operation contracts

This increment supplies matching Windows/.NET and Android contracts for outgoing folder batches. It is a component of the complete sending workflow, not an enabled batch-sending feature. No RPC operation or capability is advertised for these contracts yet. The [completion audit](completion.md) still requires durable receiver admission, shared reservations, private folder snapshots, orchestration and queue/recovery UI.

## Manifest and intent

`UploadBatchManifest` contains an ordered tree of items and a keep-both/skip/fail collision choice. An item references an earlier directory by index, rather than supplying an absolute or relative destination path. Each valid file has an exact size and SHA-256. Directories carry no file content. An explicit source-error item has no hash, reserves no content bytes and must produce no destination effect; an unusable directory cannot have children.

The validators allow at most 4,096 items, 128 selected roots, 32 levels, 2,048 UTF-16 component characters along a branch, 4 MiB of encoded manifest and 8 TiB of aggregate declared file bytes. These are protocol bounds, not an assertion that all such batches have storage or quota approval. Live names must pass the existing portable filename checks. Ill-formed UTF-16, forward/self/file parents, ambiguous siblings, inconsistent hashes/content and overflowing totals are rejected. Portable sibling comparison uses Unicode simple uppercase, including supplementary characters; it does not expand `ß` into `SS`. Native destination collision rules still apply and may be stricter.

`UploadBatchHeader` binds an operation ID, receiver history epoch, approved grant, manifest length, item count, declared bytes and SHA-256 of the canonical manifest encoding. Base64url tokens must be canonical; alternate padding bits cannot create another spelling of one receipt identity. Unknown fields and noncanonical manifest encodings are rejected so a future required feature cannot be silently discarded. A future receiver must also pin the peer, current pairing/owner generation and exact local destination identity in its durable receipt before any destination effect.

This protocol's encoder writes each named field explicitly in ascending field-number order, omits default scalars, uses minimal protobuf integers and preserves item order; the nondefault collision field follows the repeated items. Decoding checks that exact form. It does not assume that generated-message serialization is universally canonical: the [protobuf documentation](https://protobuf.dev/programming-guides/serialization-not-canonical/) explicitly cautions against relying on that across builds and library versions. The shared byte vectors protect this narrower format during dependency changes.

## Bounded parsing and assembly

A manifest is assembled from exactly sized 32 KiB chunks, with a shorter final chunk. Identical acknowledged chunks may be repeated. Gaps, changed duplicates, invalid offsets and partial-sized interior chunks are denied. Completion verifies the complete SHA-256, canonical encoding and declared totals. A failed completion or disposal clears the assembly buffer and prevents reuse. The caller's header and source byte arrays cannot alter the captured intent or received buffer.

The byte-length limit alone is insufficient: a small encoding can contain millions of empty protobuf submessages. A precheck therefore walks bounded wire fields before invoking the protobuf parser. It caps repeated items, individual item/string/hash lengths and integer encodings, rejects unknown/repeated/out-of-order scalar fields, and checks all slice bounds without materializing protobuf items or strings. Canonical parsing remains a second validation layer. A .NET allocation-bound negative test and Android runtime negative test exercise a 2 MB encoding containing one million empty items.

Assembly is control-message transport, not durable transfer progress. Its offsets are not durable acknowledgements. An interrupted assembly can be uploaded again from zero under the same intended batch receipt. A successful checksum alone cannot authorize creation or mark a batch complete. Receiver admission and durable namespace retention are the next implementation requirement.

## Child IDs and replay separation

Child IDs derive from a versioned domain, peer identity, batch ID, history epoch, grant, manifest hash, item index and file/directory kind. They are stable only for that complete binding. Their first eight bytes are zero, so the timestamp is outside the freshness window accepted by ordinary new mutation/upload requests. Losing batch context therefore cannot turn the child ID into a fresh standalone create operation.

An ID is not authority. The future batch host must validate the retained parent receipt and exact item before every child action, and must reject ordinary routing of scoped child records. It must retain completed/uncertain child outcomes and identity bindings so a missing acknowledgement cannot cause another copy. The existing standalone receipt quotas are unchanged; they are not being enlarged to simulate batch capacity.

## Compatibility evidence

The shared `upload-batches.json` fixture specifies independently encoded protobuf bytes, a file SHA-256, manifest SHA-256 and a derived child ID. It is consumed by .NET, JVM and Android runtime tests. The fixture and new device-test class are packaged only in test outputs; they are not new application data or production auto-approval paths.

The final explicit-encoding Android build on November 3, 2026 passed debug, instrumentation and unsigned release builds, **33 JVM tests**, and lint with zero errors and 52 existing advisories. The owned emulator passed **117 device tests** in 142.379 seconds, including five new contract cases. Before this run, the installed app was checked against APK SHA-256 `99bea4ef9f4f44610ae018013bb9eb76e5be83ca3731474686864baf91b97292`. The final fixture asset was confirmed present in the test APK and absent from the application APK.

The final Windows/.NET Release build passed with zero warnings/errors. **280 tests passed**: 272 main tests, including 30 new batch-contract cases, and 8 isolated-preview tests. The complete main suite took 43 seconds. This includes the unchanged production attachment checks and the revised integration-test budget described below. Git whitespace checks passed. The owned emulator was stopped after testing; no coordinator, deployment, commit or push was started for this increment.

One concurrent .NET/Gradle run exceeded the existing 30-second deadline of the 45-item Windows attachment integration test. The unchanged isolated case subsequently passed in 25 seconds after compilation finished. This is insufficient evidence to establish the exact contention cause. That integration test now allows two minutes for its forty-plus real OS attachment checks and reports batch/item phases if it times out. Production attachment-service deadlines and process limits are unchanged; broader throughput/load validation remains outstanding.

Existing physical-network and fresh Windows–Android validation gates remain separate. Automatic approval review previously rejected starting the local coordinator as **blocked by policy**; these independent-platform contract checks do not close that interoperability gate.

## Work still required

Implement bounded durable receiver plan receipts, owner/pairing/destination binding and shared byte/slot reservations before advertising batch support. New admission must enforce ordinary parent-ID freshness, while retained receipt recovery must not mint another operation after expiry. Then implement private immutable outgoing folder snapshots, scoped child creation and upload receipts, interruption/reconnect and uncertain-provider recovery, native plan review/queues and actual 4,096-item effects/capacity tests. Keep revocation, receive-only privacy, no-replace publication and Windows received-file security checks effective throughout. The full application goal remains active.
