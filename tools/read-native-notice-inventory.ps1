#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InventoryDirectory,
    [Parameter(Mandatory)][string]$BundleDirectory
)
$ErrorActionPreference='Stop'
$source=& (Join-Path $PSScriptRoot 'read-native-transport-bundle.ps1') -BundleDirectory $BundleDirectory
$root=(Resolve-Path -LiteralPath $InventoryDirectory).Path
$prefix=$root.TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
function Regular([string]$Relative,[long]$Maximum) {
    $path=[IO.Path]::GetFullPath((Join-Path $root $Relative))
    if(!$path.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) {throw 'A native notice path escapes its inventory.'}
    $item=Get-Item -LiteralPath $path -Force
    if($item.PSIsContainer -or $item.Length -gt $Maximum) {throw 'Invalid native notice file.'}
    while($item) {
        if($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {throw 'Native notice inventories must not contain redirected paths.'}
        if($item.FullName.TrimEnd([IO.Path]::DirectorySeparatorChar) -ieq $root.TrimEnd([IO.Path]::DirectorySeparatorChar)) {break}
        $item=Get-Item -LiteralPath ([IO.Path]::GetDirectoryName($item.FullName)) -Force
    }
    return $path
}
function Hash([string]$Path) {(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()}
$catalog=Regular 'notice-inventory.json' 8MB
$report=Get-Content -LiteralPath $catalog -Raw|ConvertFrom-Json -AsHashtable
if($report.schema -ne 1 -or $report.stage -cne 'NativeNoticeInventory' -or $report.binarySha256 -cne $source.BinarySha256 -or
    $report.bundleEvidenceSha256 -cne (Hash (Join-Path $source.Directory 'bundle-evidence.json')) -or $report.components.Count -ne 7) {throw 'Native notice inventory differs from the reviewed source candidate.'}
foreach($field in @('licenseApproval','distributionApproved','actualLinkerClosureVerified','completeLicenseCoverageVerified')) {
    if($report[$field] -cne $false) {throw 'A notice inventory cannot grant license or distribution approval.'}
}
$files=[Collections.Generic.List[object]]::new()
$names=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
function Verify([string]$Name,[string]$Expected,[long]$Maximum) {
    if($Expected -cnotmatch '^[a-f0-9]{64}$' -or !$names.Add($Name)) {throw 'Invalid or duplicated native notice binding.'}
    $path=Regular $Name $Maximum
    if((Hash $path) -cne $Expected) {throw 'A native notice file changed.'}
    $files.Add([pscustomobject]@{RelativePath=$Name;Path=$path;Sha256=$Expected})
}
Verify 'notice-inventory.json' (Hash $catalog) 8MB
Verify 'SOURCE_NOTICES.txt' $report.sourceNoticesSha256 64MB
foreach($expected in $source.Components) {
    $entries=@($report.components|Where-Object {$_.name -ceq $expected.Name})
    if($entries.Count -ne 1) {throw 'Duplicated or missing native notice component.'}
    $entry=$entries[0]
    if($entry.commit -cne $expected.Commit -or $entry.scope -cne $expected.Scope -or $entry.archiveSha256 -cne $expected.SourceArchiveSha256 -or
        $entry.rootLicenseSha256 -cne $expected.RootLicenseSha256) {throw 'Native notice component binding differs from the source bundle.'}
    foreach($row in $entry.records) {
        if(!$row.Contains('output')) {continue}
        if($row.output -cnotmatch ('^documents/'+[regex]::Escape($expected.Name)+'-[a-f0-9]{64}\.txt$')) {throw 'Unexpected exported notice filename.'}
        Verify $row.output $row.sha256 2MB
    }
}
$actual=@(Get-ChildItem -LiteralPath $root -Force -Recurse)
if(@($actual|Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count -or
    @($actual|Where-Object {!$_.PSIsContainer}).Count -ne $files.Count) {throw 'Unexpected or redirected native notice inventory members.'}
if((Hash $catalog) -cne $files[0].Sha256) {throw 'Native notice inventory changed during validation.'}
[pscustomobject]@{Directory=$root;BinarySha256=$source.BinarySha256;InventorySha256=$files[0].Sha256;Files=$files.ToArray();Report=$report}
