# Set up iConnect and make a first copy

iConnect is being developed for Windows x64 and Android 10 or later. The native applications, coordinator and packaging tools are implemented; the [completion audit](development/completion.md) records the remaining validation and release work. This guide describes the current controls. A completed setup does not establish that every network or document provider is supported.

## Get the applications

For development builds, follow the repository's [Windows instructions](../README.md#run-the-native-windows-app) and [Android instructions](../README.md#build-and-run-android). Keep the Windows executable and its runtime files together. The Windows desktop app and console development host have separate profiles and device identities.

For release preparation, use the [Windows MSIX procedure](development/windows-release.md) and [Android APK procedure](development/android-release.md). Production signing, installed update/uninstall checks and final release approval remain required. A locally built unsigned package is a review artifact.

Both devices need the same coordinator origin. On separate networks, use an HTTPS origin reachable by both devices with a certificate they trust. The [coordinator hosting guide](development/coordinator-release.md) describes the current direct-TLS deployment. Its health endpoint checks signalling readiness; it cannot prove that the devices can connect directly.

For a local development emulator, the README documents the loopback server and Android's `10.0.2.2` host alias. Those HTTP development exceptions do not apply to release builds or physical-phone internet testing.

## Pair the intended devices

1. In Windows **Settings**, configure the coordinator while disconnected, then connect. On Android, configure the same coordinator and connect when prompted by the pairing flow.
2. In Windows, choose **Devices → Add device**. On Android, choose **Scan QR invitation**, or paste the invitation. Invitations expire after two minutes and can be used once.
3. Choose **Request pairing** on Android. Compare the displayed codes and approve on both devices only when they match the intended pairing. If the invitation expired, create a new one.
4. Check the paired-device entry on each device. Pairing pins the other device's public key. Storage approval is a separate owner decision.

Keep invitations and codes private. A changed device identity requires fresh pairing and storage approval. If saved policy needs review, use the app's recovery controls; the [owner-recovery guides](development/phase-15-windows-owner-recovery.md) describe preserved metadata and the limits of restoration.

## Approve one test folder

Start with a new folder containing a small generated file. Windows **Shared locations** approves a selected folder for one paired device. Android **Choose phone folder** uses the system folder picker while disconnected. Android may restrict selectable locations or withdraw a provider's access.

Choose permissions for the intended operation:

| Intended use | Approval |
| --- | --- |
| Browse and download files from this device | Read-only sharing of the selected source folder. |
| Send files into a location on this device | Write and Create for the receiving location, or a receive-only inbox. A receive-only inbox permits reception without exposing its existing contents. |
| Copy into a local destination selected on the receiving device | The receiver's local folder selection. It does not share that destination with the other device. |

Approving an entire drive is a broader decision than approving a folder. Existing exclusions, expiry and pause controls still apply. Local explorer shortcuts do not create sharing approvals.

## Connect and copy a phone file to Windows

1. Keep iConnect open on Android. Connect both devices to the coordinator, then choose Windows **Connect to device** or Android **Connect to computer**.
2. Wait for the authenticated direct-connection status. A coordinator availability report is an observation; file access starts only after direct authentication and readiness checks.
3. In Windows, open **Browse files**, select a file in the approved phone folder and choose **Save selected file to this computer**.
4. Select a local Windows destination and confirm. Follow the receipt in **Transfers** through receiving, verification and saving. Existing destinations are preserved using a unique name.
5. Check the final result and the saved file. A byte count alone does not establish publication or completion. For a batch, inspect **Item results**; **Completed with issues** includes items that were skipped or not copied.

For the reverse direction, approve a Windows source folder. While Android is disconnected, choose **Choose download folder**. Reconnect and authenticate, browse the approved computer folder, then choose **Download** for a file. Follow its private verification and provider-save controls. Some document providers cannot publish atomically; an uncertain save stays available for review.

Private sending is also available from the local explorers: prepare a private file or folder copy, review the plan and select an approved receiving location after connecting. This can use a receive-only inbox without sharing the original source folder. The [native batch guide](development/phase-41-native-batch-controls.md) describes preparation, results and separate history/private-copy controls. Fresh complete cross-platform workflow validation remains open.

## Pause, reconnect and review

Pause retains durable progress. To resume, reopen the same apps, reconnect the same trusted devices and use the retained transfer's resume action. Folder batches pause when Android leaves the foreground. A selected ordinary file can continue only after its explicit background authorization and notification are active; this does not authorize other files or browsing.

Cancel ends the selected work and cleans its owned private partials where supported. Files already published and directories already created remain. If the app reports an uncertain copy, use **Check saved copy** or the corresponding review action before deciding to create another copy. Starting another request can deliberately create an additional receiver copy.

The [privacy and local-data guide](privacy.md) explains the separate effects of revoking a peer, clearing history, removing private snapshots and removing saved files. These actions serve different purposes.

## Resolve common setup problems

| Observation | Next check |
| --- | --- |
| Cannot register with the coordinator | Confirm the exact HTTPS origin, host reachability and trusted certificate. Keep debug emulator addresses limited to the documented development setup. |
| Paired device appears available, but direct connection fails | Keep both apps active. Review the optional STUN setting while disconnected. There is no default third-party STUN server and no TURN relay fallback; some network pairs cannot establish a direct path. |
| Connected, but no files are listed | Check that the source owner approved a current folder for this pairing with Read access. A receive-only inbox intentionally has no browsable contents. |
| Android destination is unavailable | Choose a supported folder again through the system picker. Existing queued records retain their own destination and may need separate review. |
| Copy pauses after leaving Android | Keep the app open for general browsing and batches, or explicitly authorize the supported individual transfer through its notification controls. |
| Saved progress or publication needs review | Keep the receipt and existing copies. Use the displayed recovery/check action; do not infer success from the presence of a file with a matching name. |

For support, review and deliberately save a diagnostic snapshot through the app. Its aggregate counts and capture time can reveal usage. No support upload occurs automatically. See the [diagnostic export details](development/phase-50-reviewed-diagnostics.md) and [security reporting instructions](../SECURITY.md).

The [physical-network test kit](development/physical-network-validation.md) is prepared for a Windows PC and a physical phone on separate connections. Its result must be recorded after execution; emulator and same-host checks do not satisfy that gate.
