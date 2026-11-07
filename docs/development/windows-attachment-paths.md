# Windows attachment checks for nested paths

The native folder-transfer control exposed an actual attachment-service failure on a 271-character volume-GUID path. A preserved earlier worker returns `0x80070057` for the generated, unexecuted text file; the corrected worker returns `S_OK` for that same file and identity. The failure prevented completion, as intended, but interrupted otherwise valid nested folder copies.

## Behavior and binding

The worker first tries the original canonical path. Only an `E_INVALIDARG` result from `IAttachmentExecute.SetLocalPath`, with a path of at least 260 characters, permits one compatibility retry. A failed `Save`, policy result, timeout or process failure never permits that retry.

The retry asks Windows for existing short names for the **parent directories only**. It preserves the complete original leaf name and extension, keeps the same volume-GUID root, requires a path shorter than 260 characters and opens the alias to verify the exact original native file identity, regular-file type and single-link count. A fresh attachment-service instance must successfully execute `Save`. Source and referrer remain unset; the receiver still applies Restricted Zone origin metadata and verifies identity, size and SHA-256 after the handler finishes.

The worker holds every canonical ancestor from the volume root through the parent, refusing reparse points and withholding write/delete sharing on those directory handles. The canonical leaf and alias handles withhold delete sharing while allowing scanner writes. Preexisting incompatible parent handles prevent approval. All handles survive through `Save` and are disposed afterward. The existing bounded, ordinary-user broker, process job, cancellation and deadline remain in force.

No short names are created and no volume, registry or Windows security policy is changed. If short names are absent, insufficient, unavailable to this user, or cannot be bound safely, the original failure remains a local review state. Long leaf names and folders whose ancestors cannot be pinned can therefore still require owner review. This is a compatibility fallback, not support for every filesystem or every long path.

Microsoft documents the relevant behavior in [GetShortPathNameW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getshortpathnamew), including the absence of guaranteed short names; [SetFileShortNameW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-setfileshortnamew), which requires DELETE access; and [IAttachmentExecute.Save](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-iattachmentexecute-save), which can invoke security providers and alter or delete a file. Successful checking and an intact checksum do not establish that content is malware-free.

## Verification

`AttachmentPathBindingTests` covers a real nested Unicode save, parent and leaf rename refusal, short-name reassignment refusal, parent write-handle refusal, wrong identity, preexisting parent mutation handles and an unshortenable leaf that remains untrusted. Existing attachment policy, process containment, cancellation, content-change and recovery tests remain applicable.

The native folder control sends six real items in both directions: a root, an empty directory, a nested directory, a Unicode text file, a 2,097,169-byte random payload and a zero-length file. It loses one actually accepted 32,768-byte range reply, recreates the private-source and sender managers, resumes the original parent intent, checks all six retained outcomes, preserves source and collision originals, and refuses new work after the receiving grant is withdrawn. Both listing-without-read and blind receiving grants are covered. Blind saves retain their randomized names; the test compares logical tree hashes through the actual returned item-name receipts and separately checks the complete physical tree.

These are Windows-to-Windows native controls. The existing Windows–Android ordinary-upload probe remains queued behind the device sequence. This change does not establish Android folder-batch, full controller/invitation/coordinator, physical-network, installed-package or independent security-review completion.

Local evidence is retained under `.tools/phase119-*`, including the earlier/fixed worker comparison, focused and regression TRX reports, build logs and the exact source-built native-control report. Build and test outputs remain outside the source repository.

The final Release solution build completes in 28.82 seconds with zero warnings/errors. All **878 selected main Windows tests pass**, with zero failures/skips, in 4 minutes 14 seconds. The three 4,096-item capacity cases and the separate large-file class retain their established exclusions from this selection. An earlier intermediate build passes 24 focused cases and 877 selected cases; the final run includes the additional parent-writer refusal case.

On the pinned source-built transport (`84a0a63dcc579d97e4afac959866f9e61e9584c18732045a48db673d8ec87dde`), all nine established native cases pass, followed by all four upload/folder-engine controls and five attachment-path cases, with zero failures/skips. Evidence is in `.tools/native-transport-verification/d09e7bb0e79c42c78abcfcd8962df9d6/`. Ten further runs of both readiness compatibility cases pass all 20 checks; their pointer is `.tools/phase119-compatibility-repeats.json`.

The previous documentation checkpoint's GitHub main regression had one compatibility-test teardown failure after its exact missing-readiness refusal was verified. Its cleanup now accepts the expected closed-connection drain only after all refusal and no-authority assertions have passed. This does not relax production authentication or the test's handshake requirements.
