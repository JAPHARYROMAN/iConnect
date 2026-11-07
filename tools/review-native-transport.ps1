#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BundleDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
function Hash([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
$bundle = (Resolve-Path -LiteralPath $BundleDirectory).Path
$manifestPath = Join-Path $bundle 'bundle-evidence.json'
if ((Get-Item -LiteralPath $manifestPath).Length -gt 1MB) { throw 'Native bundle evidence is oversized.' }
$evidence = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
$lockPath = Join-Path $PSScriptRoot 'native-transport-sources.json'
$lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json -AsHashtable
$expected = @{ libdatachannel=$lock.libdatachannel.commit; openssl=$lock.openssl.commit }
foreach ($module in $lock.libdatachannel.submodules.GetEnumerator()) { $expected[$module.Key.Substring('deps/'.Length)] = $module.Value }
if ($lock.schema -ne 1 -or $expected.Count -ne 7 -or $evidence.schema -ne 1 -or $evidence.stage -cne 'SourceAndNoticeBundle' -or
    $evidence.sourceLockSha256 -cne (Hash $lockPath) -or $evidence.binarySha256 -cnotmatch '\A[a-f0-9]{64}\z' -or
    (Hash (Join-Path $bundle 'datachannel.dll')) -cne $evidence.binarySha256 -or $evidence.components.Count -ne 7) {
    throw 'Choose a completed native source bundle matching the current pinned inputs and binary.'
}
$seen = @{}
foreach ($component in $evidence.components) {
    if ($component.name -isnot [string] -or !$expected.ContainsKey($component.name) -or $seen.ContainsKey($component.name) -or
        $component.commit -cne $expected[$component.name] -or $component.commit -cnotmatch '\A[a-f0-9]{40}\z' -or
        $component.sourceArchive -cne ('sources/' + $component.name + '.zip') -or $component.sourceArchiveSha256 -cnotmatch '\A[a-f0-9]{64}\z' -or
        (Hash (Join-Path $bundle $component.sourceArchive)) -cne $component.sourceArchiveSha256) {
        throw 'A native source component is duplicated, unpinned or changed.'
    }
    $seen[$component.name] = $true
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Choose a new advisory output directory; existing evidence is preserved.' }
[void](New-Item -Path $output -ItemType Directory)
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $output 'bundle-evidence.json')
$components = @($evidence.components | Sort-Object name -CaseSensitive)
$request = @{ queries = @($components | ForEach-Object { @{ commit=$_.commit } }) } | ConvertTo-Json -Depth 5 -Compress
$request | Set-Content -LiteralPath (Join-Path $output 'osv-request.json') -Encoding UTF8
# Only the seven public source commits are sent to OSV; local paths, payload and
# source archives stay on this computer. This lookup does not approve a release.
$endpoint = 'https://api.osv.dev/v1/querybatch'
$response = Invoke-WebRequest -Uri $endpoint -Method Post -ContentType 'application/json' -Body $request -TimeoutSec 30 -MaximumRedirection 0
if ($response.Content.Length -gt 4MB) { throw 'OSV response is oversized.' }
$response.Content | Set-Content -LiteralPath (Join-Path $output 'osv-response.json') -Encoding UTF8
$reply = $response.Content | ConvertFrom-Json -AsHashtable
if ($reply.results -isnot [array] -or $reply.results.Count -ne $components.Count) { throw 'OSV returned an incomplete native advisory response.' }
$findings = @()
for ($i=0; $i -lt $components.Count; $i++) {
    $result = $reply.results[$i]
    if ($result -isnot [System.Collections.IDictionary] -or $result.next_page_token -or $result.Contains('error')) { throw 'OSV returned an incomplete native advisory result.' }
    foreach ($advisory in $result.vulns) {
        if ($advisory.id -isnot [string] -or $advisory.id -cnotmatch '\A[A-Za-z0-9_.-]{1,128}\z') { throw 'OSV returned an invalid advisory identifier.' }
        $findings += [ordered]@{
            component=$components[$i].name; commit=$components[$i].commit
            advisory=$advisory.id; url=('https://osv.dev/vulnerability/' + $advisory.id)
        }
    }
}
if ((Hash $manifestPath) -cne (Hash (Join-Path $output 'bundle-evidence.json'))) { throw 'Native bundle evidence changed during review.' }
if ((Hash (Join-Path $bundle 'datachannel.dll')) -cne $evidence.binarySha256) { throw 'Native bundle payload changed during review.' }
$report = [ordered]@{
    schema=1; stage='NativeCommitAdvisoryLookup'; source=$endpoint
    binarySha256=$evidence.binarySha256; sourceLockSha256=$evidence.sourceLockSha256
    bundleEvidenceSha256=(Hash $manifestPath); checkedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')
    checkedCommits=$components.Count; components=@($components | ForEach-Object { @{ name=$_.name; commit=$_.commit } })
    findings=$findings; completeSecurityReview=$false; productionReleaseApproved=$false
}
$marker = Join-Path $output 'native-advisory-review.json.partial'
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $marker -Encoding UTF8
[IO.File]::Move($marker,(Join-Path $output 'native-advisory-review.json'),$false)
[pscustomobject]@{ Report=(Join-Path $output 'native-advisory-review.json'); CheckedCommits=$components.Count; Findings=$findings.Count }
if ($findings.Count) { exit 2 }
