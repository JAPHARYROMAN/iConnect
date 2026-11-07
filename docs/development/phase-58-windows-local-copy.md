# Windows local file copying

Windows can copy a selected local file without pairing, a coordinator or a storage-sharing approval. The local explorer captures the reviewed file before the destination picker opens. A changed selection cannot silently replace that reviewed source. Captured source paths are shown in pending jobs so identically named files remain distinguishable.

## Access and data handling

`WindowsLocalCopies` owns a separate queue under `local-copies`, with a native exclusive lease and pinned private directories. Its source references contain the selected root identity, relative components, file identity, version and size. They do not enter `StorageGrantStore` or a peer file service. The existing local browser RPC whitelist remains read-only. Local receipts have an empty local owner; ordinary receiving queues still require device fingerprints and refuse to load local receipts.

Copying reuses the native receiver's bounded ranges, durable checkpoints, prefix verification, whole-file SHA-256, no-replace publication and read-back checks. It opens source components through `SecureWindowsDirectory`, which rejects redirected locations, hard links and changed native identities. Removing a browsing shortcut does not cancel an already selected copy purpose. Originals and existing destination names stay intact; the owner chooses keep-both, skip or stop-on-collision.

New copies deliberately receive untrusted-origin metadata and must pass the real Windows attachment policy plus byte verification before completion. Original source metadata is unchanged. This is a verified data copy, not metadata/alternate-stream or trust inheritance. It requires a destination supporting the receiver's security metadata and native operations. Local synchronization software can upload chosen folders; the review explains this rather than promising device-only storage.

## Lifetime, recovery and controls

Preparing a source creates a durable draft. Canceling the destination picker keeps that draft; choosing a destination later requires another visible collision review. Copies can continue while the app remains open. Closing pauses work; restart never resumes a job automatically. The source is checked again when bytes are still needed. A fully verified private or published copy can finish attachment checks without reopening the original.

The local page provides paged progress, pause/resume, cancellation, recorded-copy rechecks, separate record removal and damaged-metadata preservation/removal. Rechecks cannot fetch bytes or create another copy. Cancellation removes only an identified job-owned partial; uncertain or published files remain available for review. New files remain untrusted after a failed or interrupted check. Missing source access requires a new source selection rather than silently rebinding a job.

The existing unidentified destination-part recovery now includes the local queue's validated receipts and native identities. Paused local parts are protected. Active jobs, pending cleanup or unreadable local ownership block a scan. A consumer cannot silently omit an existing local store. New local receipts also change the protection stamp, invalidating earlier removal reviews. These checks apply during review and removal, not only when displaying the folder.

Source admission is bounded to eight retained sources and 16 GiB of declared source sizes. This is not an app-wide quota or performance claim. Local folder batches, complete orphan-payload/history cleanup, metadata fidelity, shared quotas, additional volume/filesystem matrices and storage/power-loss behavior remain in the completion audit.

## Verification

The final Release solution build passes with **zero warnings and zero errors** in **28.55 seconds**. The initial 13 copy cases and expanded 17-case set pass. After recovery integration, all **36 focused local-copy and receiving-part recovery cases pass in 2 seconds**. They use generated files, actual NTFS identities and publication, real pause/restart paths and bounded injected attachment outcomes. A separate case runs the actual Windows attachment worker; injected-checker results are not presented as equivalent evidence.

The new coverage includes independent local/remote authority, shortcut removal, source drift/root replacement, collisions, source-free attachment completion, local-only rechecks, corrupt and changed records, admission limits, exclusive queue ownership, throwing observers, immutable reviewed selections, closing while active, concurrent cancel/close, later hard-link creation and wrong-purpose receipt loading. The cleanup integration creates a real paused 5 MiB transfer, proves its part is excluded from unidentified cleanup, preserves an unrelated candidate, blocks on damaged local metadata and invalidates an older review after a new local receipt.

Local evidence: `.tools/phase58-windows-protection-build.log`, `.tools/phase58-windows-focused.log`, `.tools/phase58-windows-final-focused.log` and `.tools/phase58-windows-protection-focused.log`.

The final selected Windows regression passes **523 cases in 3 minutes 45 seconds**, using the same three separately recorded 4,096-item capacity exclusions as phase 56. No new exclusion was added. Production storage, model, desktop, connectivity and proof DLLs, plus the test DLL, have matching SHA-256 identities before and after the run. Evidence: `.tools/phase58-windows-final-regression.log` and `.tools/phase58-windows-final-hashes-{before,after}.txt`. An earlier 521-case run also passed before the two cleanup-integration cases were added.

WinUI controls compile, but Windows GUI automation remains stopped after the user's Escape. This phase does not claim a new visual, keyboard, screen-reader or picker interaction run. The operator procedure is prepared for later execution in the [physical-network kit](physical-network-validation.md). Physical-network and fresh Windows–Android interoperability evidence are separate gates. The [full completion audit](completion.md) remains authoritative; the full-app goal is active.
