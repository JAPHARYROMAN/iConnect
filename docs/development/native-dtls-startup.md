# DTLS startup correction

The remaining connection failure is now captured in DTLS, after ICE has connected. An error-only probe of the ordinary SCTP-corrected library `71cc514e…` stops at sample 61. The preceding sixty samples pass all four selected cases. The failing sample records three passes and one concurrent-handshake failure at round 7, pair 3. Its native trace reports a fatal handshake I/O error in `DtlsTransport::doRecv`; the retained peer context has connection history `1,4` and ICE state 3, meaning completed ICE. The probe adds no scheduling pause and enables no managed lifecycle listener. Its original failure is preserved.

This gives a more specific cause than the earlier untraced state-4 observation. That earlier observation cannot be uniquely attributed retrospectively. The new native error is consistent with a separately reproduced MTU initialization race. The [artifact-bound record](evidence/windows-native-dtls-startup-2026-11-09.json) separates those scopes.

## Receive registration before initialization

The pinned OpenSSL backend registers its incoming callback and enters Connecting before setting the handshake MTU. A pending or fast ClientHello can then reach `doRecv()` first. The [upstream report](https://github.com/paullouisageneau/libdatachannel/issues/1657) describes this ordering problem; it remains a report rather than an accepted upstream release fix. The local Windows comparison independently exercises the pinned source and OpenSSL 3.6.5.

Both comparison builds include the SCTP mutex correction. Both receive the same diagnostic-only 20 ms pause on the passive endpoint immediately before MTU initialization. Their DTLS source differs only in receive-registration order; the peer-connection sources and all dependencies match. The original order fails the existing concurrent-handshake case and records three native handshake I/O failures. The corrected order passes the same case, including all eight rounds and 32 pairs, in 5.5435 seconds. Assertions, deadlines, authentication and concurrency remain intact.

The [production patch](../../tools/native-patches/initialize-dtls-before-receive.patch) moves incoming registration and the Connecting transition below MTU setup and the first OpenSSL handshake call. Early packets remain buffered by the lower transport until registration. It changes no certificate/fingerprint checks, session proofs, readiness requirements, cipher settings or terminal-state handling. The [diagnostic pause](../../tools/native-patches/diagnostics/pause-passive-dtls-before-mtu.patch) is separate and excluded from the public builder.

## Combined candidate

`build-native-transport.ps1 -ConnectionInitializationFixes` selects both pinned corrections in a separate cache. Their definitions bind the exact patch bytes and all three resulting source files. The builder refuses unsupported patch lists, reversed order, altered source and partial/unrelated changes. Source bundles contain both definitions and patches beside the upstream archives. Package inventories identify each correction separately and retain support for the previous single-patch inventory format.

`-ReuseOpenSslBuildDirectory` can reuse an existing matching OpenSSL build. It locks that cache, checks the source revision, source lock, architecture, runtime, assembler and both static-library hashes, and rechecks the inputs after compilation. The fresh transport still compiles from its own pinned checkout. Two actual-cache negative controls refuse a changed SSL library and a wrong OpenSSL revision without rebuilding or changing the validated transport.

The resulting ordinary DLL is `57f119d4…`, with no diagnostic pause. Nine application-native fixtures pass. Its 200-sample error-only sequence also passes: 800 selected case executions and 6,400 concurrent pairs, with the same two-processor limit and unchanged test assembly as the failing baseline. All thirteen configured upstream cases pass against this DLL. The upstream executable returns zero even for failed cases, so validation also requires its explicit thirteen-success/zero-failure summary. These upstream tests include configured public STUN use and do not prove a physical separate-network journey.

All sixty tooling tests pass. The full selected Windows regression then passes all 958 cases without failures/skips. Both patches reconstruct their three exact source files from the exported archive. The unsigned MSIX passes archive/source/notice checks, all six package guards and the inventory checks for both corrections. Its SHA-256 is `3cfb259a…`; it is neither signed nor installed.

All 200 stress console logs and TRX results are retained. The test hosts reuse two PIDs, so PID-only native filenames overwrite two earlier native logs; 198 remain. The retained logs have no fatal handshake errors or dropped records. The complete case outcomes establish the 800-case passing result; native-log coverage is partial. Future collection uses a separate native output directory per sample. The completed sequence is not repeated to hide that limitation.

The application default transport is unchanged. Default adoption, Windows–Android interoperability on the combined artifact, complete license review, physical-network and release gates remain open.
