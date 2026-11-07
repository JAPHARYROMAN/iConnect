# Phone-initiated direct connections and STUN configuration

The Android app can now start a direct connection to its saved paired computer. Connect both apps to the coordinator, then choose **Connect to computer** on the phone or connect from Windows. Android shows a connecting state and a cancel control. **Disconnect from computer** ends direct access and pauses associated transfers while keeping the coordinator connected. Phone initiation requires a visible app, current pairing approval, healthy owner policy and a connected coordinator. It does not create storage permissions or resume copied files automatically.

## Session and collision behavior

Android's native session supports creating an offer/data channel or answering a received offer. The signed authentication transcript orders identities, DTLS fingerprints and challenges by SDP initiator/responder. The initiator can be either device. Mutual signed proofs, selected direct-candidate checks and both `ready-v1` acknowledgements remain required before file RPCs.

When both clients have outgoing attempts at once, the lexicographically lower pinned device ID keeps its outgoing offer. The other side closes its attempt and answers the winner. Both clients retain bounded IDs for superseded or refused attempts and ignore their late messages after checking signatures, freshness and nonce replay. Replayed nonces remain errors. A rejected background offer's candidates cannot replace a selected-transfer session. Windows additionally checks and enqueues each outgoing signal under the session-replacement gate so an old offer/candidate cannot overtake a new answer. The normal coordinator socket preserves message order.

Windows serializes delivery of verified remote descriptions and candidates with native transport disposal. For an identified session, Windows retains up to 128 early candidates until it applies the remote description. When the native transport rejects negotiation or the early-candidate limit is reached, the attempt ends and the coordinator can accept a fresh connection attempt. Envelope, signature, freshness, replay and session checks run before native delivery.

The coordinator context permits at most 64 recorded session IDs and 2,048 received signal nonces. Reconnect to the coordinator to start a fresh context when needed. Windows' existing optional reconnect policy refreshes idle contexts earlier. Android automatic reconnect and trusted online presence remain separate outstanding work; this phase adds deliberate phone initiation.

## STUN settings

Android exposes **STUN address (optional)** below its coordinator field. Disconnect to edit; successful coordinator registration saves the selected coordinator and STUN setting. Session creation snapshots the setting. Windows keeps its existing **Settings → Connection** field and now applies the same stricter validation at the native transport boundary as well as settings validation.

The supported form is `stun:host[:port]` or `stun:[IPv6][:port]`, at most 256 characters. An empty setting disables configured STUN. The clients accept ASCII DNS names (including punycode), IPv4 and bracketed IPv6, with ports 1–65,535. Shared vectors cover malformed ports/hosts, credentials, paths, query/fragment syntax, controls, Unicode host input and relay schemes. The current clients deliberately support the UDP `stun:` form only; `stuns:` is not configured. No default external service is selected.

STUN assists endpoint discovery and is distinct from a file relay; its operator observes network metadata. This distinction is described in [RFC 7064](https://www.rfc-editor.org/rfc/rfc7064) and [ICE, RFC 8445](https://www.rfc-editor.org/rfc/rfc8445). File data still requires the authenticated DTLS channel, and relay candidates remain rejected. Configure an operator-controlled or deliberately chosen STUN endpoint before the independent-network field test; same-network success does not prove NAT traversal.

## Verification and remaining evidence

- Full Release .NET tests: 184 core/storage/model/coordinator/native tests and eight Windows preview tests pass. The subsequent signal-enqueue hardening also passes the focused collision/STUN/native/coordinator checks.
- Shared STUN vectors pass in .NET and Android JVM tests (10 JVM tests total).
- Windows collision tests hold both outgoing signed offers until both exist, exercise both device-ID orderings, authenticate a single surviving native channel, deliver a fresh signed candidate from the superseded attempt and verify files in both directions.
- Windows native STUN test uses a generated local UDP binding responder and observes a reflexive candidate. Android device tests independently exercise a local responder and observe their native reflexive candidate. The fixture returns a documentation-only test address; this proves configuration reaches discovery, not internet routing.
- Android device connection tests authenticate native peers in both roles using a real Keystore identity and a test-only software identity, verify mutual readiness and exchange bounded file requests in both directions. Absent storage authority returns AccessDenied with no entries/content. Additional tests cover malformed STUN, offline initiation, role/description ordering and closed-session behavior. The software identity exists only in the instrumentation fixture; production controllers construct the Keystore identity.
- Full Android device/UI regression: `OK (87 tests)` in 96.75 seconds, including the four new connection tests and all prior storage, recovery, explorer, lifecycle and isolated-preview tests. Debug/test APK, unsigned release APK, 10 JVM tests and lint build successfully. Lint reports zero errors and 42 existing advisories. A final test-only cleanup removes redundant nullable assertions without changing application code. The unsigned APK is not a signed distribution release.

Fresh Windows–Android coordinator interoperability for the new initiation path remains **unverified**. Automatic approval review rejected starting the local development coordinator with “blocked by policy” and supplied no more specific reason. The action was not retried through another launcher. Earlier cross-platform evidence in [phase 18](phase-18-bounded-previews.md) predates these connection changes and does not prove this new path. The [completion audit](completion.md) retains the broader pairing, presence, reconnect, platform and physical-network gates.

To run the prepared phone-initiated interop test once a coordinator is running:

1. Build/install the current debug app and test APK on the disposable development emulator. Start the existing development coordinator and `IConnect.InteropRunner` as in [the interoperability instructions](verification.md).
2. Supply the fresh runner invitation to `com.iconnect.proof.InteropTest` with instrumentation argument `initiator=phone`. Optional `stun` supplies a deliberately chosen STUN URI. The normal binary protocol and matching-code approvals are still required.
3. Compare codes and supply `approve <code>` to the Windows fixture. After `PAIRED`, the phone starts its own offer and reports `PHONE_INITIATED`; do not issue the runner's `connect` command for this variant.
4. After Android reports `SENT`, promptly issue the existing runner commands `explore-phone`, `download-phone-folder`, `download-phone`, `create-phone-folder` and `send`. Require the corresponding Windows verification messages and Android `MEDIA_PREVIEW_VERIFIED`, `OFFLINE_STATUS_BOUNDARY_VERIFIED` and `OK (1 test)`.
5. Also run the existing Windows-initiated variant without `initiator=phone`. Record both outcomes, then exercise phone cancel/disconnect, revocation, foreground/background and both simultaneous-offer outcomes on real Windows/Android clients.

The separate-network physical-phone kit remains prepared for the operator to run after implementation. No physical-network success or full-app completion is claimed here.
