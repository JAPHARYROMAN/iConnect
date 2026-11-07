# Private batch transfer progress

The outgoing batch queue on Windows and Android shows the current file's name, item position, acknowledged bytes and recent payload estimate. Folder operations and manifest preparation use the batch's existing status. After the last byte is acknowledged, the file status explains that the receiver can still be verifying or saving the copy. The batch completes when the receiver confirms the final item outcomes.

## What the estimate measures

- A validated receipt for the current file supplies the starting offset. Only a range reply with the expected offset and receiving state contributes new bytes. Time spent preparing the private snapshot, checking the source and staging the manifest is outside the estimate.
- Each file and each resumed send has a fresh measurement. A resumed file starts at the receiver's accepted offset. A range whose reply is lost can appear in that new checkpoint after reconciliation. It contributes no new bytes to the resumed rate.
- The clients use the recent progress window and discard rates after ten seconds without progress. A configured payload limit bounds the estimate while the displayed rate describes the observed acknowledgements. Changing that limit clears earlier samples.
- Pausing or losing the session removes live estimates. The queue can still show the saved item checkpoint. The rates are held in memory and a restart starts a new measurement after an explicit resume.

## Batch forecast

When later files add payload, the queue also shows the remaining planned bytes and a batch forecast. The clients calculate that byte budget from the frozen manifest and retained outcomes. They exclude files that already have an outcome, manifest errors and descendants of skipped or failed folders. The current file's validated receiver offset reduces the budget. Pending collisions can reduce it further.

The batch rate uses new acknowledgements across file boundaries within the same send attempt. Its elapsed time can include gaps between files. The estimate assumes that recent pace and the configured payload limit. There is no countdown before enough progress has been observed, and an inactive or stale rate is cleared. A resumed attempt starts its measurement again while using the receiver's reconciled checkpoint for the byte budget.

The forecast covers the currently planned payload. Receiver checks, attachment scanning and provider publication have their own status and can extend completion. Use [step 62 of the physical network test](physical-network-validation.md) to verify the status on paired release clients.

## Receiving a batch

On the receiving device, **Received batch history** shows the current file's item position, name, saved bytes and recent payload estimate. The estimate uses new ranges after their checkpoint is durably saved. Repeated ranges leave the measurement unchanged. A stall clears the countdown after ten seconds. Closing the session clears its samples, and a resumed file starts measuring from newly saved ranges.

Each file has its own rate and remaining payload estimate. The receiving app also shows when the payload has arrived and when it is verifying or saving the file. The sending app owns the remaining batch plan and its forecast. Use [step 64 of the physical network test](physical-network-validation.md) to verify both receiving directions.

Ordinary and batch incoming copies use the same [local verification and saving steps](incoming-payload-estimates.md#after-the-payload-arrives).

## Windows byte updates during sending

The Transfers page updates existing labels for private snapshot preparation and intermediate acknowledgements. The single-file label shows bytes prepared or acknowledged. Batch preparation shows the count and total size of prepared files, and a batch send shows the current file's acknowledged bytes. A receipt baseline, the last range of a file, a change of item, or a change in available actions refreshes the card. Resuming still checks the original receiver receipt before sending more bytes.

The Windows fixtures check that intermediate counts reach observers without requesting a page refresh, and that preparation completion, the original receipt baseline, pause and send completion still request one. Use [step 70 of the physical network test](physical-network-validation.md) to check the focused control, screen-reader behavior and final bytes on the paired devices.

## Per-file verification

The Windows Release build passed with zero warnings or errors. All 137 focused tests and 851 selected Windows tests passed. The selected run excluded three 4,096-item capacity cases. Android debug, test and unsigned release builds passed along with 84 JVM and 41 selected emulator tests. Lint reported zero errors and 71 warnings. The initial emulator run had two UI failures while unrelated Android system dialogs covered the app. Both passed on the same APKs after reboot, along with the rest of the selection.

The Windows NTFS and Android provider fixtures withhold a range reply, verify that it is absent from the displayed progress, pause and then use the receiver's retained checkpoint. Both confirm that a resumed estimate starts fresh and that the receiver eventually saves the expected bytes. Installed separate-network behavior, Windows visual verification and screen-reader behavior are field checks.

## Batch forecast verification

The Windows and Android fixtures send separate files around a skipped folder. Both confirm that a rate can carry across files, the skipped folder's bytes leave the forecast, and reopening after an interrupted save preserves that exclusion and starts a new measurement. The fixtures also check the final receiver output.

The Windows Release build passed with zero warnings or errors. All 21 focused and 852 selected Windows tests passed, with the same three capacity exclusions. The Android debug, test and unsigned release builds passed, along with 84 JVM tests and 42 emulator tests. Android lint reported zero errors and 71 warnings. The installed APKs matched the built packages before and after testing. Step 62 remains the field check for paired release clients.

## Receiver progress verification

The Windows NTFS and Android provider fixtures verify the saved offset and a fresh measurement after reconnecting. Replaying an accepted range leaves the measurement unchanged. Both fixtures verify the final saved bytes. The Android run also found a Stop timeout while the owner lock was held. The fix signals the already-bound provider worker before waiting for that lock. Durable Stop and cleanup still require owner authorization.

The Windows Release build passed with zero warnings or errors. All 854 selected Windows tests passed. Android debug, test and unsigned release builds passed, along with 84 JVM tests and all 107 selected emulator tests. Lint reported zero errors and 71 warnings. Three 4,096-item or part capacity cases per platform were outside these runs. The built binaries and installed APKs matched their recorded hashes. The field helper verified the two-payload fixture and rejected an altered payload. Separate-network and accessibility checks remain in step 64.
