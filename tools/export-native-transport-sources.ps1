#Requires -Version 7.2
[CmdletBinding()]
param(
    [string]$BuildDirectory,
    [string]$Destination
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'native-transport-patches.ps1')
$lockPath = Join-Path $PSScriptRoot 'native-transport-sources.json'
$lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json -AsHashtable
if ($lock.schema -ne 1 -or $lock.cacheKey -cnotmatch '\A[A-Za-z0-9_-]{1,80}\z' -or $lock.libdatachannel.submodules.Count -ne 5) {
    throw 'Unsupported native source lock.'
}
if (!$BuildDirectory) { $BuildDirectory = Join-Path $env:USERPROFILE ('.cache/iconnect/native-transport/' + $lock.cacheKey) }
$root = (Resolve-Path -LiteralPath $BuildDirectory).Path
if (!$Destination) { $Destination = Join-Path $PSScriptRoot ('../.tools/native-transport-bundles/' + [guid]::NewGuid().ToString('N')) }
$destinationPath = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $destinationPath) { throw 'Choose a new, empty destination path; existing bundles are preserved.' }
try { $owner = [IO.File]::Open((Join-Path $root 'owner.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None) }
catch { throw 'The native build cache is already in use or inaccessible.' }

function Hash([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Invoke-Git([string[]]$Arguments) {
    $output = & git.exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Native source export failed (git exit $LASTEXITCODE)." }
    return $output
}
function Assert-Source([string]$Path,[string]$Commit,[object[]]$Patches = @()) {
    Assert-IConnectNativeSource $Path $Commit $Patches
}

try {
    $artifact = Join-Path $root 'artifact'
    $buildEvidence = Join-Path $artifact 'build-evidence.json'
    if ((Get-Item -LiteralPath $buildEvidence).Length -gt 64KB) { throw 'Native build evidence is oversized.' }
    $evidence = Get-Content -LiteralPath $buildEvidence -Raw | ConvertFrom-Json -AsHashtable
    $nativePatches = @(Get-IConnectNativeSourcePatches $evidence)
    Assert-IConnectNativePatchArtifacts $artifact $nativePatches
    $binary = Join-Path $artifact 'datachannel.dll'
    $sourceLockHash = Hash $lockPath
    if ($evidence.schema -ne 1 -or $evidence.stage -cne 'BuildOnly' -or
        $evidence.sourceLockSha256 -cne $sourceLockHash -or (Hash (Join-Path $artifact 'source-lock.json')) -cne $sourceLockHash -or
        $evidence.binarySha256 -cnotmatch '\A[a-f0-9]{64}\z' -or (Hash $binary) -cne $evidence.binarySha256) {
        throw 'Native source bundle requires the matching pinned build artifact and evidence.'
    }
    $components = @(
        @{ name='libdatachannel'; path='libdatachannel'; commit=$lock.libdatachannel.commit; repository=$lock.libdatachannel.repository; license='LICENSE' }
        @{ name='openssl'; path='openssl'; commit=$lock.openssl.commit; repository=$lock.openssl.repository; license='LICENSE.txt' }
        @{ name='libjuice'; path='libdatachannel/deps/libjuice'; license='LICENSE' }
        @{ name='libsrtp'; path='libdatachannel/deps/libsrtp'; license='LICENSE' }
        @{ name='usrsctp'; path='libdatachannel/deps/usrsctp'; license='LICENSE.md' }
        @{ name='plog'; path='libdatachannel/deps/plog'; license='LICENSE' }
        @{ name='json'; path='libdatachannel/deps/json'; license='LICENSE.MIT' }
    )
    $libSource = Join-Path $root 'libdatachannel'
    foreach ($component in $components) {
        if (!$component.commit) {
            $module = 'deps/' + $component.name
            $component.commit = $lock.libdatachannel.submodules[$module]
            $component.repository = (Invoke-Git -Arguments @('-C',$libSource,'config','-f','.gitmodules','--get',('submodule.' + $module + '.url'))).Trim()
        }
        $component.source = Join-Path $root $component.path
        Assert-Source $component.source $component.commit $(if ($component.name -ceq 'libdatachannel') { $nativePatches } else { @() })
    }
    # Do not create output until all preflight checks pass. Failures after this point
    # preserve the partial bundle without a completed bundle-evidence.json marker.
    [void](New-Item -Path $destinationPath -ItemType Directory)
    foreach ($child in @('sources','notices','recipe')) { [void][IO.Directory]::CreateDirectory((Join-Path $destinationPath $child)) }
    $inventory = @()
    foreach ($component in $components) {
        $archiveName = 'sources/' + $component.name + '.zip'
        $archive = Join-Path $destinationPath $archiveName
        [void](Invoke-Git -Arguments @('-C',$component.source,'archive','--format=zip',('--prefix=' + $component.path + '/'),('--output=' + $archive),$component.commit))
        $zip = [IO.Compression.ZipFile]::OpenRead($archive)
        try {
            $prefix = $component.path + '/'
            foreach ($entry in $zip.Entries) {
                if (!$entry.FullName.StartsWith($prefix,[StringComparison]::Ordinal) -or $entry.FullName.Contains('\') -or
                    $entry.FullName -match '(^|/)\.\.?(/|$)' -or $entry.FullName -match '[\x00-\x1f:]') { throw 'Unexpected source archive path.' }
            }
            $entry = $zip.GetEntry($prefix + $component.license)
            if (!$entry -or $entry.Length -gt 1MB -or $entry.Length -eq 0) { throw "Missing source license for $($component.name)." }
            $noticeName = 'notices/' + $component.name + '-' + $component.license
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,(Join-Path $destinationPath $noticeName),$false)
            $inventory += [ordered]@{
                name=$component.name; repository=$component.repository; commit=$component.commit
                sourceArchive=$archiveName; sourceArchiveSha256=(Hash $archive); sourceEntries=$zip.Entries.Count
                rootLicense=$noticeName; rootLicenseSha256=(Hash (Join-Path $destinationPath $noticeName))
                role=if ($component.name -eq 'json') { 'Pinned source; examples disabled in this build' } else { 'Transport source input' }
            }
        } finally { $zip.Dispose() }
        Assert-Source $component.source $component.commit $(if ($component.name -ceq 'libdatachannel') { $nativePatches } else { @() })
    }
    $files = @{}
    foreach ($name in @('build-native-transport.ps1','native-transport-sources.json','test-native-transport.ps1','export-native-transport-sources.ps1','review-native-transport.ps1','read-native-transport-bundle.ps1','native-transport-patches.ps1','native-sctp-initialization.json','native-dtls-initialization.json')) {
        $relative = 'recipe/' + $name
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $destinationPath $relative)
        $files[$relative] = Hash (Join-Path $destinationPath $relative)
    }
    foreach ($patch in $nativePatches) {
        $relative = 'recipe/' + $patch.patchFile
        $target = Join-Path $destinationPath $relative
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $patch.patchFile) -Destination $target
        $files[$relative] = Hash $target
    }
    foreach ($name in @('datachannel.dll','build-evidence.json','source-lock.json')) {
        Copy-Item -LiteralPath (Join-Path $artifact $name) -Destination (Join-Path $destinationPath $name)
        $files[$name] = Hash (Join-Path $destinationPath $name)
    }
    if ($files['datachannel.dll'] -cne $evidence.binarySha256 -or (Hash $binary) -cne $evidence.binarySha256) { throw 'Native payload changed during export.' }
    @'
Pinned native transport source and notice bundle

Extract each sources/*.zip into the same directory to reconstruct libdatachannel,
its five pinned dependency trees, and OpenSSL. Git archives follow upstream export
attributes and do not include Git history. Source files retain per-file notices;
notices/ also contains each project's root license extracted from its archive.
The JSON dependency is included as a pinned source input; examples are disabled.
recipe/ records the build, source lock, application fixture verifier and exporter.
NASM and the compiler/toolchain are not redistributed in this bundle.
The builder verifies Git checkouts; exported archives are source snapshots and
do not replace those checkouts when running the builder.
When build-evidence.json lists nativePatches, the libdatachannel archive is the
pinned upstream base. Apply its exact recipe/native-patches/*.patch files to
reconstruct the modified source. The recipe/native-*-initialization.json files
record each patch and resulting source hashes (UTF-8 with LF). The recipe builder's
-SctpInitializationFix switch selects the SCTP correction;
-ConnectionInitializationFixes selects both SCTP and DTLS corrections.

bundle-evidence.json is written only after export completes. It binds the source
archives, root licenses, recipe and build evidence to this exact DLL. This bundle
does not establish reproducible builds, complete license review, independent
security review, cross-platform interoperability or production release approval.
'@ | Set-Content -LiteralPath (Join-Path $destinationPath 'README.txt') -Encoding UTF8
    $files['README.txt'] = Hash (Join-Path $destinationPath 'README.txt')
    $report = [ordered]@{
        schema=1; stage='SourceAndNoticeBundle'; binarySha256=$evidence.binarySha256
        sourceLockSha256=$sourceLockHash; components=$inventory; files=$files
        checkedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')
        completeLicenseReview=$false; productionReleaseApproved=$false
    }
    $marker = Join-Path $destinationPath 'bundle-evidence.json.partial'
    $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $marker -Encoding UTF8
    [IO.File]::Move($marker,(Join-Path $destinationPath 'bundle-evidence.json'),$false)
    [pscustomobject]@{ Stage=$report.stage; Components=$inventory.Count; BinarySha256=$evidence.binarySha256; Bundle=$destinationPath }
} finally { $owner.Dispose() }
