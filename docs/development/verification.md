# Phase 0 verification record

**Date:** 2 November 2026 (Africa/Nairobi). This records local feasibility evidence, not a production certification or a separate-network field test.

## Environment

- Windows x64, .NET SDK 10.0.403.
- Windows native transport: `MediaToolkit.WebRtc.Native.win-x64` 0.24.6.1 (libdatachannel).
- Android API 35 x86_64 in a newly created, isolated test emulator; the existing personal emulator's data was not used for test installation.
- Android app compiled against API 36 with JDK 21, AGP 8.13.2, Kotlin 2.3.0, and the checked-in Gradle 8.13 wrapper.
- Android native transport: `io.github.webrtc-sdk:android` 150.7871.04.
- Coordinator listening on Windows loopback, reached by the emulator through `10.0.2.2`. No STUN or TURN server was used in this local test.

## Results

| Check | Result |
| --- | --- |
| .NET solution build | Passed with zero warnings and zero errors. |
| .NET security, native transport, coordinator, and CNG tests | 16 passed. |
| Android debug APK | Built successfully. |
| Android JVM rules tests | 4 passed. |
| Android lint | Passed. |
| Android Keystore instrumentation | Passed: identity persisted across instances; valid DER signatures verified; altered transcripts rejected. |
| Android Compose activity smoke test | Passed: activity launched and displayed without a startup failure. |
| Windows-to-Android interoperability fixture | Passed: normal coordinator registration, invitation claim, mutual signed approvals, signed signalling, identity-bound direct channel, and verified synthetic transfer in both directions. |

The interoperability fixture used the production protocol/client methods with disposable test state. Android's separate test APK invoked its local approval action automatically; the Windows fixture was supplied the matching code observed from Android. There is no auto-approval switch in the application APK or normal Windows host.

The Android runner reported `PAIRED`, `AUTHENTICATED`, `SENT`, and `BIDIRECTIONAL_VERIFIED`, then `OK (1 test)`. The other two Android instrumentation tests reported `OK (2 tests)`. Selected-candidate checks ran before the authenticated state was published.

The successful synthetic transfers were 256 KiB each. Windows reported these verified digests:

| Direction | SHA-256 |
| --- | --- |
| Android to Windows | `c630367b9be8775f8062c380aa09b0805aaf1c85c88817e57edf6a6c07edad77` |
| Windows to Android | `4e913b7bcbd9e7e1e4df3ab4914ff38900e1fc028d8fe50a38b447934f016505` |

These hashes identify random test payloads from this run. They are not fixtures that later runs should reproduce. No personal files were transferred. Invitation secrets and pairing codes are deliberately omitted from this record.

## Reproduce the interoperability check

1. Build the .NET solution and both Android debug APKs (`:app:assembleDebug :app:assembleDebugAndroidTest`). Install both APKs on a disposable development emulator.
2. Start `./tools/dev.ps1 server`.
3. Run `dotnet run --project tests/IConnect.InteropRunner` using the configured .NET 10 SDK. This separate fixture prints a fresh invitation and accepts `approve <code>`, `connect`, `send`, and `quit` on stdin.
4. Run Android's `com.iconnect.proof.InteropTest` through `androidx.test.runner.AndroidJUnitRunner`, supplying the fresh invitation as the instrumentation argument `invitation`. The fixture expects the standard emulator host alias `10.0.2.2:5078`.
5. Compare the `CODE` reported by Android with the Windows fixture's code, then supply `approve <matching-code>` to Windows. After Android reports `PAIRED`, supply `connect`. After it reports `SENT`, supply `send`. Expect `BIDIRECTIONAL_VERIFIED` and a successful test result.
6. Quit the fixture and stop the temporary coordinator and emulator when finished.

On PowerShell, quote the whole remote `adb shell` command and use single quotes around the invitation inside it; its `&` characters must remain part of the argument rather than shell operators. Passing an invitation directly as an unquoted remote shell argument will split it incorrectly.

The Keystore and activity smoke tests do not require an invitation. An invocation of the interoperability test without an invitation is skipped, not counted as a successful network test.

## Remaining feasibility gate

Still required: a physical Android device and a Windows computer on independent internet connections, a reachable HTTPS coordinator, explicitly configured STUN discovery as appropriate, selected direct-candidate evidence, both final file digests, and recorded restrictive-network failures. Exercise app foreground/background transitions and network changes on the real phone.

This local emulator result does not establish internet NAT traversal, broad Android hardware support, production transfer throughput, resume behavior, approved-drive security, or the complete release security model. Those remain separate implementation and validation work.
