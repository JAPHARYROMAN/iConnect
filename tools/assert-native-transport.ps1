#Requires -Version 7.2
[CmdletBinding()]
param([string]$BundleDirectory = $env:ICONNECT_NATIVE_TRANSPORT_BUNDLE)
$ErrorActionPreference = 'Stop'
if (!$BundleDirectory) { $BundleDirectory = $env:ICONNECT_NATIVE_TRANSPORT_BUNDLE }
if (!$BundleDirectory) { $BundleDirectory = Join-Path $env:USERPROFILE '.cache/iconnect/native-transport/application-connection-init-v1/bundle' }
if (!(Test-Path -LiteralPath $BundleDirectory -PathType Container)) {
    throw 'Prepare the corrected native transport with ./tools/prepare-native-transport.ps1 before building the Windows application.'
}
. (Join-Path $PSScriptRoot 'native-transport-patches.ps1')
$checked = & (Join-Path $PSScriptRoot 'read-native-transport-bundle.ps1') -BundleDirectory $BundleDirectory
$patches = @(Get-IConnectNativeSourcePatches $checked.Build)
if ($patches.Count -ne 2) { throw 'The application requires both pinned SCTP and DTLS initialization corrections.' }
[pscustomobject]@{
    Stage='ApplicationNativeTransportVerified'; Directory=$checked.Directory; BinarySha256=$checked.BinarySha256
    BundleEvidenceSha256=(Get-IConnectNativeInputHash (Join-Path $checked.Directory 'bundle-evidence.json'))
    Corrections=@($patches | ForEach-Object id)
}
