# Approved storage development milestone

This extends the [initial connectivity proof](verification.md). It is part of the full application build, not a completed release. All test data is disposable; no personal drive or phone folder was automatically shared.

## Storage boundary

Windows grants explicitly bind a peer identity, selected root identity, permission mask, exclusions, optional expiry and enabled state. Normal Windows ACLs apply without elevation. A remote client receives random session references rather than filesystem paths. Every bounded operation resolves the current policy again. Handles open one path component at a time relative to an already verified directory; reparse points and multiply linked files are rejected, and held ancestors exclude delete sharing during the call. Root replacement does not authorize a replacement volume/directory.

Android uses a persisted URI obtained through the system folder picker and a separate owner approval for a pinned peer. A system URI permission alone grants no remote access. The provider's tree URIs constrain operations. Non-root operations also require verified current ancestry, with excluded ancestor names rechecked, including after a range read. Providers without sufficient ancestry or range-read support fail closed. File version metadata is advisory provider metadata; it is not a content-integrity proof or a filesystem snapshot.

Android's provider model has no portable atomic no-replace rename primitive, so rename is refused rather than treating a preflight existence query as a guarantee. Folder creation requires both the independent owner permission and provider capability. Android provider errors, absent mounts, revoked URI grants, unknown-size/virtual documents and unseekable streams are handled as unavailable or unsupported. Compatibility with real local and cloud providers still needs field coverage.

Directory replies contain at most 32 entries; range reads contain at most 32 KiB. Incoming work, outstanding RPCs, native buffers and issued references are bounded. Android uses session-scoped references and one expiring directory cursor; Windows maintains bounded references and closes cursors when policy changes. Names, response sizes, status values, permissions and continuation references are validated before display/use. Terminal preview control bytes are replaced.

The channel now requires a `ready-v1` acknowledgement from both authenticated devices before publishing availability. Android waits at most ten seconds for selected direct-candidate statistics; a missing initial statistics sample does not bypass path verification. There is still no TURN/relay fallback. Both development clients must be updated together.

## Verification

On 2 November 2026, the Windows solution built without warnings/errors, and 43 .NET tests passed. These include actual native authenticated file RPC, explicit grants, authorization changes, exclusions, 95-item paging, Unicode file reads, no-replace rename/move, held-ancestor behavior, hard links, junction substitution, replaced files/roots and adversarial response bounds. The tests exercise local fixtures under the repository's ignored `.local` directory.

Seven Android storage instrumentation tests passed on the isolated API 35 emulator. The final combined storage/Keystore/activity run reported `OK (9 tests)` in 2.847 seconds. The separately installed test APK hosts a framework `DocumentsProvider` containing disposable files; its owner issues real Android tree URI grants. Tests check persisted access across store instances, peer isolation, read-only behavior, permission/exclusion/expiry changes, Android permission revocation, documents moved outside the tree, renamed excluded ancestors, source-version changes, folder creation and bounded 97-item paging. This establishes Android framework permission behavior for the fixture provider; it is not a substitute for testing vendor/local/cloud providers. Android debug/test APK builds, four JVM tests and lint also passed.

The cross-platform rerun passed after the readiness change: Android reported `PAIRED`, `AUTHENTICATED`, `FILE_BROWSING_VERIFIED`, `SENT`, and `BIDIRECTIONAL_VERIFIED`, followed by `OK (1 test)` in 34.473 seconds. Windows reported `PHONE_BROWSING_VERIFIED` after reading the actual phone fixture and receiving access-denied for folder creation under its read-only grant. This includes actual approved-file previews in both directions and the separate 256 KiB synthetic checksum transfer in each direction. The initial extended run exposed a selected-path statistics timing race; the passing run used the bounded wait and mutual readiness handshake.

## Reproduce

Run `./tools/dev.ps1 build` and `./tools/dev.ps1 test`. Build Android with `:app:assembleDebug :app:assembleDebugAndroidTest :app:testDebugUnitTest :app:lintDebug`. Install both APKs only on a disposable development device. Invoke `com.iconnect.proof.AndroidStorageTest` through `androidx.test.runner.AndroidJUnitRunner` for the framework-storage checks.

For network interoperability, follow the [connectivity fixture steps](verification.md#reproduce-the-interoperability-check). The test APK now approves only its disposable phone fixture after mutual pairing. After Android reports `FILE_BROWSING_VERIFIED`, supply `phone` to the Windows fixture; expect `PHONE_BROWSING_VERIFIED` for the opposite direction and a denied read-only mutation. Then finish the synthetic file exchange with `send`. The fixture grants and local verification evidence do not authorize automatically sharing user data in the regular applications.

## Remaining work

The [completion audit](completion.md) remains authoritative for outstanding scope: the native WinUI application and background agent, full explorers, durable verified transfers of user-selected files, folders/batches, safe destination finalization, resume/queues/lifecycle support, history, packaging, signing, independent-network tests and security review. Text range previews do not satisfy the real-file-transfer requirement.

Android behavior follows the official [Storage Access Framework guide](https://developer.android.com/training/data-storage/shared/documents-files), [DocumentsContract API](https://developer.android.com/reference/android/provider/DocumentsContract), and [framework DocumentsProvider implementation](https://android.googlesource.com/platform/frameworks/base/+/refs/heads/main/core/java/android/provider/DocumentsProvider.java). Provider-specific guarantees must be established before enabling additional mutations.
