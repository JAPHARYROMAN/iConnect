# Outgoing batch history and reviewed cleanup

The subsequent [receiver-history milestone](phase-40-receiver-batch-history.md) adds the receiver's separate admission/receipt budgets and reviewed item-history retirement. This report records the earlier outgoing-history implementation and evidence.

Windows and Android senders now separate the active outgoing queue from archived local receipts. Up to eight jobs can occupy active slots, with four per peer; up to 32 additional jobs can remain archived. These are sender limits. Receiver plan-history limits and private-source reservations remain separate.

Archiving changes a durable local flag. It preserves the original receiver header, item attempts, outcomes and result records, and frees the local active slot. Archived jobs cannot issue send/stop RPCs. Restoring requires an available active slot and valid retained item results, and restores the same job and receiver intent. An unfinished job cannot restore over another active job using the same private source. Once a source has an earlier remote intent, queueing another copy requires an explicit additional-copy choice.

History changes use short-lived, single-use owner reviews bound to the job's parent-record version and every reviewed result-file version. Confirmation rejects changed jobs, changed files, modified review details, active work and reused reviews. History ownership checks retain the local identity/generation boundary while allowing the same local owner to clean up receipts after peer revocation. None of these actions requires or creates a network connection.

## Removing local history

Purge is available only for archived jobs. Its review identifies the job, source, number/bytes of result records and whether the original receiver may hold copies. This is a local metadata action: it does not stop a receiver plan, release remote reservations, remove private source snapshots, delete originals or alter published/uncertain receiver files. Removing an unfinished intent loses this sender's ability to resume or stop it; the receiver owner may still need to review its retained plan and staging.

The sender saves its purge intent before removing any result record. Each explicit cleanup call removes at most 64 known result files, then checkpoints its cursor. A restart can continue the same saved intent. Only missing files within the current interrupted cleanup window are tolerated; missing future records block recovery and preserve the remaining state. A final result written before its parent cursor advanced is included in the owned cleanup inventory. The parent intent is removed last, after no further job objects remain.

Unknown objects, links, changed reviewed records and native ownership failures are preserved. Windows deletion uses held native handles. Android checks the app-private directory identity and matches named/held file stamps with no-follow opens. Paths are derived only from validated job IDs and canonical item indexes; no source or receiver path participates in metadata cleanup.

Archived records are inert. Startup validates their parent records, bounded inventory, file ownership, sizes and contiguous result names without interpreting every result body. Restore validates every required result before enabling the job. Explicit metadata purge can therefore remove reviewed, damaged archived result bodies without treating them as valid transfer receipts. Non-archived jobs retain strict result validation on startup.

Sender parent records now use schema 2. Existing schema-1 jobs load with no archive/cleanup flags, keep their original authority and are upgraded on a subsequent write. Earlier readers reject schema 2 rather than silently resuming an archived job.

## Controller integration

Both controllers expose owner review, archive, restore, purge continuation and private-source removal operations. Android dispatches history operations to IO and requires the foreground. Its shared owner gate makes the current-send check atomic with private-source removal. Windows holds the sender gate across its source-use check and removal, preventing a concurrent queue/restore action from bypassing that check. Private-source removal keeps its existing separate native ownership review and does not occur as a side effect of archiving or purging history.

Complete native preparation, destination, queue, history, item-result and uncertainty controls remain open. The batch capability remains unadvertised. Receiver history separation/per-plan management, damaged or absent parent recovery, full-capacity history retirement, crash/storage/provider matrices, sustained-load performance, native accessibility and fresh encrypted/physical-network evidence are not established by these local APIs.

## Verification

The Windows solution builds with zero warnings/errors. Its final selected main regression passes **386 tests in one minute six seconds**, including 15 history/source-guard cases and all 16 prior non-capacity sender cases. **Eight isolated preview tests pass in nine seconds**. The earlier main run passed 384 cases before the guarded-source-removal and sibling-history cases; an earlier focused run passed 26 sender/history cases before the migration/checkpoint refinements.

History cases use actual private snapshots and the production local receiver/filesystem with an injected accepting attachment checker. They cover archive/restore, original-intent recovery, active-work rejection, changed/reused reviews, local owner boundaries, revoked peers, 32 retained history slots, deliberate additional copies, actual 70-item publication followed by bounded/restarted cleanup, damaged result bodies, unexpected objects, schema migration, a result ahead of its cursor, missing metadata beyond the cleanup window, guarded source removal and sibling-job preservation. Private sources, originals and published files are checked separately from metadata removal.

Android debug and unsigned release packaging pass, along with **39 JVM tests** and lint with **zero errors/67 advisories**. The broader device regression passes **236 tests in 480.944 seconds**, including the first 14 history cases. It excludes four live-invitation classes and four previously separate receiver/source capacity cases. A final test-only follow-up adds sibling-job preservation and expands the existing controller test to verify guarded private-source removal. **All 16 targeted device tests pass in 133.296 seconds**: 15 history cases and the expanded controller case. The production APK is unchanged by that follow-up.

The 236-case run used app SHA-256 `259019c5b3968abe23d4ea8a6ffb82c60006a554a78d91e3ef2f7db76b2a7201` and test APK `13870739d2fbe666a4af93ab49ceb5256766d05ceb4af5509c038aedc556c40c`. Installed hashes matched the local build before and after the run.

The final targeted run uses the same app hash and test APK `34983381f082c1faf71c14458ac87e5d1cadcea504d393d8a49ff498a524b2c6`. Installed hashes match before and after this run. The owned test emulator was shut down afterward.

No current 4,096-item history cleanup, native Windows UI, fresh encrypted session or physical-network result is claimed for this change.
