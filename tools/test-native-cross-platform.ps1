#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^emulator-[0-9]+$')][string]$Serial,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_.-]+$')][string]$ExpectedAvdName,
    [Parameter(Mandatory)][string]$NativeBundle,
    [Parameter(Mandatory)][string]$TestApk,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{64}$')][string]$ExpectedApplicationSha256,
    [ValidateSet('frames','uploads')][string]$Suite='frames',
    [string]$FixtureDirectory,
    [string]$Adb=(Join-Path $env:LOCALAPPDATA 'Android/Sdk/platform-tools/adb.exe'),
    [string]$Dotnet=(Join-Path $env:USERPROFILE '.cache/iconnect/dotnet/dotnet.exe')
)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$adbPath=(Resolve-Path -LiteralPath $Adb).Path
$dotnetPath=(Resolve-Path -LiteralPath $Dotnet).Path
$testApkPath=(Resolve-Path -LiteralPath $TestApk).Path
$native=& (Join-Path $PSScriptRoot 'read-native-transport-bundle.ps1') -BundleDirectory $NativeBundle
$built=if($FixtureDirectory) {(Resolve-Path -LiteralPath $FixtureDirectory).Path} else {Join-Path $repo 'tests/IConnect.NativeInterop.Fixture/bin/Release/net10.0'}
$assembly='IConnect.NativeInterop.Fixture.dll'
if (!(Test-Path -LiteralPath (Join-Path $built $assembly))) {throw 'Build IConnect.NativeInterop.Fixture in Release first.'}
$outputs=@(Get-Item -LiteralPath $built) + @(Get-ChildItem -LiteralPath $built -Force -Recurse)
if (@($outputs | Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count) {throw 'Redirected fixture output requires inspection.'}
function Adb([string[]]$Arguments) {
    $value=& $adbPath -s $Serial @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {throw "ADB command failed: $($value -join ' ')"}
    ($value -join "`n").Trim()
}
function Owned {
    if (((Adb -Arguments @('emu','avd','name')) -split '\r?\n')[0].Trim() -cne $ExpectedAvdName) {throw 'The disposable emulator identity does not match. No application was stopped.'}
}
function AppPid {
    $value=& $adbPath -s $Serial shell pidof com.iconnect.proof 2>$null
    if ($LASTEXITCODE -eq 0) {return ($value -join '').Trim()}
    return ''
}
function Start-Child([string]$Executable,[string[]]$Arguments) {
    $info=[Diagnostics.ProcessStartInfo]::new($Executable)
    $info.UseShellExecute=$false; $info.CreateNoWindow=$true; $info.WorkingDirectory=$repo
    $info.RedirectStandardInput=$true; $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true
    foreach ($argument in $Arguments) {[void]$info.ArgumentList.Add($argument)}
    $process=[Diagnostics.Process]::new(); $process.StartInfo=$info; [void]$process.Start()
    return $process
}
function Host-Command($Process,[hashtable]$Message) {
    $Process.StandardInput.WriteLine(($Message | ConvertTo-Json -Depth 6 -Compress)); $Process.StandardInput.Flush()
}
function Phone-Command([hashtable]$Bridge,[hashtable]$Message) {
    $Message.sequence=$Bridge.Sequence
    $json=$Message | ConvertTo-Json -Depth 6 -Compress
    $encoded=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($json)).TrimEnd('=').Replace('+','-').Replace('/','_')
    $path='files/native-transport-probes/' + $Bridge.Run + '/' + $Bridge.Sequence + '.command'
    $command="run-as com.iconnect.proof sh -c 'cat > $path.partial && mv $path.partial $path'"
    $writer=Start-Child $adbPath @('-s',$Serial,'shell','-T',$command)
    try {
        $errors=$writer.StandardError.ReadToEndAsync(); $output=$writer.StandardOutput.ReadToEndAsync()
        $writer.StandardInput.Write($encoded); $writer.StandardInput.Close()
        if (!$writer.WaitForExit(10000)) {throw 'Phone signaling write timed out.'}
        if ($writer.ExitCode -ne 0) {throw "Phone signaling write failed: $($errors.GetAwaiter().GetResult())"}
        [void]$output.GetAwaiter().GetResult(); $Bridge.Sequence++
    } finally {if (!$writer.HasExited) {$writer.Kill()}; $writer.Dispose()}
}
Owned
if (AppPid) {throw 'Finish existing instrumentation and stop the idle application on the disposable emulator before this probe.'}
$report=Join-Path $repo ('.tools/native-cross-platform/' + [guid]::NewGuid().ToString('N'))
[void](New-Item -Path $report -ItemType Directory)
$app=Adb -Arguments @('shell','pm','path','com.iconnect.proof')
if ($app -cnotmatch '\Apackage:(/data/app/[-A-Za-z0-9_./+=~]+/base\.apk)\z') {throw 'Unexpected installed application path.'}
$installed=Join-Path $report 'installed-application.apk'
[void](Adb -Arguments @('pull',$Matches[1],$installed))
if ((Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash.ToLowerInvariant() -cne $ExpectedApplicationSha256) {throw 'Installed application bytes do not match the expected proof.'}
[void](Adb -Arguments @('install','-r','-t',$testApkPath))
$probe=if($Suite -ceq 'uploads') {'NativeUploadProbe'} else {'NativeTransportProbe'}
$component='com.iconnect.proof.test/com.iconnect.proof.'+$probe
if (!(Adb -Arguments @('shell','pm','list','instrumentation')).Contains($component)) {throw 'The supplied test APK does not contain the selected native probe.'}
$payloadBytes=if($Suite -ceq 'uploads') {2097169} else {262144}
$results=@()
foreach ($role in @('windows','android')) {
    Owned
    if (AppPid) {throw 'Another application process started before the next fixture role.'}
    $roleRoot=Join-Path $report $role
    [void](New-Item -Path $roleRoot -ItemType Directory)
    $hostDirectory=Join-Path $roleRoot 'host'
    [void](New-Item -Path $hostDirectory -ItemType Directory)
    Get-ChildItem -LiteralPath $built -Force | Copy-Item -Destination $hostDirectory -Recurse
    Copy-Item -LiteralPath (Join-Path $native.Directory 'datachannel.dll') -Destination (Join-Path $hostDirectory 'libDatachannel/datachannel.dll') -Force
    $bytes=[Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
    $run=[Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
    $bridge=@{Run=$run;Sequence=0}
    $hostProcess=$null; $phoneProcess=$null
    $hostWriter=[IO.StreamWriter]::new((Join-Path $roleRoot 'windows.log'),$false,[Text.UTF8Encoding]::new($false)); $hostWriter.AutoFlush=$true
    $phoneWriter=[IO.StreamWriter]::new((Join-Path $roleRoot 'android.log'),$false,[Text.UTF8Encoding]::new($false)); $phoneWriter.AutoFlush=$true
    $phoneStarted=$false
    try {
        $hostArguments=@('exec',(Join-Path $hostDirectory $assembly),'--run',$run,'--role',$role,'--native-sha',$native.BinarySha256,'--directory',(Join-Path $roleRoot 'host-data'))
        if($Suite -ceq 'uploads') {$hostArguments+=@('--suite','uploads')}
        $hostProcess=Start-Child $dotnetPath $hostArguments
        $hostError=$hostProcess.StandardError.ReadToEndAsync()
        $initial=$hostProcess.StandardOutput.ReadLineAsync()
        if (!$initial.Wait(30000)) {throw 'Windows fixture did not publish its identity.'}
        $identityLine=$initial.GetAwaiter().GetResult(); $hostWriter.WriteLine($identityLine)
        $identity=$identityLine | ConvertFrom-Json -AsHashtable
        if ($identity.event -cne 'identity' -or $identity.run -cne $run -or $identity.role -cne $role) {throw 'Unexpected Windows fixture identity.'}
        $phoneProcess=Start-Child $adbPath @('-s',$Serial,'shell','am','instrument','-w','-r','-e','run',$run,'-e','role',$role,'-e','hostKey',$identity.key,$component)
        $phoneStarted=$true; $phoneError=$phoneProcess.StandardError.ReadToEndAsync()
        $hostRead=$hostProcess.StandardOutput.ReadLineAsync(); $phoneRead=$phoneProcess.StandardOutput.ReadLineAsync()
        $phoneVerified=$null; $hostVerified=$null; $phoneFinal=$null; $hostSent=$false; $phoneSent=$false; $checking=$false; $finishing=$false; $phoneSuccess=$false
        $hostRevoked=$false; $phoneRevoked=$false; $revoking=$false; $started=$false
        $hostCheckpoints=0; $phoneCheckpoints=0
        $deadline=[DateTime]::UtcNow.AddSeconds($(if($Suite -ceq 'uploads') {280} else {160}))
        while ([DateTime]::UtcNow -lt $deadline) {
            if ($hostRead -and $hostRead.IsCompleted) {
                $line=$hostRead.GetAwaiter().GetResult()
                if ($null -eq $line) {
                    $hostRead=$null
                    if (!$finishing) {throw "Windows fixture exited early: $($hostError.GetAwaiter().GetResult())"}
                } else {
                    $hostWriter.WriteLine($line)
                    if ($line.Length -gt 131072) {throw 'Oversized host fixture event.'}
                    $event=$line | ConvertFrom-Json -AsHashtable
                    switch ($event.event) {
                        'signal' {Phone-Command $bridge @{op='signal';kind=$event.kind;body=$event.body;mid=$event.mid}}
                        'authenticated' {Host-Command $hostProcess @{op='send'}}
                        'sent' {
                            if($event.bytes -ne $payloadBytes) {throw 'The host sent an unexpected payload length.'}
                            $hostSent=$true
                        }
                        'verified' {$hostVerified=$event}
                        'bound' {
                            if ($event.nativeSha256 -cne $native.BinarySha256) {throw 'Host loaded the wrong native payload.'}
                            if($Suite -ceq 'uploads') {Phone-Command $bridge @{op='pairing';pairing=$event.pairing}}
                        }
                        'checkpoint' {
                            $hostCheckpoints++
                            if($Suite -cne 'uploads' -or $hostCheckpoints -ne 1 -or $event.senderOffset -ne 0 -or $event.receiverOffset -ne 32768 -or $event.receipt -cnotmatch '^[A-Za-z0-9_-]{43}$') {throw 'Unexpected host durable checkpoint.'}
                        }
                        'revoked' {if($Suite -cne 'uploads') {throw 'Unexpected inbox withdrawal.'}; $hostRevoked=$true}
                        'report' {}
                        default {throw 'Unexpected host fixture event.'}
                    }
                    $hostRead=$hostProcess.StandardOutput.ReadLineAsync()
                }
            }
            if ($phoneRead -and $phoneRead.IsCompleted) {
                $line=$phoneRead.GetAwaiter().GetResult()
                if ($null -eq $line) {$phoneRead=$null} else {
                    $phoneWriter.WriteLine($line)
                    if ($line.StartsWith('INSTRUMENTATION_STATUS: nativeProbe=')) {
                        $text=$line.Substring('INSTRUMENTATION_STATUS: nativeProbe='.Length)
                        $json=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($text.Replace('-','+').Replace('_','/') + ('=' * ((4-$text.Length%4)%4))))
                        $event=$json | ConvertFrom-Json -AsHashtable
                        switch ($event.event) {
                            'identity' {
                                $binding=@{op='peer';key=$event.key}
                                if($Suite -ceq 'uploads') {$binding.pairing=$event.pairing}
                                Host-Command $hostProcess $binding
                            }
                            'signal' {Host-Command $hostProcess @{op='signal';kind=$event.kind;body=$event.body;mid=$event.mid}}
                            'authenticated' {}
                            'bound' {
                                if($Suite -cne 'uploads' -or $started -or $event.peer -cne $identity.device) {throw 'Unexpected Android owner binding.'}
                                Host-Command $hostProcess @{op='start'}; Phone-Command $bridge @{op='start'}; $started=$true
                            }
                            'checkpoint' {
                                $phoneCheckpoints++
                                if($Suite -cne 'uploads' -or $phoneCheckpoints -ne 1 -or $event.senderOffset -ne 0 -or $event.receiverOffset -ne 32768 -or $event.receipt -cnotmatch '^[A-Za-z0-9_-]{43}$') {throw 'Unexpected Android durable checkpoint.'}
                            }
                            'sent' {if($Suite -cne 'uploads' -or $event.bytes -ne $payloadBytes) {throw 'Unexpected Android send result.'}; $phoneSent=$true}
                            'revoked' {if($Suite -cne 'uploads') {throw 'Unexpected Android inbox withdrawal.'}; $phoneRevoked=$true}
                            'verified' {$phoneVerified=$event}
                            default {throw 'Unexpected Android fixture event.'}
                        }
                    } elseif ($line.StartsWith('INSTRUMENTATION_RESULT: nativeTransportResult=')) {
                        $phoneFinal=$line.Substring('INSTRUMENTATION_RESULT: nativeTransportResult='.Length) | ConvertFrom-Json -AsHashtable
                    } elseif ($line -match '^INSTRUMENTATION_CODE:\s*-1\s*$') {$phoneSuccess=$true}
                    $phoneRead=$phoneProcess.StandardOutput.ReadLineAsync()
                }
            }
            if($Suite -ceq 'uploads') {
                if($hostSent -and $phoneSent -and !$revoking) {Host-Command $hostProcess @{op='revoke'}; Phone-Command $bridge @{op='revoke'}; $revoking=$true}
                if($hostRevoked -and $phoneRevoked -and !$checking) {Host-Command $hostProcess @{op='check'}; Phone-Command $bridge @{op='check'}; $checking=$true}
            } elseif ($hostSent -and $phoneVerified -and !$checking) {Host-Command $hostProcess @{op='check'}; $checking=$true}
            if ($hostVerified -and $phoneVerified -and !$finishing) {
                if ($hostVerified.sentHash -cne $phoneVerified.receivedHash -or $hostVerified.receivedHash -cne $phoneVerified.sentHash -or
                    $hostVerified.bytes -ne $payloadBytes -or $phoneVerified.bytes -ne $payloadBytes -or $phoneVerified.run -cne $run -or $phoneVerified.role -cne $role) {throw 'Bidirectional native file hashes did not match.'}
                if($Suite -ceq 'uploads') {
                    if($hostCheckpoints -ne 1 -or $phoneCheckpoints -ne 1 -or !$hostRevoked -or !$phoneRevoked) {throw 'Both durable checkpoints and owner withdrawals are required.'}
                    foreach($proof in @($hostVerified,$phoneVerified)) {
                        if($proof.suite -cne 'uploads' -or $proof.preparations -ne 1 -or $proof.lostAcknowledgements -ne 1) {throw 'Upload engine preparation/recovery counts differ.'}
                        foreach($field in @('resumedOriginalReceipt','collisionOriginalKept','originalSourceKept','publishedCopyChecked','withdrawnInboxDenied')) {
                            if($proof[$field] -cne $true) {throw ('Upload engine verification is incomplete: '+$field)}
                        }
                    }
                }
                Host-Command $hostProcess @{op='finish'}
                Phone-Command $bridge @{op='finish'}
                $finishing=$true
            }
            if (!$phoneRead -and $phoneProcess.HasExited) {break}
            Start-Sleep -Milliseconds 10
        }
        if (!$finishing -or !$phoneSuccess -or !$phoneFinal -or !$phoneProcess.WaitForExit(10000) -or $phoneProcess.ExitCode -ne 0 -or
            !$hostProcess.WaitForExit(10000) -or $hostProcess.ExitCode -ne 0 -or $phoneFinal.sentHash -cne $phoneVerified.sentHash -or
            $phoneFinal.receivedHash -cne $phoneVerified.receivedHash -or $phoneFinal.run -cne $run -or $phoneFinal.role -cne $role -or
            $phoneFinal.bytes -ne $payloadBytes) {throw 'Native transport fixture did not complete successfully on both processes.'}
        $result=@{Role=$role;Run=$run;Suite=$Suite;Host=$hostVerified;Android=$phoneFinal;NativeSha256=$native.BinarySha256}
        $result | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $roleRoot 'result.json') -Encoding utf8
        $results+=$result
        Write-Host "Verified Windows/Android native role '$role', suite '$Suite', with two $payloadBytes-byte files and matching hashes."
    } finally {
        if ($hostProcess) {if (!$hostProcess.HasExited) {$hostProcess.Kill()}; $hostProcess.Dispose()}
        if ($phoneProcess -and !$phoneProcess.HasExited) {Owned; [void](Adb -Arguments @('shell','am','force-stop','com.iconnect.proof')); [void]$phoneProcess.WaitForExit(10000)}
        if ($phoneProcess) {$phoneProcess.Dispose()}
        $hostWriter.Dispose(); $phoneWriter.Dispose()
        if ($phoneStarted) {Owned; [void](Adb -Arguments @('shell','am','force-stop','com.iconnect.proof'))}
    }
}
@{Stage='NativeCrossPlatformVerified';Suite=$Suite;Avd=$ExpectedAvdName;Serial=$Serial;NativeSha256=$native.BinarySha256;ApplicationSha256=$ExpectedApplicationSha256;TestApkSha256=(Get-FileHash -LiteralPath $testApkPath -Algorithm SHA256).Hash.ToLowerInvariant();Roles=$results;OrdinaryUploadEnginesVerified=($Suite -ceq 'uploads');PhysicalNetworkVerified=$false;FullControllerInteropVerified=$false} |
    ConvertTo-Json -Depth 10 | Set-Content (Join-Path $report 'result.json') -Encoding utf8
[pscustomobject]@{Report=(Join-Path $report 'result.json');Roles=$results.Count;NativeSha256=$native.BinarySha256}
