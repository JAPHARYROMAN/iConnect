package com.iconnect.proof

import android.view.accessibility.AccessibilityNodeInfo
import androidx.test.core.app.ActivityScenario
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.Job
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

@RunWith(AndroidJUnit4::class)
class QueueRecoveryUiTest {
    private val instrumentation get() = InstrumentationRegistry.getInstrumentation()
    private fun nodes(root: AccessibilityNodeInfo?): List<AccessibilityNodeInfo> = if (root == null) emptyList() else
        listOf(root) + (0 until root.childCount).flatMap { nodes(root.getChild(it)) }
    private fun click(text: String) {
        val end = System.currentTimeMillis() + 10_000
        while (System.currentTimeMillis() < end) {
            val all = nodes(instrumentation.uiAutomation.rootInActiveWindow)
            var node = all.firstOrNull { it.text?.toString() == text && it.isVisibleToUser }
            if (node != null) {
                while (node != null && !node.isClickable) node = node.parent
                if (node?.performAction(AccessibilityNodeInfo.ACTION_CLICK) == true) return
            }
            all.firstOrNull { it.isScrollable }?.performAction(AccessibilityNodeInfo.ACTION_SCROLL_FORWARD)
            Thread.sleep(100)
        }
        fail("Native recovery control unavailable: $text")
    }
    private fun waitFor(condition: () -> Boolean) {
        val end = System.currentTimeMillis() + 10_000
        while (System.currentTimeMillis() < end) { if (condition()) return; Thread.sleep(25) }
        fail("Recovery UI did not finish its metadata operation.")
    }
    @Test fun damagedReceivingRecordDoesNotBlockAppAndNativeArchiveKeepsPrivateStaging() = runBlocking {
        val context = instrumentation.targetContext; val app = context.applicationContext as IConnectApplication
        lateinit var old: PhoneController; lateinit var closing: Job
        instrumentation.runOnMainSync { old = app.controller(); closing = old.close() }; closing.join()
        instrumentation.runOnMainSync { app.releaseClosed(old) }
        val queue = File(context.filesDir, "downloads"); check(queue.mkdirs() || queue.isDirectory)
        val id = StorageRules.token(); val damaged = File(queue, "$id.json"); damaged.writeText("{generated native recovery fixture")
        val part = File(queue, "$id.part"); part.writeText("generated private partial")
        var archiveId = ""; var current: PhoneController? = null
        try { ActivityScenario.launch(MainActivity::class.java).use { scenario ->
            scenario.onActivity { current = app.controller() }
            val model = requireNotNull(current); assertFalse(model.state.value.connected)
            val entry = model.state.value.transferRecovery.single { it.area == "Files" && it.issue.record == "$id.json" }
            assertEquals(1, model.state.value.transferRecovery.size); archiveId = entry.issue.id
            click("Archive progress record…"); click("Archive progress")
            waitFor { model.state.value.transferRecovery.isEmpty() }
            assertFalse(damaged.exists()); assertEquals("generated private partial", part.readText())
            assertEquals("{generated native recovery fixture", File(queue, "recovery/$archiveId/$id.json").readText())
            assertFalse(model.state.value.connected)
        } } finally {
            current?.let { model -> instrumentation.runOnMainSync { closing = model.close() }; closing.join(); instrumentation.runOnMainSync { app.releaseClosed(model) } }
            damaged.delete(); part.delete()
            if (archiveId.isNotEmpty()) File(queue, "recovery/$archiveId").deleteRecursively()
        }
    }
}
