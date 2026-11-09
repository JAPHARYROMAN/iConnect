# Recovering the completed Android large-upload result

The frozen sender-checkpoint APK completes the original `uploadAndIncomingProviderOver4GiBReopenTheOriginalReceipts` case. Its terminal AndroidJUnitRunner output reports one passing case in 7,636.975 seconds, with matching start/finish identity and a successful instrumentation terminal code. The unchanged case verifies 4,295,229,457 bytes, the original receipt/checkpoint behavior and source/provider publication checks. The production APK is `b8cc8e37…`; the test APK is `6667c887…`.

The outer helper nevertheless records failure: its summary expression requires `1 tests`, while JUnit emits `1 test`. The helper's before/focused/after APK pulls all match the frozen artifacts. A subsequent read-only observation confirms inactive instrumentation, the owned AVD and matching installed APKs again. The original helper failure remains preserved. Its raw ADB command exit code was not retained, so recovery claims the completed JUnit case, not a successful original wrapper run. The [recovery record](evidence/android-large-upload-recovered-2026-11-09.json) binds the log, source/test artifacts, parser and observations. The previous three-hour upload failure on the older APK remains a failure.

## Shared output verification

`tools/verify_android_instrumentation.py` accepts singular and plural summaries. It requires the exact expected count, sequential start/pass identities and counters, the AndroidJUnitRunner identifier, one successful terminal result, and no failure, skip or crash status. A success-looking summary alone is insufficient. Callers can bind the exact `class#method`; logs are bounded to 16 MiB. Its assessment concerns the saved runner output. The device wrapper separately checks the ADB command exit code and before/after APK identities.

Twelve controls pass, including the captured one-case output, plural output, wrong counts/identities, duplicate summaries/terminal results, missing start/terminal records, repeated counters, all unsuccessful statuses and crashes. CI executes these controls. The captured fixture contains generated test metadata and no file content or pairing material.

The original download case subsequently passes on the same frozen APKs in 4,530.86 seconds, with its 4,294,967,296-byte checkpoint reopened and verified provider publication. Its wrapper retains ADB exit zero before post-test observations, verifies the exact case through the shared parser, and obtains six matching before/focused/after APK captures. The [download record](evidence/android-large-download-2026-11-09.json) includes the exact runner output and artifact bindings.

The original offline-copy case is now running with the same parser and immediate exit-code capture. The already completed upload is not repeated. The complete current matrix and physical-provider/network coverage remain unproven; the upload's failed original wrapper and missing original ADB-exit limitation remain part of the record.
