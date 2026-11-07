package com.iconnect.proof

import androidx.test.ext.junit.runners.AndroidJUnit4
import com.iconnect.protocol.*
import kotlinx.coroutines.*
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.io.IOException

@RunWith(AndroidJUnit4::class)
class BatchReceiverHistoryTest {
    private suspend fun fixture(extras:Int=0,body:suspend (BatchSenderTest.Fixture)->Unit) {val f=BatchSenderTest.Fixture(extras=extras); try {f.initialize(); body(f)} finally {f.close()}}
    private fun request(h:UploadBatchHeader,op:FileOperation)=FileRequest.newBuilder().setOperation(op).setTransferId(h.id).setMutationEpoch(h.epoch)
    private fun header(f:BatchSenderTest.Fixture,id:String)=UploadBatchHeader.parseFrom(Rules.decode(f.sender.state.value.single {it.id==id}.header))
    private fun archive(f:BatchSenderTest.Fixture,id:String) {assertTrue(f.sender.confirmHistory(f.sender.reviewHistory(id,PhoneBatchHistoryAction.ARCHIVE)))}
    private fun finishRetirement(f:BatchSenderTest.Fixture,id:String) {repeat(260) {if(f.receiver.grants.batchPlans.continueRetirement(id)) return}; error("Receiver retirement did not finish")}
    private suspend fun canceled(f:BatchSenderTest.Fixture,epoch:String):UploadBatchHeader {
        val manifest=UploadBatchManifest.newBuilder().setCollision(UploadCollision.UPLOAD_KEEP_BOTH).addItems(UploadBatchItem.newBuilder().setParent(-1).setName("empty").setIsDirectory(true)).build()
        val h=UploadBatchRules.header(MutationRules.newId(),epoch,f.target.grantId,manifest)
        assertEquals(FileStatus.FILE_STATUS_OK,f.request(request(h,FileOperation.BEGIN_UPLOAD_BATCH).setDestinationId(f.target.id).setBatchHeader(h).build()).status)
        assertEquals(UploadBatchPlanPhase.UPLOAD_PLAN_CANCELED,f.request(request(h,FileOperation.STOP_UPLOAD_BATCH).build()).batch.phase); return h
    }
    @Test fun completedPlansReleaseActiveSlotsWhileTheirParentReceiptsRemain()=runBlocking {fixture {f->
        val first=f.queue(); repeat(6) {index->val id=if(index==0) first else f.sender.queue(f.source.id,anotherCopy=true); f.send(id)
            assertEquals(PhoneBatchSendingPhase.COMPLETED,f.sender.state.value.single {it.id==id}.phase); archive(f,id)}
        assertEquals(6,f.receiver.grants.batchPlans.all().size); assertEquals(0L,f.receiver.grants.batchPlans.reservedBytes(null)); f.receiver.restart(reopenGrants=true)
        assertTrue(f.receiver.grants.batchPlans.recoveryIssues().isEmpty()); assertEquals(6,f.receiver.grants.batchPlans.all().size)
    }}
    @Test fun removingItemHistoryKeepsFinalOutcomesAndNeverReplaysAnOldPlan()=runBlocking {fixture {f->
        val id=f.queue(); f.send(id); val h=header(f,id); val parent=f.receivedRoot(); val bytes=f.receiver.read("simu.txt",parent); val before=f.request(request(h,FileOperation.UPLOAD_BATCH_STATUS).build()).batch
        val review=f.receiver.grants.batchPlans.reviewRetirement(h.id); if(!f.receiver.grants.batchPlans.confirmRetirement(review)) finishRetirement(f,h.id)
        assertTrue(f.receiver.grants.batchPlans.all().single().retired); assertEquals(before.itemOutcomes,f.request(request(h,FileOperation.UPLOAD_BATCH_STATUS).build()).batch.itemOutcomes)
        assertEquals(FileStatus.NOT_FOUND,f.request(request(h,FileOperation.UPLOAD_BATCH_ITEM_STATUS).setBatchItemIndex(0).build()).status)
        assertEquals(UploadBatchPlanPhase.UPLOAD_PLAN_COMPLETED,f.request(request(h,FileOperation.BEGIN_UPLOAD_BATCH).setDestinationId(f.target.id).setBatchHeader(h).build()).batch.phase)
        f.receiver.restart(reopenGrants=true); assertTrue(f.receiver.grants.batchPlans.recoveryIssues().isEmpty()); assertTrue(f.receiver.grants.batchPlans.all().single().retired)
        assertEquals(before.itemOutcomes,f.request(request(h,FileOperation.STOP_UPLOAD_BATCH).build()).batch.itemOutcomes); assertArrayEquals(bytes,f.receiver.read("simu.txt",parent)); assertEquals(f.source,f.sources.state.value.single())
    }}
    @Test fun activePlansAndModifiedOrReusedReviewsCannotRemoveHistory()=runBlocking {fixture {f->
        val epoch=f.request(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_BATCH_CONTEXT).build()).mutationEpoch; val h=canceled(f,epoch); val review=f.receiver.grants.batchPlans.reviewRetirement(h.id)
        assertThrows(IllegalStateException::class.java) {f.receiver.grants.batchPlans.confirmRetirement(review.copy(metadataFiles=999))}; assertThrows(IllegalStateException::class.java) {f.receiver.grants.batchPlans.confirmRetirement(review)}
        val current=f.receiver.grants.batchPlans.reviewRetirement(h.id); assertTrue(f.receiver.grants.batchPlans.confirmRetirement(current)); assertThrows(IllegalStateException::class.java) {f.receiver.grants.batchPlans.confirmRetirement(current)}
        val id=f.queue(); f.send(id) {value->val reply=f.request(value); if(value.operation==FileOperation.BEGIN_UPLOAD_BATCH) throw IOException("Lost accepted reply"); reply}
        assertThrows(IllegalStateException::class.java) {f.receiver.grants.batchPlans.reviewRetirement(header(f,id).id)}
        try {f.receiver.grants.batchPlans.archiveHistory(closedOnly=true); fail("An active plan must keep its namespace.")} catch(_:IllegalStateException) {}
    }}
    @Test fun compactReceiptsFreeDetailedCapacityAndClosedResetRotatesTheReplayNamespace()=runBlocking {fixture {f->
        val epoch=f.request(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_BATCH_CONTEXT).build()).mutationEpoch; val receipts=mutableListOf<UploadBatchHeader>()
        repeat(AndroidBatchPlans.MAX_DETAILED_PLANS) {receipts.add(canceled(f,epoch))}
        val manifest=UploadBatchManifest.newBuilder().setCollision(UploadCollision.UPLOAD_KEEP_BOTH).addItems(UploadBatchItem.newBuilder().setParent(-1).setName("extra").setIsDirectory(true)).build()
        val h=UploadBatchRules.header(MutationRules.newId(),epoch,f.target.grantId,manifest); val begin=request(h,FileOperation.BEGIN_UPLOAD_BATCH).setDestinationId(f.target.id).setBatchHeader(h).build()
        assertEquals(FileStatus.RESOURCE_EXHAUSTED,f.request(begin).status)
        assertTrue(f.receiver.grants.batchPlans.confirmRetirement(f.receiver.grants.batchPlans.reviewRetirement(receipts.first().id))); assertEquals(FileStatus.FILE_STATUS_OK,f.request(begin).status)
        f.request(request(h,FileOperation.STOP_UPLOAD_BATCH).build()); f.receiver.grants.batchPlans.archiveHistory(closedOnly=true)
        assertTrue(f.receiver.grants.batchPlans.all().isEmpty()); assertNotEquals(epoch,f.request(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_BATCH_CONTEXT).build()).mutationEpoch); assertEquals(FileStatus.STALE_REFERENCE,f.request(begin).status)
    }}
    @Test fun bothMetadataPassesSurviveRestartAndADeletionBeforeItsCursorCheckpoint()=runBlocking {fixture(extras=63) {f->
        val id=f.queue(); f.send(id); val h=header(f,id); assertEquals(70,h.itemCount); val parent=f.receivedRoot(); val bytes=f.receiver.read("simu.txt",parent)
        assertFalse(f.receiver.grants.batchPlans.confirmRetirement(f.receiver.grants.batchPlans.reviewRetirement(h.id))); assertEquals(32,f.receiver.grants.batchPlans.all().single().retirementCursor)
        f.receiver.restart(reopenGrants=true); assertTrue(f.receiver.grants.batchPlans.recoveryIssues().isEmpty())
        while(f.receiver.grants.batchPlans.all().single().retirementCursor<102) assertFalse(f.receiver.grants.batchPlans.continueRetirement(h.id))
        f.receiver.service.close(); f.receiver.incoming.shutdown(); f.receiver.grants.batchPlans.shutdown()
        val file=File(f.receiver.root,"incoming-batch-plans/${h.id}.json"); file.writeText(JSONObject(file.readText()).put("retirementCursor",70).toString()); f.receiver.restart(reopenGrants=true)
        assertTrue(f.receiver.grants.batchPlans.recoveryIssues().isEmpty()); finishRetirement(f,h.id); assertTrue(f.receiver.grants.batchPlans.all().single().retired); assertArrayEquals(bytes,f.receiver.read("simu.txt",parent))
        f.receiver.restart(reopenGrants=true); assertTrue(f.receiver.grants.batchPlans.recoveryIssues().isEmpty()); assertTrue(f.receiver.grants.batchPlans.all().single().retired)
    }}
    @Test fun changedItemMetadataInvalidatesReviewBeforeRemovingAnyReceiptOrProviderFile()=runBlocking {fixture {f->
        val id=f.queue(); f.send(id); val h=header(f,id); val parent=f.receivedRoot(); val bytes=f.receiver.read("simu.txt",parent); val review=f.receiver.grants.batchPlans.reviewRetirement(h.id)
        val file=File(f.receiver.root,"incoming-batch-plans/${h.id}.0.json"); file.appendText(" "); assertThrows(IllegalStateException::class.java) {f.receiver.grants.batchPlans.confirmRetirement(review)}
        assertTrue(file.exists()); assertFalse(f.receiver.grants.batchPlans.all().single().retiring); assertArrayEquals(bytes,f.receiver.read("simu.txt",parent))
    }}
    @Test fun legacySchemaTwoRetainsItsOriginalAuthorityBeforeRetirementUpgrade()=runBlocking {fixture {f->
        val epoch=f.request(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_BATCH_CONTEXT).build()).mutationEpoch; val h=canceled(f,epoch)
        f.receiver.service.close(); f.receiver.incoming.shutdown(); f.receiver.grants.batchPlans.shutdown(); val file=File(f.receiver.root,"incoming-batch-plans/${h.id}.json"); val saved=JSONObject(file.readText()).put("schema",2)
        listOf("retiring","retired","retirementCursor","retirementBits").forEach {saved.remove(it)}; file.writeText(saved.toString()); f.receiver.restart(reopenGrants=true)
        assertTrue(f.receiver.grants.batchPlans.recoveryIssues().isEmpty()); assertEquals(h,f.request(request(h,FileOperation.UPLOAD_BATCH_STATUS).build()).batch.header)
        assertTrue(f.receiver.grants.batchPlans.confirmRetirement(f.receiver.grants.batchPlans.reviewRetirement(h.id))); assertEquals(3,JSONObject(file.readText()).getInt("schema"))
    }}
}
