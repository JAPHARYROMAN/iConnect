# Corrected Windows transport in application builds

Windows application builds now require the pinned source transport with both the [SCTP serialization](native-sctp-investigation.md) and [DTLS startup](native-dtls-startup.md) corrections. The production connectivity project no longer restores the vendor DLL. Its former NuGet package remains a private test dependency for the package verifier's legacy-origin refusal control.

## Prepare and build

Use Windows x64, PowerShell 7.2+, Git, MSVC x64, CMake, Ninja and Perl. The builder also downloads the pinned, hash-checked NASM archive. The first preparation compiles OpenSSL and libdatachannel; subsequent development commands validate and reuse the completed bundle.

```powershell
./tools/prepare-native-transport.ps1 -Jobs 2
./tools/dev.ps1 build -c Release -p:RestoreLockedMode=true
```

The default application cache is `%USERPROFILE%/.cache/iconnect/native-transport/application-connection-init-v1/bundle`. Native compilation uses a separate cache without whitespace. `prepare-native-transport.ps1 -BuildDirectory <path>` selects a different compilation directory. A completed matching OpenSSL build can be reused through `-ReuseOpenSslBuildDirectory`; its source, assembler and library hashes are checked before and after compilation.

To prepare from an existing source bundle, supply `-CandidateBundle <bundle>`. Preparation verifies both corrections and every recorded member, copies only those members, rechecks both sides, then publishes the completed directory. It preserves a verified previous selection when replacing it and refuses an invalid existing destination. It never falls back to the old vendor binary.

An explicit `ICONNECT_NATIVE_TRANSPORT_BUNDLE` environment variable selects a different prepared bundle for development commands and packaging. Direct SDK builds also accept `-p:IConnectNativeTransportBundle=<bundle>`. Each ordinary build validates that bundle before compiling the connectivity project. Missing evidence, changed bytes and incomplete correction lists stop the build. Design-time evaluation does not compile or download native code.

The SDK copies the selected DLL to `libDatachannel/datachannel.dll` and its complete source bundle to `PackageMetadata/NativeTransport`. Copies overwrite stale outputs even when their timestamps are newer. Build and publish use the same selection; package creation checks the staged hashes before generating notices and its inventory. Unsigned review packaging and inspection remain documented in [Windows release preparation](windows-release.md).

## Hosted builds

The `native-transport` job builds and verifies one corrected source bundle, exercises the selection guards and uploads it as a seven-day workflow artifact. The Windows main and large-file jobs download that artifact, verify it again and select it explicitly before their locked restores and builds. The action revisions are pinned. Native compilation failure blocks those consumers.

Local selection verification is available independently:

```powershell
./tools/assert-native-transport.ps1
./tools/test-native-transport-selection.ps1
```

The latter preserves generated fixtures and a JSON report under `.tools/native-selection-verification`. It checks missing evidence, a changed binary, preservation of an invalid existing destination, exclusion of unlisted files, idempotent preparation and environment selection. The default-runtime regression additionally checks the actual copied DLL, both ordered correction IDs and all source-bundle hashes.

## Adoption validation on 9 November 2026

The ordinary Release solution builds without warnings/errors and its locked restore passes. The default output contains the no-delay combined DLL `57f119d4…`. All **959 selected Windows main cases** pass without failures/skips, including the new complete-bundle regression, and all 60 tooling tests pass. Seven selection checks pass. Separate actual-bundle controls refuse the older SCTP-only candidate and a missing bundle in a direct SDK build. A further SDK control restores a changed runtime DLL and metadata file despite their deliberately newer timestamps.

Unsigned default packaging, without an explicit native-bundle argument, produces `iConnect.DefaultTransportFixture-1.247.0.0-x64.unsigned.msix`, SHA-256 `c55e38fc03a4c930e209b1ed3074a7b8640954ee0e358fadd93d72e399201b4b`. Archive/source/notice inspection, its 24-component inventory and all six package guards pass. It is not signed or installed. The [artifact-bound record](evidence/windows-native-default-adoption-2026-11-09.json) preserves the hashes, counts and limits. Capacity, large-file, preview and final ordinary-publish checks are running separately; the new hosted cold source-build workflow still needs its first completed run.

Default adoption does not complete the application. Combined-artifact Windows–Android validation, physical separate-network testing, full provider/controller coverage, complete license review, independent security review and signed installed lifecycle evidence remain separate gates.
