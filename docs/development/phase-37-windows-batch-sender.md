# Windows durable batch sender

The later [outgoing history work](phase-39-outgoing-batch-history.md) extends this engine with separate archived slots, owner reviews, same-intent restore and resumable metadata cleanup. The evidence below records the sender milestone before that follow-up.

Windows now has a sender engine for the existing private batch snapshots and scoped receiver contract. The desktop controller owns it, pauses it on disconnect and drains it before closing private source readers. Public batch-sending UI and `upload-batches-v1` advertisement remain gated; Android sender parity and complete recovery/history controls are still required.

## Durable intent and item attempts

The queue stores at most eight local jobs, four per peer, bound to the prepared source ID, owner generation, pairing generation, canonical manifest hash, item count, encoded length and byte count. Private snapshots continue to hold the shared outgoing reservation. The sender does not create another payload copy or grant remote access to original folders.

Before its first admission request, it saves the exact remote header and selected destination label. Reconnecting checks that original receiver intent by ID and history namespace. A missing or changed receipt becomes review; it never silently creates another batch. Volatile manifest assembly resumes only at a validated range boundary using the original canonical bytes.

Before the first directory or file request, it saves an item-attempt marker. After cancellation or a lost reply, the sender checks that item's original receipt instead of repeating a potentially uncertain first effect. Every successful response keeps the original parent header, and every child receipt binds the local sender identity, original item index and kind. File descriptors must also retain their original name, length, digest and collision choice. A durable observed-effect flag cannot regress in later replies.

Files come from a retained verified private reader. The reader holds the shared outgoing worker lease through complete pre-send hashing, transmission and receiver verification. Originals are never reopened. Ranges are bounded, acknowledgments must advance exactly as expected, and a restarted sender can use the receiver's flushed checkpoint. Empty files and directories remain represented. Source errors and descendants of skipped/failed branches become explicit local outcomes without destination effects.

Each final item result is committed before the queue cursor advances. Results have an 8 KiB record limit and 16 MiB aggregate limit per job; Unicode-aware JSON encoding avoids unnecessary expansion of valid international names and messages. Parent records have a 64 KiB bound. Cold startup validates parent names, item indexes, result ownership and committed outcomes; missing or unexpected metadata blocks sending and preserves snapshots and receiver copies. Returned job arrays are copied so callers cannot mutate saved headers or outcomes.

Batch completion requires all local final item results and the receiver's matching terminal outcomes. A lost final reply recovers through parent status. Stop is persisted locally before the remote request, retries only the same receiver plan and survives cancellation/restart. Completed and uncertain receiver copies stay intact. Pairing withdrawal prevents further requests under the old authority. A local pause releases its private reader and requires an explicit resume.

## Integration and remaining work

`DesktopController` exposes queue, send, pause, stop and item-result operations under the current authenticated peer/session. It includes sender metadata issues in transfer recovery. Constructor failure closes the newly opened outgoing stores, preventing a failed controller initialization from retaining their native ownership locks.

The queue is not a complete native workflow. Source/queue removal and preserved-history repair, individual uncertainty decisions, active-slot/history separation, automatic waiting for shared payload capacity, final native queue/chooser controls on both platforms, accessibility, large-file/power-loss/provider matrices and fresh encrypted cross-platform/physical-network evidence remain open. Earlier platform tests and this engine's local service tests do not close those gates.

## Verification

The first nine sender tests pass against real Windows private snapshots and the real receiver service with an injected accepting attachment checker. They cover actual nested/empty/zero-byte publication, original changes after preparation, lost range/admission/directory replies, both-host restart, a missing original receipt, changed snapshots, substituted file digests, pairing withdrawal and pause/resume.

The selected run passes **14 tests in six seconds**, adding lost parent-completion replies, changed parent headers on otherwise valid child replies, interrupted/restarted Stop, caller mutation of returned state, missing local result recovery and maximum valid Unicode result names/messages. The solution builds with zero warnings and errors. The broader main regression passes **369 tests in 38 seconds**, with **eight isolated preview tests in eight seconds**. The earlier main run passed 368 cases before the Unicode-bound refinement.

The separate sender capacity case passes in **23 minutes 16 seconds**. It sends one directory and **4,095 actual three-byte files**, completes all 4,096 item outcomes, releases receiver byte reservations and reopens both sender and receiver state before reading the last retained result and published bytes. Originals and the private source remain intact. This uses the real Windows receiver/filesystem with an injected accepting attachment checker; it does not establish full-capacity native malware-checker performance. The duration also leaves sustained-load/latency work open.

Follow-up retained-history checks reject altered terminal outcomes and noncontiguous local outcomes. A separate regression accepts receiving-root display labels without interpreting them as filenames. The follow-up solution build passes with zero warnings/errors, and **371 main tests pass in one minute one second**, including all sixteen non-capacity sender cases. **Eight isolated preview tests pass in nine seconds**. The capacity result above precedes that validation-only follow-up. Android sender evidence is recorded separately in [phase 38](phase-38-android-batch-sender.md). No fresh encrypted peer, native Windows UI or physical-network result is claimed for this engine.

CI now isolates the sender-capacity case alongside the existing receiver and private-source capacity cases. The main filter excludes all three named large cases. No remote CI execution is claimed.
