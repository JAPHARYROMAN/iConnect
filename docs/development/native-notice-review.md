# Native source-notice review

The source-backed Windows review package now includes a readable inventory of notice documents and source comments. Root licenses remain in the original source bundle. The new material preserves additional contributor notices found inside source files, including distinct BSD terms and attributions in usrsctp. For example, the pinned [`user_socket.c`](https://github.com/paullouisageneau/usrsctp/blob/fec583d54493f879d2ae44a743423bf8a04371ab/usrsctplib/user_socket.c) and [`sctp_crc32.c`](https://github.com/paullouisageneau/usrsctp/blob/fec583d54493f879d2ae44a743423bf8a04371ab/usrsctplib/netinet/sctp_crc32.c) contain notices beyond the project's root license.

## Export and package

`tools/export-native-notices.ps1` first validates the seven-component source bundle and requires the expected DLL SHA-256. Its Python helper reads the source ZIPs without extracting source paths, preserves complete matching comment blocks and license/notice documents, and records source-member hashes and detected SPDX expressions. It records candidate text it cannot scan and keyword lines outside recognized comments for further review. This is a bounded inventory, not a complete source-language parser or a determination of license applicability.

The exporter requires Python 3.11 or newer. It refuses an existing output or an output within the source bundle. Archive paths, duplicate members, entry counts, expanded sizes, text sizes and exported material have limits. Inputs and exported documents are checked again before the completed catalog is written. Partial output remains available after failure.

```powershell
./tools/export-native-notices.ps1 -BundleDirectory $verifiedBundleDirectory `
    -ExpectedBinarySha256 $expectedNativeHash -OutputDirectory $newNoticeReviewDirectory `
    -Python $pythonExecutable
./tools/read-native-notice-inventory.ps1 -InventoryDirectory $newNoticeReviewDirectory `
    -BundleDirectory $verifiedBundleDirectory
```

Source-backed `tools/package-windows.ps1` builds automatically export and validate this material under `PackageMetadata/NativeNotices/`. `-NoticePython` can select the interpreter. The package builder checks every staged file against its final MSIX entry. `tools/test-native-package.ps1 -RequireNoticeInventory` additionally requires the readable material and checks its packaged hashes, candidate/bundle bindings and component identities. Earlier source-backed packages can still be inspected without that switch.

The catalog and reader explicitly keep license approval, complete license coverage, actual linker-closure verification and distribution approval false. The inventory includes archived examples and excluded inputs such as JSON; these are not automatically claimed as code in the DLL. Applicability, complete attribution, source-offer obligations and distribution decisions still require review. Default transport adoption and independent security/release review remain open.

## Observed result

On 7 November 2026, the DLL `84a0a63dcc579d97e4afac959866f9e61e9584c18732045a48db673d8ec87dde` produced this inventory:

| Component | Source archive entries | Distinct matching comment blocks | Unexamined text candidates |
| --- | ---: | ---: | ---: |
| libdatachannel | 2,820 | 50 | 0 |
| OpenSSL | 6,196 | 677 | 8 |
| libjuice | 92 | 7 | 0 |
| libsrtp | 2,406 | 13 | 0 |
| usrsctp | 281 | 45 | 0 |
| plog | 200 | 3 | 0 |
| JSON, excluded source input | 1,271 | 60 | 0 |

The result preserves 855 distinct matching comment blocks and 17 notice-document candidates, plus their catalog and combined text. Eight oversized OpenSSL test-vector text files remain unexamined; 101 files contain keyword lines outside recognized comments. These are explicit review items. `SOURCE_NOTICES.txt` is 875,741 bytes; the catalog is 2,239,006 bytes and has SHA-256 `2ba6955c2514aec04d06b913c063d79c007baeb0f4b16fc3cf3034de3b0aea30`.

The unsigned `IConnect.NativeNoticeProof` 1.121.0.0 package has SHA-256 `85a79f79b42f1048cd433034fa899d9957290db2b067ec7ab73bcf8b24aa1de8`. Inspection verifies the original 25 source-bundle members, all 19 supplemental notice files and the 24-component dependency inventory. All six existing package guards pass. Eight additional reader checks refuse changed/missing documents, changed combined notices, a wrong binary binding, false approval, traversal, unexpected files and redirected documents. The original review inventory remains unchanged. The package was created and inspected without signing or installation.

An artifact-bound OSV lookup checks all 24 dependency coordinates and returns no matches on 7 November 2026. Its report is `.tools/phase121-source-package-review/9ae446a9250a4697820591e56779043c/dependency-review.json`, bound to the package hash above. This does not establish absence of unlisted vulnerabilities or replace independent review.

Five Python test methods cover later contributor notices, input changes, unsafe/duplicate archive members, preserved outputs and explicit unclassified/oversized review items. They run locally and are added to the GitHub workflow with Python 3.12. The action revision is pinned to the verified `setup-python` v7.1.0 commit.

Local evidence is retained in `.tools/phase121-native-notices.json`, `.tools/native-notice-review/a96fe38977d5409a9e0f6ee964e60c18/`, `.tools/phase121-notice-guards/79f059d72e8d477cbe888dc9e99537f6/`, `.tools/phase121-package.log` and `.tools/native-package-verification/9789f4c108514a9891b123bd9545fb1c/`. These reports describe review artifacts, not a production release or complete application acceptance.
