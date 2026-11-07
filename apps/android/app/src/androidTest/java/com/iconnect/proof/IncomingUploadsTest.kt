package com.iconnect.proof

import android.content.Context
import android.content.ContextWrapper
import android.net.Uri
import android.os.Bundle
import android.provider.DocumentsContract as Docs
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.google.protobuf.ByteString
import com.iconnect.protocol.*
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.security.MessageDigest
import java.util.concurrent.atomic.AtomicBoolean

@RunWith(AndroidJUnit4::class)
class IncomingUploadsTest {
    internal class Fixture(mask:Int=15,private val windowNow:()->Long={System.currentTimeMillis()},private val windowElapsed:()->Long={android.os.SystemClock.elapsedRealtime()}) {
        val base=InstrumentationRegistry.getInstrumentation().targetContext
        val prefix="incoming-test-"+StorageRules.token()
        val root=File(base.filesDir,prefix).apply {check(mkdir())}
        val context=object:ContextWrapper(base) {
            override fun getFilesDir()=root
            override fun getCacheDir()=File(root,"cache").apply {mkdirs()}
            override fun getApplicationContext():Context=this
            override fun getSharedPreferences(name:String,mode:Int)=base.getSharedPreferences(prefix+name,mode)
        }
        val data=requireNotNull(base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"fixture",null,Bundle()))
        val tree=Uri.parse(data.getString("tree"))
        val policy=AndroidOwnerPolicy(context)
        val software=TestPeer.key()
        val approval=TestPeer.approve(policy,software)
        var grants=AndroidStorageGrants(context,policy)
            private set
        val grant=grants.add(approval,tree,data.getInt("flags"),"Phone receiving")
        val visible=AtomicBoolean(true)
        var incoming=AndroidIncomingUploads(context,grants,windowNow=windowNow,windowElapsed=windowElapsed)
        var service=AndroidFileService(context,grants,available={visible.get()},incoming=incoming)
        var target=""; var epoch=""
        init {grants.update(grant.id,mask)}
        suspend fun raw(value:FileRequest,peer:String=approval.id):FileResponse=service.execute(peer,value.toBuilder().setRequestId(StorageRules.token()).build())
        suspend fun call(value:FileRequest):FileResponse {val response=raw(value); assertEquals(response.message,FileStatus.FILE_STATUS_OK,response.status); PhoneTransferManifests.validateResponse(response); return response}
        suspend fun initialize() {val reply=call(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_CONTEXT).build()); epoch=reply.mutationEpoch
            target=(reply.entriesList.firstOrNull()?:call(FileRequest.newBuilder().setOperation(FileOperation.LIST_ROOTS).build()).entriesList.single()).id}
        fun descriptor(bytes:ByteArray,name:String="from-pc.bin")=UploadInfo.newBuilder().setId(MutationRules.newId()).setName(name).setSize(bytes.size.toLong())
            .setSha256(ByteString.copyFrom(Rules.digest(bytes))).setCollision(UploadCollision.UPLOAD_KEEP_BOTH).build()
        suspend fun begin(info:UploadInfo)=call(FileRequest.newBuilder().setOperation(FileOperation.PREPARE_UPLOAD).setDestinationId(target).setTransferId(info.id).setMutationEpoch(epoch).setUpload(info).build()).upload
        suspend fun write(info:UploadInfo,bytes:ByteArray,start:Long=0):UploadInfo {
            var offset=start; var row=info
            while(offset<bytes.size) {val length=minOf(StorageRules.RANGE.toLong(),bytes.size-offset).toInt(); row=call(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_WRITE)
                .setTransferId(info.id).setMutationEpoch(epoch).setOffset(offset).setContent(ByteString.copyFrom(bytes,offset.toInt(),length)).build()).upload; offset+=length; assertEquals(offset,row.offset)}
            return row
        }
        suspend fun finish(info:UploadInfo):UploadInfo {
            call(FileRequest.newBuilder().setOperation(FileOperation.FINISH_UPLOAD).setTransferId(info.id).setMutationEpoch(epoch).build())
            withTimeout(12_000) {while(incoming.state.value.single {it.id==info.id}.active) delay(20)}
            return call(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_STATUS).setTransferId(info.id).setMutationEpoch(epoch).build()).upload
        }
        fun mode(value:String) {base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"creationMode",data.getString("root"),Bundle().apply {putString("mode",value)})}
        fun children(parent:String?=null):Map<String,String> {
            val result=linkedMapOf<String,String>()
            context.contentResolver.query(Docs.buildChildDocumentsUriUsingTree(tree,parent?:Docs.getTreeDocumentId(tree)),arrayOf(Docs.Document.COLUMN_DOCUMENT_ID,Docs.Document.COLUMN_DISPLAY_NAME),null,null,null)!!.use {while(it.moveToNext()) result[it.getString(0)]=it.getString(1)}
            return result
        }
        fun read(name:String,parent:String?=null):ByteArray {val id=children(parent).entries.single {it.value==name}.key; return context.contentResolver.openInputStream(Docs.buildDocumentUriUsingTree(tree,id))!!.use {it.readBytes()}}
        suspend fun restart(selectTarget:Boolean=true,reopenGrants:Boolean=false) {service.close(); incoming.shutdown(); if(reopenGrants) {grants.batchPlans.shutdown(); grants=AndroidStorageGrants(context,policy)} else grants.batchPlans.whenFilesIdle(); incoming=AndroidIncomingUploads(context,grants,windowNow=windowNow,windowElapsed=windowElapsed); service=AndroidFileService(context,grants,available={visible.get()},incoming=incoming); if(selectTarget) initialize()}
        suspend fun close() {service.close(); incoming.shutdown(); grants.batchPlans.shutdown(); AndroidSafAccess(context).reconcile("incoming:",emptyMap()); AndroidSafAccess(context).reconcile("batch-incoming:",emptyMap()); grants.all().forEach {grants.remove(it.id)}; base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"dispose",data.getString("root"),null)
            listOf("iconnect.proof","iconnect.storage","iconnect.saf-access").forEach {base.deleteSharedPreferences(prefix+it)}; root.deleteRecursively()}
    }
    @Test fun completedPublicationRequiresFreshChallengeAndRefusesChangedProviderBytes()=runBlocking {
        val f=Fixture()
        try {
            f.initialize()
            val bytes="generated proof bytes".toByteArray()
            val row=f.begin(f.descriptor(bytes))
            f.write(row,bytes)
            val receipt=f.finish(row)
            assertEquals(UploadPhase.UPLOAD_COMPLETED,receipt.phase)
            fun request(challenge:String)=FileRequest.newBuilder().setOperation(FileOperation.VERIFY_PUBLISHED_UPLOAD)
                .setTransferId(receipt.id).setMutationEpoch(f.epoch).setMutationId(challenge).build()
            val first=request(StorageRules.token())
            val pending=f.call(first)
            assertTrue(pending.hasPublishedUploadCheck())
            assertFalse(pending.publishedUploadCheck.verified)
            assertEquals(first.mutationId,pending.publishedUploadCheck.challenge)
            withTimeout(12_000) {f.incoming.whenIdle()}
            val confirmed=f.call(first)
            assertTrue(confirmed.publishedUploadCheck.verified)
            assertEquals(receipt,confirmed.upload)

            f.restart()
            val id=f.children().entries.single {it.value==receipt.finalName}.key
            val uri=Docs.buildDocumentUriUsingTree(f.tree,id)
            val changed=bytes.copyOf().also {it[0]=0}
            requireNotNull(f.context.contentResolver.openOutputStream(uri,"wt")).use {it.write(changed)}
            val fresh=request(StorageRules.token())
            assertFalse(f.call(fresh).publishedUploadCheck.verified)
            withTimeout(12_000) {f.incoming.whenIdle()}
            assertEquals(FileStatus.CONFLICT,f.raw(fresh).status)
            assertArrayEquals(changed,f.read(receipt.finalName))
            assertEquals(UploadPhase.UPLOAD_COMPLETED,f.incoming.state.value.single().phase)
        } finally {f.close()}
    }

    @Test fun publishedCheckCannotReuseCompletedProofAfterScopeWithdrawal()=runBlocking {
        val f=Fixture()
        try {
            f.initialize()
            val row=f.begin(f.descriptor(byteArrayOf()))
            val receipt=f.finish(row)
            assertEquals(UploadPhase.UPLOAD_COMPLETED,receipt.phase)
            val request=FileRequest.newBuilder().setOperation(FileOperation.VERIFY_PUBLISHED_UPLOAD)
                .setTransferId(row.id).setMutationEpoch(f.epoch).setMutationId(StorageRules.token()).build()
            assertFalse(f.call(request).publishedUploadCheck.verified)
            withTimeout(12_000) {f.incoming.whenIdle()}
            f.service.close()
            f.service=AndroidFileService(f.context,f.grants,available={f.visible.get()},incoming=f.incoming)
            assertFalse(f.call(request).publishedUploadCheck.verified)
            withTimeout(12_000) {f.incoming.whenIdle()}
            f.visible.set(false)
            assertEquals(FileStatus.ACCESS_DENIED,f.raw(request).status)
        } finally {f.close()}
    }

    @Test fun actualProviderReceivesVerifiedBytesAfterPrivateCheckpointRestartAndRepeatedRange()=runBlocking {
        val f=Fixture(); try {
            f.initialize(); val bytes=ByteArray(98_317) {(it*31%251).toByte()}; val row=f.begin(f.descriptor(bytes))
            val request=FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_WRITE).setTransferId(row.id).setMutationEpoch(f.epoch).setContent(ByteString.copyFrom(bytes,0,StorageRules.RANGE)).build()
            assertEquals(StorageRules.RANGE.toLong(),f.call(request).upload.offset); assertEquals(StorageRules.RANGE.toLong(),f.call(request).upload.offset)
            val changed=bytes.copyOfRange(0,StorageRules.RANGE).also {it[0]=1}
            assertEquals(FileStatus.CONFLICT,f.raw(request.toBuilder().setContent(ByteString.copyFrom(changed)).build()).status)
            f.restart(); f.write(row,bytes,StorageRules.RANGE.toLong()); val receipt=f.finish(row)
            assertEquals(receipt.message,UploadPhase.UPLOAD_COMPLETED,receipt.phase); assertArrayEquals(bytes,f.read(receipt.finalName)); assertEquals(receipt,f.finish(receipt))
            assertTrue(File(f.root,"incoming-uploads").listFiles().orEmpty().none {it.name.endsWith(".part")}); assertArrayEquals("iConnect approved phone document.\nSalama!\n".toByteArray(),f.read("simu.txt"))
        } finally {f.close()}
    }
    @Test fun receiveOnlyInboxDoesNotExposeExistingFilesAndReadOnlyDoesNotAcceptBytes()=runBlocking {
        val f=Fixture(StorageRules.READ_ONLY); try {
            f.initialize(); val bytes="private incoming".toByteArray(); val descriptor=f.descriptor(bytes,"simu.txt")
            assertEquals(FileStatus.ACCESS_DENIED,f.raw(FileRequest.newBuilder().setOperation(FileOperation.PREPARE_UPLOAD).setDestinationId(f.target).setTransferId(descriptor.id).setMutationEpoch(f.epoch).setUpload(descriptor).build()).status)
            assertTrue(File(f.root,"incoming-uploads").listFiles().orEmpty().none {it.name.endsWith(".part")})
            f.grants.update(f.grant.id,12); f.initialize(); assertTrue(f.call(FileRequest.newBuilder().setOperation(FileOperation.LIST_ROOTS).build()).entriesList.isEmpty())
            val before=f.call(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_CONTEXT).build()).entriesList.single(); assertEquals(0,before.modifiedAt)
            for(operation in listOf(FileOperation.LIST_DIRECTORY,FileOperation.GET_METADATA,FileOperation.READ_RANGE,FileOperation.PREPARE_DOWNLOAD))
                assertEquals(FileStatus.ACCESS_DENIED,f.raw(FileRequest.newBuilder().setOperation(operation).setNodeId(f.target).setLength(32).build()).status)
            val row=f.begin(descriptor); f.write(row,bytes); val receipt=f.finish(row); assertEquals(receipt.message,UploadPhase.UPLOAD_COMPLETED,receipt.phase)
            assertNotEquals("simu.txt",receipt.finalName); assertArrayEquals(bytes,f.read(receipt.finalName)); assertEquals("iConnect approved phone document.\nSalama!\n",String(f.read("simu.txt")))
            val after=f.call(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_CONTEXT).build()).entriesList.single(); assertEquals(before.version,after.version); assertEquals(0,after.modifiedAt)
        } finally {f.close()}
    }
    @Test fun existingProviderIdAndLostCreationReceiptNeverCauseOverwriteOrAutomaticAnotherCopy()=runBlocking {
        val f=Fixture(); try {
            f.initialize(); val original=f.read("simu.txt"); val data="incoming fixture".toByteArray()
            f.mode("existing"); var row=f.begin(f.descriptor(data)); f.write(row,data); var receipt=f.finish(row)
            assertEquals(UploadPhase.UPLOAD_REVIEW,receipt.phase); assertArrayEquals(original,f.read("simu.txt")); val count=f.children().size
            assertEquals(UploadPhase.UPLOAD_REVIEW,f.finish(receipt).phase); assertEquals(count,f.children().size)
            f.mode("loseReceipt"); row=f.begin(f.descriptor(data,"lost.bin")); f.write(row,data); receipt=f.finish(row); assertEquals(UploadPhase.UPLOAD_REVIEW,receipt.phase)
            val lostCount=f.children().size; assertEquals(UploadPhase.UPLOAD_REVIEW,f.finish(receipt).phase); assertEquals(lostCount,f.children().size)
            assertArrayEquals(original,f.read("simu.txt"))
        } finally {f.close()}
    }
    @Test fun leavingForegroundCancelsProviderOpenAndReconciliationNeverRewritesThePartial()=runBlocking {
        val f=Fixture(); try {
            f.initialize(); val bytes=ByteArray(65_537) {19}; val row=f.begin(f.descriptor(bytes)); f.write(row,bytes); f.mode("blockWrite")
            f.call(FileRequest.newBuilder().setOperation(FileOperation.FINISH_UPLOAD).setTransferId(row.id).setMutationEpoch(f.epoch).build())
            withTimeout(5000) {while(f.incoming.state.value.single().finalName.isEmpty()) delay(20)}
            val finalization = f.incoming.readFinalizations().getValue(row.id)
            assertEquals(PhoneIncomingFinalizationStage.PREPARING_PROVIDER_DOCUMENT, finalization.stage)
            assertNull(finalization.estimate)
            f.visible.set(false); f.service.suspendAvailability()
            withTimeout(5000) {while(f.incoming.state.value.single().active) delay(20)}
            assertTrue(f.incoming.readFinalizations().isEmpty())
            assertNotEquals(UploadPhase.UPLOAD_COMPLETED,f.incoming.state.value.single().phase)
            assertEquals(FileStatus.ACCESS_DENIED,f.raw(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_STATUS).setTransferId(row.id).setMutationEpoch(f.epoch).build()).status)
            f.visible.set(true); f.mode(""); val count=f.children().size
            assertEquals(UploadPhase.UPLOAD_REVIEW,f.finish(row).phase); assertEquals(count,f.children().size)
        } finally {f.close()}
    }
    @Test fun damagedCheckpointAndRevocationCannotPublishAndLocalPrivateCleanupKeepsProviderFiles()=runBlocking {
        val f=Fixture(); try {
            f.initialize(); val bytes=ByteArray(40_000) {7}; val row=f.begin(f.descriptor(bytes));
            f.call(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_WRITE).setTransferId(row.id).setMutationEpoch(f.epoch).setContent(ByteString.copyFrom(bytes,0,StorageRules.RANGE)).build())
            val part=File(f.root,"incoming-uploads").listFiles()!!.single {it.name.endsWith(".part")}; java.io.RandomAccessFile(part,"rw").use {it.write(8)}; f.restart()
            assertEquals(FileStatus.CONFLICT,f.raw(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_WRITE).setTransferId(row.id).setMutationEpoch(f.epoch).setOffset(StorageRules.RANGE.toLong()).setContent(ByteString.copyFrom(bytes,StorageRules.RANGE,bytes.size-StorageRules.RANGE)).build()).status)
            f.grants.revokePeer(f.approval.id)
            assertEquals(FileStatus.ACCESS_DENIED,f.raw(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_STATUS).setTransferId(row.id).setMutationEpoch(f.epoch).build()).status)
            val before=f.children(); f.incoming.discardPrivate(row.id); assertFalse(part.exists()); assertEquals(before,f.children()); f.restart(false)
            assertEquals(UploadPhase.UPLOAD_CANCELED,f.incoming.state.value.single().phase)
        } finally {f.close()}
    }
    @Test fun failedPrivateCommitCannotAdvanceTheDurableOffsetAndAColdResumeDiscardsUnacknowledgedBytes()=runBlocking {
        val f=Fixture(); try {
            f.initialize(); val bytes=ByteArray(40_000) {5}; val row=f.begin(f.descriptor(bytes)); val blocker=File(f.root,"incoming-uploads/journal.json.new").apply {check(mkdir())}
            assertNotEquals(FileStatus.FILE_STATUS_OK,f.raw(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_WRITE).setTransferId(row.id).setMutationEpoch(f.epoch).setContent(ByteString.copyFrom(bytes,0,StorageRules.RANGE)).build()).status)
            assertEquals(StorageRules.RANGE.toLong(),File(f.root,"incoming-uploads").listFiles()!!.single {it.name.endsWith(".part")}.length())
            check(blocker.delete()); f.restart(); assertEquals(0,f.call(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_STATUS).setTransferId(row.id).setMutationEpoch(f.epoch).build()).upload.offset)
            f.write(row,bytes); assertEquals(UploadPhase.UPLOAD_COMPLETED,f.finish(row).phase)
        } finally {f.close()}
    }
    @Test fun ownerHistoryArchiveInvalidatesOldEpochPreservesCopiesAndSeparatesOrphanCleanup()=runBlocking {
        val f=Fixture(); try {
            f.initialize(); val bytes=ByteArray(40_000) {3}; val row=f.begin(f.descriptor(bytes))
            f.write(row,bytes); val previousEpoch=f.epoch; val documents=f.children()
            val part=File(f.root,"incoming-uploads").listFiles()!!.single {it.name.endsWith(".part")}
            f.incoming.archiveHistory(); assertTrue(f.incoming.state.value.isEmpty()); assertTrue(part.exists())
            assertEquals(1,f.incoming.preservedRecords()); assertEquals(1,f.incoming.orphanCount()); assertEquals(documents,f.children())
            assertEquals(FileStatus.STALE_REFERENCE,f.raw(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_STATUS).setTransferId(row.id).setMutationEpoch(previousEpoch).build()).status)
            f.restart(); assertNotEquals(previousEpoch,f.epoch); assertTrue(f.incoming.recoveryIssues().isEmpty()); assertEquals(1,f.incoming.orphanCount())
            assertEquals(FileStatus.NOT_FOUND,f.raw(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_STATUS).setTransferId(row.id).setMutationEpoch(f.epoch).build()).status)
            val sentinel=File(f.root,"owner-sentinel").apply {writeText("keep")}; val link=File(part.parentFile,StorageRules.token()+".part")
            android.system.Os.symlink(sentinel.path,link.path)
            assertTrue(runCatching {IncomingPartRecoveryTest.removeReviewed(f.incoming)}.isFailure); assertEquals("keep",sentinel.readText()); assertTrue(part.exists())
            check(link.delete()); IncomingPartRecoveryTest.removeReviewed(f.incoming); assertEquals(0,f.incoming.orphanCount()); assertFalse(part.exists()); assertEquals(documents,f.children())
            f.incoming.clearArchives(); assertEquals(0,f.incoming.preservedRecords()); assertEquals("keep",sentinel.readText()); assertEquals(documents,f.children())
            assertFalse(File(f.root,"saf-access.json").readText().contains("incoming:"))
        } finally {f.close()}
    }
    @Test fun emptySkipFailAndChecksumMismatchKeepExistingDocumentsAndReleaseSkippedPrivateBytes()=runBlocking {
        val f=Fixture(); try {
            f.initialize(); val original=f.read("simu.txt"); val empty=f.begin(f.descriptor(byteArrayOf(),"empty.bin"))
            val complete=f.finish(empty); assertEquals(UploadPhase.UPLOAD_COMPLETED,complete.phase); assertArrayEquals(byteArrayOf(),f.read(complete.finalName))
            val bytes="kept source".toByteArray()
            val skip=f.begin(f.descriptor(bytes,"simu.txt").toBuilder().setCollision(UploadCollision.UPLOAD_SKIP).build()); f.write(skip,bytes)
            assertEquals(UploadPhase.UPLOAD_SKIPPED,f.finish(skip).phase)
            assertTrue(File(f.root,"incoming-uploads").listFiles().orEmpty().none {it.name.endsWith(".part")})
            val fail=f.begin(f.descriptor(bytes,"simu.txt").toBuilder().setCollision(UploadCollision.UPLOAD_FAIL).build()); f.write(fail,bytes)
            assertEquals(UploadPhase.UPLOAD_REVIEW,f.finish(fail).phase); assertArrayEquals(original,f.read("simu.txt"))
            val mismatch=f.begin(f.descriptor(bytes,"checksum.bin")); f.write(mismatch,ByteArray(bytes.size) {1})
            assertEquals(UploadPhase.UPLOAD_REVIEW,f.finish(mismatch).phase); assertFalse(f.children().values.contains("checksum.bin"))
            val before=f.children(); f.incoming.discardPrivate(fail.id); f.incoming.discardPrivate(mismatch.id); assertEquals(before,f.children())
        } finally {f.close()}
    }
    @Test fun nativeAuthenticatedPeersNegotiateIncomingCopiesAndVerifyActualProviderBytes()=runBlocking {
        val f=Fixture(12); val instrumentation=InstrumentationRegistry.getInstrumentation(); val scope=CoroutineScope(SupervisorJob()+Dispatchers.Main.immediate)
        var phone:RtcTestSession?=null; var sender:RtcTestSession?=null
        val phoneReady=CompletableDeferred<Unit>(); val senderReady=CompletableDeferred<Unit>()
        val identity=object:SessionIdentity {
            override val publicKey:ByteArray get()=f.software.public.encoded
            override val id=Rules.id(publicKey)
            override fun sign(transcript:ByteArray)=java.security.Signature.getInstance("SHA256withECDSA").run {initSign(f.software.private); update(transcript); sign()}
        }
        try {
            val sessionId=StorageRules.token(); val local=LocalIdentity()
            instrumentation.runOnMainSync {
                phone=RtcTestSession(f.context,scope,local,sessionId,identity.publicKey,
                    {kind,body,mid->when(kind){"offer"->sender!!.acceptOffer(body);"answer"->sender!!.acceptAnswer(body);"candidate"->sender!!.addCandidate(body,mid)}},
                    {},{if(it) phoneReady.complete(Unit)},fileService=f.service,available={f.visible.get()})
                sender=RtcTestSession(f.context,scope,identity,sessionId,local.publicKey,
                    {kind,body,mid->when(kind){"offer"->phone.acceptOffer(body);"answer"->phone.acceptAnswer(body);"candidate"->phone.addCandidate(body,mid)}},
                    {},{if(it) senderReady.complete(Unit)},initiator=true)
                sender.offer()
            }
            withTimeout(20_000) {phoneReady.await(); senderReady.await()}
            suspend fun request(value:FileRequest):FileResponse=withContext(Dispatchers.Main.immediate) {PhoneTransferManifests.checked(sender!!::requestFiles,value)}
            val context=request(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_CONTEXT).build()); val root=context.entriesList.single()
            val folderContext=request(FileRequest.newBuilder().setOperation(FileOperation.MUTATION_CONTEXT).build()); val folderId=MutationRules.newId()
            val created=request(FileRequest.newBuilder().setOperation(FileOperation.CREATE_DIRECTORY).setDestinationId(root.id).setName("native-folder").setMutationId(folderId).setMutationEpoch(folderContext.mutationEpoch).build())
            val folderReceipt=CreatedDirectoryReceipt(folderId,folderContext.mutationEpoch,root.grantId,"native-folder")
            CreatedDirectoryReferences.check(folderReceipt,created)
            val destination=CreatedDirectoryReferences.resolve(folderReceipt,::request)
            val providerFolder=f.children().entries.single {it.value==destination.name}.key
            assertEquals(12,destination.permissions); val bytes=ByteArray(65_551) {(it*17%251).toByte()}; val descriptor=f.descriptor(bytes,"native-pc.bin")
            request(FileRequest.newBuilder().setOperation(FileOperation.PREPARE_UPLOAD).setDestinationId(destination.id).setTransferId(descriptor.id).setMutationEpoch(context.mutationEpoch).setUpload(descriptor).build())
            var offset=0
            while(offset<bytes.size) {val count=minOf(StorageRules.RANGE,bytes.size-offset); val reply=request(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_WRITE).setTransferId(descriptor.id).setMutationEpoch(context.mutationEpoch).setOffset(offset.toLong()).setContent(ByteString.copyFrom(bytes,offset,count)).build()); offset+=count; assertEquals(offset.toLong(),reply.upload.offset)}
            request(FileRequest.newBuilder().setOperation(FileOperation.FINISH_UPLOAD).setTransferId(descriptor.id).setMutationEpoch(context.mutationEpoch).build())
            withTimeout(12_000) {while(f.incoming.state.value.single().active) delay(20)}
            val receipt=request(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_STATUS).setTransferId(descriptor.id).setMutationEpoch(context.mutationEpoch).build()).upload
            assertEquals(receipt.message,UploadPhase.UPLOAD_COMPLETED,receipt.phase); assertArrayEquals(bytes,f.read(receipt.finalName,providerFolder))
            assertTrue(request(FileRequest.newBuilder().setOperation(FileOperation.LIST_ROOTS).build()).entriesList.isEmpty())
            val unsupported=withContext(Dispatchers.Main.immediate) {runCatching {phone!!.requestFiles(FileRequest.newBuilder().setOperation(FileOperation.RESOLVE_CREATED_DIRECTORY).build())}.exceptionOrNull()}
            assertTrue(unsupported is IllegalArgumentException)
             assertTrue(sender!!.acceptsFolderBatches)
             assertFalse(phone!!.acceptsFolderBatches)
            for(operation in listOf(FileOperation.UPLOAD_BATCH_CONTEXT,FileOperation.PREPARE_BATCH_FILE,FileOperation.WRITE_BATCH_FILE,FileOperation.FINISH_BATCH_FILE,FileOperation.CANCEL_BATCH_FILE)) {
                 val batchUnsupported=withContext(Dispatchers.Main.immediate) {runCatching {phone.requestFiles(FileRequest.newBuilder().setOperation(operation).build())}.exceptionOrNull()}
                assertTrue(batchUnsupported is IllegalArgumentException)
            }
        } finally {instrumentation.runOnMainSync {phone?.close(); sender?.close()}; scope.cancel(); f.close()}
    }
    @Test fun createdFolderReceiptRecoversAfterRestartAndReceivesVerifiedNestedBytes()=runBlocking {
        val f=Fixture(); try {
            f.initialize(); val context=f.call(FileRequest.newBuilder().setOperation(FileOperation.MUTATION_CONTEXT).build())
            val id=MutationRules.newId(); val request=FileRequest.newBuilder().setOperation(FileOperation.CREATE_DIRECTORY).setDestinationId(f.target).setName("copied-tree")
                .setMutationId(id).setMutationEpoch(context.mutationEpoch).build()
            val receipt=CreatedDirectoryReceipt(id,context.mutationEpoch,f.grant.id,"copied-tree")
            val original=CreatedDirectoryReferences.check(receipt,f.call(request)); f.restart()
            val restored=CreatedDirectoryReferences.resolve(receipt) {f.call(it)}; assertNotEquals(original.id,restored.id)
            val providerFolder=f.children().entries.single {it.value==restored.name}.key
            f.target=restored.id; val bytes="actual nested provider bytes".toByteArray(); val row=f.begin(f.descriptor(bytes,"nested.txt")); f.write(row,bytes)
            val result=f.finish(row); assertEquals(UploadPhase.UPLOAD_COMPLETED,result.phase); assertArrayEquals(bytes,f.read(result.finalName,providerFolder))
            val nested=f.call(FileRequest.newBuilder().setOperation(FileOperation.CREATE_DIRECTORY).setDestinationId(restored.id).setName("empty")
                .setMutationId(MutationRules.newId()).setMutationEpoch(context.mutationEpoch).build()).entriesList.single()
            assertEquals("empty",nested.name); assertEquals(2,f.children(providerFolder).size)
            assertEquals(1,f.children().values.count {it=="copied-tree"})
        } finally {f.close()}
    }
    @Test fun receiveOnlyCreatedFolderDoesNotDiscloseExistingNamesOrDirectoryActivity()=runBlocking {
        val f=Fixture(12); try {
            f.initialize(); val context=f.call(FileRequest.newBuilder().setOperation(FileOperation.MUTATION_CONTEXT).build()); val id=MutationRules.newId()
            val receipt=CreatedDirectoryReceipt(id,context.mutationEpoch,f.grant.id,"Reports")
            val reply=f.call(FileRequest.newBuilder().setOperation(FileOperation.CREATE_DIRECTORY).setDestinationId(f.target).setName("Reports").setMutationId(id).setMutationEpoch(context.mutationEpoch).build())
            val entry=CreatedDirectoryReferences.check(receipt,reply); assertNotEquals("Reports",entry.name); assertEquals(0,entry.modifiedAt)
            assertTrue(f.call(FileRequest.newBuilder().setOperation(FileOperation.LIST_ROOTS).build()).entriesList.isEmpty())
            for(operation in listOf(FileOperation.LIST_DIRECTORY,FileOperation.GET_METADATA)) assertEquals(FileStatus.ACCESS_DENIED,f.raw(FileRequest.newBuilder().setOperation(operation).setNodeId(entry.id).build()).status)
            val folder=f.children().entries.single {it.value==entry.name}.key
            val uri=requireNotNull(Docs.createDocument(f.context.contentResolver,Docs.buildDocumentUriUsingTree(f.tree,folder),"text/plain","owner-added.txt"))
            f.context.contentResolver.openOutputStream(uri,"w")!!.use {it.write("private activity".toByteArray())}; f.restart()
            val restored=CreatedDirectoryReferences.resolve(receipt) {f.call(it)}; assertEquals(0,restored.modifiedAt); assertEquals(entry.version,restored.version)
            assertTrue(f.children().values.contains("Reports"))
            f.grants.update(f.grant.id,StorageRules.READ_ONLY)
            assertEquals(FileStatus.ACCESS_DENIED,f.raw(FileRequest.newBuilder().setOperation(FileOperation.RESOLVE_CREATED_DIRECTORY).setMutationId(id).setMutationEpoch(context.mutationEpoch).build()).status)
        } finally {f.close()}
    }
    @Test fun folderReceiptDepthLimitRefusesBeforeProviderCreation()=runBlocking {
        val f=Fixture(); try {
            f.initialize(); var providerId=Docs.getTreeDocumentId(f.tree); var reference=f.target
            repeat(63) {index->
                val name="level-$index"
                providerId=Docs.getDocumentId(requireNotNull(Docs.createDocument(f.context.contentResolver,Docs.buildDocumentUriUsingTree(f.tree,providerId),Docs.Document.MIME_TYPE_DIR,name)))
                reference=f.call(FileRequest.newBuilder().setOperation(FileOperation.LIST_DIRECTORY).setNodeId(reference).build()).entriesList.single {it.name==name}.id
            }
            val context=f.call(FileRequest.newBuilder().setOperation(FileOperation.MUTATION_CONTEXT).build())
            val id=MutationRules.newId()
            val response=f.raw(FileRequest.newBuilder().setOperation(FileOperation.CREATE_DIRECTORY).setDestinationId(reference).setName("too-deep")
                .setMutationId(id).setMutationEpoch(context.mutationEpoch).build())
            assertEquals(FileStatus.UNSUPPORTED,response.status); assertTrue(f.children(providerId).isEmpty())
            assertEquals(FileStatus.NOT_FOUND,f.raw(FileRequest.newBuilder().setOperation(FileOperation.RESOLVE_CREATED_DIRECTORY).setMutationId(id).setMutationEpoch(context.mutationEpoch).build()).status)
        } finally {f.close()}
    }
    @Test fun legacyUnknownMovedAndForeignCreatedFolderReceiptsCannotAdoptByName()=runBlocking {
        val f=Fixture(); try {
            f.initialize(); val context=f.call(FileRequest.newBuilder().setOperation(FileOperation.MUTATION_CONTEXT).build()); val id=MutationRules.newId()
            val create=FileRequest.newBuilder().setOperation(FileOperation.CREATE_DIRECTORY).setDestinationId(f.target).setName("kept-folder").setMutationId(id).setMutationEpoch(context.mutationEpoch).build()
            f.call(create); val resolve=FileRequest.newBuilder().setOperation(FileOperation.RESOLVE_CREATED_DIRECTORY).setMutationId(id).setMutationEpoch(context.mutationEpoch).build()
            assertEquals(FileStatus.NOT_FOUND,f.raw(resolve,"f".repeat(64)).status)
            val journal=File(f.root,"file-operations/journal.json"); val document=org.json.JSONObject(journal.readText()); val record=document.getJSONArray("records").getJSONObject(0)
            record.remove("created"); record.remove("ancestors"); record.remove("requested"); journal.writeText(document.toString())
            assertEquals(FileStatus.CONFLICT,f.raw(resolve).status); assertEquals(1,f.children().values.count {it=="kept-folder"})
            f.mode("loseFolderReceipt"); val lost=MutationRules.newId()
            val unknown=f.raw(create.toBuilder().setName("uncertain-folder").setMutationId(lost).build()); assertEquals(MutationPhase.CHANGE_REVIEW,unknown.mutation.phase)
            assertEquals(FileStatus.CONFLICT,f.raw(resolve.toBuilder().setMutationId(lost).build()).status); val count=f.children().size
            f.raw(create.toBuilder().setName("uncertain-folder").setMutationId(lost).build()); assertEquals(count,f.children().size)
            f.mode(""); val freshId=MutationRules.newId(); val made=f.call(create.toBuilder().setName("movable").setMutationId(freshId).build()).entriesList.single()
            val providerId=f.children().entries.single {it.value==made.name}.key
            f.base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"change",providerId,Bundle().apply {putString("parent","outside")})
            assertNotEquals(FileStatus.FILE_STATUS_OK,f.raw(resolve.toBuilder().setMutationId(freshId).build()).status)
        } finally {f.close()}
    }
}
