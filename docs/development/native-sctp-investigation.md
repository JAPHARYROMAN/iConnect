# Native SCTP initialization investigation

The native disconnect is now reproduced locally. Restricting the existing four readiness/cleanup and concurrent-handshake cases to two logical processors produces three passes and one failure on the first sample using the default vendor library. Assertions, deadlines and concurrent pair counts remain unchanged. The probe stops on failure. This preserves a reproducible local failure alongside the earlier hosted failures; it does not replace them with later passing results.

## Correlating native and managed teardown

`iConnect-NativeTransport` is an opt-in, process-local .NET EventSource. It emits numeric peer/channel handles, numeric connection/ICE states, lifecycle event names and exception type names. It records no SDP, candidates, pairing material, device identity, session identifiers, paths or file payloads. It is disabled without an attached listener. No native acceptance, authentication, failure or cleanup behavior changes. Native connection state 3 still terminates the affected transport.

A disposable test-host startup hook enables debug logging from the exact application-local DLL and listens to these lifecycle events. It is kept outside the shipped application. The raw native debug logs can contain signalling credentials and remain private. The committed [evidence record](evidence/windows-native-sctp-investigation-2026-11-09.json) contains hashes, counters and selected numeric lifecycle facts instead.

The correlated default-library trace records two failed cases out of four, with zero dropped diagnostic records. Seventy peers and seventy DTLS constructors produce seventy-two SCTP constructors. For the failed peers, native disconnection precedes managed disposal. Around the first failure, an SCTP destructor stops DTLS while another SCTP connection attempt is active. ICE state 3 means completed ICE; connection state 3 means disconnected. These are different native enums.

## Candidate correction and limits

In pinned upstream `src/impl/peerconnection.cpp`, SCTP initialization can be called both by the DTLS-connected callback and remote-description processing. It checks the shared member before construction, but has no initialization mutex. `emplaceTransport` stores the new member unconditionally. The base transport destructor stops its lower transport. DTLS initialization already has its own mutex. The [candidate patch](../../tools/native-patches/serialize-sctp-initialization.patch) applies the same serialization pattern to SCTP initialization.

The original source trees, vendor DLL and existing source-built control remain intact. A separate patched source overlay builds successfully using MSVC, static OpenSSL 3.6.5 and the pinned libdatachannel revision. Its artifact hash is recorded. The patched binary has not been adopted by the application.

Initially, the unpatched source-built control passes ten bounded two-processor samples: forty case executions and 320 concurrent pairs. It also passes one four-case trace with a diagnostic-only 50 ms pause in each SCTP constructor log callback. The patched library passes the same four-case delayed probe. Both controls use the same managed assembly and startup hook. Those initial passing comparisons provide compatibility evidence only; differences between the vendor and source builds remain a confounder. The stronger source comparison below addresses the missing overlap reproduction.

## Matched source scheduling comparison

The [follow-up record](evidence/windows-native-sctp-comparison-2026-11-09.json) adds the same two scheduling pauses to separate source builds: 200 ms in remote-description processing after committing the description, and 600 ms in SCTP initialization after its empty-member check. The only other differences between their two peer-connection source files are the mutex declaration and lock. No extra initialization caller is introduced. Both builds use the same pinned dependencies, static OpenSSL installation, MSVC settings, managed assembly, diagnostic hook, test filter and two-processor limit. The scheduling patch is [preserved separately](../../tools/native-patches/diagnostics/widen-sctp-window.patch) and is excluded from the public production build path.

The unpatched control stalls in a native description call. Its managed stack shows `NativeRtc.SetDescription` inside the native call and teardown waiting for the signalling gate. A private heap capture recovers the logging hook's 1,116 queued records: 22 peers, 22 DTLS constructions and 26 SCTP constructions. The queue capacity is 2,048, below its 4,096-record limit; it has not reached the dropping threshold. Three readiness/cleanup cases complete. The selected stress case never completes. The owned host is stopped after approximately twenty minutes, after retaining its process identity, stacks and heap. Its aborted run and the collector's missing exit-time trace are preserved. This is an incomplete control, not a four-case passing run or a completed failing assertion.

The separately launched mutex variant completes all four selected cases in approximately fifteen seconds, including all 32 concurrent pairs. Seventy peers produce seventy DTLS and seventy SCTP constructions, with no dropped trace records or EventSource metadata errors. This establishes a concrete duplicate-initialization defect under the imposed schedule and supports the mutex correction. It does not establish that every earlier native failure has this cause.

## Separate native/ICE failure

The ordinary patched experimental DLL subsequently passes all 958 selected main cases, without failures/skips, in 5.3500 minutes. A separate bounded two-processor sequence stops on its second sample: the first four cases pass; the second records three passes and one concurrent-handshake failure at round 2, pair 1. Connection history is `1,4`; the host has ICE state 2 and an attached but unopened channel, while the guest has ICE state 4 and no attached/opened channel. This differs from the earlier `1,3` disconnection and remains unresolved. Concurrent compilation and the main regression were active during this sequence; those conditions are retained as context, not an explanation for dismissing it.

An ensuing diagnostic-only sequence on the same patched DLL enables native/lifecycle tracing and passes ten bounded samples: forty case executions and 320 pairs. Each trace records 70 peers and 70 DTLS/SCTP constructions, without drops or metadata errors. It does not capture or resolve the ICE failure. The original failing sample remains a failure; the changed tracing sequence serves cause collection. No connection state is ignored.

## Optional build and source closure

`build-native-transport.ps1 -SctpInitializationFix` selects a separate default cache and one pinned patch definition. Patch bytes and normalized complete source-file hashes are validated; staged changes, unlisted files and unrelated edits are refused. Patch metadata and inputs travel with the artifact. The exporter retains the original upstream archives plus the exact patch and reconstruction recipe. The bundle reader validates the patch against the current definition. Package inventories record the patch identifier, path, digest and both resulting source hashes; the package inspector checks those properties.

All 39 tooling tests pass, including twelve new patch/artifact guards. Nine additional checks on an actual pinned Git checkout exercise clean-source acceptance, patch application/idempotence, rejection of altered source, an untracked input, staged source, a wrong revision and misrepresenting patched source as unmodified. The existing unpatched DLL also exports and reopens successfully as a seven-component bundle under the updated tools. An initial observation incorrectly counted a null patch field as one entry; it is retained separately and corrected to the validated empty inventory. The export itself remains valid.

That public SCTP-only build subsequently completes. Its DLL `71cc514e…` passes nine native fixtures, all 958 selected main cases, source reconstruction and unsigned package inspection, including six guards and the patch metadata. The [DTLS follow-up](native-dtls-startup.md) then captures a natural handshake failure on that same artifact and reproduces a separate initialization-order defect. The combined correction has its own artifact and validation scope. Default adoption, full license review and field/release gates remain open.

## Checkpoint validation

The lifecycle-tracing variant builds in Release with zero warnings/errors. Sixteen selected cases pass on the unchanged default vendor library: native connectivity, readiness/cleanup, concurrent handshakes, uploads/folder moves and bounded failure-diagnostic controls. These normal-scheduling passes do not resolve the two-processor failures. The preceding committed checkpoint `1830e3f` also finishes all four GitHub jobs successfully, including Windows capacity, isolated previews, publish-asset checks and large files. Earlier failing runs remain recorded.

The current Android full-size matrix continues independently on its frozen APKs. Its active upload result and complete three-case matrix are not yet established. This checkpoint makes no full-application, release-readiness or physical-network claim.
