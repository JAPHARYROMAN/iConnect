# Windows source lifetime and reviewed recovery

Windows standalone remote downloads now retain the source's reported expiry, show a payload-time lower bound at the configured bandwidth limit and offer a reviewed fresh source for an unfinished download. Source authority is replaced only after explicit approval; resuming remains a separate action.

## Retained expiry and estimates

`DownloadJob.SourceExpiresAt` is optional for older records and is learned from a valid source descriptor. Ordinary status replies can shorten a known deadline, but cannot clear it with an unknown value or extend it. The receiver refuses another range batch after its recorded deadline. Source-side grant, identity, version and ticket-expiry checks still apply to each read. Server source expiry now uses the grant store's existing time provider consistently; production remains on the system clock.

`TransferTimeEstimate` calculates remaining declared bytes and an overflow-safe minimum payload duration at the configured cap, assuming the file has the whole directional budget. It is a lower bound, not a completion promise. Shared work, source preparation, network conditions, hashing and Windows attachment checks can take longer. Unlimited/unknown rates do not invent an ETA. The UI warns when the lower bound reaches or exceeds the peer-reported remaining approval window. Device-clock differences can affect reported expiry; no clock is changed or trusted as proof of peer authorization.

Fully received, hashed copies keep their existing local verification/security path without fetching another source copy. Zero-byte transfers remain supported. Local copies retain their own authority and are not subjected to this remote-expiry check.

## Fresh-source review

An owner selects one current file in the authenticated phone explorer, returns to an idle interrupted/paused standalone download and chooses **Review fresh source from selected phone file…**. Preparation and the final source check are cancellable. The selected file must have the retained name and size and belong to the same currently authenticated source peer. A captured requester cannot follow a later session or another device.

The manager reads current metadata, requests a distinct source ticket and waits for its complete prepared hash. Metadata, identity/version, size, ticket and expiry are checked. When a previous full hash exists, the new source must match it exactly. A source with changed metadata but identical full content can be reviewed. Different content is refused without modifying the old receipt or partial bytes.

When no original full hash exists, recovery is available only without a received checkpoint. The review explicitly says that the selected source supplies the new full hash; it does not claim a comparison with unknown original bytes. A partially received record without a full hash cannot use this operation.

Reviews are memory-only, single-use, limited to eight preparation/review contexts and expire after five monotonic minutes. The review binds the full receiving-record stamp, current connection predicate and prepared source descriptor. Before adoption, the source's ready descriptor is rechecked. Revocation, changed source data/metadata/expiry, changed or removed receiving state, changed connection, reused/modified/expired review or cancellation prevents adoption. Previously owned/reviewed source tickets cannot be substituted as a fresh lease.

Approval durably changes only the source ticket, opaque node, version, matching hash, reported expiry and explanatory status. It preserves the destination, native root/part identities, byte offset, prefix chain, job ID and collision choice. It does not start another receiving job or resume automatically. The next explicit Resume reopens the identified part, rechecks the retained prefix and verifies the complete payload before no-replace publication and normal Windows security checks. Corrupt checkpoints still fail; a fresh source cannot bless them.

Unused reviews release their new leases on cancellation/failure/expiry cleanup or disposal. Adoption releases the old lease best-effort. Cleanup has a bounded RPC timeout; connection loss/crash can leave a source lease until its existing expiry. Source-side quotas remain unchanged, so preparing a fresh ticket can fail when no source slot is available. Existing authority is kept when preparation fails. Shutdown cancels and waits for preparations/confirmations before releasing remaining reviews.

This operation rejects local-copy and batch-owned receiving records. A missing parent cannot authorize a batch child as a standalone download. Batch-aware recovery and Android receiving recovery remain separate required work.

## User controls and privacy

The Transfers page shows retained progress, the configured-cap lower bound and reported approval lifetime. The review shows the retained destination, checkpoint count, metadata comparison and prepared full hash. It explains unknown original hashes and asks for explicit fresh-source approval. No private keys or invitations are involved. The fixed activity entry describes approval without adding filenames, paths, hashes or ticket identifiers. Review cancellation does not create a destination copy.

Windows GUI automation remains stopped after Escape. The controls compile, while actual picker/navigation/keyboard/screen-reader behavior remains unverified. The [field procedure](physical-network-validation.md) includes step 39 for later user execution.

## Verification

The Release solution builds with zero warnings/errors. **57 focused/related receiving checks pass** in 24 seconds, including **34 new cases** (26 recovery cases and eight estimate cases). Coverage includes actual 25-hour expiry simulated through injected clocks, same-part recovery and restart, changed metadata with matching bytes, different full hash, single-use/cancelled/changed/expired reviews, connection/peer/selection/revocation changes, removed/cancelled receipts, corrupt prefixes, collisions, batch ownership, no-prior-hash and zero-byte jobs, and cancellation/shutdown during both preparation and confirmation. Malicious status replies cannot clear or extend recorded expiry.

One case runs the real file protocol over two authenticated native Windows peers, pauses a generated 2 MiB-plus file at a 1 MiB checkpoint, advances the source/receiver test clocks past approval expiry, reviews a fresh source and completes exactly one verified destination file. This uses the existing encrypted loopback test seam and the real Windows receiving/attachment path; it is not Windows GUI or fresh Windows–Android evidence. Tests do not change the system clock or require the blocked standalone coordinator.

All **681 selected Windows cases pass** in 3 minutes 38 seconds. The final Release build completes in 13.03 seconds with zero warnings/errors. Eight production/test DLL hashes match before and after regression. Evidence is retained in `.tools/phase65-final-build.log`, `.tools/phase65-focused-final.log`, `.tools/phase65-final-regression.log`, `.tools/phase65-results` and `.tools/phase65-final-hashes-{before,after}.txt`; generated evidence is ignored by Git. The same three existing capacity exclusions remain; no new exclusion is introduced. Android source/binaries were unchanged from phase 64, and the owned headless AVD remains off.

## Remaining work

Android and batch-owned source recovery, measured completion estimates, source-side upload-operation expiry recovery, complete cleanup/retention, long-running throughput/clock/skew/crash/volume/provider matrices and physical interoperability remain open. Existing 24-hour approvals are not extended silently. Native UI/accessibility, signed packaging/update/upgrade/uninstall and independent security/release gates remain in the [completion audit](completion.md). This stage does not establish full application completion.
