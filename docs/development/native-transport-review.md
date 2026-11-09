# Native transport provenance and build review

Reviewed 7 November 2026. This page preserves the original vendor and unpatched-source baseline. On 9 November, ordinary Windows builds adopted the [source transport with both initialization corrections](native-default-transport.md). The older artifact hashes and default-selection observations below describe their original runs. Complete advisory/license review and independent security review remain open.

## Original Windows vendor artifact

The application resolves `MediaToolkit.WebRtc.Native.win-x64` 0.24.6.1. Its restored `datachannel.dll` SHA-256 is `39ff41497d059067a34fd9cc691236a0b5dff7af5ad6b1f6df3f9887b907b7e6`. The package describes libdatachannel 0.24.x with statically bundled OpenSSL, libjuice, libsrtp and usrsctp. Windows product/file version metadata is absent. An embedded string identifies OpenSSL 3.6.5, dated 29 September 2026; that string does not establish the complete build recipe. [NuGet metadata](https://www.nuget.org/packages/MediaToolkit.WebRtc.Native.win-x64/0.24.6.1)

The recorded repository, `https://github.com/Rukhlov/MediaToolkit.NetCore`, returns HTTP 404 through both the connected reader and the public page. Its recorded commit is `3ea7c24a093ee841d71b5fda05fe7e68e8ba8816`. Corresponding source, exact bundled revisions and complete notices remain unverified for that binary. The existing native-source and license release gate stays open.

On the review date, the published OpenSSL 3.6 advisory page listed fixed-version thresholds through 3.6.5; no listed affected interval included 3.6.5. This is an advisory/version comparison, not verification of vendor patches, configuration or complete native dependency coverage. [OpenSSL 3.6 advisories](https://openssl-library.org/news/vulnerabilities-3.6/index.html)

## Candidate source build

`tools/native-transport-sources.json` pins public libdatachannel 0.24.6 and OpenSSL 3.6.5 commits, all five recursive gitlinks and a hash-checked NASM 3.02 download. Checkouts and recursive revisions are verified. The assembler comes from [NASM's release server](https://www.nasm.us/pub/nasm/releasebuilds/3.02/win64/), with its archive hash checked against the [Scoop manifest](https://github.com/ScoopInstaller/Main/blob/master/bucket/nasm.json). No tag-signature verification is claimed. [libdatachannel release](https://github.com/paullouisageneau/libdatachannel/releases/tag/v0.24.6), [OpenSSL source revision](https://github.com/openssl/openssl/commit/c8bd5a57108599ac650bbae77fcabe3109dab2e8)

The builder needs Windows MSVC x64, CMake, Ninja and Perl. Its source-preparation mode passes and rejects an unexpected local source file before compilation. The complete builder passes, producing static OpenSSL SSL/crypto libraries and `datachannel.dll` SHA-256 `84a0a63dcc579d97e4afac959866f9e61e9584c18732045a48db673d8ec87dde`. This is one observed build, not a byte-for-byte reproducibility claim. Optional review packaging is recorded below; default adoption and complete license/notice review remain pending.

```powershell
./tools/build-native-transport.ps1 -PrepareOnly
./tools/build-native-transport.ps1 -Jobs 1
# First build tests/IConnect.Tests in Release; -Dotnet can select an installed SDK.
./tools/test-native-transport.ps1 -ArtifactDirectory (Join-Path $env:USERPROFILE '.cache/iconnect/native-transport/0246-openssl365/artifact')
```

Sources and outputs use a cache under the user profile, with a default single worker. OpenSSL uses `VC-WIN64A no-shared`. The libdatachannel recipe retains media and WebSocket support, uses the pinned bundled dependencies and links OpenSSL statically. Its output is marked `BuildOnly`, includes source/payload hashes and has `applicationInteropVerified: false`. The candidate is not yet adopted by the application. Existing encrypted transport tests and remaining field/release gates must pass before adoption and distribution.

The separate verifier passes nine application fixture cases without skips in an isolated copy of the Release test output. They cover native peer authentication and readiness, bidirectional file delivery, authorized browsing and revocation, folder publication, upload receipt resumption and local STUN binding. A loader assertion checks the actual process module's absolute path and SHA-256; it passes for the candidate and rejects a deliberately incorrect expected hash. The standard test output retains the existing NuGet binary.

Before execution, the verifier checks the source-lock/evidence/payload hashes, x64 PE header, all 18 required C exports and direct DLL imports. The observed imports are `advapi32.dll`, `bcrypt.dll`, `crypt32.dll`, `kernel32.dll`, `user32.dll` and `ws2_32.dll`; unexpected external imports require review. It requires all nine cases across the five actual transport test classes, then checks that both candidate copies retain their hashes. The successful report is `.tools/native-transport-verification/14b08ca149bd4a3893b9ef23dc1a6247/native-verification.json`, marked `WindowsNativeFixturesPassed`. This local fixture proof does not establish Android interoperability, physical-network behavior, complete license coverage or release readiness.

## Upstream test execution

The same `VC-WIN64A no-shared` OpenSSL build passes `nmake /nologo test` with `HARNESS_JOBS=1`. The harness reports `Files=356, Tests=3865` and `Result: PASS` in 1,185 seconds. Platform and build skips include FIPS-only, shared-library, dynamic-engine and unsupported native-Windows checks; these are not reported as executed passes. The build-tree SSL and crypto library hashes still match the libraries recorded in the candidate's build evidence. The full log is `test-openssl.log` under the native build cache. This is upstream test evidence, not cryptographic certification. [Upstream Windows test command](https://github.com/openssl/openssl/blob/c8bd5a57108599ac650bbae77fcabe3109dab2e8/NOTES-WINDOWS.md)

The configured libdatachannel `datachannel-tests` target also builds and passes all 13 upstream cases in 49 seconds. Its final summary, rather than the exit code alone, is required because this upstream runner returns zero even when a case fails. Cases include wrong-fingerprint rejection, DTLS startup, C/C++ connectivity, reliability, media tracks, WebSocket fixtures and cleanup. The DLL hash before and after this run matches the candidate above. The build cache retains `test-datachannel.log`; `.tools/phase108-datachannel-upstream.json` records the result. These local fixtures do not test the deployed coordinator or separate physical networks.

## Source and notice export

After a completed build, export its DLL, build evidence, pinned source archives, root licenses and recipe into a new local bundle. The exporter requires the matching current source lock, exact commits and clean worktrees, and refuses a cache owned by an active build or test. It leaves incomplete exports without a completed evidence marker. Git archives preserve per-file notices and follow upstream export attributes; they contain source snapshots without Git history. [Git archive behavior](https://git-scm.com/docs/git-archive)

```powershell
$bundle = ./tools/export-native-transport-sources.ps1
./tools/review-native-transport.ps1 -BundleDirectory $bundle.Bundle `
    -OutputDirectory .tools/native-advisory-review/new-review
```

The bundle covers libdatachannel, OpenSSL and all five pinned dependency trees. The JSON tree is included as a pinned source input; the build disables examples. The exporter copies each root license from its source archive, hashes the archives, notices and recipe, and binds them to the DLL. This supports source and attribution review; complete license review and distribution approval remain explicit open gates.

The advisory tool validates the pinned source commits, archive hashes and DLL before sending only the seven public commit hashes to OSV. It preserves the request, response and bundle manifest, rejects incomplete or paginated responses, and records the DLL and source-lock hashes. Advisory matches return exit code 2. A missing completed report indicates an unfinished review. Commit lookups cover indexed records, and do not establish that unlisted vulnerabilities are absent. [OSV commit and batch queries](https://google.github.io/osv.dev/post-v1-querybatch/)

The first complete bundle passes on 7 November 2026 at `.tools/native-transport-bundles/76e7206b26b5471d98dedd275fe7c6c0`. All seven source archives and root licenses, the recipe files, build evidence and DLL pass an independent hash read-back. The artifact-bound advisory lookup checks all seven commits with zero matches; its report is `.tools/native-advisory-review/4a3e574168574e79bae228ade780073a/native-advisory-review.json`, bound to bundle-evidence SHA-256 `df1ee9c799e50ea4bfa070aef0ff53d64e5d1bd066858f1b875c5e1d951e2d17`.

The exporter refuses an active cache and an existing bundle destination. Synthetic incomplete and paginated API responses fail without a completed report; a synthetic matching advisory is retained and returns exit code 2. A separate real API control returns three advisories for OSV's documented vulnerable example commit. Synthetic control outputs are explicitly marked as fixtures and are separate from the real artifact-bound lookup. Native adoption, full advisory/license review and independent security review remain open.

## Source-backed Windows review package

The [Windows package builder](windows-release.md#review-a-source-built-transport) accepts an explicit `-NativeTransportBundle`. A shared validator checks the current source lock, exact repositories/commits, all recipe/source/license hashes, x64 PE header and nonredirected paths. Only its fixed file inventory is copied into the new publish payload. The staged copy is checked again before replacing that payload's DLL. Ordinary build output keeps the vendor DLL.

The unsigned `IConnect.NativeSourceProof` 1.109.0.0 fixture builds successfully. Package SHA-256 is `bc0eefdfb8bbbe11df509d18ecf771bea23ec1f33f612c875643e338fee17002`. Its canonical DLL matches the candidate hash above. All 25 bundle members are present and match their source hashes. The embedded CycloneDX 1.6 inventory validates against the schema and contains 24 components: 17 NuGet/runtime coordinates and seven pinned GitHub source coordinates. The unused JSON example dependency is explicitly excluded. The source binary is not labeled as the vendor package. GitHub names are normalized according to the [PURL definition](https://github.com/package-url/purl-spec/blob/main/docs/types/definitions/github-definition.md).

The package advisory lookup checks all 24 coordinates, using commit queries for the seven GitHub inputs, with zero matches on 7 November 2026. The artifact-bound report is `.tools/phase109-source-package-review/178db7140e1741b8b6413d84bc0c64bf/dependency-review.json`. These lookups do not replace full upstream or independent security review.

`tools/test-native-package.ps1` verifies the archive's native DLL, source-bundle members and native inventory. Its six optional guard checks pass: the standard NuGet inventory remains correct, existing inventories are preserved, an unlabeled replacement is rejected, runtime/source mismatches fail, and changed or redirected notices are refused. The successful guard record is `.tools/native-package-verification/34684e219d704d518a6eb693daf7fef4/native-package-verification.json`. The source-backed package was inspected without signing, installing or launching it. Cross-platform interoperability, full license review, production signing and installed lifecycle gates remain open.

## Direct Windows–Android transport follow-up

The [direct native fixture](native-cross-platform.md#executed-windowsandroid-result) passes both actual Windows–Android frame roles with matching bidirectional hashes. Ordinary-upload and folder-batch engine probes are separately prepared and queued behind the active large-file/activity sequence. Frame interoperability does not replace those engine checks, full pairing/controller/coordinator or physical-network verification.

## Readable notice follow-up

The [source-notice inventory](native-notice-review.md) preserves 855 matching comment blocks and 17 notice-document candidates from all seven archives, binds them to the candidate DLL and records unexamined/otherwise unclassified text for review. A new unsigned review package includes and verifies all 19 supplemental files alongside the original 25 bundle members. Five parser tests, eight reader refusals and the six existing package guards pass. Complete license coverage, actual linker closure, default adoption and production distribution approval remain open.

## Android artifact check

The unsigned development release APK SHA-256 is `bb436f7017884f40904923148defbc65a8c72975ef9ca4c27ca0ab276a6c6394`; its embedded inventory SHA-256 is `aaf9a129220a2ab1486baa8e3d3ee2a79ec6ecb4a6d56dbf8664ddbef7b9dfe7`. OSV checked all 68 Maven coordinates on 7 November 2026 with no matches. The report is `.tools/security-20261107-android-f4152f5/dependency-review.json`. This does not cover every bundled native component or establish a production signature.

The inventory pins `io.github.webrtc-sdk:android:150.7871.04`, upstream artifact SHA-256 `1872dda4e3bf7d6ea6f7c6e11861332081c57081a1e62fc82fab72415c69c84c`. The SDK notes describe a compromised-certificate trust-anchor change for that version. A coordinate match does not independently verify the native build. [SDK release notes](https://github.com/webrtc-sdk/Specs/releases)
