# Bounded file previews and Android renderer isolation

Android now has PNG, JPEG and PDF previews in both the remote and independent local explorers. The viewer offers PDF page navigation, pinch/drag and button zoom, rotation and close. The original file is unchanged. The complete [application goal](completion.md) remains active: Windows image/PDF rendering is still pending.

## Input and authority

The C# and Kotlin preview readers use the existing opaque file RPC. They verify the selected ID, grant, filename, version, size and Read permission before fetching, on every returned range and after the last range. Ranges and EOF must agree with the original size. Revocation or a changed source observed by these checks prevents publication, including a revocation between the last range and final metadata check. Range requests stay at or below 32 KiB, with a maximum of 512 responses. Android's fetch has a 30-second deadline and is pinned to the original authenticated session or local-browser revision. Permissions cannot withdraw bytes already delivered to a recipient.

Image/PDF inputs are limited to 8 MiB and fetched fully before decoding. Text remains a 32 KiB prefix, using strict UTF-8 and removing display controls while retaining tabs/newlines. An incomplete trailing UTF-8 character is allowed only when the source continues beyond the prefix. A NUL or invalid complete UTF-8 input does not become a text preview. Windows remote and local text previews now use the same metadata checks; a newer remote preview or navigation prevents an older result from replacing it.

There is no new server upload endpoint, file grant or preview-specific network capability. Opening a local shortcut still creates no remote approval. Preview RPCs need the normal current Read permission; previews cannot inherit background transfer authority.

## Android decoding boundary

Each media render uses a fresh, nonexported `isolatedProcess` service instance. The isolated application does not initialize the app controller. It receives a read-only descriptor for the selected, bounded input rather than a provider URI, device key, grant store or arbitrary filesystem path. Its source descriptor is seekable for PDF rendering. The Android [PdfRenderer reference](https://developer.android.com/reference/android/graphics/pdf/PdfRenderer) recommends an isolated process for untrusted input; [bindIsolatedService](https://developer.android.com/reference/android/content/Context#bindIsolatedService(android.content.Intent,int,java.lang.String,java.util.concurrent.Executor,android.content.ServiceConnection)) supplies a separate service instance.

The service uses platform image decoding and PDF rasterization. It returns bounded raw ARGB pixels through [read-only shared memory](https://developer.android.com/reference/android/os/SharedMemory), plus page/dimension metadata. The parent validates that metadata and copies the raster; it does not decode a returned image format. The service reports an isolated PID, denied network permission and denied access to the app data directory, which the client checks. Device tests confirm these properties. The OS manifest/process boundary remains the authority; response fields alone are not a substitute for it.

The renderer is serialized across the app, has a 15-second wall deadline and is cancelled/unbound on close, superseding work or leaving the foreground. Its control thread remains separate from native decoder work. Cancellation and timeout tests use a blocked debug-only service, confirm that its Binder endpoint dies, and verify input-cache cleanup. That fixture service is excluded from the release manifest.

Inputs are staged under the app's private cache with random names and removed after each render. A crash can leave a bounded owned temporary file, which the next preview cleans up. The current PDF/input remains in memory for page navigation until the preview is closed or invalidated. This cache is not a durable receiving job, shared file or backup. Published bitmaps are released through UI ownership/GC so a frame cannot be recycled while Compose is drawing it.

## Supported formats and limits

| Input | Bounds and behavior |
| --- | --- |
| PNG | Signature/IHDR probe, nonzero dimensions, at most 8,192 pixels per side and 16,777,216 source pixels; actual decoder dimensions must match. First decoded frame only. |
| JPEG | Baseline/progressive 8-bit grayscale or three-component headers; at most 256 KiB/4,096 markers of header scanning, with the same side/pixel bounds and actual-dimension check. |
| PDF | Supported `%PDF-1.0`–`1.7` or `2.0` header, at most 128 pages and 16,384 source page units per side. Encrypted, malformed or unsupported files return an error. |
| Rendered page/image | At most 1,280 pixels per side, ARGB8888, with exact byte-count and page-index checks. Zoom affects this bounded raster. Rotation is a viewer action; EXIF orientation is not automatically applied. |
| UTF-8 text | Up to 32 KiB, plain text only. Binary/unsupported inputs receive a supported-format/save-separately message. |

PDF interaction is raster-only: the app exposes no embedded script execution, external document links or forms. No HTML viewer is used. The input, geometry, page and output limits reduce resource risk; there is no hard native-heap quota. A hostile parser can allocate resources while opening a PDF before page metadata is available. Isolation, deadline enforcement, maintained OS parsers, a wider malformed-file corpus and independent parser/security review remain important release gates.

## Verification

The tests cover actual PNG/JPEG colors and PDF pages, malformed files, dimension bombs, output downsampling, page-count limits, cancellation/deadline process termination, late source replies, private temporary cleanup, revoked/changed sources and malformed range/EOF metadata. Fifteen .NET cases cover the reader/probe contract and real NTFS source revocation. Four additional JVM cases exercise the corresponding Android input contract. Native local UI coverage opens an image without sharing the folder and exercises zoom/rotation/close and foreground lifecycle.

The interoperability fixture generates its own red PNG and green/blue two-page PDF on Windows. The Android controller fetches them over the authenticated direct channel, checks the resulting pixels/pages and clears the preview on navigation. The expected marker is `MEDIA_PREVIEW_VERIFIED`; it is separate from transfer/checksum and delete/restore markers.

The full .NET run passes 177 tests, and the changed Windows local/controller paths also pass their focused 18-case regression run. Android's nine JVM tests and all 83 device/UI tests pass; debug/test APKs and an unsigned release APK build, with no lint errors (42 advisories). Native preview/UI checks include process termination on cancel/deadline. The final viewer-copy build also passes the focused image UI/lifecycle test. A fresh authenticated Windows–Android run reports `MEDIA_PREVIEW_VERIFIED` and retains both-direction file/folder, delete/restore and checksum evidence. Final inspection finds no surviving preview processes or input-cache files; the owned coordinator and emulator are stopped afterward.

One initial image UI run found that a local folder could retain its prior scroll position with the search control above the viewport. Local navigation/search now resets that scroll position, and the UI helper can scroll in either direction. The focused native UI/rendering rerun passes. A renderer instance-name validation failure was fixed before the device tests passed; instance identifiers now use an Android-compatible hexadecimal suffix. The blocked-worker fixture also verifies termination through an actual Binder ping rather than the proxy's cached liveness field.

Windows media rendering and equivalent isolation, broader file/OS/provider and accessibility coverage, crash/cache repair, native memory-pressure testing and independent security review remain unfinished. The user-run [physical network test](physical-network-validation.md) stays prepared for execution after implementation.
