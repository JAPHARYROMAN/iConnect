#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArtifactDirectory,
    [string]$Dotnet = (Join-Path $env:USERPROFILE '.cache/iconnect/dotnet/dotnet.exe')
)
$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'The native transport fixtures require Windows x64.' }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifact = (Resolve-Path -LiteralPath $ArtifactDirectory).Path
function Hash([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
$manifestPath = Join-Path $artifact 'build-evidence.json'
if ((Get-Item -LiteralPath $manifestPath).Length -gt 64KB) { throw 'Native build evidence is oversized.' }
$evidence = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
$binary = Join-Path $artifact 'datachannel.dll'
$binarySize = (Get-Item -LiteralPath $binary).Length
if ($binarySize -lt 64 -or $binarySize -gt 128MB) { throw 'Invalid native PE size.' }
$sourceLock = Join-Path $PSScriptRoot 'native-transport-sources.json'
if ($evidence.schema -ne 1 -or $evidence.stage -cne 'BuildOnly' -or
    $evidence.binarySha256 -cnotmatch '\A[a-f0-9]{64}\z' -or $evidence.sourceLockSha256 -cne (Hash $sourceLock) -or
    (Hash (Join-Path $artifact 'source-lock.json')) -cne $evidence.sourceLockSha256 -or (Hash $binary) -cne $evidence.binarySha256) {
    throw 'The native artifact does not match its build evidence and current pinned source inputs.'
}
$bytes = [IO.File]::ReadAllBytes($binary)
if ($bytes[0] -ne 77 -or $bytes[1] -ne 90) { throw 'Invalid native PE file.' }
$pe = [BitConverter]::ToInt32($bytes,60)
if ($pe -lt 64 -or $pe -gt $bytes.Length-24 -or [BitConverter]::ToUInt32($bytes,$pe) -ne 0x4550 -or
    [BitConverter]::ToUInt16($bytes,$pe+4) -ne 0x8664) { throw 'The native candidate is not a Windows x64 PE image.' }
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$dumpbin = (& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'VC/Tools/MSVC/*/bin/Hostx64/x64/dumpbin.exe' | Select-Object -First 1)
if (!$dumpbin) { throw 'MSVC dumpbin is required to inspect the candidate before execution.' }
$dependencyText = (& $dumpbin /dependents $binary) -join "`n"
if ($LASTEXITCODE -ne 0) { throw 'Native import inspection failed.' }
$dependencies = @([regex]::Matches($dependencyText,'(?im)^\s+([A-Za-z0-9_.-]+\.dll)\s*$') | ForEach-Object { $_.Groups[1].Value.ToLowerInvariant() } | Sort-Object -Unique)
if (!$dependencies.Count) { throw 'No native import inventory was obtained.' }
$system = @('kernel32.dll','kernelbase.dll','ntdll.dll','ws2_32.dll','advapi32.dll','crypt32.dll','bcrypt.dll','ncrypt.dll',
    'iphlpapi.dll','user32.dll','gdi32.dll','ole32.dll','oleaut32.dll','combase.dll','secur32.dll','msvcrt.dll','ucrtbase.dll',
    'version.dll','winmm.dll','dnsapi.dll','userenv.dll','shlwapi.dll','shell32.dll','netapi32.dll','winhttp.dll',
    'bcryptprimitives.dll','cryptbase.dll','setupapi.dll','mswsock.dll','dbghelp.dll','psapi.dll')
$unexpected = @($dependencies | Where-Object { $_ -notin $system -and $_ -notmatch '^(api-ms-win-|ext-ms-win-)' })
if ($unexpected.Count) { throw "Review unexpected native imports before testing: $($unexpected -join ', ')" }
$exportText = (& $dumpbin /exports $binary) -join "`n"
if ($LASTEXITCODE -ne 0) { throw 'Native export inspection failed.' }
$exports = @([regex]::Matches($exportText,'(?m)^\s*\d+\s+[0-9A-Fa-f]+\s+[0-9A-Fa-f]+\s+(rtc[A-Za-z0-9_]+)(?:\s|$)') | ForEach-Object { $_.Groups[1].Value })
$binding = Get-Content -LiteralPath (Join-Path $repo 'apps/windows/IConnect.Connectivity/NativeRtc.cs') -Raw
$required = @([regex]::Matches($binding,'\bextern\s+\w+\s+(rtc[A-Za-z0-9_]+)\s*\(') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
if ($required.Count -lt 10 -or @($required | Where-Object { $_ -cnotin $exports }).Count) { throw 'The candidate lacks application-required native exports.' }
$original = Join-Path $repo 'tests/IConnect.Tests/bin/Release/net10.0'
if (!(Test-Path -LiteralPath (Join-Path $original 'IConnect.Tests.dll'))) { throw 'Build the Release test project first.' }
$redirected = @((Get-Item -LiteralPath $original)) + @(Get-ChildItem -LiteralPath $original -Recurse -Force)
if (@($redirected | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'Redirected test outputs require inspection before isolation.' }
$run = Join-Path $repo ('.tools/native-transport-verification/' + [guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $run) { throw 'The newly allocated verification directory already exists.' }
[void][IO.Directory]::CreateDirectory($run)
Get-ChildItem -LiteralPath $original -Force | Copy-Item -Destination $run -Recurse
$isolatedBinary = Join-Path $run 'libDatachannel/datachannel.dll'
Copy-Item -LiteralPath $binary -Destination $isolatedBinary
if ((Hash $isolatedBinary) -cne $evidence.binarySha256) { throw 'The isolated native copy changed.' }
# Partial classes in LocalPayloadNativeTests.cs are local-copy/controller tests,
# not transport fixtures; use actual CLR class names and their discovered cases.
$expectedCases = [ordered]@{
    NativeConnectivityTests=4; StunConnectivityTests=1; TransferNetworkNativeTests=1
    FileRpcNativeTests=2; UploadOperationNativeTests=1
}
$classes = @($expectedCases.Keys)
$filter = ($classes | ForEach-Object { 'FullyQualifiedName~IConnect.Tests.' + $_ }) -join '|'
$before = [Environment]::GetEnvironmentVariable('ICONNECT_EXPECTED_NATIVE_SHA256')
try {
    [Environment]::SetEnvironmentVariable('ICONNECT_EXPECTED_NATIVE_SHA256',$evidence.binarySha256)
    & $Dotnet vstest (Join-Path $run 'IConnect.Tests.dll') ('--TestCaseFilter:' + $filter) '--logger:console;verbosity=minimal' '--logger:trx;LogFileName=native-transport.trx' ('--ResultsDirectory:' + $run)
    if ($LASTEXITCODE -ne 0) { throw "Native application fixtures failed. Evidence remains at $run" }
} finally {
    if($null -eq $before) {Remove-Item -LiteralPath Env:ICONNECT_EXPECTED_NATIVE_SHA256 -ErrorAction SilentlyContinue}
    else {[Environment]::SetEnvironmentVariable('ICONNECT_EXPECTED_NATIVE_SHA256',$before)}
}
[xml]$trx = Get-Content -LiteralPath (Join-Path $run 'native-transport.trx') -Raw
$counts = $trx.TestRun.ResultSummary.Counters
if ([int]$counts.failed -ne 0 -or [int]$counts.notExecuted -ne 0 -or [int]$counts.passed -ne 9 -or [int]$counts.passed -ne [int]$counts.total) { throw 'The expected nine native fixture cases did not complete successfully.' }
$observed = @($trx.TestRun.TestDefinitions.UnitTest | ForEach-Object { $_.TestMethod.className })
foreach ($name in $classes) {
    if (@($observed | Where-Object { $_ -ceq ('IConnect.Tests.' + $name) }).Count -ne $expectedCases[$name]) { throw "Unexpected native fixture coverage for class: $name" }
}
if ((Hash $binary) -cne $evidence.binarySha256 -or (Hash $isolatedBinary) -cne $evidence.binarySha256) { throw 'A native payload changed during verification.' }
$report = [ordered]@{
    schema=1; stage='WindowsNativeFixturesPassed'; binarySha256=$evidence.binarySha256
    sourceLockSha256=$evidence.sourceLockSha256; imports=$dependencies; requiredExports=$required
    testAssemblySha256=(Hash (Join-Path $run 'IConnect.Tests.dll')); passed=[int]$counts.passed
    skipped=[int]$counts.notExecuted; classes=$classes; checkedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')
    androidInteropVerified=$false; fieldAndReleaseGatesComplete=$false
}
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $run 'native-verification.json') -Encoding UTF8
[pscustomobject]@{ Stage=$report.stage; Passed=$report.passed; BinarySha256=$report.binarySha256; Evidence=$run }
