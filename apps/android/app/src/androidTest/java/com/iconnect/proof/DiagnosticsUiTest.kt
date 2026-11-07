package com.iconnect.proof

import android.app.Application
import android.content.Context
import android.net.Uri
import android.provider.DocumentsContract as Docs
import android.view.accessibility.AccessibilityNodeInfo
import android.view.accessibility.AccessibilityEvent
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.*
import androidx.compose.material3.MaterialTheme
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
import java.util.concurrent.TimeoutException

@RunWith(AndroidJUnit4::class)
class DiagnosticsUiTest {
    private val instrumentation get()=InstrumentationRegistry.getInstrumentation()
    private fun nodes(root:AccessibilityNodeInfo?):List<AccessibilityNodeInfo> =if(root==null) emptyList() else listOf(root)+(0 until root.childCount).flatMap {nodes(root.getChild(it))}
    private fun waitFor(label:String,condition:()->Boolean) {
        val end=System.currentTimeMillis()+30_000
        while(System.currentTimeMillis()<end) {if(condition()) return; Thread.sleep(25)}
        error("Timed out: $label")
    }
    private fun capture(label:String) {
        runCatching {
            val known=setOf("iConnect","Diagnostic report","Review diagnostic report…","Choose report location…","Keep report private","Report creation canceled.","Save")
            File(instrumentation.targetContext.cacheDir,"diagnostics-$label.txt").writeText(nodes(instrumentation.uiAutomation.rootInActiveWindow).joinToString("\n") {item->
                val bounds=android.graphics.Rect(); item.getBoundsInScreen(bounds); val text=item.text?.toString().orEmpty()
                "${item.packageName} ${item.className} ${if(text in known) text else "<${text.length} chars>"} visible=${item.isVisibleToUser} enabled=${item.isEnabled} bounds=$bounds"
            })
            instrumentation.uiAutomation.takeScreenshot()?.let {bitmap->File(instrumentation.targetContext.cacheDir,"diagnostics-$label.png").outputStream().use {bitmap.compress(android.graphics.Bitmap.CompressFormat.PNG,100,it)}; bitmap.recycle()}
        }
    }
    private fun click(text:String) {
        val end=System.currentTimeMillis()+15_000
        while(System.currentTimeMillis()<end) {
            var node=nodes(instrumentation.uiAutomation.rootInActiveWindow).firstOrNull {it.text?.toString()?.equals(text,ignoreCase=true)==true && it.isVisibleToUser}
            while(node!=null && !node.isClickable) node=node.parent
            if(node?.isEnabled==true && node.performAction(AccessibilityNodeInfo.ACTION_CLICK)) {instrumentation.waitForIdleSync(); return}
            Thread.sleep(60)
        }
        capture("missing-control"); error("Native diagnostic control unavailable: $text")
    }
    @Test fun nativeDiagnosticReviewCanCancelThenSaveTheReviewedSnapshotWhilePickerBackgroundingEndsAvailability():Unit=runBlocking {
        val base=instrumentation.targetContext; val prefix="diagnostics-ui-"+StorageRules.token(); val root=File(base.filesDir,prefix).apply {check(mkdir())}
        val app=object:Application() {
            init {attachBaseContext(base)}
            override fun getFilesDir()=root
            override fun getCacheDir()=File(root,"cache").apply {mkdirs()}
            override fun getApplicationContext():Context=this
            override fun getSharedPreferences(name:String,mode:Int)=base.getSharedPreferences(prefix+name,mode)
        }
        val data=requireNotNull(base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"fixture",null,null)); val tree=Uri.parse(data.getString("tree"))
        val original=Docs.buildDocumentUriUsingTree(tree,data.getString("file")); val before=base.contentResolver.openInputStream(original)!!.use {it.readBytes()}
        val parent=Docs.buildDocumentUriUsingTree(tree,Docs.getTreeDocumentId(tree)); val target=requireNotNull(Docs.createDocument(base.contentResolver,parent,"application/json","generated-ui-report.json"))
        var model:PhoneController?=null; var defaultModel:PhoneController?=null; var saved:PhoneDiagnosticReview?=null
        val finished=CompletableDeferred<Unit>(); val saves=CoroutineScope(SupervisorJob()+Dispatchers.Main.immediate)
        fun main(action:()->Unit)=instrumentation.runOnMainSync(action)
        suspend fun close(value:PhoneController) {lateinit var work:Job; main {work=value.close()}; work.join()}
        try {
            lateinit var controller:PhoneController
            main {controller=PhoneController(app) {_,_->object:NetworkWatch {override fun start() {}; override fun stop() {}}}; controller.setUiVisible(true)}
            model=controller
            ActivityScenario.launch(MainActivity::class.java).use {scenario->
                scenario.onActivity {activity->defaultModel=(activity.application as IConnectApplication).controller(); activity.setContent {MaterialTheme {
                    Column(Modifier.fillMaxSize().padding(16.dp)) {DiagnosticsPanel(controller,onChooseLocation={review->
                        saved=review; controller.setUiVisible(false)
                        saves.launch {try {controller.exportDiagnostics(review,target); finished.complete(Unit)} catch(e:Throwable) {finished.completeExceptionally(e)}}
                    })}
                }}}
                click("Review diagnostic report…"); waitFor("exact JSON preview") {nodes(instrumentation.uiAutomation.rootInActiveWindow).any {it.text?.toString()?.contains("bestEffortSnapshot")==true}}
                click("Keep report private"); assertNull(saved); assertEquals(0,base.contentResolver.openInputStream(target)!!.use {it.readBytes()}.size)
                click("Review diagnostic report…"); click("Choose report location…"); withTimeout(20_000) {finished.await()}
                val review=requireNotNull(saved); assertArrayEquals(review.json.toByteArray(Charsets.UTF_8),base.contentResolver.openInputStream(target)!!.use {it.readBytes()})
                assertArrayEquals(before,base.contentResolver.openInputStream(original)!!.use {it.readBytes()}); assertFalse(review.json.contains(controller.state.value.deviceId))
                assertFalse(controller.state.value.authenticated); assertFalse(controller.state.value.connected); assertTrue(controller.state.value.phoneShares.isEmpty())
            }
        } finally {
            saves.cancel(); model?.let {close(it)}; defaultModel?.let {close(it); main {(base.applicationContext as IConnectApplication).releaseClosed(it)}}
            base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"dispose",data.getString("root"),null)
            listOf("iconnect.proof","iconnect.storage","iconnect.saf-access").forEach {base.deleteSharedPreferences(prefix+it)}
            check(root.canonicalFile.parentFile==base.filesDir.canonicalFile && root.name==prefix); root.deleteRecursively()
        }
    }
    @Test fun actualSystemCreateDocumentPickerCanCancelAndThenSaveTheExactReviewedReport():Unit=runBlocking {
        val base=instrumentation.targetContext; val prefix="diagnostics-picker-"+StorageRules.token(); val root=File(base.filesDir,prefix).apply {check(mkdir())}
        val app=object:Application() {
            init {attachBaseContext(base)}
            override fun getFilesDir()=root
            override fun getCacheDir()=File(root,"cache").apply {mkdirs()}
            override fun getApplicationContext():Context=this
            override fun getSharedPreferences(name:String,mode:Int)=base.getSharedPreferences(prefix+name,mode)
        }
        var model:PhoneController?=null; var defaultModel:PhoneController?=null; var exported:Uri?=null; var saved:PhoneDiagnosticReview?=null
        val finished=CompletableDeferred<Unit>()
        fun main(action:()->Unit)=instrumentation.runOnMainSync(action)
        suspend fun close(value:PhoneController) {lateinit var work:Job; main {work=value.close()}; work.join()}
        fun pickerVisible()=instrumentation.uiAutomation.rootInActiveWindow?.packageName?.toString()?.contains("documentsui")==true
        fun cancelPicker() {
            repeat(3) {
                if(!pickerVisible()) return
                try {
                    instrumentation.uiAutomation.executeAndWaitForEvent(
                        {instrumentation.sendKeyDownUpSync(android.view.KeyEvent.KEYCODE_BACK)},
                        {event->event.eventType==AccessibilityEvent.TYPE_WINDOW_STATE_CHANGED && event.packageName?.toString()==base.packageName},3000)
                    instrumentation.waitForIdleSync(); return
                } catch(_:TimeoutException) { /* A first Back may only dismiss the picker's keyboard. */ }
            }
            check(!pickerVisible()) {"The report picker did not return to iConnect."}
        }
        try {
            lateinit var controller:PhoneController
            main {controller=PhoneController(app) {_,_->object:NetworkWatch {override fun start() {}; override fun stop() {}}}; controller.setUiVisible(true)}
            model=controller
            ActivityScenario.launch(MainActivity::class.java).use {scenario->
                try {scenario.onActivity {activity->
                    val ordinary=(activity.application as IConnectApplication).controller(); defaultModel=ordinary; ordinary.setUiVisible(false)
                    MainActivity::class.java.getDeclaredField("model").apply {isAccessible=true}.set(activity,controller)
                    activity.setContent {MaterialTheme {Column(Modifier.fillMaxSize().padding(16.dp)) {
                        DiagnosticsPanel(controller,onSaved={review,uri->saved=review; exported=uri; finished.complete(Unit)})
                    }}}
                }
                click("Review diagnostic report…"); click("Choose report location…"); waitFor("system report chooser") {pickerVisible()}
                cancelPicker()
                waitFor("return after canceled chooser") {instrumentation.uiAutomation.rootInActiveWindow?.packageName?.toString()==base.packageName &&
                    nodes(instrumentation.uiAutomation.rootInActiveWindow).any {it.text?.toString()=="Report creation canceled."}}
                assertNull(exported); capture("after-cancel")
                click("Review diagnostic report…"); click("Choose report location…"); waitFor("system report chooser again") {pickerVisible()}
                click("Save"); withTimeout(30_000) {finished.await()}
                val uri=requireNotNull(exported); val report=requireNotNull(saved)
                assertTrue(Docs.isDocumentUri(base,uri)); assertArrayEquals(report.json.toByteArray(Charsets.UTF_8),base.contentResolver.openInputStream(uri)!!.use {it.readBytes()})
                assertFalse(report.json.contains(controller.state.value.deviceId)); assertTrue(controller.state.value.phoneShares.isEmpty()); assertFalse(controller.state.value.connected); assertFalse(controller.state.value.authenticated)
                } finally {
                    // The create-document grant belongs to this still-live Activity.
                    // Delete only this generated, verified report before it closes.
                    exported?.let {uri->check(Docs.isDocumentUri(base,uri)); check(Docs.deleteDocument(base.contentResolver,uri)); exported=null}
                }
            }
        } finally {
            model?.let {close(it)}; defaultModel?.let {close(it); main {(base.applicationContext as IConnectApplication).releaseClosed(it)}}
            listOf("iconnect.proof","iconnect.storage","iconnect.saf-access").forEach {base.deleteSharedPreferences(prefix+it)}
            check(root.canonicalFile.parentFile==base.filesDir.canonicalFile && root.name==prefix); root.deleteRecursively()
        }
    }
}
