#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^emulator-[0-9]+$')][string]$Serial,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_.-]+$')][string]$ExpectedAvdName,
    [Parameter(Mandatory)][string]$NativeBundle,
    [Parameter(Mandatory)][string]$TestApk,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{64}$')][string]$ExpectedApplicationSha256,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{64}$')][string]$ExpectedTestApkSha256,
    [Parameter(Mandatory)][string]$FixtureDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{64}$')][string]$ExpectedFixtureSha256,
    [ValidateSet('both','listing','blind')][string]$InboxMode='both',
    [string]$Adb=(Join-Path $env:LOCALAPPDATA 'Android/Sdk/platform-tools/adb.exe'),
    [string]$Dotnet=(Join-Path $env:USERPROFILE '.cache/iconnect/dotnet/dotnet.exe')
)
$ErrorActionPreference='Stop'
$driver=Join-Path $PSScriptRoot 'test-native-batch-cancellation.ps1'
if((Get-FileHash -LiteralPath $driver -Algorithm SHA256).Hash.ToLowerInvariant() -cne '0bd5c5c99b9125ba3d8ef21d2500a4c0153a40927eca630d1e2de2183d35ec4d') {
    throw 'The preserved batch cancellation driver changed.'
}
$arguments=@{}
foreach($name in @('Serial','ExpectedAvdName','NativeBundle','TestApk','ExpectedApplicationSha256','ExpectedTestApkSha256',
    'FixtureDirectory','ExpectedFixtureSha256','InboxMode','Adb','Dotnet')) {$arguments[$name]=Get-Variable -Name $name -ValueOnly}
$before=[Environment]::GetEnvironmentVariable('ICONNECT_NATIVE_FOLDER_MOVE_PROBE')
try {
    # Only the standalone, generated-data Windows fixture consumes this test setting.
    [Environment]::SetEnvironmentVariable('ICONNECT_NATIVE_FOLDER_MOVE_PROBE','1')
    $run=& $driver @arguments
} finally {
    if($null -eq $before) {Remove-Item -LiteralPath Env:ICONNECT_NATIVE_FOLDER_MOVE_PROBE -ErrorAction SilentlyContinue}
    else {[Environment]::SetEnvironmentVariable('ICONNECT_NATIVE_FOLDER_MOVE_PROBE',$before)}
}
$proof=Get-Content -LiteralPath $run.BaseReport -Raw|ConvertFrom-Json
$count=if($InboxMode -ceq 'both') {4} else {2}
if($run.Roles -ne $count -or @($proof.Roles).Count -ne $count) {throw 'Every selected native folder case is required.'}
foreach($role in $proof.Roles) {
    if($role.Host.originalFolderMoveVerified -cne $true) {throw 'The Windows original must move into recovery, verify there and restore with its complete tree.'}
}
$path=Join-Path (Split-Path -Parent $run.Report) 'folder-move-result.json'
@{Stage='NativeOutgoingFolderMovesVerified';CancellationReport=$run.Report;CancellationReportSha256=(Get-FileHash -LiteralPath $run.Report -Algorithm SHA256).Hash.ToLowerInvariant();BaseReport=$run.BaseReport;Roles=$count;
    NativeSha256=$run.NativeSha256;VerifiedWindowsOriginalMoves=$count;VerifiedWindowsOriginalRestores=$count;
    AndroidSourceMovesVerified=$false;PhysicalNetworkVerified=$false;FullControllerInteropVerified=$false;FullCompletion=$false}|
    ConvertTo-Json|Set-Content -LiteralPath $path -Encoding utf8
[pscustomobject]@{Report=$path;BaseReport=$run.BaseReport;Roles=$count;NativeSha256=$run.NativeSha256}
