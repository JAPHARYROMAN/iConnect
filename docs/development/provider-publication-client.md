# Android provider client during incoming publication

Implementation built on 7 November 2026. Device verification and a completed measurement of the changed publication path remain pending. The original large-file run uses the earlier production APK and remains separate evidence.

## Observed cost

After the original Android upload received all 4,295,229,457 bytes, its receipt entered provider finalization. A twelve-second sampling trace of that live process includes repeated provider calls, queries, reference management and OS URI-permission queries. The analyzer attributes 1.554 seconds to `refContentProvider` and 1.510 seconds to `getUriPermissions` across sampled threads. These observations identify work in the publication loop; they are not an end-to-end benchmark or a measured speedup.

The private verification, provider write and provider read-back still use bounded buffers. Reacquiring a provider for each ancestry/name query adds work on top of the checks themselves.

## Change and security boundaries

An incoming publication attempt now acquires one unstable provider client for its recorded destination tree and closes it when the attempt returns, fails or is canceled. Creation, ancestry/name queries and provider descriptor opens use a resolver wrapping that client. Android supplies this wrapper from API 29, the application's minimum platform. [Android resolver API](https://developer.android.com/reference/android/content/ContentResolver#wrap(android.content.ContentProviderClient)). The Android 10 document contract routes creation and ancestry operations through the resolver's provider call interface. [Android 10 implementation](https://github.com/aosp-mirror/platform_frameworks_base/blob/android10-release/core/java/android/provider/DocumentsContract.java).

`SafDestination` keeps a separate application resolver for current persisted URI permissions. Holding a provider connection does not replace the OS access check. Each existing per-range check of pairing, grant, permission, exclusions, foreground authority, cancellation, parent path and destination name remains. Provider metadata and permission decisions are not cached across ranges. The 32 KiB range size, private hashes, provider flush/sync, independent read-back hash and final publication checks remain unchanged.

The ordinary private-copy and download callers keep their existing default resolver. Nested destination validation forwards the selected provider resolver while continuing to read OS permissions from the application resolver. Stored receipt formats, receiving limits and replay boundaries do not change.

An unavailable or failed provider follows the existing interrupted/review path. A recorded provider document is retained for verification; retrying the same receipt does not rewrite it or silently create another copy. A provider client is not background authorization and is not evidence that a provider is trustworthy.

## Verification

Two regressions are added to `IncomingUploadsTest`:

- An acquired client and a previously valid destination cannot keep validation working after persisted Android read/write access is released.
- Moving a newly published provider document outside its destination during read-back prevents completion. The exact private payload remains; reopening and finishing the original receipt keeps the review outcome without creating another copy.

Debug, instrumentation and unsigned release builds pass in 9 minutes 4 seconds. All 85 JVM tests pass with zero failures, errors or skips. Debug lint reports zero errors and 71 warnings. The saved instrumentation manifest retains the normal JUnit runner, `ProcessRestartProbe` and `NativeTransportProbe`.

| Saved artifact | SHA-256 |
| --- | --- |
| Changed debug application | `8d40e536c274e947767e2cbbfa5fecd8ed4de00c07d5fd6b1603ab118ded388f` |
| Changed instrumentation APK | `cf4d89d803a9463aacf97131b091a3d02074ca0039ad72697db885d9327877a2` |

Evidence is retained locally in `.tools/phase112-android-build.log`, `.tools/phase112-apk-hashes.json`, `.tools/phase112-test-manifest.txt` and `.tools/phase112-publication-profile.txt`. These two regressions and the existing receiving/cancellation/recovery cases must execute against the changed APK. A guarded follow-up queues all 17 incoming-upload cases, a 519-case device regression including all four actual-capacity cases and the storage suite, then the changed upload journey above 4 GiB. The regression excludes the separately scheduled large-file class and four fresh-invitation interoperability classes; those exclusions are not passes.

The new snapshot is saved separately and is not installed while the original large-file, corrected offline-copy, process-death and native interoperability sequence remains active. The follow-up requires that exact preceding sequence to complete successfully before changing either APK. Device results and a measured performance change remain pending.

Other providers and physical devices, provider-process death, the broader crash/lifecycle matrices and independent security review remain required. The [completion audit](completion.md) retains those gates and the complete application objective.
