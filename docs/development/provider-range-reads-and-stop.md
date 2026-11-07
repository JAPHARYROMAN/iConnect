# Provider range reads and prompt batch stopping

Work in progress, 7 November 2026 (Africa/Nairobi). This follow-up is backed up on `codex/android-large-file-validation`; the [completion audit](completion.md) remains active.

## Android provider reads

Profiling the generated large-file download on the disposable API 35 emulator found repeated provider Binder calls dominating the read loop. The baseline was intentionally stopped after receiving 477,265,920 of 4,295,229,457 bytes in approximately 24 minutes 29 seconds. It was still progressing and did not establish a completed large-file result.

Each range now acquires one unstable provider connection, uses it on the synchronous worker, and closes it after all cursors and the file descriptor have closed. [Android's resolver wrapper](https://developer.android.com/reference/android/content/ContentResolver#wrap(android.content.ContentProviderClient)) supports this on the application's minimum API level 29. The connection is never shared between threads or retained between requests. The implementation follows the [provider-client lifetime requirements](https://developer.android.com/reference/android/content/ContentProviderClient).

Every range still checks current peer/grant authority, read permission, exclusions, provider containment, complete bounded ancestry and the requested file version. It checks containment, ancestor visibility and the version again after reading, before releasing any bytes. The ancestry inspection supplies the leaf metadata for that inspection, removing immediate duplicate queries. Source-ticket reads use the same validated read path instead of performing a second complete pre-read inspection. The 32 KiB range limit, seekability requirement and source-ticket size limit remain enforced. No metadata or authorization is cached across ranges.

Three new device tests mutate the provider during file opening: change its version, move it outside the approved tree, or exclude its ancestor. Each exercises both ordinary range reads and prepared source-ticket reads, requiring an error with no returned bytes or entries. All 24 storage tests pass on the final installed APKs, including framework permission revocation, issued-reference policy changes, bounded ranges, ticket ownership and background-source authority.

Debug, instrumentation and unsigned release APK builds pass. All 85 JVM tests pass without failures or skips. Debug lint reports zero errors and 68 warnings. The broader device run completes with 493 passing cases, four invitation-dependent interoperability skips and zero failures in 58 minutes 36 seconds. Together with the storage run, 517 device checks pass on the same production/test APKs. The generated large-file journeys are running; complete Android support above 4 GiB remains unproven.

`LargeFileTest` needs at least 18 GiB free in the app data filesystem; a separate disposable emulator with a 32 GiB data partition is used locally. Each of its three cases allows up to three hours. It generates every byte, crosses the real 2/4 GiB boundaries, reopens persisted checkpoints and independently hashes source and provider output. It uses native managers and a real test DocumentsProvider through in-process requests; encrypted transport, physical devices and other providers remain separate checks.

## Windows stop controls

The backup branch's hosted run exposed two Windows failures: a temporary reader-lock test exhausted the bounded retry window, and a concurrent cancel/close test found a remaining destination file. Investigation reproduced a scheduling gap independently: batch Cancel queued cleanup without first signaling its active read.

Cancel now registers the shared cleanup task and immediately signals Pause before returning. Cleanup remains asynchronous, and reentrant controls join the same registered task. The existing rules preserving already-published files and checking native identities before partial removal remain in place.

A deterministic regression holds the shared ownership gate while invoking Pause or Cancel and checks the active request's cancellation token before releasing the gate. A queued cleanup worker cannot satisfy this assertion. The Cancel case fails before the correction; both cases pass afterward. The six focused checkpoint-lock, stop-control and concurrent cancel/close cases pass. All 858 selected Windows main regression cases then pass locally in 4 minutes 3 seconds without failures or skips. Capacity, isolated preview and large-file jobs run separately in the hosted workflow.

The reader-lock test now runs the commit on a dedicated worker and releases the native reader from its already-running test thread. It no longer depends on a thread-pool continuation being scheduled within the production retry window. Production atomic-commit retry durations are unchanged, and persistent locks must still preserve both the earlier and staged state.

## Hosted verification

The workflow for implementation commit `f4152f5` passes all three jobs. Windows builds Release with zero warnings/errors and passes 858 main cases, three separate capacity cases, 10 isolated preview cases and desktop publish-asset checks. The private sender capacity case takes 19 minutes 55 seconds. The separate large-file job passes all three cases in 10 minutes 22 seconds. Android passes debug, instrumentation-test and unsigned release builds, JVM tests and debug lint. [Complete workflow](https://github.com/JAPHARYROMAN/iConnect/actions/runs/42348093891)

These results cover the provider-read and batch-stop implementation. The later process-death probe has its own pending execution record. The four skipped interoperability cases require fresh external runner invitations and are excluded from the 517 passing device checks.
