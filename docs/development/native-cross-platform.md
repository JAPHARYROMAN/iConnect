# Direct native Windows–Android transport fixture

This opt-in fixture checks the production `PeerSession` and `RtcTestSession` implementations together. A Windows console process and explicit Android test instrumentation exchange setup, public identities and native connection descriptions through local control pipes and ADB. The encrypted data channel carries authentication, readiness, file payloads, acknowledgements and file RPC. The fixture starts no coordinator and grants no remote storage access.

Each connection role sends one generated 256 KiB file in each direction. Both endpoints verify the transfer checksum; the driver independently compares each sender's reported digest with the other endpoint's saved-file digest. Both also require a denied roots request with no returned entries or content. Windows checks the actual loaded module's absolute path and SHA-256. Production peers enforce the existing direct-candidate and mutual-readiness rules.

## Prepare and run

Build the Windows fixture with the repository's .NET 10 SDK and build the Android app/test APKs with the configured JDK and Android SDK. Use a disposable emulator with the debug app already installed. Finish other instrumentation before running this probe. The driver refuses a mismatched AVD or an already-running app; stopping an idle fixture app is a separate deliberate step after its previous test has completed.

```powershell
dotnet restore tests/IConnect.NativeInterop.Fixture/IConnect.NativeInterop.Fixture.csproj --locked-mode
dotnet build tests/IConnect.NativeInterop.Fixture/IConnect.NativeInterop.Fixture.csproj -c Release --no-restore
# From apps/android, with the configured JDK and Android SDK:
./gradlew.bat :app:assembleDebug :app:assembleDebugAndroidTest --no-daemon
```

From the repository root, select the completed source/notice bundle and the new instrumentation APK containing `NativeTransportProbe`. Supply the SHA-256 of the exact installed production debug APK.

```powershell
./tools/test-native-cross-platform.ps1 -Serial emulator-5568 `
    -ExpectedAvdName iconnect-large-files -NativeBundle $bundleDirectory `
    -TestApk $instrumentationApk -ExpectedApplicationSha256 $installedApplicationHash
```

The driver checks the emulator identity and installed app hash before updating only the test APK. Each Windows role uses a new isolated fixture-output copy with the selected source-built DLL. Control commands enter Android through bounded, sequenced files in one random private probe directory. ADB stdin carries encoded setup data, avoiding shell interpretation and Windows command-line length limits. Synthetic received files are identified by their new transfer names; existing inbox files are preserved.

Reports and process logs are under `.tools/native-cross-platform/<run>/`. A completed `result.json` requires both roles, successful instrumentation and host exits, matching hashes and the expected native payload. Failure preserves logs and stops only processes started for the owned disposable fixture. No Windows application UI is launched.

## Current evidence

The Windows fixture builds in Release with zero warnings/errors. The new Android instrumentation APK builds successfully in 1 minute 55 seconds and contains the standard runner, process-death probe and native transport probe. Its SHA-256 is `02c42e85254bae5f314e27ac8e64b7c6e87d590ab546f146e7cc55b854451b06`; the production APK remains `8df834d764f5b86aa1c643ff696d797716ef66d74363533ed14b8f1626180f57`.

A Windows-only control self-test passes with two isolated instances of the new fixture, the reviewed DLL, matching bidirectional file hashes, denied roots requests and clean process exits. This exercises the host control protocol, not Android. A real ADB stdin/read-back check preserves more than 40,000 characters, Unicode, quotes and shell metacharacters in an isolated setup file. Wrong-AVD and running-app guards both refuse without stopping or installing an app.

Actual Windows–Android execution is queued after the existing Android large-file and process-death sequence completes. Its result remains unproven. This transport fixture does not replace full controller/pairing/coordinator interoperability, the four invitation-dependent tests, physical separate-network validation, provider compatibility, accessibility or production release review.
