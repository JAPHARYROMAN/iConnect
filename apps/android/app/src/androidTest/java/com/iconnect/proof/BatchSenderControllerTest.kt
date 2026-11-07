package com.iconnect.proof

import android.app.Application
import android.content.Context
import android.net.Uri
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

@RunWith(AndroidJUnit4::class)
class BatchSenderControllerTest {
    @Test fun queueAdmissionAndOwnerCleanupKeepMainResponsiveAndProtectCurrentSources()=runBlocking {
        val instrumentation=InstrumentationRegistry.getInstrumentation(); val base=instrumentation.targetContext
        val prefix="batch-controller-"+StorageRules.token(); val root=File(base.filesDir,prefix).apply {check(mkdir())}
        val app=object:Application() {
            init {attachBaseContext(base)}
            override fun getFilesDir()=root
            override fun getCacheDir()=File(root,"cache").apply {mkdirs()}
            override fun getApplicationContext():Context=this
            override fun getSharedPreferences(name:String,mode:Int)=base.getSharedPreferences(prefix+name,mode)
        }
        val data=requireNotNull(base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"fixture",null,null)); val tree=Uri.parse(data.getString("tree"))
        val policy=AndroidOwnerPolicy(app); TestPeer.approve(policy,TestPeer.key())
        var model:PhoneController?=null; val release=CountDownLatch(1)
        fun main(action:()->Unit)=instrumentation.runOnMainSync(action)
        try {
            lateinit var controller:PhoneController
            main {
                controller=PhoneController(app) {_,_->object:NetworkWatch {override fun start() {}; override fun stop() {}}}; model=controller
                controller.setUiVisible(true); controller.localFiles.add(tree,data.getInt("flags"))
            }
            withTimeout(20_000) {while(controller.localFiles.state.value.busy || controller.localFiles.state.value.rows.size!=1) delay(15)}
            main {controller.stageUploadBatch(listOf(controller.localFiles.state.value.rows.single().entry),com.iconnect.protocol.UploadCollision.UPLOAD_KEEP_BOTH)}
            withTimeout(30_000) {while(controller.state.value.privateOutgoingBatches.singleOrNull()?.let {!it.active && it.phase==PhoneBatchSourcePhase.READY}!=true) delay(15)}
            instrumentation.waitForIdleSync()
            val source=controller.state.value.privateOutgoingBatches.single().id
            val held=CountDownLatch(1); val holding=AtomicBoolean(false); val returnedWhileHeld=AtomicBoolean(false)
            val blocker=launch(Dispatchers.IO) {synchronized(policy.gate) {
                holding.set(true); held.countDown()
                try {release.await(10,TimeUnit.SECONDS)} finally {holding.set(false)}
            }}
            try {
                assertTrue(held.await(5,TimeUnit.SECONDS))
                main {controller.queueOutgoingBatch(source); returnedWhileHeld.set(holding.get())}
            } finally {release.countDown(); blocker.join()}
            assertTrue("Queue admission must return to Main while private inventory is blocked.",returnedWhileHeld.get())
            withTimeout(20_000) {while(controller.state.value.outgoingBatchSends.isEmpty()) delay(15)}
            assertEquals(source,controller.state.value.outgoingBatchSends.single().source)
            assertEquals(PhoneBatchSendingPhase.READY,controller.state.value.outgoingBatchSends.single().phase)
            assertFalse(controller.state.value.authenticated); assertTrue(controller.state.value.phoneShares.isEmpty())
            val id=controller.state.value.outgoingBatchSends.single().id
            try {controller.reviewOutgoingBatchRemoval(source); fail("A current queued job must keep its private source.")} catch(_:IllegalStateException) {}
            assertTrue(controller.confirmOutgoingBatchHistory(controller.reviewOutgoingBatchHistory(id,PhoneBatchHistoryAction.ARCHIVE)))
            val removal=controller.reviewOutgoingBatchRemoval(source)
            assertTrue(controller.confirmOutgoingBatchHistory(controller.reviewOutgoingBatchHistory(id,PhoneBatchHistoryAction.RESTORE)))
            try {controller.removeOutgoingBatchReviewed(removal); fail("A restored current job must invalidate the source-removal action.")} catch(_:IllegalStateException) {}
            assertTrue(controller.confirmOutgoingBatchHistory(controller.reviewOutgoingBatchHistory(id,PhoneBatchHistoryAction.ARCHIVE)))
            controller.removeOutgoingBatchReviewed(removal)
            withTimeout(10_000) {while(controller.state.value.privateOutgoingBatches.isNotEmpty()) delay(15)}
            assertTrue(controller.state.value.outgoingBatchSends.single().archived); assertTrue(controller.state.value.phoneShares.isEmpty())
        } finally {
            release.countDown()
            model?.let {controller->lateinit var closing:Job; main {closing=controller.close()}; closing.join()}
            LocalDocumentLocations(app).all().forEach {LocalDocumentLocations(app).remove(it.id)}
            base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"dispose",data.getString("root"),null)
            listOf("iconnect.proof","iconnect.storage","iconnect.saf-access").forEach {base.deleteSharedPreferences(prefix+it)}
            check(root.canonicalFile.parentFile==base.filesDir.canonicalFile && root.name==prefix); root.deleteRecursively()
        }
    }
}
