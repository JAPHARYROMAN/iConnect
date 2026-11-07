# Native Windows desktop milestone

The Windows application now runs as a native WinUI 3 process. This extends [approved storage](phase-1-storage.md); the complete application and release gates remain tracked in the [completion audit](completion.md).

## Implemented behavior

- Overview, device list, QR invitation and matching-code approval dialogs, direct connection controls and clear disconnected states.
- Offline access to pinned device trust, with exclusive local profile ownership and durable revocation. Coordinator access is not needed to forget a device.
- Native folder picker and separate whole-drive selection. Approvals identify one peer and expose permission, exclusion, expiry and enabled-state editing. Read-only is the default. File-writing/deletion permissions that are not yet implemented cannot be newly enabled in the desktop editor.
- Phone-location explorer, directory paging, breadcrumbs, folder creation where permitted, and bounded plain-text previews. Folder metadata is refreshed so permission changes are reflected after refresh.
- Local connection settings, stable CNG identity across device-name changes, normal-user execution, and an availability pause/close path that drains active sessions.
- Bounded local action history: maximum 500 entries, 50 entries per UI page, and removal of records older than 30 days when the app opens or records another event. Invitations, verification codes and file content are not persisted in this history. Full incoming-operation/security audit and Android history are still pending.

The native application owns its connection/controller state. No separate agent, tray process, startup task or IPC endpoint has been added yet. Closing it ends availability. Whole-drive approval still respects the storage engine's protected paths, reparse/hard-link exclusions and Windows ACLs.

Failed trust/grant writes commit neither a new in-memory authorization nor a partial policy update. A new disabled approval stays disabled. Concurrent owner grant creation and device revocation are serialized so a forgotten device cannot retain a late-created approval. The desktop waits for accepted UI actions and in-progress policy changes before disposing the trust store; new actions are rejected during shutdown.

## Verified evidence — 2 November 2026

Locked restore and the Release solution build pass without warnings/errors. The .NET suite passes 55 tests in Debug and Release, including a real loopback Kestrel coordinator flow through `DesktopController`: mutual signed pairing, direct authenticated channel, actual approved fixture-file listing/preview, denied read-only mutation, pause, and cleared remote state. Other new cases verify offline revocation across restart, profile exclusivity, unchanged identity after renaming, disabled new grants, failed persistence, concurrent share/revoke, activity retention/count bounds and invalid timestamp rejection. The final Release UI build also launched and closed cleanly after its shutdown handling was updated.

The self-contained application executable launched on this computer without setting `DOTNET_ROOT` or relying on a global .NET 10 installation. The user-interface check used the computer-use skill to inspect the actual WinUI window at its initial and maximized sizes. It verified connection registration, device availability, directory navigation, preview of `simu.txt`, and creation of `Kazi ya majaribio`. The resulting folder was also inspected on disk. A wide-window layout problem found in the check was corrected and rechecked.

The separate `IConnect.DesktopFixture` uses only newly allocated `.local/desktop-fixtures/<random>/` state, pre-pinned disposable identities and repository-owned fixture documents. It hosts a Windows implementation of the phone-side file protocol. This verifies the WinUI interaction and real protocol/storage path, **not Android hardware/provider compatibility or the human pairing UI**. Actual Android interoperability was exercised separately in the earlier milestone. The production app has no automatic approval or automatic storage-sharing switch.

Android debug/test APK builds, the four JVM tests and lint passed after the shared timestamp-bound changes. No new physical-phone, independent-network, installer, signing or external security-review evidence is claimed here.

## Reproduce the native UI fixture

1. Run `./tools/dev.ps1 build` and start `./tools/dev.ps1 server`.
2. Run the separate `tests/IConnect.DesktopFixture` console project with the configured .NET 10 SDK. It prints its newly allocated `DESKTOP_STATE` directory and waits on stdin for `quit`.
3. Launch `IConnect.Desktop.exe --state-directory "<printed DESKTOP_STATE>"`. Use the generated app output directory, including its runtime files.
4. Select Connect, open Devices, connect to **Phone UI fixture**, then Browse files. Open **Phone fixture documents** and preview `simu.txt`. A create-folder test is permitted only inside that disposable location.
5. Close the desktop before quitting the fixture. Stop the temporary coordinator. Never pre-seed trust or automatically approve user folders as part of normal application startup.

Windows App SDK 2.6.4 and Windows SDK BuildTools 10.0.26100.7175 are pinned with NuGet lockfiles. The project follows Microsoft's [unpackaged WinUI sample](https://github.com/microsoft/winappCli/tree/main/samples/winui-unpackaged-app) and [self-contained deployment guidance](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps). The output is an unsigned development application directory, not the completed distribution package.

## Remaining scope

Durable verified transfers of user-selected files, folders/batches, pause/resume/queues, safe destination finalization and provider-compatible collisions remain outstanding. So do the background agent/tray, full management/search/grid/selection/image/PDF surfaces, Android completion, production coordination deployment, packaging/update signatures and the field/security gates. This milestone does not mark the full-app goal complete.
