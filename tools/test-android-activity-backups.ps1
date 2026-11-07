#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Serial,
    [Parameter(Mandatory)][string]$ExpectedAvdName,
    [Parameter(Mandatory)][string]$ApplicationApk,
    [Parameter(Mandatory)][string]$TestApk,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{64}$')][string]$ExpectedInstalledSha256,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{64}$')][string]$ExpectedInstalledTestSha256,
    [string]$BaselineApplicationApk,
    [string]$BaselineTestApk,
    [string]$Adb=(Join-Path $env:LOCALAPPDATA 'Android/Sdk/platform-tools/adb.exe')
)
$ErrorActionPreference='Stop'
if($Serial -cnotmatch '^emulator-[0-9]+$' -or [string]::IsNullOrWhiteSpace($ExpectedAvdName)) {throw 'An explicitly named disposable emulator is required.'}
if([bool]$BaselineApplicationApk -ne [bool]$BaselineTestApk) {throw 'Provide both baseline artifacts, or neither.'}
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$report=Join-Path $repo ('.tools/android-activity-backups/'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($report)
$package='com.iconnect.proof'; $runner='com.iconnect.proof.test/androidx.test.runner.AndroidJUnitRunner'
function Hash([string]$Path) {(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()}
function Adb([string[]]$Arguments) {
    $output=& $Adb -s $Serial @Arguments 2>&1
    if($LASTEXITCODE -ne 0) {throw 'ADB did not complete successfully. Preserve the device state and inspect any live instrumentation.'}
    ($output -join "`n").Trim()
}
function Inactive {
    if(((Adb @('emu','avd','name')) -split '\r?\n')[0].Trim() -cne $ExpectedAvdName) {throw 'Disposable AVD identity mismatch.'}
    $activity=Adb @('shell','dumpsys','-t','15','activity','processes',$package)
    if($activity -match '(?m)^\s*Active instrumentation:') {throw 'Instrumentation is active. No install or app stop is permitted.'}
}
function Installed([string]$PackageId,[string]$Expected,[string]$Name) {
    $path=Adb @('shell','pm','path',$PackageId)
    if($path -cnotmatch '\Apackage:(/data/app/[-A-Za-z0-9_./+=~]+/base\.apk)\z') {throw 'Unexpected installed APK path.'}
    $saved=Join-Path $report $Name
    [void](Adb @('pull',$Matches[1],$saved))
    if((Hash $saved) -cne $Expected) {throw 'The installed APK differs from the exact expected artifact.'}
}
function Install([string]$Production,[string]$Tests,[string]$Label) {
    Inactive
    foreach($artifact in $captured) {if((Hash $artifact.Path) -cne $artifact.Sha256) {throw 'A provided artifact changed before installation.'}}
    [void](Adb @('shell','am','force-stop',$package))
    [void](Adb @('install','-r',$Production))
    [void](Adb @('install','-r','-t',$Tests))
    Installed $package (Hash $Production) ($Label+'-production.apk')
    Installed ($package+'.test') (Hash $Tests) ($Label+'-tests.apk')
}
function Run([string]$Name,[string]$Classes,[int]$Expected,[switch]$ExpectedExpiryFailure) {
    Inactive
    $log=Join-Path $report ($Name+'.log')
    & $Adb -s $Serial shell am instrument -w -r -e class $Classes $runner *> $log
    $exitCode=$LASTEXITCODE; $body=Get-Content -LiteralPath $log -Raw
    if($exitCode -ne 0 -or $body -notmatch 'INSTRUMENTATION_CODE:\s*-1(?:\r?\n|$)' -or
        $body -match 'INSTRUMENTATION_FAILED|Process crashed|(?m)^INSTRUMENTATION_STATUS_CODE:\s*-[34]\s*$') {throw "$Name did not complete. Inspect actual instrumentation state before any mutation."}
    if($ExpectedExpiryFailure) {
        if($body -notmatch 'Tests run:\s*1,\s*Failures:\s*1' -or
            $body -notmatch 'expected java\.lang\.IllegalStateException to be thrown, but nothing was thrown' -or
            [regex]::Matches($body,'(?m)^INSTRUMENTATION_STATUS_CODE:\s*-2\s*$').Count -ne 1) {throw 'The baseline did not reproduce the specific missing expiry rejection.'}
    } else {
        $passes=[regex]::Matches($body,'(?m)^INSTRUMENTATION_STATUS_CODE:\s*0\s*$').Count
        if($passes -ne $Expected -or $body -notmatch ('OK\s*\('+$Expected+' tests?\)') -or
            $body -match '(?m)^INSTRUMENTATION_STATUS_CODE:\s*-2\s*$|FAILURES!!!') {throw "$Name lacks its exact $Expected-pass result."}
    }
    Inactive
    [ordered]@{Tests=$Expected;ExpectedExpiryFailure=[bool]$ExpectedExpiryFailure;LogSha256=(Hash $log);CompletedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $report ($Name+'.json')) -Encoding utf8
}
$artifacts=@($ApplicationApk,$TestApk)
if($BaselineApplicationApk) {$artifacts+=@($BaselineApplicationApk,$BaselineTestApk)}
$captured=@($artifacts | ForEach-Object {[ordered]@{Path=(Resolve-Path -LiteralPath $_).Path;Sha256=(Hash $_)}})
try {
    Inactive
    Installed $package $ExpectedInstalledSha256 'initial-production.apk'
    Installed ($package+'.test') $ExpectedInstalledTestSha256 'initial-tests.apk'
    $expiry='com.iconnect.proof.ActivityBackupTest#expiryDuringConfirmationKeepsUnremovedMetadataForAnotherReview'
    if($BaselineApplicationApk) {
        Install $BaselineApplicationApk $BaselineTestApk 'baseline'
        Run 'baseline-expiry' $expiry 1 -ExpectedExpiryFailure
        Installed $package (Hash $BaselineApplicationApk) 'baseline-after-production.apk'
        Installed ($package+'.test') (Hash $BaselineTestApk) 'baseline-after-tests.apk'
    }
    foreach($artifact in $captured) {if((Hash $artifact.Path) -cne $artifact.Sha256) {throw 'A provided artifact changed during validation.'}}
    Install $ApplicationApk $TestApk 'candidate'
    Run 'fixed-expiry' $expiry 1
    $classes='ActivityBackupTest','ActivityBackupJourneyTest','ActivityTest','ActivityUiTest','ConnectionActivityTest','MutationActivityTest'
    Run 'activity-and-lifecycle' (($classes | ForEach-Object {'com.iconnect.proof.'+$_}) -join ',') 34
    Installed $package (Hash $ApplicationApk) 'final-production.apk'
    Installed ($package+'.test') (Hash $TestApk) 'final-tests.apk'
    [ordered]@{Serial=$Serial;Avd=$ExpectedAvdName;Artifacts=$captured;BaselineReproduced=[bool]$BaselineApplicationApk;FocusedCases=34;
        NewBackupCases=12;WindowsUiVerified=$false;PhysicalDeviceVerified=$false;CompletedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')} |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $report 'result.json') -Encoding utf8
    [pscustomobject]@{Report=(Join-Path $report 'result.json');FocusedCases=34;BaselineReproduced=[bool]$BaselineApplicationApk}
} catch {
    [ordered]@{Error=$_.Exception.Message;CapturedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')} | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $report 'failure.json') -Encoding utf8
    throw
}
