# Windows isolated image and PDF previews

Windows now previews PNG, baseline/progressive JPEG, PDF and UTF-8 text from approved phone storage or independent local shortcuts. Open one file in either explorer. The viewer provides zoom, rotation, reset, PDF page navigation and Close. Navigation, discovery, connection loss, window deactivation/minimization, hiding and shutdown cancel the relevant preview and remove its visible state. Local browsing does not create a peer, remote grant or coordinator connection.

## Boundary and limits

The existing bounded reader checks the original file ID, grant, name, version, size and read permission before, during and after reading. It refuses inconsistent EOF, zero-progress ranges, changed files, canceled requests and oversized media. Text is strict UTF-8, limited to the first 32 KiB and displayed as plain text.

Native codecs execute in a separate self-contained Windows worker. The broker launches a capability-free AppContainer with only its stdin/stdout pipe ends inherited. The child is created suspended, assigned to a kill-on-close Job Object and checked for an AppContainer token before execution. A generated inheritable owner file handle is explicitly excluded in the isolation tests. The broker checks Windows loopback exemptions and refuses to run if its sandbox is exempt or the network-isolation configuration cannot be read. It never changes the machine's exemptions.

The worker gets file bytes and a page index, without a source path, filename, grant or connection credentials. Its environment contains an allowlist of OS profile paths and runtime settings. Only its deployed `PreviewWorker` folder receives sandbox read/execute access; redirected or oversized worker trees are rejected. The worker accepts one bounded request, uses Windows BitmapDecoder/PdfDocument and returns a fixed header plus premultiplied raw BGRA pixels. The broker validates every output dimension, page and byte count. WinUI copies those pixels to a WriteableBitmap; it never opens the received PNG/JPEG/PDF with a decoder.

| Resource | Bound |
| --- | --- |
| Media input | 8 MiB, complete file required |
| PNG/JPEG source | 8,192 per side, 16,777,216 pixels |
| JPEG header scan | 256 KiB / 4,096 markers |
| PDF | 128 pages, selected page sides at most 16,384 points |
| Output | At most 1,280 × 1,280 × 4 bytes, exact declared length |
| Concurrent renderers | One per application process across both explorers |
| Native process/job committed memory | 384 MiB each |
| Native CPU / wall time | 10 seconds / 15 seconds |
| Child processes | One active process in the job, including the renderer |
| UI resources | Clipboard read/write, global atoms, desktop switching, display/system changes and shutdown restricted by the job |

Cancel, timeout, malformed output or excess output terminates the job, waits for the worker and closes the pipes. Tests verify the original process is gone. There is no unrestricted decoding fallback. Decoding errors are generic; native numeric error codes remain exception diagnostics rather than document text. PNG/JPEG request bytes are released after rendering. Only the active PDF's bytes remain for page navigation; closing cancels work and zeroes that owned byte array. Broker request/response buffers are zeroed after exchange. These are lifecycle controls, not a claim of forensic erasure from all managed/native/GPU/OS memory.

The stable per-Windows-user AppContainer profile is reused and its read/execute ACL rule is merged. No input staging file is created by iConnect. Windows still owns the AppContainer's private profile storage, and ordinary AppContainer access to resources explicitly granted to all application packages remains part of this boundary. Stronger LPAC compatibility, scratch-storage quotas/cleanup, profile removal during uninstall, signed/protected worker installation and independent review remain release work. The job's memory bound covers committed process memory; it is not a system-wide GPU or broker-service quota.

## Verification

Run `./tools/dev.ps1 build -c Release`, then `./tools/dev.ps1 test -c Release --no-build`. CI's solution test command includes the new Windows-only preview tests. Both normal build output and desktop publish output include the self-contained worker in `PreviewWorker/`; the test-only hostile fixture is copied only into the test output. Desktop publish also explicitly copies compiled XAML (`*.xbf`) and the application PRI, which the unpackaged SDK publish graph omitted. CI checks those assets and the worker runtime. Missing XAML was reproduced as a published-app startup failure and repaired before delivery.

Current evidence:

- Full Release solution build: zero warnings/errors; locked restore passes.
- Core/model/storage/coordinator/native transport regression: 180 tests pass, including viewer cancellation, late-read/decode publication and cached-PDF cleanup.
- Eight Windows preview tests: real PNG/JPEG pixels, a 3,000 × 2,000 PNG downsample, green/blue two-page PDF, bad PDF/page/frame bounds, real offline local authority → reader → isolated codec → frame, denied private-file and loopback access, child-process denial, inherited-handle exclusion, cancellation/deadline process death, memory exhaustion and bounded output.
- Native WinUI fixture: observed red PNG, zoom, 90° rotation and reset; green PDF page 1 and blue page 2 with correct navigation controls; Close and leaving/returning to local browsing cleared the viewer. This was a generated offline fixture with no sharing approvals.
- Published output: three native media/local-controller tests pass against the worker under `.tools/desktop-publish-phase19/PreviewWorker/`. The repaired published desktop starts, displays the red PNG using the final broker policy, and clears its preview when minimized/restored. The fixture window and workers stopped, and only its generated CNG identity was removed. For a custom publish test, set `ICONNECT_PREVIEW_TEST_WORKER` in the test process to that published worker executable and run the `Native_` / `Offline_local` tests; this override exists only in test code.
- Android code/protocol were unchanged in this phase. The prior 83 Android device/UI tests, nine JVM tests and authenticated media interoperability are recorded in [phase 18](phase-18-bounded-previews.md), not counted as a fresh Android run here.

Broader Windows 10/11 codec/COM compatibility, full native keyboard/screen-reader and foreground/minimize/restore coverage, corrupt/encrypted/large PDF corpus, image color/orientation formats, concurrency across application instances, crash/storage pressure, physical Android-source Windows media previews and independent security review remain required. The separate-network operator kit stays prepared for the user to run after implementation. This feature does not close the full-application completion goal.

## Platform contracts

- [Launch an AppContainer](https://learn.microsoft.com/en-us/windows/win32/secauthz/implementing-an-appcontainer)
- [Explicit process handle inheritance](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute)
- [Job memory limits](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_extended_limit_information)
- [Job UI restrictions](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_basic_ui_restrictions)
- [Read AppContainer loopback exemptions](https://learn.microsoft.com/en-us/windows/win32/api/netfw/nf-netfw-networkisolationgetappcontainerconfig)
