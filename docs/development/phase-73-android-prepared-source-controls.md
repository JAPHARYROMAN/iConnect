# Android prepared-source inventory and cleanup

Android now separates its existing selected-peer/session sending view from an owner inventory of retained source approvals for every computer. **Prepared reads from this phone** shows paged filenames, receiving-device IDs, sizes, preparation state, pause state and expiry while disconnected. The foreground owner can stop one prepared read or remove expired approvals. Original provider files, receiver checkpoints and completed copies remain untouched. Folder grants remain separate and can permit a fresh request.

## Runtime and background boundaries

The existing `PhoneSend` view retains its selected-peer/session behavior and its separate background-transfer controls. The new inventory contains display metadata without document IDs, grant IDs, provider URIs, hashes or session scopes. It cannot resume a source or authorize background activity. Stopping through the controller immediately blocks the ticket, withdraws its background allowance and interrupts its current session request before saving the removal. The owner operation waits for preparation to drain. Other approvals, including independent reads of the same file, remain usable.

Current owner policy, source approval expiry, pairing-bound grants and Android OS access continue to guard every new read. Remote release remains peer-bound. Source reads already recheck the retained ticket after provider I/O; removing one cannot publish an old range afterward. Hiding/closing prevents a new foreground owner action. Closed stores cannot prepare or serve a retained ticket.

## Persistence and cleanup

The limits remain eight approvals per peer, 128 overall, a 24-hour source deadline and two hashing workers. Expired rows remain inspectable in the new inventory but are excluded from the live sending view and cannot confer background authority. Explicit expiry cleanup, reopen and new admission persist removal of expired operational metadata. Source files and provider documents are never removed by these actions.

Failed writes block ticketed reads and preserve the last saved metadata and original files. Uncommitted `.new`/`.bak` state requires owner review even when a primary journal exists. A failed stop is reported rather than acknowledged as durable. Background approvals and folder grants are not recreated by cleanup, and a paused source is not resumed by inspection or restart.

## Verification

The final debug, unsigned release and instrumentation build passes in **1m 52s**. All **75 JVM cases** pass with zero failures/errors. Lint has **zero errors and 69 existing warnings**. All **56 focused native cases** pass in **27.575s**, including **16 new cases** and 40 existing storage/source-recovery cases. A later combined **58-case focused run** also passes in **72.788s**, including both local-browser UI journeys. All **480 selected native cases** pass in **560.718s (9m 21s)** with the same eight exclusions, 480 passing status codes and zero failing status codes. Built/installed application and test APK hashes match before/after, as do all eight recorded Windows DLL hashes. The owned emulator helper has exited and its serial is absent.

New storage, native dialog/offline-controller and encrypted-peer checks cover all-peer inventory, independent tickets, slot reuse, worker draining, expiry cleanup/reopen/admission, grant separation, paused/closed behavior, failed writes and ambiguous records. The actual dialog checks device identity and keep/stop decisions without provider deletion. The disconnected controller sees both peers, keeps the selected-peer view scoped, rejects hidden owner actions and never gains background authority. The authenticated native peer loses its stopped ticket but can deliberately prepare a fresh read through the unchanged grant. The first focused run exposed an uncaught closed-store exception; it now returns a typed access denial. The controller test also corrected its assumption that the legacy selected-peer view is empty while disconnected and accounts for legitimate pause persistence on hide.

The first selected run completed 479 of 480 cases successfully; an existing image-preview UI case observed a folder where the requested image should appear. The identical-APK isolated case then passed. Its test now waits for the exact non-directory image row and enabled/settled controls before proceeding, while still requiring a red decoded image and working rotate/zoom/close behavior. The test-only build passes in **1m** with the production APK hash unchanged. This evidence does not establish a full UI stress or timing matrix.

Windows code remains at its phase-72 baseline; Windows GUI automation stays stopped after Escape, and no standalone coordinator is used. Physical-network checks remain prepared for the user rather than executed; injected source clocks do not establish natural-expiry field evidence.

## Remaining work

Complete real-device/provider, paired-controller, visual/accessibility, natural-expiry/clock, lifecycle/power/crash/upgrade and full-capacity/performance matrices remain open. Broader history cleanup, other-direction/stage/aggregate estimates, release packaging, hardened operations and independent security review remain in the [completion audit](completion.md).
