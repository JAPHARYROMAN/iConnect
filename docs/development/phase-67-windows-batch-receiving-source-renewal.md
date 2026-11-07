# Windows receiving-batch source renewal

An idle, unfinished remote receiving batch can now review a fresh source for one retained child. Select one current source file in the phone explorer, return to the batch's item results, and choose **Review selected fresh source for …**. The full name/size/metadata preparation checks and SHA-256 comparison from standalone receiving apply. A missing original checksum is allowed only at an empty checkpoint, with an explicit explanation in the dialog. The review remains single use, connection bound, cancellation aware and limited to five monotonic minutes.

Approval belongs to the original batch. It keeps the frozen source plan, relative names, completed folder identities, destination, child ID, private-part identity, checkpoint and collision policy. It neither receives bytes nor creates destination documents or folders. Resume the original batch separately. Local batches, missing/corrupt parents, directory items, replaced destinations, unrelated children and ordinary standalone APIs cannot acquire this authority. A standalone confirmation cannot consume a batch review, even when handed its child review object.

## Durable parent approval

A bounded receipt on the original batch item records the child, original plan version, previous and reviewed versions, verified full hash, destination identity and existing private-part identity. It does not rewrite the frozen manifest. The parent and receiving manager share one owner lock for validation and adoption; no arbitrary batch-ID string grants approval.

The receiving manager rechecks the source's complete ready descriptor, expiry, current connection and unchanged child. Only then does an internal parent callback save the receipt, followed by the child descriptor. A failure before the parent write leaves the receiving record unchanged. A failure between those journals leaves a receipt accepting either the retained or reviewed descriptor with the bound hash/destination/child. Another reviewed renewal can recover a previous approved version after restart. No automatic resume or silent fallback occurs.

When a zero checkpoint has no private part yet, the receipt explicitly permits its first native allocation under that same job/destination. The normal receiver records and checks the new part's native identity and prefix on subsequent resumes. An already recorded private-part identity remains fixed. A corrupt prefix still fails before publication.

The manager allows one preparation/review per batch and shares the receiving manager's overall limit of eight source operations/reviews. Source work blocks concurrent batch resume. Parent cancellation invalidates pending reviews, cancels preparation and waits for cleanup. Shutdown cancels and drains preparation/confirmation before closing the owner. Unused tickets receive the existing two-second best-effort release; loss of connectivity or process death can retain remote leases until normal expiry.

Windows also now saves a shorter deadline reported during preparation into the failure checkpoint. A later preparation failure or status reply cannot restore a longer approval. Known expiry stops further byte ranges; it does not extend source authority. Batch cards show the same configured-cap payload lower bounds and expiry warnings as standalone cards, not a measured ETA.

## Verification

Release solution build: **zero warnings/errors**, 12.24s. All **100 focused/related cases pass** in 27s, including **22 new cases** (21 receiving-batch cases and one preparing-deadline case), standalone compatibility and existing batch/local-copy behavior. All **703 selected Windows cases pass** in 3m 28s with the same three pre-existing capacity exclusions; no new exclusions were added. Eight recorded production/test DLL hashes match before/after the final regression run. All owned build/test commands are terminal.

Tests use generated NTFS data and real attachment verification. They cover 25-hour expiry through injected clocks, changed metadata with identical full content, frozen plans and native folder/part/prefix identity after restart, collisions, tail-byte mismatch, standalone bypass refusal, cancelled/expired/disconnected reviews, parent cancellation during preparation, shutdown during confirmation, missing/corrupt parent receipts, local-batch refusal, replaced folders, checksum-free empty checkpoints, corrupt prefixes and failures in either journal write. One case uses two authenticated native Windows WebRTC peers with distinct identities, explicitly observes denial of the old expired ticket and performs a parent-owned reviewed renewal before completing one verified copy. No system clock is changed and no standalone coordinator is started.

No Windows GUI automation was resumed. The dialog is built and wired, but its visual, picker, keyboard and screen-reader behavior remains unverified. Android code and its owned AVD are unchanged in this stage; Android receiving-batch renewal remains outstanding. Physical step 41 is prepared for the user's later execution and the existing batch field-validation gate remains in force.

Local evidence is recorded in `.tools/phase67-final-build.log`, `.tools/phase67-focused-final.log`, `.tools/phase67-final-regression.log` and `.tools/phase67-final-hashes-{before,after}.txt`. These logs are not committed release artifacts. The technical white paper and README reflect the implemented scope; the public design publication is unchanged. No commit, push or deployment was created.

## Remaining work

This is one receiving workflow within the full [completion audit](completion.md). Android batch-owned renewal, measured estimates, upload-operation expiry recovery, broad source-slot/history cleanup, full-capacity/performance and long-running/provider/volume/power/crash/clock-skew/upgrade matrices remain open. Fresh Windows-to-Android and physical-network evidence, full accessibility, hardened operations, signed packaging/update/uninstall and independent security review are still required. Public deployment and release completion are not claimed.
