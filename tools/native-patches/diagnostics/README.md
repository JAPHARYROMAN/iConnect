# SCTP race scheduling probe

`widen-sctp-window.patch` is a diagnostic input for isolated libdatachannel v0.24.6 checkouts. It must **never** be applied to a shipped transport. The normal builder does not apply, copy or select it.

The patch pauses remote-description processing for 200 ms after committing the description and pauses SCTP initialization for 600 ms after observing its empty shared member. Both existing callers, transport construction and teardown remain intact. Apply the same scheduling patch to both isolated variants, adding `../serialize-sctp-initialization.patch` only to the fixed variant. Their `peerconnection.cpp` and `.hpp` content must otherwise match after removing the two mutex lines. Compile both with the same pinned dependencies, MSVC configuration and OpenSSL libraries.

Use the same Release test assembly and four-case filter for each variant:

```powershell
$filter = 'FullyQualifiedName~IConnect.Tests.NativeSessionFailureTests|FullyQualifiedName~IConnect.Tests.NativeHandshakeStressTests'
dotnet vstest /path/to/isolated/IConnect.Tests.dll "--TestCaseFilter:$filter" '--logger:trx;LogFileName=sample.trx'
```

Set `DOTNET_PROCESSOR_COUNT=2` only in the child test process and restrict that child to processor-affinity mask `3`. Leave the test assertions, deadlines, eight rounds and four pairs per round unchanged. Observe `iConnect-NativeTransport` events alongside private native debug logs. A stalled child must be recorded as incomplete/aborted, with its owned process identity and diagnostic context captured before stopping it; it cannot count as a passing test. Native debug logs and memory dumps can contain signalling credentials and must remain private.

The [recorded comparison](../../../docs/development/native-sctp-investigation.md) retains the aborted control, its recovered constructor/lifecycle counters and the completed fixed variant. This scheduling probe establishes a concrete initialization race; it does not resolve every native failure or validate release readiness.
