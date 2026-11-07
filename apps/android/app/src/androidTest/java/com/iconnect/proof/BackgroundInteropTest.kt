package com.iconnect.proof

import android.Manifest
import android.app.Notification
import android.app.NotificationManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import androidx.lifecycle.Lifecycle
import androidx.test.core.app.ActivityScenario
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Assume.assumeTrue
import org.junit.Test
import org.junit.runner.RunWith

/** Real activity/service/notification and direct-channel lifecycle; disposable test data only. */
@RunWith(AndroidJUnit4::class)
class BackgroundInteropTest {
    @Test fun selectedDownloadContinuesWithoutHostingPhoneStorageAndNotificationCanPausePublication() {
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val invitation = InstrumentationRegistry.getArguments().getString("invitation")
        assumeTrue("Supply a fresh runner invitation.", !invitation.isNullOrEmpty())
        val context = instrumentation.targetContext
        if (Build.VERSION.SDK_INT >= 33) instrumentation.uiAutomation.grantRuntimePermission(context.packageName, Manifest.permission.POST_NOTIFICATIONS)
        val fixture = requireNotNull(context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "fixture", null, null))
        val tree = Uri.parse(fixture.getString("tree"))
        val app = context.applicationContext as IConnectApplication
        lateinit var model: PhoneController
        fun onMain(action: () -> Unit) = instrumentation.runOnMainSync { action() }
        fun status(value: String) = instrumentation.sendStatus(0, Bundle().apply { putString("iconnect", value) })
        fun waitFor(label: String, predicate: () -> Boolean) {
            val deadline = System.currentTimeMillis() + 40_000
            while (System.currentTimeMillis() < deadline) { if (predicate()) return; Thread.sleep(20) }
            fail("Timed out: $label. ${model.state.value.status}. ${model.state.value.downloads.map { "${it.name}/${it.phase}/${it.active}: ${it.error}" }}")
        }
        val notifications = context.getSystemService(NotificationManager::class.java)
        ActivityScenario.launch(MainActivity::class.java).use { scenario ->
            onMain {
                model = app.controller(); model.forgetPeer(); model.setServer("http://10.0.2.2:5078")
                model.chooseDownloadDestination(tree, fixture.getInt("flags")); model.setInvitation(requireNotNull(invitation))
            }
            try {
                waitFor("saved revocation") { !model.state.value.savingTrust && !model.state.value.revocationPending }
                onMain { model.connect() }
                waitFor("coordinator") { model.state.value.connected }
                onMain { model.requestPairing() }; waitFor("pairing code") { model.state.value.code.isNotEmpty() }
                status("CODE ${model.state.value.code}")
                onMain { model.approve(model.state.value.code) }
                waitFor("pairing") { model.state.value.peerName.isNotEmpty() }
                onMain { model.addPhoneShare(tree, fixture.getInt("flags"), "Interop phone documents") }
                waitFor("phone approval and destination") { !model.state.value.savingShare && model.state.value.phoneShares.any { it.tree == tree } && model.state.value.downloadDestination == tree.toString() }
                status("PAIRED"); waitFor("direct session") { model.state.value.authenticated }
                onMain { model.browseRoots() }; waitFor("roots") { model.state.value.remoteEntries.any { it.name == "Interop documents" } }
                onMain { model.browseFolder(model.state.value.remoteEntries.single { it.name == "Interop documents" }) }
                waitFor("Windows files") { model.state.value.remoteEntries.any { it.name == "background.bin" } }
                val before = model.state.value.downloads.map { it.id }.toSet()
                onMain { model.downloadFile(model.state.value.remoteEntries.single { it.name == "background.bin" }) }
                waitFor("gated transfer") { model.state.value.downloads.any { it.id !in before && it.name == "background.bin" && it.phase == PhoneDownloadPhase.DOWNLOADING && it.active } }
                val id = model.state.value.downloads.single { it.id !in before && it.name == "background.bin" }.id
                onMain { model.downloadFile(model.state.value.remoteEntries.single { it.name == "jaribio.bin" }) }
                waitFor("unselected queued transfer") { model.state.value.downloads.any { it.id !in before && it.name == "jaribio.bin" && it.active } }
                val unselected = model.state.value.downloads.single { it.id !in before && it.name == "jaribio.bin" }.id
                onMain { model.allowBackground(id) }
                waitFor("visible transfer notification") { model.state.value.backgroundReady && notifications.activeNotifications.any { it.notification.category == Notification.CATEGORY_PROGRESS } }
                scenario.moveToState(Lifecycle.State.CREATED)
                waitFor("only selected work continues") { model.state.value.downloads.single { it.id == unselected }.let { !it.active && it.phase == PhoneDownloadPhase.PAUSED } }
                assertTrue(model.state.value.authenticated)
                assertEquals(setOf(id), model.state.value.backgroundDownloads)
                status("BACKGROUND_READY")
                // Runner must assert phone-denied, then release-background.
                waitFor("background receive and automatic service stop") {
                    model.state.value.downloads.single { it.id == id }.let { it.phase == PhoneDownloadPhase.READY_TO_SAVE && !it.active } &&
                        !model.state.value.backgroundReady && !model.state.value.connected && notifications.activeNotifications.isEmpty()
                }
                status("BACKGROUND_DOWNLOAD_VERIFIED")
                scenario.moveToState(Lifecycle.State.RESUMED)
                context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "creationMode", fixture.getString("root"), Bundle().apply { putString("mode", "blockWrite") })
                onMain { model.saveDownload(id) }
                waitFor("provider publication") { model.state.value.downloads.single { it.id == id }.let { it.phase == PhoneDownloadPhase.PUBLISHING && it.published.isNotEmpty() && it.active } }
                onMain { model.allowBackground(id) }
                waitFor("publication notification") { model.state.value.backgroundReady && notifications.activeNotifications.any { it.notification.actions?.size == 1 } }
                scenario.moveToState(Lifecycle.State.CREATED)
                val notification = notifications.activeNotifications.single { it.notification.category == Notification.CATEGORY_PROGRESS }.notification
                assertEquals(Notification.VISIBILITY_PRIVATE, notification.visibility)
                notification.actions.single().actionIntent.send()
                waitFor("notification pause preserves uncertain copy") { model.state.value.downloads.single { it.id == id }.let {
                    !it.active && it.phase == PhoneDownloadPhase.PUBLICATION_INTERRUPTED } && !model.state.value.backgroundReady && notifications.activeNotifications.isEmpty() }
                status("NOTIFICATION_PAUSE_VERIFIED")
                // Recovery creates a new copy only after explicit owner choice; the uncertain empty copy remains.
                scenario.moveToState(Lifecycle.State.RESUMED)
                context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "creationMode", fixture.getString("root"), Bundle().apply { putString("mode", "") })
                onMain { model.saveDownload(id, anotherCopy = true) }
                waitFor("verified provider save") { model.state.value.downloads.single { it.id == id }.let { it.phase == PhoneDownloadPhase.COMPLETED && !it.active } }
                val result = model.state.value.downloads.single { it.id == id }
                val bytes = context.contentResolver.openInputStream(Uri.parse(result.published))!!.use { it.readBytes() }
                assertArrayEquals(ByteArray(2 * 1024 * 1024 + 17) { (it * 31 % 251).toByte() }, bytes)
                status("BACKGROUND_SAF_BYTES_VERIFIED")
            } finally {
                lateinit var cleanup: Job
                onMain {
                    model.disconnect()
                    cleanup = CoroutineScope(Dispatchers.Main).launch {
                        model.state.value.downloads.filter { it.tree == tree.toString() }.forEach {
                            if (it.phase !in setOf(PhoneDownloadPhase.COMPLETED, PhoneDownloadPhase.CANCELED)) model.cancelDownload(it.id).join()
                            model.removeDownload(it.id).join()
                        }
                        model.clearDownloadDestination().join()
                    }
                    model.state.value.phoneShares.firstOrNull { it.tree == tree }?.let { model.removePhoneShare(it.id) }
                }
                runBlocking { cleanup.join() }; waitFor("approval removal") { !model.state.value.savingShare }
            }
        }
        lateinit var closing: Job
        onMain { closing = model.close() }; runBlocking { closing.join() }; onMain { app.releaseClosed(model) }
        context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "dispose", fixture.getString("root"), null)
    }
}
