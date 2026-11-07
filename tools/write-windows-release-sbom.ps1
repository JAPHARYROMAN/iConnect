[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Payload,
    [Parameter(Mandatory)][string]$ConnectivityAssets,
    [Parameter(Mandatory)][string]$PackageName,
    [Parameter(Mandatory)][string]$Version
)

$ErrorActionPreference = 'Stop'
$components = @{}

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
        $library = $deps.libraries[$key]
        if (!$library) { throw "Missing runtime dependency metadata for $key." }
        if ($library.type -in 'package', 'runtimepack') { $null = Add-NuGetComponent $key $library.type $library.sha512 }
    }
}

# The upstream NuGet package supplies this P/Invoke DLL as a build asset, outside the .deps graph.
$assets = Get-Content -LiteralPath $ConnectivityAssets -Raw | ConvertFrom-Json -AsHashtable
$native = @($assets.libraries.GetEnumerator() | Where-Object { $_.Key -match '^MediaToolkit\.WebRtc\.Native\.win-x64/' -and $_.Value.type -eq 'package' })
if ($native.Count -ne 1 -or !$native[0].Value.sha512) { throw 'The native WebRTC package must have exactly one resolved version and content hash.' }
$rtc = Add-NuGetComponent $native[0].Key 'package' $native[0].Value.sha512
$payloadDigest = (Get-FileHash -LiteralPath (Join-Path $Payload 'libDatachannel/datachannel.dll') -Algorithm SHA256).Hash
$rtc.properties = @([ordered]@{ name = 'iconnect:payload:libDatachannel/datachannel.dll:sha256'; value = $payloadDigest.ToLowerInvariant() })

$bom = [ordered]@{
    '$schema' = 'http://cyclonedx.org/schema/bom-1.6.schema.json'
    bomFormat = 'CycloneDX'
    specVersion = '1.6'
    version = 1
    metadata = [ordered]@{ component = [ordered]@{ type = 'application'; name = $PackageName; version = $Version } }
    components = @($components.Keys | Sort-Object -CaseSensitive | ForEach-Object { $components[$_] })
}
$directory = Join-Path $Payload 'PackageMetadata'
[void][IO.Directory]::CreateDirectory($directory)
$bom | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $directory 'sbom.cdx.json') -Encoding utf8
