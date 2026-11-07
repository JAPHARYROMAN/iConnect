# Phase 15: Windows owner identity and recovery

Windows trust and storage approvals now include the local public identity and a random owner generation. This closes the case where a missing/replaced CNG key creates a new identity while earlier metadata remains in the profile. A restored approval file from a different owner generation also fails closed.

## State and enforcement

`owner-binding.json` identifies the current owner/generation. Both `peers.json` and `storage-grants.json` use bounded envelopes carrying that same identity/generation and their records. Trust remains capped at 128 peers/1 MiB, grants at 512/4 MiB, and exclusions at 512 per grant. Existing grant permission, root identity, label, exclusion and expiry validation remains enforced; loaded grants receive additional structural validation.

A fresh profile commits both empty authority records and the binding behind `owner-reset.json`. A missing authority record, incomplete reset, corrupt record, different owner identity or mismatched generation blocks remote authority. Nonempty legacy metadata requires explicit local review; pairing alone cannot migrate its folder permissions. Empty legacy objects convey no authority and can initialize safely.

The ordinary profile lease still prevents two trust owners. In-process scopes retain their captured generation. Key changes invalidate earlier scopes; reset makes prior trust/grant objects stale. Authorized file callbacks check their scope before and after work, withholding a stale read result. Already accepted filesystem work is not a distributed transaction or automatic rollback. Live desktop recovery first disconnects and drains the session.

The desktop and console hosts bind their stores before remote use. The high-level connection client also attaches Windows file services to the same owner profile, reloading an initially unbound store through validated records. A preloaded legacy grant dictionary cannot be adopted after recovery. Raw store constructors remain available for isolated storage tests/local setup; production hosts use bound stores, and local-browser authority cannot attach to a remote session.

## Owner workflow

If the Windows key is lost or authority metadata needs review, the app stays visible, blocks pairing/remote access and shows **Windows trust needs owner review**. **Settings → Windows identity and approvals → Reset Windows trust and approvals…** explains the effects before confirmation.

Recovery pauses access, disables optional background/reconnect/sign-in preferences, and preserves previous authority metadata families under the private profile's `recovery/<random-id>/` shelf. It commits empty records under a fresh generation before clearing the durable recovery barrier. An interrupted or failed reset remains blocked and can be retried. Unexpected directories/links or changed metadata are refused by the existing archive safeguards. The normal metadata-backup removal control remains a separate owner decision.

Pair devices and approve their folders again afterward. Existing received copies, local folders/shortcuts, staged bytes and local history are kept. Old sending tickets and mutation receipts do not regain authorization through their former grant IDs. They can remain in local history/queues and may require normal removal or preparation of a new transfer.

The console host reports blocked authority before connecting. Its explicit `--reset-owner-policy` option performs the same metadata-preserving reset for the chosen proof profile. This is a local recovery operation, not a network command.

## Verification

The full .NET suite passes 122 checks and the Release solution builds without warnings/errors. Eight new tests cover current-identity persistence, actual CNG key deletion/replacement, stale live stores, restored grants, legacy/missing authority, failed reset/retry, attachment of preloaded grants and an identity change during an authorized read. File bytes remain unchanged across recovery. Existing desktop integration fixtures now seed bound profiles; fresh pairing, browsing, transfers and offline revocation continue to pass.

A disposable native key-loss fixture displays the blocked state, hidden old approvals, review disclosure and cancellation. Its saved authority remains unchanged after cancellation. Independent local shortcuts still open and discover their real files. Reset is verified through API/NTFS tests; native review/cancel and local browsing are the automated UI evidence.

Actual Windows–Android interoperability passes with the new Windows envelopes: mutual pairing, scoped explorers, real file/folder copies in both directions, durable file changes and the matching-session failure boundary. Android source code did not change in this phase.

The operator-run [physical separate-network test](physical-network-validation.md) is prepared for the user to run after implementation. Its fixture tool creates generated files and a private expected manifest and verifies returned bytes/structure. A local smoke run passes and deliberately changed fixture content is rejected. Physical-phone/separate-network results remain unverified.

## Limits

These checks bind data to identity/generation and detect partial or cross-generation restores. A complete rollback of the matching binding and both authority records under the same still-valid CNG key has no external monotonic checkpoint; it is not detected. Restoring an old profile should be followed by explicit trust/approval reset. Hostile software controlling the owning OS account remains inside the endpoint trust boundary.

Old CNG keys are not exported or recovered by this workflow. Key loss requires fresh pairing. Existing unambiguous trailing-path CNG aliases remain supported; conflicting aliases require review. Full OS-account/backup/restore/upgrade testing, native confirmation/accessibility coverage, interrupted/reset-capacity matrices, independent security review and signed distribution remain in the [completion audit](completion.md).
