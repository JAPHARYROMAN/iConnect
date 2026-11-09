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
$driver=Join-Path $PSScriptRoot 'test-native-batches-unicode.ps1'
$expected='3fb256676a33456402af5627b73d7bfe9fa94eab8a1b4fe0d48bdee1537323eb'
if((Get-FileHash -LiteralPath $driver -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expected) {throw 'The preserved native batch driver changed.'}
$arguments=@{}
foreach($name in @('Serial','ExpectedAvdName','NativeBundle','TestApk','ExpectedApplicationSha256','ExpectedTestApkSha256',
    'FixtureDirectory','ExpectedFixtureSha256','InboxMode','Adb','Dotnet')) {$arguments[$name]=Get-Variable -Name $name -ValueOnly}
$run=& $driver @arguments
$proof=Get-Content -LiteralPath $run.Report -Raw|ConvertFrom-Json
$count=if($InboxMode -ceq 'both') {4} else {2}
if($run.Roles -ne $count -or @($proof.Roles).Count -ne $count -or $proof.FolderBatchEnginesVerified -cne $true -or
    $proof.PhysicalNetworkVerified -or $proof.FullControllerInteropVerified) {throw 'The complete selected native batch matrix is required.'}
foreach($role in $proof.Roles) {
    foreach($endpoint in @($role.Host,$role.Android)) {
        if($endpoint.publishedBatchChecked -cne $true -or $endpoint.publishedBatchCanceled -cne $true) {
            throw 'Both endpoints must stop a pending batch check, acknowledge resource release and complete a fresh check.'
        }
    }
}
$result=[ordered]@{Stage='NativeBatchCancellationVerified';BaseReport=$run.Report;BaseReportSha256=(Get-FileHash -LiteralPath $run.Report -Algorithm SHA256).Hash.ToLowerInvariant();
    Roles=$count;NativeSha256=$run.NativeSha256;PublishedBatchCancellations=2*$count;FreshPublishedBatchChecks=2*$count;
    PhysicalNetworkVerified=$false;FullControllerInteropVerified=$false;FullCompletion=$false}
$path=Join-Path (Split-Path -Parent $run.Report) 'cancellation-result.json'
$result|ConvertTo-Json|Set-Content -LiteralPath $path -Encoding utf8
[pscustomobject]@{Report=$path;BaseReport=$run.Report;Roles=$count;NativeSha256=$run.NativeSha256}
