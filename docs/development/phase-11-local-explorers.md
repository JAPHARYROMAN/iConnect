# Independent local file explorers

Implemented and locally verified 3 November 2026 (Africa/Nairobi). **On this computer** in Windows and **On this phone · local folders** in Android open owner-selected local folders without pairing or a coordinator. Local browsing complements the [remote explorers](phase-10-explorers.md). The [full application completion audit](completion.md) remains active.

## Product behavior

Owners can save up to 32 local folder shortcuts, open and refresh them, navigate breadcrumbs, search filenames within the open folder, sort by name/size/modified time/type, switch between list and grid, inspect metadata, and preview up to 32 KiB of plain UTF-8 text. Display pages contain 50 items. Search and sorting use the existing bounded catalog implementation and incomplete-result notices; the same discovery/depth/metadata limits apply. Text is never interpreted as HTML or executed.

Removing a shortcut removes its local record. Files and device sharing approvals are kept. Windows preserves a damaged shortcut record under a recovery name when the owner resets local shortcuts. Android exposes local shortcut reset and reconciles only its own unused OS-access leases. Existing download and sharing leases continue to retain their required access. Missing or replaced folders stay visible as unavailable shortcuts so owners can remove them or choose the current folder again.

Android clears file rows, breadcrumbs, details and previews and cancels provider browsing when the activity stops. Local browsing cannot run as an authorized background transfer. A picker result delivered before activity start waits for foreground activation. Accepted shortcut writes finish independently of canceled discovery; shutdown waits for the active metadata write. Local queries and inspections have bounded active jobs and cancellation/deadline handling.

These explorers currently inspect files. Local copy, move, rename, delete and remote-destination upload controls are not implemented here. The system-picker journey, broader filesystem/provider compatibility and complete accessibility/device coverage remain release work.

## Authority and persistence

Local shortcuts live in a separate, bounded `local-locations.json` record; there is no peer identifier, write permission or remote sharing approval in that saved record. Windows validates local display paths and canonical volume-GUID paths on reload, rejects network/unavailable drives on selection, binds the selected root's filesystem identity and refuses a substituted root until explicit reselection. Stored network paths are rejected before a file operation. Existing handle-relative traversal, no-link and identity checks still apply.

Android obtains read access through the system folder picker and retains it under a `browser:` OS-access lease. Selecting a local folder does not take an additional persistent write grant. Local records survive process restart without creating missing OS access. A withdrawn URI grant must be restored through the picker. Local records are covered by the app's existing backup/device-transfer exclusions.

Both file engines now have a policy interface: incoming sessions retain their original peer-bound sharing policy; private local hosts use independent read-only authorities. Local IDs are issued by a separate host and cannot be resolved by a remote host. Local inspection may show owner-accessible names such as `.ssh`, while the remote policy retains its credential exclusions. This does not weaken remote containment, policy epochs, readiness or foreground-availability requirements. No new wire operation, transfer ticket or upload endpoint was introduced.

## Evidence and limits

- Release solution build: zero warnings/errors. Full .NET suite: 88 checks; four new local tests cover offline catalogs/search/preview, private-name and reference isolation, restart/root replacement, failed commits, damaged-record recovery and rejection of a stored network path.
- Android application/test APKs and four JVM checks build/pass. Device instrumentation passes 62 policy, storage, transfer, lifecycle and explorer checks, including five new local-browser tests. A separate native Compose accessibility-surface test passes opening the local screen/folder, setting a filename query, showing live details, previewing actual generated provider content and removing its shortcut while disconnected.
- Native Windows visual check uses only a disposable local fixture and no coordinator: a 112-item catalog across three pages, nested Unicode search, list/grid and actual text preview. The native folder-picker selection itself was not driven in this check.
- Actual authenticated Windows–Android interoperability still passes: scoped remote explorer flows, verified receiving/SAF publication, phone-to-Windows folder receiving with empty/nested directories, and acknowledged transfers in both directions. An earlier fixture attempt timed out waiting for the harness's Windows pairing approval; the complete rerun passes.

The Windows restart test caught a path-format validation error during implementation: the filesystem reports a volume-GUID canonical path. The corrected validation accepts that local form and rejects UNC/network forms; root identity is still checked on every operation. Provider read permissions, ancestry reporting, seekability and metadata quality remain compatibility constraints. These local checks do not establish real-phone, independent-network or production-security readiness.
