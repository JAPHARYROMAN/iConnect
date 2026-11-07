[CmdletBinding(DefaultParameterSetName = 'Build')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Build')]
    [string]$KeyStore,
    [Parameter(Mandatory, ParameterSetName = 'Build')]
    [ValidateNotNullOrEmpty()]
    [string]$KeyAlias,
    [Parameter(ParameterSetName = 'Build')]
    [switch]$Offline,
    [Parameter(Mandatory, ParameterSetName = 'Verify')]
    [string]$Apk,
    [Parameter(Mandatory)]
    [ValidateRange(1, 2100000000)]
    [int]$VersionCode,
    [Parameter(Mandatory)]
    [ValidateLength(5, 64)]
    [ValidatePattern('\A[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?\z')]
    [string]$VersionName,
    [Parameter(Mandatory)]
    [ValidatePattern('\A[0-9A-Fa-f]{64}\z')]
    [string]$ExpectedSignerSha256
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$sdk = $env:ANDROID_HOME
if (!$sdk) { $sdk = $env:ANDROID_SDK_ROOT }
if (!$sdk) { $sdk = Join-Path $env:LOCALAPPDATA 'Android/Sdk' }
$tools = Join-Path $sdk 'build-tools/36.0.0'
$signer = Join-Path $tools 'lib/apksigner.jar'
$align = Join-Path $tools 'zipalign.exe'
$aapt = Join-Path $tools 'aapt2.exe'
foreach ($tool in @($signer, $align, $aapt)) {
    if (!(Test-Path -LiteralPath $tool -PathType Leaf)) { throw 'Install Android SDK Build Tools 36.0.0 and set ANDROID_HOME.' }
}
$java = if ($env:JAVA_HOME) { Join-Path $env:JAVA_HOME 'bin/java.exe' } else { (Get-Command java.exe -CommandType Application).Source }
if (!(Test-Path -LiteralPath $java -PathType Leaf)) { throw 'Set JAVA_HOME to the Java installation used for the Android build.' }

function Test-ReleaseApk([string]$Path) {
    $file = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $verification = & $java -jar $signer verify --verbose --print-certs -Werr $Path
        if ($LASTEXITCODE -ne 0) { throw 'APK signature verification failed.' }
        $digests = @($verification | Select-String '^Signer #\d+ certificate SHA-256 digest: ([0-9a-f]{64})$')
        if ($digests.Count -ne 1 -or $digests[0].Matches[0].Groups[1].Value -ine $ExpectedSignerSha256) {
            throw 'APK does not have exactly one signer matching the expected release certificate.'
        }
        if (($verification -match '^Signer #\d+ certificate DN:.*\bCN=Android Debug(?:,|$)') -or
            !($verification -match '^Verified using v3 scheme .*: true$')) {
            throw 'APK must use a release certificate and an APK Signature Scheme v3 signature.'
        }
        & $align -c -P 16 4 $Path
        if ($LASTEXITCODE -ne 0) { throw 'APK alignment verification failed.' }
        $badging = & $aapt dump badging $Path
        if ($LASTEXITCODE -ne 0) { throw 'APK manifest inspection failed.' }
        $package = @($badging | Select-String -CaseSensitive "^package: name='com\.iconnect\.proof' versionCode='(\d+)' versionName='([^']+)'(?:\s|$)")
        if ($package.Count -ne 1 -or $package[0].Matches[0].Groups[1].Value -cne "$VersionCode" -or
            $package[0].Matches[0].Groups[2].Value -cne $VersionName -or ($badging -match '^\s*application-debuggable\b')) {
            throw 'APK must be a non-debuggable iConnect release with the requested version.'
        }
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    finally { $file.Dispose() }
}

if ($PSCmdlet.ParameterSetName -eq 'Verify') {
    $result = (Resolve-Path -LiteralPath $Apk).Path
    $hash = Test-ReleaseApk $result
}
else {
    $keyFile = (Resolve-Path -LiteralPath $KeyStore).Path
    if (!(Test-Path -LiteralPath $keyFile -PathType Leaf)) { throw 'Choose an existing release keystore file.' }
    $storePassword = $env:ICONNECT_ANDROID_KEYSTORE_PASSWORD
    $keyPassword = $env:ICONNECT_ANDROID_KEY_PASSWORD
    if (!$storePassword) { throw 'Set ICONNECT_ANDROID_KEYSTORE_PASSWORD in the current process.' }
    $output = Join-Path $repository '.local/releases/android'
    [void][IO.Directory]::CreateDirectory($output)
    $result = Join-Path $output "iConnect-Android-$VersionCode-$VersionName.apk"
    if (Test-Path -LiteralPath $result) { throw 'A package already exists for this version at the output path.' }
    $candidate = Join-Path $output ('.candidate-' + [Guid]::NewGuid().ToString('N') + '.apk')
    $android = Join-Path $repository 'apps/android'
    $previousSdk = $env:ANDROID_HOME
    try {
        # The build and its Gradle workers do not need access to the release key passwords.
        $env:ICONNECT_ANDROID_KEYSTORE_PASSWORD = $null
        $env:ICONNECT_ANDROID_KEY_PASSWORD = $null
        $env:ANDROID_HOME = $sdk
        Push-Location $android
        try {
            $arguments = @('--no-daemon', '--console=plain', ':app:assembleRelease', "-PiconnectReleaseVersionCode=$VersionCode", "-PiconnectReleaseVersionName=$VersionName")
            if ($Offline) { $arguments += '--offline' }
            & ./gradlew.bat @arguments | Out-Host
            if ($LASTEXITCODE -ne 0) { throw 'Android release build failed.' }
        }
        finally { Pop-Location }

        $unsigned = Join-Path $android 'app/build/outputs/apk/release/app-release-unsigned.apk'
        $archive = [IO.Compression.ZipFile]::OpenRead($unsigned)
        try {
            if ($null -eq $archive.GetEntry('assets/iconnect/sbom.cdx.json')) { throw 'The Android release APK is missing its embedded SBOM.' }
        }
        finally { $archive.Dispose() }
        $signing = @('-jar', $signer, 'sign', '--ks', $keyFile, '--ks-key-alias', $KeyAlias,
            '--ks-pass', 'env:ICONNECT_ANDROID_KEYSTORE_PASSWORD', '--v3-signing-enabled', 'true', '--v4-signing-enabled', 'false')
        if ($keyPassword) { $signing += @('--key-pass', 'env:ICONNECT_ANDROID_KEY_PASSWORD') }
        else { $signing += @('--key-pass', 'env:ICONNECT_ANDROID_KEYSTORE_PASSWORD') }
        $env:ICONNECT_ANDROID_KEYSTORE_PASSWORD = $storePassword
        $env:ICONNECT_ANDROID_KEY_PASSWORD = $keyPassword
        & $java @signing --out $candidate $unsigned | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'APK signing failed.' }
        $env:ICONNECT_ANDROID_KEYSTORE_PASSWORD = $null
        $env:ICONNECT_ANDROID_KEY_PASSWORD = $null
        $hash = Test-ReleaseApk $candidate
        [IO.File]::Move($candidate, $result)
    }
    finally {
        $env:ANDROID_HOME = $previousSdk
        $env:ICONNECT_ANDROID_KEYSTORE_PASSWORD = $storePassword
        $env:ICONNECT_ANDROID_KEY_PASSWORD = $keyPassword
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { Remove-Item -LiteralPath $candidate }
    }
}

[pscustomobject]@{
    Path = $result
    ApplicationId = 'com.iconnect.proof'
    VersionCode = $VersionCode
    VersionName = $VersionName
    SignerSha256 = $ExpectedSignerSha256.ToLowerInvariant()
    Sha256 = $hash
}
