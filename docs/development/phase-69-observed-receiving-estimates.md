# Observed receiving progress and payload estimates

Standalone and batch receiving views on Windows and Android now display recent payload progress and an approximate remaining-payload time. They still show the configured-limit lower bound and peer-reported source expiry. The forecast is bounded by the current local payload cap and can warn when it reaches the reported expiry. It never extends an approval, resumes a job or changes a provider decision.

This is receiving progress, not an OS/network billing meter or a promise of whole-copy completion. Source preparation, queueing, retries, verification, security checks and provider saving can take longer. Small/fast files may finish before a rate can be measured. Sending/upload directions, local copies and forecasts for the other stages remain separate work.

## Sampling and lifecycle

Both implementations use monotonic timestamps, at least two seconds of actual progress, a roughly twenty-second rolling window and a ten-second no-progress cutoff. Repeated reads and unchanged offsets do not count as progress. Old rates disappear after stalls; fresh activity starts a new window. Pause, rollback, source/session/limit changes and app closure discard earlier samples. Per-file samples never aggregate unrelated jobs.

The cache is bounded to 128 jobs and 96 points per job. It holds only in-memory job/source/session scopes and counters, without filenames, paths, IP addresses or network names. It is never exported, persisted, sent to the coordinator or used as transfer authority. A one-second view refresh invalidates stale text without issuing file requests. UI refreshes are coalesced on Windows and cancelled with the controller lifetime on both platforms.

Windows observes the existing throttled receiving notifications. Android adds a separate range-progress stream, emitted at most every 250 milliseconds apart from phase/final events. It does not change `PhoneDownload.offset` or checkpoint-chain semantics. The UI labels live bytes and the saved resume checkpoint separately. Pausing/cancelling still flushes validated progress through the original receiver; observations neither acknowledge bytes nor create provider copies.

## Verification

The Windows solution builds in 25.72s with zero warnings/errors. All 76 focused checks pass in 16s, including 19 new tracker cases and a native NTFS receiving-observation case. All **723 selected Windows cases** pass in **3m 16s** with the same three capacity exclusions. Eight recorded production/test DLL hashes match before/after.

Android debug, unsigned release and instrumentation builds pass in **3m 31s**. All **75 JVM cases** pass with no failures/errors, including 17 new estimate cases. Lint reports **zero errors and 67 existing warnings**. All **49 focused native cases** pass in **45.228s**, including the new live-progress and actual-card checks plus 47 receiving-recovery compatibility cases. All **442 selected native cases** pass in **1,206.422s (20m 06s)** with the same eight exclusions and zero failing status codes. Built and installed application/test APK hashes match before/after; the owned emulator helper has exited and its serial is absent.

Logic checks cover startup measurement, rounding, cap-bounded forecasts, idle expiry, pause/resume, changed scope, offset/clock rollback, delayed observations, rolling-window replacement, independent jobs, bounded cache/pruning, maximum supported sizes and malformed observations. Native checks exercise actual receiving bytes independently of publication and durable checkpoints; UI checks cover measured, waiting and stopped text.

Physical step 43 is prepared in [the validation kit](physical-network-validation.md). No physical-network run or Windows GUI automation is implied. Windows GUI automation remains stopped after Escape; the blocked standalone coordinator is not bypassed.

## Remaining work

Observed payload time does not establish measured preparation/verification/security/provider-save time or an aggregate batch-completion forecast. Ordinary upload and offline local-file/local-batch file payload estimates are implemented on [Windows](phase-74-windows-send-local-payload-estimates.md) and [Android](phase-75-android-send-local-payload-estimates.md); remaining sending/upload/local-copy directions stay open. Receiving-owner upload write-window recovery is covered by the later [Windows](phase-70-windows-upload-window-recovery.md) and [Android](phase-71-android-upload-window-recovery.md) implementations. Broad source-slot/history cleanup, complete full-load/performance, provider/volume/lifecycle/power/crash/clock-skew/upgrade/accessibility and physical matrices remain in the [completion audit](completion.md). Signed release/update/uninstall, hardened operations and independent security review are still release gates.
