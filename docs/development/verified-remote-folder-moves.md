# Verified remote folder moves from Windows

A fully successful completed batch containing one folder can now become a reviewed move. In **Batch queue and saved history**, choose **Review moving an original folder…**, select the original with the native Windows picker, and inspect its path, receiving device, actual receiving name and file/folder/byte counts. Confirm **Move original into recovery** only for the intended original.

This selection is new local owner authority. A remote request never supplies the original path. The source must match the retained whole-batch plan; its complete tree is checked before source recovery. Unavailable, skipped or multiple-root plans do not qualify for this folder action. Existing standalone file moves remain separate.

## Transaction and preservation

The five-minute, single-use review binds the original parent and folder identities, file/directory identities and versions, complete membership, completed sender row and original source manifest. Cancellation releases its memory-only approval. Restart, changed ownership or pairing, changed private journals and altered source/destination evidence require a fresh review or refuse the action. At most two reviews and two active batch controls are admitted.

Confirmation opens the exact original under a native mutation handle, opens every child safely, hashes every file against the prepared manifest and checks folder membership. File handles deny concurrent writers during the fresh receiver check. The receiver must verify its original completed batch under the same authenticated session, pairing, header, history epoch and fresh challenge. An old receipt is insufficient. Listing access is not required for a blind Write/Create inbox.

The sender records a source-recovery transaction ID and original binding before asking the local recovery manager to move anything. Its journal uses schema 3; older schema 1/2 copy records remain readable and upgrade when written. Missing schema-3 move fields, invalid bindings or nondefault move state disguised as a legacy record block the store. A completed move cannot run again through the same history row. A surviving recovery record also prevents replay when the outgoing completion marker was not saved.

Windows refuses this directory rename while its child handles are open. The sender therefore closes child handles, keeps the root pinned, rechecks named identities and membership, and performs a no-replace rename into recovery on the source volume. The fresh receiver result must still be within ten seconds immediately before that effect. After the rename, the entire saved tree is reopened, pinned and hashed again before success. That final check may continue after the source has entered recovery; it does not extend permission to start a late rename.

A change between these checks, cancellation or a failure after the rename produces a recovery review outcome. The moved tree stays in recovery, including late owner additions or changed bytes. It is never silently deleted or automatically retried. Restoring it uses the existing owner recovery controls. Prepared snapshots and received copies remain intact, and the saved original has no automatic expiry. The completed sender message records that recovery happened; current location comes from local saved items.

These checks establish observed main-stream bytes, native identities and membership at the transaction's checkpoints. They do not certify metadata fidelity, every filesystem, hostile same-user interference, a lasting unchanged destination or permanent deletion of the original.

## Verification checkpoint

The first complete-tree test initially reproduces the unsupported held-directory boundary, then exposes the Windows rename refusal with open children. With separate pre-rename and saved-tree checks, the real NTFS test passes: nested Unicode text, empty directories and a zero-byte file survive source recovery and restoration; private and received copies remain unchanged.

All 38 selected storage, legacy-folder and native engine cases pass. They include twelve source/receiver refusal variants, review cancellation, restart and parent replacement, blocked schema downgrade or missing fields, stop cleanup, an empty root and refusal of file-only/multiple-root plans. Two durable-intent races retain late changed bytes and a new owner file in restorable recovery without claiming verified success. Two encrypted native listing/blind cases additionally move and restore originals on both Windows endpoints. All 956 selected Windows regression cases pass with zero failures/skips and the established three capacity exclusions and large-file selection unchanged. Desktop and standalone fixture Release builds have zero warnings/errors. The pinned source transport passes nine standard and eleven additional native/session/junction cases.

The first final regression passes 955 cases but fails its loaded-module check because the preceding validation runner restores a missing environment variable as an empty value. The runner now removes an originally absent variable. A real nine-case native invocation verifies that restoration, then the complete 956-case regression passes in the same process environment. The failed report remains preserved; no production assertion was relaxed.

The [Windows–Android driver](../../tools/test-native-folder-moves.ps1) retains the earlier batch transfer, lost-acknowledgement, cancellation, fresh-check and withdrawal assertions. A generated-data-only setting tells the standalone Windows fixture to move its original into recovery, verify its tree there and restore it before reporting success. The enclosing driver requires that extra evidence in every selected initiator/inbox case. Production applications do not consume this test setting. A live guarded follow-up waits for the earlier frozen validation generations; cross-platform execution is pending.

The 208-file host fixture has assembly SHA-256 `5b55d480c326fd48eea17f704df338c05378cac1452a208fced963cb3acb4776`. Its inventory, unchanged Android application/instrumentation and three driver hashes are frozen in `.tools/phase139-folder-move-artifacts.json` (SHA-256 `561463c47927e121760907fe25f36611c8efc2f9448f4028e928c4031be79292`). `.tools/phase139-device-state.json` records the queued execution. Local reports are `.tools/phase137-tests/phase137-focused.trx`, `.tools/phase138-tests/phase138-main-clean.trx` and `.tools/phase138-env-restoration.json`; the source-built report is `.tools/native-transport-verification/b2cb814a124b4ece9e75a9b787d851b0/phase137-native-additional.trx`.

Full 4,096-item move performance, complete interruption/storage/volume and metadata matrices, actual controller/invitation/coordinator journeys, native accessibility, physical separate networks, independent review and release approval remain open. Android-source folder moves and other provider-safe Android source management remain unimplemented. Full application completion remains false.

## Prepared operator journey

1. Use disposable generated originals: a nested folder, Unicode filename, empty directory and zero-byte file. Complete a folder batch to Android, once with a listing inbox and once with a blind inbox. Retain hashes and actual receipt names.
2. Select the completed batch, choose its original folder and cancel the review. Originals, receiver copies and history must remain unchanged. Repeat and confirm. Check that the original disappears from its old path, appears under local saved items and restores with all names, contents and empty directories intact.
3. After review, change an original file, replace an identical-byte original, replace its parent, add a member, change a received file or withdraw receiving approval. Confirmation must refuse or retain a recovery review; it must preserve all resulting owner data.
4. Stop during hashing or the receiver check, disconnect, and quit/reopen. No old review may authorize another move. If a recovery record exists, inspect and restore through local saved items. No automatic source removal may occur on reconnect.
5. Exercise the picker, confirmation, Stop control, status and recovery navigation with keyboard and screen reader. Record exact binaries and outcomes. Windows GUI automation remains stopped following the user's earlier Escape; this journey is prepared for execution.

The user's separate-network physical Android test remains prepared for execution after implementation. This local and encrypted fixture evidence does not substitute for that test.
