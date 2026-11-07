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
class BatchDirectoryTest {
    @Test fun batchFileOperationsRequireAnIncomingHostAndCannotFallThroughToFileManagement()=runBlocking {
        val f=IncomingUploadsTest.Fixture(); val withoutFiles=AndroidFileService(f.context,f.grants)
        try {
            val before=f.children()
            for(op in listOf(FileOperation.PREPARE_BATCH_FILE,FileOperation.WRITE_BATCH_FILE,FileOperation.FINISH_BATCH_FILE,FileOperation.CANCEL_BATCH_FILE)) {
                val response=withoutFiles.execute(f.approval.id,FileRequest.newBuilder().setRequestId(StorageRules.token()).setOperation(op).build())
                assertEquals(FileStatus.UNSUPPORTED,response.status)
            }
            assertEquals(before,f.children()); assertTrue(f.incoming.state.value.isEmpty())
        } finally {withoutFiles.close(); f.close()}
    }
    private fun plan(name:String="copied-tree",nested:Boolean=true,collision:UploadCollision=UploadCollision.UPLOAD_KEEP_BOTH)=UploadBatchManifest.newBuilder().setCollision(collision)
        .addItems(UploadBatchItem.newBuilder().setParent(-1).setName(name).setIsDirectory(true)).apply {
            if(nested) addItems(UploadBatchItem.newBuilder().setParent(0).setName("empty-文書").setIsDirectory(true))
        }.build()
    private fun request(h:UploadBatchHeader,op:FileOperation)=FileRequest.newBuilder().setOperation(op).setTransferId(h.id).setMutationEpoch(h.epoch)
    private suspend fun accepted(f:IncomingUploadsTest.Fixture,manifest:UploadBatchManifest=plan()):UploadBatchHeader {
        f.initialize(); val context=f.call(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_BATCH_CONTEXT).build())
        val h=UploadBatchRules.header(MutationRules.newId(),context.mutationEpoch,f.grant.id,manifest)
        f.call(request(h,FileOperation.BEGIN_UPLOAD_BATCH).setDestinationId(f.target).setBatchHeader(h).build())
        val bytes=UploadBatchRules.encode(manifest); var offset=0
        while(offset<bytes.size) {val count=minOf(StorageRules.RANGE,bytes.size-offset); f.call(request(h,FileOperation.WRITE_UPLOAD_MANIFEST).setOffset(offset.toLong()).setContent(ByteString.copyFrom(bytes,offset,count)).build()); offset+=count}
        assertFalse(f.call(request(h,FileOperation.SEAL_UPLOAD_BATCH).build()).batch.effectsStarted); return h
    }
    private suspend fun create(f:IncomingUploadsTest.Fixture,h:UploadBatchHeader,index:Int=0)=f.call(request(h,FileOperation.CREATE_BATCH_DIRECTORY).setBatchItemIndex(index).build()).also {
        UploadBatchRules.checkItemReceipt(f.approval.id,h,index,true,it)
    }.batchItem
    private suspend fun status(f:IncomingUploadsTest.Fixture,h:UploadBatchHeader,index:Int=0)=f.raw(request(h,FileOperation.UPLOAD_BATCH_ITEM_STATUS).setBatchItemIndex(index).build())
    private fun childFile(f:IncomingUploadsTest.Fixture,h:UploadBatchHeader,index:Int=0)=File(f.root,"incoming-batch-plans/${h.id}.$index.json")
    private fun indexFile(f:IncomingUploadsTest.Fixture,h:UploadBatchHeader)=File(f.root,"incoming-batch-plans/${h.id}.children.json")
    private fun change(f:IncomingUploadsTest.Fixture,id:String,key:String,value:String) {f.base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"change",id,Bundle().apply {putString(key,value)})}
    @Test fun nestedEmptyFoldersRecoverExactProviderIdsAndNeverReplay()=runBlocking {
        val f=IncomingUploadsTest.Fixture(); try {
            val h=accepted(f); val before=f.children()
            assertEquals(FileStatus.NOT_FOUND,status(f,h).status); assertEquals(before,f.children())
            assertEquals(FileStatus.CONFLICT,f.raw(request(h,FileOperation.CREATE_BATCH_DIRECTORY).setBatchItemIndex(1).build()).status)
            val first=create(f,h); val second=create(f,h,1); assertEquals(UploadBatchItemPhase.BATCH_ITEM_SUCCEEDED,first.phase); assertEquals(UploadBatchItemPhase.BATCH_ITEM_SUCCEEDED,second.phase)
            val root=f.children().entries.single {it.value==first.resultingName}.key; val nested=f.children(root).entries.single {it.value==second.resultingName}.key; assertTrue(f.children(nested).isEmpty())
            assertTrue(f.call(request(h,FileOperation.UPLOAD_BATCH_STATUS).build()).batch.effectsStarted)
            assertEquals(FileStatus.CONFLICT,f.raw(request(h,FileOperation.CANCEL_UPLOAD_BATCH).build()).status)
            val after=f.children(); f.restart(reopenGrants=true); assertEquals(first,create(f,h)); assertEquals(second,create(f,h,1)); assertEquals(after,f.children())
            assertTrue(f.incoming.state.value.isEmpty()); f.grants.batchPlans.archiveHistory(); assertEquals(after,f.children()); assertEquals(FileStatus.STALE_REFERENCE,status(f,h).status)
            f.grants.batchPlans.clearPreservedRecords(); assertEquals(0,f.grants.batchPlans.preservedRecords()); assertEquals(after,f.children())
        } finally {f.close()}
    }
    private suspend fun collision(choice:UploadCollision,phase:UploadBatchItemPhase) {
        val f=IncomingUploadsTest.Fixture(); try {
            val parent=f.children().entries.single {it.value=="Reports"}.key; val existing=f.children(parent); val h=accepted(f,plan("Reports",true,choice)); val result=create(f,h)
            assertEquals(phase,result.phase); assertEquals(existing,f.children(parent))
            if(phase==UploadBatchItemPhase.BATCH_ITEM_SUCCEEDED) {assertNotEquals("Reports",result.resultingName); assertEquals(UploadBatchItemPhase.BATCH_ITEM_SUCCEEDED,create(f,h,1).phase)}
            else {assertEquals(FileStatus.CONFLICT,f.raw(request(h,FileOperation.CREATE_BATCH_DIRECTORY).setBatchItemIndex(1).build()).status); assertEquals("",result.resultingName)}
            f.restart(reopenGrants=true); assertEquals(result,status(f,h).batchItem); assertEquals(existing,f.children(parent))
        } finally {f.close()}
    }
    @Test fun skipNeverMergesAnExistingFolder()=runBlocking {collision(UploadCollision.UPLOAD_SKIP,UploadBatchItemPhase.BATCH_ITEM_SKIPPED)}
    @Test fun failNeverMergesAnExistingFolder()=runBlocking {collision(UploadCollision.UPLOAD_FAIL,UploadBatchItemPhase.BATCH_ITEM_FAILED)}
    @Test fun keepBothCreatesSeparateEmptyStructure()=runBlocking {collision(UploadCollision.UPLOAD_KEEP_BOTH,UploadBatchItemPhase.BATCH_ITEM_SUCCEEDED)}
    @Test fun receiveOnlyFolderNamesAndStatusDoNotRevealOwnerActivity()=runBlocking {
        val f=IncomingUploadsTest.Fixture(12); try {
            val h=accepted(f,plan("Reports",false,UploadCollision.UPLOAD_SKIP)); val result=create(f,h); assertEquals(UploadBatchItemPhase.BATCH_ITEM_SUCCEEDED,result.phase); assertTrue(result.resultingName.startsWith("Reports ("))
            val provider=f.children().entries.single {it.value==result.resultingName}.key
            requireNotNull(Docs.createDocument(f.context.contentResolver,Docs.buildDocumentUriUsingTree(f.tree,provider),"text/plain","owner.txt"))
            assertEquals(result,status(f,h).batchItem); assertTrue(f.call(FileRequest.newBuilder().setOperation(FileOperation.LIST_ROOTS).build()).entriesList.isEmpty())
        } finally {f.close()}
    }
    private suspend fun uncertain(mode:String) {
        val f=IncomingUploadsTest.Fixture(); try {
            val h=accepted(f); val before=f.children(); f.mode(mode); val receipt=create(f,h)
            assertEquals(UploadBatchItemPhase.BATCH_ITEM_REVIEW,receipt.phase); f.mode(""); val after=f.children()
            assertEquals(receipt,create(f,h)); f.restart(reopenGrants=true); assertEquals(receipt,create(f,h)); assertEquals(after,f.children())
            assertTrue(after.size>=before.size); assertEquals(FileStatus.CONFLICT,f.raw(request(h,FileOperation.CREATE_BATCH_DIRECTORY).setBatchItemIndex(1).build()).status)
        } finally {f.close()}
    }
    @Test fun lostProviderReceiptNeverCreatesAnotherFolder()=runBlocking {uncertain("loseFolderReceipt")}
    @Test fun returnedExistingDocumentIsKeptAndNotAdopted()=runBlocking {uncertain("existing")}
    @Test fun returnedRegularFileCannotBecomeDirectoryReceipt()=runBlocking {uncertain("fileForFolder")}
    @Test fun returnedFolderUnderAnotherParentCannotBeAdopted()=runBlocking {uncertain("misplacedFolder")}
    @Test fun returnedNonemptyFolderIsKeptWithoutAdoptingItsContents()=runBlocking {uncertain("nonemptyFolder")}
    @Test fun renamedAncestorAndSameNameReplacementCannotResumeChildren()=runBlocking {
        val f=IncomingUploadsTest.Fixture(); try {
            val h=accepted(f); val result=create(f,h); val id=f.children().entries.single {it.value==result.resultingName}.key
            change(f,id,"name","renamed-owner-folder"); requireNotNull(Docs.createDocument(f.context.contentResolver,Docs.buildDocumentUriUsingTree(f.tree,Docs.getTreeDocumentId(f.tree)),Docs.Document.MIME_TYPE_DIR,result.resultingName))
            val before=f.children(); assertEquals(FileStatus.STALE_REFERENCE,status(f,h).status); assertEquals(FileStatus.STALE_REFERENCE,f.raw(request(h,FileOperation.CREATE_BATCH_DIRECTORY).setBatchItemIndex(1).build()).status); assertEquals(before,f.children())
            f.restart(reopenGrants=true); assertNotEquals(FileStatus.FILE_STATUS_OK,status(f,h).status); assertEquals(before,f.children())
        } finally {f.close()}
    }
    private suspend fun lostMetadata(kind:String) {
        val f=IncomingUploadsTest.Fixture(); try {
            val h=accepted(f); create(f,h); val before=f.children()
            when(kind) {
                "receipt"->check(childFile(f,h).delete())
                "index"->check(indexFile(f,h).delete())
                else->{check(childFile(f,h).delete()); val file=indexFile(f,h); val json=JSONObject(file.readText()); json.put("bits",Rules.b64(byteArrayOf(0))); file.writeText(json.toString())}
            }
            assertEquals(FileStatus.CONFLICT,f.raw(request(h,FileOperation.CREATE_BATCH_DIRECTORY).setBatchItemIndex(0).build()).status); assertTrue(f.grants.batchPlans.recoveryIssues().isNotEmpty()); assertEquals(before,f.children())
            f.restart(reopenGrants=true); assertTrue(f.grants.batchPlans.recoveryIssues().isNotEmpty()); assertNotEquals(FileStatus.FILE_STATUS_OK,status(f,h).status); assertEquals(before,f.children())
        } finally {f.close()}
    }
    @Test fun missingLeafReceiptBlocksRuntimeAndColdReplay()=runBlocking {lostMetadata("receipt")}
    @Test fun missingAttemptIndexBlocksRuntimeAndColdReplay()=runBlocking {lostMetadata("index")}
    @Test fun corruptAttemptBitsCannotHideAMissingReceipt()=runBlocking {lostMetadata("corrupt")}
    @Test fun pendingReceiptAfterRestartRequiresReviewWithoutAdoptingByName()=runBlocking {
        val f=IncomingUploadsTest.Fixture(); try {
            val h=accepted(f); create(f,h); val before=f.children(); val file=childFile(f,h); val json=JSONObject(file.readText()).put("phase",UploadBatchItemPhase.BATCH_ITEM_PENDING.name).put("document","")
            file.writeText(json.toString()); f.restart(reopenGrants=true); assertEquals(UploadBatchItemPhase.BATCH_ITEM_REVIEW,create(f,h).phase); assertEquals(before,f.children())
        } finally {f.close()}
    }
    @Test fun missingParentReceiptWithRetainedDescendantBlocksRecovery()=runBlocking {
        val f=IncomingUploadsTest.Fixture(); try {
            val h=accepted(f); create(f,h); create(f,h,1); val before=f.children(); check(childFile(f,h).delete()); f.restart(reopenGrants=true)
            assertTrue(f.grants.batchPlans.recoveryIssues().isNotEmpty()); assertNotEquals(FileStatus.FILE_STATUS_OK,status(f,h,1).status); assertEquals(before,f.children())
        } finally {f.close()}
    }
    @Test fun wrongIndexMissingIndexAndFileKindHaveNoProviderEffects()=runBlocking {
        val f=IncomingUploadsTest.Fixture(); try {
            val manifest=plan().toBuilder().addItems(UploadBatchItem.newBuilder().setParent(-1).setName("file.bin").setSha256(ByteString.copyFrom(Rules.digest(byteArrayOf())))).build()
            val h=accepted(f,manifest); val before=f.children()
            listOf(request(h,FileOperation.CREATE_BATCH_DIRECTORY),request(h,FileOperation.CREATE_BATCH_DIRECTORY).setBatchItemIndex(4096),request(h,FileOperation.CREATE_BATCH_DIRECTORY).setBatchItemIndex(2)).forEach {assertNotEquals(FileStatus.FILE_STATUS_OK,f.raw(it.build()).status)}
            assertFalse(f.call(request(h,FileOperation.UPLOAD_BATCH_STATUS).build()).batch.effectsStarted); assertEquals(before,f.children())
        } finally {f.close()}
    }
    @Test fun currentGrantPermissionsAndExclusionsApplyToRetainedFolders()=runBlocking {
        val f=IncomingUploadsTest.Fixture(); try {
            val h=accepted(f); create(f,h); val before=f.children(); f.grants.update(f.grant.id,StorageRules.READ_ONLY)
            assertEquals(FileStatus.ACCESS_DENIED,status(f,h).status); f.grants.update(f.grant.id,15)
            f.grants.update(f.grant.id,15,setOf("copied-tree")); assertEquals(FileStatus.ACCESS_DENIED,status(f,h).status)
            assertEquals(FileStatus.ACCESS_DENIED,f.raw(request(h,FileOperation.CREATE_BATCH_DIRECTORY).setBatchItemIndex(1).build()).status); f.grants.update(f.grant.id,15)
            val id=f.children().entries.single {it.value=="copied-tree"}.key; change(f,id,"parent","outside")
            assertNotEquals(FileStatus.FILE_STATUS_OK,f.raw(request(h,FileOperation.CREATE_BATCH_DIRECTORY).setBatchItemIndex(1).build()).status)
            change(f,id,"parent",Docs.getTreeDocumentId(f.tree)); assertEquals(before,f.children())
        } finally {f.close()}
    }
    @Test fun damagedChildReceiptBlocksAllIncomingWritesUntilReview()=runBlocking {
        val f=IncomingUploadsTest.Fixture(); try {
            val h=accepted(f); create(f,h); val before=f.children(); childFile(f,h).writeText("{broken")
            assertNotEquals(FileStatus.FILE_STATUS_OK,status(f,h).status); assertTrue(f.grants.batchPlans.recoveryIssues().isNotEmpty())
            val file=f.descriptor(byteArrayOf(1)); assertNotEquals(FileStatus.FILE_STATUS_OK,f.raw(FileRequest.newBuilder().setOperation(FileOperation.PREPARE_UPLOAD)
                .setDestinationId(f.target).setMutationEpoch(f.epoch).setTransferId(file.id).setUpload(file).build()).status)
            f.restart(reopenGrants=true); assertTrue(f.grants.batchPlans.recoveryIssues().isNotEmpty()); assertEquals(before,f.children())
        } finally {f.close()}
    }
    @Test fun foregroundWithdrawalBeforeCreationLeavesNoChildAttempt()=runBlocking {
        val f=IncomingUploadsTest.Fixture(); try {
            val h=accepted(f); val before=f.children(); f.visible.set(false); f.service.suspendAvailability()
            assertEquals(FileStatus.ACCESS_DENIED,f.raw(request(h,FileOperation.CREATE_BATCH_DIRECTORY).setBatchItemIndex(0).build()).status)
            f.visible.set(true); assertFalse(f.call(request(h,FileOperation.UPLOAD_BATCH_STATUS).build()).batch.effectsStarted); assertEquals(before,f.children())
            assertFalse(childFile(f,h).exists()); assertFalse(indexFile(f,h).exists())
        } finally {f.close()}
    }
    @Test fun changedAcceptedManifestInvalidatesTheCacheAndBlocksCreation()=runBlocking {
        val f=IncomingUploadsTest.Fixture(); try {
            val h=accepted(f); create(f,h); val before=f.children()
            val file=File(f.root,"incoming-batch-plans/${h.id}.json"); val json=JSONObject(file.readText()).put("manifest",Rules.b64(byteArrayOf(1,2,3))); file.writeText(json.toString())
            assertNotEquals(FileStatus.FILE_STATUS_OK,f.raw(request(h,FileOperation.CREATE_BATCH_DIRECTORY).setBatchItemIndex(1).build()).status)
            assertTrue(f.grants.batchPlans.recoveryIssues().isNotEmpty()); assertEquals(before,f.children())
            f.restart(reopenGrants=true); assertTrue(f.grants.batchPlans.recoveryIssues().isNotEmpty()); assertEquals(before,f.children())
        } finally {f.close()}
    }
    @Test fun full4096DirectoryPlanCreatesActualProviderFoldersAndReopensLastReceipt()=runBlocking {
        val f=IncomingUploadsTest.Fixture(); try {
            // 128 roots with 31 children each: exact item/root bounds without a quadratic sibling listing.
            val manifest=UploadBatchManifest.newBuilder().setCollision(UploadCollision.UPLOAD_KEEP_BOTH).apply {
                repeat(128) {root->val parent=itemsCount; addItems(UploadBatchItem.newBuilder().setParent(-1).setName("capacity-$root").setIsDirectory(true))
                    repeat(31) {child->addItems(UploadBatchItem.newBuilder().setParent(parent).setName("empty-$child").setIsDirectory(true))}}
            }.build()
            val h=accepted(f,manifest); repeat(4096) {index->val result=create(f,h,index); assertEquals("item $index: ${result.message}",UploadBatchItemPhase.BATCH_ITEM_SUCCEEDED,result.phase)}
            val roots=f.children().filterValues {it.startsWith("capacity-")}; assertEquals(128,roots.size)
            roots.keys.forEach {assertEquals(31,f.children(it).size)}
            val before=f.children(); f.restart(reopenGrants=true); assertTrue(f.grants.batchPlans.recoveryIssues().isEmpty())
            assertEquals(UploadBatchItemPhase.BATCH_ITEM_SUCCEEDED,status(f,h,4095).batchItem.phase); assertEquals(before,f.children()); assertTrue(f.incoming.state.value.isEmpty())
        } finally {f.close()}
    }
}
