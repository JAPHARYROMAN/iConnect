# Outgoing verification control release

The [first native folder-recovery attempt](evidence/android-folder-recovery-failure-2026-11-09.json) fails when Android starts another completed-batch check immediately after canceling one. The sender reports the cancellation before its completion callback removes the outgoing control. The next call correctly refuses the still-active control, interrupting the generated-data interoperability journey.

`PhoneBatchSender.verifyPublished` now waits for both its worker and the existing control-release marker before returning. An exception cancels the worker and is rethrown after that cleanup. The wait also runs when the caller is canceled, using a non-cancelable cleanup context. Success waits for the same marker, so a completed worker cannot return ahead of control unregistration. This changes the local API completion boundary; remote resource-release acknowledgements remain a separate protocol obligation.

The existing peer, pairing, successful-parent, retained-history and two-control admission checks remain in place. The receiver cleanup deadline and original failure are preserved. This operation still changes no published files, source originals or durable receipts.

## Regression evidence

Two new device methods cover cancellation and an ordinary failed check. Each holds the existing owner gate while the worker reaches completion, then runs a barrier on the caller's single-thread executor. That barrier observes whether the API returned while its cleanup callback still needs the held gate. It does not depend on an arbitrary short sleep or inspect the private control registry. After releasing the gate, each test requires the original failure type/message, inactive local state, unchanged receipt/source bytes and an immediately successful fresh check without a separate `whenIdle()` call.

The first regression fixture times out on both APKs before establishing the intended assertions. Those two attempts are retained and excluded from success evidence. The corrected fixture selects the same four clean entries used by the established batch-check tests, explicitly requires successful receiver outcomes and reports any refusal before its request callback.

The matched run uses identical instrumentation (`8118961b…`) with the preserved `1727aa9e…` production APK and corrected `dd6f88a4…` APK. Both new methods fail at the exact early-return assertion on the former. All **53 selected receiving/sender cases** pass on the latter in 100.683 seconds, without failures or skips. The [before output](evidence/sender-control-release-before-2026-11-09.txt), [fixed output](evidence/sender-control-release-after-2026-11-09.txt) and [artifact-bound record](evidence/sender-control-release-2026-11-09.json) are tracked.

The complete debug/test/unsigned Release build passes in **9m 7s**, with all **87 JVM cases** passing. After the fixture correction, the test-only APK/lint rebuild passes in **3m 18s**; debug and unsigned Release APK bytes remain unchanged. Final lint reports zero errors and 71 warnings. The evidence distinguishes these two build scopes and binds the final source hashes.

Both native matrices pass on the same corrected app and instrumentation: **four cancellation cases** with eight cancellation acknowledgements/eight fresh checks, followed by **four Windows original-folder recovery cases** with four verified moves and four verified restores. Both initiating roles and listing/blind inboxes retain the original transfer, resume, hash, source/collision, Unicode and withdrawal checks. The Windows fixtures remain `72e01283…` and `5b55d480…`; the pinned native library remains `84a0a63d…`. Exact installed APK hashes match before and after each matrix.

The original failed folder run remains preserved. This is direct native engine evidence on the owned API 35 emulator. It does not close the separate large-file timeout, Android-source moves, broader capacity/provider/lifecycle/controller/physical-network, accessibility or independent security/release gates in the [completion audit](completion.md).
