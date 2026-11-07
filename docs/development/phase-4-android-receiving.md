# Android receiving and document-provider recovery

Implemented and verified 3 November 2026 (Africa/Nairobi). This milestone extends the existing `downloads-v1` protocol; it does not complete the full application. See the [completion audit](completion.md).

## Receiving behavior

Android now receives actual approved computer files through a requester captured from one authenticated session and pinned peer. RPC calls are dispatched onto the session's Main context; storage and hashing run on IO workers. A later connection cannot retarget a queued download to another device. The queue has two worker slots, at most 128 persisted records, 32 KiB reads, 64-bit offsets and the existing 8 TiB design bound. These are implementation limits, not evidence of large-file testing.

Each job has an atomic private JSON journal and an app-private staging file. An exclusive queue lock prevents two managers from owning the same journal directory. Flushed checkpoints record the offset and SHA-256 chunk hash chain. Resume truncates uncheckpointed trailing data, re-reads and validates the saved prefix, and rebuilds the full-file digest before requesting missing data. Changed source descriptors, mismatched peers, malformed ranges and checksum failures cannot advance a job to saving. A fully received file with a durable expected checksum can finish verification without its source. The staging file is independently re-read before it becomes ready to save.

The Android UI exposes destination selection, per-file download, byte progress, pause/resume/cancel, verified save and terminal-record removal. Select a writable phone folder while disconnected. Receiving into that folder is an owner action and does not create a remote storage grant. The destination for an existing job remains the folder chosen when it was queued. Current availability remains foreground-only: leaving the activity disconnects the session and pauses downloads and publication. Queue shutdown is awaited off Main to avoid deadlocking canceled Main-dispatched RPCs.

## SAF publication boundary

Publication starts only after staging verification. The adapter checks persisted Android read/write access, a create-capable destination directory, and the provider's document ancestry. It requests a new document through `DocumentsContract.createDocument`, checks that the returned ID was not in the bounded pre-create child listing, and checks that the result is directly inside the chosen tree. Existing names receive a numbered alternative. This relies on the installed provider honoring Android's new-document contract and stable document identity; the preflight listing does not establish an atomic no-replace primitive or protect against a malicious provider lying about identities.

Publication intent is journaled before document creation. The returned URI is journaled before writing. Only the newly created document is written. Normal seekable file descriptors are required; pipes and unsupported providers are refused. Cancellation signals and descriptor closure bound supported provider I/O on pause. After close, the saved provider bytes are read back and checked for exact size and SHA-256. Completion is persisted before private staging cleanup. Provider close/read-back success does not establish remote cloud synchronization or universal power-loss durability.

SAF cannot provide a portable atomic create/write/rename transaction. An interrupted save therefore becomes **publication interrupted**, never an automatic success. Recovery only reads the recorded document and can reconcile an exact saved copy offline. It never truncates, rewrites or deletes an uncertain provider copy. If creation happened without a returned/durable URI, the app discloses that a copy may remain. Creating another copy requires the user's explicit decision, preserves earlier copies, and has an eight-attempt bound. Cancel deletes only private staging; provider copies remain for owner inspection. Removing a terminal record leaves saved provider files intact.

## Verification

The complete .NET Release solution build passed with zero warnings/errors, and all 63 .NET tests passed in Debug and Release. Source preparation now issues an independent ticket for each download job on both platforms: releasing one job cannot revoke a simultaneous download of the same file. A new regression test covers this on each platform.

Android debug and test APK assembly, four JVM rules tests and lint passed. Lint still reports existing dependency-update advisories; no lint errors were reported. Twenty-one combined instrumentation tests passed in 2.984 seconds: nine storage/source tests, one Keystore test, one activity smoke test and ten new receiving tests. The receiving tests cover:

- Actual 2 MiB + 17 byte publication, exact bytes and preserving an existing destination name.
- Pause at 1 MiB, manager/journal restart, prefix validation, missing-range resume and another-peer rejection.
- Corrupted staging and changed source descriptors, with no provider publication.
- Corrupted network bytes, rejected before publication.
- Offline completion-receipt reconciliation and preserving a provider file changed by its owner.
- Withdrawn Android destination access without modifying existing files.
- A provider substituting an already-listed file ID, refused before opening it for writing.
- Lost creation receipts: no automatic second copy; an explicitly requested new copy preserves the uncertain earlier file.
- An empty Unicode-named file and offline verification after all bytes/checksum were checkpointed.
- Canceling a queued worker, releasing its active-state record and resuming it after other worker slots drain.

The final authenticated Windows/Android emulator interoperability test passed in **32.871 seconds**. Android received the Windows fixture `jaribio.bin` (**2,097,169 bytes**), saved it through the separate test APK's framework document provider, and compared every byte. Its SHA-256 was `2ce3172ab5811f22d8e2d9d45d2c71397e38a636b4a5fd5a09517b7454fb2368`. Windows also received and verified the actual 42-byte phone fixture with SHA-256 `e26a25141c8dfacf5e9276534024b8c48c81195c9dd5b7227b8fd662ad5ce9c9`. Both synthetic transfer diagnostics also passed. The test fixtures alone issue approvals for disposable test data; production code has no auto-approval mode.

The integration harness waits for its specific job to leave active cleanup before saving, matching the UI's disabled-button behavior. An earlier run exposed a harness race that tried to save during ticket release; that run failed and is superseded by the final result above. Fixture cleanup now awaits cancel/record removal for that run's own jobs.

To repeat, use the existing [interop setup](verification.md#reproduce-the-interoperability-check) with current APKs and runner. Android now reports `REAL_WINDOWS_DOWNLOAD_VERIFIED` before `SENT`. Use `download-phone` on the Windows runner before the existing `send` step. Framework fault injection and disposable provider components reside only in the test APK.

## Remaining work

The full Compose destination-picker/download/control flow still needs visual and accessibility verification; the activity smoke test only proves launch. Physical phones, real local/cloud-provider compatibility, >4 GiB transfers, full-disk/journal faults at every boundary, process termination at every crash point, blocking/misbehaving providers, and independent networks remain unproven. A damaged queue journal currently fails closed and needs a user-facing repair flow. Source-ticket expiry and failed/canceled lease cleanup across reconnects need broader handling. Persisted SAF access ownership also needs consolidation: removing the last sharing approval for a tree currently releases Android's access even if it is also a download destination, so affected jobs must reselect that destination.

Explicit background receiving/publication and independent SAF-access ownership were subsequently added in [phase 5](phase-5-android-lifecycle.md), which also supersedes this phase's sharing/destination permission-release limitation. Source-side background continuation remains outstanding. Folder manifests/batches, complete explorers and file management, metered/bandwidth controls, history/diagnostic export, Windows background hosting, signing, installers and release/security gates remain tracked separately. These results establish the described local emulator behavior only.

The publication design follows Android's [DocumentsContract API](https://developer.android.com/reference/android/provider/DocumentsContract) and [DocumentsProvider contract](https://developer.android.com/reference/android/provider/DocumentsProvider). Future background work must follow the platform's [data-transfer guidance](https://developer.android.com/develop/background-work/background-tasks/data-transfer-options) and [foreground-service timeouts](https://developer.android.com/develop/background-work/services/fgs/timeout).
