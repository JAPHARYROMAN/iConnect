package com.iconnect.proof

import android.app.Application
import android.content.Context
import android.view.accessibility.AccessibilityNodeInfo
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
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

@RunWith(AndroidJUnit4::class)
class PreparedSourceControlsUiTest {
    private val instrumentation get()=InstrumentationRegistry.getInstrumentation()
    private fun nodes(root:AccessibilityNodeInfo?):List<AccessibilityNodeInfo> =if(root==null) emptyList() else listOf(root)+(0 until root.childCount).flatMap {nodes(root.getChild(it))}
    private fun click(label:String) {
        val end=System.currentTimeMillis()+15000
        while(System.currentTimeMillis()<end) {
            val all=nodes(instrumentation.uiAutomation.rootInActiveWindow); var node=all.firstOrNull {it.isVisibleToUser && it.text?.toString()==label}
            while(node!=null && !node.isClickable) node=node.parent
            if(node?.isEnabled==true && node.performAction(AccessibilityNodeInfo.ACTION_CLICK)) {instrumentation.waitForIdleSync(); return}
            all.firstOrNull {it.isScrollable}?.performAction(AccessibilityNodeInfo.ACTION_SCROLL_FORWARD); Thread.sleep(60)
        }; error("Prepared-source control unavailable: $label")
    }
    private fun waitIdentity(id:String) {
        val end=System.currentTimeMillis()+15000
        while(System.currentTimeMillis()<end) {
            if(nodes(instrumentation.uiAutomation.rootInActiveWindow).any {it.isVisibleToUser && it.text?.toString()?.contains(id)==true}) return
            instrumentation.waitForIdleSync(); Thread.sleep(40)
        }; error("Prepared-source receiving-device ID was not visible.")
    }
    @Test fun actualOwnerDialogCancelsThenStopsWithoutDeletingProviderBytes():Unit=runBlocking {
        val f=PreparedSourceControlsTest.Fixture(); var defaultModel:PhoneController?=null
        try {val source=f.prepare(); f.ready(source.id); val before=f.journal.readBytes(); val original=f.original
            ActivityScenario.launch(MainActivity::class.java).use {scenario->scenario.onActivity {activity->
                defaultModel=(activity.application as IConnectApplication).existingController()
                activity.setContent {MaterialTheme {val rows by f.sources.inventory.collectAsState()
                    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp)) {
                        PreparedSourceControlsPanel(rows,true,emptySet(),{f.sources.stopOwned(it) {true}},{f.sources.removeExpiredOwned {true}})
                    }
                }}
            }
                click("Stop this prepared read…"); waitIdentity(f.base.approval.id); click("Keep approval")
                assertArrayEquals(before,f.journal.readBytes()); assertEquals(source.id,f.sources.inventory.value.single().id)
                click("Stop this prepared read…"); click("Stop prepared read")
                withTimeout(10_000) {while(f.sources.inventory.value.isNotEmpty()) delay(20)}
                assertArrayEquals(original,f.original)
            }
        } finally {
            defaultModel?.let {model->val job=withContext(Dispatchers.Main) {model.close()}; job.join(); withContext(Dispatchers.Main) {(instrumentation.targetContext.applicationContext as IConnectApplication).releaseClosed(model)}}
            f.close()
        }
    }
    @Test fun disconnectedControllerShowsEveryPeerAndStopsOnlyTheChosenApprovalInForeground():Unit=runBlocking {
        val f=PreparedSourceControlsTest.Fixture(); var model:PhoneController?=null
        try {
            val other=TestPeer.approve(f.base.policy,TestPeer.key()); f.base.grants.add(other,f.base.tree,f.base.data.getInt("flags"),"Other receiving computer")
            val one=f.prepare(); val two=f.prepare(other.id); f.ready(one.id); f.ready(two.id,other.id); val original=f.original
            f.service.close(); f.sources.shutdown(); f.base.service.close(); f.base.incoming.shutdown(); f.base.grants.batchPlans.shutdown()
            val application=object:Application() {
                init {attachBaseContext(instrumentation.targetContext)}
                override fun getApplicationContext():Context=this
                override fun getFilesDir()=f.base.context.filesDir
                override fun getCacheDir()=f.base.context.cacheDir
                override fun getSharedPreferences(name:String,mode:Int)=f.base.context.getSharedPreferences(name,mode)
            }
            instrumentation.runOnMainSync {model=PhoneController(application) {_,_->object:NetworkWatch {override fun start() {}; override fun stop() {}}}; model.setUiVisible(true)}
            val current=requireNotNull(model); withTimeout(10_000) {while(current.state.value.preparedSources.size!=2) delay(20)}
            assertTrue(current.state.value.sends.map {it.peer}.distinct().size<=1); assertTrue(current.state.value.sends.none {it.active})
            assertFalse(current.state.value.connected); assertFalse(current.state.value.authenticated)
            current.stopPreparedSource(one.id); withTimeout(10_000) {while(current.state.value.preparedSources.size!=1) delay(20)}
            assertEquals(two.id,current.state.value.preparedSources.single().id); assertTrue(current.state.value.backgroundSends.isEmpty())
            instrumentation.runOnMainSync {current.setUiVisible(false)}
            assertTrue(runCatching {current.stopPreparedSource(two.id)}.isFailure); assertEquals(two.id,current.state.value.preparedSources.single().id)
            instrumentation.runOnMainSync {current.setUiVisible(true)}; withTimeout(10_000) {while(two.id in current.state.value.changingSends) delay(20)}
            assertEquals(0,current.removeExpiredPreparedSources()); current.stopPreparedSource(two.id)
            withTimeout(10_000) {while(current.state.value.preparedSources.isNotEmpty()) delay(20)}
            assertArrayEquals(original,f.original); assertFalse(current.state.value.backgroundReady)
        } finally {model?.let {val job=withContext(Dispatchers.Main) {it.close()}; job.join()}; f.close()}
    }
}
