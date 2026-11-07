# Native transport provenance and build review

Reviewed 7 November 2026. This is partial release evidence. Native adoption, complete advisory/license review and independent security review remain open.

## Current Windows artifact

The application resolves `MediaToolkit.WebRtc.Native.win-x64` 0.24.6.1. Its restored `datachannel.dll` SHA-256 is `39ff41497d059067a34fd9cc691236a0b5dff7af5ad6b1f6df3f9887b907b7e6`. The package describes libdatachannel 0.24.x with statically bundled OpenSSL, libjuice, libsrtp and usrsctp. Windows product/file version metadata is absent. An embedded string identifies OpenSSL 3.6.5, dated 29 September 2026; that string does not establish the complete build recipe. [NuGet metadata](https://www.nuget.org/packages/MediaToolkit.WebRtc.Native.win-x64/0.24.6.1)

The recorded repository, `https://github.com/Rukhlov/MediaToolkit.NetCore`, returns HTTP 404 through both the connected reader and the public page. Its recorded commit is `3ea7c24a093ee841d71b5fda05fe7e68e8ba8816`. Corresponding source, exact bundled revisions and complete notices remain unverified for that binary. The existing native-source and license release gate stays open.

On the review date, the published OpenSSL 3.6 advisory page listed fixed-version thresholds through 3.6.5; no listed affected interval included 3.6.5. This is an advisory/version comparison, not verification of vendor patches, configuration or complete native dependency coverage. [OpenSSL 3.6 advisories](https://openssl-library.org/news/vulnerabilities-3.6/index.html)

## Candidate source build

`tools/native-transport-sources.json` pins public libdatachannel 0.24.6 and OpenSSL 3.6.5 commits, all five recursive gitlinks and a hash-checked NASM 3.02 download. Checkouts and recursive revisions are verified. The assembler comes from [NASM's release server](https://www.nasm.us/pub/nasm/releasebuilds/3.02/win64/), with its archive hash checked against the [Scoop manifest](https://github.com/ScoopInstaller/Main/blob/master/bucket/nasm.json). No tag-signature verification is claimed. [libdatachannel release](https://github.com/paullouisageneau/libdatachannel/releases/tag/v0.24.6), [OpenSSL source revision](https://github.com/openssl/openssl/commit/c8bd5a57108599ac650bbae77fcabe3109dab2e8)

The builder needs Windows MSVC x64, CMake, Ninja and Perl. Its source-preparation mode passes and rejects an unexpected local source file before compilation. The isolated OpenSSL source build and installation pass, producing static SSL/crypto libraries. The complete builder is now running. Full DLL compilation, runtime dependency inspection, application interoperability, notices and packaging integration are pending.

```powershell
./tools/build-native-transport.ps1 -PrepareOnly
./tools/build-native-transport.ps1 -Jobs 1
```

Sources and outputs use a cache under the user profile, with a default single worker. OpenSSL uses `VC-WIN64A no-shared`. The libdatachannel recipe retains media and WebSocket support, uses the pinned bundled dependencies and links OpenSSL statically. Its output is marked `BuildOnly`, includes source/payload hashes and has `applicationInteropVerified: false`. The candidate is not yet adopted by the application. Existing encrypted transport tests and remaining field/release gates must pass before adoption and distribution.

## Android artifact check

The unsigned development release APK SHA-256 is `bb436f7017884f40904923148defbc65a8c72975ef9ca4c27ca0ab276a6c6394`; its embedded inventory SHA-256 is `aaf9a129220a2ab1486baa8e3d3ee2a79ec6ecb4a6d56dbf8664ddbef7b9dfe7`. OSV checked all 68 Maven coordinates on 7 November 2026 with no matches. The report is `.tools/security-20261107-android-f4152f5/dependency-review.json`. This does not cover every bundled native component or establish a production signature.

The inventory pins `io.github.webrtc-sdk:android:150.7871.04`, upstream artifact SHA-256 `1872dda4e3bf7d6ea6f7c6e11861332081c57081a1e62fc82fab72415c69c84c`. The SDK notes describe a compromised-certificate trust-anchor change for that version. A coordinate match does not independently verify the native build. [SDK release notes](https://github.com/webrtc-sdk/Specs/releases)
