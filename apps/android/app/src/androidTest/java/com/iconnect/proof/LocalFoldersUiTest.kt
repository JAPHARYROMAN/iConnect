package com.iconnect.proof

import android.net.Uri
import android.os.Bundle
import android.provider.DocumentsContract as Docs
import android.view.KeyEvent
import android.view.accessibility.AccessibilityNodeInfo
import androidx.test.core.app.ActivityScenario
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class LocalFoldersUiTest {
    private val instrumentation get()=InstrumentationRegistry.getInstrumentation()
    private val context get()=instrumentation.targetContext
    private fun nodes(root:AccessibilityNodeInfo?):List<AccessibilityNodeInfo> =if(root==null) emptyList() else listOf(root)+(0 until root.childCount).flatMap {nodes(root.getChild(it))}
    private suspend fun waitFor(label:String,condition:()->Boolean) {
        try {withTimeout(40000) {while(!condition()) delay(40)}}
        catch(e:TimeoutCancellationException) {error("Timed out: $label; "+nodes(instrumentation.uiAutomation.rootInActiveWindow).mapNotNull {it.text?.toString()}.take(30).joinToString(" | "))}
    }
    private fun action(all:List<AccessibilityNodeInfo>,text:String):AccessibilityNodeInfo?=all.asSequence().filter {
        it.isVisibleToUser && (it.text?.toString()?.equals(text,true)==true || it.contentDescription?.toString()?.equals(text,true)==true)
    }.mapNotNull {label->generateSequence(label) {it.parent}.firstOrNull {it.isClickable && it.isEnabled && it.isVisibleToUser}}.firstOrNull()
    private fun click(text:String) {
        var direction=AccessibilityNodeInfo.ACTION_SCROLL_FORWARD
        repeat(100) {
            val all=nodes(instrumentation.uiAutomation.rootInActiveWindow)
            if(action(all,text)?.performAction(AccessibilityNodeInfo.ACTION_CLICK)==true) {instrumentation.waitForIdleSync(); return}
            if(all.firstOrNull {it.isScrollable && it.isVisibleToUser && it.className?.toString()?.contains("EditText")!=true}?.performAction(direction)!=true)
                direction=if(direction==AccessibilityNodeInfo.ACTION_SCROLL_FORWARD) AccessibilityNodeInfo.ACTION_SCROLL_BACKWARD else AccessibilityNodeInfo.ACTION_SCROLL_FORWARD
            Thread.sleep(60)
        }; error("Local folder control unavailable: $text; "+nodes(instrumentation.uiAutomation.rootInActiveWindow).mapNotNull {it.text?.toString()}.take(35).joinToString(" | "))
    }
    private fun pickerVisible()=instrumentation.uiAutomation.rootInActiveWindow?.packageName?.toString()?.contains("documentsui")==true
    private suspend fun chooseFolder() {
        waitFor("system tree picker") {pickerVisible()}
        fun fixtureSelected():Boolean {
            val all=nodes(instrumentation.uiAutomation.rootInActiveWindow)
            return all.any {it.text?.toString()=="Files from iConnect test copies"} && all.any {it.text?.toString()=="simu.txt"} && action(all,"Use this folder")!=null
        }
        if(!fixtureSelected()) {
            if(action(nodes(instrumentation.uiAutomation.rootInActiveWindow),"iConnect test copies")==null) click("Show roots")
            click("iConnect test copies")
        }
        waitFor("writable fixture root") {fixtureSelected()}
        click("Use this folder")
        waitFor("picker permission or review") {nodes(instrumentation.uiAutomation.rootInActiveWindow).any {it.text?.toString()?.let {text->text.equals("Allow",true) || text=="Confirm local folder creation"}==true}}
        if(nodes(instrumentation.uiAutomation.rootInActiveWindow).any {it.text?.toString()?.equals("Allow",true)==true}) click("Allow")
    }
    @Test fun nativeFolderNamePickerCancelApproveCreateAndReceiptRemovalPreserveOriginals():Unit=runBlocking {
        val fixture=requireNotNull(context.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"fixture",null,Bundle().apply {putBoolean("pickerRoot",true)}))
        val tree=Uri.parse(fixture.getString("tree")); var model:PhoneController?=null; var approvalsBefore:List<AndroidStorageGrant>?=null
        fun children()=context.contentResolver.query(Docs.buildChildDocumentsUriUsingTree(tree,fixture.getString("root")),arrayOf(Docs.Document.COLUMN_DISPLAY_NAME),null,null,null)!!.use {cursor->buildList {while(cursor.moveToNext()) add(cursor.getString(0))}}
        val file=Docs.buildDocumentUriUsingTree(tree,fixture.getString("file")); val original=context.contentResolver.openInputStream(file)!!.use {it.readBytes()}
        UiExistingApproval(context,tree,fixture.getInt("flags")).use { existing ->
        try {ActivityScenario.launch(MainActivity::class.java).use {scenario->
            lateinit var browser:PhoneLocalBrowser
            scenario.onActivity {activity->model=(activity.application as IConnectApplication).controller(); approvalsBefore=requireNotNull(model).state.value.phoneShares.toList(); check(approvalsBefore!!.any {it.id==existing.grantId}); browser=requireNotNull(model).localFiles; browser.add(tree,fixture.getInt("flags"))}
            waitFor("local root") {!browser.state.value.busy && browser.state.value.rows.any {it.entry.grantId==browser.state.value.locations.firstOrNull {location->location.tree==tree}?.id}}
            click("On this phone · local folders"); click("Open local folder")
            waitFor("source folder") {!browser.state.value.busy && browser.state.value.breadcrumbs.size==1}
            suspend fun start(name:String) {
                click("Create folder here…")
                waitFor("folder name dialog") {val all=nodes(instrumentation.uiAutomation.rootInActiveWindow); all.any {it.text?.toString()=="Create a folder here?"} && all.any {it.className?.toString()=="android.widget.EditText"}}
                val edit=nodes(instrumentation.uiAutomation.rootInActiveWindow).first {it.className?.toString()=="android.widget.EditText"}
                assertTrue(edit.performAction(AccessibilityNodeInfo.ACTION_SET_TEXT,Bundle().apply {putCharSequence(AccessibilityNodeInfo.ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE,name)}))
                waitFor("entered folder name") {nodes(instrumentation.uiAutomation.rootInActiveWindow).any {it.className?.toString()=="android.widget.EditText" && it.text?.toString()==name}}
                instrumentation.waitForIdleSync(); click("Approve folder access…")
            }
            val manager=requireNotNull(browser.folders); val before=children()
            suspend fun reopen() {
                click("Local folders"); waitFor("local roots") {!browser.state.value.busy && browser.state.value.breadcrumbs.isEmpty() && browser.state.value.rows.size==1}
                click("Open local folder"); waitFor("local parent") {!browser.state.value.busy && browser.state.value.breadcrumbs.size==1}
            }
            start("UI cancelled folder"); waitFor("first picker") {pickerVisible()}; instrumentation.sendKeyDownUpSync(KeyEvent.KEYCODE_BACK); instrumentation.waitForIdleSync()
            waitFor("canceled picker") {!pickerVisible()}; assertNull(manager.state.value.review); assertEquals(before,children())
            reopen()
            start("UI kept folder"); chooseFolder(); waitFor("creation review") {manager.state.value.review!=null && !manager.state.value.busy}
            assertEquals("UI kept folder",manager.state.value.review!!.name); click("Keep current folder"); waitFor("cancel review") {manager.state.value.review==null && !manager.state.value.busy}; assertEquals(before,children())
            reopen()
            start("UI created folder"); chooseFolder(); waitFor("final creation review") {manager.state.value.review?.name=="UI created folder" && !manager.state.value.busy}
            click("Create folder"); waitFor("durable local result") {!manager.state.value.busy && manager.state.value.receipts.any {it.name=="UI created folder" && it.phase==PhoneLocalFolderPhase.CREATED}}
            assertEquals(1,children().count {it=="UI created folder"}); assertEquals(requireNotNull(approvalsBefore).toSet(),requireNotNull(model).state.value.phoneShares.toSet()); assertFalse(requireNotNull(model).state.value.connected)
            click("Remove folder receipt…"); click("Keep receipt"); assertTrue(manager.state.value.receipts.any {it.name=="UI created folder"})
            click("Remove folder receipt…"); click("Remove receipt"); waitFor("removed metadata only") {manager.state.value.receipts.none {it.name=="UI created folder"}}
            assertEquals(1,children().count {it=="UI created folder"}); assertArrayEquals(original,context.contentResolver.openInputStream(file)!!.use {it.readBytes()})
        }} finally {
            model?.let {controller->withContext(Dispatchers.Main) {controller.setUiVisible(true)}
                controller.localFiles.folders?.let {manager->while(manager.state.value.busy) delay(15); manager.state.value.receipts.toList().forEach {manager.removeReceipt(it.id).join()}}
                withContext(Dispatchers.Main) {controller.localFiles.state.value.locations.filter {it.tree==tree}.forEach {controller.localFiles.remove(it.id)}}
                waitFor("shortcut cleanup") {!controller.localFiles.state.value.busy && controller.localFiles.state.value.locations.none {it.tree==tree}}
                val closing=withContext(Dispatchers.Main) {controller.close()}; closing.join(); withContext(Dispatchers.Main) {(context.applicationContext as IConnectApplication).releaseClosed(controller)}
            }
            context.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"dispose",fixture.getString("root"),null)
        }
        }
    }
}
