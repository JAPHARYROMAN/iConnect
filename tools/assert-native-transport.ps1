#Requires -Version 7.2
[CmdletBinding()]
param([string]$BundleDirectory = $env:ICONNECT_NATIVE_TRANSPORT_BUNDLE)
$ErrorActionPreference = 'Stop'
if (!$BundleDirectory) { $BundleDirectory = $env:ICONNECT_NATIVE_TRANSPORT_BUNDLE }
if (!$BundleDirectory) { $BundleDirectory = Join-Path $env:USERPROFILE '.cache/iconnect/native-transport/application-connection-init-v2-openssl35/bundle' }
if (!(Test-Path -LiteralPath $BundleDirectory -PathType Container)) {
    throw 'Prepare the corrected native transport with ./tools/prepare-native-transport.ps1 before building the Windows application.'
}
. (Join-Path $PSScriptRoot 'native-transport-patches.ps1')
$checked = & (Join-Path $PSScriptRoot 'read-native-transport-bundle.ps1') -BundleDirectory $BundleDirectory
$patches = @(Get-IConnectNativeSourcePatches $checked.Build)
if ($patches.Count -ne 2) { throw 'The application requires both pinned SCTP and DTLS initialization corrections.' }
$currentLockPath = Join-Path $PSScriptRoot 'native-transport-sources.json'
if ($checked.Manifest.sourceLockSha256 -cne (Get-IConnectNativeInputHash $currentLockPath)) {
    throw 'The application requires the current supported OpenSSL source baseline; historical bundles are for inspection only.'
}
$currentLock = Get-Content -LiteralPath $currentLockPath -Raw | ConvertFrom-Json
if ($currentLock.openssl.supportUntil -cnotmatch '\A[0-9]{4}-[0-9]{2}-[0-9]{2}\z' -or
    [DateTime]::UtcNow.Date -ge [DateTime]::ParseExact($currentLock.openssl.supportUntil,'yyyy-MM-dd',[Globalization.CultureInfo]::InvariantCulture).Date) {
    throw 'The pinned OpenSSL support period has ended or is unspecified. Review a supported source baseline before building.'
}
[pscustomobject]@{
    Stage='ApplicationNativeTransportVerified'; Directory=$checked.Directory; BinarySha256=$checked.BinarySha256
    BundleEvidenceSha256=(Get-IConnectNativeInputHash (Join-Path $checked.Directory 'bundle-evidence.json'))
    Corrections=@($patches | ForEach-Object id)
    OpenSsl=$currentLock.openssl.tag; SupportUntil=$currentLock.openssl.supportUntil
}
