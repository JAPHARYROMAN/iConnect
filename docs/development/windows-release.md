# Build and verify a Windows MSIX

Use PowerShell 7, the repository's .NET 10 SDK, and the x64 packaging tools from Windows SDK 10.0.26100.0. This workflow builds an x64 MSIX with the WinUI and .NET runtimes and both isolated workers. Keep the package name, publisher and application ID stable across releases. Increase the four-part package version for each distributed update. [Windows package identity](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/package-identity-overview)

## Build and sign

Build an unsigned review artifact with the publisher subject intended for signing.

```powershell
./tools/package-windows.ps1 -PackageName iConnect.Desktop -Version 1.0.0.0 `
    -Publisher $releasePublisher -Unsigned
```

To produce a signed artifact, use the release owner's code-signing certificate in the current user's Personal store and an approved HTTPS timestamp service. The publisher must exactly match the certificate subject. The script selects the certificate by thumbprint, then asks SignTool to verify the timestamped result before making the signed artifact available. The release signing identity is separate from iConnect device identities. [Microsoft MSIX signing guidance](https://learn.microsoft.com/en-us/windows/msix/package/sign-app-package-using-signtool)

```powershell
./tools/package-windows.ps1 -PackageName iConnect.Desktop -Version 1.0.0.0 `
    -Publisher $releasePublisher -CertificateThumbprint $releaseThumbprint `
    -TimestampServer $releaseTimestampUrl
```

The script prints the final SHA-256 and writes the artifact under `.local/releases/windows/`. An existing output is preserved. The local work directory contains the staged publish files and packaging inputs for inspection. Build output labeled `.unsigned.msix` is intended for review and signing. Signed releases require a publisher trusted by the destination Windows installation.

The current package includes `libDatachannel/datachannel.dll` and the [embedded dependency inventory](release-security.md). Rebuild packages made before the native publish fix to include the DLL required for direct connections.

## Review a source-built transport

The default build uses the restored NuGet transport. To review the [pinned-source candidate](native-transport-review.md), provide a completed source/notice bundle to the package builder. The builder checks its exact commits, hashes, x64 image and nonredirected file paths before publishing, then checks the staged copy. It replaces the DLL only in the new package payload and includes the source archives, notices and recipe under `PackageMetadata/NativeTransport/`.

```powershell
$bundle = ./tools/export-native-transport-sources.ps1
$package = ./tools/package-windows.ps1 -PackageName iConnect.NativeReview -Version 1.0.0.0 `
    -Publisher $releasePublisher -Unsigned -NativeTransportBundle $bundle.Bundle
./tools/test-native-package.ps1 -Package $package.Path `
    -Payload (Join-Path $package.WorkDirectory 'payload') -ExerciseGuards
./tools/review-release-dependencies.ps1 -Package $package.Path `
    -OutputDirectory .tools/native-package-review/new-review
```

The package verifier reads the archive and checks the canonical DLL, every source-bundle member and the native inventory. Its optional guard exercises use generated fixture copies and the restored default package; they do not install or launch an application. The inventory records the source commits and actual DLL hash instead of labeling the source build as the vendor package. JSON is recorded as an excluded source input because this recipe disables examples. Existing inventories are preserved; publish to a new payload directory to regenerate one.

This option prepares a review artifact. Cross-platform interoperability, complete license/advisory review, independent security review, trusted signing and installed lifecycle verification remain required before production distribution. The default transport is not changed by this option.

## Verify and install

Compare the artifact hash and publisher to the reviewed release record. Verify the expected trusted signature and timestamp with the SDK's `signtool.exe`, then install on a disposable Windows account or VM that trusts the release publisher.

```powershell
& $signTool verify /pa /tw $firstPackage
if ($LASTEXITCODE -ne 0) { throw 'Verify the package signature and timestamp before installing.' }
Get-FileHash -LiteralPath $firstPackage -Algorithm SHA256
# Compare the hash with the reviewed release record before the next command.
Add-AppxPackage -Path $firstPackage
Get-AppxPackage -Name $releasePackageName | Select-Object Name, Publisher, Version, PackageFullName
```

Open iConnect from Start. The installed app uses its own Windows package data folder for identity, settings, approvals and transfer history. That profile is separate from an unpackaged development run. The package declares an initially disabled Windows sign-in task. Enable it through iConnect only for the deliberate sign-in check and record the status shown by Windows Startup settings. [Windows StartupTask API](https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.startuptask)

## Upgrade and uninstall check

1. On the first installed version, record the device ID privately. Change a harmless setting and generate a test transfer receipt. Exercise a preview and a verified Windows receive to check that both bundled workers run. Keep the test's shared files outside the private package profile.
2. Use **Quit iConnect** from the app or tray. Verify the next signed package with the same publisher and package name, compare its hash, and run `Add-AppxPackage -Path $updatePackage`. Check the installed version and reopen from Start. Require the same device ID, saved setting and transfer receipt. Repeat both worker checks. Try the earlier signed package without force options and record whether Windows refuses the downgrade.
3. With generated data, exercise the packaged sign-in task after explicit opt-in. Verify the app starts in the notification area when the saved background and sign-in preferences allow it. Disable the task in iConnect and check the Windows Startup setting. Repeat sign-in to confirm the disabled app stays closed.
4. Quit and uninstall the exact disposable package through Windows Settings. Check that its Start and sign-in registrations are gone. Verify the generated file in the independent shared folder. Reinstall the reviewed package and require a fresh iConnect identity. Uninstall removes the package's private profile. Export or move any copies you need before removing an installation.

Record package version and hash, Windows version, install, update, downgrade, both workers, sign-in, uninstall, and the preserved shared-file hash. The [separate-network guide](physical-network-validation.md) covers pairing and file transfers with the reviewed installed builds.

## Development evidence

The Windows SDK created two x64 package versions. Each staged payload file matched its packaged SHA-256. The package-scoped resource index contained both compiled WinUI screens, and both worker executables and their runtime files were present. The package builder refused an invalid version and an attempt to overwrite an existing output.

SignTool signed both versions with a disposable certificate. The fixture checks verified the signatures and the signed block-map hashes. Windows trust verification rejected the certificate because it was untrusted. The packages were inspected without changing certificate trust, registering the app, or running a sign-in test. The installed lifecycle procedure above remains open.

The Windows Release solution built with zero warnings or errors. All 838 selected Windows tests and all 10 isolated preview tests passed. The selected run excludes the three separate capacity tests.
