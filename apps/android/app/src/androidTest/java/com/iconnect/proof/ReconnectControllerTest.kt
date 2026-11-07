package com.iconnect.proof

import android.app.Application
import android.content.Context
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.Job
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

/** The only network attempt is a refused loopback connection. No coordinator or external service is started. */
@RunWith(AndroidJUnit4::class)
class ReconnectControllerTest {
    @Test fun foregroundRetryPauseRestartEditingAndRevocationRespectOwnerIntent() {
        val instrumentation=InstrumentationRegistry.getInstrumentation(); val context=instrumentation.targetContext
        val prefix="reconnect-controller-"+StorageRules.token(); val root=File(context.filesDir,prefix).apply {check(mkdir())}
        val app=object:Application() {
            init {attachBaseContext(context)}
            override fun getFilesDir()=root
            override fun getCacheDir()=File(root,"cache").apply {mkdirs()}
            override fun getApplicationContext():Context=this
            override fun getSharedPreferences(name:String,mode:Int)=context.getSharedPreferences(prefix+name,mode)
        }
        lateinit var model:PhoneController; var watchStarts=0; var watchStops=0
        fun main(action:()->Unit)=instrumentation.runOnMainSync(action)
        fun waitFor(label:String,predicate:()->Boolean) {
            val deadline=System.currentTimeMillis()+8000
            while(System.currentTimeMillis()<deadline) {if(predicate()) return; Thread.sleep(20)}
            fail("$label: ${model.state.value.status}, ${model.state.value.reconnectIssue}")
        }
        TestPeer.approve(AndroidOwnerPolicy(app),TestPeer.key())
        main {
            model=PhoneController(app) {_,changed->object:NetworkWatch {
                override fun start() {watchStarts++; changed(true,7L)}
                override fun stop() {watchStops++; changed(false,null)}
            }}
            model.setServer("http://127.0.0.1:1/"); model.setStun(""); model.setUiVisible(true)
            assertFalse(model.state.value.autoReconnect); assertEquals(0L,model.automaticAttempts)
        }
        try {
            main {model.setAutoReconnect(true)}
            waitFor("saved opt in") {!model.state.value.reconnectSaving && model.state.value.autoReconnect && !model.state.value.reconnectPaused}
            waitFor("real refused WebSocket attempt") {model.automaticAttempts>0 && model.state.value.events.any {it.startsWith("Coordinator unavailable:")}}
            main {model.pauseConnections()}; waitFor("durable pause") {!model.state.value.reconnectSaving && model.state.value.reconnectPaused}
            val paused=model.automaticAttempts; Thread.sleep(2300); assertEquals(paused,model.automaticAttempts); assertFalse(model.state.value.connected)
            assertTrue(AndroidReconnect(app,LocalIdentity().id).paused)
            main {model.connect()}; waitFor("one off manual failure") {!model.state.value.connecting}; assertTrue(model.state.value.reconnectPaused); assertEquals(paused,model.automaticAttempts)
            main {model.resumeConnections()}; waitFor("resume") {!model.state.value.reconnectSaving && !model.state.value.reconnectPaused}
            waitFor("resumed automatic attempt") {model.automaticAttempts>paused}
            main {model.setUiVisible(false)}; val hidden=model.automaticAttempts; Thread.sleep(2300); assertEquals(hidden,model.automaticAttempts)
            main {model.setUiVisible(true)}; waitFor("return to foreground") {model.automaticAttempts>hidden}
            waitFor("disconnected endpoint editing") {!model.state.value.connected && !model.state.value.connecting}
            main {model.setServer("http://127.0.0.1:2/")}; waitFor("endpoint edit pause") {!model.state.value.reconnectSaving && model.state.value.reconnectPaused}
            assertFalse(model.state.value.autoReconnect)
            main {model.setAutoReconnect(true)}; waitFor("new endpoint approval") {!model.state.value.reconnectSaving && model.state.value.autoReconnect}
            main {model.forgetPeer()}; waitFor("saved revocation") {!model.state.value.savingTrust && !model.state.value.reconnectSaving && !model.state.value.revocationPending}
            val revoked=model.automaticAttempts; Thread.sleep(2200); assertEquals(revoked,model.automaticAttempts); assertFalse(model.state.value.autoReconnect); assertEquals("",model.state.value.peerName)
            assertTrue(watchStarts>=2); assertTrue(watchStops>=1)
        } finally {
            lateinit var closing:Job; main {closing=model.close()}; runBlocking {closing.join()}
            listOf("iconnect.proof","iconnect.storage","iconnect.saf-access").forEach {context.deleteSharedPreferences(prefix+it)}
            root.deleteRecursively()
        }
    }
}
