# Android trust persistence and revocation recovery

Implemented and verified 3 November 2026 (Africa/Nairobi). This extends the [background sending work](phase-6-android-sending.md). The [completion audit](completion.md) remains the full application checklist; this phase does not establish release readiness.

## One authority record

Android saves trusted peer keys, the selected computer and remote folder approvals in `owner-policy.json`, using bounded private atomic writes, explicit file sync and read-back verification. SharedPreferences no longer authorizes a peer. Policy loading validates its schema, identifiers, canonical P-256 keys, limits and local device identity. Corrupt policy denies remote access and requires explicit repair. A changed local identity invalidates existing in-process policy instances, removes trusted peers and makes earlier folder selections inactive.

Each mutually signed pairing receives a fresh random generation. Folder approvals contain that generation as well as the peer identifier. Re-pairing the same public key cannot reuse old folder permissions or source tickets. Policy instances for the same application directory share a snapshot and synchronization gate, so one instance cannot overwrite another instance's changes from a stale cache.

Folder-provider validation happens outside the authority gate. Immediately before saving the new grant, Android checks that the exact captured pairing is still current. A slow provider query cannot prevent revocation or save approval for a replacement pairing. Permission updates also require the current generation; inactive selections cannot be enabled through the permission switch. OS URI permissions remain independent local access leases and do not themselves authorize any remote device.

## Revocation, cancellation and repair

Forgetting a computer first blocks its exact generation in memory and closes coordination and the active direct session. Accepted revocation work then saves that generation in the separate `trust-revocations.json` intent journal before removing it from the main policy. If main-policy cleanup fails, the durable intent still denies the saved generation when policy is loaded again. Successful cleanup removes obsolete intents. Cleanup for an older generation cannot remove a newer pairing or its fresh approvals.

Canceling pairing blocks the proposed generation too. This covers cancellation racing an in-progress pairing save: a delayed save cannot publish usable trust afterward. Committing a pairing independently checks the host/guest identities, expiry and both actual approval signatures. Current sessions capture their pairing generation, so their authorization does not silently become that of a later pairing with the same public key.

The controller serializes accepted trust writes and drains them during deliberate shutdown. Connection and new-pairing controls remain unavailable while changes are being saved or revocation needs retry. If the intent journal cannot be written, the UI states that access is blocked only in the current run and asks the owner to free storage and retry before closing the app. If intent is durable but cleanup failed, the UI distinguishes that condition. It does not claim a failed write will survive process death.

**Reset pairing data…** has a confirmation explaining that it removes trusted computers and remote folder approvals while preserving phone files, local downloads and the selected local destination. Earlier prototype policies are not bound to a local identity/generation, so migration retains their folder selections as disabled records and requires fresh pairing and folder approval. Reset and migration never turn these records back into live remote access.

## Verification

Debug app/test APK assembly, four JVM rules tests and Android lint pass. Lint reports zero errors and 30 advisories. No Windows production or wire-protocol change was needed.

The final combined Android instrumentation suite passed **43 tests in 11.681 seconds**. Eight new tests cover actual mutual signatures and cold policy loading; canceled or failed pairing commits; a durable revocation intent with injected policy-write failure; failed intent persistence followed by retry; provider-query/revocation/re-pair concurrency with the same public key; changed local identity; corrupt-policy repair preserving local files; and malformed saved permission types. Invalid booleans, expiry values, fractional permission masks and null generations deny authority instead of using permissive defaults. Existing storage, source-ticket, receiving, URI-ownership, backup, Keystore and activity checks also pass. Fault injection uses a nonempty staging directory so `AtomicFile` cannot silently remove the injected failure during recovery. Test identities issue real signatures; no production auto-approval path was added.

All three interoperability fixtures passed again using the actual Android controller, native Windows transport and disposable provider data:

| Fixture | Result |
| --- | --- |
| Foreground two-way browsing and real-file receiving | `OK (1 test)`, 32.444 seconds; Windows-to-phone 2,097,169 bytes and phone-to-Windows document both verified. |
| Background phone sending, denial of unrelated operations, notification pause and same-ticket resume | `OK (1 test)`, 77.517 seconds; Windows verified both completed copies. |
| Background receiving and provider publication, unrelated browsing denial and notification pause | `OK (1 test)`, 35.158 seconds; exact SAF-saved bytes verified. |

The deterministic 2,097,169-byte fixture digest remains `2ce3172ab5811f22d8e2d9d45d2c71397e38a636b4a5fd5a09517b7454fb2368`. The phone document digest remains `e26a25141c8dfacf5e9276534024b8c48c81195c9dd5b7227b8fd662ad5ce9c9`. The interop fixtures now wait for durable old-peer revocation before requesting the next connection. Reproduction follows [the interop instructions](verification.md#reproduce-the-interoperability-check) and the sending/receiving phase documents.

Cold-load tests copy committed authority and intent files into a fresh isolated policy directory, avoiding the in-process snapshot cache. They establish the tested persistence states, not arbitrary hardware power-loss behavior. Physical devices, OS restore/force-stop/process-death matrices, full recovery UI/accessibility coverage, provider/disk fault combinations, independent security review and release signing remain outstanding. Folder/batch operations and future management features must preserve these generation boundaries when implemented.
