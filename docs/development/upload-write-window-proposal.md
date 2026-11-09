# Proposed bounded upload write windows

**Design proposal; not implemented, enabled or benchmarked.** The current upload path durably flushes a 32 KiB private range and its receipt before acknowledging it. That preserves restart and lost-reply safety, but generates many durable writes for a large file. The local Windows over-4-GiB upload fixture takes 18m 9s during default-adoption validation; the frozen Android upload fixture takes 7,636.975 seconds. These different fixtures and host conditions are not a comparative benchmark.

The download engines already checkpoint at 1 MiB. Increasing their checkpoint interval is not the proposed upload optimization. The protocol's 64 KiB frame bound and 32 KiB range bound should remain unchanged.

## Negotiation and acknowledgements

Explore an optional `UploadInfo.write_window` with supported values zero/one for the existing behavior and four for the extension. A new sender requests four during preparation. An older receiver's absent/zero response means one. A new receiver must retain the single-range behavior for an older sender. Persist the chosen behavior with the original receipt, and verify ordinary and batch-child interoperability before enabling it.

For a four-range window, retain up to four contiguous ranges in a bounded buffer. Replies must distinguish the durable `offset` from an optional volatile `buffered_offset`; buffered bytes must never be reported as durable progress or successful completion. Once a window fills, or the file's final range arrives, validate authority again, write the contiguous group, flush its data and persist the resulting receipt before acknowledging the new durable offset. Existing whole-file verification and publication decisions remain required.

Keep the broker's bounded dispatch semantics explicit. Immediate, clearly volatile replies can permit a sequential receiver to collect a window; awaiting a durable first reply while waiting for later requests would deadlock that receiver. Sender pipelining and any out-of-order request handling require separate bounded scheduling tests.

## Authority and recovery

- Bind pending bytes to the authenticated peer, original receipt, receiving owner, approval generation and live session scope. A different session must obtain the durable checkpoint, discard the previous volatile window and resume the original receipt.
- Bound both bytes and active windows globally and per peer. Refuse excess work before allocation. Use an absolute buffer lifetime, and release reservations on cancellation, stop, foreground withdrawal, session close and shutdown.
- Check current write authority immediately before committing a group under the existing mutation serialization. A pending window creates no permission to publish or continue after withdrawal.
- Retain the existing interrupted-write recovery. A crash after a data flush but before receipt persistence must never produce an acknowledged checkpoint. Any uncommitted tail can be discarded only through the existing identity-checked private-part recovery.
- Lost volatile or durable replies must reconcile against the original receipt. They must not create an additional receiving copy. Explicit pause/resume needs an authenticated way to drop pending bytes and obtain a durable-only checkpoint.

## Required implementation evidence

Test legacy peers in both directions, new peers, zero-byte files, final partial windows, every lost-reply position, duplicate/different ranges, gaps, reordered replies, invalid negotiation values and arithmetic at the signed and unsigned 32-bit boundaries. Inject failures around data writes, flushes and receipt replacement. Cover owner revocation, expired approvals, two simultaneous uploads, resource exhaustion, cancellation and actual process restart without affecting unrelated work.

Measure actual durable flush counts and matched end-to-end runs, including the original large-file fixtures and native Windows–Android ordinary/batch engines. Any speedup claim requires those measurements. Keep the current frozen APKs, native artifacts and ongoing validation intact while developing the extension; their results do not validate this proposal.
