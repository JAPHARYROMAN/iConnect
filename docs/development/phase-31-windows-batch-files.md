# Windows scoped batch files

This increment adds Windows file-byte receiving and publication inside accepted outgoing batch plans. It reuses the existing incoming-file engine and received-file security boundary. Android has the matching protocol/reply validation but its provider-backed batch-file engine remains unfinished. The optional `upload-batches-v1` capability remains unadvertised. Capability negotiation controls compatibility; server authorization checks remain mandatory independently.

## Binding and publication

Operations 39–42 prepare, write, finish and cancel a file child. Item status (38) dispatches by the kind recorded in the accepted manifest. Each call retains the original parent batch ID/history epoch and explicitly present item index. The host derives the child ID, name, size, SHA-256, collision choice and destination from that original plan. Requests cannot substitute a destination/name/descriptor or turn a directory item into a file.

The parent stores a separate file-child binding and attempt marker before the incoming engine records its intent and creates the owned part. There is no empty file journal that can later look like permission to start a new copy. Missing or changed bindings, indexes or file journals fail closed for review. Cold startup validates each file journal against its original peer, owner scope, deterministic child ID, manifest descriptor and logical parent identity. Orphaned journals cannot become standalone transfers or fresh batch children.

All created ancestors are opened by their recorded identities. Their native handles remain held through file I/O and publication, so those ancestors cannot move during an operation. File grants, exclusions, protected-profile containment and owner policy are checked again. Pairing/session closure cancels batch verification; reconnecting must use the retained original authority and receipt. Permission/exclusion withdrawal and a new pairing generation are tested.

Preparation checks the current planned filename's exclusions before creating its child intent. The selected destination's existing 64-component record limit is now enforced at admission, and every parent record is validated before saving. Previously that bound was only checked on reload. A real-directory boundary test verifies that depth 65 is refused without a record while depth 64 accepts a file child and reopens cleanly. The plan's selected destination and all manifest components also remain subject to grant containment checks at sealing.

Parts are marked with the existing untrusted Windows origin before payload writes. The existing flushed checkpoint/prefix-chain and whole-file SHA-256 checks remain. Publication uses native no-replace rename. Completion still requires the Windows attachment broker's successful result, retained native identity/length/full digest and final origin mark. No code executes the received contents. Cancellation deletes only the identified owned part, refuses uncertain published copies and retains its canceled receipt. Keep-both/skip/fail preserve owner files.

Client checks in C# and Kotlin bind the reply's file bytes, name, size, collision choice and outcome to the original child descriptor. A claimed successful item must agree with the completed upload's final name.

## Bounds, reservations and recovery

The parent continues to own the plan's aggregate incoming-byte reservation; individual scoped receivers do not charge those bytes again. Ordinary uploads and all batch-file receivers share two verification slots. The slots are released on completion, cancellation and failure. Two scoped receiver objects are cached; an uncached status request can use one transient object without starting work. Accepted-manifest bodies remain in the bounded two-entry cache rather than being captured by every child receiver.

File journals are flat private metadata beside the parent and directory receipts. Each is limited to 256 KiB, and file-journal bytes to 16 MiB per batch. The existing 4 MiB child-binding budget remains separate. Native file versions detect missing/external journal changes before cached state is reused. The eight-plan / four-per-peer / 4,096-item limits remain. Long destination paths can reach the metadata budget before the item limit; the host refuses rather than dropping receipts.

Local metadata archiving requires idle batch workers, preserves archived journals, rotates the history namespace and keeps destination items and unfinished parts. It does not adopt or remove a part merely by filename. The review text warns that archived receipts are needed to identify unfinished parts safely. Parent cancellation after effects have started still retains its reservation and requires local review; complete per-plan stop/completion accounting and native incoming/outgoing recovery controls remain work for the full sending workflow.

An initial focused run exposed a lock inversion in waiting for file work: the parent lock was held while waiting for a receiver that needed that parent to commit progress. Waiting and cancellation now snapshot receivers under the parent lock and operate after releasing it. The owned deadlocked test host was stopped after that concrete fix; the rerun passed. Journal commits/notifications avoid acquiring the parent lock synchronously from failure cleanup.

## Verification

The final solution build passed with zero warnings/errors. The final main regression passed **324 tests in 46 seconds**, excluding the separately exercised file-capacity case. The capacity/limiter/reservation/reply-binding run passed **four tests in 4 minutes 14 seconds**. The capacity case precedes the later parent-record/admission validation and filename-exclusion preflight fixes; its successful file path was not rerun at full capacity on the final build. Subsequent focused checks and the final regression pass the owner-policy/pairing, destination-depth boundary, current filename exclusions, reply binding and expanded native capability refusal. All **eight Windows preview tests** passed in a separate run with unchanged sandbox limits. These are separate runs, not a claim of one combined full-suite pass.

The capacity case accepts **4,096 items** (one directory and **4,095 actual three-byte files**), prepares/writes/publishes every file, exceeds ordinary per-file receipt limits without ordinary rows, reopens the private store and recovers the final receipt and actual bytes. It injects an accepting attachment checker to isolate scheduling and metadata capacity; origin marks, real files, prefix/digest checks and no-replace publication still execute. A separate scoped restart/resume test uses the **actual Windows attachment checker**, confirms the resulting bytes/origin and retained security-policy version. The capacity case does not establish 4,095 native attachment-broker calls or malware-free files.

Other cases cover changed repeated ranges, stale/replaced parents, descriptor/index/kind substitution, ordinary-ID separation, shared aggregate reservations, cancellation, collisions, missing/corrupt plan/binding/index/journal metadata and cold recovery. A held-checker test proves that ordinary and scoped work share two slots, that history cannot archive active workers, and that scope closure stops batch verification before later explicit finishing.

Android passed **38 JVM tests**, debug/instrumentation/unsigned-release APK builds and lint (**zero errors, 57 advisories**). After installed app/test APK digests were verified against the build outputs, **18 focused device tests passed in 9.385 seconds**. They cover runtime contract/receipt validation, plan admission, nested-directory recovery, absence of an incoming host and native authenticated Android peers refusing all new file calls without the advertised capability. Android batch-file effects remain explicitly unsupported. This was a focused compatibility run, not another full device regression.

| Artifact | SHA-256 |
| --- | --- |
| App | `a992548a595c5699b88c106c2074564853e4d2fd4078424a412203be054c7f0a` |
| Instrumentation | `09a967883c2c35d40e8716d5ed5aa5763781719b371dd9d0fac5afa3959c9fcb` |

No fresh Windows–Android batch interoperability is claimed. Power-loss/storage-controller durability, large-file/large-path/provider/concurrent-load and installed security-provider evidence remain separate gates. Windows Computer Use remains stopped and the earlier desktop fixture window is untouched. The [completion audit](completion.md) retains the complete product objective; the [physical-network kit](physical-network-validation.md) remains prepared for the user's later run after implementation.
