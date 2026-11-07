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
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$adbPath=(Resolve-Path -LiteralPath $Adb).Path
$dotnetPath=(Resolve-Path -LiteralPath $Dotnet).Path
$testApkPath=(Resolve-Path -LiteralPath $TestApk).Path
$built=(Resolve-Path -LiteralPath $FixtureDirectory).Path
$assembly='IConnect.NativeInterop.Fixture.dll'
function Hash([string]$Path) {(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()}
if ((Hash $testApkPath) -cne $ExpectedTestApkSha256 -or (Hash (Join-Path $built $assembly)) -cne $ExpectedFixtureSha256) {throw 'The selected test APK or Windows fixture differs from its expected bytes.'}
$outputs=@(Get-Item -LiteralPath $built)+@(Get-ChildItem -LiteralPath $built -Force -Recurse)
if (@($outputs | Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count) {throw 'Redirected fixture output requires inspection.'}
$native=& (Join-Path $PSScriptRoot 'read-native-transport-bundle.ps1') -BundleDirectory $NativeBundle
function Adb([string[]]$Arguments) {
    $value=& $adbPath -s $Serial @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {throw "ADB command failed: $($value -join ' ')"}
    ($value -join "`n").Trim()
}
function Owned {
    if (((Adb -Arguments @('emu','avd','name')) -split '\r?\n')[0].Trim() -cne $ExpectedAvdName) {throw 'The disposable emulator identity does not match. No application was stopped.'}
}
function AppPid {
    $value=& $adbPath -s $Serial shell pidof com.iconnect.proof 2>&1
    $observed=($value -join '').Trim()
    if ($LASTEXITCODE -eq 0 -and $observed -cmatch '^[1-9][0-9]*(\s+[1-9][0-9]*)*$') {return $observed}
    if ($LASTEXITCODE -eq 1 -and !$observed) {return ''}
    throw 'The application process state could not be established. No application was stopped.'
}
function Verify-Installed([string]$Package,[string]$Expected,[string]$Output) {
    $path=Adb -Arguments @('shell','pm','path',$Package)
    if ($path -cnotmatch '\Apackage:(/data/app/[-A-Za-z0-9_./+=~]+/base\.apk)\z') {throw 'Unexpected installed APK path.'}
    [void](Adb -Arguments @('pull',$Matches[1],$Output))
    if ((Hash $Output) -cne $Expected) {throw 'The installed APK differs from its expected bytes.'}
}
function Start-Child([string]$Executable,[string[]]$Arguments) {
    $info=[Diagnostics.ProcessStartInfo]::new($Executable)
    $info.UseShellExecute=$false; $info.CreateNoWindow=$true; $info.WorkingDirectory=$repo
    $info.RedirectStandardInput=$true; $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true
    foreach ($argument in $Arguments) {[void]$info.ArgumentList.Add($argument)}
    $process=[Diagnostics.Process]::new(); $process.StartInfo=$info; [void]$process.Start(); return $process
}
function Host-Command($Process,[hashtable]$Message) {
    $Process.StandardInput.WriteLine(($Message | ConvertTo-Json -Depth 6 -Compress)); $Process.StandardInput.Flush()
}
function Phone-Command([hashtable]$Bridge,[hashtable]$Message) {
    $Message.sequence=$Bridge.Sequence
    $json=$Message | ConvertTo-Json -Depth 6 -Compress
    $encoded=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($json)).TrimEnd('=').Replace('+','-').Replace('/','_')
    $path='files/native-transport-probes/'+$Bridge.Run+'/'+$Bridge.Sequence+'.command'
    $writer=Start-Child $adbPath @('-s',$Serial,'shell','-T',"run-as com.iconnect.proof sh -c 'cat > $path.partial && mv $path.partial $path'")
    try {
        $errors=$writer.StandardError.ReadToEndAsync(); $output=$writer.StandardOutput.ReadToEndAsync()
        $writer.StandardInput.Write($encoded); $writer.StandardInput.Close()
        if (!$writer.WaitForExit(10000)) {throw 'Phone signaling write timed out.'}
        if ($writer.ExitCode -ne 0) {throw "Phone signaling write failed: $($errors.GetAwaiter().GetResult())"}
        [void]$output.GetAwaiter().GetResult(); $Bridge.Sequence++
    } finally {if (!$writer.HasExited) {$writer.Kill()}; $writer.Dispose()}
}
function Check-Sent($Value) {
    if($Value.bytes -ne 2097207 -or $Value.items -ne 6 -or $Value.preparations -ne 1 -or $Value.lostAcknowledgements -ne 1 -or
        $Value.resumedOriginalReceipt -cne $true -or $Value.sha256 -cnotmatch '^[a-f0-9]{64}$' -or @($Value.names).Count -ne 6 -or
        @($Value.names | Where-Object {$_ -isnot [string] -or $_.Length -notin 1..255 -or $_ -match '[\\/:\x00-\x1f]'}).Count) {throw 'Unexpected six-item native send proof.'}
}
function Check-Proof($Value,$Sent,[bool]$Listing) {
    if($Value.event -cne 'verified' -or $Value.suite -cne 'batches' -or $Value.bytes -ne 2097207 -or $Value.items -ne 6 -or
        $Value.preparations -ne 1 -or $Value.lostAcknowledgements -ne 1 -or $Value.sentHash -cne $Sent.sha256 -or
        $Value.receivedHash -cnotmatch '^[a-f0-9]{64}$' -or $Value.blindNamesRetained -cne (!$Listing)) {throw 'Incomplete native batch proof.'}
    foreach($field in @('resumedOriginalReceipt','collisionOriginalKept','originalSourceKept','emptyDirectoriesKept','unicodeNameTextKept','zeroLengthFilesKept','parentOutcomesMatch','withdrawnInboxDenied')) {
        if($Value[$field] -cne $true) {throw ('Incomplete native batch invariant: '+$field)}
    }
}
Owned
if (AppPid) {throw 'Finish existing instrumentation on the disposable emulator before this probe. No application was stopped.'}
$report=Join-Path $repo ('.tools/native-batch-cross-platform/'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($report)
$app=Adb -Arguments @('shell','pm','path','com.iconnect.proof')
if ($app -cnotmatch '\Apackage:(/data/app/[-A-Za-z0-9_./+=~]+/base\.apk)\z') {throw 'Unexpected installed application path.'}
$installed=Join-Path $report 'installed-application.apk'
[void](Adb -Arguments @('pull',$Matches[1],$installed))
if ((Hash $installed) -cne $ExpectedApplicationSha256) {throw 'Installed application bytes do not match the expected proof. No test APK was installed.'}
Owned
if (AppPid) {throw 'An application process started before the test installation. No application was stopped.'}
[void](Adb -Arguments @('install','-r','-t',$testApkPath))
Verify-Installed 'com.iconnect.proof.test' $ExpectedTestApkSha256 (Join-Path $report 'installed-tests.apk')
$component='com.iconnect.proof.test/com.iconnect.proof.NativeBatchProbe'
if (!(Adb -Arguments @('shell','pm','list','instrumentation')).Contains($component)) {throw 'The supplied test APK does not contain NativeBatchProbe.'}
$modes=if($InboxMode -ceq 'both') {@('listing','blind')} else {@($InboxMode)}
$results=@()
foreach($mode in $modes) {foreach($role in @('windows','android')) {
    Owned
    if(AppPid) {throw 'Another application process started before the next native fixture role.'}
    $listing=$mode -ceq 'listing'; $suite=if($listing) {'batches'} else {'blind-batches'}
    $roleRoot=Join-Path $report ($mode+'/'+$role); [void][IO.Directory]::CreateDirectory($roleRoot)
    $hostDirectory=Join-Path $roleRoot 'host'; [void][IO.Directory]::CreateDirectory($hostDirectory)
    Get-ChildItem -LiteralPath $built -Force | Copy-Item -Destination $hostDirectory -Recurse
    Copy-Item -LiteralPath (Join-Path $native.Directory 'datachannel.dll') -Destination (Join-Path $hostDirectory 'libDatachannel/datachannel.dll') -Force
    if((Hash (Join-Path $hostDirectory $assembly)) -cne $ExpectedFixtureSha256 -or (Hash (Join-Path $hostDirectory 'libDatachannel/datachannel.dll')) -cne $native.BinarySha256) {throw 'The copied fixture bytes changed.'}
    $run=[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).TrimEnd('=').Replace('+','-').Replace('/','_')
    $bridge=@{Run=$run;Sequence=0}; $hostProcess=$null; $phoneProcess=$null; $phoneStarted=$false
    $hostWriter=[IO.StreamWriter]::new((Join-Path $roleRoot 'windows.log'),$false,[Text.UTF8Encoding]::new($false)); $hostWriter.AutoFlush=$true
    $phoneWriter=[IO.StreamWriter]::new((Join-Path $roleRoot 'android.log'),$false,[Text.UTF8Encoding]::new($false)); $phoneWriter.AutoFlush=$true
    try {
        $hostProcess=Start-Child $dotnetPath @('exec',(Join-Path $hostDirectory $assembly),'--run',$run,'--role',$role,'--native-sha',$native.BinarySha256,'--directory',(Join-Path $roleRoot 'host-data'),'--suite',$suite)
        $hostError=$hostProcess.StandardError.ReadToEndAsync(); $initial=$hostProcess.StandardOutput.ReadLineAsync()
        if(!$initial.Wait(30000)) {throw 'Windows fixture did not publish its identity.'}
        $line=$initial.GetAwaiter().GetResult(); $hostWriter.WriteLine($line); $identity=$line | ConvertFrom-Json -AsHashtable
        if($identity.event -cne 'identity' -or $identity.run -cne $run -or $identity.role -cne $role) {throw 'Unexpected Windows fixture identity.'}
        $phoneProcess=Start-Child $adbPath @('-s',$Serial,'shell','am','instrument','-w','-r','-e','run',$run,'-e','role',$role,'-e','hostKey',$identity.key,'-e','listing',$listing.ToString().ToLowerInvariant(),$component)
        $phoneStarted=$true; $phoneError=$phoneProcess.StandardError.ReadToEndAsync()
        $hostRead=$hostProcess.StandardOutput.ReadLineAsync(); $phoneRead=$phoneProcess.StandardOutput.ReadLineAsync()
        $hostSent=$null; $phoneSent=$null; $hostVerified=$null; $phoneVerified=$null; $phoneFinal=$null
        $hostRevoked=$false; $phoneRevoked=$false; $revoking=$false; $checking=$false; $finishing=$false; $started=$false; $phoneSuccess=$false
        $hostCheckpoints=0; $phoneCheckpoints=0; $hostAuthenticated=0; $phoneAuthenticated=0
        $deadline=[DateTime]::UtcNow.AddSeconds(300)
        while([DateTime]::UtcNow -lt $deadline) {
            if($hostRead -and $hostRead.IsCompleted) {
                $line=$hostRead.GetAwaiter().GetResult()
                if($null -eq $line) {$hostRead=$null; if(!$finishing) {throw "Windows fixture exited early: $($hostError.GetAwaiter().GetResult())"}}
                else {
                    $hostWriter.WriteLine($line); if($line.Length -gt 131072) {throw 'Oversized host fixture event.'}
                    $event=$line | ConvertFrom-Json -AsHashtable
                    switch($event.event) {
                        'signal' {Phone-Command $bridge @{op='signal';kind=$event.kind;body=$event.body;mid=$event.mid}}
                        'authenticated' {$hostAuthenticated++; if($hostAuthenticated -ne 1) {throw 'Repeated native authentication.'}; Host-Command $hostProcess @{op='send'}}
                        'bound' {if($event.nativeSha256 -cne $native.BinarySha256 -or $event.suite -cne $suite) {throw 'Unexpected host native binding.'}; Phone-Command $bridge @{op='pairing';pairing=$event.pairing}}
                        'checkpoint' {$hostCheckpoints++; if($hostCheckpoints -ne 1 -or $event.senderOffset -ne 0 -or $event.receiverOffset -ne 32768 -or $event.receipt -cnotmatch '^[A-Za-z0-9_-]{43}$') {throw 'Unexpected host durable checkpoint.'}}
                        'sent' {if($hostSent) {throw 'Repeated host send result.'}; Check-Sent $event; $hostSent=$event}
                        'revoked' {if(!$revoking -or $hostRevoked) {throw 'Unexpected host withdrawal.'}; $hostRevoked=$true}
                        'verified' {if(!$checking -or $hostVerified) {throw 'Unexpected host verification.'}; $hostVerified=$event}
                        'report' {}
                        default {throw 'Unexpected host fixture event.'}
                    }
                    $hostRead=$hostProcess.StandardOutput.ReadLineAsync()
                }
            }
            if($phoneRead -and $phoneRead.IsCompleted) {
                $line=$phoneRead.GetAwaiter().GetResult()
                if($null -eq $line) {$phoneRead=$null} else {
                    $phoneWriter.WriteLine($line); if($line.Length -gt 262144) {throw 'Oversized Android fixture event.'}
                    if($line.StartsWith('INSTRUMENTATION_STATUS: nativeProbe=')) {
                        $encoded=$line.Substring('INSTRUMENTATION_STATUS: nativeProbe='.Length)
                        $json=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($encoded.Replace('-','+').Replace('_','/')+('='*((4-$encoded.Length%4)%4))))
                        $event=$json | ConvertFrom-Json -AsHashtable
                        switch($event.event) {
                            'identity' {Host-Command $hostProcess @{op='peer';key=$event.key;pairing=$event.pairing}}
                            'signal' {Host-Command $hostProcess @{op='signal';kind=$event.kind;body=$event.body;mid=$event.mid}}
                            'bound' {if($started -or $event.peer -cne $identity.device) {throw 'Unexpected Android owner binding.'}; Host-Command $hostProcess @{op='start'}; Phone-Command $bridge @{op='start'}; $started=$true}
                            'authenticated' {$phoneAuthenticated++; if($phoneAuthenticated -ne 1) {throw 'Repeated Android authentication.'}}
                            'checkpoint' {$phoneCheckpoints++; if($phoneCheckpoints -ne 1 -or $event.senderOffset -ne 0 -or $event.receiverOffset -ne 32768 -or $event.receipt -cnotmatch '^[A-Za-z0-9_-]{43}$') {throw 'Unexpected Android durable checkpoint.'}}
                            'sent' {if($phoneSent) {throw 'Repeated Android send result.'}; Check-Sent $event; $phoneSent=$event}
                            'revoked' {if(!$revoking -or $phoneRevoked) {throw 'Unexpected Android withdrawal.'}; $phoneRevoked=$true}
                            'verified' {if(!$checking -or $phoneVerified) {throw 'Unexpected Android verification.'}; $phoneVerified=$event}
                            default {throw 'Unexpected Android fixture event.'}
                        }
                    } elseif($line.StartsWith('INSTRUMENTATION_RESULT: nativeTransportResult=')) {
                        if($phoneFinal) {throw 'Repeated Android final result.'}; $phoneFinal=$line.Substring('INSTRUMENTATION_RESULT: nativeTransportResult='.Length) | ConvertFrom-Json -AsHashtable
                    } elseif($line -match '^INSTRUMENTATION_CODE:\s*-1\s*$') {$phoneSuccess=$true}
                    $phoneRead=$phoneProcess.StandardOutput.ReadLineAsync()
                }
            }
            if($hostSent -and $phoneSent -and !$revoking) {Host-Command $hostProcess @{op='revoke'}; Phone-Command $bridge @{op='revoke'}; $revoking=$true}
            if($hostRevoked -and $phoneRevoked -and !$checking) {
                Host-Command $hostProcess @{op='check';names=$phoneSent.names}; Phone-Command $bridge @{op='check';names=$hostSent.names}; $checking=$true
            }
            if($hostVerified -and $phoneVerified -and !$finishing) {
                Check-Proof $hostVerified $hostSent $listing; Check-Proof $phoneVerified $phoneSent $listing
                if($hostVerified.receivedHash -cne $phoneSent.sha256 -or $phoneVerified.receivedHash -cne $hostSent.sha256 -or
                    $phoneVerified.run -cne $run -or $phoneVerified.role -cne $role -or $hostAuthenticated -ne 1 -or $phoneAuthenticated -ne 1 -or
                    $hostCheckpoints -ne 1 -or $phoneCheckpoints -ne 1) {throw 'Both native roles must authenticate, resume their checkpoints and verify the complete opposite trees.'}
                Host-Command $hostProcess @{op='finish'}; Phone-Command $bridge @{op='finish'}; $finishing=$true
            }
            if(!$phoneRead -and $phoneProcess.HasExited) {break}; Start-Sleep -Milliseconds 10
        }
        if(!$finishing -or !$phoneSuccess -or !$phoneFinal -or !$phoneProcess.WaitForExit(10000) -or $phoneProcess.ExitCode -ne 0 -or
            !$hostProcess.WaitForExit(10000) -or $hostProcess.ExitCode -ne 0) {throw 'The native batch probe did not complete on both processes.'}
        Check-Proof $phoneFinal $phoneSent $listing
        if($phoneFinal.run -cne $run -or $phoneFinal.role -cne $role -or $phoneFinal.receivedHash -cne $hostSent.sha256) {throw 'The Android final result differs from its observed native proof.'}
        $result=@{Mode=$mode;Role=$role;Run=$run;Host=$hostVerified;Android=$phoneFinal;NativeSha256=$native.BinarySha256}
        $result | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $roleRoot 'result.json') -Encoding utf8; $results+=$result
        Write-Host "Verified native folder batches: inbox '$mode', initiating role '$role', six items and 2097207 bytes in each direction."
    } finally {
        if($hostProcess) {if(!$hostProcess.HasExited) {$hostProcess.Kill()}; $hostProcess.Dispose()}
        if($phoneProcess -and !$phoneProcess.HasExited) {Owned; [void](Adb -Arguments @('shell','am','force-stop','com.iconnect.proof')); [void]$phoneProcess.WaitForExit(10000)}
        if($phoneProcess) {$phoneProcess.Dispose()}; $hostWriter.Dispose(); $phoneWriter.Dispose()
        if($phoneStarted) {Owned; [void](Adb -Arguments @('shell','am','force-stop','com.iconnect.proof'))}
    }
}}
if($results.Count -ne $modes.Count*2 -or (Hash $testApkPath) -cne $ExpectedTestApkSha256 -or (Hash (Join-Path $built $assembly)) -cne $ExpectedFixtureSha256) {throw 'The complete requested matrix or its artifact bindings changed.'}
Owned
if(AppPid) {throw 'The probe did not leave the owned application inactive.'}
Verify-Installed 'com.iconnect.proof' $ExpectedApplicationSha256 (Join-Path $report 'final-application.apk')
Verify-Installed 'com.iconnect.proof.test' $ExpectedTestApkSha256 (Join-Path $report 'final-tests.apk')
@{Stage='NativeBatchCrossPlatformVerified';InboxMode=$InboxMode;Avd=$ExpectedAvdName;Serial=$Serial;NativeSha256=$native.BinarySha256;
    ApplicationSha256=$ExpectedApplicationSha256;TestApkSha256=$ExpectedTestApkSha256;FixtureSha256=$ExpectedFixtureSha256;Roles=$results;
    FolderBatchEnginesVerified=$true;PhysicalNetworkVerified=$false;FullControllerInteropVerified=$false} |
    ConvertTo-Json -Depth 10 | Set-Content (Join-Path $report 'result.json') -Encoding utf8
[pscustomobject]@{Report=(Join-Path $report 'result.json');Roles=$results.Count;NativeSha256=$native.BinarySha256}
