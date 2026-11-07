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

@RunWith(AndroidJUnit4::class)
class SendingInteropTest {
    @Test fun selectedPhoneSourceContinuesAndNotificationPauseCanResumeTheSameTicket() {
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val invitation = InstrumentationRegistry.getArguments().getString("invitation")
        assumeTrue("Supply a fresh interoperability runner invitation.", !invitation.isNullOrEmpty())
        val context = instrumentation.targetContext; val app = context.applicationContext as IConnectApplication
        if (Build.VERSION.SDK_INT >= 33) instrumentation.uiAutomation.grantRuntimePermission(context.packageName, Manifest.permission.POST_NOTIFICATIONS)
        val fixture = requireNotNull(context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "fixture", null, Bundle().apply { putBoolean("largeFile", true) }))
        val tree = Uri.parse(fixture.getString("tree")); val notifications = context.getSystemService(NotificationManager::class.java)
        lateinit var model: PhoneController
        var initialized = false
        fun onMain(action: () -> Unit) = instrumentation.runOnMainSync { action() }
        fun status(value: String) = instrumentation.sendStatus(0, Bundle().apply { putString("iconnect", value) })
        fun waitFor(label: String, predicate: () -> Boolean) {
            val deadline = System.currentTimeMillis() + 55_000
            while (System.currentTimeMillis() < deadline) { if (predicate()) return; Thread.sleep(20) }
            fail("Timed out: $label. ${model.state.value.status}; sends=${model.state.value.sends}")
        }
        try {
            ActivityScenario.launch(MainActivity::class.java).use { scenario ->
                onMain {
                    model = app.controller(); initialized = true; model.forgetPeer(); model.setServer("http://10.0.2.2:5078")
                    model.setInvitation(requireNotNull(invitation))
                }
                try {
                    waitFor("saved revocation") { !model.state.value.savingTrust && !model.state.value.revocationPending }
                    onMain { model.connect() }
                    waitFor("coordinator") { model.state.value.connected }; onMain { model.requestPairing() }
                    waitFor("code") { model.state.value.code.isNotEmpty() }; status("CODE ${model.state.value.code}")
                    onMain { model.approve(model.state.value.code) }; waitFor("pairing") { model.state.value.peerName.isNotEmpty() }
                    onMain { model.addPhoneShare(tree, fixture.getInt("flags"), "Interop phone documents") }
                    waitFor("owner approval") { !model.state.value.savingShare && model.state.value.phoneShares.any { it.tree == tree } }
                    status("PAIRED"); waitFor("direct session") { model.state.value.authenticated }
                    status("START_PHONE_SEND")
                    waitFor("ready active phone source") { model.state.value.sends.any { it.name == "kutuma.bin" && it.active && it.state == com.iconnect.protocol.DownloadState.DOWNLOAD_READY } }
                    val first = model.state.value.sends.single { it.name == "kutuma.bin" && it.active }.id
                    assertTrue(model.state.value.sends.any { it.name == "simu.txt" })
                    onMain { model.allowBackgroundSending(first) }
                    waitFor("sending notification") { model.state.value.backgroundReady && first in model.state.value.backgroundSends && notifications.activeNotifications.any { it.notification.actions?.size == 1 } }
                    scenario.moveToState(Lifecycle.State.CREATED)
                    assertTrue(model.state.value.authenticated); assertEquals(setOf(first), model.state.value.backgroundSends)
                    status("SENDING_BACKGROUND_READY")
                    // Runner must check phone-denied and sending-denied, then release-sending.
                    waitFor("source released and service ended") { model.state.value.sends.none { it.id == first } &&
                        !model.state.value.backgroundReady && !model.state.value.connected && notifications.activeNotifications.isEmpty() }
                    status("SENDING_ENDED")

                    scenario.moveToState(Lifecycle.State.RESUMED); onMain { model.connect() }
                    waitFor("coordinator reconnect") { model.state.value.connected }; status("RECONNECT_FOR_PAUSE")
                    waitFor("second direct session") { model.state.value.authenticated }; status("START_PHONE_SEND_AGAIN")
                    waitFor("second source") { model.state.value.sends.any { it.name == "kutuma.bin" && it.id != first && it.active && it.state == com.iconnect.protocol.DownloadState.DOWNLOAD_READY } }
                    val second = model.state.value.sends.single { it.name == "kutuma.bin" && it.id != first && it.active }.id
                    onMain { model.allowBackgroundSending(second) }
                    waitFor("second notification") { model.state.value.backgroundReady && notifications.activeNotifications.any { it.notification.actions?.size == 1 } }
                    scenario.moveToState(Lifecycle.State.CREATED)
                    val notification = notifications.activeNotifications.single { it.notification.category == Notification.CATEGORY_PROGRESS }.notification
                    notification.actions.single().actionIntent.send()
                    waitFor("sending pause") { model.state.value.sends.any { it.id == second && it.paused } && second !in model.state.value.changingSends &&
                        !model.state.value.backgroundReady && !model.state.value.connected && notifications.activeNotifications.isEmpty() }
                    status("SENDING_NOTIFICATION_PAUSE_VERIFIED")
                    // Runner releases its old held request, leaving its durable receiver interrupted.
                    scenario.moveToState(Lifecycle.State.RESUMED)
                    onMain { model.resumeSending(second) }
                    waitFor("owner allowed the same source ticket") { model.state.value.sends.any { it.id == second && !it.paused } && second !in model.state.value.changingSends }
                    onMain { model.connect() }; waitFor("resume coordinator") { model.state.value.connected }; status("RECONNECT_FOR_RESUME")
                    waitFor("resume direct session") { model.state.value.authenticated }; status("RESUME_PHONE_SEND")
                    waitFor("resumed ticket released") { model.state.value.sends.none { it.id == second } }
                    status("SENDING_SAME_TICKET_RESUMED")
                } finally {
                    onMain { model.disconnect() }
                    // These are this test peer's disposable source records; saved phone files remain untouched.
                    waitFor("sending changes drained") { model.state.value.changingSends.isEmpty() }
                    onMain { model.state.value.sends.forEach { model.removeSending(it.id) } }
                    waitFor("source records removed") { model.state.value.sends.isEmpty() && model.state.value.changingSends.isEmpty() }
                    onMain { model.state.value.phoneShares.firstOrNull { it.tree == tree }?.let { model.removePhoneShare(it.id) } }
                    waitFor("approval removed") { !model.state.value.savingShare }
                }
            }
        } finally {
            if (initialized) {
                lateinit var closing: Job
                onMain { closing = model.close() }; runBlocking { closing.join() }; onMain { app.releaseClosed(model) }
            }
            context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "dispose", fixture.getString("root"), null)
        }
    }
}
