package com.iconnect.proof

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
class BatchDownloadSourceRecoveryUiTest {
    private val instrumentation get()=InstrumentationRegistry.getInstrumentation()
    private fun nodes(root:AccessibilityNodeInfo?):List<AccessibilityNodeInfo> =if(root==null) emptyList() else listOf(root)+(0 until root.childCount).flatMap {nodes(root.getChild(it))}
    private fun click(label:String) {
        val end=System.currentTimeMillis()+15000
        while(System.currentTimeMillis()<end) {
            val all=nodes(instrumentation.uiAutomation.rootInActiveWindow); var node=all.firstOrNull {it.isVisibleToUser && it.text?.toString()==label}
            while(node!=null && !node.isClickable) node=node.parent
            if(node?.isEnabled==true && node.performAction(AccessibilityNodeInfo.ACTION_CLICK)) {instrumentation.waitForIdleSync(); return}
            all.firstOrNull {it.isScrollable}?.performAction(AccessibilityNodeInfo.ACTION_SCROLL_FORWARD); Thread.sleep(70)
        }; error("Batch source control unavailable: $label")
    }
    @Test fun actualDialogCancelsThenApprovesWithoutCopyingUntilOriginalBatchIsResumed()=runBlocking {
        val f=BatchDownloadSourceRecoveryTest.Fixture(); var defaultModel:PhoneController?=null
        try {
            f.pause(); val before=f.child; val objects=f.providerChildren()
            ActivityScenario.launch(MainActivity::class.java).use {scenario->scenario.onActivity {activity->
                defaultModel=(activity.application as IConnectApplication).controller()
                activity.setContent {MaterialTheme {
                    val rows by f.files.state.collectAsState(); val row=rows.firstOrNull()
                    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp)) {
                        if(row!=null) BatchDownloadSourceRecoveryPanel(row,row.name,PhoneTransferTimeEstimate.create(row.size,row.offset,65536,row.sourceExpiresAt,f.data.now),true,
                            {f.review()},f.batches::confirmSourceReview,f.batches::cancelSourceReview)
                        TextButton({f.batches.resume(f.id,f.data.peer,f.epoch,f::request)}) {Text("Resume original batch")}
                    }
                }}
            }
                click("Review selected fresh source…"); click("Cancel"); assertEquals(before,f.child); assertEquals(objects,f.providerChildren())
                click("Review selected fresh source…"); click("Approve source in original batch")
                withTimeout(10000) {while(f.child.ticket==before.ticket) delay(20)}
                assertEquals(before.offset,f.child.offset); assertEquals(PhoneBatchPhase.PAUSED,f.parent.phase); assertEquals(objects,f.providerChildren())
                click("Resume original batch"); f.batches.awaitIdle(f.id); assertEquals(f.parent.error,PhoneBatchPhase.COMPLETED,f.parent.phase)
                assertEquals(objects.size+1,f.providerChildren().size)
            }
        } finally {
            defaultModel?.let {model->val job=withContext(Dispatchers.Main) {model.close()}; job.join(); withContext(Dispatchers.Main) {(instrumentation.targetContext.applicationContext as IConnectApplication).releaseClosed(model)}}
            f.dispose()
        }
    }
}
