[CmdletBinding(DefaultParameterSetName = 'Unsigned')]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('\A[A-Za-z0-9][A-Za-z0-9.-]{1,48}[A-Za-z0-9]\z')]
    [string]$PackageName,
    [Parameter(Mandatory)]
    [ValidatePattern('\A[0-9]{1,5}(\.[0-9]{1,5}){3}\z')]
    [string]$Version,
    [Parameter(Mandatory)]
    [ValidateLength(4, 8192)]
    [string]$Publisher,
    [Parameter(Mandatory, ParameterSetName = 'Unsigned')]
    [switch]$Unsigned,
    [Parameter(Mandatory, ParameterSetName = 'Signed')]
    [ValidatePattern('\A[0-9A-Fa-f]{40}\z')]
    [string]$CertificateThumbprint,
    [Parameter(Mandatory, ParameterSetName = 'Signed')]
    [uri]$TimestampServer,
    [switch]$NoRestore,
    [string]$NativeTransportBundle,
    [string]$NoticePython='python'
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$sourceNative = if ($NativeTransportBundle) {
    & (Join-Path $PSScriptRoot 'read-native-transport-bundle.ps1') -BundleDirectory $NativeTransportBundle
} else { $null }
$versionValue = [version]$Version
if ($versionValue.ToString() -cne $Version -or $versionValue.Major -eq 0 -or
    @($Version.Split('.') | Where-Object { [int]$_ -gt 65535 }).Count -ne 0) {
    throw 'Use a canonical four-part MSIX version with a nonzero major version.'
}
if ($Publisher -match '[\p{Cc}]' -or $Publisher -match 'OID\.2\.25\.311729368913984317654407730594956997722') {
    throw 'Use the intended signing certificate subject for the package publisher.'
}
$signed = $PSCmdlet.ParameterSetName -eq 'Signed'
if (!$signed -and !$Unsigned) { throw 'Choose an unsigned build or provide the release signing certificate.' }
if ($signed) {
    if (!$TimestampServer.IsAbsoluteUri -or $TimestampServer.Scheme -ne 'https' -or $TimestampServer.UserInfo) {
        throw 'Choose an HTTPS release timestamp service.'
    }
    $certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$CertificateThumbprint"
    if (!$certificate.HasPrivateKey -or $certificate.Subject -cne $Publisher) {
        throw 'The signing certificate must have a private key and its subject must exactly match the package publisher.'
    }
}

$kit = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin/10.0.26100.0/x64'
$makeAppx = Join-Path $kit 'makeappx.exe'
$makePri = Join-Path $kit 'makepri.exe'
$signTool = Join-Path $kit 'signtool.exe'
if (!(Test-Path -LiteralPath $makeAppx -PathType Leaf) -or !(Test-Path -LiteralPath $makePri -PathType Leaf) -or
    ($signed -and !(Test-Path -LiteralPath $signTool -PathType Leaf))) {
    throw 'Install the Windows 10.0.26100.0 SDK packaging tools.'
}

$output = Join-Path $repository '.local/releases/windows'
$suffix = if ($signed) { '.msix' } else { '.unsigned.msix' }
$result = Join-Path $output "$PackageName-$Version-x64$suffix"
if (Test-Path -LiteralPath $result) { throw 'A package already exists for this identity and version at the output path.' }
$work = Join-Path $output ('work-' + [Guid]::NewGuid().ToString('N'))
$payload = Join-Path $work 'payload'
[void][IO.Directory]::CreateDirectory($payload)
$arguments = @('-c', 'Release', '-o', $payload, "-p:IConnectDesktopVersion=$Version")
if ($NoRestore) { $arguments += '--no-restore' }
else { $arguments += '-p:RestoreLockedMode=true' }
& (Join-Path $PSScriptRoot 'dev.ps1') publish-desktop @arguments | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Windows desktop publish failed.' }

foreach ($asset in @('IConnect.Desktop.exe', 'IConnect.Desktop.dll', 'IConnect.Desktop.pri', 'App.xbf', 'MainWindow.xbf',
        'coreclr.dll', 'Microsoft.WindowsAppRuntime.dll', 'libDatachannel/datachannel.dll', 'PreviewWorker/IConnect.Preview.Worker.exe', 'PreviewWorker/coreclr.dll',
        'PreviewWorker/WinRT.Runtime.dll', 'AttachmentWorker/IConnect.Attachment.Worker.exe', 'AttachmentWorker/coreclr.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $payload $asset) -PathType Leaf)) { throw "Missing published asset $asset." }
}
$metadata = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $payload 'IConnect.Desktop.dll'))
if ($metadata.FileVersion -cne $Version) { throw 'The desktop binary does not match the package version.' }
$connectivityAssets = Join-Path $repository 'apps/windows/IConnect.Connectivity/obj/project.assets.json'
$nativeMetadata = Join-Path $payload 'PackageMetadata/NativeTransport'
if (Test-Path -LiteralPath $nativeMetadata) { throw 'Unexpected native metadata in the fresh desktop publish output.' }
if ($sourceNative) {
    [void](New-Item -Path $nativeMetadata -ItemType Directory)
    foreach ($file in $sourceNative.Files) {
        $target = Join-Path $nativeMetadata $file.RelativePath
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
        Copy-Item -LiteralPath $file.Path -Destination $target
    }
    # Validate the staged source/notice copy before replacing this fresh publish's
    # default transport. The repository's regular build output stays unchanged.
    $stagedNative = & (Join-Path $PSScriptRoot 'read-native-transport-bundle.ps1') -BundleDirectory $nativeMetadata
    Copy-Item -LiteralPath (Join-Path $stagedNative.Directory 'datachannel.dll') -Destination (Join-Path $payload 'libDatachannel/datachannel.dll') -Force
    $noticeMetadata = Join-Path $payload 'PackageMetadata/NativeNotices'
    & (Join-Path $PSScriptRoot 'export-native-notices.ps1') -BundleDirectory $stagedNative.Directory `
        -ExpectedBinarySha256 $stagedNative.BinarySha256 -OutputDirectory $noticeMetadata -Python $NoticePython | Out-Host
    $nativeNotices = & (Join-Path $PSScriptRoot 'read-native-notice-inventory.ps1') -InventoryDirectory $noticeMetadata -BundleDirectory $stagedNative.Directory
}
& (Join-Path $PSScriptRoot 'write-windows-release-sbom.ps1') -Payload $payload -ConnectivityAssets $connectivityAssets `
    -PackageName $PackageName -Version $Version

$template = Join-Path $repository 'apps/windows/IConnect.Desktop/packaging/AppxManifest.xml'
[xml]$manifest = Get-Content -LiteralPath $template -Raw
$manifest.Package.Identity.SetAttribute('Name', $PackageName)
$manifest.Package.Identity.SetAttribute('Version', $Version)
$manifest.Package.Identity.SetAttribute('Publisher', $Publisher)
$manifest.Save((Join-Path $payload 'AppxManifest.xml'))

# Reuse the WinUI resource graph, including embedded XAML and runtime PRI dependencies.
$project = Join-Path $repository 'apps/windows/IConnect.Desktop'
$priConfig = Join-Path $project 'obj/Release/net10.0-windows10.0.26100.0/win-x64/priconfig.xml'
$packagePri = Join-Path $payload 'resources.pri'
$priOutput = & $makePri new /pr $project /cf $priConfig /in $PackageName /of $packagePri 2>&1
if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $packagePri -PathType Leaf)) {
    $priOutput | Out-Host
    throw 'The package resource index could not be generated.'
}

# App Installer requires three raster assets. Use the same simple mark at the exact target sizes.
Add-Type -AssemblyName System.Drawing
$logos = Join-Path $payload 'PackageAssets'
[void][IO.Directory]::CreateDirectory($logos)
foreach ($logo in @(@{Name='StoreLogo'; Size=50}, @{Name='Square44x44Logo'; Size=44}, @{Name='Square150x150Logo'; Size=150})) {
    $size = $logo.Size
    $bitmap = [Drawing.Bitmap]::new($size, $size)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $ink = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(158, 241, 222))
    try {
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.Clear([Drawing.Color]::FromArgb(20, 33, 61))
        $graphics.FillRectangle($ink, [single]($size * 0.31), [single]($size * 0.45), [single]($size * 0.38), [single]($size * 0.10))
        $graphics.FillEllipse($ink, [single]($size * 0.22), [single]($size * 0.36), [single]($size * 0.28), [single]($size * 0.28))
        $graphics.FillEllipse($ink, [single]($size * 0.50), [single]($size * 0.36), [single]($size * 0.28), [single]($size * 0.28))
        $bitmap.Save((Join-Path $logos ($logo.Name + '.png')), [Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $ink.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
}

$candidate = Join-Path $work 'candidate.msix'
$files = @(Get-ChildItem -LiteralPath $payload -File -Recurse | Where-Object { $_.Extension -ine '.pdb' })
$mapping = Join-Path $work 'payload-map.txt'
$map = @('[Files]') + @($files | ForEach-Object {
    '"{0}" "{1}"' -f $_.FullName, [IO.Path]::GetRelativePath($payload, $_.FullName)
})
[IO.File]::WriteAllLines($mapping, $map, [Text.Encoding]::Unicode)
$packOutput = & $makeAppx pack /h SHA256 /f $mapping /p $candidate 2>&1
if ($LASTEXITCODE -ne 0) {
    $packOutput | Select-Object -Last 25 | Out-Host
    throw 'MSIX creation or manifest validation failed.'
}
if ($signed) {
    & $signTool sign /fd SHA256 /sha1 $CertificateThumbprint /tr $TimestampServer.AbsoluteUri /td SHA256 $candidate | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'MSIX signing failed.' }
    & $signTool verify /pa /tw $candidate | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'The timestamped MSIX signature could not be verified.' }
}

$archive = [IO.Compression.ZipFile]::OpenRead($candidate)
try {
    if ($null -eq $archive.GetEntry('AppxBlockMap.xml') -or ($signed -and $null -eq $archive.GetEntry('AppxSignature.p7x'))) {
        throw 'MSIX verification data is missing.'
    }
    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($payload, $file.FullName).Replace('\', '/')
        $entry = $archive.GetEntry($relative)
        if ($null -eq $entry -or $entry.Length -ne $file.Length) { throw "Missing or incomplete package entry $relative." }
        $stream = $entry.Open()
        try { $actual = (Get-FileHash -InputStream $stream -Algorithm SHA256).Hash }
        finally { $stream.Dispose() }
        if ($actual -ne (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash) { throw "Package entry $relative changed." }
    }
}
finally { $archive.Dispose() }

$hash = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::Move($candidate, $result)
[pscustomobject]@{
    Path = $result
    PackageName = $PackageName
    Publisher = $Publisher
    Version = $Version
    Signed = $signed
    Sha256 = $hash
    WorkDirectory = $work
    NativeTransport = if ($sourceNative) { 'PinnedSourceBundle' } else { 'NuGet' }
    NativeNoticeInventorySha256 = if ($sourceNative) { $nativeNotices.InventorySha256 } else { $null }
}
