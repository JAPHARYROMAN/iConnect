# Android ordinary incoming-part recovery

Ordinary Android incoming copies now use the same owner-reviewed, bounded cleanup engine as batch receiving parts. The old unbounded orphan-discard method and dialog are removed. The native surface provides local inspection, an exact file/byte review, cancellation and explicit continuation without adopting a private part as new receiving state.

## Journal and directory binding

The ordinary receiver records the native version of its journal after loading or saving it. Before inspection, review, confirmation or continuation, cleanup requires a healthy local identity, an open idle receiver, the original private-directory identity, the current receiver epoch, an unchanged journal and no uncommitted journal sidecars. A changed journal blocks cleanup and produces a recoverable metadata issue; the owner preserves that metadata before resetting to an empty context. Parts referenced by current receipts stay protected even after peer revocation.

The shared engine captures exact native file versions and the current owner generation in a single-use review with a two-minute monotonic expiry. Confirmation rechecks the full candidate list. Each call deletes at most 64 reviewed files using no-follow descriptors, rechecks their versions and current references, and keeps new or changed files for another review. File contents are never interpreted as ownership evidence. The exclusive lease file is checked against its opened native descriptor before use.

Receiver-history reset and metadata recovery invalidate old deletion permission. Restart also requires fresh review of remaining files. Foreground withdrawal can interrupt a call between files while preserving its current-process cursor. The native loop yields between calls and ends when its screen is disposed. The separate cleanup-change flow updates orphan counts and progress even when the ordinary receipt list remains unchanged.

Current incoming parts, batch receiving parts, original sources, unrelated owner files and all provider documents remain outside ordinary orphan removal. No cleanup operation contacts a peer or creates sharing approval. Both native receiving surfaces use the same engine and presentation implementation with their own journals and candidate lists.

## Verification and remaining gates

The debug application, instrumentation and unsigned release builds pass, together with **39 JVM tests** and debug lint with zero errors and 67 existing warnings. Evidence: `.tools/phase48-android-build.log`.

The focused device run passes all **39 cases in 22.976 seconds**. It combines the ordinary incoming-upload suite with both private-part recovery suites and their actual Compose screen journeys. Evidence: `.tools/phase48-android-parts.log`.

The 13 new ordinary-part cases cover current-receipt protection, history reset, forged and changed-owner reviews, new or changed parts, 64-file continuation, 50-entry inventory pages, restart, peer revocation, foreground withdrawal, changed journals and uncommitted sidecars, links and native directory replacement. The native journey inspects actual names, cancels removal, then confirms it; it checks that provider originals, unrelated owner files and the lease remain intact without enabling sharing or authentication.

The broad device regression passes all **313 selected cases in 414.116 seconds**, including all new recovery cases and the existing receiving, batch and native screen suites. It uses the same four coordinator-dependent interoperability-class exclusions and four separate full-capacity exclusions as phase 47. Evidence: `.tools/phase48-android-regression.log`.

The application APK SHA-256 is `c68abf65ef59fe806e16ed876e8c4a7d4f302d03bfe961d30d518db7f5e28126`; the instrumentation APK SHA-256 is `f0a534df9155e9fe81e7a28c938c9672452da69519a5ca6a7a3c5a839eb9328b`. Installed hashes match the built files before and after the device runs. The owned headless emulator was stopped afterward. These are development APKs; the release APK is unsigned.

The operator-run physical-network guide now includes an ordinary incoming-part review/cancel/restart/removal check using only generated transfer history. Physical execution remains deferred until after implementation, as requested.

These checks use app-private native identities, no-follow opens and cooperative leases; they do not establish resistance to a hostile process sharing the application UID or actual process/power-loss durability. The ordinary store retains its bounded 128-part inventory; maximum-capacity responsiveness, supported provider/filesystem/lifecycle matrices, independent review, signing, packaging and release evidence remain in the [completion audit](completion.md).

Windows code was unchanged by this phase. Its last verification remains the [phase-46](phase-46-android-private-source-recovery.md) 439-case regression and Release build. Windows destination-side unidentified partial recovery remains open, and Windows UI automation remains stopped following the user's physical Escape. The [physical-network kit](physical-network-validation.md) remains prepared for the user to run after implementation.
