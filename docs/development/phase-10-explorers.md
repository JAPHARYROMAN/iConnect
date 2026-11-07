# Remote explorer catalogs and scoped discovery

Implemented and locally verified 3 November 2026 (Africa/Nairobi). Windows and Android now collect bounded directory catalogs before presenting sorted result pages, offer filename search within one approved folder and its descendants, and retain selections across result pages, sorting and searches in that scope. This extends the receiving workflows described in [Windows batches](phase-8-windows-batches.md) and [Android batches](phase-9-android-batches.md); it does not complete the [full application](completion.md).

## Owner-facing behavior

Both remote explorers have name, size, modified-time and file-type sorting, ascending/descending order, list/grid layouts, breadcrumbs, live item details and display pages of 50 items. Folders remain first in either sort direction. Sorting applies to the loaded catalog rather than only the current display page. Counts and incomplete-discovery notices describe what was loaded.

Filename search uses a literal, case-insensitive, Unicode-normalized query of 1–128 visible characters. It searches filenames in the open approved folder and its accessible descendants. Results include relative paths. Opening a matched folder preserves its route from the approved root. Search requests metadata and directory listings; it does not read file content, prepare downloads, or send queries to the coordinator. There is no persistent search index.

Up to 128 selected entries survive page, sort, layout and search changes. Navigation to another folder, refresh, disconnect, and Android activity stop clear selection. A copy plan uses the stored route of each selection, including items on other pages or hidden by the current search. Selecting both a folder and its descendant includes the descendant once through the folder. Planning and execution still validate grants, current source metadata and the pinned peer/session; retained selection never grants additional access.

Discovery can be canceled. A canceled or failed query leaves earlier results visible with an error. A capped or timed-out query can publish partial results with an explicit incomplete notice. A zero-result partial query is not described as an empty folder. Refreshing may change session-local references and deliberately clears selection.

## Bounds and trust boundary

Each discovery has a 20-second deadline, 8,192 visited items, 2,048 requests, 256 transport pages per directory and a 16 MiB metadata budget, including stored route metadata. Search returns at most 2,048 matches and traverses at most 32 levels below an approved root. These are defensive limits, not full-load performance claims. Large or slow providers may return incomplete results; users can narrow their scope or query.

Routes must start at one approved root and retain the same grant and exact parent chain. Directory metadata is checked before and after enumeration. Invalid parents, duplicate references/names, unrelated grants, changed folder metadata and cyclic routes are rejected. Denied, missing, stale or unsupported search branches produce an incomplete notice. Display paths remain descriptive metadata; no remote raw path becomes filesystem authority.

The additive `CLOSE_LISTING` operation closes only the requesting peer's exact node/cursor pair and returns no file data. Query cancellation, errors and caps attempt cleanup with a separate one-second deadline. Unsupported cleanup on older peers is ignored; session teardown still disposes outstanding source resources. It is optional cleanup within the existing protocol version, not a new transfer capability. It does not enable background phone browsing.

Both manifest planners previously rejected repeated continuation strings, although the real source implementations legitimately retain one stateful cursor token over successive pages. They now accept that behavior. Duplicate entry checks and page limits still reject repeated content or bound a non-progressing source. Tests enumerate more than two actual transport pages on Windows and an Android document provider.

## Windows state-file reliability

A combined Debug test run reproduced the earlier transfer interruption with `Access to the path is denied`. A deterministic test reproduces that Windows rename error while a reader holds a private progress file without delete sharing. Private JSON commits now retry only the already-flushed staged-file rename on Windows errors 5, 32 and 33, with delays of 10, 25, 50, 100 and 200 milliseconds. They do not retry file-content publication or change filesystem permissions.

Tests demonstrate recovery after a temporary reader closes and a visible failure with both old and staged state preserved under a persistent lock. Existing failed-write authority tests remain in place. The intermittent run's actual external trigger was not captured, so it is still an open reliability investigation; these tests establish a concrete failure mode and bounded recovery, not a proven root cause or a power-loss guarantee.

## Verification and remaining work

- Release solution build and native desktop build: zero warnings/errors. Full .NET regression: 84 passing checks in Debug and Release, including seven explorer tests, real desktop-controller cross-page selection/planning, and two private-state lock tests.
- Android debug application/test APKs build; JVM checks and lint pass. Emulator instrumentation: 57 passing checks, including five explorer/provider tests covering multipage sorting, scoped search, cursor isolation/cancellation, bounded results, forged parent metadata and multipage manifests.
- Actual authenticated Windows–Android channel: Android discovers 111 items across three display pages, retains two cross-page selections through sorting/grid/search, reviews their two-item copy plan, opens a nested Unicode search result and fetches live details. Windows independently searches the Android provider. The interoperability test passes.
- Actual receiving remains verified in both directions: Windows receives the phone's six-item folder with empty/nested directories and a real file; Android verifies and saves a 2,097,169-byte Windows file through SAF. Synthetic acknowledged transfers still pass in both directions.
- Native Windows visual check against a disposable, pre-pinned fixture: a 112-item three-page catalog, list/grid switching, scoped Unicode search, visible relative paths, selection and the live metadata dialog. The toolbar was compacted after the first check pushed file rows below the initial viewport. This is not evidence for pairing UI or a complete accessibility audit.

Local explorers, favorites, complete management, richer previews, exhaustive keyboard/screen-reader/touch checks, physical-device/provider compatibility and full-load/crash/network coverage remain outstanding. Search is a bounded view of a changing source, not a filesystem-wide consistent snapshot. Packaging, independent-network evidence and security review remain required before release.
