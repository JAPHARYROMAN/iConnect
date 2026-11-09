# Recovering the completed Android large-upload result

The frozen sender-checkpoint APK completes the original `uploadAndIncomingProviderOver4GiBReopenTheOriginalReceipts` case. Its terminal AndroidJUnitRunner output reports one passing case in 7,636.975 seconds, with matching start/finish identity and a successful instrumentation terminal code. The unchanged case verifies 4,295,229,457 bytes, the original receipt/checkpoint behavior and source/provider publication checks. The production APK is `b8cc8e37…`; the test APK is `6667c887…`.

The outer helper nevertheless records failure: its summary expression requires `1 tests`, while JUnit emits `1 test`. The helper's before/focused/after APK pulls all match the frozen artifacts. A subsequent read-only observation confirms inactive instrumentation, the owned AVD and matching installed APKs again. The original helper failure remains preserved. Its raw ADB command exit code was not retained, so recovery claims the completed JUnit case, not a successful original wrapper run. The [recovery record](evidence/android-large-upload-recovered-2026-11-09.json) binds the log, source/test artifacts, parser and observations. The previous three-hour upload failure on the older APK remains a failure.

## Shared output verification

`tools/verify_android_instrumentation.py` accepts singular and plural summaries. It requires the exact expected count, sequential start/pass identities and counters, the AndroidJUnitRunner identifier, one successful terminal result, and no failure, skip or crash status. A success-looking summary alone is insufficient. Callers can bind the exact `class#method`; logs are bounded to 16 MiB. Its assessment concerns the saved runner output. The device wrapper separately checks the ADB command exit code and before/after APK identities.

Twelve controls pass, including the captured one-case output, plural output, wrong counts/identities, duplicate summaries/terminal results, missing start/terminal records, repeated counters, all unsuccessful statuses and crashes. CI executes these controls. The captured fixture contains generated test metadata and no file content or pairing material.

The remaining original download and offline-copy cases are running on the same frozen APKs with the shared parser. Their wrapper writes each ADB exit code immediately, before further observations. It does not repeat the already completed upload case. The complete matrix and physical-provider/network coverage remain unproven until their required evidence is collected.
