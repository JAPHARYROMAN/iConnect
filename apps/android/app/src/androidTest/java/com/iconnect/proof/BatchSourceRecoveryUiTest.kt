package com.iconnect.proof

import android.app.Application
import android.content.Context
import android.net.Uri
import android.provider.DocumentsContract as Docs
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
import com.iconnect.protocol.UploadCollision
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

@RunWith(AndroidJUnit4::class)
class BatchSourceRecoveryUiTest {
    private val instrumentation get()=InstrumentationRegistry.getInstrumentation()
    private fun nodes(root:AccessibilityNodeInfo?):List<AccessibilityNodeInfo> =if(root==null) emptyList() else listOf(root)+(0 until root.childCount).flatMap {nodes(root.getChild(it))}
    private fun waitFor(label:String,condition:()->Boolean) {
        val end=System.currentTimeMillis()+30_000
        while(System.currentTimeMillis()<end) {if(condition()) return; Thread.sleep(25)}
        error("Timed out: $label\n"+nodes(instrumentation.uiAutomation.rootInActiveWindow).mapNotNull {it.text?.toString()}.take(35).joinToString(" | "))
    }
    private fun click(text:String) {
        val end=System.currentTimeMillis()+15_000; var direction=AccessibilityNodeInfo.ACTION_SCROLL_FORWARD
        while(System.currentTimeMillis()<end) {
            val all=nodes(instrumentation.uiAutomation.rootInActiveWindow); var node=all.firstOrNull {it.text?.toString()==text && it.isVisibleToUser}
            while(node!=null && !node.isClickable) node=node.parent
            if(node?.isEnabled==true && node.performAction(AccessibilityNodeInfo.ACTION_CLICK)) {instrumentation.waitForIdleSync(); return}
            if(all.firstOrNull {it.isScrollable}?.performAction(direction)!=true) direction=if(direction==AccessibilityNodeInfo.ACTION_SCROLL_FORWARD) AccessibilityNodeInfo.ACTION_SCROLL_BACKWARD else AccessibilityNodeInfo.ACTION_SCROLL_FORWARD
            Thread.sleep(80)
        }
        error("Native recovery control unavailable: $text\n"+nodes(instrumentation.uiAutomation.rootInActiveWindow).mapNotNull {it.text?.toString()}.take(35).joinToString(" | "))
    }
    @Test fun nativeSourceRecoveryCanCancelPreserveInspectAndRemoveWhileKeepingOriginalsSenderControlsAndFutureCopyReview():Unit=runBlocking {
        val base=instrumentation.targetContext; val prefix="source-recovery-ui-"+StorageRules.token(); val root=File(base.filesDir,prefix).apply {check(mkdir())}
        val app=object:Application() {
            init {attachBaseContext(base)}
            override fun getFilesDir()=root
            override fun getCacheDir()=File(root,"cache").apply {mkdirs()}
            override fun getApplicationContext():Context=this
            override fun getSharedPreferences(name:String,mode:Int)=base.getSharedPreferences(prefix+name,mode)
        }
        val data=requireNotNull(base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"fixture",null,null)); val tree=Uri.parse(data.getString("tree"))
        val original=Docs.buildDocumentUriUsingTree(tree,data.getString("file")); val originalBytes=base.contentResolver.openInputStream(original)!!.use {it.readBytes()}
        val policy=AndroidOwnerPolicy(app); TestPeer.approve(policy,TestPeer.key()); var model:PhoneController?=null; var defaultModel:PhoneController?=null
        fun main(action:()->Unit)=instrumentation.runOnMainSync(action)
        fun create():PhoneController {lateinit var result:PhoneController; main {result=PhoneController(app) {_,_->object:NetworkWatch {override fun start() {}; override fun stop() {}}}; result.setUiVisible(true)}; return result}
        suspend fun close(value:PhoneController) {lateinit var closing:Job; main {closing=value.close()}; closing.join()}
        try {
            var controller=create(); model=controller
            main {controller.localFiles.add(tree,data.getInt("flags"))}; waitFor("generated local folder") {!controller.localFiles.state.value.busy && controller.localFiles.state.value.rows.size==1}
            main {controller.stageUploadBatch(listOf(controller.localFiles.state.value.rows.single().entry),UploadCollision.UPLOAD_KEEP_BOTH)}
            waitFor("private snapshot") {controller.state.value.privateOutgoingBatches.singleOrNull()?.let {!it.active && it.phase==PhoneBatchSourcePhase.READY}==true}
            val source=controller.state.value.privateOutgoingBatches.single(); val manifest=controller.inspectPrivateBatch(source.id); val index=manifest.itemsList.indexOfFirst {it.name=="simu.txt"}
            val privatePart=File(root,"outgoing-batch-copies/${source.id}/$index.bin"); val privateBytes=privatePart.readBytes()
            main {controller.queueOutgoingBatch(source.id)}; waitFor("local queued send") {controller.state.value.outgoingBatchSends.size==1}
            val id=controller.state.value.outgoingBatchSends.single().id; close(controller); model=null
            val damaged=File(root,"outgoing-batch-copies/${source.id}.json"); val unreadable="generated unreadable snapshot ownership"; damaged.writeText(unreadable)
            controller=create(); model=controller; waitFor("source recovery issue") {controller.state.value.transferRecovery.any {it.area=="Private batches"}}
            ActivityScenario.launch(MainActivity::class.java).use {scenario->
                scenario.onActivity {activity->defaultModel=(activity.application as IConnectApplication).controller(); activity.setContent {MaterialTheme {
                    val state by controller.state.collectAsState()
                    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp)) {BatchSourceRecoveryPanel(controller,state)}
                }}}
                click("Pause batch work and review snapshot recovery…"); click("Cancel"); assertEquals(unreadable,damaged.readText())
                assertTrue(controller.state.value.batchSourceRecoveryArchives.isEmpty())
                click("Pause batch work and review snapshot recovery…"); click("Preserve snapshots and reset")
                waitFor("preserved snapshots and empty preparation store") {controller.state.value.batchSourceRecoveryArchives.size==1 && controller.state.value.transferRecovery.none {it.area=="Private batches"}}
                val backup=controller.state.value.batchSourceRecoveryArchives.single().id; val saved=File(root,"outgoing-batch-source-backup-$backup/${source.id}.json")
                val savedPart=File(root,"outgoing-batch-source-backup-$backup/${source.id}/$index.bin")
                assertEquals(unreadable,saved.readText()); assertEquals(id,controller.state.value.outgoingBatchSends.single().id); assertTrue(controller.state.value.privateOutgoingBatches.isEmpty())
                assertArrayEquals(privateBytes,savedPart.readBytes()); assertTrue(controller.state.value.privateOutgoingBytes>=privateBytes.size)
                click("Inspect preserved snapshot entries…")
                waitFor("bounded read-only inventory") {
                    val all=nodes(instrumentation.uiAutomation.rootInActiveWindow)
                    if(all.any {it.text?.toString()=="${source.id}.json · ${unreadable.toByteArray().size} bytes"}) true
                    else {all.firstOrNull {it.isScrollable}?.performAction(AccessibilityNodeInfo.ACTION_SCROLL_FORWARD); false}
                }
                click("Done"); click("Review preserved snapshot removal…"); click("Cancel"); assertEquals(unreadable,saved.readText())
                click("Review preserved snapshot removal…"); click("Remove reviewed snapshots")
                waitFor("reviewed snapshot archive removed") {controller.state.value.batchSourceRecoveryArchives.isEmpty() && controller.state.value.privateOutgoingBytes==0L}
                assertFalse(saved.exists()); assertFalse(savedPart.exists()); assertArrayEquals(originalBytes,base.contentResolver.openInputStream(original)!!.use {it.readBytes()})
                assertTrue(controller.state.value.phoneShares.isEmpty()); assertFalse(controller.state.value.authenticated)
                waitFor("audited source archive removal") {controller.state.value.activity.entries.any {it.kind==PhoneActivityKind.SOURCE_ARCHIVE_CLEANUP && it.outcome==PhoneActivityOutcome.COMPLETED}}
                controller.flushActivity(); assertTrue(controller.state.value.activity.entries.any {it.kind==PhoneActivityKind.SOURCE_STORE_RESET && it.outcome==PhoneActivityOutcome.COMPLETED})
                val activityJson=File(root,"local-activity/activity.json").readText(); assertFalse(activityJson.contains(source.id)); assertFalse(activityJson.contains(unreadable)); assertFalse(activityJson.contains("simu.txt"))
            }
            close(controller); model=null; controller=create(); model=controller
            main {controller.localFiles.browse()}
            waitFor("reopened local folder") {!controller.localFiles.state.value.busy && controller.localFiles.state.value.rows.size==1}
            main {controller.stageUploadBatch(listOf(controller.localFiles.state.value.rows.single().entry),UploadCollision.UPLOAD_KEEP_BOTH)}
            waitFor("new snapshot retains additional-copy review") {controller.state.value.privateOutgoingBatches.singleOrNull()?.let {!it.active && it.phase==PhoneBatchSourcePhase.READY}==true}
            val next=controller.state.value.privateOutgoingBatches.single(); assertTrue(next.mayHaveReceiverCopies)
            assertThrows(IllegalStateException::class.java) {runBlocking {controller.queueReviewedBatch(next.id,false)}}
        } finally {
            model?.let {close(it)}
            defaultModel?.let {close(it); main {(base.applicationContext as IConnectApplication).releaseClosed(it)}}
            LocalDocumentLocations(app).all().forEach {LocalDocumentLocations(app).remove(it.id)}
            base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"dispose",data.getString("root"),null)
            listOf("iconnect.proof","iconnect.storage","iconnect.saf-access").forEach {base.deleteSharedPreferences(prefix+it)}
            check(root.canonicalFile.parentFile==base.filesDir.canonicalFile && root.name==prefix); root.deleteRecursively()
        }
    }
}
