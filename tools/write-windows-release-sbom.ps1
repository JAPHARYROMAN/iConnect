[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Payload,
    [Parameter(Mandatory)][string]$ConnectivityAssets,
    [Parameter(Mandatory)][string]$PackageName,
    [Parameter(Mandatory)][string]$Version
)

$ErrorActionPreference = 'Stop'
$components = @{}
$inventoryPath = Join-Path $Payload 'PackageMetadata/sbom.cdx.json'
if (Test-Path -LiteralPath $inventoryPath) { throw 'The staged inventory already exists. Publish to a new payload directory before generating another inventory.' }
$desktopPath = Join-Path $Payload 'IConnect.Desktop.dll'
if (!(Test-Path -LiteralPath $desktopPath -PathType Leaf) -or
    ((Get-Item -LiteralPath $desktopPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'The inventory requires the actual nonredirected staged desktop binary.'
}
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($desktopPath).FileVersion -cne $Version) {
    throw 'The desktop binary does not match the inventory version.'
}
$nativeMetadata = Join-Path $Payload 'PackageMetadata/NativeTransport'
$sourceNative = if (Test-Path -LiteralPath $nativeMetadata) {
    & (Join-Path $PSScriptRoot 'read-native-transport-bundle.ps1') -BundleDirectory $nativeMetadata
} else { $null }

function Add-NuGetComponent([string]$Identity, [string]$Kind, [string]$ContentHash) {
    $name, $release = $Identity -split '/', 2
    if ($Kind -eq 'runtimepack') { $name = $name -creplace '^runtimepack\.', '' }
    if ($name -notmatch '\A[A-Za-z0-9_.-]+\z' -or !$release) { throw "Invalid resolved package identity $Identity." }
    $purl = 'pkg:nuget/{0}@{1}' -f $name.ToLowerInvariant(), [uri]::EscapeDataString($release)
    $component = [ordered]@{
        'bom-ref' = $purl
        type = if ($Kind -eq 'runtimepack') { 'framework' } else { 'library' }
        name = $name
        version = $release
        purl = $purl
    }
    if ($ContentHash) {
        $digest = [Convert]::FromBase64String(($ContentHash -creplace '^sha512-', ''))
        if ($digest.Length -ne 64) { throw "Invalid NuGet content hash for $Identity." }
        $component.hashes = @([ordered]@{ alg = 'SHA-512'; content = [Convert]::ToHexString($digest).ToLowerInvariant() })
    }
    if ($components.ContainsKey($purl)) {
        $previous = $components[$purl]
        if ($previous.type -ne $component.type -or $previous.hashes.content -ne $component.hashes.content) {
            throw "Inconsistent NuGet metadata for $Identity across packaged runtimes."
        }
    }
    else { $components[$purl] = $component }
    return $components[$purl]
}

foreach ($entry in @('IConnect.Desktop.deps.json', 'PreviewWorker/IConnect.Preview.Worker.deps.json',
        'AttachmentWorker/IConnect.Attachment.Worker.deps.json')) {
    $deps = Get-Content -LiteralPath (Join-Path $Payload $entry) -Raw | ConvertFrom-Json -AsHashtable
    $target = $deps.targets[$deps.runtimeTarget.name]
    if (!$target) { throw "Missing the runtime dependency graph in $entry." }
    foreach ($key in $target.Keys) {
        if ($sourceNative -and $key -match '^MediaToolkit\.WebRtc\.Native\.win-x64/') { continue }
        $library = $deps.libraries[$key]
        if (!$library) { throw "Missing runtime dependency metadata for $key." }
        if ($library.type -in 'package', 'runtimepack') { $null = Add-NuGetComponent $key $library.type $library.sha512 }
    }
}

$payloadDigest = (Get-FileHash -LiteralPath (Join-Path $Payload 'libDatachannel/datachannel.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
if ($sourceNative) {
    if ($payloadDigest -cne $sourceNative.BinarySha256) { throw 'The packaged native DLL does not match its source bundle.' }
    foreach ($nativeComponent in $sourceNative.Components) {
        $component = [ordered]@{
            'bom-ref'=$nativeComponent.Purl; type='library'; name=$nativeComponent.Name
            version=$nativeComponent.Version; purl=$nativeComponent.Purl; scope=$nativeComponent.Scope
            externalReferences=@([ordered]@{type='vcs';url=$nativeComponent.Repository})
            properties=@(
                [ordered]@{name='iconnect:source:commit';value=$nativeComponent.Commit}
                [ordered]@{name='iconnect:source:archive:path';value=('PackageMetadata/NativeTransport/' + $nativeComponent.SourceArchive)}
                [ordered]@{name='iconnect:source:archive:sha256';value=$nativeComponent.SourceArchiveSha256}
                [ordered]@{name='iconnect:license:root:path';value=('PackageMetadata/NativeTransport/' + $nativeComponent.RootLicense)}
                [ordered]@{name='iconnect:license:root:sha256';value=$nativeComponent.RootLicenseSha256}
            )
        }
        if ($nativeComponent.Name -ceq 'libdatachannel') {
            $component.hashes=@([ordered]@{alg='SHA-256';content=$payloadDigest})
            $component.properties+=@([ordered]@{name='iconnect:payload:libDatachannel/datachannel.dll:sha256';value=$payloadDigest})
            foreach ($patch in $nativeComponent.SourcePatches) {
                $prefix = 'iconnect:source:patch:' + $patch.id
                $component.properties+=@(
                    [ordered]@{name='iconnect:source:patch:id';value=$patch.id}
                    [ordered]@{name=($prefix + ':sha256');value=$patch.patchSha256}
                    [ordered]@{name=($prefix + ':path');value=('PackageMetadata/NativeTransport/recipe/' + $patch.patchFile)}
                    [ordered]@{name=($prefix + ':postimage:normalization');value='UTF-8 with LF'}
                )
                foreach ($name in @($patch.postImages.Keys | Sort-Object -CaseSensitive)) {
                    $component.properties+=@([ordered]@{name=($prefix + ':postimage:' + $name + ':sha256');value=$patch.postImages[$name]})
                }
            }
        }
        $components[$nativeComponent.Purl]=$component
    }
} else {
    # This P/Invoke DLL is a build asset outside the .deps graph. Do not label a
    # substituted binary as the restored vendor package merely by hashing it.
    $assets = Get-Content -LiteralPath $ConnectivityAssets -Raw | ConvertFrom-Json -AsHashtable
    $native = @($assets.libraries.GetEnumerator() | Where-Object { $_.Key -match '^MediaToolkit\.WebRtc\.Native\.win-x64/' -and $_.Value.type -eq 'package' })
    if ($native.Count -ne 1 -or !$native[0].Value.sha512 -or $native[0].Value.path -cne $native[0].Key.ToLowerInvariant()) { throw 'The native WebRTC package must have exactly one resolved version, path and content hash.' }
    $packageHashes = @($assets.packageFolders.Keys | ForEach-Object {
        $candidate = Join-Path $_ ($native[0].Value.path + '/native/win-x64/datachannel.dll')
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash.ToLowerInvariant() }
    } | Sort-Object -Unique)
    if ($packageHashes.Count -ne 1 -or $packageHashes[0] -cne $payloadDigest) { throw 'The native DLL differs from the resolved NuGet artifact and has no matching source bundle.' }
    $rtc = Add-NuGetComponent $native[0].Key 'package' $native[0].Value.sha512
    $rtc.properties = @([ordered]@{ name = 'iconnect:payload:libDatachannel/datachannel.dll:sha256'; value = $payloadDigest })
}

$bom = [ordered]@{
    '$schema' = 'http://cyclonedx.org/schema/bom-1.6.schema.json'
    bomFormat = 'CycloneDX'
    specVersion = '1.6'
    version = 1
    metadata = [ordered]@{ component = [ordered]@{ type = 'application'; name = $PackageName; version = $Version } }
    components = @($components.Keys | Sort-Object -CaseSensitive | ForEach-Object { $components[$_] })
}
if ($sourceNative) {
    $bom.metadata.properties=@(
        [ordered]@{name='iconnect:native:origin';value='pinned-source-bundle'}
        [ordered]@{name='iconnect:native:source-lock:sha256';value=$sourceNative.Manifest.sourceLockSha256}
        [ordered]@{name='iconnect:native:bundle-evidence:sha256';value=(Get-FileHash -LiteralPath (Join-Path $nativeMetadata 'bundle-evidence.json') -Algorithm SHA256).Hash.ToLowerInvariant()}
    )
}
$directory = Join-Path $Payload 'PackageMetadata'
[void][IO.Directory]::CreateDirectory($directory)
$temporaryInventory = Join-Path $directory ('sbom-' + [guid]::NewGuid().ToString('N') + '.partial')
$bom | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $temporaryInventory -Encoding utf8
[IO.File]::Move($temporaryInventory,$inventoryPath,$false)
