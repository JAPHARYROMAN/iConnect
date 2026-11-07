#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Package,
    [Parameter(Mandatory)][string]$Payload,
    [switch]$ExerciseGuards
)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$payload=(Resolve-Path -LiteralPath $Payload).Path
$packagePath=(Resolve-Path -LiteralPath $Package).Path
if ([IO.Path]::GetExtension($packagePath) -ine '.msix') {throw 'Choose a source-backed Windows MSIX review package.'}
$bundle=Join-Path $payload 'PackageMetadata/NativeTransport'
$source=& (Join-Path $repo 'tools/read-native-transport-bundle.ps1') -BundleDirectory $bundle
$fixtureRoot=Join-Path $repo ('.tools/native-package-verification/' + [guid]::NewGuid().ToString('N'))
[void](New-Item -Path $fixtureRoot -ItemType Directory)
$reportPath=Join-Path $fixtureRoot 'native-package-verification.json'
$report=[ordered]@{Stage='NativeSourcePackageInspected';ProductionReleaseApproved=$false;SignatureTrustVerified=$false;ExerciseGuards=[bool]$ExerciseGuards;CheckedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')}
$file=[IO.File]::Open($packagePath,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
try {
    $report.PackageSha256=(Get-FileHash -InputStream $file -Algorithm SHA256).Hash.ToLowerInvariant()
    $file.Position=0
    $zip=[IO.Compression.ZipArchive]::new($file,[IO.Compression.ZipArchiveMode]::Read,$true)
    try {
        $expected=@([pscustomobject]@{RelativePath='libDatachannel/datachannel.dll';Sha256=$source.BinarySha256}) +
            @($source.Files | ForEach-Object {[pscustomobject]@{RelativePath=('PackageMetadata/NativeTransport/' + $_.RelativePath);Sha256=$_.Sha256}})
        foreach ($entryInfo in $expected) {
            $entries=@($zip.Entries | Where-Object FullName -ceq $entryInfo.RelativePath)
            if ($entries.Count -ne 1) {throw "Missing or duplicate packaged native entry: $($entryInfo.RelativePath)"}
            $stream=$entries[0].Open()
            try {$hash=(Get-FileHash -InputStream $stream -Algorithm SHA256).Hash.ToLowerInvariant()} finally {$stream.Dispose()}
            if ($hash -cne $entryInfo.Sha256) {throw "Packaged native entry differs from the source bundle: $($entryInfo.RelativePath)"}
        }
        foreach ($name in @('AppxManifest.xml','AppxBlockMap.xml','PackageMetadata/sbom.cdx.json')) {
            $entries=@($zip.Entries | Where-Object FullName -ceq $name)
            if ($entries.Count -ne 1 -or $entries[0].Length -lt 1 -or $entries[0].Length -gt 2MB) {throw "Invalid MSIX metadata: $name"}
        }
        $stream=$zip.GetEntry('PackageMetadata/sbom.cdx.json').Open()
        $reader=[IO.StreamReader]::new($stream,[Text.UTF8Encoding]::new($false,$true))
        try {$bom=$reader.ReadToEnd() | ConvertFrom-Json -AsHashtable} finally {$reader.Dispose()}
        if ($bom.bomFormat -cne 'CycloneDX' -or $bom.specVersion -cne '1.6' -or $bom.metadata.component.type -cne 'application') {throw 'Unexpected packaged inventory format.'}
        if (@($bom.components | Where-Object {$_.purl -like 'pkg:nuget/mediatoolkit.webrtc.native.win-x64@*'}).Count) {throw 'The source binary is mislabeled as the vendor package.'}
        foreach ($component in $source.Components) {
            $entries=@($bom.components | Where-Object purl -ceq $component.Purl)
            if ($entries.Count -ne 1 -or $entries[0].scope -cne $component.Scope -or $entries[0].version -cne $component.Version) {throw 'The packaged source inventory is incomplete or inconsistent.'}
        }
        if (@($bom.components | Where-Object purl -like 'pkg:github/*').Count -ne $source.Components.Count -or
            ($bom.components | Where-Object name -ceq 'libdatachannel').hashes.content -cne $source.BinarySha256) {throw 'The packaged native inventory does not describe the selected binary.'}
        $report.Components=$bom.components.Count
        $report.SourceFiles=$source.Files.Count
        $report.NativeSha256=$source.BinarySha256
    } finally {$zip.Dispose()}
} finally {$file.Dispose()}
if (!$ExerciseGuards) {
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding utf8
    [pscustomobject]@{Report=$reportPath;NativeSha256=$source.BinarySha256;SourceFiles=$source.Files.Count;Guards=0}
    return
}
# Guard fixtures use generated copies of the real published dependency graphs,
# source bundle and restored vendor binary; no application is installed or run.
$assets=Get-Content (Join-Path $repo 'apps/windows/IConnect.Connectivity/obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
$vendorPackage=@($assets.libraries.GetEnumerator() | Where-Object {$_.Key -match '^MediaToolkit\.WebRtc\.Native\.win-x64/' -and $_.Value.type -eq 'package'})
if ($vendorPackage.Count -ne 1) {throw 'Restore the standard native package before exercising default-origin guards.'}
$vendorCandidates=@($assets.packageFolders.Keys | ForEach-Object {Join-Path $_ ($vendorPackage[0].Value.path + '/native/win-x64/datachannel.dll')} | Where-Object {Test-Path -LiteralPath $_ -PathType Leaf})
if (!$vendorCandidates.Count) {throw 'The restored default native package is unavailable.'}
$vendor=$vendorCandidates[0]
function Copy-Bundle([string]$Name,[bool]$SkipNotices=$false) {
    $target=Join-Path $fixtureRoot $Name
    [void](New-Item -Path $target -ItemType Directory)
    foreach ($file in $source.Files) {
        if ($SkipNotices -and $file.RelativePath.StartsWith('notices/')) {continue}
        $destination=Join-Path $target $file.RelativePath
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
        Copy-Item -LiteralPath $file.Path -Destination $destination
    }
    return $target
}
function Make-Payload([string]$Name,[string]$Dll,[bool]$WithSource=$false) {
    $target=Join-Path $fixtureRoot $Name
    [void](New-Item -Path $target -ItemType Directory)
    foreach ($relative in @('IConnect.Desktop.deps.json','PreviewWorker/IConnect.Preview.Worker.deps.json','AttachmentWorker/IConnect.Attachment.Worker.deps.json')) {
        $destination=Join-Path $target $relative
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
        Copy-Item -LiteralPath (Join-Path $payload $relative) -Destination $destination
    }
    [void][IO.Directory]::CreateDirectory((Join-Path $target 'libDatachannel'))
    Copy-Item -LiteralPath $Dll -Destination (Join-Path $target 'libDatachannel/datachannel.dll')
    if ($WithSource) {
        foreach ($file in $source.Files) {
            $destination=Join-Path $target ('PackageMetadata/NativeTransport/' + $file.RelativePath)
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
            Copy-Item -LiteralPath $file.Path -Destination $destination
        }
    }
    return $target
}
function Write-Inventory([string]$Target) {
    & (Join-Path $repo 'tools/write-windows-release-sbom.ps1') -Payload $Target -ConnectivityAssets (Join-Path $repo 'apps/windows/IConnect.Connectivity/obj/project.assets.json') -PackageName 'iConnect.NativeInventoryFixture' -Version '1.109.0.0'
}
$checks=@()
$default=Make-Payload 'default' $vendor
Write-Inventory $default
$bom=Get-Content (Join-Path $default 'PackageMetadata/sbom.cdx.json') -Raw | ConvertFrom-Json
if (@($bom.components | Where-Object name -eq 'MediaToolkit.WebRtc.Native.win-x64').Count -ne 1 -or @($bom.components | Where-Object purl -like 'pkg:github/*').Count -ne 0) {throw 'Default native origin changed.'}
$checks+=@{Case='default-nuget-inventory';Passed=$true;Components=$bom.components.Count}
$before=(Get-FileHash (Join-Path $default 'PackageMetadata/sbom.cdx.json') -Algorithm SHA256).Hash
try { Write-Inventory $default; throw 'Existing inventory was overwritten.' } catch { if ($_.Exception.Message -notlike 'The staged inventory already exists*') {throw} }
if ((Get-FileHash (Join-Path $default 'PackageMetadata/sbom.cdx.json') -Algorithm SHA256).Hash -cne $before) {throw 'The existing inventory changed.'}
$checks+=@{Case='existing-inventory-preserved';Passed=$true}
$unlabelled=Make-Payload 'unlabelled-replacement' (Join-Path $bundle 'datachannel.dll')
try {Write-Inventory $unlabelled;throw 'Substituted DLL was mislabeled as NuGet.'} catch {if ($_.Exception.Message -notlike 'The native DLL differs from the resolved NuGet artifact*') {throw}}
if (Test-Path (Join-Path $unlabelled 'PackageMetadata/sbom.cdx.json')) {throw 'A mislabeled inventory was produced.'}
$checks+=@{Case='unlabelled-replacement-refused';Passed=$true}
$mismatch=Make-Payload 'source-runtime-mismatch' $vendor $true
try {Write-Inventory $mismatch;throw 'Mismatched source/runtime was accepted.'} catch {if ($_.Exception.Message -notlike 'The packaged native DLL does not match its source bundle*') {throw}}
$checks+=@{Case='source-runtime-mismatch-refused';Passed=$true}
$badNotice=Copy-Bundle 'changed-notice'
[IO.File]::AppendAllText((Join-Path $badNotice 'notices/libdatachannel-LICENSE'),"`nOwned fixture mutation.")
try {& (Join-Path $repo 'tools/package-windows.ps1') -PackageName 'iConnect.NativeBadFixture' -Version '1.109.0.0' -Publisher 'CN=Owned native fixture' -Unsigned -NoRestore -NativeTransportBundle $badNotice;throw 'Changed notice was accepted.'} catch {if ($_.Exception.Message -notlike 'Native bundle file changed: notices/libdatachannel-LICENSE*') {throw}}
$checks+=@{Case='changed-notice-preflight-refused';Passed=$true}
$redirected=Copy-Bundle 'redirected-notices' $true
$junction=Join-Path $redirected 'notices'
[void](New-Item -Path $junction -ItemType Junction -Target (Join-Path $bundle 'notices'))
try {
    try {& (Join-Path $repo 'tools/read-native-transport-bundle.ps1') -BundleDirectory $redirected | Out-Null;throw 'Redirected notices were accepted.'} catch {if ($_.Exception.Message -notlike 'Native bundles must not contain redirected*') {throw}}
} finally {
    if (!((Get-Item -LiteralPath $junction -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {throw 'Expected owned junction changed; no cleanup performed.'}
    [IO.Directory]::Delete($junction,$false)
}
$checks+=@{Case='redirected-notices-refused';Passed=$true}
$report.Stage='NativeSourcePackageGuardsPassed'
$report.Guards=$checks
$report.CheckedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding utf8
[pscustomobject]@{Report=$reportPath;NativeSha256=$source.BinarySha256;SourceFiles=$source.Files.Count;Guards=$checks.Count}
