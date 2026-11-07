# iConnect protocol compatibility

Windows, Android and the coordinator currently use binary Protobuf frames with `Frame.version = 1`. An absent or unsupported frame version ends that connection. Reconnect with compatible releases. Keep wire compatibility separate from package versions and from the format of local trust, approvals and transfer history.

## Coordinator and client compatibility

| Connection | Required | Optional behavior |
| --- | --- | --- |
| Client to coordinator | Receive one 32-byte challenge, send the signed registration and receive one acknowledgement for the local device ID. Only then can pairing and signalling proceed. | A version-1 acknowledgement without capability fields still permits registration and signalling. Presence stays disabled until the coordinator advertises `presence-v1` and the owner enables it. The client opts in to `signal-unavailable` through `Registration.signal_status`. |
| Client to paired client | Pinned device identities, the signed DTLS-bound transcript, a direct ICE path and both `ready-v1` acknowledgements. | `files-v1` enables file requests. Other operations also require their own capability. Examples include `downloads-v1`, `mutations-v1`, `created-folders-v1`, `recovery-v1`, `uploads-v1` and `upload-batches-v1`. Fresh published-copy checks require both `uploads-v1` and `published-upload-check-v1`. |

New capabilities are disabled for peers that omit them. Unknown capability labels within the frame limits can coexist with known ones. A capability only describes a feature. The receiving client still checks the current pairing, owner permissions and operation-specific authority before accessing storage.

## Release rules

- Keep existing field and enum numbers stable. Add new optional fields under new numbers and reserve retired numbers. Specify the behavior for absent fields. [Protobuf binary schema evolution](https://protobuf.dev/programming-guides/proto3/#updating)
- Introduce a new frame kind or remote operation behind an explicit capability. Keep its meaning stable. Treat changes to signed transcripts, required authentication steps and existing operation semantics as protocol changes. A release that requires a new major version needs an explicit transition plan and must reject peers that lack it.
- Keep a scripted baseline fixture for the oldest supported behavior. Before declaring support for a new release pair, verify actual versions in both initiator roles, pairing and reconnection, a basic file operation, and refusal of features the other side lacks. Use disposable profiles for upgrades and review storage migration separately.

## Verification boundaries

The Android controller fixture delivers binary messages through the WebSocket callback. It exercises a legacy acknowledgement, signatures on the challenge response, unsupported frame versions, unsolicited and repeated registration messages, and a late callback from a previous connection. Windows uses a scripted version-1 peer on a native WebRTC DataChannel to prove that baseline file requests still work, optional requests are withheld and missing mutual readiness blocks authentication. The in-process coordinator suite exercises registration without the newer `signal_status` opt-in.

The Windows Release build passed with zero warnings and errors. All 39 focused tests and 850 selected Windows tests passed, with three capacity tests excluded from the selected run. Android debug, test and unsigned release builds passed alongside 84 JVM and 59 selected emulator tests. Android lint reported zero errors and 71 warnings. The first emulator run exposed two fixture mistakes. Availability preference setup now happens before connection and the upload refusal case now targets the peer without batch reception.

Record actual mixed Windows and Android release pairs and a deployed coordinator in [step 61 of the physical network test](physical-network-validation.md). Those results are needed to claim installed cross-platform compatibility.
