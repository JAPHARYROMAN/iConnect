package com.iconnect.proof

import android.net.Uri
import android.os.Bundle
import android.view.KeyEvent
import android.view.accessibility.AccessibilityEvent
import android.view.accessibility.AccessibilityNodeInfo
import androidx.test.core.app.ActivityScenario
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.util.concurrent.TimeoutException

/** Real Compose controls and Android's actual tree picker; only generated test-provider files. */
@RunWith(AndroidJUnit4::class)
class LocalCopyUiTest {
    private val instrumentation get() = InstrumentationRegistry.getInstrumentation()
    private val context get() = instrumentation.targetContext
    private fun nodes(root: AccessibilityNodeInfo?): List<AccessibilityNodeInfo> {
        if (root == null) return emptyList()
        val result = mutableListOf(root); for (index in 0 until root.childCount) result.addAll(nodes(root.getChild(index))); return result
    }
    private fun waitFor(label: String, condition: () -> Boolean) {
        val end = System.currentTimeMillis() + 25_000
        while (System.currentTimeMillis() < end) { if (condition()) return; Thread.sleep(40) }
        error("Timed out: $label")
    }
    private fun click(text: String) {
        var direction = AccessibilityNodeInfo.ACTION_SCROLL_FORWARD
        repeat(65) {
            val all = nodes(instrumentation.uiAutomation.rootInActiveWindow)
            var node = all.firstOrNull { it.isVisibleToUser && (it.text?.toString()?.equals(text, true) == true || it.contentDescription?.toString()?.equals(text, true) == true) }
            while (node != null && !node.isClickable) node = node.parent
            if (node?.isEnabled == true && node.performAction(AccessibilityNodeInfo.ACTION_CLICK)) { instrumentation.waitForIdleSync(); return }
            if (all.firstOrNull { it.isScrollable && it.isVisibleToUser && it.className?.toString()?.contains("EditText") != true }?.performAction(direction) != true)
                direction = if (direction == AccessibilityNodeInfo.ACTION_SCROLL_FORWARD) AccessibilityNodeInfo.ACTION_SCROLL_BACKWARD else AccessibilityNodeInfo.ACTION_SCROLL_FORWARD
            Thread.sleep(80)
        }
        error("Native copy control unavailable: $text; " + nodes(instrumentation.uiAutomation.rootInActiveWindow).mapNotNull { it.text?.toString() }.take(35).joinToString(" | "))
    }
    private fun pickerVisible() = instrumentation.uiAutomation.rootInActiveWindow?.packageName?.toString()?.contains("documentsui") == true
    private fun cancelPicker() {
        repeat(3) {
            if (!pickerVisible()) return
            try {
                instrumentation.uiAutomation.executeAndWaitForEvent({ instrumentation.sendKeyDownUpSync(KeyEvent.KEYCODE_BACK) },
                    { it.eventType == AccessibilityEvent.TYPE_WINDOW_STATE_CHANGED && it.packageName?.toString() == context.packageName }, 3000)
                instrumentation.waitForIdleSync(); return
            } catch (_: TimeoutException) { /* The picker may dismiss its keyboard first. */ }
        }
        check(!pickerVisible())
    }
    @Test fun nativeCopyReviewCanCancelThePickerThenSaveAndRemoveOnlyTheCopyRecord(): Unit = runBlocking {
        val from = requireNotNull(context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "fixture", null, null))
        val to = requireNotNull(context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "fixture", null, Bundle().apply { putBoolean("pickerRoot", true) }))
        val sourceTree = Uri.parse(from.getString("tree")); var model: PhoneController? = null
        try { ActivityScenario.launch(MainActivity::class.java).use { scenario ->
            lateinit var browser: PhoneLocalBrowser
            scenario.onActivity { activity ->
                val controller = (activity.application as IConnectApplication).controller(); model = controller; browser = controller.localFiles
                assertFalse(controller.state.value.connected); assertTrue(browser.copies.state.value.items.isEmpty())
                browser.add(sourceTree, from.getInt("flags"))
            }
            waitFor("local source shortcut") { !browser.state.value.busy && browser.state.value.rows.size == 1 }
            click("On this phone · local folders"); click("Open local folder")
            waitFor("source listing") { !browser.state.value.busy && browser.state.value.breadcrumbs.size == 1 }
            var edit: AccessibilityNodeInfo? = null
            waitFor("native filename search field") { edit = nodes(instrumentation.uiAutomation.rootInActiveWindow).firstOrNull { it.className?.toString() == "android.widget.EditText" }; edit != null }
            assertTrue(requireNotNull(edit).performAction(AccessibilityNodeInfo.ACTION_SET_TEXT, Bundle().apply { putCharSequence(AccessibilityNodeInfo.ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE, "simu.txt") }))
            instrumentation.waitForIdleSync()
            click("Search local scope"); waitFor("exact simu.txt source result") {
                val snapshot=browser.state.value
                !snapshot.busy && snapshot.query=="simu.txt" && snapshot.rows.singleOrNull()?.entry?.let {it.name=="simu.txt" && !it.isDirectory}==true
            }
            instrumentation.waitForIdleSync()
            click("Copy on this phone…")
            waitFor("provider privacy explanation") { nodes(instrumentation.uiAutomation.rootInActiveWindow).any { it.text?.toString()?.contains("cloud-backed folders may use the internet") == true } }
            click("Choose destination…"); waitFor("real tree picker") { pickerVisible() }
            cancelPicker(); waitFor("canceled picker and retained draft") {
                instrumentation.uiAutomation.rootInActiveWindow?.packageName?.toString() == context.packageName && browser.copies.state.value.items.size == 1
            }
            assertNull(browser.copies.state.value.items.single().download)
            click("Choose destination for: simu.txt"); click("Skip this copy"); click("Keep draft")
            assertNull(browser.copies.state.value.items.single().download)
            click("Choose destination for: simu.txt"); click("Keep both files"); click("Choose destination…")
            waitFor("second real tree picker") { pickerVisible() }
            if (nodes(instrumentation.uiAutomation.rootInActiveWindow).none { it.text?.toString() == "iConnect test copies" }) click("Show roots")
            click("iConnect test copies"); click("Use this folder")
            waitFor("folder grant or completed copy") { browser.copies.state.value.items.single().download?.phase == PhoneDownloadPhase.COMPLETED ||
                nodes(instrumentation.uiAutomation.rootInActiveWindow).any { it.text?.toString()?.equals("Allow", true) == true } }
            if (browser.copies.state.value.items.single().download?.phase != PhoneDownloadPhase.COMPLETED) click("Allow")
            waitFor("verified local save") { browser.copies.state.value.items.single().download?.phase == PhoneDownloadPhase.COMPLETED }
            val row = browser.copies.state.value.items.single().download!!
            assertEquals("simu (1).txt", row.publishedName); assertTrue(row.peer.isEmpty())
            val original = android.provider.DocumentsContract.buildDocumentUriUsingTree(sourceTree, from.getString("file"))
            val expected = context.contentResolver.openInputStream(original)!!.use { it.readBytes() }
            assertArrayEquals(expected, context.contentResolver.openInputStream(Uri.parse(row.published))!!.use { it.readBytes() })
            assertTrue(requireNotNull(model).state.value.phoneShares.isEmpty()); assertFalse(requireNotNull(model).state.value.connected)
            click("Remove local copy record…"); click("Continue"); waitFor("removed copy history") { browser.copies.state.value.items.isEmpty() }
            assertArrayEquals(expected, context.contentResolver.openInputStream(Uri.parse(row.published))!!.use { it.readBytes() })
        } } finally {
            model?.let { controller ->
                val browser = controller.localFiles
                instrumentation.runOnMainSync { controller.setUiVisible(true) }
                browser.copies.state.value.items.toList().forEach { item ->
                    if (item.download?.phase !in setOf(PhoneDownloadPhase.COMPLETED, PhoneDownloadPhase.CANCELED, PhoneDownloadPhase.SKIPPED)) browser.copies.cancel(item.id)
                    if (browser.copies.state.value.items.any { it.id == item.id && it.download != null }) browser.copies.remove(item.id)
                }
                instrumentation.runOnMainSync { browser.state.value.locations.filter { it.tree == sourceTree }.forEach { browser.remove(it.id) } }
                waitFor("source shortcut cleanup") { !browser.state.value.busy && browser.state.value.locations.none { it.tree == sourceTree } }
                lateinit var closing: Job; instrumentation.runOnMainSync { closing = controller.close() }; closing.join()
                instrumentation.runOnMainSync { (context.applicationContext as IConnectApplication).releaseClosed(controller) }
            }
            listOf(from, to).forEach { context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "dispose", it.getString("root"), null) }
        }
    }
}
