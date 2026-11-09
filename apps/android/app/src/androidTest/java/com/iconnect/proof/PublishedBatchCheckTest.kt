package com.iconnect.proof

import android.net.Uri
import android.os.Bundle
import android.provider.DocumentsContract as Docs
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.google.protobuf.ByteString
import com.iconnect.protocol.*
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

@RunWith(AndroidJUnit4::class)
class PublishedBatchCheckTest {
    private data class Saved(val header:UploadBatchHeader,val folder:String,val file:String,val empty:String)
    private fun op(h:UploadBatchHeader,operation:FileOperation,index:Int?=null)=FileRequest.newBuilder().setOperation(operation)
        .setTransferId(h.id).setMutationEpoch(h.epoch).also {if(index!=null) it.setBatchItemIndex(index)}
    private fun uri(f:IncomingUploadsTest.Fixture,id:String)=Docs.buildDocumentUriUsingTree(f.tree,id)
    private suspend fun publish(f:IncomingUploadsTest.Fixture):Saved {
        f.initialize()
        val bytes="abc".toByteArray()
        val manifest=UploadBatchManifest.newBuilder().setCollision(UploadCollision.UPLOAD_KEEP_BOTH)
            .addItems(UploadBatchItem.newBuilder().setParent(-1).setName("batch-root").setIsDirectory(true))
            .addItems(UploadBatchItem.newBuilder().setParent(0).setName("payload.bin").setSize(3).setSha256(ByteString.copyFrom(Rules.digest(bytes))))
            .addItems(UploadBatchItem.newBuilder().setParent(0).setName("empty").setIsDirectory(true)).build()
        val epoch=f.call(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_BATCH_CONTEXT).build()).mutationEpoch
        val h=UploadBatchRules.header(MutationRules.newId(),epoch,f.grant.id,manifest)
        f.call(op(h,FileOperation.BEGIN_UPLOAD_BATCH).setDestinationId(f.target).setBatchHeader(h).build())
        f.call(op(h,FileOperation.WRITE_UPLOAD_MANIFEST).setContent(ByteString.copyFrom(UploadBatchRules.encode(manifest))).build())
        f.call(op(h,FileOperation.SEAL_UPLOAD_BATCH).build())
        val folderName=f.call(op(h,FileOperation.CREATE_BATCH_DIRECTORY,0).build()).batchItem.resultingName
        val folder=f.children().entries.single {it.value==folderName}.key
        f.call(op(h,FileOperation.PREPARE_BATCH_FILE,1).build())
        f.call(op(h,FileOperation.WRITE_BATCH_FILE,1).setContent(ByteString.copyFrom(bytes)).build())
        f.call(op(h,FileOperation.FINISH_BATCH_FILE,1).build())
        withTimeout(20_000) {f.grants.batchPlans.whenFilesIdle()}
        val fileName=f.call(op(h,FileOperation.UPLOAD_BATCH_ITEM_STATUS,1).build()).upload.finalName
        val file=f.children(folder).entries.single {it.value==fileName}.key
        val emptyName=f.call(op(h,FileOperation.CREATE_BATCH_DIRECTORY,2).build()).batchItem.resultingName
        val empty=f.children(folder).entries.single {it.value==emptyName}.key
        val done=f.call(op(h,FileOperation.FINISH_UPLOAD_BATCH).build()).batch
        assertArrayEquals(byteArrayOf(1,1,1),done.itemOutcomes.toByteArray())
        return Saved(h,folder,file,empty)
    }
    private fun request(s:Saved)=op(s.header,FileOperation.VERIFY_PUBLISHED_BATCH).setBatchHeader(s.header).setMutationId(StorageRules.token()).build()
    private suspend fun collect(f:IncomingUploadsTest.Fixture,request:FileRequest):FileResponse=withTimeout(20_000) {
        while(true) {
            val result=f.raw(request)
            if(result.status!=FileStatus.FILE_STATUS_OK || result.publishedBatchCheck.verified) return@withTimeout result
            delay(20)
        }
        @Suppress("UNREACHABLE_CODE") error("No result")
    }
    private suspend fun fixture(mask:Int=15,action:suspend (IncomingUploadsTest.Fixture)->Unit) {
        val f=IncomingUploadsTest.Fixture(mask); try {action(f)} finally {f.close()}
    }
    private fun history(f:IncomingUploadsTest.Fixture)=File(f.root,"incoming-batch-plans").listFiles()!!.associate {it.name to it.readBytes().toList()}
    // Raw operation value also lets this one method run against the pre-cancellation app.
    private suspend fun stop(f:IncomingUploadsTest.Fixture,original:FileRequest):FileResponse=withTimeout(5_000) {
        val value=original.toBuilder().setOperationValue(47).build()
        while(true) {
            val reply=f.raw(value)
            assertEquals("Stopping a saved batch check must be supported.",FileStatus.FILE_STATUS_OK,reply.status)
            PhoneTransferManifests.validateResponse(reply)
            assertFalse(reply.publishedBatchCheck.verified)
            if(reply.publishedBatchCheck.canceled) return@withTimeout reply
            delay(10)
        }
        @Suppress("UNREACHABLE_CODE") error("No stop acknowledgement")
    }
    private suspend fun blocked(f:IncomingUploadsTest.Fixture,saved:Saved) {
        withTimeout(5_000) {while(!f.base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"openProbe",saved.file,null)!!.getBoolean("entered")) delay(10)}
        assertTrue(f.base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"openProbe",saved.file,null)!!.getBoolean("cancelable"))
    }
    private fun release(f:IncomingUploadsTest.Fixture,saved:Saved) {
        f.base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"openProbe",saved.file,Bundle().apply {putBoolean("release",true)})
        f.mode("")
    }

    @Test fun cancelingSavedBatchCheckReleasesBlockedReadsAndPreservesCopiesAndReceipts()=runBlocking {fixture {f->
        val saved=publish(f); val before=history(f); val value=request(saved)
        try {
            f.mode("blockRead"); assertFalse(f.call(value).publishedBatchCheck.verified); blocked(f,saved)
            val stopped=stop(f,value)
            assertEquals(value.mutationId,stopped.publishedBatchCheck.challenge); assertEquals(saved.header,stopped.batch.header)
            assertTrue(stopped.content.isEmpty); assertEquals(0,stopped.entriesCount)
            withTimeout(3_000) {f.grants.batchPlans.whenFilesIdle()}
            assertFalse(f.base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"openProbe",saved.file,null)!!.getBoolean("entered"))
            val first=f.grants.reserveIncomingVerification()!!; val second=f.grants.reserveIncomingVerification()!!
            try {assertNull(f.grants.reserveIncomingVerification())} finally {first.close(); second.close()}
            release(f,saved); assertEquals(before,history(f)); assertArrayEquals("abc".toByteArray(),f.read(f.children(saved.folder).getValue(saved.file),saved.folder))
            assertTrue(stop(f,value).publishedBatchCheck.canceled)
            assertTrue(collect(f,request(saved)).publishedBatchCheck.verified)
            withTimeout(3_000) {f.grants.batchPlans.whenFilesIdle()}; assertEquals(before,history(f))
        } finally {release(f,saved)}
    }}

    @Test fun cancelingSavedBatchCheckRejectsForeignOrAlteredRequests()=runBlocking {fixture {f->
        val saved=publish(f); val before=history(f); val value=request(saved)
        try {
            f.mode("blockRead"); f.call(value); blocked(f,saved)
            for(change in listOf("bad-challenge","foreign-challenge","header","item","destination","node","name","version","continuation","query","deleted","offset","length","content","upload","epoch","peer")) {
                val bad=value.toBuilder().setOperationValue(47)
                when(change) {
                    "bad-challenge"->bad.setMutationId("not-a-token")
                    "foreign-challenge"->bad.setMutationId(StorageRules.token())
                    "header"->bad.setBatchHeader(saved.header.toBuilder().setTotalBytes(4))
                    "item"->bad.setBatchItemIndex(0)
                    "destination"->bad.setDestinationId(StorageRules.token())
                    "node"->bad.setNodeId(StorageRules.token())
                    "name"->bad.setName("another.bin")
                    "version"->bad.setExpectedVersion("a".repeat(64))
                    "continuation"->bad.setContinuation(StorageRules.token())
                    "query"->bad.setQuery("another")
                    "deleted"->bad.setDeletedId(StorageRules.token())
                    "offset"->bad.setOffset(1)
                    "length"->bad.setLength(1)
                    "content"->bad.setContent(ByteString.copyFrom(byteArrayOf(1)))
                    "upload"->bad.setUpload(UploadInfo.getDefaultInstance())
                    "epoch"->bad.setMutationEpoch(StorageRules.token())
                }
                val denied=f.raw(bad.build(),if(change=="peer") "d".repeat(64) else f.approval.id)
                assertNotEquals(change,FileStatus.FILE_STATUS_OK,denied.status); assertFalse(denied.hasPublishedBatchCheck())
                assertTrue(change,f.base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"openProbe",saved.file,null)!!.getBoolean("entered"))
            }
            assertTrue(stop(f,value).publishedBatchCheck.canceled)
            withTimeout(3_000) {f.grants.batchPlans.whenFilesIdle()}; assertEquals(before,history(f))
        } finally {release(f,saved)}
    }}

    @Test fun cancelingSavedBatchCheckCannotTakeOverAnotherSession()=runBlocking {fixture {f->
        val saved=publish(f); val value=request(saved)
        val other=AndroidFileService(f.context,f.grants,incoming=f.incoming)
        try {
            f.mode("blockRead"); f.call(value); blocked(f,saved)
            val reply=other.execute(f.approval.id,value.toBuilder().setRequestId(StorageRules.token()).setOperationValue(47).build())
            assertEquals(FileStatus.CONFLICT,reply.status)
            assertTrue(f.base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"openProbe",saved.file,null)!!.getBoolean("entered"))
            assertTrue(stop(f,value).publishedBatchCheck.canceled)
        } finally {release(f,saved); other.close()}
    }}

    @Test fun cancellationFrameInterruptsBlockedCollectionBeforeTheRpcWorkerCanHandleIt()=runBlocking {fixture {f->
        val saved=publish(f); val before=history(f); val value=request(saved)
        val scope=CoroutineScope(SupervisorJob()+Dispatchers.IO)
        lateinit var client:RemoteFiles; lateinit var server:RemoteFiles
        val failures=java.util.concurrent.ConcurrentLinkedQueue<String>()
        client=RemoteFiles({kind,payload->server.handle(Rules.parse(Rules.pack(kind,payload)))},scope,f.approval.id,null,{failures.add(it)})
        server=RemoteFiles({kind,payload->client.handle(Rules.parse(Rules.pack(kind,payload)))},scope,f.approval.id,f.service,{failures.add(it)})
        var collector:Deferred<FileResponse>?=null
        try {
            // Observe the first hash read without changing bytes or metadata.
            f.base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"readFault",saved.file,Bundle().apply {putString("fault","observe")})
            assertFalse(f.call(value).publishedBatchCheck.verified)
            withTimeout(5_000) {while(f.base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"readCount",saved.file,null)!!.getInt("count")<1) delay(10)}
            f.mode("blockRead")
            collector=scope.async {withTimeout(10_000) {
                while(true) {
                    val reply=client.request(value)
                    if(reply.status!=FileStatus.FILE_STATUS_OK || reply.publishedBatchCheck.verified) return@withTimeout reply
                    delay(10)
                }
                @Suppress("UNREACHABLE_CODE") error("No collection response")
            }}
            blocked(f,saved)
            withTimeout(3_000) {
                while(true) {
                    val reply=client.request(value.toBuilder().setOperationValue(47).build())
                    assertEquals(reply.message,FileStatus.FILE_STATUS_OK,reply.status)
                    if(reply.publishedBatchCheck.canceled) break
                    delay(10)
                }
            }
            assertEquals(FileStatus.CONFLICT,collector.await().status)
            withTimeout(3_000) {f.grants.batchPlans.whenFilesIdle()}
            assertTrue(failures.toString(),failures.isEmpty()); assertEquals(before,history(f))
        } finally {
            release(f,saved); collector?.cancelAndJoin(); client.close(); server.close(); scope.cancel()
        }
    }}

    @Test fun verifiesActualCollisionAndBlindNamesWithoutChangingCopiesOrMetadata()=runBlocking {
        for(mask in listOf(15,12)) fixture(mask) {f->
            val seed=Docs.createDocument(f.context.contentResolver,uri(f,Docs.getTreeDocumentId(f.tree)),Docs.Document.MIME_TYPE_DIR,"batch-root")!!
            val saved=publish(f); val before=history(f); val request=request(saved)
            val pending=f.call(request); assertFalse(pending.publishedBatchCheck.verified)
            val result=collect(f,request); assertEquals(FileStatus.FILE_STATUS_OK,result.status); PhoneTransferManifests.validateResponse(result)
            assertTrue(result.publishedBatchCheck.verified); assertEquals(request.mutationId,result.publishedBatchCheck.challenge)
            assertEquals(saved.header,result.batch.header); assertTrue(result.content.isEmpty); assertEquals(0,result.entriesCount)
            withTimeout(10_000) {f.grants.batchPlans.whenFilesIdle()}
            assertEquals(before,history(f)); assertTrue(f.children(Docs.getDocumentId(seed)).isEmpty())
            assertArrayEquals("abc".toByteArray(),f.context.contentResolver.openInputStream(uri(f,saved.file))!!.use {it.readBytes()})
            assertFalse(f.call(request).publishedBatchCheck.verified)
        }
    }
    @Test fun refusesChangedBytesNamesMembershipAndDocumentReplacement()=runBlocking {
        for(change in listOf("bytes","name","extra","missing","replacement","move")) fixture {f->
            val saved=publish(f)
            when(change) {
                "bytes"->f.context.contentResolver.openOutputStream(uri(f,saved.file),"wt")!!.use {it.write("xyz".toByteArray())}
                "name"->f.base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"change",saved.file,Bundle().apply {putString("name","owner.bin")})
                "extra"->Docs.createDocument(f.context.contentResolver,uri(f,saved.folder),"text/plain","owner.txt")
                "missing"->f.base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"remove",saved.empty,null)
                "replacement"->{val name=f.children(saved.folder).getValue(saved.file); f.base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"remove",saved.file,null)
                    val replacement=Docs.createDocument(f.context.contentResolver,uri(f,saved.folder),"application/octet-stream",name)!!
                    f.context.contentResolver.openOutputStream(replacement,"wt")!!.use {it.write("abc".toByteArray())}}
                "move"->f.base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"change",saved.file,Bundle().apply {putString("parent",Docs.getTreeDocumentId(f.tree))})
            }
            val before=history(f); val request=request(saved); assertFalse(f.call(request).publishedBatchCheck.verified)
            val result=collect(f,request); assertEquals(change,FileStatus.CONFLICT,result.status); assertFalse(result.hasPublishedBatchCheck())
            withTimeout(10_000) {f.grants.batchPlans.whenFilesIdle()}; assertEquals(before,history(f))
            assertEquals(UploadBatchPlanPhase.UPLOAD_PLAN_COMPLETED,f.grants.batchPlans.all().single().phase)
        }
    }
    @Test fun rechecksProviderFilesAndNamesWhenTheResultIsCollected()=runBlocking {
        for(fault in listOf("content","name","kind")) fixture {f->
            val saved=publish(f); f.base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"readFault",saved.file,Bundle().apply {putString("fault",fault)})
            val result=collect(f,request(saved)); assertEquals(fault,FileStatus.CONFLICT,result.status)
            val reads=f.base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"readCount",saved.file,null)!!.getInt("count")
            assertEquals("The mutation must occur at collection, after the first hash read.",2,reads)
            withTimeout(10_000) {f.grants.batchPlans.whenFilesIdle()}; assertTrue(f.children(saved.folder).containsKey(saved.file))
        }
    }
    @Test fun requiresOriginalIntentCurrentGrantAndSessionAndSharesVerificationSlots()=runBlocking {fixture {f->
        val saved=publish(f); val value=request(saved)
        assertEquals(FileStatus.ACCESS_DENIED,f.raw(value,"d".repeat(64)).status)
        assertEquals(FileStatus.INVALID_REQUEST,f.raw(value.toBuilder().setBatchHeader(saved.header.toBuilder().setTotalBytes(4)).build()).status)
        assertEquals(FileStatus.INVALID_REQUEST,f.raw(value.toBuilder().setBatchItemIndex(0).build()).status)
        val first=f.grants.reserveIncomingVerification()!!; val second=f.grants.reserveIncomingVerification()!!
        try {assertEquals(FileStatus.RESOURCE_EXHAUSTED,f.raw(value).status)} finally {first.close(); second.close()}
        assertFalse(f.call(value).publishedBatchCheck.verified)
        assertEquals(FileStatus.CONFLICT,f.raw(value.toBuilder().setMutationId(StorageRules.token()).build()).status)
        f.service.suspendAvailability(); withTimeout(10_000) {f.grants.batchPlans.whenFilesIdle()}
        f.restart(); assertFalse(f.call(value).publishedBatchCheck.verified)
        f.grants.update(f.grant.id,StorageRules.READ_ONLY)
        assertEquals(FileStatus.ACCESS_DENIED,f.raw(value).status)
        withTimeout(10_000) {f.grants.batchPlans.whenFilesIdle()}
    }}
    @Test fun refusesRetiredHistoryAndUncommittedMetadataWithoutRepairingIt()=runBlocking {fixture {f->
        val saved=publish(f); val before=history(f)
        val journal=File(f.root,"incoming-batch-plans/${saved.header.id}.1.upload.json")
        val pending=File(journal.path+".bak"); pending.writeText("preserve this backup")
        val result=collect(f,request(saved)); assertEquals(FileStatus.CONFLICT,result.status)
        withTimeout(10_000) {f.grants.batchPlans.whenFilesIdle()}
        assertEquals("preserve this backup",pending.readText()); assertEquals(before[journal.name],journal.readBytes().toList())
        assertTrue(pending.delete())
        val review=f.grants.batchPlans.reviewRetirement(saved.header.id)
        if(!f.grants.batchPlans.confirmRetirement(review)) while(!f.grants.batchPlans.continueRetirement(saved.header.id)) yield()
        assertEquals(FileStatus.NOT_FOUND,f.raw(request(saved)).status)
    }}
    @Test fun sessionWithdrawalCancelsBlockedProviderReadsAndReleasesTheSharedSlot()=runBlocking {fixture {f->
        val saved=publish(f); val before=history(f)
        try {
            f.mode("blockRead"); assertFalse(f.call(request(saved)).publishedBatchCheck.verified)
            withTimeout(5_000) {while(!f.base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"openProbe",saved.file,null)!!.getBoolean("entered")) delay(10)}
            assertTrue(f.base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"openProbe",saved.file,null)!!.getBoolean("cancelable"))
            f.service.suspendAvailability()
            withTimeout(3_000) {f.grants.batchPlans.whenFilesIdle()}
            assertFalse(f.base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"openProbe",saved.file,null)!!.getBoolean("entered"))
            f.mode(""); f.restart()
            assertTrue(collect(f,request(saved)).publishedBatchCheck.verified)
            withTimeout(10_000) {f.grants.batchPlans.whenFilesIdle()}; assertEquals(before,history(f))
        } finally {f.mode("")}
    }}
}
