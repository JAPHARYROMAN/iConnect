# Native transport patch candidates

`serialize-sctp-initialization.patch` targets libdatachannel v0.24.6, commit `6b1e2e620f1e37f0eafeee702eaea0043cb305fd`. The source builder applies it only with **`-SctpInitializationFix`**. The application still uses its existing default transport. No patched binary is included here.

The candidate adds a mutex around SCTP transport initialization, following the existing DTLS initialization pattern. Two callers can otherwise observe an empty SCTP member, construct different transports and overwrite that member. Destroying the replaced transport can stop the shared lower DTLS transport. A local failing trace contains more SCTP constructors than peer/DTLS constructors and records native closure before managed disposal. This is evidence for a race candidate, not proof that this patch resolves every observed disconnect.

An isolated copy of the pinned source builds successfully with this patch and passes 958 selected main cases. A stronger [diagnostic scheduling comparison](diagnostics/README.md) records 26 SCTP constructions for 22 peers and a stalled native call on the unpatched source. With the same pauses and the mutex, all four selected cases finish and 70 peers produce 70 SCTP constructions. A separate native/ICE failure remains unresolved. See the [investigation record](../../docs/development/native-sctp-investigation.md).

The optional public build path is:

```powershell
./tools/build-native-transport.ps1 -SctpInitializationFix
./tools/test-native-transport.ps1 -ArtifactDirectory "$env:USERPROFILE/.cache/iconnect/native-transport/0246-openssl365-sctp-init1/artifact"
./tools/export-native-transport-sources.ps1 -BuildDirectory "$env:USERPROFILE/.cache/iconnect/native-transport/0246-openssl365-sctp-init1"
```

The default cache stays separate. `native-sctp-initialization.json` pins the patch bytes and both resulting source files, with UTF-8/LF normalization for the latter. Other source changes, staged changes and unlisted inputs are refused. Exported bundles retain the upstream base archives, exact patch, source hashes and build recipe. Package inventories identify the patch and its source hashes. The first complete public build and package validation are running; these commands are not yet evidence of a finished release gate. Default adoption, broader regression, license/provenance closure and physical-network validation remain open.

For an isolated source checkout, apply with `git apply --check` followed by `git apply`, then verify both inserted mutex lines. Run from a checkout with its own `.git` directory: an unrelated parent repository can cause Git to skip paths. Preserve the original checkout and binary. This patch modifies MPL-2.0-covered upstream files; retain upstream licensing and publish the corresponding modified source with any distributed binary.
