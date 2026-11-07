package com.iconnect.proof

import android.app.Application
import android.content.Context
import android.net.Uri
import android.os.Bundle
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
import com.iconnect.protocol.*
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

/** Actual Compose controls plus real private storage and SAF fixtures; no simulated authentication. */
@RunWith(AndroidJUnit4::class)
class BatchTransfersUiTest {
    private val instrumentation get()=InstrumentationRegistry.getInstrumentation()
    private fun nodes(root:AccessibilityNodeInfo?):List<AccessibilityNodeInfo> =if(root==null) emptyList() else listOf(root)+(0 until root.childCount).flatMap {nodes(root.getChild(it))}
    private fun waitFor(label:String,condition:()->Boolean) {
        val end=System.currentTimeMillis()+30_000
        while(System.currentTimeMillis()<end) {if(condition()) return; Thread.sleep(25)}
        fail("Timed out: $label")
    }
    private fun find(text:String):AccessibilityNodeInfo {
        val end=System.currentTimeMillis()+15_000; var direction=AccessibilityNodeInfo.ACTION_SCROLL_FORWARD
        while(System.currentTimeMillis()<end) {
            val all=nodes(instrumentation.uiAutomation.rootInActiveWindow)
            all.firstOrNull {it.text?.toString()==text && it.isVisibleToUser}?.let {return it}
            if(all.firstOrNull {it.isScrollable}?.performAction(direction)!=true)
                direction=if(direction==AccessibilityNodeInfo.ACTION_SCROLL_FORWARD) AccessibilityNodeInfo.ACTION_SCROLL_BACKWARD else AccessibilityNodeInfo.ACTION_SCROLL_FORWARD
            Thread.sleep(80)
        }
        error("Native control unavailable: $text\n"+nodes(instrumentation.uiAutomation.rootInActiveWindow).mapNotNull {it.text?.toString()}.take(45).joinToString(" | "))
    }
    private fun click(text:String) {
        val end=System.currentTimeMillis()+15_000
        while(System.currentTimeMillis()<end) {
            var node:AccessibilityNodeInfo?=find(text)
            while(node!=null && !node.isClickable) node=node.parent
            if(node?.isEnabled==true && node.performAction(AccessibilityNodeInfo.ACTION_CLICK)) {instrumentation.waitForIdleSync(); return}
            Thread.sleep(80)
        }
        fail("Native control did not become enabled: $text\n"+nodes(instrumentation.uiAutomation.rootInActiveWindow).mapNotNull {it.text?.toString()}.take(45).joinToString(" | "))
    }
    @Test fun received_history_cancel_retirement_and_closed_reset_use_the_native_reviews()=runBlocking {
        val base=instrumentation.targetContext; val prefix="batch-receiver-ui-"+StorageRules.token()
        val root=File(base.filesDir,prefix).apply {check(mkdir())}
        val app=object:Application() {
            init {attachBaseContext(base)}
            override fun getFilesDir()=root
            override fun getCacheDir()=File(root,"cache").apply {mkdirs()}
            override fun getApplicationContext():Context=this
            override fun getSharedPreferences(name:String,mode:Int)=base.getSharedPreferences(prefix+name,mode)
        }
        val data=requireNotNull(base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"fixture",null,null))
        val tree=Uri.parse(data.getString("tree")); val policy=AndroidOwnerPolicy(app)
        val approval=TestPeer.approve(policy,TestPeer.key()); val grants=AndroidStorageGrants(app,policy)
        val grant=grants.add(approval,tree,data.getInt("flags"),"Generated receiving folder"); grants.update(grant.id,12)
        val incoming=AndroidIncomingUploads(app,grants); val service=AndroidFileService(app,grants,incoming=incoming)
        var model:PhoneController?=null; var defaultModel:PhoneController?=null
        try {
            suspend fun call(request:FileRequest):FileResponse =service.execute(approval.id,request.toBuilder().setRequestId(StorageRules.token()).build()).also {assertEquals(it.message,FileStatus.FILE_STATUS_OK,it.status)}
            val target=call(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_CONTEXT).build()).entriesList.single()
            val epoch=call(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_BATCH_CONTEXT).build()).mutationEpoch
            val manifest=UploadBatchManifest.newBuilder().setCollision(UploadCollision.UPLOAD_KEEP_BOTH).apply {
                repeat(70) {addItems(UploadBatchItem.newBuilder().setParent(-1).setName("Generated-$it").setIsDirectory(true))}
            }.build()
            val header=UploadBatchRules.header(MutationRules.newId(),epoch,target.grantId,manifest)
            call(FileRequest.newBuilder().setOperation(FileOperation.BEGIN_UPLOAD_BATCH).setDestinationId(target.id).setTransferId(header.id).setMutationEpoch(epoch).setBatchHeader(header).build())
            assertEquals(UploadBatchPlanPhase.UPLOAD_PLAN_CANCELED,call(FileRequest.newBuilder().setOperation(FileOperation.STOP_UPLOAD_BATCH).setTransferId(header.id).setMutationEpoch(epoch).build()).batch.phase)
            service.close(); incoming.shutdown(); grants.batchPlans.shutdown()
            val parent=File(root,"incoming-batch-plans/${header.id}.json"); val before=parent.readBytes()
            lateinit var controller:PhoneController
            instrumentation.runOnMainSync {
                controller=PhoneController(app) {_,_->object:NetworkWatch {override fun start() {}; override fun stop() {}}}; model=controller; controller.setUiVisible(true)
            }
            waitFor("saved incoming plan") {controller.state.value.incomingBatchPlans.size==1}
            ActivityScenario.launch(MainActivity::class.java).use {scenario->
                scenario.onActivity {activity->
                    defaultModel=(activity.application as IConnectApplication).controller()
                    activity.setContent {MaterialTheme {val state by controller.state.collectAsState()
                        Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp)) {BatchTransfersPanel(controller,state) {}}
                    }}
                }
                click("Review receipt retirement…"); click("Cancel"); assertArrayEquals(before,parent.readBytes())
                assertFalse(controller.state.value.incomingBatchPlans.single().retired)
                click("Review receipt retirement…"); click("Retire detailed receipts")
                waitFor("compact received receipt") {controller.state.value.incomingBatchPlans.single().retired}
                find("Compact receipt retained. Detailed item lookup is unavailable.")
                assertEquals(header.id,controller.state.value.incomingBatchPlans.single().id)
                assertEquals(UploadBatchPlanPhase.UPLOAD_PLAN_CANCELED,controller.state.value.incomingBatchPlans.single().phase)
                click("Review closed-history reset…"); click("Cancel"); assertEquals(header.id,controller.state.value.incomingBatchPlans.single().id)
                click("Review closed-history reset…"); click("Preserve and reset")
                waitFor("new receiver history context") {controller.state.value.incomingBatchPlans.isEmpty()}
                assertTrue(root.listFiles().orEmpty().filter {it.name.startsWith("batch-plan-backup-")}.any {File(it,"${header.id}.json").isFile})
                assertTrue(controller.state.value.phoneShares.single().permissions and 12==12); assertFalse(controller.state.value.authenticated)
            }
        } finally {
            service.close(); incoming.shutdown(); grants.batchPlans.shutdown()
            model?.let {controller->lateinit var closing:Job; instrumentation.runOnMainSync {closing=controller.close()}; closing.join()}
            defaultModel?.let {controller->lateinit var closing:Job; instrumentation.runOnMainSync {closing=controller.close()}; closing.join(); instrumentation.runOnMainSync {(base.applicationContext as IConnectApplication).releaseClosed(controller)}}
            AndroidStorageGrants(app,policy).all().forEach {AndroidStorageGrants(app,policy).remove(it.id)}
            base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"dispose",data.getString("root"),null)
            listOf("iconnect.proof","iconnect.storage","iconnect.saf-access").forEach {base.deleteSharedPreferences(prefix+it)}
            check(root.canonicalFile.parentFile==base.filesDir.canonicalFile && root.name==prefix); root.deleteRecursively()
        }
    }
    @Test fun selection_plan_queue_results_and_reviewed_history_preserve_originals_without_sharing()=runBlocking {
        val base=instrumentation.targetContext; val prefix="batch-ui-"+StorageRules.token()
        val root=File(base.filesDir,prefix).apply {check(mkdir())}
        val app=object:Application() {
            init {attachBaseContext(base)}
            override fun getFilesDir()=root
            override fun getCacheDir()=File(root,"cache").apply {mkdirs()}
            override fun getApplicationContext():Context=this
            override fun getSharedPreferences(name:String,mode:Int)=base.getSharedPreferences(prefix+name,mode)
        }
        val data=requireNotNull(base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"fixture",null,Bundle().apply {putInt("extraFiles",63); putBoolean("emptyFolder",true)}))
        val tree=Uri.parse(data.getString("tree")); val policy=AndroidOwnerPolicy(app); TestPeer.approve(policy,TestPeer.key())
        var model:PhoneController?=null; var defaultModel:PhoneController?=null
        try {
            lateinit var controller:PhoneController
            instrumentation.runOnMainSync {
                controller=PhoneController(app) {_,_->object:NetworkWatch {override fun start() {}; override fun stop() {}}}; model=controller
                controller.setUiVisible(true); controller.localFiles.add(tree,data.getInt("flags"))
            }
            waitFor("local roots") {!controller.localFiles.state.value.busy && controller.localFiles.state.value.rows.size==1}
            val selectedRoot=controller.localFiles.state.value.rows.single().entry
            ActivityScenario.launch(MainActivity::class.java).use {scenario->
                scenario.onActivity {activity->
                    defaultModel=(activity.application as IConnectApplication).controller()
                    activity.setContent {MaterialTheme {
                        val state by controller.state.collectAsState(); var local by remember {mutableStateOf(true)}
                        if(local) LocalFilesScreen(controller.localFiles,stageBatch=controller::stageUploadBatch,
                            preparationBusy=state.privateOutgoingBatches.any {it.active},back={local=false})
                        else Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp)) {BatchTransfersPanel(controller,state) {local=true}}
                    }}
                }
                waitFor("local roots displayed") {!controller.localFiles.state.value.busy && controller.localFiles.state.value.rows.size==1}
                click("Select folder for batch: ${selectedRoot.name}"); click("Review selected batch…"); click("Cancel")
                assertTrue(controller.state.value.privateOutgoingBatches.isEmpty())
                click("Review selected batch…"); click("Skip existing names"); click("Prepare private batch")
                waitFor("private snapshot") {controller.state.value.privateOutgoingBatches.singleOrNull()?.let {!it.active && it.phase==PhoneBatchSourcePhase.READY}==true}
                val source=controller.state.value.privateOutgoingBatches.single()
                assertEquals(selectedRoot.name,source.label); assertEquals(UploadCollision.UPLOAD_SKIP,controller.inspectPrivateBatch(source.id).collision)
                assertTrue(source.items>50); assertTrue(controller.state.value.phoneShares.isEmpty()); assertFalse(controller.state.value.authenticated)
                click("Back to devices"); click("Review batch plan…"); click("Next plan items"); click("Previous plan items"); click("Review queueing batch…"); click("Cancel")
                assertTrue(controller.state.value.outgoingBatchSends.isEmpty())
                click("Review batch plan…"); click("Review queueing batch…"); click("Queue batch")
                waitFor("queued snapshot") {controller.state.value.outgoingBatchSends.size==1}
                val id=controller.state.value.outgoingBatchSends.single().id
                click("Review item results…"); click("Next result items"); find("51. "+controller.batchResultPage(id,50).first().name+" · Not processed"); click("Done")
                click("Review stopping batch…"); click("Cancel"); assertEquals(PhoneBatchSendingPhase.READY,controller.state.value.outgoingBatchSends.single().phase)
                click("Review stopping batch…"); click("Stop batch")
                waitFor("offline local stop") {controller.state.value.outgoingBatchSends.single().phase==PhoneBatchSendingPhase.STOPPED}
                click("Review archiving send…"); click("Cancel"); assertFalse(controller.state.value.outgoingBatchSends.single().archived)
                click("Review archiving send…"); click("Archive send"); waitFor("archived") {controller.state.value.outgoingBatchSends.single().archived}
                click("Review restoring send…"); click("Restore send"); waitFor("restored") {!controller.state.value.outgoingBatchSends.single().archived}
                click("Review archiving send…"); click("Archive send"); waitFor("archived again") {controller.state.value.outgoingBatchSends.single().archived}
                click("Review private-copy removal…"); click("Cancel"); assertEquals(source.id,controller.state.value.privateOutgoingBatches.single().id)
                click("Review private-copy removal…"); click("Remove private copies"); waitFor("private copies removed") {controller.state.value.privateOutgoingBatches.isEmpty()}
                assertTrue(controller.state.value.outgoingBatchSends.single().archived)
                click("Review item results…"); find("1. Item 1 · Not processed"); click("Done")
                click("Review history purge…"); click("Cancel"); assertEquals(id,controller.state.value.outgoingBatchSends.single().id)
                click("Review history purge…"); click("Purge history"); waitFor("history purged") {controller.state.value.outgoingBatchSends.isEmpty()}
                assertTrue(controller.state.value.phoneShares.isEmpty()); assertFalse(controller.state.value.authenticated)
                instrumentation.runOnMainSync {controller.localFiles.browse()}
                waitFor("original local root") {!controller.localFiles.state.value.busy && controller.localFiles.state.value.breadcrumbs.isEmpty()}
                instrumentation.runOnMainSync {controller.localFiles.browse(controller.localFiles.state.value.rows.single().entry)}
                waitFor("originals still readable") {!controller.localFiles.state.value.busy && controller.localFiles.state.value.count>50}
                assertTrue(controller.localFiles.state.value.rows.any {it.entry.name.startsWith("item-")})
            }
        } finally {
            model?.let {controller->lateinit var closing:Job; instrumentation.runOnMainSync {closing=controller.close()}; closing.join()}
            defaultModel?.let {controller->lateinit var closing:Job; instrumentation.runOnMainSync {closing=controller.close()}; closing.join(); instrumentation.runOnMainSync {(base.applicationContext as IConnectApplication).releaseClosed(controller)}}
            LocalDocumentLocations(app).all().forEach {LocalDocumentLocations(app).remove(it.id)}
            base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"dispose",data.getString("root"),null)
            listOf("iconnect.proof","iconnect.storage","iconnect.saf-access").forEach {base.deleteSharedPreferences(prefix+it)}
            check(root.canonicalFile.parentFile==base.filesDir.canonicalFile && root.name==prefix); root.deleteRecursively()
        }
    }
}
