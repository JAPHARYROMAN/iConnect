# Windows local file management

The offline Windows explorer can create folders, rename files/folders, move them within a volume and move selected items into recoverable storage. The owner reviews the selected source and destination before applying a change. Existing destinations are never replaced. Local management requires neither pairing nor a sharing approval.

## Authority and reviewed changes

`WindowsLocalManagement` owns a separate `local-management` profile, directory-reference catalog, operation journal and deleted-item catalog. Its internal authority accepts only the empty local owner. Incoming network sessions still require `StorageGrantStore`; the local browser's read-only RPC whitelist is unchanged. No synthetic peer or trust entry is created. Activity uses fixed local action categories without filenames, paths, operation IDs or peer identifiers.

Selections capture native item identity and version through the existing local explorer. Reviewed operations pin exact source-parent and destination identities rather than granting an entire browsing shortcut. Shortcut roots and drives cannot be renamed, moved or deleted. Creating a child folder remains allowed. Moves require the same volume and reject a folder's own descendants. Private iConnect profile contents, links and unsupported filesystem objects are refused. Folder scans are bounded to 4,096 counted entries and 32 relative levels; folder traversal counts can make this limit conservative.

Reviews are memory-only, single use, limited to eight and expire after five minutes. Changing a review, restarting or cancelling before confirmation cannot authorize an effect. Native identities/versions are checked again before applying changes. Existing durable intents and receipt reconciliation prevent silent retries of uncertain effects. The private profile and local-management lease remain native-bound; a second manager cannot acquire the same lease.

The directory catalog admits at most 512 distinct native directory references and 8 MiB of metadata. Existing engine limits include 512 retained operation records, 128 operations per owner within their receipt window and an 8 MiB journal. These are admission bounds, not full-capacity performance evidence. Directory references currently have no pruning workflow.

## Recovery controls

Recoverable deletion moves an item into a reserved vault under its original parent on the same volume. The saved copy has no automatic expiry. The original volume and parent must remain accessible for recovery; this is not an independent backup.

The local explorer exposes paged change receipts and saved-item records. An owner can restore under the original or a new name without replacing an existing item, inspect and confirm recorded native locations, review permanent deletion or explicitly restore remaining data after an interrupted purge. Permanent deletion requires a fresh bounded tree review and exact `DELETE` confirmation. Remaining-data restore requires `RESTORE REMAINS`; earlier deletion is irreversible. Settled receipts retain the existing dismissal rules.

Unreadable operation/recovery journals block new changes and can be explicitly preserved as metadata before starting fresh. Preserving a deleted-item catalog can leave saved data unlisted; it does not erase that data. Missing or corrupt directory references block changes and require profile repair, while embedded saved-item metadata can still support owner recovery. Archived metadata cleanup does not delete file contents. A changed directory catalog during a running session is refused.

Management work and journal loading run off the UI thread. The controller caches history/recovery snapshots, serializes management actions and waits for work during shutdown. Observer exceptions cannot release an operation early. Local browsing remains usable when management history needs review.

## Verification

The Release solution compiles with zero warnings/errors. The focused run passes **94 checks**, including **32 new local-management cases**, on generated NTFS data. Coverage includes file/folder rename and moves; nested/empty/Unicode content; creation and destination collisions; changed source, parent and destination identities; protected shortcut roots; descendant moves; changed/reused/cancelled reviews; exclusive ownership and restart; deletion/restore after shortcut removal; restore collisions; typed permanent deletion and changed saved trees; location confirmation; missing/corrupt directory references; preserved damaged journals; private-profile and hard-link refusal; controller refresh; and shutdown with a throwing observer.

The first focused run passed 92/93: a collision fixture created a directory after capturing its source-folder version and was correctly refused as stale. The fixture now captures after setup. The checks were not relaxed.

All **597 selected Windows cases pass** in 4 minutes 3 seconds, including the real attachment-worker checks. The same three pre-existing capacity exclusions remain; no new exclusion is introduced. The final six production/test DLL hashes match before and after regression. Release build, focused/native results and hash evidence are retained in `.tools/phase62-final-build.log`, `.tools/phase62-focused-final.log`, `.tools/phase62-final-regression.log`, `.tools/phase62-results` and `.tools/phase62-final-hashes-{before,after}.txt` locally; generated verification files are ignored by Git.

## Remaining evidence

Actual Windows picker, visual, keyboard and screen-reader checks remain unverified: GUI automation is still stopped after Escape. Step 36 of the [physical test procedure](physical-network-validation.md) is prepared for the user to run later. No physical phone or separate-network execution is claimed.

Cross-volume/device moves, Android provider-safe management, alternate-destination recovery, absent-object acknowledgement, local-management export/search and directory-reference pruning remain open. So do whole-store orphan/vault cleanup, shared quotas, full-capacity performance, coordinated profile rollback, hostile private-profile interference and complete filesystem/volume/concurrency/process/power-loss matrices. Reusing the partial-recovery engine does not establish fresh local UI or every crash-point coverage. Signed packaging and independent security/release gates remain in the [completion audit](completion.md).
