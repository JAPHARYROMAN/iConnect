# Supported OpenSSL baseline

The support-policy review on 9 November 2026 found that the earlier OpenSSL 3.6 line is out of support. The [official download policy](https://openssl-library.org/source/) lists 3.5, 4.0 and 4.1 as supported; [3.5 LTS support ends on 8 April 2030](https://openssl-library.org/policies/releasestrat/). The repository now pins OpenSSL **3.5.9**, commit `45e844fa2a14ec92d146bd8f5778ac130b6625fb`, while retaining libdatachannel 0.24.6 and both exact SCTP/DTLS corrections. The [3.5 advisory page](https://openssl-library.org/news/vulnerabilities-3.5/) is reviewed separately from support eligibility. A zero-match advisory lookup does not establish that a dependency is maintained.

## Selection and historical inspection

Application builds require the current source-lock hash, both corrections, and an unexpired explicit support date. A separate application cache prevents reuse of the earlier default. The new OpenSSL libraries are compiled from the LTS checkout; the 3.6 build cannot satisfy reuse validation.

The old lock is retained exactly at `tools/native-source-locks/0246-openssl365.json`, SHA-256 `d8d8c75efa7114c6c7df7984fc199415dafa222de5ba95506d86847313ab0b53`. Generic bundle inspection accepts only the current lock or that exact historical lock. Historical artifacts remain inspectable, including the original failure comparisons and package controls, but application selection rejects them. New source bundles include the archived lock so their inspection recipe is complete. A self-consistently edited bundle manifest cannot replace the known catalog hash.

## Tag authentication

The annotated tag `openssl-3.5.9`, object `d0ca66a1abe52545f14eca635c648932fcde5615`, binds the pinned commit. Its detached signature verifies with the current primary key published on the [official downloads page](https://openssl-library.org/source/): `B146647E45A7B33947AB226B2A2C87D161692D40`. The signing subkey is `C46ED3F2CBEFDA1FDAADA44264ED7B1DCCE71CB2`. The authoritative public-key bundle came from [OpenSSL's own server](https://openssl-library.org/source/pubkeys.asc).

Verification uses an isolated public-only keyring, with automatic key retrieval and agent startup disabled. Both GPGv and full GPG verification succeed; full GPG reports no signature/key expiry or revocation error in the supplied official key material. GPGv alone does not check those conditions. Changing the signed payload causes signature rejection. The [tag verification record](evidence/openssl-lts-tag-verification-2026-11-09.json) binds the exact payload, signature, official keys and verifier output. Earlier path/socket setup failures remain preserved locally.

This authenticates the Git tag against the official key material retrieved on the review date. It does not verify a release tarball signature, prove bit-for-bit reproducible compilation, or close provenance/license/security review for every native component. OpenSSL's [artifact signing policy](https://openssl-library.org/policies/general/artifact-signing-policy/) excludes individual Git tags from its release-artifact scope.

## Qualification status

The [initial LTS checkpoint](evidence/windows-native-lts-checkpoint-2026-11-09.json) passes the source build, nine selection guards, Release solution build, thirteen configured upstream cases, reconstruction of all three modified source files, and 60 tooling tests. The replacement DLL is `016d8b449da214db2fc34ce900d13c539c2b56d885af9fbd870f326005249c68`. Its complete exported bundle has 31 verified files and seven source components. Subsequently all 959 selected main Windows cases and the nine explicit native application fixtures pass on that DLL; these fixture cases also occur in the main selection.

The [bounded constrained stress record](evidence/windows-native-lts-stress-2026-11-09.json) subsequently verifies 200 qualified samples, 800 passing test executions and 6,400 concurrent handshake pairs on the same test assembly used by the natural failure control. Every actual test host records affinity mask 3, with two logical processors selected. All 200 native logs are retained in separate sample directories, without dropped records or diagnostic scheduling pauses. No fatal DTLS startup I/O error is recorded.

The native logs contain five other error entries: one SCTP connection-failure notification and four SCTP writes rejected because the SSL protocol is already shut down. All corresponding tests pass. These messages can arise around closure, but the error-only hook does not establish their exact case/lifecycle association; that limitation and every message remain in the record. The original wrapper observation failures and an interrupted attempt are also preserved. One sample has passing JUnit output but a failed process-observation wrapper and no retained original exit; it is excluded from the 200 qualified samples and was not repeated. The final 55 new samples use the unchanged diagnostic hook's actual in-host affinity record.

The remaining capacity/large-file, packaging and Windows–Android checks are in progress. Historical 3.6 results are not transferred to the replacement binary. Physical-phone/separate-network validation remains prepared for owner execution; Windows GUI validation remains stopped following the earlier Escape request. Packages remain unsigned review artifacts.
