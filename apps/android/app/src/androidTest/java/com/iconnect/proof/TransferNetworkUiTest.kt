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
import java.io.File

@RunWith(AndroidJUnit4::class)
class TransferNetworkUiTest {
    private val instrumentation get()=InstrumentationRegistry.getInstrumentation()
    private fun nodes(root:AccessibilityNodeInfo?):List<AccessibilityNodeInfo> =if(root==null) emptyList() else listOf(root)+(0 until root.childCount).flatMap {nodes(root.getChild(it))}
    private fun click(label:String) {
        val end=System.currentTimeMillis()+15000; var direction=AccessibilityNodeInfo.ACTION_SCROLL_FORWARD
        while(System.currentTimeMillis()<end) {
            val all=nodes(instrumentation.uiAutomation.rootInActiveWindow)
            var node=all.firstOrNull {it.isVisibleToUser && (it.text?.toString()==label || it.contentDescription?.toString()==label)}
            while(node!=null && !node.isClickable) node=node.parent
            if(node?.isEnabled==true && node.performAction(AccessibilityNodeInfo.ACTION_CLICK)) {instrumentation.waitForIdleSync(); return}
            if(all.firstOrNull {it.isScrollable}?.performAction(direction)!=true) direction=if(direction==AccessibilityNodeInfo.ACTION_SCROLL_FORWARD) AccessibilityNodeInfo.ACTION_SCROLL_BACKWARD else AccessibilityNodeInfo.ACTION_SCROLL_FORWARD
            Thread.sleep(70)
        }; error("Network control unavailable: $label")
    }
    private suspend fun waitFor(condition:()->Boolean) {withTimeout(15000) {while(!condition()) delay(20)}}
    @Test fun actualControlsCanCancelSaveReopenAndRecoverSettingsWithoutChangingOriginalData()=runBlocking {
        val f=ActivityControlsTest.Fixture(); val kept=File(f.root,"generated-owner.txt").apply {writeText("original generated data")}; var defaultModel:PhoneController?=null
        try {
            f.open(); var controller=f.model
            ActivityScenario.launch(MainActivity::class.java).use {scenario->
                fun show() {val active=controller; scenario.onActivity {activity->defaultModel=(activity.application as IConnectApplication).controller(); activity.setContent {MaterialTheme {
                    val state by active.state.collectAsState(); Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp)) {TransferNetworkPanel(state.transferNetwork,active::setTransferNetwork,active::resetTransferNetwork,active::clearTransferNetworkBackups)}
                }}}}
                show(); click("Review transfer network settings…"); click("64 KiB/s"); click("Cancel"); assertEquals(0L,controller.state.value.transferNetwork.preferences.bytesPerSecond)
                click("Review transfer network settings…"); click("64 KiB/s"); click("Pause remote content on metered or unknown networks"); click("Save reviewed controls")
                waitFor {controller.state.value.transferNetwork.preferences==PhoneNetworkPreferences(65536,true) && !controller.state.value.transferNetwork.busy}
                f.closeModel(); f.open(); controller=f.model; show(); assertEquals(PhoneNetworkPreferences(65536,true),controller.state.value.transferNetwork.preferences)
                f.closeModel(); val path=File(f.root,"transfer-network/settings.json"); path.writeText("generated unreadable network metadata"); f.open(); controller=f.model; show()
                click("Preserve and reset network settings…"); click("Cancel"); assertEquals("generated unreadable network metadata",path.readText())
                click("Preserve and reset network settings…"); click("Reset reviewed settings"); waitFor {controller.state.value.transferNetwork.issue.isEmpty() && !controller.state.value.transferNetwork.busy}
                assertEquals(PhoneNetworkPreferences(),controller.state.value.transferNetwork.preferences); assertEquals("original generated data",kept.readText()); assertTrue(controller.state.value.phoneShares.isEmpty()); assertFalse(controller.state.value.connected)
            }
        } finally {defaultModel?.let {value->lateinit var job:Job; f.main {job=value.close()}; job.join(); f.main {(instrumentation.targetContext.applicationContext as IConnectApplication).releaseClosed(value)}}; f.dispose()}
    }
}
