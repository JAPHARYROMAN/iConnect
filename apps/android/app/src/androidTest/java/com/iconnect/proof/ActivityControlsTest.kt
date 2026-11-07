package com.iconnect.proof

import android.app.Application
import android.content.Context
import android.content.SharedPreferences
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.util.concurrent.atomic.AtomicBoolean

@RunWith(AndroidJUnit4::class)
class ActivityControlsTest {
    internal class Fixture {
        val instrumentation=InstrumentationRegistry.getInstrumentation()
        val base=instrumentation.targetContext
        val prefix="activity-controls-"+StorageRules.token()
        val root=File(base.filesDir,prefix).apply {check(mkdir())}
        val failPreferences=AtomicBoolean()
        val application=object:Application() {
            init {attachBaseContext(base)}
            override fun getApplicationContext():Context=this
            override fun getFilesDir()=root
            override fun getCacheDir()=File(root,"cache").apply {mkdirs()}
            override fun getSharedPreferences(name:String,mode:Int):SharedPreferences {
                val original=base.getSharedPreferences(prefix+name,mode)
                if(name!="iconnect.proof") return original
                return object:SharedPreferences by original {
                    override fun edit():SharedPreferences.Editor {
                        val editor=original.edit()
                        return object:SharedPreferences.Editor by editor {
                            override fun putString(key:String?,value:String?):SharedPreferences.Editor {editor.putString(key,value); return this}
                            override fun putBoolean(key:String?,value:Boolean):SharedPreferences.Editor {editor.putBoolean(key,value); return this}
                            override fun commit()=if(failPreferences.get()) false else editor.commit()
                        }
                    }
                }
            }
        }
        lateinit var model:PhoneController
        private var opened=false
        fun main(action:()->Unit)=instrumentation.runOnMainSync(action)
        fun open(sockets: okhttp3.WebSocket.Factory? = null) {
            main {
                val network = { _: Context, _: (Boolean, Long?) -> Unit ->
                    object : NetworkWatch {
                        override fun start() {}
                        override fun stop() {}
                    }
                }
                model = if (sockets == null) PhoneController(application, networkFactory = network)
                    else PhoneController(application, sockets, network)
                opened = true
                model.setUiVisible(true)
            }
        }
        suspend fun closeModel() {if(opened) {lateinit var job:Job; main {job=model.close()}; job.join(); opened=false}}
        suspend fun dispose() {
            closeModel(); listOf("iconnect.proof","iconnect.storage","iconnect.saf-access").forEach {base.deleteSharedPreferences(prefix+it)}
            check(root.canonicalFile.parentFile==base.filesDir.canonicalFile && root.name==prefix); check(root.deleteRecursively())
        }
    }
    private suspend fun waitFor(label:String,condition:()->Boolean) {try {withTimeout(30_000) {while(!condition()) delay(20)}} catch(e:TimeoutCancellationException) {throw AssertionError("Timed out: $label",e)} }
    private suspend fun fixture(work:suspend(Fixture)->Unit) {val f=Fixture(); try {f.open(); waitFor("history ready") {f.model.state.value.activity.ready}; work(f)} finally {f.dispose()}}

    @Test fun actualSettingsAndRecoveryRecordCommittedFailedAndReviewOutcomesWithoutPrivateMetadata()=runBlocking {fixture {f->
        f.main {f.model.setSharePresence(true)}; waitFor("saved presence") {f.model.state.value.sharePresence && !f.model.state.value.presenceSaving}
        f.failPreferences.set(true); f.main {f.model.setSharePresence(false)}
        waitFor("failed preference") {f.model.state.value.presenceIssue.isNotEmpty() && !f.model.state.value.presenceSaving}; assertTrue(f.model.state.value.sharePresence)
        f.failPreferences.set(false)
        val blocker=File(f.root,"downloads/recovery/NEVER-LOG-RECOVERY-NAME.txt").apply {check(parentFile!!.mkdirs() || parentFile!!.isDirectory); writeText("NEVER-LOG-OWNER-BYTES")}
        f.main {f.model.clearPreservedProgress()}
        waitFor("recovery review") {f.model.state.value.activity.entries.any {it.kind==PhoneActivityKind.PROGRESS_BACKUP_REMOVAL && it.outcome==PhoneActivityOutcome.REVIEW}}
        assertEquals("NEVER-LOG-OWNER-BYTES",blocker.readText()); check(blocker.delete())
        f.main {f.model.clearPreservedProgress()}
        waitFor("recovery completed") {f.model.state.value.activity.entries.any {it.kind==PhoneActivityKind.PROGRESS_BACKUP_REMOVAL && it.outcome==PhoneActivityOutcome.COMPLETED}}
        f.model.flushActivity(); val rows=f.model.state.value.activity.entries
        assertTrue(rows.any {it.kind==PhoneActivityKind.PRESENCE_SETTINGS && it.outcome==PhoneActivityOutcome.COMPLETED})
        assertTrue(rows.any {it.kind==PhoneActivityKind.PRESENCE_SETTINGS && it.outcome==PhoneActivityOutcome.FAILED})
        val json=File(f.root,"local-activity/activity.json").readText(); assertFalse(json.contains("NEVER-LOG")); assertFalse(json.contains(f.root.path)); assertFalse(json.contains("unexpected item"))
        assertTrue(f.model.state.value.phoneShares.isEmpty()); assertFalse(f.model.state.value.connected); assertFalse(f.model.state.value.authenticated)
        f.closeModel(); f.open(); waitFor("reopened history") {f.model.state.value.activity.ready}
        assertTrue(f.model.state.value.activity.entries.any {it.kind==PhoneActivityKind.PROGRESS_BACKUP_REMOVAL && it.outcome==PhoneActivityOutcome.REVIEW}); assertTrue(f.model.state.value.sharePresence)
    }}
    @Test fun recordingOffKeepsActualSettingsAndRecoveryWorkingAndPersistsAcrossControllerReopen()=runBlocking {fixture {f->
        f.model.configureActivity(0); f.main {f.model.setSharePresence(true)}
        waitFor("saved while history off") {f.model.state.value.sharePresence && !f.model.state.value.presenceSaving}
        f.main {f.model.clearPreservedProgress()}; waitFor("cleanup while off") {f.model.state.value.status.startsWith("Preserved metadata backups removed")}
        f.model.flushActivity(); assertTrue(f.model.state.value.activity.entries.isEmpty()); assertEquals(0,f.model.state.value.activity.days)
        f.closeModel(); f.open(); waitFor("off after reopen") {f.model.state.value.activity.ready}
        assertEquals(0,f.model.state.value.activity.days); assertTrue(f.model.state.value.activity.entries.isEmpty()); assertTrue(f.model.state.value.sharePresence)
        assertTrue(f.model.state.value.phoneShares.isEmpty()); assertFalse(f.model.state.value.connected)
    }}
}
