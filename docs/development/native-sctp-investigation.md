# Native SCTP initialization investigation

The native disconnect is now reproduced locally. Restricting the existing four readiness/cleanup and concurrent-handshake cases to two logical processors produces three passes and one failure on the first sample using the default vendor library. Assertions, deadlines and concurrent pair counts remain unchanged. The probe stops on failure. This preserves a reproducible local failure alongside the earlier hosted failures; it does not replace them with later passing results.

## Correlating native and managed teardown

`iConnect-NativeTransport` is an opt-in, process-local .NET EventSource. It emits numeric peer/channel handles, numeric connection/ICE states, lifecycle event names and exception type names. It records no SDP, candidates, pairing material, device identity, session identifiers, paths or file payloads. It is disabled without an attached listener. No native acceptance, authentication, failure or cleanup behavior changes. Native connection state 3 still terminates the affected transport.

A disposable test-host startup hook enables debug logging from the exact application-local DLL and listens to these lifecycle events. It is kept outside the shipped application. The raw native debug logs can contain signalling credentials and remain private. The committed [evidence record](evidence/windows-native-sctp-investigation-2026-11-09.json) contains hashes, counters and selected numeric lifecycle facts instead.

The correlated default-library trace records two failed cases out of four, with zero dropped diagnostic records. Seventy peers and seventy DTLS constructors produce seventy-two SCTP constructors. For the failed peers, native disconnection precedes managed disposal. Around the first failure, an SCTP destructor stops DTLS while another SCTP connection attempt is active. ICE state 3 means completed ICE; connection state 3 means disconnected. These are different native enums.

## Candidate correction and limits

In pinned upstream `src/impl/peerconnection.cpp`, SCTP initialization can be called both by the DTLS-connected callback and remote-description processing. It checks the shared member before construction, but has no initialization mutex. `emplaceTransport` stores the new member unconditionally. The base transport destructor stops its lower transport. DTLS initialization already has its own mutex. The [candidate patch](../../tools/native-patches/serialize-sctp-initialization.patch) applies the same serialization pattern to SCTP initialization.

The original source trees, vendor DLL and existing source-built control remain intact. A separate patched source overlay builds successfully using MSVC, static OpenSSL 3.6.5 and the pinned libdatachannel revision. Its artifact hash is recorded. The patched binary has not been adopted by the application or the public build/export pipeline.

The unpatched source-built control passes ten bounded two-processor samples: forty case executions and 320 concurrent pairs. It also passes one four-case trace with a diagnostic-only 50 ms pause in each SCTP constructor log callback. The patched library passes the same four-case delayed probe. Both controls use the same managed assembly and startup hook. Since the unpatched source control does not fail, this is compatibility evidence only. Differences between the vendor and source builds remain a confounder. A matched failing control and broader native regression are still needed before attributing the disconnect to this race or adopting the patch.

## Checkpoint validation

The lifecycle-tracing variant builds in Release with zero warnings/errors. Sixteen selected cases pass on the unchanged default vendor library: native connectivity, readiness/cleanup, concurrent handshakes, uploads/folder moves and bounded failure-diagnostic controls. These normal-scheduling passes do not resolve the two-processor failures. The preceding committed checkpoint `1830e3f` also finishes all four GitHub jobs successfully, including Windows capacity, isolated previews, publish-asset checks and large files. Earlier failing runs remain recorded.

The current Android full-size matrix continues independently on its frozen APKs. Its active upload result and complete three-case matrix are not yet established. This checkpoint makes no full-application, release-readiness or physical-network claim.
