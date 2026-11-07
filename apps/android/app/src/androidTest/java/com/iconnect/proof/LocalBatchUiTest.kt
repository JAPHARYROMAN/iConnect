package com.iconnect.proof

import android.net.Uri
import android.os.Bundle
import android.view.KeyEvent
import android.view.accessibility.AccessibilityNodeInfo
import androidx.test.core.app.ActivityScenario
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith

/** Actual Compose controls and the system tree picker, using disposable provider data. */
@RunWith(AndroidJUnit4::class)
class LocalBatchUiTest {
    private val instrumentation get() = InstrumentationRegistry.getInstrumentation()
    private val context get() = instrumentation.targetContext
    private fun nodes(root: AccessibilityNodeInfo?): List<AccessibilityNodeInfo> {
        if (root == null) return emptyList(); val result = mutableListOf(root)
        for (index in 0 until root.childCount) result.addAll(nodes(root.getChild(index))); return result
    }
    private fun waitFor(label: String, action: () -> Boolean) {
        val end = System.currentTimeMillis() + 40_000
        while (System.currentTimeMillis() < end) { if (action()) return; Thread.sleep(40) }; error("Timed out: $label")
    }
    private fun click(text: String) {
        var direction = AccessibilityNodeInfo.ACTION_SCROLL_FORWARD
        repeat(80) {
            val all = nodes(instrumentation.uiAutomation.rootInActiveWindow)
            var node = all.firstOrNull { it.isVisibleToUser && (it.text?.toString()?.equals(text, true) == true || it.contentDescription?.toString()?.equals(text, true) == true) }
            while (node != null && !node.isClickable) node = node.parent
            if (node?.isEnabled == true && node.performAction(AccessibilityNodeInfo.ACTION_CLICK)) { instrumentation.waitForIdleSync(); return }
            if (all.firstOrNull { it.isScrollable && it.isVisibleToUser && it.className?.toString()?.contains("EditText") != true }?.performAction(direction) != true)
                direction = if (direction == AccessibilityNodeInfo.ACTION_SCROLL_FORWARD) AccessibilityNodeInfo.ACTION_SCROLL_BACKWARD else AccessibilityNodeInfo.ACTION_SCROLL_FORWARD
            Thread.sleep(80)
        }; error("Local batch control unavailable: $text; " + nodes(instrumentation.uiAutomation.rootInActiveWindow).mapNotNull { it.text?.toString() }.take(30).joinToString(" | "))
    }
    private fun pickerVisible() = instrumentation.uiAutomation.rootInActiveWindow?.packageName?.toString()?.contains("documentsui") == true
    @Test fun selectFolderPlanCancelPickerThenCopyAndRemoveOnlyItsHistory(): Unit = runBlocking {
        val from = requireNotNull(context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "fixture", null, null))
        val to = requireNotNull(context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "fixture", null, Bundle().apply { putBoolean("pickerRoot", true) }))
        val sourceTree = Uri.parse(from.getString("tree")); var model: PhoneController? = null
        try { ActivityScenario.launch(MainActivity::class.java).use { scenario ->
            lateinit var browser: PhoneLocalBrowser
            scenario.onActivity { activity -> val controller = (activity.application as IConnectApplication).controller(); model = controller; browser = controller.localFiles; browser.add(sourceTree, from.getInt("flags")) }
            waitFor("local shortcut") { !browser.state.value.busy && browser.state.value.rows.size == 1 }
            click("On this phone · local folders"); click("Open local folder")
            waitFor("source listing") { !browser.state.value.busy && browser.state.value.breadcrumbs.size == 1 }
            var edit: AccessibilityNodeInfo? = null
            waitFor("filename search") { edit = nodes(instrumentation.uiAutomation.rootInActiveWindow).firstOrNull { it.className?.toString() == "android.widget.EditText" }; edit != null }
            assertTrue(requireNotNull(edit).performAction(AccessibilityNodeInfo.ACTION_SET_TEXT, Bundle().apply { putCharSequence(AccessibilityNodeInfo.ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE, "Reports") }))
            instrumentation.waitForIdleSync()
            click("Search local scope"); waitFor("exact Reports folder result") {
                val snapshot=browser.state.value
                !snapshot.busy && snapshot.query=="Reports" && snapshot.rows.singleOrNull()?.entry?.let {it.name=="Reports" && it.isDirectory}==true
            }
            instrumentation.waitForIdleSync()
            click("Select for batch: Reports"); click("Plan local file and folder copy…")
            waitFor("frozen local plan") { !browser.localBatches.state.value.planning && browser.localBatches.state.value.items.size == 1 }
            val id = browser.localBatches.state.value.items.single().id; assertNull(browser.localBatches.state.value.items.single().batch)
            click("Review destination for local batch…"); click("Choose batch destination…"); waitFor("real folder picker") { pickerVisible() }
            instrumentation.sendKeyDownUpSync(KeyEvent.KEYCODE_BACK); instrumentation.waitForIdleSync()
            waitFor("canceled picker draft") { !pickerVisible() && browser.localBatches.state.value.items.single().batch == null }
            click("Review destination for local batch…"); click("Choose batch destination…"); waitFor("second folder picker") { pickerVisible() }
            if (nodes(instrumentation.uiAutomation.rootInActiveWindow).none { it.text?.toString() == "iConnect test copies" }) click("Show roots")
            click("iConnect test copies"); click("Use this folder")
            waitFor("folder permission") { browser.localBatches.state.value.items.single().batch?.phase == PhoneBatchPhase.COMPLETED || nodes(instrumentation.uiAutomation.rootInActiveWindow).any { it.text?.toString()?.equals("Allow", true) == true } }
            if (browser.localBatches.state.value.items.single().batch?.phase != PhoneBatchPhase.COMPLETED) click("Allow")
            waitFor("verified folder copy") { browser.localBatches.state.value.items.single().batch?.phase == PhoneBatchPhase.COMPLETED }
            val row = browser.localBatches.state.value.items.single().batch!!; assertEquals(id, row.id); assertTrue(row.peer.isEmpty() && row.epoch.isEmpty())
            val original = android.provider.DocumentsContract.buildDocumentUriUsingTree(sourceTree, from.getString("nested")); val published = Uri.parse(row.items.single { !it.source.directory }.published)
            val expected = context.contentResolver.openInputStream(original)!!.use { it.readBytes() }; assertArrayEquals(expected, context.contentResolver.openInputStream(published)!!.use { it.readBytes() })
            assertTrue(requireNotNull(model).state.value.phoneShares.isEmpty()); assertFalse(requireNotNull(model).state.value.connected)
            click("Remove local batch record…"); click("Confirm local batch action"); waitFor("removed history") { browser.localBatches.state.value.items.isEmpty() }
            assertArrayEquals(expected, context.contentResolver.openInputStream(published)!!.use { it.readBytes() })
        } } finally {
            model?.let { controller ->
                val browser = controller.localFiles; instrumentation.runOnMainSync { controller.setUiVisible(true) }
                browser.localBatches.state.value.items.toList().forEach { item -> runCatching { if (item.batch != null) { browser.localBatches.cancel(item.id); browser.localBatches.remove(item.id) } else if (item.started) browser.localBatches.removeMissing(item.id) else browser.localBatches.cancel(item.id) } }
                instrumentation.runOnMainSync { browser.state.value.locations.filter { it.tree == sourceTree }.forEach { browser.remove(it.id) } }
                waitFor("shortcut cleanup") { !browser.state.value.busy && browser.state.value.locations.none { it.tree == sourceTree } }
                lateinit var closing: Job; instrumentation.runOnMainSync { closing = controller.close() }; closing.join()
                instrumentation.runOnMainSync { (context.applicationContext as IConnectApplication).releaseClosed(controller) }
            }
            listOf(from, to).forEach { context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "dispose", it.getString("root"), null) }
        }
    }
}
