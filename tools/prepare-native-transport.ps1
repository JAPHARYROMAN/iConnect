#Requires -Version 7.2
[CmdletBinding()]
param(
    [string]$CandidateBundle,
    [string]$Destination = (Join-Path $env:USERPROFILE '.cache/iconnect/native-transport/application-connection-init-v2-openssl35/bundle'),
    [string]$BuildDirectory,
    [string]$ReuseOpenSslBuildDirectory,
    [ValidateRange(1,8)][int]$Jobs = 1
)
$ErrorActionPreference = 'Stop'
$destinationPath = [IO.Path]::GetFullPath($Destination).TrimEnd([IO.Path]::DirectorySeparatorChar)
$parent = [IO.Path]::GetDirectoryName($destinationPath)
if (!$parent -or $destinationPath -ieq [IO.Path]::GetPathRoot($destinationPath)) { throw 'Choose a native bundle directory below a cache or workspace.' }
function Assert-NoRedirect([string]$Path) {
    $current = $Path
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (!$item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Native cache directories must be regular directories.' }
        }
        $next = [IO.Path]::GetDirectoryName($current)
        if ($next -ceq $current) { break }; $current = $next
    }
}
Assert-NoRedirect $parent
[void][IO.Directory]::CreateDirectory($parent)
$owner = $null
try {
    $owner = [IO.File]::Open((Join-Path $parent 'prepare-owner.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $existing = $null
    if (Test-Path -LiteralPath $destinationPath) {
        $existing = & (Join-Path $PSScriptRoot 'assert-native-transport.ps1') -BundleDirectory $destinationPath
        if (!$CandidateBundle) {
            [pscustomobject]@{Stage='ApplicationNativeTransportPrepared';Directory=$existing.Directory;BinarySha256=$existing.BinarySha256;Existing=$true}
            return
        }
    }
    if (!$CandidateBundle) {
        if (!$IsWindows) { throw 'Compile the Windows transport on Windows, or supply a validated candidate bundle.' }
        $arguments = @{ConnectionInitializationFixes=$true;Jobs=$Jobs}
        if ($BuildDirectory) { $arguments.BuildDirectory=$BuildDirectory }
        if ($ReuseOpenSslBuildDirectory) { $arguments.ReuseOpenSslBuildDirectory=$ReuseOpenSslBuildDirectory }
        $built = & (Join-Path $PSScriptRoot 'build-native-transport.ps1') @arguments
        if ($built.Stage -cne 'BuildOnly') { throw 'The corrected native build did not complete.' }
        $buildRoot = [IO.Path]::GetDirectoryName($built.Artifact)
        $export = & (Join-Path $PSScriptRoot 'export-native-transport-sources.ps1') -BuildDirectory $buildRoot
        $CandidateBundle = $export.Bundle
    }
    $selected = & (Join-Path $PSScriptRoot 'assert-native-transport.ps1') -BundleDirectory $CandidateBundle
    if ($existing -and $existing.BundleEvidenceSha256 -ceq $selected.BundleEvidenceSha256) {
        [pscustomobject]@{Stage='ApplicationNativeTransportPrepared';Directory=$existing.Directory;BinarySha256=$existing.BinarySha256;Existing=$true}
        return
    }
    $source = & (Join-Path $PSScriptRoot 'read-native-transport-bundle.ps1') -BundleDirectory $selected.Directory
    $prepared = [IO.Path]::GetFullPath((Join-Path $parent ('prepared-'+[guid]::NewGuid().ToString('N'))))
    $prefix = $parent.TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
    if (!$prepared.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $prepared)) { throw 'Unexpected native preparation path.' }
    [void][IO.Directory]::CreateDirectory($prepared)
    foreach ($file in $source.Files) {
        $target = [IO.Path]::GetFullPath((Join-Path $prepared $file.RelativePath))
        if (!$target.StartsWith($prepared+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'A native copy path escapes preparation.' }
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
        Copy-Item -LiteralPath $file.Path -Destination $target
    }
    $ready = & (Join-Path $PSScriptRoot 'assert-native-transport.ps1') -BundleDirectory $prepared
    $unchanged = & (Join-Path $PSScriptRoot 'assert-native-transport.ps1') -BundleDirectory $selected.Directory
    if ($ready.BundleEvidenceSha256 -cne $selected.BundleEvidenceSha256 -or $unchanged.BundleEvidenceSha256 -cne $selected.BundleEvidenceSha256) { throw 'The native bundle changed during preparation.' }
    Assert-NoRedirect $parent
    if ($existing) {
        # Preserve a verified old bundle within this same cache; never delete it.
        $preserved = [IO.Path]::GetFullPath((Join-Path $parent ('preserved-'+[guid]::NewGuid().ToString('N'))))
        if (!$destinationPath.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or !$preserved.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or
            (Test-Path -LiteralPath $preserved)) { throw 'Unexpected native preservation paths.' }
        Assert-NoRedirect $destinationPath
        [IO.Directory]::Move($destinationPath,$preserved)
    } elseif (Test-Path -LiteralPath $destinationPath) { throw 'The native destination appeared during preparation.' }
    [IO.Directory]::Move($prepared,$destinationPath)
    $final = & (Join-Path $PSScriptRoot 'assert-native-transport.ps1') -BundleDirectory $destinationPath
    [pscustomobject]@{Stage='ApplicationNativeTransportPrepared';Directory=$final.Directory;BinarySha256=$final.BinarySha256;Existing=$false}
} finally { if ($owner) { $owner.Dispose() } }
