# Phase 13: durable file changes

**Status:** implemented in the development apps; release gates in [the completion audit](completion.md) remain open.

## User workflow

Both connected explorers offer **New folder** where the owner granted creation permission. The Android explorer offers **Rename selected…** and **Move selected…** for approved Windows items. Move captures one source, clears its selection so the user can browse to a destination, and requires confirmation. Windows performs an atomic same-volume, no-replace rename/move. An existing destination is preserved. Roots, self-descendant destinations, stale source versions and unauthorized locations are refused.

The desktop explorer exposes rename/move controls too, with permission checks before execution. Android SAF storage currently advertises creation only: portable provider rename/move has insufficient no-replace guarantees. Local explorers remain read-only. Copying remains the verified receiving workflow; this phase does not add remote-destination uploads or deletion.

**Review recent changes** loads up to the newest 32 receipts for the connected peer. Completed, kept-existing and needs-review outcomes have readable labels; history can be collapsed. A confirmed response remains visible even if refreshing history fails. Disconnect clears the client view. Reconnect and request recent changes from the source device to review its durable record. Clients do not automatically resend uncertain changes.

## Saved intent and replay boundary

The optional `mutations-v1` capability covers creation, rename, move, operation context, individual status and paged receipt listing. Older peers can still browse through the existing protocol; clients refuse mutation RPCs when this capability is absent.

Before a filesystem/provider effect, the host commits an intent to its private `file-operations/journal.json`. An operation carries a random identifier containing its creation time, a host-generated journal context, and a fingerprint of the request. The identifier is accepted for 24 hours, allowing five minutes of forward clock skew. The fingerprint excludes transport request ID and journal fields. A known identifier with the same peer and payload returns its recorded outcome without invoking the effect again, including after a service restart with expired opaque node references. Reusing it for another payload conflicts. A changed context rejects old requests after owner-approved journal recovery.

Outcomes are saved separately from the initial intent. A Windows pending rename/move can reconcile success by matching the original file identity at the bound destination parent. An uncertain creation cannot establish ownership of a preexisting folder: it becomes **Needs review**. Android never repeats an uncertain provider call. A saved pending intent becomes review, including when the provider created a folder and lost its reply. An absent or expired receipt does not prove that no change happened; inspect the original and destination before making a new operation.

This is a durable replay barrier, not a transaction across private metadata and arbitrary storage providers. There is no automatic rollback or exactly-once guarantee after every possible disk or power failure. A completed receipt describes the recorded operation; an owner can subsequently change the resulting item.

## Authority and bounds

- Execution and receipt lookup check the authenticated peer and current approval. Revoked, expired, disabled or narrowed permissions cannot authorize a replay or reveal its receipt. Windows binds roots and destination parents to file identities; Android binds receipts to the approval generation and exact tree grant. Local-browser shortcuts confer no mutation authority.
- Windows retains handle-relative no-replace operations and refuses redirected or hard-linked objects. Folder mutation preflight checks protected/excluded descendants with a 4,096-visit, 32-level bound. This scan is not an atomic snapshot of a concurrently edited tree.
- Android checks provider creation support, scans at most 4,096 sibling IDs with a 1 MiB metadata bound, and refuses an existing requested name before calling the provider. It revalidates the approved parent immediately before creation. A returned existing ID, non-folder, excluded name or different parent is not adopted as successful creation. Uncertain provider results are kept for review. Provider correctness and concurrent edits remain compatibility/security-review work.
- Journals are capped at 8 MiB, 512 receipts overall and 128 per peer. New changes prune entries older than the identifier lifetime; a full journal refuses writes. Receipt pages contain at most 32 rows. The apps show the first page; broader history/navigation remains work.
- Damaged/unreadable mutation metadata blocks changes through that journal while normal browsing remains available. Owner-confirmed progress archival preserves metadata and starts a fresh context. It does not delete resulting files/folders or reconstruct unknown operations. Generic inaccessible-profile and OS storage-registry repair remain open.

## Verification

The Release solution builds with zero warnings/errors; all 99 .NET checks pass. Android debug/test APKs and four JVM checks pass, with zero lint errors and 39 advisories (one new URI/KTX style suggestion). All 75 emulator storage, policy, transfer, recovery and explorer/UI checks pass. These counts describe the local development run, not release certification.

Windows tests cover repeated creation after service restart, payload mismatch, peer scoping and revocation, identity reconciliation of rename/move, uncertain creation, replaced destination identity, failed intent persistence, corrupt metadata/context reset, expired requests and protected descendants. Rewriting a pending receipt in a fault-state fixture simulates an interrupted acknowledgment; it is not a process-kill/power-loss test.

Android tests cover repeated creation across service instances, payload mismatch, peer isolation/revocation, a provider that creates then throws, pending-state restart, failed intent persistence, corrupt-journal recovery, and wrong-type/wrong-parent provider responses. They use actual framework SAF grants and a disposable DocumentsProvider. The failed-write test uses a nonempty staged-path blocker because Android's AtomicFile can remove an empty directory while reading committed state.

Actual Windows–Android interoperability verifies Android-controller create/rename/move against Windows and previews moved content. Windows creates a phone folder, repeats its operation ID, and verifies one provider item plus its receipt. Existing scoped explorer checks, two-way acknowledged test transfers, a verified 2,097,169-byte Windows receive/SAF save, a six-item phone folder and a real phone-file download pass.

The test harness must dispatch its remaining commands promptly after `SENT`, within the phone test's 35-second wait. One delayed manual run timed out at this checkpoint; synchronized dispatch passed against the final builds.

A disposable WinUI fixture verifies native create/rename dialogs, destination browsing, explicit move confirmation and three completed receipts. The moved folder exists at the destination and is absent from its original location. This fixture uses Windows storage on both ends; separate emulator interoperability exercises Android storage.

Complete crash-point/full-disk tests, physical Android providers/devices, full touch/keyboard/accessibility journeys, independent security review and signed release packaging remain required. The completion audit tracks remaining full-app work.
