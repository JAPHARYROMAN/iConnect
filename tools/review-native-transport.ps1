#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BundleDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
function Hash([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
$native = & (Join-Path $PSScriptRoot 'read-native-transport-bundle.ps1') -BundleDirectory $BundleDirectory
$bundle = $native.Directory
$manifestPath = Join-Path $bundle 'bundle-evidence.json'
$evidence = $native.Manifest
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Choose a new advisory output directory; existing evidence is preserved.' }
[void](New-Item -Path $output -ItemType Directory)
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $output 'bundle-evidence.json')
$components = @($native.Components | Sort-Object Name -CaseSensitive)
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
