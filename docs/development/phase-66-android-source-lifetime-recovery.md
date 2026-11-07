# Android standalone source lifetime and recovery

Android receiving records now retain the peer's source expiry. Legacy records without this field keep an unknown deadline. Invalid deadlines enter the existing progress-recovery flow. Ordinary status replies can shorten a known deadline but cannot extend or clear it; a shortened deadline is saved even if preparation subsequently fails. An expired known deadline stops further range requests and keeps verified progress.

The Downloads card displays the configured-limit lower bound for remaining payload and warns when it reaches the reported source expiry. This assumes the file has the whole directional budget: preparation, other transfers, networking, hashing and provider saving can take longer. Unlimited bandwidth provides no invented ETA. Device clocks may differ. No wire format or source lifetime is changed.

## Explicit source review

While the app is visible on the current authenticated connection, select exactly one current computer file in the explorer. An idle, unfinished standalone download offers **Review selected fresh source…**. Preparation checks current metadata and obtains an independent source ticket. Name and size must match. Once preparation completes, the full SHA-256 must match the original full checksum; matching a prefix alone is insufficient. A record without an original checksum can adopt only at an empty checkpoint, and the dialog explains that limitation.

Approval is single use and memory only, bound to the original record and connection. It expires after five monotonic minutes, including device sleep. The manager allows at most eight preparations/reviews and one per job. Hiding, disconnecting, cancelling, removing a job, changed records, stale metadata, reused tickets, altered status descriptors and expiry invalidate the operation. Confirmation rechecks the complete ready descriptor and the current connection before saving.

Approval updates only source node/version/ticket, checksum, deadline and the old error. It retains the job ID, private part, verified prefix/checkpoint chain, SAF destination, parent bindings, collision choice and phase. It never resumes or publishes. **Resume** separately reopens and verifies the saved prefix and complete bytes. **Save verified file** remains a separate provider operation. Already received bytes use the existing local verification path. Local copies, batch children and attempted/uncertain provider publication cannot acquire standalone source authority through this flow.

Unused offers receive best-effort release with a two-second timeout. Preparation and confirmation are tracked in the manager lifetime, cancelled and drained on shutdown. A cancellation at result delivery also removes the unused offer. Offline peers or process death can leave a remote lease until its normal expiry; this is not a guarantee of immediate remote cleanup. Old tickets are released only after durable adoption. No review survives restart or grants general background access; the existing explicit single-file background selection remains separate.

## Verification

Debug, unsigned release and instrumentation APK builds pass; the final combined build completes in 2m 53s. All **54 JVM cases** pass with no failures/errors. Lint reports **zero errors and 67 existing warnings**. All **20 focused native cases** pass in 25.099s. All **413 selected native cases** pass in 1,084.234s (18m 04s), including those 20 new cases, with the same eight pre-existing interoperability/capacity exclusions documented in [phase 64](phase-64-android-transfer-network-controls.md). No new exclusions were added.

Built and installed debug/test APK hashes match before and after both native runs:

- Debug application: `a1bfc7b1ba1f2074a3398ae9316b87cea264b730c39dd0c6dfab392ba8b5937f`.
- Instrumentation: `6a3ec4931a366b3f772a32a80f9e1dc249a4d8621501dee0c0fc310d6147d513`.

The owned headless `iconnect-proof` AVD was shut down afterward; its saved helper exited and `emulator-5556` is absent. All owned build/test commands are terminal. No Windows code was changed or Windows GUI exercised in this stage. The prior 681 selected Windows result remains separate evidence.

The focused cases cover actual private-prefix continuation and restart, changed source metadata with matching complete content, a different final byte despite a matching prefix, checksum-free empty and zero-byte checkpoints, old-deadline extension/clearing/shortening attempts, preparation failure after shortening, stale/single-use/expired reviews, peer and selection mismatches, reused source tickets, bounded reviews, corruption, batch/local ownership, cancellation/removal and shutdown during preparation/confirmation. The real dialog cancels once, approves without receiving, then verifies only after a separate Resume action.

One case uses two authenticated native Android WebRTC peers and the real provider-backed file service. It pauses a generated file, advances injected source/receiver clocks by 25 hours, confirms the old ticket is denied, approves a fresh full-hash-matching source, resumes the retained prefix and separately saves/verifies the provider copy. System clocks are not changed. This is local Android-to-Android evidence, not a fresh Windows-to-Android or physical-network run.

The physical-network procedure is prepared for the user's later execution; see step 40 in [the validation kit](physical-network-validation.md). Logs live under `.tools/phase66-*`; they are local evidence, not committed release artifacts.

## Limits

This covers Android standalone receiving. Batch-owned renewal, measured ETAs, upload-operation expiry recovery, broad source-slot/history cleanup, full-capacity and long-running/provider/power/crash/clock-skew/upgrade matrices and complete accessibility remain open in the [completion audit](completion.md). Local authenticated Android-to-Android evidence does not establish fresh Windows-to-Android or physical-network interoperability. Windows GUI automation remains stopped after Escape, and the blocked standalone coordinator is not bypassed. Release artifacts are unsigned; no publication or deployment is implied.
