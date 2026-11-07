package com.iconnect.proof

import android.app.Application
import android.net.Uri
import android.os.Bundle
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.*
import org.junit.Assume.assumeTrue
import org.junit.Test
import org.junit.runner.RunWith
import com.iconnect.protocol.*
import java.security.MessageDigest
import kotlinx.coroutines.*

/** Explicit test-APK fixture. Production app code has no auto-approval mode. */
@RunWith(AndroidJUnit4::class)
class InteropTest {
    @Test fun windowsAndAndroidAuthenticateAndVerifyBothDirections() {
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val invitation = InstrumentationRegistry.getArguments().getString("invitation")
        assumeTrue("Supply a fresh invitation from IConnect.InteropRunner.", !invitation.isNullOrEmpty())
        val context = instrumentation.targetContext
        val phoneFixture = requireNotNull(context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "fixture", null,
            Bundle().apply { putBoolean("emptyFolder", true) }))
        lateinit var model: PhoneController
        fun onMain(action: () -> Unit) = instrumentation.runOnMainSync { action() }
        fun status(value: String) = instrumentation.sendStatus(0, Bundle().apply { putString("iconnect", value) })
        fun waitFor(label: String, timeout: Long = 35_000, predicate: () -> Boolean) {
            val deadline = System.currentTimeMillis() + timeout
            while (System.currentTimeMillis() < deadline) {
                if (predicate()) return
                Thread.sleep(50)
            }
            fail("Timed out: $label. ${model.state.value.events.joinToString(" | ")}. Downloads: ${model.state.value.downloads.map { "${it.phase}/${it.active}: ${it.error}" }}")
        }
        onMain {
            model = (context.applicationContext as IConnectApplication).controller()
            model.setUiVisible(true)
            model.forgetPeer()
            model.setServer("http://10.0.2.2:5078")
            model.setStun(InstrumentationRegistry.getArguments().getString("stun")?:"")
            model.setInvitation(requireNotNull(invitation))
            model.chooseDownloadDestination(Uri.parse(phoneFixture.getString("tree")), phoneFixture.getInt("flags"))
        }
        try {
            waitFor("saved revocation") { !model.state.value.savingTrust && !model.state.value.revocationPending }
            onMain { model.connect() }
            waitFor("coordinator registration") { model.state.value.connected }
            onMain { model.requestPairing() }
            waitFor("pairing challenge") { model.state.value.code.isNotEmpty() }
            status("CODE ${model.state.value.code}")
            // The harness controls both disposable fixtures and verifies their codes.
            onMain { model.approve(model.state.value.code) }
            waitFor("mutual pairing") { model.state.value.peerName.isNotEmpty() }
            onMain { model.addPhoneShare(Uri.parse(phoneFixture.getString("tree")), phoneFixture.getInt("flags"), "Interop phone documents") }
            waitFor("explicit fixture-only phone approval") { model.state.value.phoneShares.any { it.label == "Interop phone documents" } }
            status("PAIRED")
            if(InstrumentationRegistry.getArguments().getString("initiator") in listOf("phone","glare")) {
                onMain {model.connectPeer()}; status("PHONE_INITIATED")
            }
            waitFor("authenticated direct connection") { model.state.value.authenticated }
            status("AUTHENTICATED")
            onMain { model.browseRoots() }
            waitFor("approved Windows storage") { model.state.value.remoteEntries.any { it.isRoot && it.name == "Interop documents" } }
            onMain { model.browseFolder(model.state.value.remoteEntries.single { it.isRoot && it.name == "Interop documents" }) }
            waitFor("Windows directory listing") { model.state.value.remoteEntries.any { it.name == "ripoti.txt" } }
            onMain { model.previewText(model.state.value.remoteEntries.single { it.name == "ripoti.txt" }) }
            waitFor("actual Windows file preview") { model.state.value.textPreview.contains("iConnect approved-file preview.") }
            val approvedRoot = model.state.value.breadcrumbs.first()
            onMain { model.browseFolder(model.state.value.remoteEntries.single { it.name == "Explorer fixture" }) }
            waitFor("complete multipage directory") { !model.state.value.listingBusy && model.state.value.explorerCount == 111 }
            assertEquals(3, model.state.value.explorerPages)
            val firstSelected = model.state.value.remoteEntries.single { it.name == "item-000.txt" }
            onMain { model.selectExplorerItem(firstSelected.id, true); model.nextDirectoryPage(); model.nextDirectoryPage() }
            assertEquals(2, model.state.value.explorerPage)
            onMain { model.selectExplorerItem(model.state.value.remoteEntries.single { it.name == "item-109.txt" }.id, true); model.setExplorerSort(PhoneExplorerSort.SIZE, true); model.setExplorerLayout(PhoneExplorerLayout.GRID) }
            assertEquals(2, model.state.value.selectedFiles.size); assertEquals("item-109.txt", model.state.value.remoteEntries.first { !it.isDirectory }.name)
            onMain { model.searchFiles("HABARI") }
            waitFor("scoped nested filename search") { !model.state.value.listingBusy && model.state.value.searchQuery == "HABARI" }
            assertEquals("Explorer fixture / Nested / Habari-文書.txt", model.state.value.explorerRows.single().relativePath)
            assertEquals(2, model.state.value.selectedFiles.size)
            onMain { model.planBatch(model.state.value.selectedFiles.map { it.entry }) }
            waitFor("cross-page selected copy plan") { model.state.value.batchPlan != null }
            assertEquals(2, model.state.value.batchPlan!!.manifest.items.size)
            onMain { model.dismissBatchPlan(); model.searchFiles("Nested") }
            waitFor("matched folder route") { !model.state.value.listingBusy && model.state.value.searchQuery == "Nested" }
            onMain { model.browseFolder(model.state.value.remoteEntries.single()) }
            waitFor("opening search folder preserves breadcrumbs") { !model.state.value.listingBusy && model.state.value.breadcrumbs.size == 3 }
            onMain { model.showFileDetails(model.state.value.remoteEntries.single()) }
            waitFor("live metadata details") { model.state.value.fileDetails != null }
            assertEquals("Habari-文書.txt", model.state.value.fileDetails!!.name)
            onMain { model.dismissFileDetails(); model.browseFolder(approvedRoot) }
            waitFor("return to original approved scope") { !model.state.value.listingBusy && model.state.value.remoteEntries.any { it.name == "ripoti.txt" } }
            status("EXPLORER_QUERY_VERIFIED")
            onMain { model.browseFolder(model.state.value.remoteEntries.single { it.name == "Changes fixture" }) }
            waitFor("file management fixture") { !model.state.value.listingBusy && model.state.value.remoteEntries.any { it.name == "original.txt" } }
            onMain { model.createFolder("Destination") }
            waitFor("durable remote folder creation") { !model.state.value.mutationBusy && !model.state.value.listingBusy && model.state.value.remoteEntries.any { it.name == "Destination" } }
            onMain { model.selectExplorerItem(model.state.value.remoteEntries.single { it.name == "original.txt" }.id, true); model.renameSelected("renamed.txt") }
            waitFor("durable remote rename") { !model.state.value.mutationBusy && !model.state.value.listingBusy && model.state.value.remoteEntries.any { it.name == "renamed.txt" } }
            onMain { model.selectExplorerItem(model.state.value.remoteEntries.single { it.name == "renamed.txt" }.id, true); model.beginMove(); model.browseFolder(model.state.value.remoteEntries.single { it.name == "Destination" }) }
            waitFor("move destination") { !model.state.value.listingBusy && model.state.value.breadcrumbs.last().name == "Destination" }
            onMain { model.moveHere() }
            waitFor("durable remote move") { !model.state.value.mutationBusy && !model.state.value.listingBusy && model.state.value.remoteEntries.any { it.name == "renamed.txt" } }
            assertNull(model.state.value.pendingMove)
            assertEquals(3, model.state.value.recentMutations.count { it.phase == com.iconnect.protocol.MutationPhase.CHANGE_SUCCEEDED })
            onMain { model.previewText(model.state.value.remoteEntries.single { it.name == "renamed.txt" }) }
            waitFor("moved fixture content") { model.state.value.textPreview.contains("Manage this approved fixture.") }
            status("FILE_CHANGES_VERIFIED")
            val recoverable = model.state.value.remoteEntries.single { it.name == "renamed.txt" }
            onMain { model.selectExplorerItem(recoverable.id, true); model.deleteSelected(recoverable) }
            waitFor("recoverable Windows deletion") { !model.state.value.mutationBusy && !model.state.value.listingBusy &&
                model.state.value.remoteEntries.none { it.name == "renamed.txt" } && model.state.value.deletedItems.any { it.name == "renamed.txt" && it.state == DeletedState.DELETED_STORED } }
            val savedItem = model.state.value.deletedItems.single { it.name == "renamed.txt" }
            onMain { model.restoreDeleted(savedItem, "renamed.txt") }
            waitFor("Windows restore without replacement") { !model.state.value.mutationBusy && !model.state.value.listingBusy &&
                model.state.value.remoteEntries.any { it.name == "renamed.txt" } && model.state.value.deletedItems.any { it.id == savedItem.id && it.state == DeletedState.DELETED_RESTORED } }
            onMain { model.previewText(model.state.value.remoteEntries.single { it.name == "renamed.txt" }) }
            waitFor("restored Windows fixture bytes") { model.state.value.textPreview.contains("Manage this approved fixture.") }
            status("RECOVERABLE_DELETE_VERIFIED")
            onMain { model.browseFolder(approvedRoot) }
            waitFor("return after file changes") { !model.state.value.listingBusy && model.state.value.remoteEntries.any { it.name == "ripoti.txt" } }
            onMain { model.previewText(model.state.value.remoteEntries.single { it.name == "ripoti.txt" }) }
            waitFor("restored preview") { model.state.value.textPreview.contains("iConnect approved-file preview.") }
            status("FILE_BROWSING_VERIFIED")
            onMain { model.browseFolder(model.state.value.remoteEntries.single { it.name == "Preview fixture" }) }
            waitFor("preview fixture folder") { !model.state.value.listingBusy && model.state.value.remoteEntries.any { it.name == "fixture.png" } }
            onMain { model.previewText(model.state.value.remoteEntries.single { it.name == "fixture.png" }) }
            waitFor("real Windows PNG preview") { model.preview.state.value.frame != null && !model.preview.state.value.busy }
            assertEquals(android.graphics.Color.RED,model.preview.state.value.frame!!.bitmap.getPixel(0,0))
            onMain { model.previewText(model.state.value.remoteEntries.single { it.name == "fixture.pdf" }) }
            waitFor("real Windows PDF preview") { model.preview.state.value.frame?.pages == 2 && !model.preview.state.value.busy }
            assertEquals(android.graphics.Color.GREEN,model.preview.state.value.frame!!.bitmap.getPixel(10,10))
            onMain { model.preview.page(1) }
            waitFor("second PDF preview page") { model.preview.state.value.frame?.page == 1 && !model.preview.state.value.busy }
            assertEquals(android.graphics.Color.BLUE,model.preview.state.value.frame!!.bitmap.getPixel(10,10))
            onMain { model.browseFolder(approvedRoot) }
            waitFor("return after media preview") { !model.state.value.listingBusy && model.state.value.remoteEntries.any { it.name == "jaribio.bin" } }
            assertNull(model.preview.state.value.frame)
            status("MEDIA_PREVIEW_VERIFIED")
            waitFor("saved download destination") { model.state.value.downloadDestination.isNotEmpty() }
            val previousDownloads = model.state.value.downloads.map { it.id }.toSet()
            onMain { model.downloadFile(model.state.value.remoteEntries.single { it.name == "jaribio.bin" }) }
            waitFor("verified Windows file in Android staging", 60_000) { model.state.value.downloads.any {
                it.id !in previousDownloads && it.name == "jaribio.bin" && it.phase == PhoneDownloadPhase.READY_TO_SAVE && !it.active } }
            val download = model.state.value.downloads.single { it.id !in previousDownloads && it.name == "jaribio.bin" }
            onMain { model.saveDownload(download.id) }
            waitFor("verified Windows file saved through SAF") { model.state.value.downloads.any { it.id == download.id && it.phase == PhoneDownloadPhase.COMPLETED } }
            val saved = model.state.value.downloads.single { it.id == download.id }
            val actual = context.contentResolver.openInputStream(Uri.parse(saved.published))!!.use { it.readBytes() }
            val expected = ByteArray(2 * 1024 * 1024 + 17) { (it * 31 % 251).toByte() }
            assertArrayEquals(expected, actual)
            status("REAL_WINDOWS_DOWNLOAD_VERIFIED ${Rules.hex(MessageDigest.getInstance("SHA-256").digest(actual))}")
            onMain { model.setPhoneCreate(model.state.value.phoneShares.single { it.label == "Interop phone documents" }.id, true) }
            waitFor("explicit phone folder creation permission") { !model.state.value.savingShare && model.state.value.phoneShares.single { it.label == "Interop phone documents" }.permissions and StorageRules.CREATE != 0 }
            status("PHONE_CREATE_READY")
            onMain { model.sendTestFile() }
            waitFor("Windows verified Android test file") {
                model.state.value.events.any { it.startsWith("Sent 256 KiB. Destination verified SHA-256:") }
            }
            status("SENT")
            waitFor("Android verified Windows test file") {
                model.state.value.events.any { it.startsWith("Received 256 KiB. Verified SHA-256:") }
            }
            assertTrue(model.state.value.authenticated)
            status("BIDIRECTIONAL_VERIFIED")
            // Fault-inject coordinator failure frames into the real connected controller.
            // Reflection stays in the test APK; production exposes no frame-injection entry point.
            val sessionField = PhoneController::class.java.getDeclaredField("session").apply { isAccessible = true }
            val activeSession = sessionField.get(model) as RtcTestSession
            val hostId = Rules.id(Rules.decode(requireNotNull(Uri.parse(invitation).getQueryParameter("key"))))
            fun unavailable(session: String, peer: String) = SignalUnavailable.newBuilder().setSessionId(session).setPeerId(peer).build()
            fun inject(value: SignalUnavailable) = runBlocking {
                withContext(Dispatchers.Main) {
                    kotlin.coroutines.suspendCoroutine<Unit> { continuation ->
                        val method = PhoneController::class.java.getDeclaredMethod("handle", Frame::class.java, kotlin.coroutines.Continuation::class.java).apply { isAccessible = true }
                        try {
                            val reply = method.invoke(model, Frame.newBuilder().setVersion(1).setKind("signal-unavailable").setPayload(value.toByteString()).build(), continuation)
                            if (reply !== kotlin.coroutines.intrinsics.COROUTINE_SUSPENDED) continuation.resumeWith(Result.success(Unit))
                        } catch (error: java.lang.reflect.InvocationTargetException) { continuation.resumeWith(Result.failure(error.targetException)) }
                    }
                }
            }
            inject(unavailable(StorageRules.token(), hostId)); assertTrue(model.state.value.authenticated)
            inject(unavailable(activeSession.sessionId, "a".repeat(64))); assertTrue(model.state.value.authenticated)
            inject(unavailable(activeSession.sessionId, hostId))
            assertFalse(model.state.value.authenticated); assertTrue(model.state.value.connected)
            assertFalse(model.state.value.backgroundReady); assertTrue(model.state.value.backgroundDownloads.isEmpty()); assertTrue(model.state.value.backgroundSends.isEmpty())
            status("OFFLINE_STATUS_BOUNDARY_VERIFIED")
        } finally {
            lateinit var cleanup: Job
            onMain {
                model.disconnect()
                cleanup = CoroutineScope(Dispatchers.Main).launch {
                    model.state.value.downloads.filter { it.tree == phoneFixture.getString("tree") }.forEach { row ->
                        if (row.phase !in setOf(PhoneDownloadPhase.COMPLETED, PhoneDownloadPhase.CANCELED)) model.cancelDownload(row.id).join()
                        model.removeDownload(row.id).join()
                    }
                }
                model.state.value.phoneShares.firstOrNull { it.tree.toString() == phoneFixture.getString("tree") }?.let { model.removePhoneShare(it.id) }
            }
            runBlocking { cleanup.join() }
            waitFor("fixture approval cleanup") { !model.state.value.savingShare }
            lateinit var closing: kotlinx.coroutines.Job
            onMain { closing = model.close() }
            kotlinx.coroutines.runBlocking { closing.join() }
            onMain { (context.applicationContext as IConnectApplication).releaseClosed(model) }
            context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "dispose", phoneFixture.getString("root"), null)
        }
    }
}
