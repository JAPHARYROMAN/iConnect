[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$install = Join-Path $repository '.tools/dotnet'
$executable = Join-Path $install 'dotnet.exe'
if (Test-Path -LiteralPath $executable) {
    & $executable --version
    exit $LASTEXITCODE
}
$sdkVersion = '10.0.403'
$metadata = Invoke-RestMethod -Uri 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'
$release = $metadata.releases | Where-Object { $_.sdk.version -eq $sdkVersion } | Select-Object -First 1
if (-not $release) { throw "SDK $sdkVersion was not found in Microsoft's release metadata." }
$download = $release.sdk.files | Where-Object { $_.rid -eq 'win-x64' -and $_.url.EndsWith('.zip') } | Select-Object -First 1
if (-not $download -or ([uri]$download.url).Host -ne 'builds.dotnet.microsoft.com') { throw 'Unexpected SDK download source.' }
New-Item -ItemType Directory -Path (Join-Path $repository '.tools') -Force | Out-Null
$archive = Join-Path $repository '.tools/dotnet-sdk.zip'
Write-Host "Downloading .NET SDK $sdkVersion from Microsoft..."
Invoke-WebRequest -Uri $download.url -OutFile $archive
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash.ToLowerInvariant() -ne $download.hash) {
    throw 'SDK checksum mismatch; archive will not be executed.'
}
Expand-Archive -LiteralPath $archive -DestinationPath $install -Force
& $executable --version
exit $LASTEXITCODE
