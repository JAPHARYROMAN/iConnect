package com.iconnect.proof

import android.app.Application
import android.content.Context
import android.net.Uri
import android.provider.DocumentsContract as Docs
import android.view.accessibility.AccessibilityNodeInfo
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.test.core.app.ActivityScenario
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

@RunWith(AndroidJUnit4::class)
class IncomingPartRecoveryUiTest {
    private val instrumentation get()=InstrumentationRegistry.getInstrumentation()
    private fun nodes(root:AccessibilityNodeInfo?):List<AccessibilityNodeInfo> =if(root==null) emptyList() else listOf(root)+(0 until root.childCount).flatMap {nodes(root.getChild(it))}
    private fun waitFor(label:String,condition:()->Boolean) {
        val end=System.currentTimeMillis()+30_000
        while(System.currentTimeMillis()<end) {if(condition()) return; Thread.sleep(25)}
        error("Timed out: $label\n"+nodes(instrumentation.uiAutomation.rootInActiveWindow).mapNotNull {it.text?.toString()}.take(35).joinToString(" | "))
    }
    private fun click(text:String) {
        val end=System.currentTimeMillis()+15_000; var direction=AccessibilityNodeInfo.ACTION_SCROLL_FORWARD
        while(System.currentTimeMillis()<end) {
            val all=nodes(instrumentation.uiAutomation.rootInActiveWindow); var node=all.firstOrNull {it.text?.toString()==text && it.isVisibleToUser}
            while(node!=null && !node.isClickable) node=node.parent
            if(node?.isEnabled==true && node.performAction(AccessibilityNodeInfo.ACTION_CLICK)) {instrumentation.waitForIdleSync(); return}
            if(all.firstOrNull {it.isScrollable}?.performAction(direction)!=true) direction=if(direction==AccessibilityNodeInfo.ACTION_SCROLL_FORWARD) AccessibilityNodeInfo.ACTION_SCROLL_BACKWARD else AccessibilityNodeInfo.ACTION_SCROLL_FORWARD
            Thread.sleep(80)
        }
        error("Native recovery control unavailable: $text\n"+nodes(instrumentation.uiAutomation.rootInActiveWindow).mapNotNull {it.text?.toString()}.take(35).joinToString(" | "))
    }
    @Test fun nativeIncomingPartRecoveryCanInspectCancelAndRemoveOnlyReviewedFilesWithoutSharing():Unit=runBlocking {
        val base=instrumentation.targetContext; val prefix="incoming-part-ui-"+StorageRules.token(); val root=File(base.filesDir,prefix).apply {check(mkdir())}
        val app=object:Application() {
            init {attachBaseContext(base)}
            override fun getFilesDir()=root
            override fun getCacheDir()=File(root,"cache").apply {mkdirs()}
            override fun getApplicationContext():Context=this
            override fun getSharedPreferences(name:String,mode:Int)=base.getSharedPreferences(prefix+name,mode)
        }
        val data=requireNotNull(base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"fixture",null,null)); val tree=Uri.parse(data.getString("tree"))
        val original=Docs.buildDocumentUriUsingTree(tree,data.getString("file")); val originalBytes=base.contentResolver.openInputStream(original)!!.use {it.readBytes()}
        val keep=File(root,"generated-owner-file.txt").apply {writeText("keep current owner file")}
        var model:PhoneController?=null; var defaultModel:PhoneController?=null
        fun main(action:()->Unit)=instrumentation.runOnMainSync(action)
        fun create():PhoneController {lateinit var result:PhoneController; main {result=PhoneController(app) {_,_->object:NetworkWatch {override fun start() {}; override fun stop() {}}}; result.setUiVisible(true)}; return result}
        suspend fun close(value:PhoneController) {lateinit var closing:Job; main {closing=value.close()}; closing.join()}
        try {
            val before=create(); close(before)
            val parts=File(root,"incoming-uploads").apply {check(isDirectory)}
            val first=File(parts,StorageRules.token()+".part").apply {writeBytes(byteArrayOf(11,17,23))}
            val second=File(parts,StorageRules.token()+".part").apply {writeBytes(byteArrayOf(31,37,41))}
            val controller=create(); model=controller; waitFor("unreferenced private parts") {controller.state.value.orphanIncoming==2}
            ActivityScenario.launch(MainActivity::class.java).use {scenario->
                scenario.onActivity {activity->defaultModel=(activity.application as IConnectApplication).controller(); activity.setContent {MaterialTheme {
                    val state by controller.state.collectAsState()
                    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp)) {IncomingPartRecoveryPanel(controller,state)}
                }}}
                click("Inspect unreferenced incoming parts…")
                waitFor("private-part inventory") {nodes(instrumentation.uiAutomation.rootInActiveWindow).any {it.text?.toString()=="${first.name} · 3 bytes"}}
                click("Done"); click("Review unreferenced incoming-part removal…"); click("Keep private parts")
                assertArrayEquals(byteArrayOf(11,17,23),first.readBytes()); assertArrayEquals(byteArrayOf(31,37,41),second.readBytes())
                click("Review unreferenced incoming-part removal…"); click("Remove reviewed incoming parts")
                waitFor("only reviewed parts removed") {controller.state.value.orphanIncoming==0 && controller.state.value.incomingPartRemoval==null}
                assertFalse(first.exists()); assertFalse(second.exists()); assertTrue(File(parts,"owner.lock").isFile)
                assertEquals("keep current owner file",keep.readText()); assertArrayEquals(originalBytes,base.contentResolver.openInputStream(original)!!.use {it.readBytes()})
                assertTrue(controller.state.value.incomingBatchPlans.isEmpty()); assertTrue(controller.state.value.phoneShares.isEmpty()); assertFalse(controller.state.value.authenticated)
            }
        } finally {
            model?.let {close(it)}
            defaultModel?.let {close(it); main {(base.applicationContext as IConnectApplication).releaseClosed(it)}}
            base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"dispose",data.getString("root"),null)
            listOf("iconnect.proof","iconnect.storage","iconnect.saf-access").forEach {base.deleteSharedPreferences(prefix+it)}
            check(root.canonicalFile.parentFile==base.filesDir.canonicalFile && root.name==prefix); root.deleteRecursively()
        }
    }
}
