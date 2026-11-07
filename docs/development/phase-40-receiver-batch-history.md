# Receiver final receipts and item-history retirement

Finished receiver plans no longer consume the eight active admission slots or the four-active-plans-per-peer allowance. Both stores separately bound detailed plan metadata to 40 records, including active work. Admission reserves a detailed-record slot before any effects, so completing an admitted plan does not depend on obtaining another history slot.

An owner can review a terminal plan and remove its detailed item history. A compact parent receipt keeps the original header, pairing/destination authority, terminal phase, effects-started barrier and final outcome array. Up to 256 compact receipts are retained separately from detailed plans. They answer authorized parent status/finish/stop requests and prevent an old Begin from creating another copy. Detailed item operations return unavailable after retirement begins. The batch protocol and capability advertisement remain unchanged.

## Reviewed, resumable metadata removal

Retirement requires terminal state, idle verification, unchanged final outcomes and absence of known private file staging. Reviews are short-lived and single-use, bind every metadata-file version and are checked again before committing the local intent. Active or stopping plans cannot retire. A changed or reappearing partial, changed reviewed metadata or an unknown job object preserves the history for local review.

The saved retirement intent includes the original attempt bitmap. Cleanup first removes file journals while all child and ancestor bindings remain available to verify their scopes and private-part absence. It then removes child receipts. Each call advances at most 32 item indexes in one pass and persists its cursor. Missing metadata is tolerated only within the current interrupted cleanup window. Future required records and the bitmap remain checked on restart.

The attempt record is removed last, followed by replacing the detailed parent with its compact receipt. A restart after that final removal can still finish the same transition. Retirement never deletes source originals, published/uncertain destination objects or private parts; parts must already have ended through normal completion or Stop. No destination object is adopted by name.

Compact receipts are not individually discarded, which would allow an old identifier to be admitted again. When every plan is terminal and no retirement is pending, the owner can preserve and reset closed history. This retains a private backup and rotates the history namespace before new admission. Earlier requests then fail with a stale namespace. The existing forced history-recovery reset remains a separate operation with its existing interruption warning.

## Storage and integration

Receiver parent schema 3 records retirement flags, the cursor and the attempt bitmap. Existing schema-1/2 parents remain loadable under their original rules and upgrade when written. Compact records contain no manifest. Metadata loading recognizes partially retired plans without treating intentionally removed journals as lost active-file ownership. Other plans retain full validation.

Windows uses native, single-link file handles and journal-version checks. Android uses app-private directory/file identity checks and no-follow descriptor opens. Android journal capacity now follows the detailed-receipt budget while its private-part count remains limited by active work; receipt retention does not increase staging capacity. The Android owner gate and Windows plan gate serialize admission, state transitions and owner cleanup.

Both controllers expose review, confirmation, continuation and closed-history reset. Android executes owner actions on IO and requires the foreground. Native preparation/queue/history/result/uncertainty controls, damaged-parent recovery, full-capacity retirement, power-loss/storage/provider matrices, performance, accessibility and fresh encrypted/physical-network evidence remain open. The batch capability is still unadvertised.

## Verification

The Windows solution builds with zero warnings/errors. The selected main regression passes **394 tests in one minute fifty-one seconds**, with **eight isolated preview tests passing in nine seconds**. Eight new receiver-history cases cover six sequential real completed batches without exhausting peer admission, compact parent receipt/replay checks, owner review rejection, detailed-capacity release, namespace rotation, actual 70-item publication and both retirement passes across restart, a deleted-child/checkpoint boundary, the 256-summary limit, private-part reappearance, changed metadata and legacy schema migration. Tests use the production local receiver/filesystem with an injected accepting attachment checker. The earlier selected sender/history run passed 36 cases before the final refinements.

Android debug and unsigned release packaging pass, as do **39 JVM tests** and lint with **zero errors/67 advisories**. The final full selected device rerun passes **244 tests in 377.656 seconds**, including all seven receiver-history cases. Four live-invitation classes and four separate receiver/source capacity cases are excluded.

The first broader run finishes 244 cases with **243 passes and one timeout** in the existing moved-download-ancestor case, waiting ten seconds for its generated source to reach a one-MiB pause barrier. The exact case then passes on the same APKs in **0.819 seconds**, followed by the clean full rerun above. No code changed between those runs; the original trigger remains unproven rather than being described as a fixed product defect.

Installed APK hashes match the local build before and after testing: app `f981755ba1accf342c5b3f5c194c99a19604bf7e6108d787d2212503372f17e5`, test APK `295e142340fd3d80d9d13328c30333d719bd8d0abc26e485aae4bc9062682b8e`. The owned test emulator was shut down afterward.

No full-capacity retirement, native Windows UI, fresh encrypted session or physical-network result is claimed for this milestone.
