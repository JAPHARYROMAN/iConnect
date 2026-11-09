#Requires -Version 7.2
[CmdletBinding()]
param(
    [string]$BuildDirectory,
    [ValidateRange(1,8)][int]$Jobs = 1,
    [switch]$PrepareOnly,
    [switch]$SctpInitializationFix,
    [switch]$ConnectionInitializationFixes,
    [string]$ReuseOpenSslBuildDirectory
)
$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'This builder targets Windows x64 and requires MSVC.' }
. (Join-Path $PSScriptRoot 'native-transport-patches.ps1')
$nativePatches = @()
if ($ConnectionInitializationFixes) { $nativePatches = @((Get-IConnectNativeSctpPatch),(Get-IConnectNativeDtlsPatch)) }
elseif ($SctpInitializationFix) { $nativePatches = @(Get-IConnectNativeSctpPatch) }
$lockPath = Join-Path $PSScriptRoot 'native-transport-sources.json'
$lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json -AsHashtable
if ($lock.schema -ne 1 -or $lock.cacheKey -cnotmatch '\A[A-Za-z0-9_-]{1,80}\z') { throw 'Unsupported native source lock.' }
foreach ($component in @($lock.libdatachannel,$lock.openssl)) {
    if ($component.commit -cnotmatch '\A[a-f0-9]{40}\z' -or $component.tag -cnotmatch '\A[A-Za-z0-9_.-]+\z' -or
        $component.repository -cnotmatch '\Ahttps://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\.git\z') { throw 'Invalid native source coordinate.' }
}
if ($lock.nasm.url -cnotmatch '\Ahttps://www\.nasm\.us/pub/nasm/releasebuilds/[0-9.]+/win64/nasm-[0-9.]+-win64\.zip\z' -or
    $lock.nasm.sha256 -cnotmatch '\A[a-f0-9]{64}\z') { throw 'Invalid assembler archive coordinate.' }
if (!$BuildDirectory) {
    $cache = $lock.cacheKey + $(if ($ConnectionInitializationFixes) { '-connection-init1' } elseif ($SctpInitializationFix) { '-sctp-init1' } else { '' })
    $BuildDirectory = Join-Path $env:USERPROFILE ('.cache/iconnect/native-transport/' + $cache)
}
$root = [IO.Path]::GetFullPath($BuildDirectory)
# OpenSSL's Windows makefiles require a build path without whitespace.
if ($root -match '[\s"%!^&|<>]') { throw 'Choose a native build directory without whitespace or command metacharacters.' }
[void][IO.Directory]::CreateDirectory($root)
try { $owner = [IO.File]::Open((Join-Path $root 'owner.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None) }
catch { throw 'The native build cache is already in use or inaccessible.' }

function Invoke-Git([string[]]$Arguments) {
    $output = & git.exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Git failed while validating native sources (exit $LASTEXITCODE)." }
    return $output
}
function Assert-Source([string]$Path,[string]$Commit,[object[]]$Patches = @()) {
    Assert-IConnectNativeSource $Path $Commit $Patches
}
function Source([string]$Name,[hashtable]$Component) {
    $path = Join-Path $root $Name
    if (!(Test-Path -LiteralPath $path)) {
        [void](Invoke-Git -Arguments @('-c','advice.detachedHead=false','clone','-c','core.autocrlf=false','--depth','1','--branch',$Component.tag,'--single-branch',$Component.repository,$path))
    }
    if ($Name -ceq 'libdatachannel' -and $nativePatches.Count) {
        Initialize-IConnectNativeSourcePatch $path $Component.commit $nativePatches
    } else { Assert-Source $path $Component.commit }
    return $path
}
function Hash([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function CmdPath([string]$Path) {
    if ($Path -match '["%!^&|<>\r\n]' -or $Path -match '[^\x20-\x7e]') { throw 'Unsupported native tool path.' }
    return '"' + $Path + '"'
}
function Run-Driver([string]$Name,[string[]]$Commands) {
    $driver = Join-Path $root ($Name + '.cmd')
    $log = Join-Path $root ($Name + '.log')
    [IO.File]::WriteAllLines($driver,$Commands,[Text.ASCIIEncoding]::new())
    & $env:COMSPEC /d /c $driver *> $log
    $result = $LASTEXITCODE
    if ($result -ne 0) { Get-Content -LiteralPath $log -Tail 20; throw "Native stage '$Name' failed ($result). Inspect $log" }
    Write-Host "Native stage '$Name' completed. Log: $log"
}

$sslReuseOwner = $null
try {
    $libSource = Source 'libdatachannel' $lock.libdatachannel
    [void](Invoke-Git -Arguments @('-C',$libSource,'submodule','update','--init','--recursive','--depth','1'))
    $modules = @(Invoke-Git -Arguments @('-C',$libSource,'submodule','status','--recursive'))
    if ($modules.Count -ne $lock.libdatachannel.submodules.Count) { throw 'Unexpected native submodule inventory.' }
    foreach ($line in $modules) {
        if ($line -cnotmatch '\A ([a-f0-9]{40}) (deps/[A-Za-z0-9_-]+)(?: .*)?\z' -or
            $lock.libdatachannel.submodules[$Matches[2]] -cne $Matches[1]) { throw 'A native submodule is missing or differs from its pinned gitlink.' }
        Assert-Source (Join-Path $libSource $Matches[2]) $Matches[1]
    }
    $sslSource = Source 'openssl' $lock.openssl
    $archive = Join-Path $root ('nasm-' + $lock.nasm.version + '-win64.zip')
    if (!(Test-Path -LiteralPath $archive)) { Invoke-WebRequest -Uri $lock.nasm.url -OutFile $archive -TimeoutSec 60 }
    if ((Hash $archive) -cne $lock.nasm.sha256) { throw 'Assembler archive hash mismatch.' }
    $nasmDirectory = Join-Path $root 'nasm'
    [void][IO.Directory]::CreateDirectory($nasmDirectory)
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        foreach ($name in @('nasm.exe','ndisasm.exe')) {
            $entries = @($zip.Entries | Where-Object { $_.FullName -ceq ('nasm-' + $lock.nasm.version + '/' + $name) })
            if ($entries.Count -ne 1 -or $entries[0].Length -gt 10MB) { throw 'Unexpected assembler archive member.' }
            $target = Join-Path $nasmDirectory $name
            if (!(Test-Path -LiteralPath $target)) { [IO.Compression.ZipFileExtensions]::ExtractToFile($entries[0],$target,$false) }
            $stream = $entries[0].Open()
            try { $expected = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
            finally { $stream.Dispose() }
            if ((Hash $target) -cne $expected) { throw 'An extracted assembler executable changed.' }
        }
    } finally { $zip.Dispose() }
    if ($PrepareOnly) {
        [pscustomobject]@{ Stage='Prepared'; SourceLockSha256=(Hash $lockPath); Root=$root; Submodules=$modules.Count }
        return
    }

    $cmake = (Get-Command cmake.exe -ErrorAction Stop).Source
    [void](Get-Command ninja.exe -ErrorAction Stop)
    $perl = (Get-Command perl.exe -ErrorAction Stop).Source
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $studio = (& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1)
    if (!$studio) { throw 'Install the MSVC x64 build tools and Windows SDK before compiling the native transport.' }
    $vs = Join-Path $studio 'Common7/Tools/VsDevCmd.bat'
    $sslBuild = Join-Path $root 'openssl-build'
    $sslPrefix = Join-Path $root 'openssl-install'
    $libBuild = Join-Path $root 'datachannel-build'
    $reuseEvidenceHash = $null
    if ($ReuseOpenSslBuildDirectory) {
        $reuse = (Resolve-Path -LiteralPath $ReuseOpenSslBuildDirectory).Path
        if ($reuse.TrimEnd('\') -ieq $root.TrimEnd('\')) { throw 'Choose a separate cache for OpenSSL reuse.' }
        try { $sslReuseOwner = [IO.File]::Open((Join-Path $reuse 'owner.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None) }
        catch { throw 'The OpenSSL reuse cache is already in use or inaccessible.' }
        $reusePath = Join-Path $reuse 'artifact/build-evidence.json'
        if ((Get-Item -LiteralPath $reusePath).Length -gt 64KB) { throw 'OpenSSL reuse evidence is oversized.' }
        $reuseProof = Get-Content -LiteralPath $reusePath -Raw | ConvertFrom-Json -AsHashtable
        if ($reuseProof.schema -ne 1 -or $reuseProof.stage -cne 'BuildOnly' -or $reuseProof.sourceLockSha256 -cne (Hash $lockPath) -or
            $reuseProof.architecture -cne 'windows-x64' -or $reuseProof.runtime -cne 'MSVC static' -or
            $reuseProof.openssl.commit -cne $lock.openssl.commit -or $reuseProof.nasmVersion -cne $lock.nasm.version -or
            $reuseProof.nasmSha256 -cne (Hash (Join-Path $nasmDirectory 'nasm.exe')) -or
            $reuseProof.nasmSha256 -cne (Hash (Join-Path $reuse 'nasm/nasm.exe'))) { throw 'OpenSSL reuse inputs do not match the pinned build.' }
        Assert-Source (Join-Path $reuse 'openssl') $lock.openssl.commit
        $sslPrefix = Join-Path $reuse 'openssl-install'
        if ((Hash (Join-Path $sslPrefix 'lib/libssl.lib')) -cne $reuseProof.opensslLibSslSha256 -or
            (Hash (Join-Path $sslPrefix 'lib/libcrypto.lib')) -cne $reuseProof.opensslLibCryptoSha256) { throw 'OpenSSL reuse libraries differ from their build evidence.' }
        $reuseEvidenceHash = Hash $reusePath
    }
    foreach ($directory in @($sslBuild,$libBuild)) { [void][IO.Directory]::CreateDirectory($directory) }
    $environment = @('@echo off',('call ' + (CmdPath $vs) + ' -arch=x64 -host_arch=x64 -no_logo'),
        'if errorlevel 1 exit /b 1',('set "PATH=' + $nasmDirectory + ';%PATH%"'),'set "CL="','set "_CL_="','set "LC_ALL=C"','set "LANG=C"')
    if (!$ReuseOpenSslBuildDirectory) { Run-Driver 'build-openssl' ($environment + @(
        ('cd /d ' + (CmdPath $sslBuild)),
        ((CmdPath $perl) + ' ' + (CmdPath (Join-Path $sslSource 'Configure')) + ' VC-WIN64A no-shared --prefix=' + (CmdPath $sslPrefix)),
        'if errorlevel 1 exit /b 1','nmake /nologo','if errorlevel 1 exit /b 1',
        'nmake /nologo install_sw','if errorlevel 1 exit /b 1','exit /b 0')) }
    $configure = (CmdPath $cmake) + ' -S ' + (CmdPath $libSource) + ' -B ' + (CmdPath $libBuild) +
        ' -G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_POLICY_DEFAULT_CMP0091=NEW -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded' +
        ' -DBUILD_SHARED_LIBS=ON -DBUILD_SHARED_DEPS_LIBS=OFF -DNO_EXAMPLES=ON -DNO_TESTS=OFF' +
        ' -DUSE_NICE=OFF -DUSE_GNUTLS=OFF -DUSE_MBEDTLS=OFF -DPREFER_SYSTEM_LIB=OFF' +
        ' -DOPENSSL_USE_STATIC_LIBS=TRUE -DOPENSSL_MSVC_STATIC_RT=TRUE -DOPENSSL_ROOT_DIR=' + (CmdPath $sslPrefix)
    Run-Driver 'build-datachannel' ($environment + @($configure,'if errorlevel 1 exit /b 1',
        ((CmdPath $cmake) + ' --build ' + (CmdPath $libBuild) + ' --target datachannel --parallel ' + $Jobs),
        'if errorlevel 1 exit /b 1','exit /b 0'))
    Assert-Source $libSource $lock.libdatachannel.commit $nativePatches
    Assert-Source $sslSource $lock.openssl.commit
    foreach ($module in $lock.libdatachannel.submodules.GetEnumerator()) { Assert-Source (Join-Path $libSource $module.Key) $module.Value }
    if ($ReuseOpenSslBuildDirectory) {
        Assert-Source (Join-Path $reuse 'openssl') $lock.openssl.commit
        if ((Hash $reusePath) -cne $reuseEvidenceHash -or (Hash (Join-Path $sslPrefix 'lib/libssl.lib')) -cne $reuseProof.opensslLibSslSha256 -or
            (Hash (Join-Path $sslPrefix 'lib/libcrypto.lib')) -cne $reuseProof.opensslLibCryptoSha256) { throw 'OpenSSL reuse inputs changed during the native build.' }
    }
    $binary = Join-Path $libBuild 'datachannel.dll'
    if (!(Test-Path -LiteralPath $binary)) { throw 'The expected Windows native DLL was not produced.' }
    $artifact = Join-Path $root 'artifact'
    [void][IO.Directory]::CreateDirectory($artifact)
    Copy-Item -LiteralPath $binary -Destination (Join-Path $artifact 'datachannel.dll')
    Copy-Item -LiteralPath $lockPath -Destination (Join-Path $artifact 'source-lock.json')
    if ($nativePatches.Count) {
        foreach ($patch in $nativePatches) {
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot (Get-IConnectNativePatchDefinitionName $patch.id)) -Destination $artifact
            $target = Join-Path $artifact $patch.patchFile
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot $patch.patchFile) -Destination $target
        }
        Assert-IConnectNativePatchArtifacts $artifact $nativePatches
    }
    $evidence = [ordered]@{
        schema=1; stage='BuildOnly'; sourceLockSha256=(Hash $lockPath); binarySha256=(Hash $binary)
        libdatachannel=$lock.libdatachannel; openssl=$lock.openssl
        nasmVersion=$lock.nasm.version; nasmSha256=(Hash (Join-Path $nasmDirectory 'nasm.exe'))
        opensslLibSslSha256=(Hash (Join-Path $sslPrefix 'lib/libssl.lib'))
        opensslLibCryptoSha256=(Hash (Join-Path $sslPrefix 'lib/libcrypto.lib'))
        opensslBuildReused=[bool]$ReuseOpenSslBuildDirectory; opensslReuseEvidenceSha256=$reuseEvidenceHash
        cmakeVersion=((& $cmake --version | Select-Object -First 1) -join '')
        architecture='windows-x64'; runtime='MSVC static'; jobs=$Jobs
        verifiedAtUtc=[DateTimeOffset]::UtcNow.ToString('o'); applicationInteropVerified=$false
        nativePatches=$nativePatches
    }
    $evidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $artifact 'build-evidence.json') -Encoding UTF8
    [pscustomobject]@{ Stage='BuildOnly'; Artifact=$artifact; BinarySha256=$evidence.binarySha256; ApplicationInteropVerified=$false }
} finally { if ($sslReuseOwner) { $sslReuseOwner.Dispose() }; $owner.Dispose() }
