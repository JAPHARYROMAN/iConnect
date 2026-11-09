[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('server', 'windows', 'desktop', 'publish-desktop', 'publish-coordinator', 'build', 'test', 'android')]
    [string]$Task = 'build',
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ExtraArguments
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$candidates = @(
    (Join-Path $repository '.tools/dotnet/dotnet.exe'),
    (Join-Path $env:USERPROFILE '.cache/iconnect/dotnet/dotnet.exe')
)
$systemDotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($systemDotnet) { $candidates += $systemDotnet.Source }
$dotnet = $null
foreach ($candidate in $candidates) {
    if ((Test-Path -LiteralPath $candidate) -and ((& $candidate --list-sdks) -match '^10\.0\.4\d\d ')) {
        $dotnet = $candidate
        break
    }
}
if (-not $dotnet -and $Task -ne 'android') { throw 'Install .NET 10 SDK 10.0.400+ or run ./tools/bootstrap.ps1 first.' }
$previousEnvironment = $env:ASPNETCORE_ENVIRONMENT
Push-Location $repository
try {
    if ($Task -in @('windows','desktop','publish-desktop','build','test')) {
        if ($env:ICONNECT_NATIVE_TRANSPORT_BUNDLE) {
            [void](& (Join-Path $PSScriptRoot 'assert-native-transport.ps1') -BundleDirectory $env:ICONNECT_NATIVE_TRANSPORT_BUNDLE)
        } else {
            & (Join-Path $PSScriptRoot 'prepare-native-transport.ps1') | Out-Host
        }
    }
    switch ($Task) {
        'server' {
            $env:ASPNETCORE_ENVIRONMENT = 'Development'
            & $dotnet run --project backend/IConnect.Rendezvous -- --urls http://127.0.0.1:5078 @ExtraArguments
        }
        'windows' { & $dotnet run --project apps/windows/IConnect.Proof -- @ExtraArguments }
        'desktop' { & $dotnet run --project apps/windows/IConnect.Desktop -- @ExtraArguments }
        'publish-desktop' { & $dotnet publish apps/windows/IConnect.Desktop/IConnect.Desktop.csproj @ExtraArguments }
        'publish-coordinator' { & $dotnet publish backend/IConnect.Rendezvous/IConnect.Rendezvous.csproj -c Release -p:UseAppHost=false -p:RestoreLockedMode=true @ExtraArguments }
        'build' { & $dotnet build iConnect.slnx @ExtraArguments }
        'test' { & $dotnet test iConnect.slnx --logger 'console;verbosity=minimal' @ExtraArguments }
        'android' {
            Push-Location (Join-Path $repository 'apps/android')
            try { & './gradlew.bat' :app:assembleDebug :app:testDebugUnitTest @ExtraArguments }
            finally { Pop-Location }
        }
    }
    $result = $LASTEXITCODE
}
finally {
    $env:ASPNETCORE_ENVIRONMENT = $previousEnvironment
    Pop-Location
}
exit $result
