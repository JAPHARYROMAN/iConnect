# Android validation state and local approval preservation

The first complete 552-case run on the frozen production APK finishes with **538 passed, 14 failed and none skipped**. Its original output, immediate ADB exit zero, exact identities and six before/focused/after APK captures are retained. A successful ADB command does not make failing JUnit cases pass. The [original failure record](evidence/android-regression-storage-failures-2026-11-09.json) remains separate from subsequent checks.

Ten failures occur in `AndroidStorageTest`. Its fixtures use global app source-ticket storage; read-only inspection finds uncommitted `.new` and `.bak` files there. Production source preparation correctly refuses saved progress requiring review. `QueueRecoveryTest` also deliberately damages a global sending cache. The fixes give those two fixture classes their own metadata directories and preferences through `IsolatedStorageContext`, matching the existing scoped fixtures. Source workers are shut down before profile cleanup; the queued-dispatcher control drains canceled work before waiting for completion. The real recovery guard is unchanged.

The other four failures are local UI assertions that require the entire phone-sharing list to be empty. The retained app already has approvals. Each changed journey now deliberately seeds a generated read-only approval before opening its local workflow, captures the existing approval set before adding a local shortcut, and requires that exact set afterward. This exercises native preview, local copying, local batches and folder creation with an existing approval. `UiExistingApproval` revokes its own generated peer during cleanup. The checks do not erase other approvals or require local actions to disconnect an existing share.

## Focused result

All **29 storage/recovery cases and five selected native local UI cases pass**, with no failures/skips. The storage group leaves the unrelated global source metadata unchanged. The four modified UI journeys preserve their deliberately seeded read-only approvals. The fifth selected UI case is the browser class's other existing case.

The UI wrapper originally requests four cases although those four classes contain five methods. Its original count-mismatch failure is preserved. Strictly assessing the saved output against the independent five-case source selection verifies the completed run, its retained ADB exit zero and six matching APK captures. That completed run was not repeated. The [focused record](evidence/android-storage-local-ui-focused-2026-11-09.json) distinguishes passing execution from the failed original wrapper.

The rebuilt test APK is `41be6e290aca57ea15711757f92fb2afc1941360b4570dd1a08a026fb890546e`. The production debug APK remains `b8cc8e37ebcbadc52e0d0445150be5b4fa18da86bdf9b5d849d59d3c99de0a0e`; the unsigned release APK remains `535a0c1d79ba9dea836ce63a1261b543c18bddfd43c079ee4490b9483e619f2a`. All 89 JVM cases pass and lint has no errors. These fixture repairs change instrumentation sources only. The earlier large-file evidence still binds to the same production APK.

A fresh complete 552-case run is in progress on the corrected test APK. Focused success does not replace that result. Physical-device/provider coverage, the separate-network check, Windows GUI validation and independent security/release gates remain open.
