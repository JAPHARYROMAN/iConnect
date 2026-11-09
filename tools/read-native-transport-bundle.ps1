#Requires -Version 7.2
[CmdletBinding()]
param([Parameter(Mandatory)][string]$BundleDirectory)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'native-transport-patches.ps1')
$root = (Resolve-Path -LiteralPath $BundleDirectory).Path
$rootPrefix = $root.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
function Assert-Regular([string]$Relative,[long]$Maximum) {
    $path = [IO.Path]::GetFullPath((Join-Path $root $Relative))
    if (!$path.StartsWith($rootPrefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Native bundle path escapes its root.' }
    $item = Get-Item -LiteralPath $path -Force
    if ($item.PSIsContainer -or $item.Length -lt 1 -or $item.Length -gt $Maximum) { throw "Invalid native bundle file: $Relative" }
    while ($null -ne $item) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Native bundles must not contain redirected files or directories.' }
        if ($item.FullName.TrimEnd([IO.Path]::DirectorySeparatorChar) -ieq $root.TrimEnd([IO.Path]::DirectorySeparatorChar)) { break }
        $item = Get-Item -LiteralPath ([IO.Path]::GetDirectoryName($item.FullName)) -Force
    }
    return $path
}
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Json([string]$Relative,[long]$Maximum) {
    Get-Content -LiteralPath (Assert-Regular $Relative $Maximum) -Raw | ConvertFrom-Json -AsHashtable
}
$manifest = Json 'bundle-evidence.json' 1MB
$lockPath = Join-Path $PSScriptRoot 'native-transport-sources.json'
$lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json -AsHashtable
$lockHash = Hash $lockPath
if ($lock.schema -ne 1 -or $lock.libdatachannel.submodules.Count -ne 5 -or
    $manifest.schema -ne 1 -or $manifest.stage -cne 'SourceAndNoticeBundle' -or
    $manifest.sourceLockSha256 -cne $lockHash -or $manifest.binarySha256 -cnotmatch '\A[a-f0-9]{64}\z' -or
    $manifest.components.Count -ne 7 -or $manifest.files -isnot [Collections.IDictionary]) {
    throw 'Native bundle evidence does not match the current pinned source inputs.'
}
$verified = [Collections.Generic.List[object]]::new()
function Verify([string]$Relative,[string]$Expected,[long]$Maximum) {
    if ($Expected -cnotmatch '\A[a-f0-9]{64}\z') { throw 'Invalid native bundle file hash.' }
    $path = Assert-Regular $Relative $Maximum
    if ((Hash $path) -cne $Expected) { throw "Native bundle file changed: $Relative" }
    $verified.Add([pscustomobject]@{ RelativePath=$Relative; Path=$path; Sha256=$Expected })
}
$build = Json 'build-evidence.json' 64KB
$nativePatches = @(Get-IConnectNativeSourcePatches $build)
$required = @('datachannel.dll','build-evidence.json','source-lock.json','README.txt') +
    @('build-native-transport.ps1','native-transport-sources.json','test-native-transport.ps1',
        'export-native-transport-sources.ps1','review-native-transport.ps1' | ForEach-Object { 'recipe/' + $_ })
$patchRecipe = @('recipe/native-transport-patches.ps1','recipe/native-sctp-initialization.json')
if ($nativePatches.Count) { $required += $patchRecipe + @($nativePatches | ForEach-Object { 'recipe/' + $_.patchFile }) }
$allowed = $required + @('recipe/read-native-transport-bundle.ps1') + $patchRecipe
foreach ($name in $required) { if (!$manifest.files.Contains($name)) { throw "Native bundle is missing $name." } }
foreach ($name in $manifest.files.Keys) {
    if ($name -cnotin $allowed) { throw 'Unexpected native bundle recipe or payload entry.' }
    Verify $name $manifest.files[$name] $(if ($name -ceq 'datachannel.dll') {128MB} else {1MB})
}
if ($manifest.files['datachannel.dll'] -cne $manifest.binarySha256 -or $manifest.files['source-lock.json'] -cne $lockHash -or
    $manifest.files['recipe/native-transport-sources.json'] -cne $lockHash) { throw 'Native bundle payload or source lock is inconsistent.' }
foreach ($patch in $nativePatches) {
    if ($manifest.files['recipe/' + $patch.patchFile] -cne $patch.patchSha256 -or
        $manifest.files['recipe/native-sctp-initialization.json'] -cne (Hash (Join-Path $PSScriptRoot 'native-sctp-initialization.json'))) {
        throw 'Native bundle lacks the pinned patch and modified-source definition.'
    }
}
if ($build.schema -ne 1 -or $build.stage -cne 'BuildOnly' -or $build.architecture -cne 'windows-x64' -or
    $build.runtime -cne 'MSVC static' -or $build.binarySha256 -cne $manifest.binarySha256 -or
    $build.sourceLockSha256 -cne $lockHash -or $build.libdatachannel.commit -cne $lock.libdatachannel.commit -or
    $build.openssl.commit -cne $lock.openssl.commit) { throw 'Native build evidence is inconsistent with the exported candidate.' }
$bytes = [IO.File]::ReadAllBytes((Join-Path $root 'datachannel.dll'))
if ($bytes.Length -lt 64 -or $bytes[0] -ne 77 -or $bytes[1] -ne 90) { throw 'Invalid native PE image.' }
$pe = [BitConverter]::ToInt32($bytes,60)
if ($pe -lt 64 -or $pe -gt $bytes.Length-24 -or [BitConverter]::ToUInt32($bytes,$pe) -ne 0x4550 -or
    [BitConverter]::ToUInt16($bytes,$pe+4) -ne 0x8664) { throw 'Native bundle requires a Windows x64 PE image.' }
$specs = @(
    @{name='libdatachannel'; repository=$lock.libdatachannel.repository; commit=$lock.libdatachannel.commit; license='LICENSE'; version=$lock.libdatachannel.tag.Substring(1)}
    @{name='openssl'; repository=$lock.openssl.repository; commit=$lock.openssl.commit; license='LICENSE.txt'; version=$lock.openssl.tag.Substring('openssl-'.Length)}
    @{name='libjuice'; repository='https://github.com/paullouisageneau/libjuice.git'; license='LICENSE'}
    @{name='libsrtp'; repository='https://github.com/cisco/libsrtp.git'; license='LICENSE'}
    @{name='usrsctp'; repository='https://github.com/paullouisageneau/usrsctp.git'; license='LICENSE.md'}
    @{name='plog'; repository='https://github.com/SergiusTheBest/plog.git'; license='LICENSE'}
    @{name='json'; repository='https://github.com/nlohmann/json.git'; license='LICENSE.MIT'}
)
$components = @()
foreach ($spec in $specs) {
    if (!$spec.commit) { $spec.commit=$lock.libdatachannel.submodules['deps/' + $spec.name]; $spec.version=$spec.commit }
    $entries = @($manifest.components | Where-Object { $_.name -ceq $spec.name })
    if ($entries.Count -ne 1) { throw 'Native source inventory is duplicated or incomplete.' }
    $component = $entries[0]
    $archive = 'sources/' + $spec.name + '.zip'
    $notice = 'notices/' + $spec.name + '-' + $spec.license
    if ($component.commit -cne $spec.commit -or $component.repository -cne $spec.repository -or
        $component.sourceArchive -cne $archive -or $component.rootLicense -cne $notice) { throw 'Native source coordinates or notice paths changed.' }
    Verify $archive $component.sourceArchiveSha256 512MB
    Verify $notice $component.rootLicenseSha256 1MB
    $repositoryPath = ([uri]$spec.repository).AbsolutePath.Trim('/')
    $repositoryName = $repositoryPath.Substring(0,$repositoryPath.Length-4).ToLowerInvariant()
    $components += [pscustomobject]@{
        Name=$spec.name; Version=$spec.version; Commit=$spec.commit; Repository=$spec.repository
        Purl=('pkg:github/' + $repositoryName + '@' + $spec.commit)
        Scope=if ($spec.name -eq 'json') {'excluded'} else {'required'}
        SourceArchive=$archive; SourceArchiveSha256=$component.sourceArchiveSha256
        RootLicense=$notice; RootLicenseSha256=$component.rootLicenseSha256
        SourcePatches=@($nativePatches | Where-Object { $spec.name -ceq 'libdatachannel' })
    }
}
$manifestPath = Assert-Regular 'bundle-evidence.json' 1MB
$verified.Add([pscustomobject]@{RelativePath='bundle-evidence.json';Path=$manifestPath;Sha256=(Hash $manifestPath)})
[pscustomobject]@{ Directory=$root; Manifest=$manifest; Build=$build; Components=$components; Files=$verified.ToArray(); BinarySha256=$manifest.binarySha256 }
