# Android scoped batch directories

This increment implements Android's provider-backed directory effects for accepted incoming batch plans. Protocol operations 37–38 and the matching Windows effects were introduced in [the preceding increment](phase-29-windows-batch-directories.md). The `upload-batches-v1` capability remains unadvertised: scoped file children, outgoing preparation and native source queues are still unfinished.

## Ownership and provider boundaries

Every operation uses the original batch ID, history namespace and explicitly present item index. The private accepted manifest determines the item's name, directory kind and parent index; the request cannot substitute them. The deterministic child ID binds the paired peer, original header, index and kind. Current owner policy, pairing generation, Write/Create approval, foreground availability, persisted framework permission and exclusions are checked again.

Both platforms now reject zero/negative timestamps in ordinary operation freshness checks even if the wall clock is reset close to the Unix epoch. The reserved zero-timestamp batch child namespace cannot become an ordinary operation through a clock reset. Independent C# and Kotlin tests exercise that boundary with explicit clock values.

The selected tree and every ancestor retain exact document IDs and names. A child is created only beneath a parent with a successful retained receipt. The complete path is checked through `DocumentsContract.isChildDocument` and `findDocumentPath`; changed or replaced parents cannot be recovered by matching their names.

Before provider creation, the host records the parent effects-started state, pending child receipt and checksummed attempt bitmap. A successful receipt records the returned ID only after confirming that it was absent from the bounded earlier child listing, belongs to the exact expected parent and identifies a newly empty writable directory. Returned existing documents, regular files, misplaced/nonempty folders and lost results require review. The host never deletes a questionable provider item, merges into an existing folder or retries an uncertain creation automatically.

SAF providers do not offer a portable atomic no-replace creation primitive. These checks preserve existing documents and reject unverifiable results; they do not establish compatibility with every provider or protect against a provider that lies about its own identities. Real-provider and physical-device validation remain required.

## Collision, receipt and recovery behavior

Skip and fail retain the existing folder without merging; descendants cannot proceed beneath a skipped/failed parent. Keep both chooses an unused name. Receive-only destinations use a random suffix and return no listing or activity timestamps. A retained successful status validates identity and ancestry without inspecting folder contents, so subsequent owner additions do not change the receipt.

The attempt bitmap binds the original batch and manifest hash. Missing child receipts for marked items, missing indexes, checksum damage, orphaned metadata and inconsistent parent identities block incoming writes for local review. A pending receipt becomes a review result without adopting a same-name folder. This addresses accidental metadata damage and interrupted effects; a complete attacker-controlled rollback of private state remains a separate gate.

Once effects may have started, remote cancellation retains the reservation and refuses to discard the batch's ownership history. The local review action pauses connections, preserves private metadata in an archive, rotates the namespace and keeps provider folders. It must be coordinated with file workers before scoped file publication is enabled.

## Bounds and native review

The existing eight-plan/four-per-peer limits and shared incoming-byte reservations remain. At most 4,096 child receipts belong to a plan, each limited to 32 KiB and together to 4 MiB, plus a bitmap record of at most 4 KiB. Two accepted manifests are cached; native inode/size/nanosecond modification and change stamps invalidate a changed private plan. Larger provider IDs can exhaust receipt capacity before the item limit; that is a resource failure requiring a smaller selection, not permission to drop receipts.

Provider collision listings are limited to 8,192 entries and one million ID/name characters. General SAF destination listings now also enforce ID/name and aggregate metadata bounds. The created-folder emptiness check stops after the bounded first entries. Phone review text now distinguishes unstarted plans from plans whose destination items may already exist.

## Verification

The final .NET solution build reported zero warnings/errors. All **307 main tests passed**, and all **8 preview tests passed in a separate run** after the diagnostic improvement described below. These are separate results, not a claim that the initial combined run passed.

Android's final checks passed **37 JVM tests**, debug/instrumentation APK packaging, unsigned release APK packaging and lint (**zero errors, 57 advisories**, including three additional URI-style suggestions). The final installed app and test APKs were verified against their build-output SHA-256 digests before device testing:

| Artifact | SHA-256 |
| --- | --- |
| App | `22c4ed2b19e52d88c652f1808fdeb0bb218def3d6242441520eb50e44d99c5e5` |
| Instrumentation | `ebd6b920e446dafb62cfe0b1c67eb51100c5f81d8678268144b873ebb36c0b3f` |

The final device regression passed **148 tests in 158.276 seconds**. Its explicit selection excludes four live-invitation interoperability cases and the capacity test. The capacity test passed in the earlier run against app digest `c18e44cde1c5cc71d566b5439d4db6aaf88039aaa8df2cb9a8b9710fdfca763a`: it creates **4,096 actual provider directories**, verifies 128 roots with 31 empty children each, reopens the receipt store and recovers the final item. That capacity case was not rerun on the final APK; later changes add error translation, changed-manifest blocking, zero-timestamp hardening and review-log wording.

All 22 new directory cases therefore have passing evidence across those runs. The final run includes the other 21 directory cases and the existing native review dialog. Coverage includes collision modes, receive-only status, lost results, invalid provider returns, moved/replaced parents, missing/damaged receipts, foreground withdrawal and accepted-manifest cache invalidation.

An initial unfiltered Android run scheduled 152 cases: 141 passed, seven failed and four live-invitation cases were assumption-skipped. The actual 4,096-directory case passed. Five expected metadata/stale-parent failures escaped as `IllegalStateException`; the host now returns explicit Conflict/StaleReference responses and tests check those statuses. Two existing image-preview/private-copy waits expired while build/test jobs were also active; both passed in the final run. The initial .NET solution run passed all 307 main tests but had a null preview frame in one of eight preview tests; the test now asserts the preview error before using its frame. All eight preview tests then passed separately with unchanged renderer/deadline limits. The exact causes of the wait expirations and initial null frame were not established; sustained-load evidence remains required.

Process restart and simulated provider-result loss do not establish power-loss/storage-controller durability. That remains part of the crash/storage/provider validation gate before the complete feature is enabled.

These checks do not establish fresh Windows–Android batch interoperability, scoped file-byte transfers, complete native sending journeys or physical-network behavior. The [operator-run network kit](physical-network-validation.md) is prepared for the user's later run after implementation. The [completion audit](completion.md) retains the remaining application scope. Windows Computer Use remains stopped; its earlier fixture window was left untouched.
