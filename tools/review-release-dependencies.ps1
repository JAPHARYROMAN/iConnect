[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Package,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$SbomOnly
)

$ErrorActionPreference = 'Stop'
$path = (Resolve-Path -LiteralPath $Package).Path
$entryName = switch ([IO.Path]::GetExtension($path).ToLowerInvariant()) {
    '.apk' { 'assets/iconnect/sbom.cdx.json' }
    '.msix' { 'PackageMetadata/sbom.cdx.json' }
    default { throw 'Choose an iConnect release APK or MSIX.' }
}

$file = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
try {
    $packageHash = (Get-FileHash -InputStream $file -Algorithm SHA256).Hash.ToLowerInvariant()
    $file.Position = 0
    $zip = [IO.Compression.ZipArchive]::new($file, [IO.Compression.ZipArchiveMode]::Read, $true)
    try {
        $entries = @($zip.Entries | Where-Object { $_.FullName -ceq $entryName })
        if ($entries.Count -ne 1 -or $entries[0].Length -lt 1 -or $entries[0].Length -gt 2097152) {
            throw 'The release package must contain exactly one bounded embedded SBOM.'
        }
        $bytes = [byte[]]::new([int]$entries[0].Length)
        $stream = $entries[0].Open()
        try {
            $offset = 0
            while ($offset -lt $bytes.Length) {
                $count = $stream.Read($bytes, $offset, $bytes.Length - $offset)
                if ($count -eq 0) { throw 'The embedded SBOM is incomplete.' }
                $offset += $count
            }
            if ($stream.ReadByte() -ne -1) { throw 'The embedded SBOM exceeds its declared size.' }
        }
        finally { $stream.Dispose() }
    }
    finally { $zip.Dispose() }
}
finally { $file.Dispose() }

$bom = [Text.UTF8Encoding]::new($false, $true).GetString($bytes) | ConvertFrom-Json -AsHashtable
if ($bom.bomFormat -cne 'CycloneDX' -or $bom.specVersion -cne '1.6' -or $bom.metadata.component.type -cne 'application' -or
    !$bom.metadata.component.name -or !$bom.metadata.component.version -or $bom.components.Count -lt 1 -or $bom.components.Count -gt 4096) {
    throw 'The package does not contain a supported iConnect release SBOM.'
}
$purls = @($bom.components | ForEach-Object { $_.purl })
foreach ($purl in $purls) {
    if ($purl -isnot [string] -or $purl.Length -gt 512 -or
        ($purl -cnotmatch '\Apkg:(?:maven/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+|nuget/[a-z0-9_.-]+)@(?:[A-Za-z0-9_.~+-]|%[0-9A-Fa-f]{2})+\z' -and
         $purl -cnotmatch '\Apkg:github/[a-z0-9_.-]+/[a-z0-9_.-]+@[a-f0-9]{40}\z')) {
        throw 'A release component lacks a supported Maven, NuGet or commit-pinned GitHub package URL.'
    }
}
$purls = @($purls | Sort-Object -CaseSensitive -Unique)
if ($purls.Count -ne $bom.components.Count) { throw 'The release SBOM contains duplicate components.' }

$destination = New-Item -Path $OutputDirectory -ItemType Directory
$sbomPath = Join-Path $destination.FullName 'sbom.cdx.json'
[IO.File]::WriteAllBytes($sbomPath, $bytes)
$sbomHash = (Get-FileHash -LiteralPath $sbomPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($SbomOnly) {
    [pscustomobject]@{ PackageSha256 = $packageHash; Sbom = $sbomPath; SbomSha256 = $sbomHash; Components = $purls.Count }
    return
}

# OSV receives only public package URLs or pinned public source commits.
$endpoint = 'https://api.osv.dev/v1/querybatch'
$findings = @()
for ($start = 0; $start -lt $purls.Count; $start += 100) {
    $end = [Math]::Min($start + 99, $purls.Count - 1)
    $batch = $purls[$start..$end]
    $body = @{ queries = @($batch | ForEach-Object {
        if ($_.StartsWith('pkg:github/',[StringComparison]::Ordinal)) { @{commit=$_.Substring($_.Length-40)} }
        else { @{ package = @{ purl = $_ } } }
    }) } | ConvertTo-Json -Depth 5 -Compress
    $reply = Invoke-RestMethod -Uri $endpoint -Method Post -ContentType 'application/json' -Body $body `
        -TimeoutSec 30 -MaximumRedirection 0
    if ($reply.results -isnot [array] -or $reply.results.Count -ne $batch.Count) { throw 'OSV returned an incomplete advisory response.' }
    for ($i = 0; $i -lt $batch.Count; $i++) {
        $result = $reply.results[$i]
        if ($null -eq $result -or $result.next_page_token -or $result.PSObject.Properties.Name -contains 'error') { throw 'OSV returned a partial advisory result.' }
        foreach ($advisory in $result.vulns) {
            if ($advisory.id -notmatch '\A[A-Za-z0-9_.-]{1,128}\z') { throw 'OSV returned an invalid advisory identifier.' }
            $findings += [ordered]@{ purl = $batch[$i]; advisory = $advisory.id; url = 'https://osv.dev/vulnerability/' + $advisory.id }
        }
    }
}

$report = [ordered]@{
    package = [IO.Path]::GetFileName($path)
    packageSha256 = $packageHash
    sbomSha256 = $sbomHash
    checkedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    source = $endpoint
    checkedCoordinates = $purls.Count
    findings = $findings
}
$reportPath = Join-Path $destination.FullName 'dependency-review.json'
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding utf8
[pscustomobject]@{ Report = $reportPath; Sbom = $sbomPath; CheckedCoordinates = $purls.Count; Findings = $findings.Count }
if ($findings.Count -gt 0) { exit 2 }
