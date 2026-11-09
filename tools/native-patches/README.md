# Native transport patch candidates

`serialize-sctp-initialization.patch` is an **experimental, unapplied** change for libdatachannel v0.24.6, commit `6b1e2e620f1e37f0eafeee702eaea0043cb305fd`. The normal application build and native source-build tools do not apply it. No patched binary is included here.

The candidate adds a mutex around SCTP transport initialization, following the existing DTLS initialization pattern. Two callers can otherwise observe an empty SCTP member, construct different transports and overwrite that member. Destroying the replaced transport can stop the shared lower DTLS transport. A local failing trace contains more SCTP constructors than peer/DTLS constructors and records native closure before managed disposal. This is evidence for a race candidate, not proof that this patch resolves every observed disconnect.

An isolated copy of the pinned source builds successfully with this patch. The unpatched and patched source-built libraries both pass an initial four-case scheduling probe, so that comparison does not establish causality. Matched reproduction, broader regression, source export, license/provenance checks and explicit default adoption remain required. See the [investigation record](../../docs/development/native-sctp-investigation.md).

For an isolated source checkout, apply with `git apply --check` followed by `git apply`, then verify both inserted mutex lines. Run from a checkout with its own `.git` directory: an unrelated parent repository can cause Git to skip paths. Preserve the original checkout and binary. This patch modifies MPL-2.0-covered upstream files; retain upstream licensing and publish the corresponding modified source with any distributed binary.
