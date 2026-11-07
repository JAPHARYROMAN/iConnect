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
class DownloadSourceRecoveryUiTest {
    private val instrumentation get()=InstrumentationRegistry.getInstrumentation()
    private fun nodes(root:AccessibilityNodeInfo?):List<AccessibilityNodeInfo> =if(root==null) emptyList() else listOf(root)+(0 until root.childCount).flatMap {nodes(root.getChild(it))}
    private fun click(label:String) {
        val end=System.currentTimeMillis()+15000
        while(System.currentTimeMillis()<end) {
            val all=nodes(instrumentation.uiAutomation.rootInActiveWindow)
            var node=all.firstOrNull {it.isVisibleToUser && it.text?.toString()==label}
            while(node!=null && !node.isClickable) node=node.parent
            if(node?.isEnabled==true && node.performAction(AccessibilityNodeInfo.ACTION_CLICK)) {instrumentation.waitForIdleSync(); return}
            all.firstOrNull {it.isScrollable}?.performAction(AccessibilityNodeInfo.ACTION_SCROLL_FORWARD); Thread.sleep(70)
        }; error("Download source control unavailable: $label")
    }
    @Test fun actualSourceReviewCanCancelThenApproveWithoutReceivingUntilSeparateResume()=runBlocking {
        val f=DownloadSourceRecoveryTest.Fixture(); var defaultModel:PhoneController?=null
        try {
            f.paused(); val before=f.row
            ActivityScenario.launch(MainActivity::class.java).use {scenario->
                scenario.onActivity {activity->
                    defaultModel=(activity.application as IConnectApplication).controller()
                    activity.setContent {MaterialTheme {
                        val rows by f.manager.state.collectAsState(); val row=rows.single()
                        Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp)) {
                            DownloadSourceRecoveryPanel(row,PhoneTransferTimeEstimate.create(row.size,row.offset,65536,row.sourceExpiresAt,f.now),true,
                                {f.review()},f.manager::confirmSourceReview,f.manager::cancelSourceReview)
                            TextButton({f.manager.resume(f.id,f.peer,f.source::request)},enabled=!row.active) {Text("Resume received bytes")}
                        }
                    }}
                }
                click("Review selected fresh source…"); click("Cancel"); assertEquals(before,f.row)
                click("Review selected fresh source…"); click("Approve source only")
                withTimeout(10000) {while(f.row.ticket==before.ticket) delay(20)}
                assertEquals(before.offset,f.row.offset); assertEquals(PhoneDownloadPhase.PAUSED,f.row.phase); assertFalse(f.row.active)
                click("Resume received bytes"); f.manager.awaitIdle(f.id); assertEquals(f.row.error,PhoneDownloadPhase.READY_TO_SAVE,f.row.phase)
                assertTrue(f.row.published.isEmpty())
            }
        } finally {
            defaultModel?.let {model->val job=withContext(Dispatchers.Main) {model.close()}; job.join(); withContext(Dispatchers.Main) {(instrumentation.targetContext.applicationContext as IConnectApplication).releaseClosed(model)}}
            f.dispose()
        }
    }
}
