# Android private receiving-part recovery

Android now replaces the batch receiver's unbounded orphan-discard action with a separate owner review and bounded cleanup. It targets only app-private batch parts without a current file journal. It never adopts those bytes as a new transfer and never deletes a provider document.

## Review and ownership checks

The receiver requires a healthy local identity, foreground availability and idle batch file verification. Before review, confirmation or continuation, it checks that the scoped journal inventory still matches the loaded file journals and verifies each journal's native version. Missing, changed or newly appearing journals block cleanup and require explicit receiver-history recovery. Unreadable metadata is preserved and reset first; its former parts can then be reviewed as unreferenced files.

A single-use review captures every candidate's name, logical byte length and native version, the current owner generation, and both metadata and private-part directory identities. Confirmation expires after two minutes on a monotonic clock and rechecks the complete candidate list. A new file appearing before confirmation requires another review. Known journal references protect their parts independently of peer connectivity or revocation.

The private-part store retains its existing exclusive lease and now also checks that the lease file is a regular, single-link object whose opened descriptor matches its path. Redirects, links, unexpected folders and unsupported part names are kept rather than followed by cleanup.

## Bounded removal and native controls

Confirmation and continuation remove at most 64 reviewed files per call. Each deletion rechecks the current local owner, directory bindings, whether a current journal now references the part, and the file's native version through a no-follow descriptor and its path. Newly appearing parts are never added to an existing authorization. A changed or newly referenced part remains for fresh review. The candidate files remain in place when a review is canceled. Cleanup invalidates the part-count cache on every exit, so newly appearing files remain visible and count toward the staging limit after the app's own deletions update directory timestamps.

The Transfers screen provides a paged inventory of 50 names and byte sizes, a separate removal confirmation with file/byte counts, and visible continuation progress. The dialog explains that archived transfers cannot resume from deleted private parts and that provider copies remain intact. The native loop yields between bounded calls and stops when the screen is disposed. Foreground withdrawal can stop a call between files and retains its exact current-process cursor. After restart, owner-generation changes or receiver-context reset, remaining parts require a fresh review. No cleanup resumes automatically.

Current receiving parts, source snapshots, sender metadata and every published, incomplete or uncertain provider document remain outside the removal scope. Peer revocation does not prevent current-owner cleanup of unreferenced private parts.

## Verification and remaining work

The first focused run passed **12 of 13 cases in 26.141 seconds**, including the actual native panel journey. The remaining case verified that a newly added part was kept but found its orphan count incorrectly cached as zero after the reviewed files were deleted. Invalidating the cache after each cleanup call corrects that behavior. Evidence: `.tools/phase47-android-parts.log`.

The final debug, instrumentation and unsigned release builds, **39 JVM tests** and debug lint pass. Lint reports zero errors and 67 existing warnings. The corrected focused device run passed all **13 cases in 15.572 seconds** on the owned headless emulator. Evidence: `.tools/phase47-android-verified-build.log` and `.tools/phase47-android-verified-parts.log`.

Those cases use actual incoming private bytes and provider storage. They verify current-journal protection, preservation before confirmation, forged/stale/changed-owner reviews, bounded removal, new and changed parts, restart requiring fresh review, peer revocation, owner reset, receiver-context reset, foreground interruption, changed journal inventory, links and nested directories, and a part becoming referenced during cleanup. The actual Compose accessibility journey inspects the inventory, cancels removal, confirms it, and verifies originals, unrelated owner files, the lease file and provider bytes remain intact with no sharing or authentication enabled.

The final broad device regression passed **300 cases in 472.773 seconds**, including all 13 new recovery cases and the existing native screen journeys. It uses the same four coordinator-dependent interoperability-class exclusions and four separate full-capacity exclusions as phase 46. Evidence: `.tools/phase47-android-regression.log`.

The final application APK SHA-256 is `2b30fe669ce051bd91a80ab959d25475b020ddbe7be017630559231fcfb9c50a`; the final test APK SHA-256 is `20099d7c9a0d0f84dbafee3ecc5f87374ea207f44c089c643261606a40979a65`. Installed hashes matched the built files before and after the final device runs. The owned emulator was stopped afterward, and the adb device list was empty. These are development APKs; the release APK is unsigned.

The implementation uses app-private filesystem identities, no-follow opens and cooperative leases. It does not establish resistance to a hostile process sharing the app UID or actual process/power-loss durability. Full-capacity review and cleanup performance, provider and lifecycle matrices, independent review and release gates remain open. Journal inventory checks are bounded but their latency at maximum retained history is unproven.

Windows receiving parts are located alongside approved destinations, rather than in a dedicated app-private part directory. Owner recovery of unidentified Windows destination-side partials requires its corresponding scoped inspection and management flow. Windows code was unchanged by this Android phase; its last verification remains the [phase-46](phase-46-android-private-source-recovery.md) 439-case regression and Release build. Windows UI automation remains stopped following the user's physical Escape. The [completion audit](completion.md) keeps the full application objective active, and the [physical-network kit](physical-network-validation.md) remains prepared for the user to run after implementation.
