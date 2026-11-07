# Windows send and local-copy payload estimates

Windows ordinary uploads and offline local file copies now show recent payload rates and approximate remaining-payload time. Local folder batches show the current file's estimate alongside that file's result. Preparation, source checking, receiver verification, attachment checks and saving retain their own status; payload completion does not imply that the final copy is ready.

## Measurement and authority

Ordinary upload observations start only after the private source is checked and a validated receiver receipt establishes the starting offset. Only an acknowledged range with the exact expected offset becomes progress. A lost reply is not counted. Resume uses a new memory-only run identity and treats the receiver's retained offset as its baseline, excluding earlier bytes and time spent stopped or hashing. Observations end before receiver completion checks and on every exit, including cancellation and invalid replies. A failing display observer cannot interrupt the transfer or change its authority.

The existing bounded tracker keeps up to 128 rows and 96 samples per row. It requires two seconds of progress, uses a recent 20-second window, drops a rate after ten seconds without progress and resets on rollback, changed scope or pause. Ordinary upload estimates include the configured peer payload limit without relabeling the measured acknowledgement rate. The run, connection generation and limit scope prevent reuse across sending attempts or settings changes. Rate samples and run identities are never written to a receipt, history, diagnostic export or coordinator message.

Local copies observe the existing receiver's validated progress, using independent samples for standalone files and batch children. Their estimates have no network limit and create no pairing, sharing grant or remote session. The source ticket/version distinguishes the active source. Verification, finalization and cancellation clear live samples; a restart begins without live measurements. Batch completion retains its existing compact parent results and removes finished child receipts. Looking up a current child's progress does not recreate a receipt or source authority.

The existing coalesced one-second controller refresh also updates send and local-copy estimates, allowing stalled text to expire without issuing file requests. No protocol, collision, source lifetime, publication, checkpoint, attachment or background permission rule changes.

## Verification

The Release solution builds in **8.74s** with **zero warnings and errors**. All **100 focused tests** pass in **8s**, including **10 new cases**. These cover acknowledgement-only measurement, cap bounding, stall behavior, fresh resume baselines, lost and invalid replies, observer failure, independent local/batch samples, source and clock changes, actual native copies and attachment-stage separation. The actual offline controller copies generated data while remote network policy refuses content, preserves the original, creates no sharing approval and reopens without a live rate.

The initial focused run passed 99 of 100 cases. A new assertion expected a completed batch child receipt to remain; the batch correctly retires it after saving the parent outcome. The corrected case verifies current-child lookup during copying, native progress, completed observations, parent results and absence of retired child receipts.

All **784 selected Windows cases** pass in **2m 46s**, with the same three capacity exclusions and zero failures/errors. All eight recorded Windows production/test DLL hashes match before/after. Windows GUI automation remains stopped after Escape. Android is unchanged from phase 73, and the owned AVD remains off. The standalone coordinator is not launched. Physical step 48 is prepared for the user after implementation, not executed.

## Remaining work

Android ordinary upload/local-copy estimates are covered by [phase 75](phase-75-android-send-local-payload-estimates.md). Private-batch sending and owner-visible incoming-upload estimates on both platforms, serving-read estimates where a complete receiver offset is unavailable, preparation/verification/security/provider-stage forecasts and aggregate batch-completion estimates remain open. This phase supplies per-file payload forecasts rather than whole-operation or billing guarantees. Native visual/keyboard/screen-reader, provider/volume, full-capacity/performance, lifecycle/power/crash/clock/upgrade and physical-network matrices remain in the [completion audit](completion.md), alongside release packaging, hardened operations and independent security review.
