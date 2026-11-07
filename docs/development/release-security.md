# Review release dependencies

Current Windows MSIX and Android release APK builds embed a CycloneDX 1.6 software bill of materials. The Windows inventory uses the desktop and worker runtime graphs, the .NET runtime packs and the separately copied WebRTC DLL. The Android inventory uses resolved release Maven artifacts and records SHA-256 hashes of their upstream JARs and AARs. Both inventories carry the app version. Package signing covers the embedded inventory. [CycloneDX 1.6 JSON reference](https://cyclonedx.org/docs/1.6/json/)

## Extract and check a package

Verify the intended release signature using the [Windows](windows-release.md) or [Android](android-release.md) procedure. Then run the review from PowerShell with a new output directory.

```powershell
./tools/review-release-dependencies.ps1 -Package $releasePackage `
    -OutputDirectory .local/releases/dependency-review
```

The output contains `sbom.cdx.json` and `dependency-review.json`. The review records the package and SBOM hashes, lookup time, coordinate count and matching advisory IDs. The lookup sends only package URLs to OSV. Exit code 2 indicates matching advisories. A missing report indicates an incomplete lookup. Use a new output directory to repeat a check. Add `-SbomOnly` to extract the same inventory offline. [OSV batch query API](https://google.github.io/osv.dev/post-v1-querybatch/)

Before distributing a build, review findings against the affected versions, platform and exposure. Upgrade affected dependencies or record the evidence for any finding assessed as inapplicable. Rebuild and review the final package, then publish its inventory alongside the reviewed artifact hash. A new build or a new signature changes the package under review.

## Coverage

The dependency lookup covers known advisories indexed for the listed Maven/NuGet coordinates and commit-pinned GitHub sources at the recorded time. GitHub inventory entries use full commit hashes and are queried by commit, without a package version in the same query. Review native components bundled inside dependencies against their upstream advisories, including the WebRTC implementations. Keep separate evidence for the deployed coordinator, the operating-system runtimes, dependency licenses and the independent authentication, filesystem and preview isolation review.

The [native transport follow-up](native-transport-review.md) records the current binary hashes, the Windows vendor-source provenance gap and the candidate build from pinned public source. It also records the fresh 68-coordinate Android package lookup. Native adoption and release review remain pending.

The candidate has a separate source/notice export and commit-level advisory procedure. It retains the source inputs and root license texts for review, with hashes bound to the candidate DLL. An optional Windows review package now includes this bundle and identifies its seven source components instead of labeling the selected DLL as the vendor package. Its 24-component inventory passes schema validation and an artifact-bound advisory lookup with zero matches. Default builds continue using the restored NuGet transport. Complete native adoption, license review and release verification remain open.

Use the [security policy](../../SECURITY.md) for reporting and triage. Confirm the repository has a monitored private reporting channel before distributing release builds.

The GitHub repository API reports private vulnerability reporting disabled on 7 November 2026. A working private contact and its monitoring remain unverified release requirements.

## Fixture evidence

Both embedded inventories passed the CycloneDX 1.6 schema check. OSV returned zero matches for 18 Windows and 68 Android coordinates on 2026-11-05. A synthetic vulnerable coordinate returned advisory matches and exit code 2. The review rejected a missing inventory and preserved an existing output directory.

The inventory audit found that an earlier Windows MSIX omitted its WebRTC DLL. The rebuilt package and CI-style publish contain the resolved DLL, and the published copy loaded with the expected native exports.

The Windows Release build passed with zero warnings or errors, along with 838 selected Windows tests and 10 isolated preview tests. The selected run excluded three capacity tests. Android debug, test and unsigned release builds passed, as did a disposable-signed release build and all 84 JVM tests. Debug lint reported zero errors and 71 warnings.

## Hosted build verification

On 7 November 2026, the hosted workflow for commit `9a7ffe0` passed both platform jobs. Windows built Release with zero warnings or errors. All 856 main regression cases, the three separate 4,096-item capacity cases and 10 isolated preview cases passed. Desktop publishing also passed its checks for compiled XAML, resources, the native transport and both workers. The sender capacity case completed in 19 minutes 57 seconds. [GitHub run](https://github.com/JAPHARYROMAN/iConnect/actions/runs/42337462066)

The Ubuntu Android job built debug, instrumentation-test and unsigned release APKs, then completed its JVM test and debug lint tasks. SDK setup specifies `platform-tools` explicitly to avoid the retired `tools` package in the setup action's default list. [Android job](https://github.com/JAPHARYROMAN/iConnect/actions/runs/42337462066/job/128297521644), [setup action inputs](https://github.com/android-actions/setup-android/blob/v3/action.yml)

The hosted Android job builds the instrumentation APK. Its device execution is covered by the separate emulator and field records. The Windows sender capacity fixture uses an accepting attachment checker. Installed security-provider, signed-package, accessibility and physical-network verification remain in the [completion audit](completion.md).
