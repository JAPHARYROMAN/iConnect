#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BundleDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{64}$')][string]$ExpectedBinarySha256,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$Python='python'
)
$ErrorActionPreference='Stop'
$verified=& (Join-Path $PSScriptRoot 'read-native-transport-bundle.ps1') -BundleDirectory $BundleDirectory
if($verified.BinarySha256 -cne $ExpectedBinarySha256) {throw 'The native candidate differs from its expected reviewed bytes.'}
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $output) {throw 'The notice output directory already exists.'}
$prefixes=@{libdatachannel='libdatachannel/';openssl='openssl/';libjuice='libdatachannel/deps/libjuice/';libsrtp='libdatachannel/deps/libsrtp/';usrsctp='libdatachannel/deps/usrsctp/';plog='libdatachannel/deps/plog/';json='libdatachannel/deps/json/'}
$components=@($verified.Components|ForEach-Object {
    # Use a local name rather than nesting $_ in the manifest lookup.
    $name=$_.Name; $entry=@($verified.Manifest.components|Where-Object {$_.name -ceq $name})
    if($entry.Count -ne 1) {throw 'The verified source inventory differs.'}
    [ordered]@{name=$name;commit=$_.Commit;scope=$_.Scope;archive=$_.SourceArchive;archiveSha256=$_.SourceArchiveSha256;
        entries=$entry[0].sourceEntries;prefix=$prefixes[$name];rootMember=$prefixes[$name]+[IO.Path]::GetFileName($_.RootLicense).Substring($name.Length+1);rootLicense=$_.RootLicense;rootLicenseSha256=$_.RootLicenseSha256}
})
$inputFile=[IO.Path]::GetTempFileName()
try {
    [ordered]@{directory=$verified.Directory;binarySha256=$verified.BinarySha256;manifestSha256=(Get-FileHash -LiteralPath (Join-Path $verified.Directory 'bundle-evidence.json') -Algorithm SHA256).Hash.ToLowerInvariant();components=$components}|
        ConvertTo-Json -Depth 7|Set-Content -LiteralPath $inputFile -Encoding utf8
    & $Python (Join-Path $PSScriptRoot 'native_notice_inventory.py') --verified-input $inputFile --output $output
    if($LASTEXITCODE -ne 0) {throw 'Native notice inventory failed. Incomplete output was preserved without approval.'}
} finally {Remove-Item -LiteralPath $inputFile}
