# Phase 0: connectivity foundation

## Scope and trust boundary

This increment proves the accountless networking path before building remote filesystem access. Windows currently uses a console host; Android uses a Compose activity. Files are synthetic test data only, stored in an application-owned inbox under opaque transfer IDs. There is no command execution, personal-folder access, drive enumeration, or automatic elevation.

The coordinator is a single ASP.NET Core process with bounded in-memory presence and invitation state. `/health` is a health check. `/signal` is a native-client binary WebSocket endpoint; it refuses browser Origins. HTTPS is required except for loopback requests in the Development environment. There is no reverse-proxy trust configuration, durable device directory, account database, or file endpoint.

The initial capacity is 64 registered connections, one connection per device identity, one current invitation per owner, a two-minute invitation lifetime, a ten-second registration timeout, and a one-hour maximum connection lifetime. Connections are rate limited per remote IP, messages per connection are limited, and binary frames may not exceed 64 KiB. These are prototype bounds, not a production abuse-prevention system. Scaling and rate-limit tuning require deployment measurements.

## Identity and pairing

Public identities are canonical DER SubjectPublicKeyInfo encodings of P-256 keys. The device ID is the lowercase hexadecimal SHA-256 of those bytes. Signatures use SHA-256 with ECDSA and ASN.1 DER signature encoding, which is shared by .NET and Java. Names are display labels and do not determine identity.

Registration proves possession of the private key over a fresh 32-byte server challenge. Pairing invitations have a 32-byte random, Base64URL-encoded secret. A QR invitation contains the configured coordinator origin, secret, and host public key. Scanning does not authorize switching coordinators or saving trust.

The first claim consumes the invitation's claim slot. Both devices reconstruct a pairing transcript, compare a six-digit digest-derived verification code, and approve locally. The coordinator forwards both signed approvals. Each endpoint verifies both signatures, the expected identities, transaction, token, expiry, and its own local-approval state before persisting trust. The Windows owner also pins the original local invitation expiry so the coordinator cannot extend it. A second claim, missing approval, changed identity, or expired transaction fails closed.

Windows keys are non-exportable CNG software-provider keys in the current user context, separated by named local profiles. Android keys use Android Keystore; the current prototype does not claim hardware attestation or StrongBox protection. The Android app disables backup. A changed key requires re-pairing. Local revocation removes the pin and closes the active session.

## Transcript format

Every transcript is UTF-8, starts with `iconnect/<purpose>/v1\n`, and joins the following fields with LF, with no trailing LF. Raw CR/LF characters are rejected in fields. Base64URL is unpadded. Integers use invariant decimal formatting. Device names are excluded from signed identity assertions and remain untrusted labels.

| Purpose | Ordered fields |
| --- | --- |
| `register` | Base64URL server challenge. |
| `pair-approve` | Transaction ID, invitation token, Base64URL host public key, Base64URL guest public key, expiry in Unix milliseconds. |
| `signal` | Session ID, sender device ID, target device ID, signal kind, Base64URL UTF-8 body, Base64URL UTF-8 media ID, Base64URL nonce, creation time in Unix milliseconds. |
| `session` | Session ID, initiator device ID, responder device ID, normalized offer DTLS fingerprint, normalized answer DTLS fingerprint, Base64URL initiator challenge, Base64URL responder challenge. |

Protobuf is the message schema, not the signature canonicalization format. Signalling bodies and media IDs are Base64URL-encoded in transcripts to avoid newline or delimiter ambiguity. Key representation, role order, time units, and encoding must stay identical on both platforms.

## Direct transport and authentication

Windows uses libdatachannel through a small C API binding; Android uses the WebRTC Android bindings. No TURN server is configured. Endpoints reject relay and unknown candidate types, including embedded candidates in SDP. SHA-256 DTLS fingerprints are required, and audio/video media descriptions are rejected.

Offer, answer, and trickled candidates are signed by the pinned sender identity for the target device, session, nonce, and time. Messages older than one minute or too far in the future are rejected; development devices need reasonably synchronized clocks. A bounded per-connection nonce set rejects repeated signals, and session IDs cannot be reused within that coordination connection.

After DTLS and the DataChannel open, the peers exchange fresh 32-byte challenges and signatures over the session transcript. File messages are rejected until this succeeds. The transport implementation checks the remote DTLS certificate against the signed SDP fingerprint, and the application proof binds the stored identity, both fingerprints, both roles, and both fresh challenges. Selected ICE candidate information is inspected to confirm a direct path.

There is one `iconnect-v1` ordered, reliable binary DataChannel. An additional or incorrectly labelled channel is rejected. Queues and frames are bounded. Negotiation and application authentication have timeouts. The prototype has no relay fallback, automatic router reconfiguration, or certificate-verification bypass.

## Test transfer contract

Only `iconnect-test.bin` metadata is accepted, with an opaque 32-byte Base64URL transfer ID, size at most 1 MiB, and a 32-byte expected digest. Each session accepts at most four incoming files. Both clients' send actions generate 256 KiB of random test data.

The receiver acknowledges the start and each contiguous chunk. Chunk data is limited to 16 KiB. Stop-and-wait acknowledgements bound outstanding work in this feasibility build; this is not the production throughput design. The receiver writes a new `<transfer-id>.part`, hashes as it writes, verifies the final SHA-256, flushes the destination, and moves it to a non-overwriting `<transfer-id>.bin`. Only then does it send the success result. The sender verifies the returned digest before reporting completion.

Unexpected offsets, duplicate acknowledgements, excess sizes, wrong names, checksum mismatches, and unknown operations fail the session. Failed partial files never receive a completed extension. Resume and automatic partial-file cleanup are deliberately not implemented yet. There are no cross-device moves or deletion operations in this increment.

## Availability and known gaps

Android disconnects and closes its peer session in the activity's `onStop`. Scan a QR invitation before connecting, or paste it after connecting. There is no background access or transfer job in this prototype. Windows runs while the console process is alive. Both peers must be registered with the same live coordinator, and Windows currently initiates a session after both sides report successful pairing.

Trust is persisted locally; presence is ephemeral. The Android proof app currently pins one computer; the Windows test host can store multiple peers. Neither client automatically reconnects after coordinator failure. Reconnect manually and create a fresh direct session. A lost device must be revoked separately on each host. Revocation cannot remove already received copies.

The reviewed production pairing protocol, robust replay persistence, identity migration, frontend usability, transport failure recovery, metered-network controls, unattended Windows lifecycle, and storage-provider/drive authorization remain future work. Do not deploy this development service publicly as a finished remote-file-access product.

## Validation boundaries

Automated .NET tests exercise real native encrypted peers and the coordinator-to-pairing-to-signed-signalling-to-transfer path. Android instrumentation has also demonstrated actual Windows/Android interoperability on an isolated local emulator. The [verification record](verification.md) contains the results and reproduction steps. This establishes a local implementation baseline. It does not prove physical Android compatibility, NAT traversal between independent internet connections, resilience on restrictive networks, or a completed security review.

Before advancing to filesystem work, record a Windows-to-Android transfer on separate real networks, both checksums, selected direct candidates, failure cases, application lifecycle behavior, and evidence that the coordination service never handles file payloads. Independent authentication and native-binding review remains a release gate.

## Dependency sources

- [libdatachannel C API](https://github.com/paullouisageneau/libdatachannel): the Windows native transport; MPL-2.0. Its NuGet redistribution is pinned as `MediaToolkit.WebRtc.Native.win-x64` in the project file.
- [WebRTC Android builds](https://github.com/webrtc-sdk/android): the Android native transport, pinned in Gradle.
- [Protocol Buffers](https://github.com/protocolbuffers/protobuf): shared messages and generated codecs.
- [QRCoder](https://github.com/codebude/QRCoder): Windows invitation rendering.
- [ZXing Android Embedded](https://github.com/journeyapps/zxing-android-embedded): QR capture launched only on user action.

These dependencies remain subject to version, source, licence, and vulnerability review before production distribution. Native binaries and generated APKs are build output, not repository source. No distribution licence is assigned to iConnect's own source by this increment.
