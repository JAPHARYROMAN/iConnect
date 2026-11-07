# Build and verify an Android release APK

Use PowerShell 7 on Windows with the repository's JDK, Android SDK 36 and Build Tools 36.0.0. Keep the application ID `com.iconnect.proof` and use a higher version code for each distributed update. The same release signing identity must be accepted by the installed app. This workflow builds a direct-distribution APK. [Android signing and updates](https://developer.android.com/studio/publish/app-signing)

## Build with a release key

Use the release owner's protected keystore. Keep it outside the repository and obtain the expected SHA-256 fingerprint from the release owner's public certificate or an independently trusted prior release. Use 64 hexadecimal characters without separators. The signing key is separate from each phone's iConnect device identity.

```powershell
$env:ICONNECT_ANDROID_KEYSTORE_PASSWORD = Read-Host 'Keystore password' -MaskInput
try {
    ./tools/package-android.ps1 -KeyStore $releaseKeystore -KeyAlias $releaseAlias `
        -VersionCode 2 -VersionName 0.2.0 -ExpectedSignerSha256 $trustedFingerprint
}
finally {
    Remove-Item Env:ICONNECT_ANDROID_KEYSTORE_PASSWORD -ErrorAction SilentlyContinue
    Remove-Item Env:ICONNECT_ANDROID_KEY_PASSWORD -ErrorAction SilentlyContinue
}
```

Set `ICONNECT_ANDROID_KEY_PASSWORD` in the current process if the key has a separate password. The script removes those two variables while Gradle runs and passes passwords to the signer through environment references. `-Offline` reuses cached Gradle dependencies. The script checks APK signing, certificate, alignment, app ID and version before moving the verified artifact to `.local/releases/android/`. It prints the APK and certificate SHA-256 values. Existing output files are preserved. [Android APK signing tool](https://developer.android.com/tools/apksigner)

Review the [embedded dependency inventory](release-security.md) for the verified APK before distribution.

## Verify before installing or updating

```powershell
$verified = ./tools/package-android.ps1 -Apk $candidateApk `
    -VersionCode 2 -VersionName 0.2.0 -ExpectedSignerSha256 $trustedFingerprint
$verified | Format-List
```

Compare the reported artifact hash and version to the reviewed release record and select the intended Android device before installing. In a disposable test, install the earlier signed release, open iConnect, and record its device ID privately. Install the verified update over that installation with `adb -s <serial> install -r <apk>`. Reopen and check that the device ID, settings, pairing and receipts you exercised in the earlier version are still present. Check a generated file transfer after updating. Android enforces signature compatibility and version-code checks when updating an installed app. [Android versioning](https://developer.android.com/studio/publish/versioning)

The existing development APK uses the same app ID with a debug signer. Moving between debug and release normally requires uninstalling the old build first. Uninstalling removes app-private data, including the phone identity, pairings, settings, private snapshots and transfer receipts. Keep any desired generated copies in a user-selected destination and review remaining provider documents before uninstalling. Reinstall and pair again for a fresh test. Use disposable data for this check and record whether shared provider files survive on the test device.

Record the artifact hash, signer fingerprint, version, device and Android version, first install, upgrade, downgrade refusal, and uninstall/reinstall results. Use the [physical test guide](physical-network-validation.md) to check the installed releases on separate networks. Production signing authority and a release distribution channel are tracked separately in the [completion audit](completion.md).

## Development evidence

Two APKs signed with a disposable key passed independent host verification. The verifier refused an APK from a different valid signer, a version mismatch, an unsigned APK and an APK altered after signing. A repeated build targeting an existing output path was refused.

A fresh Android 15 emulator installed both reviewed APKs and reported the expected versions and installed APK hashes. The update kept the device ID shown by iConnect. Android rejected a same-version package signed by another key, a lower version signed by the first key, and debug access to the release app's sandbox. Uninstall kept an independently generated shared file with the same hash. Reinstall generated a new device ID. The test used an unpaired app with generated data. Physical-device lifecycle checks with active pairings and transfer history are still open.
