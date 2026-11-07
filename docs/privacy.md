# Privacy and local data

This guide describes the repository implementation reviewed on 7 November 2026. It explains what the current applications and coordinator handle and which owner controls affect that data. A future release or hosted service needs its own identified operator, contact, retention commitments and completed review before publication. The [completion audit](development/completion.md) tracks the remaining release and security gates.

## Who can see what

| Participant | Data handled by the current implementation |
| --- | --- |
| Your Windows or Android device | Its device key and settings; pinned peer identities and pairing approvals; folder permissions; local paths or provider URIs; transfer names, sizes, hashes, checkpoints and outcomes; private payload snapshots/partials; selected history and recovery metadata. Previewing and saving also process file contents locally. |
| A paired device with current permission | The approved operation's file or folder metadata and contents over the authenticated encrypted direct session. Read, Write, Create and other operation permissions remain separate. Receive-only locations do not expose their existing contents for browsing. |
| Your selected coordinator | Device names, public keys/derived device IDs and live registration; short-lived invitations and pairing approvals; signed session descriptions and ICE candidates, which can contain network addresses. Opt-in availability updates also identify the paired devices included in those updates. The host sees connection addresses and timing. |
| An optional STUN operator | Network discovery traffic and the addresses/timing needed to answer it. There is no default third-party STUN address; the owner supplies one. |
| A selected Android document provider | Files or diagnostic reports read from or written to that provider and the related document operations. A cloud-backed provider can store or sync them under its own service rules. |

The coordinator routes pairing and connection messages. The repository coordinator has no file relay or file-storage API; file operations and payloads use the direct data channel after peer authentication and readiness. Signalling is protected in transit by HTTPS, but its metadata is visible to the coordinator. Direct encrypted transport does not hide the participating devices' network addresses from each other or turn the system into an anonymous service.

The coordinator's application state is in memory. Registered identities are removed on disconnect, invitations expire or are consumed, and availability observations expire. A restart loses that state. Application-level routine signalling failures log only a fixed exception category at debug level. Host, service-manager, firewall and any surrounding infrastructure logs have their own access and retention settings; this repository cannot promise deletion of those logs or the absence of host-level capture. See the [hosting procedure](development/coordinator-release.md).

## Keys and data on your devices

Windows signs using a per-user CNG key configured as nonexportable. Android signs using an Android Keystore key. These choices do not establish hardware-backed storage on every device. The app does not include the private signing key in invitations, file transfers or diagnostic reports.

Transfer records need filenames, hashes, identities and destination information to check progress and avoid silently adopting a different file. Private snapshots and receiving parts can hold complete or partial file contents after a pause or interruption. They remain separate from original source files, published receiver copies and ordinary activity history.

The applications do not add a separate encryption layer to all local metadata, private snapshots or saved files. Their protection also depends on the OS user/app boundary, device encryption, backups and the selected provider. The Windows development desktop profile normally resides under the user's local application data at `iConnect/desktop`; packaged installations use their package-local data directory. The console host uses a separate profile. Android stores private application state in its app-private storage.

The Android manifest disables application backup and defines exclusions for cloud backup and device transfer. That does not control copies in selected document providers, OS/vendor behavior outside the app or files copied elsewhere. Exporting an entire application profile is not a supported way to migrate trusted identity and storage authority. Lost keys or inconsistent restored policy require the app's review/reset and fresh pairing where indicated.

## Permissions and availability

Android requests network access for coordination/direct transport, camera access for QR invitation scanning, and notification/foreground-service/wake-lock capabilities for explicitly authorized continuing transfers. Folder access comes from the system picker and its URI grants; the manifest does not request all-files storage access. You can paste an invitation instead of using the camera. The app's own permissions and background authorizations still apply after Android grants a capability.

General Android browsing and folder batches stop or pause outside the foreground. A specifically authorized ordinary transfer can continue with notification controls. Windows closes availability by default when its window closes; optional notification-area availability and sign-in startup are separate owner settings. Pausing availability does not erase local state.

Both devices initially disable paired-device availability reporting. When enabled, the coordinator receives the peer identifiers needed for the bounded availability query. A peer is reported available only under the mutual opt-in rules, and an observation expires. Availability does not authenticate a direct session or approve storage access.

## History and diagnostic exports

Local activity supports Off or 1-, 7-, 30- and 90-day retention with at most 500 entries; the default is 30 days. Records can include timestamps, compiled action/outcome categories, a peer identity, byte counts and repeat counts. The activity view is bounded and can lose events during process death or storage failure. It is not a complete or tamper-evident audit trail. Age retention runs when history is loaded or updated, rather than as an always-running deletion service.

Turning recording off or clearing activity removes the active activity history after confirmation. Transfer receipts, private files, pairing and preserved recovery archives are separate and remain. Details are in [local history and retention](development/phase-51-local-activity-retention.md).

Diagnostic reports show the exact JSON before an owner deliberately chooses where to save it. They include app/OS versions, capture time, aggregate counts and fixed connection/queue/recovery states. They exclude device and peer names/IDs, keys, invitations/codes, endpoint addresses, filenames, paths/URIs, contents/hashes, activity entries and raw errors. Counts and timestamps can still reveal usage. Reports are bounded to 32 KiB and are not uploaded automatically. A document provider selected for the export may sync the saved report. See [reviewed diagnostics](development/phase-50-reviewed-diagnostics.md).

## Choose the control that matches the data

| Owner action | Effect and retained data |
| --- | --- |
| Stop sharing or revoke/forget a peer | Withdraws the relevant future sharing/trust authority. Previously delivered files and copies on either device remain. Android URI access still needed by another local destination or queued job can retain a separate lease. |
| Pause or disconnect | Ends or suspends the relevant live work. Saved receipts, partials and published copies remain for review/resume. |
| Cancel a transfer | Stops the selected work and cleans owned private partials where supported. Completed files and created directories remain; uncertain provider publication can require review. |
| Remove a history record or private sending copy | Applies only to the selected metadata or private source after its review. Read the confirmation: private sources, sender receipts, receiver receipts and published files have separate lifetimes. |
| Clear activity / turn recording off | Clears active activity history. Preserved history backups and transfer/recovery stores remain separate. |
| Review and remove recovery data | Acts on the exact reviewed recovery area/items. Preservation/reset can keep earlier metadata or file contents until a later explicit removal. |
| Uninstall or clear app data | Can lose identity, approvals and recovery controls. Files saved outside app-private storage can remain, and Windows key/profile behavior needs the installed package's uninstall validation. This is not a coordinated erase of both devices or providers. |

None of these controls promises secure erasure of storage media, provider backups, another device's copies or infrastructure logs. Use the app's review controls before removing state needed to identify an interrupted copy.

## Implementation references

The data boundaries above follow the [coordinator entry point](../backend/IConnect.Rendezvous/Program.cs), [in-memory broker](../backend/IConnect.Rendezvous/PairingBroker.cs), [Windows identity](../shared/IConnect.Core/DeviceIdentity.cs), [Android identity](../apps/android/app/src/main/java/com/iconnect/proof/LocalIdentity.kt), [Android manifest](../apps/android/app/src/main/AndroidManifest.xml) and [backup exclusions](../apps/android/app/src/main/res/xml/data_extraction_rules.xml), together with the linked storage, history and diagnostic procedures. Independent review, physical-provider/network evidence and final release approval remain open. Report security concerns through [SECURITY.md](../SECURITY.md).
