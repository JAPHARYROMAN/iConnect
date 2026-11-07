package com.iconnect.proof

import android.app.Application
import android.content.Context
import android.content.SharedPreferences
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.Job
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

/** Private settings/controller test. Does not open a coordinator or any external network connection. */
@RunWith(AndroidJUnit4::class)
class PresencePolicyTest {
    @Test fun consentDefaultsOffSurvivesRestartAndFailedWritesCannotPretendToSave() {
        val instrumentation=InstrumentationRegistry.getInstrumentation(); val context=instrumentation.targetContext
        val prefix="presence-policy-"+StorageRules.token(); val root=File(context.filesDir,prefix).apply {check(mkdir())}
        val failCommit=java.util.concurrent.atomic.AtomicBoolean(false)
        val app=object:Application() {
            init {attachBaseContext(context)}
            override fun getFilesDir()=root
            override fun getCacheDir()=File(root,"cache").apply {mkdirs()}
            override fun getApplicationContext():Context=this
            override fun getSharedPreferences(name:String,mode:Int):SharedPreferences {
                val original=context.getSharedPreferences(prefix+name,mode)
                if(name!="iconnect.proof") return original
                return object:SharedPreferences by original {
                    override fun edit():SharedPreferences.Editor {
                        val editor=original.edit()
                        return object:SharedPreferences.Editor by editor {
                            override fun putString(key:String?,value:String?):SharedPreferences.Editor {editor.putString(key,value); return this}
                            override fun putBoolean(key:String?,value:Boolean):SharedPreferences.Editor {editor.putBoolean(key,value); return this}
                            override fun commit()=if(failCommit.get()) false else editor.commit()
                        }
                    }
                }
            }
        }
        lateinit var model:PhoneController
        fun main(action:()->Unit)=instrumentation.runOnMainSync(action)
        fun open()=main {model=PhoneController(app); model.setUiVisible(true)}
        fun close() {lateinit var job:Job; main {job=model.close()}; runBlocking {job.join()}}
        fun saved() {
            val deadline=android.os.SystemClock.elapsedRealtime()+5000
            while(model.state.value.presenceSaving && android.os.SystemClock.elapsedRealtime()<deadline) Thread.sleep(10)
            assertFalse(model.state.value.presenceSaving)
        }
        open()
        try {
            assertFalse(model.state.value.sharePresence); assertFalse(model.state.value.peerAvailable)
            failCommit.set(true); main {model.setSharePresence(true)}; saved()
            assertFalse(model.state.value.sharePresence); assertTrue(model.state.value.presenceIssue.isNotEmpty())
            failCommit.set(false); main {model.setSharePresence(true)}; saved(); assertTrue(model.state.value.sharePresence)
            main {model.setUiVisible(false)}; assertFalse(model.state.value.peerAvailable); assertFalse(model.state.value.connected)
            close(); open(); assertTrue(model.state.value.sharePresence); assertFalse(model.state.value.peerAvailable)
            failCommit.set(true); main {model.setSharePresence(false)}; saved(); assertTrue(model.state.value.sharePresence)
            assertTrue(model.state.value.presenceIssue.isNotEmpty())
            failCommit.set(false); main {model.setSharePresence(false)}; saved(); assertFalse(model.state.value.sharePresence)
            close(); open(); assertFalse(model.state.value.sharePresence)
            close(); check(context.getSharedPreferences(prefix+"iconnect.proof",0).edit().putString("presence.owner","0".repeat(64)).putBoolean("presence.enabled",true).commit())
            open(); assertFalse(model.state.value.sharePresence); assertFalse(model.state.value.peerAvailable)
            close(); check(context.getSharedPreferences(prefix+"iconnect.proof",0).edit().putString("presence.owner",LocalIdentity().id).putString("presence.enabled","damaged").commit())
            open(); assertFalse(model.state.value.sharePresence); assertTrue(model.state.value.presenceIssue.isNotEmpty())
            main {model.setSharePresence(true)}; saved(); assertTrue(model.state.value.sharePresence); assertEquals("",model.state.value.presenceIssue)
        } finally {
            failCommit.set(false); close()
            listOf("iconnect.proof","iconnect.storage","iconnect.saf-access").forEach {context.deleteSharedPreferences(prefix+it)}
            root.deleteRecursively()
        }
    }
}
