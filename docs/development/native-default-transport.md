# Corrected Windows transport in application builds

Windows application builds now require the pinned source transport with both the [SCTP serialization](native-sctp-investigation.md) and [DTLS startup](native-dtls-startup.md) corrections. The production connectivity project no longer restores the vendor DLL. Its former NuGet package remains a private test dependency for the package verifier's legacy-origin refusal control.

The current pin is **OpenSSL 3.5.9 LTS**, supported through 8 April 2030 under the [OpenSSL release strategy](https://openssl-library.org/policies/releasestrat/). The [current downloads policy](https://openssl-library.org/source/) identifies the earlier 3.6 line as out of support. Its passing runtime/advisory checks below are historical evidence; those binaries are refused for current application selection. The [LTS migration record](native-openssl-lts.md) tracks qualification of the replacement.

## Prepare and build

Use Windows x64, PowerShell 7.2+, Git, MSVC x64, CMake, Ninja and Perl. The builder also downloads the pinned, hash-checked NASM archive. The first preparation compiles OpenSSL and libdatachannel; subsequent development commands validate and reuse the completed bundle.

```powershell
./tools/prepare-native-transport.ps1 -Jobs 2
./tools/dev.ps1 build -c Release -p:RestoreLockedMode=true
```

The default application cache is `%USERPROFILE%/.cache/iconnect/native-transport/application-connection-init-v2-openssl35/bundle`. Native compilation uses a separate cache without whitespace. `prepare-native-transport.ps1 -BuildDirectory <path>` selects a different compilation directory. A completed matching OpenSSL build can be reused through `-ReuseOpenSslBuildDirectory`; its source, assembler and library hashes are checked before and after compilation. An older 3.6 cache cannot satisfy the new source lock.

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

The latter preserves generated fixtures and a JSON report under `.tools/native-selection-verification`. It checks missing evidence, a changed binary, unknown source locks, an altered historical-lock catalog even with an updated manifest, preservation of an invalid existing destination, exclusion of unlisted files, idempotent preparation and environment selection. The default-runtime regression additionally checks the actual copied DLL, both ordered correction IDs, the LTS source coordinates and all source-bundle hashes. Selection refuses an expired or unspecified pinned support period.

## Historical 3.6 adoption validation on 9 November 2026

These artifacts established the correction and packaging behavior before the support-policy review. They do not qualify the replacement LTS binary for release.

The ordinary Release solution builds without warnings/errors and its locked restore passes. The default output contains the no-delay combined DLL `57f119d4…`. All **959 selected Windows main cases** pass without failures/skips, including the new complete-bundle regression, and all 60 tooling tests pass. Seven selection checks pass. Separate actual-bundle controls refuse the older SCTP-only candidate and a missing bundle in a direct SDK build. A further SDK control restores a changed runtime DLL and metadata file despite their deliberately newer timestamps.

Unsigned default packaging, without an explicit native-bundle argument, produces `iConnect.DefaultTransportFixture-1.247.0.0-x64.unsigned.msix`, SHA-256 `c55e38fc03a4c930e209b1ed3074a7b8640954ee0e358fadd93d72e399201b4b`. Archive/source/notice inspection, its 24-component inventory and the original six package guards pass. It is not signed or installed. The [adoption record](evidence/windows-native-default-adoption-2026-11-09.json) preserves that checkpoint. The [completed local split validation](evidence/windows-native-default-local-validation-2026-11-09.json) subsequently passes all 975 case executions: 959 main, three capacity, three over-4-GiB and ten preview cases. The solution build has zero warnings/errors.

The [first hosted source-build job](https://github.com/JAPHARYROMAN/iConnect/actions/runs/42641731615/job/129244301689) passes on commit `266448c`, including the selection guards. Its seven-day workflow artifact has ZIP SHA-256 `c2da70300f6b551348d9354f2fe1de79e289d2d52d5b170d5188a59ab657362e`. Downloading it through GitHub and independently reading it locally verifies that digest, both corrections and the complete bundle. The hosted DLL is `8acf23bee1fda8fd7448157f05096a368993b5dbfee19fe4eedf9621f4816428`; its bundle-evidence hash is `a9d1a4efc61d5236cd64f8d99b6895a85de3f1f0b3f91ae9985f03fcd2426fdd`. This distinct compiled artifact subsequently passes the complete hosted validation below. The earlier `f5cd179` workflow finishes all four jobs successfully on its original default transport.

The current default package's [advisory refresh](evidence/current-package-advisories-2026-11-09.json) returns zero OSV matches across 24 recorded coordinates/commits. The current unsigned Android release separately returns zero matches across 68 Maven coordinates. These are artifact-bound lookups at the recorded times, with incomplete native/configuration coverage and no production signing or independent review claim.

The hosted bundle also passes local unsigned packaging and the original six package guards. A separate control exposes one selection-handoff bug: the original package builder validates an explicit bundle, then its development helper rejects an unrelated invalid environment selection. The corrected builder scopes the selected bundle through publishing and restores the caller's environment afterward. The exact pre-fix script, adjusted only for its original tools directory, reproduces the failure. The fixed builder succeeds under that same invalid ambient selection, producing the hosted DLL with its source records and passing all six guards. The [before/after evidence](evidence/windows-native-explicit-bundle-2026-11-09.json) preserves both results. The [child workflow on `de5453a`](https://github.com/JAPHARYROMAN/iConnect/actions/runs/42647509354) subsequently passes all five jobs, including this explicit-selection packaging scenario. Neither local review package is signed or installed.

The first adoption workflow completes all five jobs successfully. Its [hosted result](evidence/windows-native-default-hosted-validation-2026-11-09.json) independently records 975 Windows case executions with zero failures/skips, a clean managed solution build and a successful ordinary publish/inventory step on the hosted DLL. The version-integrity guard below is a later change; the two completed workflows do not validate that new guard.

## Application version integrity

A later local audit finds a limitation in the original no-build publish check. A versioned packaging build updated the shared desktop outputs while capacity tests were running; the subsequent no-build publish reused application version `1.268.0.0`, while that test's inventory declared `1.0.0.0`. Its native/source checks passed, and the original artifact remains preserved. A fresh publish with a build now verifies `1.0.0.0` in both the desktop binary and inventory, together with the selected DLL and 24 components.

Inventory generation now requires the actual nonredirected staged desktop binary and refuses a different version before writing an inventory. Guard fixtures copy that binary and use its real version. All **seven current package guards** pass on the earlier default package, the independently built hosted transport package and the legacy SCTP-only review package. The latter remains ineligible as an application default. The [version-integrity record](evidence/windows-inventory-version-integrity-2026-11-09.json) distinguishes the original limited check, fresh publish and all three guard sets.

The next hosted run (`7ea6ec8`) exposes another stale expectation: the ordinary development project declares `0.2.0-dev`, producing file version `0.2.0.0`, while CI still requests inventory version `1.0.0.0`. The version guard correctly refuses it. CI now requests `0.2.0.0`. A fresh LTS solution build and ordinary publish verify that version in the staged binary and 24-component inventory; the former CI expectation is independently refused before any incorrect inventory is written. The earlier `1.0.0.0` local publish remains a versioned fixture, not proof of the project's ordinary development version.

The current LTS artifact now passes [twelve Windows–Android native transport/file-engine scenarios](evidence/native-lts-windows-android-2026-11-09.json), including both frame/upload roles, four batch-cancellation combinations and four verified Windows original-folder moves and restores. The original emulator is preserved; the matrix uses a distinct fresh headless AVD and exact frozen APKs.

Default adoption does not complete the application. Full native controller/invitation journeys, physical separate-network testing, broader provider coverage, complete license review, independent security review and signed installed lifecycle evidence remain separate gates.
