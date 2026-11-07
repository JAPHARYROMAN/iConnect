package com.iconnect.proof

import android.view.accessibility.AccessibilityNodeInfo
import android.app.Application
import android.content.Context
import androidx.activity.compose.setContent
import androidx.compose.material3.MaterialTheme
import androidx.test.core.app.ActivityScenario
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

@RunWith(AndroidJUnit4::class)
class BatchPlanRecoveryUiTest {
    private val instrumentation get()=InstrumentationRegistry.getInstrumentation()
    private fun nodes(root:AccessibilityNodeInfo?):List<AccessibilityNodeInfo> =if(root==null) emptyList() else listOf(root)+(0 until root.childCount).flatMap {nodes(root.getChild(it))}
    private fun scroll(root:AccessibilityNodeInfo,forward:Boolean) {
        val bounds=android.graphics.Rect(); root.getBoundsInScreen(bounds)
        val x=bounds.left+bounds.width()*0.94f
        val start=bounds.top+bounds.height()*(if(forward) 0.72f else 0.40f)
        val finish=bounds.top+bounds.height()*(if(forward) 0.40f else 0.72f)
        val down=android.os.SystemClock.uptimeMillis()
        fun event(action:Int,y:Float) {val value=android.view.MotionEvent.obtain(down,android.os.SystemClock.uptimeMillis(),action,x,y,0)
            value.source=android.view.InputDevice.SOURCE_TOUCHSCREEN
            try {check(instrumentation.uiAutomation.injectInputEvent(value,true))} finally {value.recycle()}
        }
        event(android.view.MotionEvent.ACTION_DOWN,start)
        for(step in 1..8) {Thread.sleep(30); event(android.view.MotionEvent.ACTION_MOVE,start+(finish-start)*step/8)}
        // A stationary tail avoids a fling that can skip a narrow control.
        repeat(2) {Thread.sleep(100); event(android.view.MotionEvent.ACTION_MOVE,finish)}
        event(android.view.MotionEvent.ACTION_UP,finish); Thread.sleep(100)
    }
    private fun advance(all:List<AccessibilityNodeInfo>,forward:Boolean):Boolean {
        val root=all.firstOrNull {it.isScrollable}?:return forward
        val action=if(forward) AccessibilityNodeInfo.ACTION_SCROLL_FORWARD else AccessibilityNodeInfo.ACTION_SCROLL_BACKWARD
        val selected=if(root.actionList.any {it.id==action}) forward else !forward
        scroll(root,selected); return selected
    }
    private fun click(text:String) {
        val end=android.os.SystemClock.elapsedRealtime()+45_000; var forward=true
        while(android.os.SystemClock.elapsedRealtime()<end) {
            val all=nodes(instrumentation.uiAutomation.rootInActiveWindow); var node=all.firstOrNull {it.text?.toString()==text && it.isVisibleToUser}
            while(node!=null && !node.isClickable) node=node.parent
            if(node?.isEnabled==true && node.performAction(AccessibilityNodeInfo.ACTION_CLICK)) {instrumentation.waitForIdleSync(); Thread.sleep(100); return}
            forward=advance(all,forward); Thread.sleep(50)
        }
        val pages=nodes(instrumentation.uiAutomation.rootInActiveWindow).mapNotNull {it.text?.toString()}.filter {it.startsWith("Recovery page ")}
        runCatching {
            val known=setOf("iConnect","Devices and approved files","On this phone · local folders","Incoming batch plans","Review saved progress","Archive / reset batch-plan history…","Review batch-plan history…","Next recovery items","Previous recovery items","Keep history","Local activity history","Recent status","Connect")
            File(instrumentation.targetContext.cacheDir,"batch-plan-ui-failure.txt").writeText(nodes(instrumentation.uiAutomation.rootInActiveWindow).joinToString("\n") {item->
                val bounds=android.graphics.Rect(); item.getBoundsInScreen(bounds); val label=item.text?.toString().orEmpty()
                "${item.className} ${if(label in known || label.startsWith("Recovery page ")) label else "<${label.length} chars>"} visible=${item.isVisibleToUser} scroll=${item.isScrollable} enabled=${item.isEnabled} bounds=$bounds actions=${item.actionList.map {it.id}}"
            })
            instrumentation.uiAutomation.takeScreenshot()?.let {bitmap->File(instrumentation.targetContext.cacheDir,"batch-plan-ui-failure.png").outputStream().use {bitmap.compress(android.graphics.Bitmap.CompressFormat.PNG,100,it)}; bitmap.recycle()}
        }
        fail("Native batch-plan review control unavailable: $text; $pages")
    }
    private fun waitText(text:String) {val end=android.os.SystemClock.elapsedRealtime()+30_000; var forward=false; while(android.os.SystemClock.elapsedRealtime()<end) {
        val all=nodes(instrumentation.uiAutomation.rootInActiveWindow); if(all.any {it.text?.toString()==text}) return
        forward=advance(all,forward); Thread.sleep(50)
    }; fail("Native recovery page unavailable: $text")}
    @Test fun damagedPlanHistoryIsVisibleAndCancelingReviewKeepsItsExactMetadata()=runBlocking {
        val context=instrumentation.targetContext; val prefix="batch-plan-ui-"+StorageRules.token(); val root=File(context.filesDir,prefix).apply {check(mkdir())}
        val app=object:Application() {
            init {attachBaseContext(context)}
            override fun getFilesDir()=root
            override fun getCacheDir()=File(root,"cache").apply {mkdirs()}
            override fun getApplicationContext():Context=this
            override fun getSharedPreferences(name:String,mode:Int)=context.getSharedPreferences(prefix+name,mode)
        }
        val directory=File(root,"incoming-batch-plans").apply {check(mkdir())}
        val fixture="{generated damaged batch context fixture"; val damaged=File(directory,"context.json"); damaged.writeText(fixture)
        val files=File(root,"downloads").apply {check(mkdir())}
        val unrelated=(0 until 21).map {File(files,StorageRules.token()+".json").apply {writeText("{generated unreadable file fixture")}}
        var current:PhoneController?=null; var defaultModel:PhoneController?=null
        suspend fun close(model:PhoneController) {lateinit var job:Job; instrumentation.runOnMainSync {job=model.close()}; job.join()}
        try {
            instrumentation.runOnMainSync {val controller=PhoneController(app) {_,_->object:NetworkWatch {override fun start() {}; override fun stop() {}}}; current=controller; controller.setUiVisible(true)}
            val model=requireNotNull(current); withTimeout(10_000) {while(!model.state.value.activity.ready) delay(20)}
            ActivityScenario.launch(MainActivity::class.java).use {scenario->
            scenario.onActivity {activity->defaultModel=(activity.application as IConnectApplication).controller(); activity.setContent {MaterialTheme {ProofScreen(model)}}}
            assertTrue(model.state.value.transferRecovery.any {it.area=="Batch plans"}); assertFalse(model.state.value.connected)
            assertEquals(22,model.state.value.transferRecovery.size)
            click("Archive / reset batch-plan history…"); click("Keep history")
            assertEquals(fixture,damaged.readText()); assertTrue(model.state.value.transferRecovery.any {it.area=="Batch plans"}); assertFalse(model.state.value.connected)
            val targetPage=model.state.value.transferRecovery.indexOfFirst {it.area=="Batch plans"}/20
            val pages=maxOf(1,(model.state.value.transferRecovery.size+19)/20)
            println("Batch-plan recovery route: $pages pages, target page ${targetPage+1}.")
            repeat(targetPage) {page->click("Next recovery items"); waitText("Recovery page ${page+2} / $pages")}
            click("Review batch-plan history…"); click("Keep history"); assertEquals(fixture,damaged.readText()); assertFalse(model.state.value.connected)
            model.flushActivity(); assertTrue(model.state.value.activity.entries.none {it.kind==PhoneActivityKind.BATCH_HISTORY_RESET && it.outcome==PhoneActivityOutcome.COMPLETED})
            unrelated.forEach {assertEquals("{generated unreadable file fixture",it.readText())}
        }} finally {
            current?.let {close(it)}; defaultModel?.let {close(it); instrumentation.runOnMainSync {(context.applicationContext as IConnectApplication).releaseClosed(it)}}
            listOf("iconnect.proof","iconnect.storage","iconnect.saf-access").forEach {context.deleteSharedPreferences(prefix+it)}
            check(root.canonicalFile.parentFile==context.filesDir.canonicalFile && root.name==prefix); check(root.deleteRecursively())
        }
    }
}
