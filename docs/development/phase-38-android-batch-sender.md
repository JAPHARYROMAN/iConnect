# Android durable batch sender

The later [outgoing history work](phase-39-outgoing-batch-history.md) adds separate archived slots, owner reviews, same-intent restore and resumable metadata cleanup. The limits and evidence below describe the preceding sender milestone.

`PhoneBatchSender` sends retained Android private batch snapshots through the scoped receiver protocol. `PhoneController` exposes queue, send, pause, stop and item-result operations, collects saved jobs into application state and reports damaged sender metadata in transfer recovery. Private-inventory admission runs on an IO dispatcher, keeping its bounded filesystem validation away from the UI thread. This engine does not yet establish the complete native batch workflow; the batch capability remains unadvertised.

## Authority and durable progress

Each job binds the local owner identity, intended peer, pairing epoch, source ID, canonical manifest digest, item count, encoded length and byte total. Preparation does not create a remote sharing approval. Sending requires the current authenticated session and current pairing, and checks foreground availability before and after each RPC. Disconnecting, revoking the peer or leaving the foreground pauses batch sending. Ordinary single-transfer background approval cannot authorize a batch.

The exact receiver header is saved before the first admission request. Reconnecting queries that original header and namespace; a missing receipt becomes review rather than a new admission. Canonical manifest chunks resume from validated receiver boundaries. An item-attempt marker is saved before directory creation or file preparation. Lost replies and restarts query the original item receipt instead of repeating an uncertain first effect.

Every successful scoped response must retain the original parent header. Child receipts also bind the local sending identity, item index and kind; file receipts retain the original name, length, digest and collision choice. Once observed, the receiver's effects-started barrier cannot regress. Final item results are saved before advancing the local cursor. Completion requires every local final result and matching receiver terminal outcomes.

Files use held private readers with whole-file digest verification and the shared outgoing payload-worker lease. Original document URIs are never reopened for sending. Ranges are bounded and acknowledgments must advance exactly; empty files, empty folders, source errors and suppressed branches remain represented. Readers and worker leases close on pause, failure and shutdown.

Stop is durably recorded before contacting the receiver. Interrupted stops keep their original barrier and can be explicitly continued after restart. Published or uncertain receiver copies, source originals and private source snapshots are preserved. An unstarted local job can stop without creating a receiver plan.

## Private metadata

Sender metadata lives in the app-private `outgoing-batch-jobs` directory under a held native ownership lock. Directory identity and parent-record versions are checked. Cold loading validates bounded parent and result records, canonical names, ownership, contiguous committed results and matching saved terminal outcomes. Unexpected, missing or damaged records block new sends and preserve copies for review.

Limits are eight retained jobs, four per peer and two simultaneous control tasks. Parent records are limited to 64 KiB, item results to 8 KiB each and aggregate result metadata to 16 MiB per job. Payload byte reservations and worker slots remain shared with ordinary private outgoing files through the existing resource owner. Retained-history retirement and separation from active admission slots remain unfinished.

## Verification

The debug sender and controller compile. Instrumentation uses real private snapshots, the production receiving service and actual document-provider publication. Its separate sender identity exists only in the test APK; both sides approve real signed pairing receipts through the production policy checks.

The focused run passes **16 device tests in 34.763 seconds**. The final build adds a receiving-label case, and all **17 sender tests pass within the final 221-case device regression**. The initial run passed fourteen of fifteen tests; its remaining test changed a snapshot before queue creation and was rejected by the existing admission integrity check. Correcting that setup to mutate the already-queued snapshot exercises the intended send-time check. Altered retained terminal outcomes are also rejected on cold start.

Debug and unsigned release APKs build successfully. All **39 JVM tests** pass; lint reports **zero errors and 67 advisories**. The final broader device regression passes **221 tests in 246.019 seconds**, excluding four live-invitation classes and four previously separate receiver/source capacity cases. The earlier 220-case run passed in 150.483 seconds before the display-label follow-up. Receiving-root labels are retained as display text, rather than treated as filenames; their IDs and grants still authorize destination effects.

For the 221-case regression, the installed debug APK SHA-256 is `ee222b52abcbdc70645cf8479521d698a46dcb10f27b7a79e1a0a490bff971ba`; the test APK is `0de608cb99e2943e5aeafaf876b091c72bc55d96b31c4cfde372f6ca1650e094`. Both installed hashes were checked against the local build before and after that run. Earlier focused/220-case evidence used app hash `608b7f68e44fc2b818f9e37cc1991a0c5f0fb032911b1ed070feefea5f89ddf9` and test hash `59e31b59e597698b7122d4ea4e220ff5ecc4ec7a48025e303994fcfa9f7567f9`.

A subsequent controller-only follow-up dispatches queue admission to IO. Its lock-wait regression holds the private inventory gate while checking that the Main callback returns, then releases the gate and verifies a ready queued job without a connection or sharing grant. **All 18 targeted device tests pass in 67.682 seconds** on that build: the 17 sender cases and this controller case. Debug/unsigned release packaging, all 39 JVM tests and lint with zero errors/67 advisories also pass. The 221-case broader run above precedes this dispatch-only change.

The follow-up app hash is `bd01bb62588f61b47e9fb68ac91de5f9d1dd7be5ccb63065a765e15e4c94bb0d`; the test APK is `e93f1ca3f6eea82f2a7afc58e34281098e3e854daa439afe3d36d055d52026ae`. Installed hashes were checked after the targeted run. The owned test emulator was then shut down.

The focused cases cover nested/empty/zero-byte publication, original changes after preparation, lost admission/directory/range/completion replies, both-host restart, missing original receipts, substituted parent headers and file digests, foreground withdrawal, pairing revocation, pause/resume, interrupted Stop, missing local results, changed private snapshots and maximum valid Unicode result text.

Complete native preparation/queue/destination/result/recovery controls, source/history removal, owner decisions for uncertain copies, active/history slot separation, large sender-capacity and power-loss matrices, sustained-load performance, native accessibility, fresh encrypted cross-platform sessions and operator-run physical-network evidence remain open. Existing receiver-capacity results are not sender-capacity evidence.
