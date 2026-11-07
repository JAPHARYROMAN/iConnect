# Android offline file copies

Android's local explorer can copy a selected file to an owner-selected folder without a coordinator, pairing or remote storage approval. The source remains intact. This adds single-file offline copying; local folder batches, Windows offline copying, provider-safe move/delete/rename and the broader release gates remain in the completion audit.

Offline operation depends on the selected providers. Android's Storage Access Framework can expose both local and cloud-backed storage, so the review and queue explain that a chosen provider may use the internet; they do not promise that every destination stays physically on the phone. The review also explains retained source access, and shortcut removal distinguishes retained copies from the shortcut itself. See [Android's provider model](https://developer.android.com/guide/topics/providers/document-provider).

## Access and verification

The browser captures a private source reference only from its current local catalog and local-only host. No new RPC exposes this reference. A job retains an independent read lease before Android's system destination picker opens. Removing a browsing shortcut therefore removes browsing access while preserving the source access already selected for that copy. Canceling an unused draft or removing a finished record releases only its own lease.

Local jobs use a separate queue and the empty local owner. The ordinary receiving queue still requires a device fingerprint. Local copy activity has a distinct fixed kind and an empty peer field. No pairing receipt, remote grant or synthetic peer identity is created. Local sources are never attached to the network file service.

The source adapter reuses the native file host's ancestry, metadata/version, known-size and bounded-range checks. It computes a whole-file SHA-256 before copying. The receiver verifies private checkpoints and the whole staged file, then separately creates and reads back the provider copy. Existing destination files are kept. Owners choose keep-both, skip or stop-on-collision; substituted existing provider IDs are rejected before opening for writes.

## Foreground and recovery behavior

Source selection is durable before the picker starts. Canceling the picker leaves a draft that can be resumed or canceled. Choosing a destination from a draft requires a fresh visible review of collision handling; an earlier canceled chooser is not treated as a durable collision-policy choice after restart. Picker results arriving before the activity returns to the foreground are deferred. No source bytes are copied in the background. Temporary picker leases are reconciled after the receiving queue has retained its own recorded access.

Jobs pause when the activity leaves the foreground and remain stopped after process recreation. Explicit resume checks the source again and retains the pinned checksum for a saved checkpoint. A verified private copy can be published without rereading the original. Interrupted provider publication remains a separate review state: verify the recorded provider document, or explicitly approve another copy after inspecting the destination. Cancellation does not remove provider files.

Receiver job registration shares the same gate as local pause/cancel. Automatic dispatch also checks the originating coroutine's cancellation state inside that gate. This closes the interval where an earlier worker could otherwise register a new save after the owner's pause pass, including after a quick foreground return.

Damaged progress blocks new work until the owner preserves the exact metadata record. Preserved metadata has a separate removal confirmation. Neither action removes originals, provider copies or unidentified private payloads. If a receiving record is lost while its private part remains, choosing a destination for the retained draft refuses to adopt or truncate that part. Canceling the draft also preserves it. Full orphan-payload inspection/cleanup and maximum-history recovery performance remain separate gates. Source admission is bounded to eight retained sources and 16 GiB of declared source sizes; this is not a claim that all app-wide storage quotas have been verified.

## Validation

Final debug, instrumentation and unsigned release builds pass in **3 minutes 16 seconds**. All **39 JVM tests** pass with zero failures/errors; lint reports **zero errors and 67 existing warnings**. Gradle was stopped before native execution. The **15 focused local-copy cases pass in 42.876 seconds**, and the complete selected Android regression passes **363 cases in 1,439.822 seconds**. The same eight exclusion entries as phase 56 remain: four interoperability classes and four separately recorded full-capacity methods. No new exclusion was added.

Coverage includes real offline provider bytes without pairing or remote grants; independent source leases after shortcut removal; draft restart; changed/outside-tree sources; keep-both/skip/fail collisions; substituted existing provider IDs; lost create receipts; an actually blocked provider write during foreground withdrawal; canceled-worker refusal after foreground restoration followed by a fresh successful owner save; early picker results; damaged metadata preservation; unidentified-part refusal; bounded admission and failed journal commits. The real Compose/system-picker journey also checks the provider explanation, cancels the picker, changes collision handling in a draft review, keeps that draft without queueing, explicitly reviews another destination, verifies the saved bytes and removes only the record.

Installed and built APK hashes match before and after the final regression:

- Application SHA-256: `7aecd5f449a38e5077265b8b4d6af72903efab71bf9b91bb9f44a5b048333519`.
- Instrumentation SHA-256: `37aa06f078abbd4c29c83ab46f6ea630a7994311bc2ef0a58c461f0710acd1f7`.

The owned headless AVD `iconnect-proof` was stopped after verification; its helper exited and serial disappeared. Windows GUI automation remains stopped. These results do not establish fresh Windows–Android interoperability or physical-device/provider compatibility.

The first focused run passed 11 of 13 cases in 49.953 seconds. It exposed an observable-state lag immediately after a joined save and a screen-test lookup before Compose displayed its search field. Save completion now synchronizes the observable state; the screen test waits for the actual field and for shortcut cleanup. Subsequent review added the unidentified-part guard, explicit provider/privacy wording, fresh draft-destination review and atomic dispatch/withdrawal checks.

The first selected regression passed 357 of 362 cases in 1,354.012 seconds. Five 70-item retirement/recovery cases hit the shared sender fixture's one-minute send deadline. An unchanged-binary isolated repetition also timed out, in 77.124 seconds overall; read-only inspection showed its cursor advancing from item 21 to item 58 of 70. The functional fixture now budgets two seconds per item, with a one-minute minimum and three-minute maximum, giving a 70-item send 140 seconds. It logs only counts, fixed phases, elapsed time and the limit. All checksum, publication, restart, deletion-review and provider-byte assertions remain. The five cases then pass in 396.843 seconds on unchanged production bytes, with actual sends of **61.854–64.696 seconds**. This calibrates a functional test deadline; provider latency, sustained load and capacity performance remain open release gates.

An intermediate regression was intentionally stopped after 170 completed cases, with no recorded assertion failure, to apply the reviewed dispatch/withdrawal correction. That runner reports `Process crashed` because only the owned test application was force-stopped. It is not a passing regression or evidence of an application crash. The final 363-case run above includes the correction and the added stale-worker case. That case uses the private worker boundary against an actual staged provider copy; no production injection API was added.

Local evidence:

- Final build/JVM/lint: `.tools/phase57-android-dispatch-build.log`, generated JVM/lint reports and `.tools/phase57-gradle-dispatch-stop.log`.
- Final native cases and artifact identity: `.tools/phase57-android-dispatch-focused.log`, `.tools/phase57-android-dispatch-regression.log`, `.tools/phase57-apk-dispatch-hashes-{before,after}.txt` and `.tools/phase57-emulator-shutdown.log`.
- Initial failures and measured fixture correction: `.tools/phase57-android-focused.log`, `.tools/phase57-android-regression.log`, `.tools/phase57-large-fixture-original-isolated.log`, `.tools/phase57-large-fixture-measured.log` and `.tools/phase57-large-fixture-metrics.log`.
- Earlier picker refinement and deliberately interrupted regression: `.tools/phase57-android-ui-final-focused.log` and `.tools/phase57-android-final-regression.log`.

The operator procedure is prepared as step 31 of the [physical-network kit](physical-network-validation.md), as requested for later execution. It has not been run on a physical phone. Remaining product and release requirements stay in the [completion audit](completion.md); the full-app goal remains active.
