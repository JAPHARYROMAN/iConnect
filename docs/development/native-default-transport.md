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

Unsigned default packaging, without an explicit native-bundle argument, produces `iConnect.DefaultTransportFixture-1.247.0.0-x64.unsigned.msix`, SHA-256 `c55e38fc03a4c930e209b1ed3074a7b8640954ee0e358fadd93d72e399201b4b`. Archive/source/notice inspection, its 24-component inventory and all six package guards pass. It is not signed or installed. The [artifact-bound record](evidence/windows-native-default-adoption-2026-11-09.json) preserves the hashes, counts and limits. The final ordinary solution build also passes without warnings/errors. The 4,096-item receiving and private-snapshot cases pass; sender capacity, large-file, preview and final ordinary-publish checks are running separately.

The [first hosted source-build job](https://github.com/JAPHARYROMAN/iConnect/actions/runs/42641731615/job/129244301689) subsequently passes on commit `266448c`, including the selection guards. Its seven-day workflow artifact has ZIP SHA-256 `c2da70300f6b551348d9354f2fe1de79e289d2d52d5b170d5188a59ab657362e`. Downloading it through GitHub and independently reading it locally verifies that digest, both corrections and the complete bundle. The hosted DLL is `8acf23bee1fda8fd7448157f05096a368993b5dbfee19fe4eedf9621f4816428`; its bundle-evidence hash is `a9d1a4efc61d5236cd64f8d99b6895a85de3f1f0b3f91ae9985f03fcd2426fdd`. This is a distinct compiled artifact. The Windows consumer jobs are still running; this observation alone does not establish their runtime result. The earlier `f5cd179` workflow finishes all four jobs successfully on its original default transport.

The current default package's [advisory refresh](evidence/current-package-advisories-2026-11-09.json) returns zero OSV matches across 24 recorded coordinates/commits. The current unsigned Android release separately returns zero matches across 68 Maven coordinates. These are artifact-bound lookups at the recorded times, with incomplete native/configuration coverage and no production signing or independent review claim.

The hosted bundle also passes local unsigned packaging and all six package guards. A separate control exposes one selection-handoff bug: the original package builder validates an explicit bundle, then its development helper rejects an unrelated invalid environment selection. The corrected builder scopes the selected bundle through publishing and restores the caller's environment afterward. The exact pre-fix script, adjusted only for its original tools directory, reproduces the failure. The fixed builder succeeds under that same invalid ambient selection, producing the hosted DLL with its source records and passing all six guards. The [before/after evidence](evidence/windows-native-explicit-bundle-2026-11-09.json) preserves both results. A hosted packaging step now tests this scenario; its first run remains pending. Neither local review package is signed or installed.

Default adoption does not complete the application. Combined-artifact Windows–Android validation, physical separate-network testing, full provider/controller coverage, complete license review, independent security review and signed installed lifecycle evidence remain separate gates.
