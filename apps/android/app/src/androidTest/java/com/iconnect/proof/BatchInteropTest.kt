package com.iconnect.proof

import android.app.NotificationManager
import android.net.Uri
import android.os.Bundle
import android.provider.DocumentsContract as Docs
import androidx.lifecycle.Lifecycle
import androidx.test.core.app.ActivityScenario
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.Job
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.joinAll
import org.junit.Assert.*
import org.junit.Assume.assumeTrue
import org.junit.Test
import org.junit.runner.RunWith

/** Actual native Windows source, Android activity, private staging and nested SAF publication. */
@RunWith(AndroidJUnit4::class)
class BatchInteropTest {
    @Test fun folderBatchPausesWhenTheUiStopsAndResumesNestedProviderPublication() {
        val instrument = InstrumentationRegistry.getInstrumentation()
        val invitation = InstrumentationRegistry.getArguments().getString("invitation")
        assumeTrue("Supply a fresh native runner invitation.", !invitation.isNullOrEmpty())
        val context = instrument.targetContext; val app = context.applicationContext as IConnectApplication
        val fixture = requireNotNull(context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "fixture", null, null)); val tree = Uri.parse(fixture.getString("tree"))
        lateinit var model: PhoneController
        var initialized = false
        fun onMain(action: () -> Unit) = instrument.runOnMainSync(action)
        fun status(text: String) = instrument.sendStatus(0, Bundle().apply { putString("iconnect", text) })
        fun waitFor(label: String, check: () -> Boolean) {
            val deadline = System.currentTimeMillis() + 60_000
            while (System.currentTimeMillis() < deadline) { if (check()) return; Thread.sleep(20) }
            fail("Timed out: $label. ${model.state.value.status}. ${model.state.value.batches.map { "${it.phase}/${it.active}: ${it.error}" }}")
        }
        try {
            ActivityScenario.launch(MainActivity::class.java).use { scenario ->
                onMain {
                    model = app.controller(); initialized = true; model.forgetPeer(); model.setServer("http://10.0.2.2:5078")
                    model.chooseDownloadDestination(tree, fixture.getInt("flags")); model.setInvitation(requireNotNull(invitation))
                }
                waitFor("durable revocation and destination") { !model.state.value.savingTrust && !model.state.value.revocationPending && model.state.value.downloadDestination == tree.toString() }
                onMain { model.connect() }; waitFor("coordinator") { model.state.value.connected }
                onMain { model.requestPairing() }; waitFor("code") { model.state.value.code.isNotEmpty() }; status("CODE ${model.state.value.code}")
                onMain { model.approve(model.state.value.code) }; waitFor("pairing") { model.state.value.peerName.isNotEmpty() }; status("PAIRED")
                waitFor("native authenticated channel") { model.state.value.authenticated }
                onMain { model.browseRoots() }; waitFor("Windows roots") { model.state.value.remoteEntries.any { it.name == "Interop documents" } }
                onMain { model.browseFolder(model.state.value.remoteEntries.single { it.name == "Interop documents" }) }
                waitFor("Windows source selection") { model.state.value.remoteEntries.any { it.name == "Folder fixture" } }
                onMain { model.planBatch(model.state.value.remoteEntries.filter { it.name in setOf("Folder fixture", "ripoti.txt") }) }
                waitFor("reviewable plan") { model.state.value.batchPlan != null }
                assertEquals(6, model.state.value.batchPlan!!.manifest.items.size)
                onMain { model.confirmBatch(PhoneCollision.KEEP_BOTH) }
                waitFor("gated source read") { model.state.value.downloads.any { it.batchId.isNotEmpty() && it.name == "background.bin" && it.active } }
                val batch = model.state.value.batches.single(); val held = model.state.value.downloads.single { it.batchId == batch.id && it.name == "background.bin" }
                onMain { model.allowBackground(held.id) }
                assertFalse(held.id in model.state.value.backgroundDownloads); assertFalse(model.state.value.backgroundReady)
                scenario.moveToState(Lifecycle.State.CREATED)
                waitFor("batch foreground pause") { model.state.value.batches.single().phase == PhoneBatchPhase.PAUSED && !model.state.value.batches.single().active && !model.state.value.connected }
                assertTrue(model.state.value.downloads.none { it.batchId == batch.id && it.active })
                assertTrue(context.getSystemService(NotificationManager::class.java).activeNotifications.isEmpty())
                status("BATCH_FOREGROUND_PAUSE_VERIFIED")
                scenario.moveToState(Lifecycle.State.RESUMED); onMain { model.connect() }; waitFor("coordinator resume") { model.state.value.connected }
                status("RECONNECT_FOR_BATCH")
                waitFor("new direct session") { model.state.value.authenticated }
                onMain { model.resumeBatch(batch.id) }
                waitFor("completed provider batch") { model.state.value.batches.single().phase == PhoneBatchPhase.COMPLETED && !model.state.value.batches.single().active }
                val result = model.state.value.batches.single()
                assertEquals(6, result.completed); assertEquals(0, result.issues)
                assertTrue(model.state.value.downloads.none { it.batchId == batch.id })
                val large = result.items.single { it.source.name == "background.bin" }
                val bytes = context.contentResolver.openInputStream(Uri.parse(large.published))!!.use { it.readBytes() }
                val expected = ByteArray(2 * 1024 * 1024 + 17) { (it * 31 % 251).toByte() }; assertArrayEquals(expected, bytes)
                assertEquals("2ce3172ab5811f22d8e2d9d45d2c71397e38a636b4a5fd5a09517b7454fb2368", large.hash)
                val notes = result.items.single { it.source.name == "notes.txt" }
                assertEquals("iConnect nested batch document.\nSalama!\n", context.contentResolver.openInputStream(Uri.parse(notes.published))!!.bufferedReader().use { it.readText() })
                val empty = result.items.single { it.source.name == "Empty" }
                context.contentResolver.query(Docs.buildChildDocumentsUriUsingTree(tree, empty.document), arrayOf(Docs.Document.COLUMN_DOCUMENT_ID), null, null, null)!!.use { assertFalse(it.moveToFirst()) }
                status("REAL_ANDROID_FOLDER_BATCH_VERIFIED ${result.completed} items ${result.verifiedBytes} bytes")
                onMain { model.removeBatch(batch.id) }; waitFor("batch record removal") { model.state.value.batches.none { it.id == batch.id } }
                onMain { model.clearDownloadDestination(); model.disconnect() }
                waitFor("destination selection cleared") { model.state.value.downloadDestination.isEmpty() }
            }
        } finally {
            if (initialized) {
                var cleanup = emptyList<Job>()
                onMain { cleanup = model.state.value.batches.filter { it.tree == tree.toString() }.map { model.cancelBatch(it.id) } }; runBlocking { cleanup.joinAll() }
                onMain { cleanup = model.state.value.batches.filter { it.tree == tree.toString() && model.canRemoveBatch(it.id) }.map { model.removeBatch(it.id) } }; runBlocking { cleanup.joinAll() }
                lateinit var closing: Job
                onMain { closing = model.close() }; runBlocking { closing.join() }; onMain { app.releaseClosed(model) }
            }
            context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "dispose", fixture.getString("root"), null)
        }
    }
}
