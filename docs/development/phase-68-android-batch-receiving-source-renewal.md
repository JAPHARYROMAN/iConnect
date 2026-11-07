# Android receiving-batch source renewal

Android receiving batches now offer **Review selected fresh source…** for one idle, unfinished retained file. Select exactly one current computer file in the explorer, then return to that file's original batch. Preparation checks current metadata and a fresh source ticket. Name and size must match, and the complete SHA-256 must match the recorded checksum. A missing original checksum is accepted only at an empty checkpoint, with an explicit explanation in the dialog.

Approval changes source authority only. It retains the frozen manifest, relative destination names, completed provider-folder bindings, original child ID, checkpoint chain, private part, destination and collision choice. It creates no provider copy and never resumes. **Resume the original batch** separately continues its existing receiving/saving plan. Uncertain provider publication stays in its original review workflow. No child becomes a standalone or general background transfer.

## Parent ownership and crash recovery

The batch and receiving manager share an owner lock. A memory-only authority binds a review to the unchanged original parent, item, peer, pairing epoch, current visible connection, provider destination and native private-file identity. Ordinary standalone preparation and confirmation cannot use it. Reviews are single use, limited to five monotonic minutes including sleep, one per batch, within the receiver's overall eight-operation/review bound.

After rechecking the complete ready descriptor and unchanged job, the receiver invokes an internal parent callback. It saves a bounded approval receipt before adopting the child descriptor. The receipt anchors the original plan version, previous/reviewed versions and hashes, destination binding and private-file identity. It never rewrites the manifest. A failure before the parent write keeps the receiving record unchanged. A failure between the two journals preserves either descriptor for deliberate recovery and another review; there is no automatic resume or silent standalone fallback.

The destination binding uses a versioned, length-delimited SHA-256 of the tree, ordered folder IDs/names and collision choice. Private-file identity includes native device/inode values for both the receiving folder and part; symlinks and multiply linked parts are refused. An unallocated checkpoint first binds its receiving folder, then records the part's identity when the original batch allocates it. Subsequent resume still rechecks the saved prefix and complete content. A completed child whose part was already removed can recover the parent outcome from its saved receipt without producing another provider copy.

Hiding, disconnecting, pausing, parent cancellation and shutdown invalidate reviews and cancel source work. Preparation/confirmation drain through their owner lifetimes. Unused tickets use the existing two-second best-effort release. An offline peer or process death may retain a lease until its normal expiry. Provider objects and original files are preserved by these cleanup decisions.

## Verification

Debug, unsigned release and instrumentation builds pass; the final full build completes in **2m 59s**. All **58 JVM checks** pass with no failures/errors, including four new destination-binding/part-identity cases. Lint reports **zero errors and the same 67 existing warnings**. All **47 focused native checks** pass in **39.72s**, including 27 new batch cases and 20 standalone compatibility cases. All **440 selected native cases** pass with the same eight pre-existing exclusions recorded in [phase 64](phase-64-android-transfer-network-controls.md); no new exclusions were added. The run reports 3,759.811s, with a prolonged 2,458s observation delay on its existing live handle. This elapsed duration is not a performance benchmark.

Built and installed debug/test APK identities match before and after focused/full verification:

- Debug application: `26521e1d964cb7e6e8f66eacbf754662e98d4c91c6c9223c7afcae394720c6c8`.
- Instrumentation: `ae264e4cd08fcdfbbe2ba7e595dc2fcf33c96c14326a14d6db17b6b1497674f5`.

The owned headless `iconnect-proof` AVD was shut down afterward; its saved helper exited and `emulator-5556` is absent. All owned build/test commands are terminal. No Windows code or GUI was exercised in this stage; the prior 703 selected Windows result remains separate evidence.

Native cases cover retained progress and frozen plans, changed metadata with matching content, tail-byte mismatch, standalone bypass refusal, cancelled/expired/disconnected reviews, private-part replacement before and after approval, provider-folder changes, failures in either journal write, missing/corrupt parents, zero/empty/unallocated checkpoints, late completed-child recovery, local-batch refusal and uncertain provider publication. The actual dialog cancels once, approves without copying and continues only after explicit original-batch resume.

One case uses two authenticated native Android WebRTC peers and the real provider-backed service. It pauses a generated file, advances injected source/receiver clocks by 25 hours, explicitly observes denial of the old ticket, reviews a fresh source and completes one verified copy through the original batch. This is component-level local Android-to-Android evidence; it does not establish a complete paired-controller UI journey, fresh Windows-to-Android or physical-network interoperability.

The first focused run exposed missing request IDs in the new fake facade: production correctly rejected those responses before a checkpoint. Correcting test correlation metadata produced the passing focused run; no validation was relaxed and the application APK was unchanged by the fixture-only rebuild. Local logs are under `.tools/phase68-*`, outside committed release artifacts.

System clocks are not changed. The physical procedure is prepared for the user's later run in step 42 of [the validation kit](physical-network-validation.md); the existing batch field-validation gate remains in force.

## Remaining work

This is part of the full [completion audit](completion.md). Measured estimates, upload-operation expiry recovery, broad source-slot/history cleanup, full-capacity/performance, long-running/provider/volume/power/crash/clock-skew/upgrade and complete accessibility matrices remain open. Local Android-to-Android evidence does not prove fresh Windows-to-Android or physical-network interoperability. Windows GUI automation remains stopped after Escape, and the blocked standalone coordinator is not bypassed. Hardened operations, signed packaging/update/uninstall and independent security review remain release gates. No commit, push or deployment is implied.
