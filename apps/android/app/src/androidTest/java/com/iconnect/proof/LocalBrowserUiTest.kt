package com.iconnect.proof

import android.net.Uri
import android.os.Bundle
import android.view.accessibility.AccessibilityNodeInfo
import androidx.test.core.app.ActivityScenario
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import kotlinx.coroutines.Job
import kotlinx.coroutines.runBlocking

/** Exercises the actual Compose accessibility surface; only generated provider files are seeded. */
@RunWith(AndroidJUnit4::class)
class LocalBrowserUiTest {
    @Test fun nativeLocalImagePreviewCanRotateZoomAndCloseWithoutSharingTheFolder() {
        val context=instrumentation.targetContext
        val fixture=requireNotNull(context.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"fixture",null,Bundle().apply { putBoolean("media",true) }))
        val tree=Uri.parse(fixture.getString("tree")); var controller: PhoneController?=null; var approvalsBefore:List<AndroidStorageGrant>?=null
        UiExistingApproval(context,tree,fixture.getInt("flags")).use { existing ->
        try { ActivityScenario.launch(MainActivity::class.java).use { scenario ->
            lateinit var browser: PhoneLocalBrowser
            scenario.onActivity { activity -> val model=(activity.application as IConnectApplication).controller(); controller=model; approvalsBefore=model.state.value.phoneShares.toList(); check(approvalsBefore!!.any {it.id==existing.grantId}); browser=model.localFiles; browser.add(tree,fixture.getInt("flags")) }
            waitFor("media local shortcut") { !browser.state.value.busy && browser.state.value.rows.size==1 }
            click("On this phone · local folders"); click("Open local folder")
            waitFor("media local files") { !browser.state.value.busy && browser.state.value.rows.any { it.entry.name=="fixture.png" } }
            var edit: AccessibilityNodeInfo?=null
            waitFor("local filename search") { edit=nodes(instrumentation.uiAutomation.rootInActiveWindow).firstOrNull { it.className?.toString()=="android.widget.EditText" }; edit!=null }
            assertTrue(requireNotNull(edit).performAction(AccessibilityNodeInfo.ACTION_SET_TEXT,Bundle().apply { putCharSequence(AccessibilityNodeInfo.ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE,"fixture.png") }))
            click("Search local scope"); waitFor("one image match") {
                val snapshot=browser.state.value
                !snapshot.busy && snapshot.query=="fixture.png" && snapshot.rows.singleOrNull()?.entry?.let {it.name=="fixture.png" && !it.isDirectory}==true
            }
            instrumentation.waitForIdleSync()
            click("Preview"); waitFor("native isolated image") { browser.preview.state.value.frame!=null && !browser.preview.state.value.busy }
            assertEquals(android.graphics.Color.RED,browser.preview.state.value.frame!!.bitmap.getPixel(0,0)); assertEquals(requireNotNull(approvalsBefore).toSet(),controller!!.state.value.phoneShares.toSet())
            click("Zoom in"); click("Rotate preview"); click("Reset view"); click("Close preview")
            waitFor("closed preview") { browser.preview.state.value.frame==null && browser.preview.state.value.name.isEmpty() }
            scenario.onActivity { browser.inspect(browser.state.value.rows.single().entry,true) }
            waitFor("reopened image") { browser.preview.state.value.frame!=null }
            scenario.moveToState(androidx.lifecycle.Lifecycle.State.CREATED)
            waitFor("background clears private preview") { browser.preview.state.value.frame==null && browser.preview.state.value.name.isEmpty() }
        } } finally {
            controller?.let { model -> lateinit var closing:Job; instrumentation.runOnMainSync { closing=model.close() }; runBlocking { closing.join() }; instrumentation.runOnMainSync { (context.applicationContext as IConnectApplication).releaseClosed(model) } }
            LocalDocumentLocations(context).all().filter { it.tree==tree }.forEach { LocalDocumentLocations(context).remove(it.id) }
            context.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"dispose",fixture.getString("root"),null)
        }
        }
    }
    private val instrumentation get() = InstrumentationRegistry.getInstrumentation()
    private fun nodes(root: AccessibilityNodeInfo?): List<AccessibilityNodeInfo> {
        if (root == null) return emptyList()
        return listOf(root) + (0 until root.childCount).flatMap { nodes(root.getChild(it)) }
    }
    private fun click(text: String) {
        val end = System.currentTimeMillis() + 10_000
        var direction=AccessibilityNodeInfo.ACTION_SCROLL_FORWARD
        while (System.currentTimeMillis() < end) {
            val all = nodes(instrumentation.uiAutomation.rootInActiveWindow)
            var node = all.firstOrNull { it.text?.toString() == text && it.isVisibleToUser }
            if (node != null) {
                while (node != null && !node.isClickable) node = node.parent
                if (node?.isEnabled==true && node.performAction(AccessibilityNodeInfo.ACTION_CLICK)) {instrumentation.waitForIdleSync(); return}
            }
            val scrolled=all.firstOrNull { it.isScrollable && it.className?.toString()?.contains("EditText") != true }?.performAction(direction) == true
            if(!scrolled) direction=if(direction==AccessibilityNodeInfo.ACTION_SCROLL_FORWARD) AccessibilityNodeInfo.ACTION_SCROLL_BACKWARD else AccessibilityNodeInfo.ACTION_SCROLL_FORWARD
            Thread.sleep(100)
        }
        fail("Native control unavailable: $text\n"+nodes(instrumentation.uiAutomation.rootInActiveWindow).mapNotNull { it.text?.toString() }.take(40).joinToString(" | "))
    }
    private fun waitFor(label: String, condition: () -> Boolean) {
        val end = System.currentTimeMillis() + 10_000
        while (System.currentTimeMillis() < end) { if (condition()) return; Thread.sleep(25) }
        fail("Timed out: $label")
    }
    @Test fun localScreenOpensSearchesAndPreviewsGeneratedFilesWithNoConnection() {
        val context = instrumentation.targetContext
        val fixture = requireNotNull(context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "fixture", null, null))
        val tree = Uri.parse(fixture.getString("tree"))
        lateinit var browser: PhoneLocalBrowser
        var controller: PhoneController? = null
        try { ActivityScenario.launch(MainActivity::class.java).use { scenario ->
            scenario.onActivity { activity ->
                val model = (activity.application as IConnectApplication).controller(); assertFalse(model.state.value.connected)
                controller = model
                browser = model.localFiles; browser.add(tree, fixture.getInt("flags"))
            }
            waitFor("local shortcut") { !browser.state.value.busy && browser.state.value.rows.size == 1 }
            click("On this phone · local folders"); click("Open local folder")
            waitFor("local file rows") { !browser.state.value.busy && browser.state.value.breadcrumbs.size == 1 }
            var edit: AccessibilityNodeInfo? = null
            waitFor("native search field") { edit = nodes(instrumentation.uiAutomation.rootInActiveWindow).firstOrNull { it.className?.toString() == "android.widget.EditText" }; edit != null }
            assertTrue(requireNotNull(edit).performAction(AccessibilityNodeInfo.ACTION_SET_TEXT, Bundle().apply {
                putCharSequence(AccessibilityNodeInfo.ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE, "notes")
            }))
            click("Search local scope")
            waitFor("nested local match") { !browser.state.value.busy && browser.state.value.query == "notes" && browser.state.value.rows.size == 1 }
            click("Details")
            waitFor("native metadata dialog") { nodes(instrumentation.uiAutomation.rootInActiveWindow).any { it.text?.toString() == "Local item details" } }
            click("Done"); click("Preview")
            waitFor("actual provider preview") { browser.state.value.preview == "nested fixture" }
            assertTrue(nodes(instrumentation.uiAutomation.rootInActiveWindow).any { it.text?.toString() == "notes.txt" })
            val id = browser.state.value.locations.single { it.tree == tree }.id
            scenario.onActivity { browser.remove(id) }
            waitFor("removed fixture shortcut") { !browser.state.value.busy && browser.state.value.locations.none { it.tree == tree } }
        } } finally {
            controller?.let { model ->
                lateinit var closing: Job; instrumentation.runOnMainSync { closing = model.close() }; runBlocking { closing.join() }
                instrumentation.runOnMainSync { (context.applicationContext as IConnectApplication).releaseClosed(model) }
            }
            LocalDocumentLocations(context).all().filter { it.tree == tree }.forEach { LocalDocumentLocations(context).remove(it.id) }
            context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "dispose", fixture.getString("root"), null)
        }
    }
}
