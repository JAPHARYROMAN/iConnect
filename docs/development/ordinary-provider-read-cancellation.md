# Ordinary provider-read cancellation

Work in progress, 9 November 2026. Device execution is queued behind the preserved large-file, activity-backup and native-engine validation sequence. The full application remains incomplete.

## Implementation

`AndroidIncomingUploads` now opens ordinary published-copy checks and provider-backed incoming-window reviews through an unstable provider client, passing the operation's cancellation signal to `openFile`. The descriptor closes on completion or cancellation, and the client closes when that read ends. Publication continues using its existing retained, wrapped provider connection. Receipt formats, authority checks, hash/length checks, provider publication and source-removal decisions do not change.

The earlier [batch check regression](published-batch-checks.md) observed that its resolver read path did not deliver a cancellation signal to the document provider. The ordinary helper used the same resolver API. This change addresses that opening path; the queued ordinary-file baseline must establish its own runtime reproduction before this follow-up is considered verified.

Two instrumentation methods cover three cancellation scenarios: a fresh check of an ordinary completed publication, review of an expired receipt's recorded provider copy, and confirmation of that review. They require the provider to receive a cancellation signal, interrupt a blocked read within three seconds, preserve file bytes and receipt metadata, and permit a subsequent check. The ordinary case also requires both shared verification permits to be available after cancellation. Window checks accept actual coroutine or Android provider cancellation; timeouts and unrelated failures do not satisfy the assertion. Cleanup can explicitly release only the generated fixture's blocked open.

## Saved builds

Debug, instrumentation and unsigned Release builds pass. All 86 JVM tests pass with zero failures, errors or skips. Debug lint reports zero errors and 71 warnings. The final instrumentation rebuild passes separately after tightening its cancellation exception handling. These are build results; the new device cases have not run.

| Artifact | SHA-256 |
| --- | --- |
| Baseline production APK | `54d2c19a489e7c25c2a6c565bdbf37564a33e9fada40b4c9778992151368edf0` |
| Candidate production APK | `e5bca36d76ea0711ceb3eba2b9d480de0ae882f87342070db550857f4c797f74` |
| Identical instrumentation for both runs | `cdd3306074f0339d9f3b6935d645bed8859649e3cd1601acec3b2a26ee356ce4` |
| Candidate unsigned Release APK | `70b86b91adc9a38410dd6dc2c367b5c62abbc9a2e50c02c9c330025947f54e4e` |

The source parent is `7908dad02702477571be7d51bf718c6ef38a7f41`; the manifests also bind the changed source files and full frozen Windows fixture inventory. The candidate manifest `.tools/phase131-read-fixed-artifacts.json` has SHA-256 `411e8f54a2553ba74a654ee8f23c1948e1ca2d2f00240480cda00c94c817a715`. The matched baseline is `.tools/phase133-matched-baseline-artifacts.json`. Build logs are `.tools/phase131-cancellable-reads-build.log` and `.tools/phase133-final-read-tests-build.log`.

## Required device evidence

The owned follow-up waits for the exact predecessor process to finish and requires its completed report before installing anything. It verifies the expected emulator, inactivity, installed APKs, frozen APKs and helper/manifest bytes. It preserves failed observations and retries read-only observations only.

1. Run both new methods on the baseline APK. Both must fail at the exact assertion that the provider did not receive a cancellation signal. A timeout, crash, skipped case, duplicate method or unrelated exception cannot reproduce this baseline. Seven parser controls exercise those acceptance/refusal boundaries without device mutation.
2. Run all **45 selected cases** on the candidate: the two new methods, 17 incoming-upload cases, 20 incoming-window cases and six completed-batch check cases. All must pass without failures or skips, with exact APK hashes verified before and after.
3. Execute both native Windows–Android ordinary-upload connection roles with the candidate APKs and the pinned source-built Windows transport. Both directions must retain their receipts, resume the lost accepted-range reply, preserve collision/source originals, obtain fresh published-copy checks and refuse withdrawn receiving authority. Installed APK hashes must still match afterward.

Current follow-up state is `.tools/phase132-device-state.json`, with reports under `.tools/phase132-device-proofs/`. It is waiting for the live predecessor, rather than treating the earlier interrupted sequence's stale state as completion. None of these steps has a passing device result yet. Broader provider/lifecycle, actual process/power-loss, physical separate-network, accessibility and independent security/release evidence remain open in the [completion audit](completion.md).
