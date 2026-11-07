package com.iconnect.proof

import android.provider.DocumentsContract as Docs
import android.os.Bundle
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.google.protobuf.ByteString
import com.iconnect.protocol.*
import kotlinx.coroutines.*
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.io.RandomAccessFile

@RunWith(AndroidJUnit4::class)
class BatchFileTest {
    private fun plan(bytes:ByteArray,files:Int=1,collision:UploadCollision=UploadCollision.UPLOAD_KEEP_BOTH)=UploadBatchManifest.newBuilder().setCollision(collision)
        .addItems(UploadBatchItem.newBuilder().setParent(-1).setName("batch-root").setIsDirectory(true)).apply {
            repeat(files) {index->addItems(UploadBatchItem.newBuilder().setParent(0).setName("file-$index.bin").setSize(bytes.size.toLong()).setSha256(ByteString.copyFrom(Rules.digest(bytes))))}
        }.build()
    private fun request(h:UploadBatchHeader,op:FileOperation,index:Int?=null)=FileRequest.newBuilder().setOperation(op).setTransferId(h.id).setMutationEpoch(h.epoch).also {if(index!=null) it.setBatchItemIndex(index)}
    private suspend fun accepted(f:IncomingUploadsTest.Fixture,manifest:UploadBatchManifest):UploadBatchHeader {
        f.initialize(); val epoch=f.call(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_BATCH_CONTEXT).build()).mutationEpoch
        val h=UploadBatchRules.header(MutationRules.newId(),epoch,f.grant.id,manifest)
        f.call(request(h,FileOperation.BEGIN_UPLOAD_BATCH).setDestinationId(f.target).setBatchHeader(h).build())
        val bytes=UploadBatchRules.encode(manifest); var offset=0
        while(offset<bytes.size) {val count=minOf(StorageRules.RANGE,bytes.size-offset); f.call(request(h,FileOperation.WRITE_UPLOAD_MANIFEST).setOffset(offset.toLong()).setContent(ByteString.copyFrom(bytes,offset,count)).build()); offset+=count}
        f.call(request(h,FileOperation.SEAL_UPLOAD_BATCH).build()); return h
    }
    private suspend fun folder(f:IncomingUploadsTest.Fixture,h:UploadBatchHeader,index:Int=0):String {
        val result=f.call(request(h,FileOperation.CREATE_BATCH_DIRECTORY,index).build()).batchItem
        assertEquals(UploadBatchItemPhase.BATCH_ITEM_SUCCEEDED,result.phase)
        return f.children().entries.single {it.value==result.resultingName}.key
    }
    private suspend fun prepare(f:IncomingUploadsTest.Fixture,h:UploadBatchHeader,index:Int=1)=f.call(request(h,FileOperation.PREPARE_BATCH_FILE,index).build())
    private suspend fun status(f:IncomingUploadsTest.Fixture,h:UploadBatchHeader,index:Int=1)=f.call(request(h,FileOperation.UPLOAD_BATCH_ITEM_STATUS,index).build())
    private suspend fun write(f:IncomingUploadsTest.Fixture,h:UploadBatchHeader,bytes:ByteArray,index:Int=1,start:Int=0) {
        var offset=start
        while(offset<bytes.size) {val count=minOf(StorageRules.RANGE,bytes.size-offset); val reply=f.call(request(h,FileOperation.WRITE_BATCH_FILE,index).setOffset(offset.toLong()).setContent(ByteString.copyFrom(bytes,offset,count)).build()); offset+=count; assertEquals(offset.toLong(),reply.upload.offset)}
    }
    private suspend fun finish(f:IncomingUploadsTest.Fixture,h:UploadBatchHeader,index:Int=1):FileResponse {
        f.call(request(h,FileOperation.FINISH_BATCH_FILE,index).build()); withTimeout(20_000) {f.grants.batchPlans.whenFilesIdle()}; return status(f,h,index)
    }
    private fun journal(f:IncomingUploadsTest.Fixture,h:UploadBatchHeader,index:Int=1)=File(f.root,"incoming-batch-plans/${h.id}.$index.upload.json")
    private fun privateParts(f:IncomingUploadsTest.Fixture)=File(f.root,"incoming-batch-parts").listFiles().orEmpty().filter {it.name.endsWith(".part")}
    private fun partDescriptors(f:IncomingUploadsTest.Fixture)=File("/proc/self/fd").listFiles().orEmpty().count {file->
        runCatching {android.system.Os.readlink(file.path)}.getOrDefault("").let {it.startsWith(f.root.canonicalPath+"/") && it.substringBefore(" (deleted)").endsWith(".part")}}
    private fun original(peer:String,h:UploadBatchHeader,manifest:UploadBatchManifest,index:Int)=manifest.getItems(index).let {UploadInfo.newBuilder().setId(UploadBatchRules.childId(peer,h,index,false)).setName(it.name).setSize(it.size).setSha256(it.sha256).setCollision(manifest.collision).build()}
    private suspend fun stopPlan(f:IncomingUploadsTest.Fixture,h:UploadBatchHeader):UploadBatchReceipt {
        repeat(6) {val reply=f.call(request(h,FileOperation.STOP_UPLOAD_BATCH).build()).batch; if(UploadBatchRules.terminal(reply.phase)) return reply; withTimeout(20_000) {f.grants.batchPlans.whenFilesIdle()}}
        error("Fixture batch did not stop.")
    }
    @Test fun batchReceiverMeasuresSavedRangesAndClearsRateOnReconnect() = runBlocking {
        val f = IncomingUploadsTest.Fixture()
        try {
            val range = StorageRules.RANGE
            val limit = 64L * 1024
            val bytes = ByteArray(4 * range + 7) { (it % 251).toByte() }
            val h = accepted(f, plan(bytes))
            val parent = folder(f, h)
            prepare(f, h)
            fun progress() = f.grants.batchPlans.readFileProgress(f.approval.id, f.approval.epoch, limit).single()
            suspend fun writeRange(offset: Int) {
                f.call(request(h, FileOperation.WRITE_BATCH_FILE, 1).setOffset(offset.toLong())
                    .setContent(ByteString.copyFrom(bytes, offset, range)).build())
            }

            assertNull(progress().estimate)
            writeRange(0)
            assertEquals(PhonePayloadRateState.MEASURING, progress().estimate?.state)
            delay(2100)
            writeRange(0)
            assertEquals(PhonePayloadRateState.MEASURING, progress().estimate?.state)
            writeRange(range)
            val active = progress()
            assertEquals(h.id, active.batch)
            assertEquals(1, active.index)
            assertEquals(2L * range, active.file.offset)
            val estimate = requireNotNull(active.estimate)
            assertEquals(PhonePayloadRateState.AVAILABLE, estimate.state)
            val expectedRate = minOf(requireNotNull(estimate.bytesPerSecond), limit)
            assertEquals((bytes.size - active.file.offset + expectedRate - 1) / expectedRate, estimate.remainingSeconds)
            assertTrue(f.grants.batchPlans.readFileProgress(f.approval.id, StorageRules.token(), 0).isEmpty())

            f.restart()
            assertEquals(2L * range, progress().file.offset)
            assertNull(progress().estimate)
            assertEquals(2L * range, status(f, h).upload.offset)
            writeRange(2 * range)
            assertEquals(PhonePayloadRateState.MEASURING, progress().estimate?.state)
            write(f, h, bytes, start = 3 * range)
            assertEquals(bytes.size.toLong(), progress().file.offset)
            assertNull(progress().estimate)
            val done = finish(f, h).upload
            assertEquals(UploadPhase.UPLOAD_COMPLETED, done.phase)
            assertArrayEquals(bytes, f.read(done.finalName, parent))
            assertTrue(f.grants.batchPlans.readFileProgress(f.approval.id, f.approval.epoch, 0).isEmpty())
        } finally { f.close() }
    }

    @Test fun completionNeedsFinalReceiptsReleasesBytesAndSurvivesLostRepliesAndRestart()=runBlocking {
        val f=IncomingUploadsTest.Fixture(); try {
            val bytes=byteArrayOf(7,8,9); val h=accepted(f,plan(bytes)); assertEquals(FileStatus.CONFLICT,f.raw(request(h,FileOperation.FINISH_UPLOAD_BATCH).build()).status)
            assertEquals(3L,f.grants.batchPlans.reservedBytes(f.approval.id)); val parent=folder(f,h); prepare(f,h); write(f,h,bytes); val file=finish(f,h).upload
            val done=f.call(request(h,FileOperation.FINISH_UPLOAD_BATCH).build()).batch; assertEquals(UploadBatchPlanPhase.UPLOAD_PLAN_COMPLETED,done.phase); assertArrayEquals(byteArrayOf(1,1),done.itemOutcomes.toByteArray()); assertEquals(0L,f.grants.batchPlans.reservedBytes(null))
            assertEquals(done,f.call(request(h,FileOperation.FINISH_UPLOAD_BATCH).build()).batch); assertEquals(done,f.call(request(h,FileOperation.STOP_UPLOAD_BATCH).build()).batch)
            assertEquals(FileStatus.CONFLICT,f.raw(request(h,FileOperation.PREPARE_BATCH_FILE,1).build()).status)
            f.restart(reopenGrants=true); assertTrue(f.grants.batchPlans.recoveryIssues().isEmpty()); assertEquals(done,f.call(request(h,FileOperation.UPLOAD_BATCH_STATUS).build()).batch)
            assertEquals(file,status(f,h).upload); assertArrayEquals(bytes,f.read(file.finalName,parent))
            assertEquals(done,f.call(request(h,FileOperation.BEGIN_UPLOAD_BATCH).setDestinationId(f.target).setBatchHeader(h).build()).batch)
            val next=UploadBatchRules.header(MutationRules.newId(),h.epoch,f.grant.id,plan(bytes)).toBuilder().setTotalBytes(UploadRules.QUOTA).build()
            f.call(request(next,FileOperation.BEGIN_UPLOAD_BATCH).setDestinationId(f.target).setBatchHeader(next).build()); assertEquals(UploadRules.QUOTA,f.grants.batchPlans.reservedBytes(null))
        } finally {f.close()}
    }
    @Test fun completionFoldsSourceErrorsAndSkippedBranchesWithoutCreatingTheirDescendants()=runBlocking {
        val f=IncomingUploadsTest.Fixture(); try {
            val bytes=byteArrayOf(3,4,5); val manifest=UploadBatchManifest.newBuilder().setCollision(UploadCollision.UPLOAD_SKIP)
                .addItems(UploadBatchItem.newBuilder().setParent(-1).setName("Reports").setIsDirectory(true))
                .addItems(UploadBatchItem.newBuilder().setParent(0).setName("not-copied.bin").setSize(3).setSha256(ByteString.copyFrom(Rules.digest(bytes))))
                .addItems(UploadBatchItem.newBuilder().setParent(-1).setName("unavailable.bin").setError("The owner selected an unavailable source."))
                .addItems(UploadBatchItem.newBuilder().setParent(-1).setName("copied.bin").setSize(3).setSha256(ByteString.copyFrom(Rules.digest(bytes)))).build()
            val h=accepted(f,manifest); val before=f.children(f.data.getString("folder")!!); assertEquals(UploadBatchItemPhase.BATCH_ITEM_SKIPPED,f.call(request(h,FileOperation.CREATE_BATCH_DIRECTORY,0).build()).batchItem.phase)
            prepare(f,h,3); write(f,h,bytes,3); finish(f,h,3); val done=f.call(request(h,FileOperation.FINISH_UPLOAD_BATCH).build()).batch
            assertArrayEquals(byteArrayOf(2,2,3,1),done.itemOutcomes.toByteArray()); assertEquals(before,f.children(f.data.getString("folder")!!)); assertEquals(0L,f.grants.batchPlans.reservedBytes(null))
            f.restart(reopenGrants=true); assertTrue(f.grants.batchPlans.recoveryIssues().isEmpty()); assertEquals(done,f.call(request(h,FileOperation.UPLOAD_BATCH_STATUS).build()).batch)
        } finally {f.close()}
    }
    @Test fun stopRemovesOnlyOwnedPrivatePartsKeepsProviderCopiesAndCannotResume()=runBlocking {
        val f=IncomingUploadsTest.Fixture(); try {
            val bytes=ByteArray(StorageRules.RANGE+7); val h=accepted(f,plan(bytes,2)); val parent=folder(f,h); val before=f.children()
            prepare(f,h); f.call(request(h,FileOperation.WRITE_BATCH_FILE,1).setContent(ByteString.copyFrom(bytes,0,StorageRules.RANGE)).build())
            val stopped=stopPlan(f,h); assertEquals(UploadBatchPlanPhase.UPLOAD_PLAN_STOPPED,stopped.phase); assertArrayEquals(byteArrayOf(1,3,0),stopped.itemOutcomes.toByteArray())
            assertEquals(before,f.children()); assertTrue(f.children(parent).isEmpty()); assertTrue(privateParts(f).isEmpty()); assertEquals(0L,f.grants.batchPlans.reservedBytes(null))
            assertEquals(UploadPhase.UPLOAD_CANCELED,status(f,h).upload.phase); assertEquals(FileStatus.CONFLICT,f.raw(request(h,FileOperation.WRITE_BATCH_FILE,1).setContent(ByteString.copyFrom(bytes,0,StorageRules.RANGE)).build()).status)
            assertEquals(FileStatus.CONFLICT,f.raw(request(h,FileOperation.PREPARE_BATCH_FILE,2).build()).status); assertEquals(FileStatus.CONFLICT,f.raw(request(h,FileOperation.FINISH_UPLOAD_BATCH).build()).status)
            f.restart(reopenGrants=true); assertTrue(f.grants.batchPlans.recoveryIssues().isEmpty()); assertEquals(stopped,stopPlan(f,h)); assertEquals(before,f.children()); assertEquals(0,partDescriptors(f))
        } finally {f.close()}
    }
    @Test fun stopCancelsBlockedProviderPublicationAndKeepsItsUncertainCopy()=runBlocking {
        val f=IncomingUploadsTest.Fixture(); try {
            val bytes=ByteArray(65_537) {19}; val h=accepted(f,plan(bytes)); val parent=folder(f,h); prepare(f,h); write(f,h,bytes); f.mode("blockWrite")
            f.call(request(h,FileOperation.FINISH_BATCH_FILE,1).build())
            withTimeout(10_000) {while(runCatching {JSONObject(journal(f,h).readText()).getJSONArray("rows").getJSONObject(0).getString("published").isEmpty()}.getOrDefault(true)) delay(10)}
            val published=JSONObject(journal(f,h).readText()).getJSONArray("rows").getJSONObject(0).getString("published")
            val possible=Docs.getDocumentId(android.net.Uri.parse(published)); val beforeStop=android.os.SystemClock.elapsedRealtime()
            fun blocked()=f.base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"openProbe",possible,null)!!.getBoolean("entered")
            withTimeout(5000) {while(!blocked()) delay(10)}
            val progress = f.grants.batchPlans.readFileProgress(f.approval.id, f.approval.epoch, 0).single()
            assertEquals(bytes.size.toLong(), progress.file.offset)
            assertEquals(PhoneIncomingFinalizationStage.PREPARING_PROVIDER_DOCUMENT, progress.finalization?.stage)
            assertNull(requireNotNull(progress.finalization).estimate)
            val release=java.util.concurrent.CountDownLatch(1); val held=CompletableDeferred<Unit>()
            val holder=async(Dispatchers.IO) {synchronized(f.policy.gate) {held.complete(Unit); check(release.await(8,java.util.concurrent.TimeUnit.SECONDS))}}
            held.await(); val stopping=async(Dispatchers.IO) {f.call(request(h,FileOperation.STOP_UPLOAD_BATCH).build()).batch}
            try {withTimeout(3000) {while(blocked()) delay(10)}; assertFalse("Durable stop still needs owner authorization.",stopping.isCompleted)} finally {release.countDown()}
            holder.await(); val first=withTimeout(5000) {stopping.await()}
            assertTrue("Stop must cancel the provider's ten-second open wait.",android.os.SystemClock.elapsedRealtime()-beforeStop<5000)
            assertTrue(first.phase in setOf(UploadBatchPlanPhase.UPLOAD_PLAN_STOPPING,UploadBatchPlanPhase.UPLOAD_PLAN_STOPPED))
            withTimeout(10_000) {f.grants.batchPlans.whenFilesIdle()}; f.mode(""); val stopped=stopPlan(f,h)
            assertArrayEquals(byteArrayOf(1,4),stopped.itemOutcomes.toByteArray()); assertEquals(UploadPhase.UPLOAD_REVIEW,status(f,h).upload.phase)
            assertEquals(setOf(possible),f.children(parent).keys); assertTrue(privateParts(f).isEmpty()); assertEquals(0L,f.grants.batchPlans.reservedBytes(null)); assertEquals(0,partDescriptors(f))
            f.restart(reopenGrants=true); assertTrue(f.grants.batchPlans.recoveryIssues().isEmpty()); assertEquals(stopped,stopPlan(f,h)); assertEquals(setOf(possible),f.children(parent).keys)
        } finally {f.mode(""); f.close()}
    }
    @Test fun stopCleanupIsBoundedAndColdRetryContinuesTheSameBarrier()=runBlocking {
        val f=IncomingUploadsTest.Fixture(); try {
            val h=accepted(f,plan(byteArrayOf(1,2,3),17)); val parent=folder(f,h); for(index in 1..17) prepare(f,h,index)
            assertEquals(UploadBatchPlanPhase.UPLOAD_PLAN_STOPPING,f.call(request(h,FileOperation.STOP_UPLOAD_BATCH).build()).batch.phase); assertEquals(1,privateParts(f).size); assertEquals(51L,f.grants.batchPlans.reservedBytes(null))
            f.restart(reopenGrants=true); assertTrue(f.grants.batchPlans.recoveryIssues().isEmpty()); val stopped=stopPlan(f,h)
            assertEquals(UploadBatchPlanPhase.UPLOAD_PLAN_STOPPED,stopped.phase); assertTrue(privateParts(f).isEmpty()); assertTrue(f.children(parent).isEmpty()); assertEquals(0L,f.grants.batchPlans.reservedBytes(null)); assertEquals(0,partDescriptors(f))
        } finally {f.close()}
    }
    @Test fun changedTerminalOutcomesCannotAdoptRetainedHistoryAfterRestart()=runBlocking {
        val f=IncomingUploadsTest.Fixture(); try {
            val h=accepted(f,plan(byteArrayOf(1))); val parent=folder(f,h); prepare(f,h); stopPlan(f,h)
            val file=File(f.root,"incoming-batch-plans/${h.id}.json"); file.writeText(JSONObject(file.readText()).put("outcomes",Rules.b64(byteArrayOf(1,1))).toString())
            f.restart(reopenGrants=true); assertTrue(f.grants.batchPlans.recoveryIssues().isNotEmpty()); assertTrue(f.children().containsKey(parent)); assertTrue(file.exists())
        } finally {f.close()}
    }
    @Test fun stoppingOnePlanPreservesAnotherPlansStagingAndFuturePublication()=runBlocking {
        val f=IncomingUploadsTest.Fixture(); try {
            val bytes=byteArrayOf(2,3,4); val first=accepted(f,plan(bytes)); val second=accepted(f,plan(bytes)); folder(f,first); val other=folder(f,second); prepare(f,first); prepare(f,second)
            assertEquals(2,privateParts(f).size); assertEquals(UploadBatchPlanPhase.UPLOAD_PLAN_STOPPED,stopPlan(f,first).phase)
            assertEquals(1,privateParts(f).size); assertEquals(3L,f.grants.batchPlans.reservedBytes(null)); assertEquals(UploadPhase.UPLOAD_RECEIVING,status(f,second).upload.phase)
            write(f,second,bytes); val done=finish(f,second).upload; assertArrayEquals(bytes,f.read(done.finalName,other))
            assertEquals(UploadBatchPlanPhase.UPLOAD_PLAN_COMPLETED,f.call(request(second,FileOperation.FINISH_UPLOAD_BATCH).build()).batch.phase); assertEquals(0L,f.grants.batchPlans.reservedBytes(null)); assertTrue(privateParts(f).isEmpty())
        } finally {f.close()}
    }
    @Test fun malformedOrForeignEndRequestsCannotInstallAStopBarrier()=runBlocking {
        val f=IncomingUploadsTest.Fixture(); try {
            val h=accepted(f,plan(byteArrayOf(1))); assertEquals(FileStatus.INVALID_REQUEST,f.raw(request(h,FileOperation.STOP_UPLOAD_BATCH,0).build()).status)
            assertEquals(FileStatus.STALE_REFERENCE,f.raw(request(h,FileOperation.STOP_UPLOAD_BATCH).setMutationEpoch(StorageRules.token()).build()).status)
            assertEquals(FileStatus.ACCESS_DENIED,f.raw(request(h,FileOperation.STOP_UPLOAD_BATCH).build(),"c".repeat(64)).status)
            assertEquals(UploadBatchPlanPhase.UPLOAD_PLAN_READY,f.call(request(h,FileOperation.UPLOAD_BATCH_STATUS).build()).batch.phase); val before=f.children()
            val canceled=stopPlan(f,h); assertEquals(UploadBatchPlanPhase.UPLOAD_PLAN_CANCELED,canceled.phase); assertTrue(canceled.itemOutcomes.isEmpty); assertEquals(before,f.children())
        } finally {f.close()}
    }
    @Test fun actualProviderBytesResumeAfterRestartAndReceiptsRetainTheOriginalScope()=runBlocking {
        val f=IncomingUploadsTest.Fixture(); try {
            val bytes=ByteArray(98_317) {(it*17%251).toByte()}; val manifest=plan(bytes); val h=accepted(f,manifest)
            assertEquals(FileStatus.CONFLICT,f.raw(request(h,FileOperation.PREPARE_BATCH_FILE,1).build()).status)
            val destination=folder(f,h); val first=prepare(f,h); val expected=original(f.approval.id,h,manifest,1)
            assertEquals(first.upload,UploadBatchRules.checkFileReceipt(f.approval.id,h,1,expected,first)); assertEquals(0L,MutationRules.created(first.upload.id)); assertTrue(f.incoming.state.value.isEmpty())
            val chunk=request(h,FileOperation.WRITE_BATCH_FILE,1).setContent(ByteString.copyFrom(bytes,0,StorageRules.RANGE)).build()
            f.call(chunk); assertEquals(StorageRules.RANGE.toLong(),f.call(chunk).upload.offset)
            assertEquals(FileStatus.CONFLICT,f.raw(chunk.toBuilder().setContent(ByteString.copyFrom(ByteArray(StorageRules.RANGE))).build()).status)
            f.restart(reopenGrants=true); assertEquals(StorageRules.RANGE.toLong(),status(f,h).upload.offset); write(f,h,bytes,start=StorageRules.RANGE)
            val done=finish(f,h); assertEquals(UploadPhase.UPLOAD_COMPLETED,done.upload.phase); UploadBatchRules.checkFileReceipt(f.approval.id,h,1,expected,done)
            assertArrayEquals(bytes,f.read(done.upload.finalName,destination)); assertTrue(privateParts(f).isEmpty()); f.restart(reopenGrants=true); assertEquals(done.batchItem,status(f,h).batchItem)
        } finally {f.close()}
    }
    @Test fun changedIntentKindIndexAndOrdinaryIdCannotCreateAnotherPrivatePart()=runBlocking {
        val f=IncomingUploadsTest.Fixture(); try {
            val h=accepted(f,plan(byteArrayOf(1))); folder(f,h)
            val bad=f.descriptor(byteArrayOf(2),"changed.bin")
            assertEquals(FileStatus.INVALID_REQUEST,f.raw(request(h,FileOperation.PREPARE_BATCH_FILE,1).setUpload(bad).build()).status)
            assertEquals(FileStatus.UNSUPPORTED,f.raw(request(h,FileOperation.PREPARE_BATCH_FILE,0).build()).status)
            assertEquals(FileStatus.INVALID_REQUEST,f.raw(request(h,FileOperation.PREPARE_BATCH_FILE,4096).build()).status); assertTrue(privateParts(f).isEmpty())
            val prepared=prepare(f,h).upload; val ordinary=prepared.toBuilder().setPhase(UploadPhase.UPLOAD_PENDING).setMessage("").build()
            assertEquals(FileStatus.STALE_REFERENCE,f.raw(FileRequest.newBuilder().setOperation(FileOperation.PREPARE_UPLOAD).setTransferId(ordinary.id).setUpload(ordinary).setMutationEpoch(f.epoch).setDestinationId(f.target).build()).status)
            assertEquals(1,privateParts(f).size)
        } finally {f.close()}
    }
    @Test fun emptyFilesAndChecksumFailurePreserveProviderContentWithoutPublishingWrongBytes()=runBlocking {
        val f=IncomingUploadsTest.Fixture(); try {
            val manifest=plan(byteArrayOf()).toBuilder().addItems(UploadBatchItem.newBuilder().setParent(0).setName("bad.bin").setSize(3).setSha256(ByteString.copyFrom(Rules.digest(byteArrayOf(1,2,3))))).build()
            val h=accepted(f,manifest); val destination=folder(f,h); prepare(f,h); val empty=finish(f,h)
            assertEquals(UploadPhase.UPLOAD_COMPLETED,empty.upload.phase); assertArrayEquals(byteArrayOf(),f.read(empty.upload.finalName,destination))
            prepare(f,h,2); write(f,h,byteArrayOf(9,9,9),2); assertEquals(UploadPhase.UPLOAD_REVIEW,finish(f,h,2).upload.phase)
            assertFalse(f.children(destination).values.contains("bad.bin")); assertEquals(1,privateParts(f).size)
        } finally {f.close()}
    }
    @Test fun cancellationAndArchiveOnlyRemoveDeliberatelyReviewedPrivateBytes()=runBlocking {
        val f=IncomingUploadsTest.Fixture(); try {
            val h=accepted(f,plan(byteArrayOf(1),2)); val destination=folder(f,h); prepare(f,h); write(f,h,byteArrayOf(1))
            assertEquals(UploadPhase.UPLOAD_CANCELED,f.call(request(h,FileOperation.CANCEL_BATCH_FILE,1).build()).upload.phase); assertTrue(privateParts(f).isEmpty())
            assertEquals(UploadPhase.UPLOAD_CANCELED,prepare(f,h).upload.phase); prepare(f,h,2); write(f,h,byteArrayOf(1),2)
            f.grants.batchPlans.archiveHistory(); assertEquals(1,privateParts(f).size); assertEquals(1,f.grants.batchPlans.orphanParts())
            assertEquals(FileStatus.STALE_REFERENCE,f.raw(request(h,FileOperation.PREPARE_BATCH_FILE,2).build()).status); val before=f.children(destination)
            BatchPartRecoveryTest.removeReviewed(f.grants.batchPlans); assertTrue(privateParts(f).isEmpty()); assertEquals(before,f.children(destination))
            f.grants.batchPlans.clearPreservedRecords(); assertEquals(0,f.grants.batchPlans.preservedRecords())
        } finally {f.close()}
    }
    private suspend fun uncertain(mode:String) {
        val f=IncomingUploadsTest.Fixture(); try {
            val bytes=byteArrayOf(1,2,3); val h=accepted(f,plan(bytes)); val parent=folder(f,h); val original=f.read("simu.txt")
            if(mode=="existing") {
                val uri=requireNotNull(Docs.createDocument(f.context.contentResolver,Docs.buildDocumentUriUsingTree(f.tree,parent),"application/octet-stream","owner.bin"))
                f.context.contentResolver.openOutputStream(uri)!!.use {it.write(byteArrayOf(9))}
            }
            fun modeAt(value:String) {f.base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"creationMode",parent,Bundle().apply {putString("mode",value)})}
            prepare(f,h); write(f,h,bytes); modeAt(mode)
            val result=finish(f,h); assertEquals(UploadPhase.UPLOAD_REVIEW,result.upload.phase); modeAt(""); val count=f.children(parent).size
            f.restart(reopenGrants=true); assertEquals(UploadPhase.UPLOAD_REVIEW,finish(f,h).upload.phase); assertEquals(count,f.children(parent).size); assertArrayEquals(original,f.read("simu.txt"))
            if(mode=="existing") assertArrayEquals(byteArrayOf(9),f.read("owner.bin",parent))
            assertEquals(1,privateParts(f).size)
        } finally {f.close()}
    }
    @Test fun lostProviderResultNeverCreatesAnotherCopy()=runBlocking {uncertain("loseReceipt")}
    @Test fun existingProviderDocumentCannotBeAdoptedOrOverwritten()=runBlocking {uncertain("existing")}
    private suspend fun collision(choice:UploadCollision,phase:UploadPhase) {
        val f=IncomingUploadsTest.Fixture(); try {
            val bytes=byteArrayOf(7); val h=accepted(f,plan(bytes,collision=choice)); val parent=folder(f,h)
            val uri=requireNotNull(Docs.createDocument(f.context.contentResolver,Docs.buildDocumentUriUsingTree(f.tree,parent),"application/octet-stream","file-0.bin"))
            f.context.contentResolver.openOutputStream(uri)!!.use {it.write(byteArrayOf(9))}; prepare(f,h); write(f,h,bytes); val done=finish(f,h)
            assertEquals(phase,done.upload.phase); assertArrayEquals(byteArrayOf(9),f.read("file-0.bin",parent))
            if(phase==UploadPhase.UPLOAD_COMPLETED) assertArrayEquals(bytes,f.read(done.upload.finalName,parent))
        } finally {f.close()}
    }
    @Test fun keepBothPreservesOwnerBytes()=runBlocking {collision(UploadCollision.UPLOAD_KEEP_BOTH,UploadPhase.UPLOAD_COMPLETED)}
    @Test fun skipPreservesOwnerBytes()=runBlocking {collision(UploadCollision.UPLOAD_SKIP,UploadPhase.UPLOAD_SKIPPED)}
    @Test fun failPreservesOwnerBytes()=runBlocking {collision(UploadCollision.UPLOAD_FAIL,UploadPhase.UPLOAD_REVIEW)}
    @Test fun sharedVerificationPermitsApplyToBothOrdinaryAndScopedFiles()=runBlocking {
        val f=IncomingUploadsTest.Fixture(); var one:AutoCloseable?=null; var two:AutoCloseable?=null
        try {
            val h=accepted(f,plan(byteArrayOf(1))); folder(f,h); prepare(f,h); write(f,h,byteArrayOf(1)); val ordinary=f.begin(f.descriptor(byteArrayOf(),"ordinary.bin"))
            one=f.grants.reserveIncomingVerification(); two=f.grants.reserveIncomingVerification(); assertNotNull(one); assertNotNull(two)
            assertEquals(FileStatus.RESOURCE_EXHAUSTED,f.raw(request(h,FileOperation.FINISH_BATCH_FILE,1).build()).status)
            assertEquals(FileStatus.RESOURCE_EXHAUSTED,f.raw(FileRequest.newBuilder().setOperation(FileOperation.FINISH_UPLOAD).setTransferId(ordinary.id).setMutationEpoch(f.epoch).build()).status)
            one!!.close(); one=null; assertEquals(UploadPhase.UPLOAD_COMPLETED,finish(f,h).upload.phase)
            two!!.close(); two=null; assertEquals(UploadPhase.UPLOAD_COMPLETED,f.finish(ordinary).phase)
            val a=f.grants.reserveIncomingVerification(); val b=f.grants.reserveIncomingVerification(); assertNotNull(a); assertNotNull(b); assertNull(f.grants.reserveIncomingVerification()); a!!.close(); b!!.close()
        } finally {one?.close(); two?.close(); f.close()}
    }
    @Test fun aggregateReservationIsNotChargedAgainForEachPreparedChild()=runBlocking {
        val f=IncomingUploadsTest.Fixture(); try {
            val huge=plan(byteArrayOf()).toBuilder().setItems(1,plan(byteArrayOf()).getItems(1).toBuilder().setSize(UploadRules.QUOTA-3)).build(); accepted(f,huge)
            val h=accepted(f,plan(byteArrayOf(1,2,3))); folder(f,h); prepare(f,h)
            assertEquals(UploadRules.QUOTA,f.grants.batchPlans.reservedBytes(f.approval.id)); assertEquals(3L,f.grants.batchPlans.privateUnwrittenBytes())
            write(f,h,byteArrayOf(1,2,3)); assertEquals(0L,f.grants.batchPlans.privateUnwrittenBytes()); assertEquals(UploadPhase.UPLOAD_COMPLETED,finish(f,h).upload.phase)
        } finally {f.close()}
    }
    @Test fun currentExclusionsPermissionsAndPairingGenerationStillOwnRetainedFiles()=runBlocking {
        val f=IncomingUploadsTest.Fixture(); try {
            val h=accepted(f,plan(byteArrayOf(1))); folder(f,h); f.grants.update(f.grant.id,15,setOf("file-0.bin"))
            assertEquals(FileStatus.ACCESS_DENIED,f.raw(request(h,FileOperation.PREPARE_BATCH_FILE,1).build()).status); assertTrue(privateParts(f).isEmpty()); assertFalse(journal(f,h).exists())
            f.grants.update(f.grant.id,15); prepare(f,h); write(f,h,byteArrayOf(1)); f.grants.update(f.grant.id,StorageRules.READ_ONLY)
            assertEquals(FileStatus.ACCESS_DENIED,f.raw(request(h,FileOperation.FINISH_BATCH_FILE,1).build()).status); f.grants.update(f.grant.id,15)
            assertTrue(f.policy.revoke(setOf(f.policy.blockPeer(f.approval)))); f.grants.reconcileAccess(); TestPeer.approve(f.policy,f.software); f.restart(selectTarget=false,reopenGrants=true)
            assertEquals(FileStatus.ACCESS_DENIED,f.raw(request(h,FileOperation.FINISH_BATCH_FILE,1).build()).status); assertEquals(1,privateParts(f).size)
        } finally {f.close()}
    }
    private suspend fun damaged(kind:String) {
        val f=IncomingUploadsTest.Fixture(); try {
            val h=accepted(f,plan(byteArrayOf(1))); val parent=folder(f,h); prepare(f,h)
            val file=when(kind) {"binding"->File(f.root,"incoming-batch-plans/${h.id}.1.json"); "index"->File(f.root,"incoming-batch-plans/${h.id}.children.json"); "manifest"->File(f.root,"incoming-batch-plans/${h.id}.json"); else->journal(f,h)}
            if(kind=="corrupt" || kind=="manifest") file.writeText("{broken") else check(file.delete())
            assertNotEquals(FileStatus.FILE_STATUS_OK,f.raw(request(h,FileOperation.PREPARE_BATCH_FILE,1).build()).status); assertTrue(f.grants.batchPlans.recoveryIssues().isNotEmpty()); assertEquals(1,privateParts(f).size)
            f.restart(reopenGrants=true); assertTrue(f.grants.batchPlans.recoveryIssues().isNotEmpty()); assertNotEquals(FileStatus.FILE_STATUS_OK,f.raw(request(h,FileOperation.PREPARE_BATCH_FILE,1).build()).status)
            assertTrue(f.children(parent).isEmpty()); f.grants.batchPlans.archiveHistory(); assertEquals(1,f.grants.batchPlans.orphanParts()); BatchPartRecoveryTest.removeReviewed(f.grants.batchPlans); assertTrue(privateParts(f).isEmpty())
        } finally {f.close()}
    }
    @Test fun missingJournalCannotRestartTheSameCopy()=runBlocking {damaged("journal")}
    @Test fun missingBindingCannotRestartTheSameCopy()=runBlocking {damaged("binding")}
    @Test fun missingAttemptIndexCannotRestartTheSameCopy()=runBlocking {damaged("index")}
    @Test fun corruptJournalCannotRestartTheSameCopy()=runBlocking {damaged("corrupt")}
    @Test fun corruptManifestCannotRestartTheSameCopy()=runBlocking {damaged("manifest")}
    @Test fun changedPrivateCheckpointIsRejectedBeforeAdditionalBytesOrPublication()=runBlocking {
        val f=IncomingUploadsTest.Fixture(); try {
            val bytes=ByteArray(StorageRules.RANGE+1) {7}; val h=accepted(f,plan(bytes)); val parent=folder(f,h); prepare(f,h)
            f.call(request(h,FileOperation.WRITE_BATCH_FILE,1).setContent(ByteString.copyFrom(bytes,0,StorageRules.RANGE)).build()); f.restart(reopenGrants=true)
            RandomAccessFile(privateParts(f).single(),"rw").use {it.writeByte(9)}
            assertEquals(FileStatus.CONFLICT,f.raw(request(h,FileOperation.WRITE_BATCH_FILE,1).setOffset(StorageRules.RANGE.toLong()).setContent(ByteString.copyFrom(bytes,StorageRules.RANGE,1)).build()).status)
            assertEquals(StorageRules.RANGE.toLong(),status(f,h).upload.offset); assertTrue(f.children(parent).isEmpty())
        } finally {f.close()}
    }
    @Test fun leavingForegroundStopsBatchPublicationEvenWhenTheScopeIsListedForPreservation()=runBlocking {
        val f=IncomingUploadsTest.Fixture(); try {
            val bytes=ByteArray(65_537) {19}; val h=accepted(f,plan(bytes)); val parent=folder(f,h); prepare(f,h); write(f,h,bytes); f.mode("blockWrite")
            f.call(request(h,FileOperation.FINISH_BATCH_FILE,1).build())
            withTimeout(5000) {while(runCatching {JSONObject(journal(f,h).readText()).getJSONArray("rows").getJSONObject(0).getString("published").isEmpty()}.getOrDefault(true)) delay(10)}
            f.visible.set(false); f.service.suspendAvailability(setOf(h.id)); withTimeout(5000) {f.grants.batchPlans.whenFilesIdle()}
            assertEquals(FileStatus.ACCESS_DENIED,f.raw(request(h,FileOperation.UPLOAD_BATCH_ITEM_STATUS,1).build()).status)
            f.visible.set(true); f.mode(""); val before=f.children(parent); val done=finish(f,h)
            assertEquals(UploadPhase.UPLOAD_REVIEW,done.upload.phase); assertEquals(before,f.children(parent)); assertEquals(1,privateParts(f).size)
        } finally {f.close()}
    }
    @Test fun receiveOnlyReceiptsDoNotRevealOwnerActivityOrAcquireReadPermission()=runBlocking {
        val f=IncomingUploadsTest.Fixture(12); try {
            val bytes=byteArrayOf(1,2,3); val h=accepted(f,plan(bytes)); val parent=folder(f,h); prepare(f,h); write(f,h,bytes); val done=finish(f,h)
            assertEquals(UploadPhase.UPLOAD_COMPLETED,done.upload.phase); assertTrue(done.upload.finalName.startsWith("file-0 (")); assertArrayEquals(bytes,f.read(done.upload.finalName,parent))
            requireNotNull(Docs.createDocument(f.context.contentResolver,Docs.buildDocumentUriUsingTree(f.tree,parent),"text/plain","owner-added.txt"))
            assertEquals(done.upload,status(f,h).upload); assertEquals(done.batchItem,status(f,h).batchItem); assertTrue(f.call(FileRequest.newBuilder().setOperation(FileOperation.LIST_ROOTS).build()).entriesList.isEmpty())
        } finally {f.close()}
    }
    @Test fun movedAndReplacedProviderParentCannotReceiveAdditionalPrivateOrPublishedBytes()=runBlocking {
        val f=IncomingUploadsTest.Fixture(); try {
            val h=accepted(f,plan(byteArrayOf(1))); val parent=folder(f,h); prepare(f,h)
            f.base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"change",parent,Bundle().apply {putString("name","owner-renamed")})
            val replacement=requireNotNull(Docs.createDocument(f.context.contentResolver,Docs.buildDocumentUriUsingTree(f.tree,Docs.getTreeDocumentId(f.tree)),Docs.Document.MIME_TYPE_DIR,"batch-root"))
            assertEquals(FileStatus.STALE_REFERENCE,f.raw(request(h,FileOperation.UPLOAD_BATCH_ITEM_STATUS,1).build()).status)
            assertEquals(FileStatus.STALE_REFERENCE,f.raw(request(h,FileOperation.WRITE_BATCH_FILE,1).setContent(ByteString.copyFrom(byteArrayOf(1))).build()).status)
            assertTrue(f.children(Docs.getDocumentId(replacement)).isEmpty()); assertEquals(0L,privateParts(f).single().length())
        } finally {f.close()}
    }
    @Test fun completedFileWithoutItsProviderIdentityCannotBeTrustedAfterRestart()=runBlocking {
        val f=IncomingUploadsTest.Fixture(); try {
            val bytes=byteArrayOf(1,2,3); val h=accepted(f,plan(bytes)); val parent=folder(f,h); prepare(f,h); write(f,h,bytes); val done=finish(f,h); assertEquals(UploadPhase.UPLOAD_COMPLETED,done.upload.phase)
            val file=journal(f,h); val json=JSONObject(file.readText()); json.getJSONArray("rows").getJSONObject(0).put("published",""); file.writeText(json.toString())
            f.restart(reopenGrants=true); assertTrue(f.grants.batchPlans.recoveryIssues().isNotEmpty()); assertNotEquals(FileStatus.FILE_STATUS_OK,f.raw(request(h,FileOperation.UPLOAD_BATCH_ITEM_STATUS,1).build()).status)
            assertArrayEquals(bytes,f.read(done.upload.finalName,parent))
        } finally {f.close()}
    }
    @Test fun missingEntireMetadataCannotDiscardPrivatePartsOrTheirOsAccessBeforeOwnerReview()=runBlocking {
        val f=IncomingUploadsTest.Fixture(); try {
            val bytes=byteArrayOf(1,2,3); val h=accepted(f,plan(bytes)); val parent=folder(f,h); prepare(f,h); write(f,h,bytes)
            fun leases()=org.json.JSONArray(File(f.root,"saf-access.json").readText()).let {rows->(0 until rows.length()).count {rows.getJSONObject(it).getString("owner").startsWith("batch-incoming:")}}
            assertEquals(1,leases()); f.service.close(); f.incoming.shutdown(); f.grants.batchPlans.shutdown()
            val metadata=File(f.root,"incoming-batch-plans"); check(metadata.canonicalFile.parentFile==f.root.canonicalFile)
            metadata.listFiles().orEmpty().forEach {check(it.isFile && it.delete())}
            f.restart(selectTarget=false,reopenGrants=true)
            assertTrue(f.grants.batchPlans.recoveryIssues().isNotEmpty()); assertEquals(1,leases()); assertEquals(1,privateParts(f).size)
            assertNotEquals(FileStatus.FILE_STATUS_OK,f.raw(request(h,FileOperation.PREPARE_BATCH_FILE,1).build()).status)
            try {BatchPartRecoveryTest.removeReviewed(f.grants.batchPlans); fail("Damaged ownership must be reviewed before private cleanup.")} catch(e:StorageFailure) {assertEquals(FileStatus.CONFLICT,e.status)}
            assertEquals(1,privateParts(f).size); assertTrue(f.children(parent).isEmpty())
            f.grants.batchPlans.archiveHistory(); assertEquals(0,leases()); assertEquals(1,f.grants.batchPlans.orphanParts())
            BatchPartRecoveryTest.removeReviewed(f.grants.batchPlans); assertTrue(privateParts(f).isEmpty()); assertTrue(f.children(parent).isEmpty())
        } finally {f.close()}
    }
    @Test fun privateDescriptorsCloseAfterOrdinaryAndScopedReceivingPublicationAndOwnerCleanup()=runBlocking {
        val f=IncomingUploadsTest.Fixture(); try {
            fun assertClosed() {
                assertEquals("No borrowed stream may retain an Os.open-owned private descriptor.",0,partDescriptors(f))
            }
            val probe=File(f.root,"fd-probe.part").apply {writeBytes(byteArrayOf(1))}
            android.system.Os.open(probe.path,android.system.OsConstants.O_RDONLY,0).useOsDescriptor {assertEquals("The resource oracle must see an intentionally held descriptor.",1,partDescriptors(f))}
            assertClosed(); check(probe.delete())
            val bytes=ByteArray(StorageRules.RANGE+1) {17}; val h=accepted(f,plan(bytes,2)); folder(f,h)
            prepare(f,h); assertClosed(); write(f,h,bytes); assertClosed(); assertEquals(UploadPhase.UPLOAD_COMPLETED,finish(f,h).upload.phase); assertClosed()
            prepare(f,h,2); write(f,h,bytes,2); assertClosed(); f.grants.batchPlans.archiveHistory(); BatchPartRecoveryTest.removeReviewed(f.grants.batchPlans); assertClosed()
            val ordinary=f.descriptor(bytes,"ordinary.bin"); f.begin(ordinary); assertClosed(); f.write(ordinary,bytes); assertClosed(); assertEquals(UploadPhase.UPLOAD_COMPLETED,f.finish(ordinary).phase); assertClosed()
            File(f.root,"incoming-uploads/${StorageRules.token()}.part").writeBytes(byteArrayOf(1)); IncomingPartRecoveryTest.removeReviewed(f.incoming); assertClosed()
        } finally {f.close()}
    }
    @Test fun all4095PrivatePartsAndReceiptsReopenWithOnlyOneBatchOsLease()=runBlocking {
        val f=IncomingUploadsTest.Fixture(); try {
            val h=accepted(f,plan(byteArrayOf(1),4095)); val parent=folder(f,h)
            for(index in 1 until 4096) {assertEquals("item $index",UploadPhase.UPLOAD_RECEIVING,prepare(f,h,index).upload.phase); if(index%64==0) assertEquals("item $index retained private descriptors",0,partDescriptors(f))}
            assertEquals(4095,privateParts(f).size); assertTrue(f.children(parent).isEmpty()); assertTrue(f.incoming.state.value.isEmpty())
            fun leases()=org.json.JSONArray(File(f.root,"saf-access.json").readText()).let {rows->(0 until rows.length()).count {rows.getJSONObject(it).getString("owner").startsWith("batch-incoming:")}}
            assertEquals(1,leases()); f.restart(reopenGrants=true); assertTrue(f.grants.batchPlans.recoveryIssues().isEmpty()); assertEquals(4095,privateParts(f).size)
            assertEquals(UploadPhase.UPLOAD_RECEIVING,status(f,h,4095).upload.phase); assertEquals(1,leases()); assertEquals(0,partDescriptors(f))
            var stopped:UploadBatchReceipt?=null; var previous=0
            repeat(300) {
                if(stopped==null) {
                    val reply=f.call(request(h,FileOperation.STOP_UPLOAD_BATCH).build()).batch
                    val cursor=JSONObject(File(f.root,"incoming-batch-plans/${h.id}.json").readText()).getInt("stopCursor")
                    assertTrue(cursor>=previous && cursor-previous<=256); previous=cursor
                    if(reply.phase==UploadBatchPlanPhase.UPLOAD_PLAN_STOPPED) stopped=reply
                    if(it%16==0) assertEquals(0,partDescriptors(f))
                }
            }
            assertNotNull("Bounded Stop must finish the 4,095-part inventory.",stopped); assertEquals(4096,previous); assertEquals(4096,stopped!!.itemOutcomes.size())
            assertTrue(privateParts(f).isEmpty()); assertEquals(0L,f.grants.batchPlans.reservedBytes(null)); assertEquals(0,leases()); assertTrue(f.children(parent).isEmpty())
            f.restart(reopenGrants=true); assertTrue(f.grants.batchPlans.recoveryIssues().isEmpty()); assertEquals(stopped,f.call(request(h,FileOperation.UPLOAD_BATCH_STATUS).build()).batch); assertEquals(0,leases()); assertEquals(0,partDescriptors(f))
        } finally {f.close()}
    }
    @Test fun full4096ItemPlanPublishesActualProviderFilesAndReopensTheFinalReceipt()=runBlocking {
        val f=IncomingUploadsTest.Fixture(); try {
            val bytes=byteArrayOf(17,31,47); val manifest=UploadBatchManifest.newBuilder().setCollision(UploadCollision.UPLOAD_KEEP_BOTH).apply {
                repeat(128) {root->val parent=itemsCount; addItems(UploadBatchItem.newBuilder().setParent(-1).setName("capacity-$root").setIsDirectory(true))
                    repeat(31) {child->addItems(UploadBatchItem.newBuilder().setParent(parent).setName("file-$child.bin").setSize(3).setSha256(ByteString.copyFrom(Rules.digest(bytes))))}}
            }.build()
            val h=accepted(f,manifest)
            repeat(128) {root->val parentIndex=root*32; val parent=folder(f,h,parentIndex)
                for(index in parentIndex+1..parentIndex+31) {prepare(f,h,index); write(f,h,bytes,index); val done=finish(f,h,index); assertEquals("item $index: ${done.upload.message}",UploadPhase.UPLOAD_COMPLETED,done.upload.phase)}
                assertEquals(31,f.children(parent).size); assertEquals("root $root retained private descriptors",0,partDescriptors(f))
            }
            assertEquals(128,f.children().values.count {it.startsWith("capacity-")}); assertTrue(privateParts(f).isEmpty()); assertTrue(f.incoming.state.value.isEmpty())
            val ended=f.call(request(h,FileOperation.FINISH_UPLOAD_BATCH).build()).batch; assertEquals(UploadBatchPlanPhase.UPLOAD_PLAN_COMPLETED,ended.phase)
            assertEquals(4096,ended.itemOutcomes.size()); assertTrue(ended.itemOutcomes.toByteArray().all {it==1.toByte()}); assertEquals(0L,f.grants.batchPlans.reservedBytes(null))
            f.restart(reopenGrants=true); assertTrue(f.grants.batchPlans.recoveryIssues().isEmpty()); val last=status(f,h,4095)
            assertEquals(ended,f.call(request(h,FileOperation.FINISH_UPLOAD_BATCH).build()).batch)
            assertEquals(UploadPhase.UPLOAD_COMPLETED,last.upload.phase); val parent=f.children().entries.single {it.value=="capacity-127"}.key; assertArrayEquals(bytes,f.read(last.upload.finalName,parent))
        } finally {f.close()}
    }
}
