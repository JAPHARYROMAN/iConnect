package com.iconnect.proof

import android.app.Application
import android.content.Context
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
class ActivityUiTest {
    private val instrumentation get()=InstrumentationRegistry.getInstrumentation()
    private fun nodes(root:AccessibilityNodeInfo?):List<AccessibilityNodeInfo> =if(root==null) emptyList() else listOf(root)+(0 until root.childCount).flatMap {nodes(root.getChild(it))}
    private fun waitFor(label:String,condition:()->Boolean) {val end=System.currentTimeMillis()+30_000; while(System.currentTimeMillis()<end) {if(condition()) return; Thread.sleep(25)}; error("Timed out: $label")}
    private fun click(text:String) {
        val end=System.currentTimeMillis()+15_000; var direction=AccessibilityNodeInfo.ACTION_SCROLL_FORWARD
        while(System.currentTimeMillis()<end) {
            val all=nodes(instrumentation.uiAutomation.rootInActiveWindow); var node=all.firstOrNull {it.text?.toString()==text && it.isVisibleToUser}
            while(node!=null && !node.isClickable) node=node.parent
            if(node?.isEnabled==true && node.performAction(AccessibilityNodeInfo.ACTION_CLICK)) {instrumentation.waitForIdleSync(); return}
            if(all.firstOrNull {it.isScrollable}?.performAction(direction)!=true) direction=if(direction==AccessibilityNodeInfo.ACTION_SCROLL_FORWARD) AccessibilityNodeInfo.ACTION_SCROLL_BACKWARD else AccessibilityNodeInfo.ACTION_SCROLL_FORWARD
            Thread.sleep(70)
        }; error("Activity control unavailable: $text")
    }
    @Test fun nativeHistoryCanCancelChangeRetentionReopenOffAndPreserveUnreadableMetadataWithoutSharing():Unit=runBlocking {
        val base=instrumentation.targetContext; val prefix="activity-ui-"+StorageRules.token(); val root=File(base.filesDir,prefix).apply {check(mkdir())}
        val app=object:Application() {
            init {attachBaseContext(base)}
            override fun getFilesDir()=root
            override fun getCacheDir()=File(root,"cache").apply {mkdirs()}
            override fun getApplicationContext():Context=this
            override fun getSharedPreferences(name:String,mode:Int)=base.getSharedPreferences(prefix+name,mode)
        }
        PhoneActivityCore(app).use {core->core.append(PhoneActivityEvent(PhoneActivityKind.SHARING,PhoneActivityOutcome.COMPLETED)); core.flush()}
        val keep=File(root,"generated-owner.txt").apply {writeText("generated original bytes")}
        var model:PhoneController?=null; var defaultModel:PhoneController?=null
        fun main(action:()->Unit)=instrumentation.runOnMainSync(action)
        fun create():PhoneController {lateinit var value:PhoneController; main {value=PhoneController(app) {_,_->object:NetworkWatch {override fun start() {}; override fun stop() {}}}; value.setUiVisible(true)}; return value}
        suspend fun close(value:PhoneController) {lateinit var work:Job; main {work=value.close()}; work.join()}
        try {
            var controller=create(); model=controller; waitFor("initial activity") {controller.state.value.activity.ready && controller.state.value.activity.entries.size==1}
            ActivityScenario.launch(MainActivity::class.java).use {scenario->
                fun show() {val current=controller; scenario.onActivity {activity->defaultModel=(activity.application as IConnectApplication).controller(); activity.setContent {MaterialTheme {
                    val state by current.state.collectAsState(); Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp)) {ActivityPanel(current,state)}
                }}}}
                show(); click("Clear local activity…"); click("Keep current history"); assertEquals(1,controller.state.value.activity.entries.size)
                click("Clear local activity…"); click("Apply activity change"); waitFor("cleared history") {controller.state.value.activity.entries.isEmpty()}
                click("Off"); click("Apply activity change"); waitFor("recording off") {controller.state.value.activity.days==0}
                close(controller); controller=create(); model=controller; waitFor("off after reopen") {controller.state.value.activity.ready && controller.state.value.activity.days==0}; show()
                close(controller); val journal=File(root,"local-activity/activity.json"); journal.writeText("generated unreadable local history")
                controller=create(); model=controller; waitFor("history recovery") {controller.state.value.activity.issue.isNotEmpty() && controller.state.value.activity.canReset}; show()
                click("Preserve and reset activity…"); click("Keep current history"); assertEquals("generated unreadable local history",journal.readText())
                click("Preserve and reset activity…"); click("Preserve and reset"); waitFor("reset history") {controller.state.value.activity.issue.isEmpty() && controller.state.value.activity.days==30}
                assertTrue(File(root,"local-activity/recovery").walkTopDown().filter {it.isFile}.any {it.readText()=="generated unreadable local history"})
                assertEquals("generated original bytes",keep.readText()); assertTrue(controller.state.value.phoneShares.isEmpty()); assertFalse(controller.state.value.authenticated); assertFalse(controller.state.value.connected)
            }
        } finally {
            model?.let {close(it)}; defaultModel?.let {close(it); main {(base.applicationContext as IConnectApplication).releaseClosed(it)}}
            listOf("iconnect.proof","iconnect.storage","iconnect.saf-access").forEach {base.deleteSharedPreferences(prefix+it)}
            check(root.canonicalFile.parentFile==base.filesDir.canonicalFile && root.name==prefix); root.deleteRecursively()
        }
    }
}
