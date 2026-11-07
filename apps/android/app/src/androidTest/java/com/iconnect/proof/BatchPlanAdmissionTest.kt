package com.iconnect.proof

import android.os.Bundle
import android.provider.DocumentsContract as Docs
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.google.protobuf.ByteString
import com.iconnect.protocol.*
import kotlinx.coroutines.runBlocking
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

@RunWith(AndroidJUnit4::class)
class BatchPlanAdmissionTest {
    private fun plan(count:Int=2,size:Long=6)=UploadBatchManifest.newBuilder().setCollision(UploadCollision.UPLOAD_KEEP_BOTH).apply {
        addItems(UploadBatchItem.newBuilder().setParent(-1).setName("folder").setIsDirectory(true))
        repeat(count-1) {index->addItems(UploadBatchItem.newBuilder().setParent(0).setName("Habari-文書-$index.txt").setSize(size)
            .setSha256(ByteString.copyFrom(Rules.digest(if(size==0L) byteArrayOf() else "salama".toByteArray()))))}
    }.build()
    private fun request(header:UploadBatchHeader,operation:FileOperation)=FileRequest.newBuilder().setOperation(operation).setTransferId(header.id).setMutationEpoch(header.epoch)
    private suspend fun intent(f:IncomingUploadsTest.Fixture,manifest:UploadBatchManifest):UploadBatchHeader {
        f.initialize(); val context=f.call(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_BATCH_CONTEXT).build())
        return UploadBatchRules.header(MutationRules.newId(),context.mutationEpoch,f.grant.id,manifest)
    }
    private suspend fun begin(f:IncomingUploadsTest.Fixture,header:UploadBatchHeader)=f.call(request(header,FileOperation.BEGIN_UPLOAD_BATCH).setDestinationId(f.target).setBatchHeader(header).build())
    private suspend fun upload(f:IncomingUploadsTest.Fixture,header:UploadBatchHeader,manifest:UploadBatchManifest) {
        val bytes=UploadBatchRules.encode(manifest)
        for(at in bytes.indices step StorageRules.RANGE) f.call(request(header,FileOperation.WRITE_UPLOAD_MANIFEST).setOffset(at.toLong())
            .setContent(ByteString.copyFrom(bytes,at,minOf(StorageRules.RANGE,bytes.size-at))).build())
    }
    @Test fun all4096ItemsPersistAcrossManagerRestartWithoutCreatingProviderDocuments()=runBlocking {
        val f=IncomingUploadsTest.Fixture(); try {
            val before=f.children(); val manifest=plan(4096,0); val h=intent(f,manifest); begin(f,h); upload(f,h,manifest)
            val sealed=f.call(request(h,FileOperation.SEAL_UPLOAD_BATCH).build()).batch
            assertEquals(UploadBatchPlanPhase.UPLOAD_PLAN_READY,sealed.phase); assertEquals(h,sealed.header); assertEquals(h.encodedLength,sealed.manifestOffset)
            f.restart(reopenGrants=true); val restored=f.call(request(h,FileOperation.UPLOAD_BATCH_STATUS).build()).batch
            assertEquals(h,restored.header); assertEquals(UploadBatchPlanPhase.UPLOAD_PLAN_READY,restored.phase)
            assertEquals(UploadBatchPlanPhase.UPLOAD_PLAN_READY,begin(f,h).batch.phase)
            assertEquals(before,f.children()); assertEquals(1,f.grants.batchPlans.all().size); assertTrue(f.incoming.state.value.isEmpty())
        } finally {f.close()}
    }
    @Test fun lostManifestSessionResumesAtZeroUnderTheOriginalReceipt()=runBlocking {
        val f=IncomingUploadsTest.Fixture(); try {
            val manifest=plan(4096,0); val h=intent(f,manifest); begin(f,h); val bytes=UploadBatchRules.encode(manifest)
            val chunk=request(h,FileOperation.WRITE_UPLOAD_MANIFEST).setContent(ByteString.copyFrom(bytes,0,StorageRules.RANGE)).build()
            assertEquals(StorageRules.RANGE,f.call(chunk).batch.manifestOffset); assertEquals(StorageRules.RANGE,f.call(chunk).batch.manifestOffset)
            f.restart(reopenGrants=true); assertEquals(0,f.call(request(h,FileOperation.UPLOAD_BATCH_STATUS).build()).batch.manifestOffset)
            assertEquals(FileStatus.INVALID_REQUEST,f.raw(chunk.toBuilder().setOffset(StorageRules.RANGE.toLong()).build()).status)
            upload(f,h,manifest); assertEquals(UploadBatchPlanPhase.UPLOAD_PLAN_READY,f.call(request(h,FileOperation.SEAL_UPLOAD_BATCH).build()).batch.phase)
            assertEquals(1,f.grants.batchPlans.all().size); assertFalse(f.children().values.contains("folder"))
        } finally {f.close()}
    }
    @Test fun batchAndStandaloneRequestsCannotEachReserveTheFullPeerQuota()=runBlocking {
        for(batchFirst in listOf(true,false)) {
            val f=IncomingUploadsTest.Fixture(); try {
                val h=intent(f,plan(size=UploadRules.QUOTA)); val ordinary=f.descriptor(byteArrayOf(1))
                if(batchFirst) begin(f,h) else f.begin(ordinary)
                val next=if(batchFirst) FileRequest.newBuilder().setOperation(FileOperation.PREPARE_UPLOAD).setDestinationId(f.target).setTransferId(ordinary.id).setMutationEpoch(f.epoch).setUpload(ordinary).build()
                    else request(h,FileOperation.BEGIN_UPLOAD_BATCH).setDestinationId(f.target).setBatchHeader(h).build()
                assertEquals(FileStatus.RESOURCE_EXHAUSTED,f.raw(next).status)
                if(batchFirst) f.call(request(h,FileOperation.CANCEL_UPLOAD_BATCH).build())
                else f.call(FileRequest.newBuilder().setOperation(FileOperation.CANCEL_UPLOAD).setTransferId(ordinary.id).setMutationEpoch(f.epoch).build())
                f.call(next)
            } finally {f.close()}
        }
    }
    @Test fun fourPlansPerPeerAndEightGlobalPlansBoundPersistentAndVolatileStorage()=runBlocking {
        val f=IncomingUploadsTest.Fixture(); try {
            val manifest=plan(size=0); val first=intent(f,manifest); begin(f,first)
            repeat(3) {begin(f,intent(f,manifest))}
            val extra=intent(f,manifest); assertEquals(FileStatus.RESOURCE_EXHAUSTED,f.raw(request(extra,FileOperation.BEGIN_UPLOAD_BATCH).setDestinationId(f.target).setBatchHeader(extra).build()).status)
            val other=TestPeer.approve(f.policy,TestPeer.key()); val grant=f.grants.add(other,f.tree,f.data.getInt("flags"),"Second peer"); f.grants.update(grant.id,15)
            val target=f.raw(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_CONTEXT).build(),other.id).entriesList.single()
            val epoch=f.raw(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_BATCH_CONTEXT).build(),other.id).mutationEpoch
            repeat(4) {val h=UploadBatchRules.header(MutationRules.newId(),epoch,grant.id,manifest)
                assertEquals(FileStatus.FILE_STATUS_OK,f.raw(request(h,FileOperation.BEGIN_UPLOAD_BATCH).setDestinationId(target.id).setBatchHeader(h).build(),other.id).status)}
            assertEquals(8,f.grants.batchPlans.all().size); assertEquals(FileStatus.NOT_FOUND,f.raw(request(first,FileOperation.UPLOAD_BATCH_STATUS).build(),other.id).status)
            val third=TestPeer.approve(f.policy,TestPeer.key()); val thirdGrant=f.grants.add(third,f.tree,f.data.getInt("flags"),"Third peer"); f.grants.update(thirdGrant.id,15)
            val thirdTarget=f.raw(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_CONTEXT).build(),third.id).entriesList.single()
            val h=UploadBatchRules.header(MutationRules.newId(),epoch,thirdGrant.id,manifest)
            assertEquals(FileStatus.RESOURCE_EXHAUSTED,f.raw(request(h,FileOperation.BEGIN_UPLOAD_BATCH).setDestinationId(thirdTarget.id).setBatchHeader(h).build(),third.id).status)
        } finally {f.close()}
    }
    @Test fun readOnlyRevokedAndRepairedPairingsCannotUseEarlierReceipts()=runBlocking {
        val f=IncomingUploadsTest.Fixture(); try {
            val h=intent(f,plan()); begin(f,h); f.grants.update(f.grant.id,StorageRules.READ_ONLY)
            assertFalse(f.children().values.contains("folder"))
            assertEquals(FileStatus.ACCESS_DENIED,f.raw(request(h,FileOperation.UPLOAD_BATCH_STATUS).build()).status)
            f.grants.update(f.grant.id,15); assertEquals(FileStatus.FILE_STATUS_OK,f.raw(request(h,FileOperation.UPLOAD_BATCH_STATUS).build()).status)
            assertTrue(f.policy.revoke(setOf(f.policy.blockPeer(f.approval)))); f.grants.reconcileAccess()
            val approved=TestPeer.approve(f.policy,f.software); assertNotEquals(f.approval.epoch,approved.epoch)
            f.restart(selectTarget=false,reopenGrants=true)
            assertEquals(FileStatus.ACCESS_DENIED,f.raw(request(h,FileOperation.UPLOAD_BATCH_STATUS).build()).status)
        } finally {f.close()}
    }
    @Test fun movedDestinationAndExcludedNamesCannotSealAPlan()=runBlocking<Unit> {
        val f=IncomingUploadsTest.Fixture(); try {
            val excluded=plan().toBuilder().setItems(1,plan().itemsList[1].toBuilder().setName(".ssh")).build(); val h=intent(f,excluded)
            begin(f,h); upload(f,h,excluded); assertEquals(FileStatus.ACCESS_DENIED,f.raw(request(h,FileOperation.SEAL_UPLOAD_BATCH).build()).status)
            assertEquals(0,f.call(request(h,FileOperation.UPLOAD_BATCH_STATUS).build()).batch.manifestOffset)
            val uri=requireNotNull(Docs.createDocument(f.context.contentResolver,Docs.buildDocumentUriUsingTree(f.tree,Docs.getTreeDocumentId(f.tree)),Docs.Document.MIME_TYPE_DIR,"parent"))
            val folder=f.call(FileRequest.newBuilder().setOperation(FileOperation.LIST_DIRECTORY).setNodeId(f.target).build()).entriesList.single {it.name=="parent"}
            val moved=intent(f,plan()); f.target=folder.id; begin(f,moved)
            f.base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"change",Docs.getDocumentId(uri),Bundle().apply {putString("parent","outside")})
            assertNotEquals(FileStatus.FILE_STATUS_OK,f.raw(request(moved,FileOperation.UPLOAD_BATCH_STATUS).build()).status)
            f.base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"change",Docs.getDocumentId(uri),Bundle().apply {putString("parent",Docs.getTreeDocumentId(f.tree))})
        } finally {f.close()}
    }
    @Test fun corruptPlanBlocksBothAdmissionPathsUntilLocalArchiveRotatesTheNamespace()=runBlocking {
        val f=IncomingUploadsTest.Fixture(); try {
            val before=f.children(); val manifest=plan(); val h=intent(f,manifest); begin(f,h); upload(f,h,manifest); f.call(request(h,FileOperation.SEAL_UPLOAD_BATCH).build())
            val file=File(f.root,"incoming-batch-plans/${h.id}.json"); val value=JSONObject(file.readText()).put("manifest",Rules.b64(byteArrayOf(1,2,3))); file.writeText(value.toString())
            f.restart(reopenGrants=true); assertTrue(f.grants.batchPlans.recoveryIssues().isNotEmpty())
            assertNotEquals(FileStatus.FILE_STATUS_OK,f.raw(request(h,FileOperation.UPLOAD_BATCH_STATUS).build()).status)
            val ordinary=f.descriptor(byteArrayOf(1)); val attempt=FileRequest.newBuilder().setOperation(FileOperation.PREPARE_UPLOAD).setDestinationId(f.target).setTransferId(ordinary.id).setMutationEpoch(f.epoch).setUpload(ordinary).build()
            assertNotEquals(FileStatus.FILE_STATUS_OK,f.raw(attempt).status)
            f.grants.batchPlans.archiveHistory(); assertEquals(1,f.grants.batchPlans.preservedRecords()); assertTrue(f.grants.batchPlans.all().isEmpty())
            assertEquals(FileStatus.STALE_REFERENCE,f.raw(request(h,FileOperation.UPLOAD_BATCH_STATUS).build()).status); assertEquals(before,f.children())
            f.call(attempt); f.grants.batchPlans.clearPreservedRecords(); assertEquals(0,f.grants.batchPlans.preservedRecords()); assertEquals(before,f.children())
        } finally {f.close()}
    }
    @Test fun foregroundWithdrawalAndChangedIntentCannotStartAnotherPlan()=runBlocking {
        val f=IncomingUploadsTest.Fixture(12); try {
            val h=intent(f,plan()); begin(f,h); val changed=h.toBuilder().setTotalBytes(h.totalBytes+1).build()
            assertEquals(FileStatus.CONFLICT,f.raw(request(changed,FileOperation.BEGIN_UPLOAD_BATCH).setDestinationId(f.target).setBatchHeader(changed).build()).status)
            f.visible.set(false); f.service.suspendAvailability()
            assertEquals(FileStatus.ACCESS_DENIED,f.raw(request(h,FileOperation.CANCEL_UPLOAD_BATCH).build()).status); f.visible.set(true)
            assertEquals(UploadBatchPlanPhase.UPLOAD_PLAN_CANCELED,f.call(request(h,FileOperation.CANCEL_UPLOAD_BATCH).build()).batch.phase)
            assertTrue(f.call(FileRequest.newBuilder().setOperation(FileOperation.LIST_ROOTS).build()).entriesList.isEmpty())
            assertEquals(UploadBatchPlanPhase.UPLOAD_PLAN_CANCELED,begin(f,h).batch.phase); assertFalse(f.children().values.contains("folder"))
        } finally {f.close()}
    }
    @Test fun failedMetadataCommitRequiresLocalRecoveryWithoutCreatingProviderContent()=runBlocking {
        val f=IncomingUploadsTest.Fixture(); try {
            val before=f.children(); val h=intent(f,plan()); val blocker=File(f.root,"incoming-batch-plans/${h.id}.json.new"); check(blocker.mkdir())
            assertNotEquals(FileStatus.FILE_STATUS_OK,f.raw(request(h,FileOperation.BEGIN_UPLOAD_BATCH).setDestinationId(f.target).setBatchHeader(h).build()).status)
            assertTrue(f.grants.batchPlans.recoveryIssues().isNotEmpty()); assertTrue(f.grants.batchPlans.all().isEmpty()); assertEquals(before,f.children())
            check(blocker.delete()); f.grants.batchPlans.archiveHistory()
            assertEquals(FileStatus.STALE_REFERENCE,f.raw(request(h,FileOperation.UPLOAD_BATCH_STATUS).build()).status); assertEquals(before,f.children())
        } finally {f.close()}
    }
}
