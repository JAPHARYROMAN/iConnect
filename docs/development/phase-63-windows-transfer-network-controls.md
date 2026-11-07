# Windows transfer network controls

Windows Settings now offers a saved remote-content bandwidth limit and an optional pause on metered or unknown networks. Initially the limit is unlimited and metered-network pausing is off. The owner reviews and confirms changes. These controls apply to Windows network use; they cannot determine the other device's mobile-data allowance.

## Payload boundary

One `FileTrafficPolicy` belongs to the desktop owner and is shared across its authenticated peer sessions. It has one aggregate payload lane for sending and one for receiving. Limits can be unlimited or between 64 KiB/s and 64 MiB/s. The UI offers fixed presets and accurately displays a valid saved custom value. File copies, batches and remote previews share the same lanes. The synthetic diagnostic transfer also checks this policy.

The file-RPC boundary gates content preparation, file ranges, incoming file writes, batch admission/manifests and batch content creation/writes before invoking the file service. Outgoing requests are cloned before waiting. RPC admission happens before pacing, so occupied credits cannot accumulate prepaid payloads and release them as a burst. The wire-response timeout starts after local admission and pacing. Cancellation, connection closure and shutdown cancel pending work. Cost and health are checked again before transmission and during pacing; reducing the limit is observed while a range waits.

For receiving, read requests are paced before requesting bytes. Incoming writes are paced before filesystem processing and acknowledgement. Existing bounded queues, chunk validation and native backpressure still apply. Status, receipt, cancellation, release and finalization requests remain available when content is paused. Finished incoming data can therefore be reconciled or cleaned up without enabling another content range. Grant/trust checks are unchanged.

This is application payload pacing, **not an OS traffic or billing cap**. It does not count signaling, metadata, encryption/transport overhead, retries or unsolicited traffic from a hostile peer. A remote write has already arrived before its processing/acknowledgement can be delayed. Buffered or in-flight data cannot be recalled. Request pacing cannot guarantee the exact timing of bytes released by the OS/network. Configuring both endpoints can reduce throughput further. These limits make no full-load fairness or performance guarantee.

## Metered-network observation

The native Windows monitor queries all connected profiles, rather than inferring cost from Wi-Fi or cellular interface labels. Any fixed/variable cost, roaming, exceeded/approaching data limit or background-data restriction produces a conservative metered classification. Otherwise an unknown profile produces unknown; only all-unrestricted connected profiles produce unrestricted. No suitable connected profile or an API failure also produces unknown. This is a conservative connected-profile observation, not proof of the WebRTC route. Microsoft documents the cost indicators in [ConnectionCost](https://learn.microsoft.com/en-us/uwp/api/windows.networking.connectivity.connectioncost?view=winrt-26100).

Network-change events and a two-second timer refresh the observation. Readings older than five seconds become unknown using monotonic time. Cost reads do not hold the lifetime lock; a slow query cannot block shutdown or publish after disposal. Concurrent refreshes do not overlap. Native API/event failures remain conservative. The monitor follows the Windows [network-status observation model](https://learn.microsoft.com/en-us/uwp/api/windows.networking.connectivity.networkinformation.getinternetconnectionprofile?view=winrt-26100), while considering all connected profiles.

With metered pausing enabled, both metered and unknown classifications refuse subsequent remote content requests. Existing file/batch error handling retains interrupted work for explicit owner resume. Returning to an unrestricted network does not itself restart a stopped job. Local browsing, offline copies and local management do not use this RPC policy and remain independent. Changing preferences does not revoke or create a sharing approval.

The current classification is displayed when Settings renders. Actual Windows cost-setting changes, multi-interface route behavior and the final UI remain field-validation work. Android's own saved controls were added subsequently in [phase 64](phase-64-android-transfer-network-controls.md); Windows cannot determine its cellular peer's charges.

## Persistence and recovery

`WindowsTransferNetworkSettings` keeps a schema-1 record under `transfer-network`, with bounded JSON, an exclusive native lease, retained profile-ancestor handles, flushed staging and atomic commit. The record contains only the limit and pause preference, without network names/IDs, peer IDs, addresses or file paths. Invalid owner choices leave the saved controls unchanged. Changed records are refused instead of being overwritten by a later settings save.

Fresh profiles persist defaults. A missing record on a previously opened store does not silently remove metered protection: it blocks remote content until reviewed. Missing-state reset requires the records to remain absent; a reappearing record is kept for a fresh review. Unreadable, invalid, oversized or uncommitted records also block content. The owner can preserve damaged metadata and reset the controls to the explicitly reviewed defaults, or remove only preserved metadata backups. Files, transfer receipts and approvals remain separate.

If this settings store cannot be opened, the desktop uses a blocked content policy and still opens the local explorer. Storage access must be restored and the app restarted. This does not establish whole-profile rollback detection or protection against every hostile process with the same OS privileges.

## Verification

The Release solution builds with zero warnings/errors. **52 focused main-suite checks pass**, including 50 new cases and the existing native RPC/synthetic transfer checks. New coverage includes operation-level content denial before service execution; continued status/cleanup; invalid bounds and payload-bearing metadata; unknown/stale costs; unlimited and aggregate timed pacing; changing limits/cost during a wait; cancellation/disposal; immutable requests; occupied-credit burst prevention; saved/missing/damaged settings and reviewed recovery; exclusive ownership and changed records; unavailable-storage fallback; controller restart; and an actual offline file copy while remote content is blocked.

The native authenticated channel case receives a generated 128 KiB-plus file at the configured limit and verifies its bytes through the existing real Windows receiving/attachment path. It refuses metered reads and upload preparation without creating an incoming record, accepts an initial upload chunk when permitted, refuses its next chunk after a cost change, preserves the existing offset and allows status/cancellation while content remains blocked. This is generated NTFS data over Windows native encrypted loopback channels, not fresh Windows–Android or physical-network interoperability.

**Two additional Windows SDK monitor cases pass** in the existing native test project, linking the production observer source without opening a window. The actual OS profile/cost calls succeeded, and the monitor reported `Unrestricted` on this machine. The other case proves that a slow query does not block disposal or publish afterward. This does not prove an actual metered/roaming/data-limit transition.

All **647 selected Windows main-suite cases pass** in 3 minutes 31 seconds. The final eight production/test DLL hashes match before and after the regression run, including the changed Core assembly and the separate Windows SDK test assembly. Local build, focused, monitor and full-regression evidence is retained in `.tools/phase63-final-build.log`, `.tools/phase63-focused-final.log`, `.tools/phase63-native-monitor.log`, `.tools/phase63-final-regression.log`, `.tools/phase63-results` and `.tools/phase63-final-hashes-{before,after}.txt`. Generated verification files are ignored by Git. The same three existing capacity exclusions remain; no new exclusion is introduced.

## Remaining work

Step 37 of the [physical test procedure](physical-network-validation.md) is prepared for the user to run later. No physical metered-network test or desktop GUI automation was performed. Windows GUI automation remains stopped after Escape. The owned Android AVD remains off; Android source/binaries were unchanged this phase.

Existing source approvals and operation lifetimes are unchanged. Ordinary Windows download-source tickets expire after 24 hours; a sufficiently slow or large transfer can outlast its approval. The limiter does not silently extend that authority. Fresh-source recovery, useful lifetime-aware transfer estimates and long-duration verification remain work to complete.

Android cost/rate controls, actual OS cost transitions, route/interface/provider/device compatibility, long-running accuracy, full-capacity fairness, crash/upgrade/power matrices and independent security/release evidence remain open. This feature does not enable the still-gated batch capability or complete the full application; see the [completion audit](completion.md).
