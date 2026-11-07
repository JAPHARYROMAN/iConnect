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
import com.iconnect.protocol.UploadPhase
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class IncomingWindowRecoveryUiTest {
    private val instrumentation get()=InstrumentationRegistry.getInstrumentation()
    private fun nodes(root:AccessibilityNodeInfo?):List<AccessibilityNodeInfo> =if(root==null) emptyList() else listOf(root)+(0 until root.childCount).flatMap {nodes(root.getChild(it))}
    private fun click(label:String) {
        val end=System.currentTimeMillis()+15000
        while(System.currentTimeMillis()<end) {
            val all=nodes(instrumentation.uiAutomation.rootInActiveWindow); var node=all.firstOrNull {it.isVisibleToUser && it.text?.toString()==label}
            while(node!=null && !node.isClickable) node=node.parent
            if(node?.isEnabled==true && node.performAction(AccessibilityNodeInfo.ACTION_CLICK)) {instrumentation.waitForIdleSync(); return}
            all.firstOrNull {it.isScrollable}?.performAction(AccessibilityNodeInfo.ACTION_SCROLL_FORWARD); Thread.sleep(70)
        }; error("Incoming-window control unavailable: $label")
    }
    private fun waitForIdentity(id:String) {
        val end=System.currentTimeMillis()+15000
        while(System.currentTimeMillis()<end) {
            if(nodes(instrumentation.uiAutomation.rootInActiveWindow).any {it.isVisibleToUser && it.text?.toString()?.contains(id)==true}) return
            instrumentation.waitForIdleSync(); Thread.sleep(40)
        }; error("The reviewed sending-device ID was not visible in the dialog.")
    }
    @Test fun actualDialogCancelsThenApprovesWithoutReceivingOrCreatingProviderDocuments():Unit=runBlocking {
        val f=IncomingWindowRecoveryTest.Fixture(); var defaultModel:PhoneController?=null
        try {f.start(); f.expire(); val before=f.row; val metadata=f.journal.readBytes(); val provider=f.base.children()
            ActivityScenario.launch(MainActivity::class.java).use {scenario->scenario.onActivity {activity->
                defaultModel=(activity.application as IConnectApplication).existingController()
                activity.setContent {MaterialTheme {val rows by f.base.incoming.state.collectAsState()
                    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp)) {
                        IncomingWindowRecoveryPanel(rows.single(),true,{f.review()},f.base.incoming::confirmWindow,f.base.incoming::cancelWindowReview)
                    }
                }}
            }
                click("Review this write window…"); waitForIdentity(f.base.approval.id)
                click("Cancel"); assertArrayEquals(metadata,f.journal.readBytes()); assertEquals(before,f.row)
                click("Review this write window…"); click("Allow reviewed write window")
                withTimeout(10_000) {while(f.row.operationExpiresAt==before.operationExpiresAt) delay(20)}
                assertEquals(before.offset,f.row.offset); assertEquals(UploadPhase.UPLOAD_RECEIVING,f.row.phase); assertFalse(f.row.active); assertEquals(provider,f.base.children())
            }
        } finally {
            defaultModel?.let {model->val job=withContext(Dispatchers.Main) {model.close()}; job.join(); withContext(Dispatchers.Main) {(instrumentation.targetContext.applicationContext as IConnectApplication).releaseClosed(model)}}
            f.close()
        }
    }
    @Test fun offlineControllerRequiresForegroundAndFreshContextAndLeavesTheSameReceiptStopped():Unit=runBlocking {
        val f=IncomingWindowRecoveryTest.Fixture(); var model:PhoneController?=null
        try {
            f.clock.wall.addAndGet(-MutationRules.LIFETIME-3_600_000); f.start(); val metadata=f.journal.readBytes(); val bytes=f.part().readBytes(); val provider=f.base.children()
            f.base.service.close(); f.base.incoming.shutdown(); f.base.grants.batchPlans.shutdown()
            val application=object:Application() {
                init {attachBaseContext(instrumentation.targetContext)}
                override fun getApplicationContext():Context=this
                override fun getFilesDir()=f.base.context.filesDir
                override fun getCacheDir()=f.base.context.cacheDir
                override fun getSharedPreferences(name:String,mode:Int)=f.base.context.getSharedPreferences(name,mode)
            }
            instrumentation.runOnMainSync {model=PhoneController(application) {_,_->object:NetworkWatch {override fun start() {}; override fun stop() {}}}; model.setUiVisible(true)}
            val current=requireNotNull(model); withTimeout(10_000) {while(current.state.value.incomingCopies.isEmpty()) delay(20)}
            var review=current.reviewIncomingWindow(f.descriptor.id); current.cancelIncomingWindowReview(review.id)
            assertTrue(runCatching {current.confirmIncomingWindow(review)}.isFailure); assertArrayEquals(metadata,f.journal.readBytes())
            review=current.reviewIncomingWindow(f.descriptor.id); instrumentation.runOnMainSync {current.setUiVisible(false)}
            assertTrue(runCatching {current.confirmIncomingWindow(review)}.isFailure); instrumentation.runOnMainSync {current.setUiVisible(true)}
            review=current.reviewIncomingWindow(f.descriptor.id); instrumentation.runOnMainSync {current.disconnect()}
            assertTrue(runCatching {current.confirmIncomingWindow(review)}.isFailure); assertArrayEquals(metadata,f.journal.readBytes())
            review=current.reviewIncomingWindow(f.descriptor.id); current.confirmIncomingWindow(review)
            withTimeout(10_000) {while(current.state.value.incomingCopies.single().operationExpiresAt!=review.proposedExpiresAt) delay(20)}
            val row=current.state.value.incomingCopies.single(); assertEquals(f.descriptor.id,row.id); assertEquals(StorageRules.RANGE.toLong(),row.offset)
            assertEquals(UploadPhase.UPLOAD_RECEIVING,row.phase); assertFalse(row.active); assertFalse(current.state.value.connected); assertFalse(current.state.value.authenticated)
            assertArrayEquals(bytes,f.part().readBytes()); assertEquals(provider,f.base.children())
        } finally {model?.let {val job=withContext(Dispatchers.Main) {it.close()}; job.join()}; f.close()}
    }
}
