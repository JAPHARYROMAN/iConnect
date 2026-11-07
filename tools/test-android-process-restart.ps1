#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^emulator-[0-9]+$')][string]$Serial,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_.-]+$')][string]$ExpectedAvdName,
    [string]$Adb = (Join-Path $env:LOCALAPPDATA 'Android/Sdk/platform-tools/adb.exe')
)
$ErrorActionPreference = 'Stop'
$adbPath = (Resolve-Path -LiteralPath $Adb).Path
$package = 'com.iconnect.proof'
$component = 'com.iconnect.proof.test/com.iconnect.proof.ProcessRestartProbe'

function Invoke-Adb([string[]]$Arguments) {
    $output = & $adbPath -s $Serial @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "ADB failed: $($output -join ' ')" }
    return ($output -join "`n").Trim()
}
function Assert-OwnedEmulator {
    $avd = Invoke-Adb -Arguments @('emu', 'avd', 'name')
    $actual = ($avd -split '\r?\n')[0].Trim()
    if ($actual -cne $ExpectedAvdName) { throw "Expected disposable AVD '$ExpectedAvdName'; found '$actual'. No app was stopped." }
}
function AppPid {
    $output = & $adbPath -s $Serial shell pidof $package 2>$null
    if ($LASTEXITCODE -eq 0) { return ($output -join '').Trim() }
    return ''
}

Assert-OwnedEmulator
if (AppPid) { throw 'The test app is already running. Finish existing tests and stop the idle test app on this disposable emulator before starting this probe.' }
$instrumentation = Invoke-Adb -Arguments @('shell', 'pm', 'list', 'instrumentation')
if (!$instrumentation.Contains($component)) { throw 'Install the debug app and the test APK containing ProcessRestartProbe first.' }

$random = New-Object byte[] 32
$generator = [Security.Cryptography.RandomNumberGenerator]::Create()
try { $generator.GetBytes($random) } finally { $generator.Dispose() }
$run = [Convert]::ToBase64String($random).TrimEnd('=').Replace('+', '-').Replace('/', '_')
$reportBase = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../.tools/android-process-restart'))
$report = Join-Path $reportBase $run
if ([IO.Directory]::Exists($report) -or [IO.File]::Exists($report)) { throw 'The newly allocated report path already exists.' }
[void][IO.Directory]::CreateDirectory($report)
$seedLog = Join-Path $report 'seed.log'
$seedError = Join-Path $report 'seed-error.log'
$readyPath = "files/process-restart-probes/$run/ready.json"
$seedArguments = @('-s', $Serial, 'shell', 'am', 'instrument', '-w', '-r', '-e', 'phase', 'seed', '-e', 'run', $run, $component)
$seed = Start-Process -FilePath $adbPath -ArgumentList $seedArguments -WindowStyle Hidden -PassThru -RedirectStandardOutput $seedLog -RedirectStandardError $seedError
$deadline = [DateTime]::UtcNow.AddMinutes(5)
$ready = $null
while ([DateTime]::UtcNow -lt $deadline) {
    $seed.Refresh()
    if ($seed.HasExited) { throw "The seed runner exited before its checkpoint. Inspect $seedLog and $seedError" }
    $text = $null
    try {
        $candidate = & $adbPath -s $Serial shell run-as $package cat $readyPath 2>$null
        if ($LASTEXITCODE -eq 0) { $text = $candidate -join "`n" }
    } catch { $text = $null }
    if ($text) {
        $ready = $text | ConvertFrom-Json
        break
    }
    Start-Sleep -Milliseconds 250
}
if (!$ready -or $ready.run -cne $run -or $ready.phase -cne 'waiting-for-process-kill' -or
    $ready.checkpoint -ne 1048576 -or $ready.partBytes -ne 1081344 -or $ready.seedPid -le 0) {
    throw "The exact active checkpoint was not observed. No process was stopped. Inspect $report"
}
Assert-OwnedEmulator
$beforePid = AppPid
if ($beforePid -cne [string]($ready.seedPid) -or $seed.HasExited) { throw 'The checkpoint process is no longer the running seed process. No app was stopped.' }
Write-Host "Terminating owned test process $beforePid at checkpoint $($ready.checkpoint), private length $($ready.partBytes)."
[void](Invoke-Adb -Arguments @('shell', 'am', 'force-stop', $package))
if (AppPid) { throw 'The application process did not terminate; no restart result can be claimed.' }
if (!$seed.WaitForExit(10000)) { throw "The seed ADB handle did not close after process termination. Inspect $report" }
$killedAt = [DateTime]::UtcNow.ToString('o')
$ready | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $report 'ready.json') -Encoding UTF8

Assert-OwnedEmulator
$resume = Invoke-Adb -Arguments @('shell', 'am', 'instrument', '-w', '-r', '-e', 'phase', 'resume', '-e', 'run', $run, $component)
$resume | Set-Content -LiteralPath (Join-Path $report 'resume.log') -Encoding UTF8
$resultLine = @($resume -split '\r?\n' | Where-Object { $_ -match '^INSTRUMENTATION_RESULT: processRestartResult=' })
if ($resume -notmatch 'INSTRUMENTATION_CODE:\s*-1(?:\r?\n|$)' -or $resultLine.Count -ne 1) { throw "The fresh-process probe did not report success. Inspect $report" }
$result = ($resultLine[0] -replace '^INSTRUMENTATION_RESULT: processRestartResult=', '') | ConvertFrom-Json
if ($result.run -cne $run -or $result.phase -cne 'completed' -or $result.seedPid -ne $ready.seedPid -or
    $result.resumePid -eq $ready.seedPid -or $result.resumePid -le 0 -or $result.checkpoint -ne $ready.checkpoint -or
    $result.retainedPartBytes -ne $ready.partBytes -or $result.firstResumedOffset -ne $ready.checkpoint -or
    $result.preparationsAfterRestart -ne 0 -or $result.bytes -ne $ready.size -or $result.sha256 -cne $ready.hash -or $result.copies -ne 1) {
    throw "The process/checkpoint/hash evidence did not match. Inspect $report"
}
[ordered]@{ Avd = $ExpectedAvdName; Serial = $Serial; KilledAt = $killedAt; Result = $result } |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $report 'result.json') -Encoding UTF8
Write-Host "Verified a fresh-process resume of $($result.bytes) bytes with one provider copy. Evidence: $report"
