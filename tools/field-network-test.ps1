#Requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Prepare','Verify')][string]$Mode = 'Prepare',
    [string]$OutputDirectory = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'iConnect field tests'),
    [ValidateRange(1,8192)][int]$SizeMiB = 8,
    [string]$ManifestPath,
    [string]$ReceivedDirectory,
    [ValidateRange(1,2)][int]$BatchPayloadFiles = 1
)
$ErrorActionPreference = 'Stop'

function ChildPath([string]$Base, [string]$Relative) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or $Relative.Length -gt 4096 -or $Relative.Contains('\')) { throw 'Invalid fixture path.' }
    $parts = $Relative.Split('/')
    if ($parts.Count -gt 32) { throw 'Fixture path is too deep.' }
    foreach ($part in $parts) {
        if ($part -in '.', '..' -or $part.Length -eq 0 -or $part.Length -gt 255 -or $part.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) { throw 'Invalid fixture component.' }
    }
    $result = $Base
    foreach ($part in $parts) { $result = [IO.Path]::Combine($result, $part) }
    return $result
}
function NoLinks([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if ([IO.File]::Exists($current) -or [IO.Directory]::Exists($current)) {
            if (([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Choose an ordinary local fixture folder without redirected components.' }
        }
        $parent = [IO.Directory]::GetParent($current)
        $current = if ($parent) { $parent.FullName } else { $null }
    }
}
function FileHash([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

if ($Mode -eq 'Prepare') {
    $base = [IO.Path]::GetFullPath($OutputDirectory)
    NoLinks $base
    $runId = [Guid]::NewGuid().ToString('N')
    $run = [IO.Path]::Combine($base, ('run-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + $runId.Substring(0,8)))
    if ([IO.Directory]::Exists($run) -or [IO.File]::Exists($run)) { throw 'The newly allocated run folder already exists.' }
    $share = [IO.Path]::Combine($run, 'share')
    [void][IO.Directory]::CreateDirectory($share)
    [void][IO.Directory]::CreateDirectory([IO.Path]::Combine($share, 'Empty'))
    [void][IO.Directory]::CreateDirectory([IO.Path]::Combine($share, 'Reports'))
    $encoding = [Text.UTF8Encoding]::new($false)
    [IO.File]::WriteAllText([IO.Path]::Combine($share, '_iconnect-field-run.txt'), $runId, $encoding)
    [IO.File]::WriteAllText([IO.Path]::Combine($share, 'hello.txt'), "iConnect separate-network fixture.`nRun: $runId`n", $encoding)
    $unicodeName = 'Ripoti-' + [char]0x6587 + [char]0x66f8 + '.txt'
    [IO.File]::WriteAllText([IO.Path]::Combine($share, $unicodeName), "Habari! Unicode file preview.`n", $encoding)
    [IO.File]::WriteAllText([IO.Path]::Combine($share, 'Reports', 'notes.txt'), "Nested folder fixture.`n", $encoding)
    [IO.File]::WriteAllBytes([IO.Path]::Combine($share, 'empty.bin'), [byte[]]::new(0))
    $buffer = [byte[]]::new(251 * 256)
    $payloadPaths = @('network.bin')
    if ($BatchPayloadFiles -eq 2) { $payloadPaths += 'Reports/network-2.bin' }
    for ($index = 0; $index -lt $payloadPaths.Count; $index++) {
        for ($i = 0; $i -lt $buffer.Length; $i++) { $buffer[$i] = [byte](($i * 31 + $index) % 251) }
        $binary = [IO.File]::Open((ChildPath $share $payloadPaths[$index]), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            $remaining = [long]$SizeMiB * 1024 * 1024 + 17
            while ($remaining -gt 0) { $count = [int][Math]::Min([long]$buffer.Length, $remaining); $binary.Write($buffer, 0, $count); $remaining -= $count }
            $binary.Flush($true)
        } finally { $binary.Dispose() }
    }
    $files = @(Get-ChildItem -LiteralPath $share -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{ Path = $_.FullName.Substring($share.Length + 1).Replace('\','/'); Size = $_.Length; SHA256 = (FileHash $_.FullName) }
    })
    $expected = [ordered]@{ Schema = 1; RunId = $runId; CreatedUtc = [DateTime]::UtcNow.ToString('O'); Files = $files; Directories = @('Empty','Reports') }
    $manifest = [IO.Path]::Combine($run, 'expected.json')
    [IO.File]::WriteAllText($manifest, ($expected | ConvertTo-Json -Depth 6), $encoding)
    [pscustomobject]@{ RunDirectory = $run; ShareDirectory = $share; ManifestPath = $manifest; Files = $files.Count; Bytes = ($files | Measure-Object -Property Size -Sum).Sum }
    return
}

if ([string]::IsNullOrWhiteSpace($ManifestPath) -or [string]::IsNullOrWhiteSpace($ReceivedDirectory)) { throw 'Verify requires -ManifestPath and -ReceivedDirectory.' }
$manifest = [IO.Path]::GetFullPath($ManifestPath); $received = [IO.Path]::GetFullPath($ReceivedDirectory)
NoLinks $manifest; NoLinks $received
if ((Get-Item -LiteralPath $manifest).Length -gt 2 * 1024 * 1024) { throw 'Expected manifest exceeds its limit.' }
$expected = [IO.File]::ReadAllText($manifest) | ConvertFrom-Json
if ($expected.Schema -ne 1 -or $expected.RunId -notmatch '^[0-9a-f]{32}$' -or @($expected.Files).Count -notin 1..4096 -or @($expected.Directories).Count -gt 4096) { throw 'Invalid expected manifest.' }
$marker = ChildPath $received '_iconnect-field-run.txt'; NoLinks $marker
if (![IO.File]::Exists($marker) -or (Get-Item -LiteralPath $marker).Length -gt 1024 -or [IO.File]::ReadAllText($marker) -ne $expected.RunId) { throw 'Choose this run''s received share folder, containing _iconnect-field-run.txt.' }
$known = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($directory in @($expected.Directories)) {
    $path = ChildPath $received ([string]$directory); NoLinks $path
    if (!$known.Add([string]$directory) -or ![IO.Directory]::Exists($path)) { throw "Missing or repeated fixture directory: $directory" }
}
foreach ($file in @($expected.Files)) {
    $path = ChildPath $received ([string]$file.Path); NoLinks $path
    if (!$known.Add([string]$file.Path) -or $file.SHA256 -notmatch '^[0-9a-f]{64}$' -or [long]$file.Size -lt 0 -or [long]$file.Size -gt 8192L * 1024 * 1024 + 17) { throw 'Invalid expected file.' }
    if (![IO.File]::Exists($path) -or (Get-Item -LiteralPath $path).Length -ne [long]$file.Size -or (FileHash $path) -ne $file.SHA256) { throw "Content mismatch or missing fixture file: $($file.Path)" }
}
$pending = [Collections.Generic.Queue[string]]::new(); $pending.Enqueue($received); $seen = 0
while ($pending.Count -gt 0) {
    foreach ($path in [IO.Directory]::EnumerateFileSystemEntries($pending.Dequeue())) {
        if (++$seen -gt 8192) { throw 'Received fixture exceeds its item limit.' }
        NoLinks $path
        $relative = $path.Substring($received.TrimEnd('\','/').Length + 1).Replace('\','/')
        if (!$known.Contains($relative)) { throw "Unexpected item in the received fixture: $relative" }
        if ([IO.Directory]::Exists($path)) { $pending.Enqueue($path) }
    }
}
[pscustomobject]@{ Verified = $true; RunId = $expected.RunId; Files = @($expected.Files).Count; Directories = @($expected.Directories).Count; Bytes = (@($expected.Files) | Measure-Object -Property Size -Sum).Sum }
