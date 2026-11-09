#Requires -Version 7.2
[CmdletBinding()]
param([string]$BundleDirectory = $env:ICONNECT_NATIVE_TRANSPORT_BUNDLE)
$ErrorActionPreference = 'Stop'
$selected = & (Join-Path $PSScriptRoot 'assert-native-transport.ps1') -BundleDirectory $BundleDirectory
$source = & (Join-Path $PSScriptRoot 'read-native-transport-bundle.ps1') -BundleDirectory $selected.Directory
$root = Join-Path (Split-Path $PSScriptRoot -Parent) ('.tools/native-selection-verification/' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
$checks = [Collections.Generic.List[string]]::new()
function Expect-Refusal([scriptblock]$Action, [string]$Message, [string]$Check) {
    $refused = $false
    try { & $Action | Out-Null } catch {
        if ($_.Exception.Message -notlike ('*' + $Message + '*')) { throw }
        $refused = $true
    }
    if (!$refused) { throw "Guard accepted an invalid transport: $Check" }
    $checks.Add($Check)
}
function Copy-Bundle([string]$Name) {
    $targetRoot = Join-Path $root $Name
    foreach ($file in $source.Files) {
        $target = Join-Path $targetRoot $file.RelativePath
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
        Copy-Item -LiteralPath $file.Path -Destination $target
    }
    return $targetRoot
}
Expect-Refusal { & (Join-Path $PSScriptRoot 'assert-native-transport.ps1') -BundleDirectory (Join-Path $root 'missing') } 'Prepare the corrected native transport' 'MissingBundle'
$incomplete = Join-Path $root 'dll-only'
[void][IO.Directory]::CreateDirectory($incomplete)
Copy-Item -LiteralPath (Join-Path $source.Directory 'datachannel.dll') -Destination $incomplete
Expect-Refusal { & (Join-Path $PSScriptRoot 'assert-native-transport.ps1') -BundleDirectory $incomplete } 'bundle-evidence.json' 'DllWithoutSourceEvidence'
$mutated = Copy-Bundle 'changed-dll'
$dll = Join-Path $mutated 'datachannel.dll'
$stream = [IO.File]::Open($dll, [IO.FileMode]::Append, [IO.FileAccess]::Write)
try { $stream.WriteByte(1) } finally { $stream.Dispose() }
$changedHash = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
Expect-Refusal { & (Join-Path $PSScriptRoot 'assert-native-transport.ps1') -BundleDirectory $mutated } 'datachannel.dll' 'ChangedBinary'
Expect-Refusal { & (Join-Path $PSScriptRoot 'prepare-native-transport.ps1') -CandidateBundle $source.Directory -Destination $mutated } 'datachannel.dll' 'InvalidExistingDestination'
if ((Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash -cne $changedHash) { throw 'Preparation overwrote an invalid existing destination.' }
$unknown = Copy-Bundle 'unknown-source-lock'
$unknownManifestPath = Join-Path $unknown 'bundle-evidence.json'
$unknownManifest = Get-Content -LiteralPath $unknownManifestPath -Raw | ConvertFrom-Json -AsHashtable
$unknownManifest.sourceLockSha256 = '0' * 64
$unknownManifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $unknownManifestPath -Encoding utf8
Expect-Refusal { & (Join-Path $PSScriptRoot 'assert-native-transport.ps1') -BundleDirectory $unknown } 'known pinned source lock' 'UnknownSourceLock'
$catalog = Copy-Bundle 'changed-historical-catalog'
$catalogRelative = 'recipe/native-source-locks/0246-openssl365.json'
$catalogPath = Join-Path $catalog $catalogRelative
[IO.File]::AppendAllText($catalogPath, ' ')
$catalogManifestPath = Join-Path $catalog 'bundle-evidence.json'
$catalogManifest = Get-Content -LiteralPath $catalogManifestPath -Raw | ConvertFrom-Json -AsHashtable
$catalogManifest.files[$catalogRelative] = (Get-FileHash -LiteralPath $catalogPath -Algorithm SHA256).Hash.ToLowerInvariant()
$catalogManifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $catalogManifestPath -Encoding utf8
Expect-Refusal { & (Join-Path $PSScriptRoot 'assert-native-transport.ps1') -BundleDirectory $catalog } 'historical source-lock catalog changed' 'ChangedHistoricalCatalogDespiteUpdatedManifest'
$extra = Copy-Bundle 'with-unlisted-file'
[IO.File]::WriteAllText((Join-Path $extra 'unlisted-fixture.txt'), 'This file must stay outside the prepared bundle.')
$destination = Join-Path $root 'prepared-cache/bundle'
$prepared = & (Join-Path $PSScriptRoot 'prepare-native-transport.ps1') -CandidateBundle $extra -Destination $destination
if ($prepared.Existing -or $prepared.BinarySha256 -cne $selected.BinarySha256 -or
    (Test-Path -LiteralPath (Join-Path $destination 'unlisted-fixture.txt')) -or
    !(Test-Path -LiteralPath (Join-Path $extra 'unlisted-fixture.txt'))) { throw 'Preparation copied an unlisted file or changed its source.' }
$checks.Add('VerifiedMembersOnly')
$second = & (Join-Path $PSScriptRoot 'prepare-native-transport.ps1') -CandidateBundle $extra -Destination $destination
if (!$second.Existing -or $second.BinarySha256 -cne $selected.BinarySha256) { throw 'Repeated preparation changed the verified selection.' }
$checks.Add('IdempotentPreparation')
$previous = $env:ICONNECT_NATIVE_TRANSPORT_BUNDLE
try {
    $env:ICONNECT_NATIVE_TRANSPORT_BUNDLE = $destination
    $environmentSelection = & (Join-Path $PSScriptRoot 'assert-native-transport.ps1') -BundleDirectory ''
    if ($environmentSelection.Directory -cne $destination) { throw 'An empty optional argument ignored the selected environment bundle.' }
    $checks.Add('EnvironmentSelection')
} finally { $env:ICONNECT_NATIVE_TRANSPORT_BUNDLE = $previous }
$report = [ordered]@{
    Stage='ApplicationNativeSelectionVerified'; BinarySha256=$selected.BinarySha256
    BundleEvidenceSha256=$selected.BundleEvidenceSha256; Checks=$checks.ToArray()
    CheckedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')
}
$reportPath = Join-Path $root 'native-selection-verification.json'
$report | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $reportPath -Encoding utf8
[pscustomobject]@{Stage=$report.Stage; Checks=$checks.Count; BinarySha256=$selected.BinarySha256; Report=$reportPath}
