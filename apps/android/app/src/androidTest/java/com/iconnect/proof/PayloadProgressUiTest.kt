package com.iconnect.proof

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

@RunWith(AndroidJUnit4::class)
class PayloadProgressUiTest {
    private val instrumentation get()=InstrumentationRegistry.getInstrumentation()
    private fun nodes(root:AccessibilityNodeInfo?):List<AccessibilityNodeInfo> =if(root==null) emptyList() else listOf(root)+(0 until root.childCount).flatMap {nodes(root.getChild(it))}
    private suspend fun waitText(part:String)=withTimeout(10000) {while(nodes(instrumentation.uiAutomation.rootInActiveWindow).none {it.text?.toString()?.contains(part)==true}) delay(30)}
    @Test fun actualSendAndLocalCardsSeparatePayloadFromReceiverAndProviderSaving():Unit=runBlocking {
        var defaultModel:PhoneController?=null
        var local by mutableStateOf(PhoneDownload("local","","fixture.bin","node","version",10L*1048576,"tree",offset=1048576,phase=PhoneDownloadPhase.DOWNLOADING,active=true))
        var upload by mutableStateOf(PhoneUpload("send","owner","peer","pairing","fixture.bin",10L*1048576,remote="receiver",phase=PhoneUploadPhase.SENDING,active=true))
        var estimate by mutableStateOf(PhonePayloadRateEstimate(PhonePayloadRateState.AVAILABLE,1048576,8))
        var finalization by mutableStateOf<PhoneIncomingFinalization?>(null)
        try {
            ActivityScenario.launch(MainActivity::class.java).use {scenario->scenario.onActivity {activity->
                defaultModel=(activity.application as IConnectApplication).controller()
                activity.setContent {MaterialTheme {Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp)) {
                    UploadPayloadStatus(upload,estimate,true); LocalPayloadStatus(local,3L*1048576,estimate,finalization)
                }}}
            }
                waitText("Recent acknowledged send progress"); waitText("Recent local copy progress"); waitText("Saved resume checkpoint: 1048576 bytes")
                withContext(Dispatchers.Main) {estimate=PhonePayloadRateEstimate(PhonePayloadRateState.WAITING)}
                waitText("Waiting for fresh acknowledged send progress"); waitText("Waiting for fresh local copy progress")
                assertFalse(nodes(instrumentation.uiAutomation.rootInActiveWindow).any {it.text?.toString()?.contains("About 8 s")==true})
                withContext(Dispatchers.Main) {upload=upload.copy(phase=PhoneUploadPhase.VERIFYING); local=local.copy(phase=PhoneDownloadPhase.READY_TO_SAVE)}
                waitText("The receiver is verifying or saving"); waitText("Saving to the chosen provider is separate")
                withContext(Dispatchers.Main) {local=local.copy(phase=PhoneDownloadPhase.PUBLISHING)}; waitText("Saving and checking the provider copy")
                withContext(Dispatchers.Main) {
                    finalization = PhoneIncomingFinalization(PhoneIncomingFinalizationStage.WRITING_PROVIDER_DOCUMENT, local.size, 2L * 1048576,
                        PhonePayloadRateEstimate(PhonePayloadRateState.AVAILABLE, 1048576, 8))
                }
                waitText("Copying bytes into the provider document · 2097152")
                waitText("About 8 s for the remaining bytes in this step")
                withContext(Dispatchers.Main) {
                    finalization = PhoneIncomingFinalization(PhoneIncomingFinalizationStage.SYNCING_PROVIDER_DOCUMENT, local.size, 0, null)
                }
                waitText("Flushing and syncing the provider document. Time remaining is unavailable.")
                assertFalse(nodes(instrumentation.uiAutomation.rootInActiveWindow).any {it.text?.toString()?.contains("About 8 s")==true})
                withContext(Dispatchers.Main) {finalization=null; local=local.copy(phase=PhoneDownloadPhase.COMPLETED)}
                waitText("The verified provider copy is saved")
            }
        } finally {defaultModel?.let {model->val job=withContext(Dispatchers.Main) {model.close()}; job.join(); withContext(Dispatchers.Main) {(instrumentation.targetContext.applicationContext as IConnectApplication).releaseClosed(model)}}}
    }
    @Test fun actualReceivingCardShowsMeasuredWaitingAndStoppedWithoutReusingOldTime()=runBlocking {
        val f=DownloadSourceRecoveryTest.Fixture(); var defaultModel:PhoneController?=null
        var shown by mutableStateOf(PhonePayloadRateEstimate(PhonePayloadRateState.AVAILABLE,65536,30))
        try {
            f.id=f.manager.enqueue(f.peer,f.source.selected,f.tree); val row=f.row.copy(phase=PhoneDownloadPhase.DOWNLOADING,active=true)
            ActivityScenario.launch(MainActivity::class.java).use {scenario->scenario.onActivity {activity->
                defaultModel=(activity.application as IConnectApplication).controller()
                activity.setContent {MaterialTheme {Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp)) {
                    DownloadSourceRecoveryPanel(row,PhoneTransferTimeEstimate.create(row.size,row.offset,0,0,f.now),false,{error("No source action authorized")},{error("No source action authorized")},{},observed=shown)
                }}}
            }
                waitText("Recent payload progress:"); waitText("30 s")
                withContext(Dispatchers.Main) {shown=PhonePayloadRateEstimate(PhonePayloadRateState.WAITING)}; waitText("Waiting for fresh payload progress")
                assertFalse(nodes(instrumentation.uiAutomation.rootInActiveWindow).any {it.text?.toString()?.contains("About 30 s")==true})
                withContext(Dispatchers.Main) {shown=PhonePayloadRateEstimate(PhonePayloadRateState.STOPPED)}; waitText("unavailable while this file is stopped")
            }
        } finally {
            defaultModel?.let {model->val job=withContext(Dispatchers.Main) {model.close()}; job.join(); withContext(Dispatchers.Main) {(instrumentation.targetContext.applicationContext as IConnectApplication).releaseClosed(model)}}
            f.dispose()
        }
    }
}
