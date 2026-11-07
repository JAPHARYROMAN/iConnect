package com.iconnect.proof

import android.net.Uri
import android.os.Bundle
import android.provider.DocumentsContract as Docs
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.iconnect.protocol.*
import kotlinx.coroutines.*
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.io.RandomAccessFile
import java.security.Signature

@RunWith(AndroidJUnit4::class)
class BatchDownloadSourceRecoveryTest {
    internal class Fixture {
        val data=DownloadSourceRecoveryTest.Fixture()
        val context get()=data.context
        val files get()=data.manager
        val source get()=data.source
        val epoch=StorageRules.token()
        val directory=File(data.directory,"batch-records")
        var disallowStart=false
        var nested=false
        var failPreparation=false
        val rootId=StorageRules.token(); val folderId=StorageRules.token()
        var batches=batchManager()
        var id=""
        var index=0
        fun batchManager()=AndroidBatchDownloads(context,files,directory,dispatch={worker,action->check(!disallowStart) {"Generated stop before private allocation"}; worker.ensureActive(); action()})
        val parent get()=batches.state.value.single {it.id==id}
        val child get()=files.state.value.single {it.id==parent.items[index].child}
        val part get()=File(data.directory,"${child.id}.part")
        val journal get()=File(directory,"$id.json")
        fun root()=FileEntry.newBuilder().setId(rootId).setGrantId(source.selected.grantId).setName("Generated root").setIsRoot(true).setIsDirectory(true)
            .setVersion("b".repeat(64)).setPermissions(StorageRules.READ_ONLY).build()
        fun folder()=root().toBuilder().setId(folderId).setParentId(rootId).setIsRoot(false).setName("Generated folder").build()
        fun plan():PhoneTransferManifest {
            val file=source.selected
            val item=PhoneTransferItem(if(nested) 0 else -1,file.grantId,if(nested) listOf(folder().name,file.name) else listOf(file.name),file.name,false,file.size,file.version)
            return PhoneTransferManifest(if(nested) listOf(PhoneTransferItem(-1,file.grantId,listOf(folder().name),folder().name,true,0,folder().version),item) else listOf(item))
        }
        suspend fun request(value:FileRequest):FileResponse {
            val response=when(value.operation) {
            FileOperation.LIST_ROOTS->FileResponse.newBuilder().addEntries(root()).build()
            FileOperation.LIST_DIRECTORY->FileResponse.newBuilder().addEntries(if(nested && value.nodeId==rootId) folder() else source.selected).build()
            FileOperation.GET_METADATA->when(value.nodeId) {rootId->FileResponse.newBuilder().addEntries(root()).build(); folderId->FileResponse.newBuilder().addEntries(folder()).build(); else->source.request(value)}
            FileOperation.PREPARE_DOWNLOAD->if(failPreparation) error("Generated unavailable source") else source.request(value)
            else->source.request(value)
            }
            return response.toBuilder().setRequestId(value.requestId).build()
        }
        suspend fun pause(nested:Boolean=false,beforePreparation:Boolean=false,beforeAllocation:Boolean=false) {
            this.nested=nested; index=if(nested) 1 else 0
            source.selected=source.selected.toBuilder().setParentId(if(nested) folderId else rootId).build()
            failPreparation=beforePreparation; disallowStart=beforeAllocation
            id=batches.enqueue(data.peer,epoch,plan(),data.tree,PhoneCollision.KEEP_BOTH)
            if(!beforePreparation && !beforeAllocation) source.stopAt=1048576
            val running=batches.resume(id,data.peer,epoch,::request)
            if(!beforePreparation && !beforeAllocation) {
                withTimeout(15000) {while(!source.stopped.isCompleted && !running.isCompleted) delay(10)}
                check(source.stopped.isCompleted) {"Generated batch stopped before checkpoint: ${parent.phase}: ${parent.error}; ${files.state.value.map {it.error}}"}
                batches.pause(id)
            }
            batches.awaitIdle(id); source.stopAt=null; failPreparation=false; disallowStart=false
            assertEquals(if(beforePreparation || beforeAllocation) 0L else 1048576L,child.offset)
        }
        suspend fun review()=batches.prepareSourceReview(id,index,data.peer,epoch,source.selected,::request) {data.allowed}
        fun changeMetadata() {source.selected=source.selected.toBuilder().setId(StorageRules.token()).setVersion(if(source.selected.version=="c".repeat(64)) "d".repeat(64) else "c".repeat(64)).build()}
        suspend fun restart() {batches.shutdown(); files.shutdown(); data.manager=data.manager(); batches=batchManager()}
        suspend fun finish() {
            batches.resume(id,data.peer,epoch,::request).join(); assertEquals(parent.error,PhoneBatchPhase.COMPLETED,parent.phase)
            val item=parent.items[index]; assertArrayEquals(source.bytes,context.contentResolver.openInputStream(Uri.parse(item.published))!!.use {it.readBytes()}); assertTrue(files.state.value.isEmpty())
        }
        fun providerChildren(parent:String=Docs.getTreeDocumentId(data.tree)):Map<String,String> =context.contentResolver.query(
            Docs.buildChildDocumentsUriUsingTree(data.tree,parent),arrayOf(Docs.Document.COLUMN_DOCUMENT_ID,Docs.Document.COLUMN_DISPLAY_NAME),null,null,null)!!.use {cursor->
            buildMap {while(cursor.moveToNext()) put(cursor.getString(0),cursor.getString(1))}
        }
        suspend fun dispose() {
            batches.shutdown(); batches=batchManager()
            batches.recoveryIssues().filter {it.canArchive}.forEach {batches.archiveRecovery(it.id)}
            batches.state.value.forEach {batches.cancel(it.id); batches.remove(it.id)}; batches.shutdown(); data.dispose()
        }
    }
    private suspend fun fixture(work:suspend(Fixture)->Unit) {val f=Fixture(); try {work(f)} finally {f.dispose()}}
    private suspend fun refused(work:suspend()->Unit) {try {work(); fail("Expected source refusal")} catch(_:Exception) {}}
    private suspend fun waitFor(condition:()->Boolean)=withTimeout(5000) {while(!condition()) delay(10)}

    @Test fun expiredChangedMetadataKeepsFrozenPlanFolderPartAndPrefixThenFinishesAfterRestart()=runBlocking {fixture {f->
        f.pause(nested=true); f.data.now+=25*60*60*1000L
        f.batches.resume(f.id,f.data.peer,f.epoch,f::request).join(); val before=f.child; val originalSources=f.parent.items.map {it.source}; val folder=f.parent.items[0]
        val part=f.files.sourcePartIdentity(before.id); val prefix=f.part.readBytes(); f.changeMetadata()
        f.batches.confirmSourceReview(f.review()); assertEquals(originalSources,f.parent.items.map {it.source}); assertEquals(folder,f.parent.items[0])
        assertEquals(before.offset,f.child.offset); assertEquals(before.chain,f.child.chain); assertEquals(part,f.files.sourcePartIdentity(before.id)); assertArrayEquals(prefix,f.part.readBytes())
        assertEquals(before.tree,f.child.tree); assertEquals(before.parents,f.child.parents); assertEquals(before.collision,f.child.collision); assertEquals(before.phase,f.child.phase)
        assertNotEquals(before.ticket,f.child.ticket); assertNotEquals(before.version,f.child.version); assertNotNull(f.parent.items[f.index].sourceApproval)
        f.restart(); f.finish()
    }}
    @Test fun approvalCreatesNoProviderCopyAndExplicitBatchResumeKeepsOriginalCollision()=runBlocking {fixture {f->
        f.pause(); val before=f.child; val children=f.providerChildren(); val original=Docs.buildDocumentUriUsingTree(f.data.tree,f.data.bundle.getString("file"))
        val kept=f.context.contentResolver.openInputStream(original)!!.use {it.readBytes()}
        f.batches.confirmSourceReview(f.review()); assertEquals(children,f.providerChildren()); assertEquals(before.offset,f.child.offset); assertFalse(f.parent.active)
        f.finish(); assertEquals(children.size+1,f.providerChildren().size); assertNotEquals(before.name,f.parent.items[0].localName)
        assertArrayEquals(kept,f.context.contentResolver.openInputStream(original)!!.use {it.readBytes()})
    }}
    @Test fun differentFinalByteCannotReplaceCheckpointEvenWithMatchingPrefixNameAndSize()=runBlocking {fixture {f->
        f.pause(); val child=f.child; val journal=f.journal.readText(); f.source.bytes[f.source.bytes.lastIndex]=(f.source.bytes.last().toInt() xor 1).toByte()
        refused {f.review()}; assertEquals(child,f.child); assertEquals(journal,f.journal.readText())
    }}
    @Test fun standalonePreparationAndConfirmationCannotBypassTheOriginalParent()=runBlocking {fixture {f->
        f.pause(); refused {f.files.prepareSourceReview(f.child.id,f.data.peer,f.source.selected,f::request) {true}}
        val review=f.review(); refused {f.files.confirmSourceReview(review.source)}; f.batches.confirmSourceReview(review); assertNotNull(f.parent.items[0].sourceApproval)
    }}
    @Test fun cancelReviewKeepsBothJournalsAndReleasesOnlyTheUnusedTicket()=runBlocking {fixture {f->
        f.pause(); val child=f.child; val journal=f.journal.readText(); val review=f.review(); val fresh=f.source.infos.keys.last()
        refused {f.batches.resume(f.id,f.data.peer,f.epoch,f::request)}; f.batches.cancelSourceReview(review.id)
        waitFor {f.source.released.contains(fresh)}; assertFalse(f.source.released.contains(child.ticket)); assertEquals(child,f.child); assertEquals(journal,f.journal.readText())
        refused {f.batches.confirmSourceReview(review)}
    }}
    @Test fun changedConnectionAndMonotonicReviewExpiryNeverWriteParentApproval()=runBlocking {
        for(expire in listOf(false,true)) fixture {f->f.pause(); val child=f.child; val journal=f.journal.readText(); val review=f.review()
            if(expire) {f.data.elapsed+=300000; f.data.now-=600000} else f.data.allowed=false
            refused {f.batches.confirmSourceReview(review)}; assertEquals(child,f.child); assertEquals(journal,f.journal.readText()); assertNull(f.parent.items[0].sourceApproval)
        }
    }
    @Test fun changedReadyStatusCannotWriteTheParentIntent()=runBlocking {fixture {f->
        f.pause(); val child=f.child; val journal=f.journal.readText(); val review=f.review()
        f.source.transform={it.toBuilder().setExpiresAt(it.expiresAt+1).build()}
        refused {f.batches.confirmSourceReview(review)}; assertEquals(child,f.child); assertEquals(journal,f.journal.readText())
    }}
    @Test fun hidingOrPausingInvalidatesReviewsWithoutGivingBackgroundAuthority()=runBlocking {fixture {f->
        f.pause(); val child=f.child; val review=f.review(); f.batches.pauseAll(); refused {f.batches.confirmSourceReview(review)}
        assertEquals(child,f.child); assertNull(f.parent.items[0].sourceApproval); assertTrue(f.child.batchId.isNotEmpty())
    }}
    @Test fun corruptPrefixStillFailsBeforeAnyProviderPublication()=runBlocking {fixture {f->
        f.pause(); f.batches.confirmSourceReview(f.review()); val children=f.providerChildren()
        RandomAccessFile(f.part,"rw").use {it.write((f.source.bytes[0].toInt() xor 1))}
        f.batches.resume(f.id,f.data.peer,f.epoch,f::request).join(); assertEquals(PhoneDownloadPhase.INTERRUPTED,f.child.phase); assertTrue(f.child.error.contains("partial"))
        assertEquals(children,f.providerChildren()); assertTrue(f.child.published.isEmpty())
    }}
    @Test fun privatePartReplacementRefusesApprovalEvenWhenItsBytesAreIdentical()=runBlocking {fixture {f->
        f.pause(); val child=f.child; val review=f.review(); val part=f.part; val kept=File(part.path+".kept"); check(part.renameTo(kept)); part.writeBytes(kept.readBytes())
        refused {f.batches.confirmSourceReview(review)}; assertEquals(child,f.child); assertNull(f.parent.items[0].sourceApproval); assertTrue(kept.isFile)
    }}
    @Test fun recordedPartReplacementAfterApprovalCannotResumeOrCreateAProviderCopy()=runBlocking {fixture {f->
        f.pause(); f.batches.confirmSourceReview(f.review()); val child=f.child; val part=f.part; val kept=File(part.path+".kept"); val objects=f.providerChildren(); val reads=f.source.reads
        check(part.renameTo(kept)); part.writeBytes(kept.readBytes()); f.batches.resume(f.id,f.data.peer,f.epoch,f::request).join()
        assertEquals(PhoneBatchPhase.INTERRUPTED,f.parent.phase); assertEquals(child,f.child); assertEquals(reads,f.source.reads); assertEquals(objects,f.providerChildren()); assertTrue(kept.isFile)
    }}
    @Test fun alreadyPublishedChildRecoversItsParentOutcomeAfterRestartWithoutAnotherProviderCopy()=runBlocking {fixture {f->
        f.pause(); f.changeMetadata(); f.batches.confirmSourceReview(f.review()); val id=f.child.id
        f.files.resumeBatchFile(id,f.data.peer,f.id,f::request).join(); assertEquals(PhoneDownloadPhase.READY_TO_SAVE,f.child.phase)
        f.files.publishBatchFile(id,false,f.id).join(); assertEquals(PhoneDownloadPhase.COMPLETED,f.child.phase); assertFalse(f.part.exists()); val objects=f.providerChildren()
        f.restart(); f.finish(); assertEquals(objects,f.providerChildren())
    }}
    @Test fun renamedProviderFolderRefusesApprovalAndPreservesIt()=runBlocking {fixture {f->
        f.pause(nested=true); val child=f.child; val review=f.review(); val folder=f.parent.items[0]
        f.context.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"change",folder.document,Bundle().apply {putString("name",folder.localName+" kept")})
        refused {f.batches.confirmSourceReview(review)}; assertEquals(child,f.child); assertNull(f.parent.items[f.index].sourceApproval); assertEquals(folder.localName+" kept",f.providerChildren()[folder.document])
    }}
    @Test fun parentCancellationInvalidatesReviewAndCleansOnlyPrivateReceivingParts()=runBlocking {fixture {f->
        f.pause(); val children=f.providerChildren(); val review=f.review(); f.batches.cancel(f.id,f::request)
        refused {f.batches.confirmSourceReview(review)}; assertEquals(PhoneBatchPhase.CANCELED,f.parent.phase); assertTrue(f.files.state.value.isEmpty()); assertEquals(children,f.providerChildren())
    }}
    @Test fun parentCancellationDrainsInFlightPreparationAndItsNewLease()=runBlocking {fixture {f->
        f.pause(); val entered=CompletableDeferred<Unit>()
        f.source.transform={it.toBuilder().setState(DownloadState.DOWNLOAD_PREPARING).setPreparedBytes(0).clearSha256().build()}
        f.source.before={if(it.operation==FileOperation.DOWNLOAD_STATUS) {entered.complete(Unit); awaitCancellation()}}
        val work=async {f.review()}; entered.await(); f.batches.cancel(f.id,f::request); refused {work.await()}
        assertEquals(PhoneBatchPhase.CANCELED,f.parent.phase); assertTrue(f.source.released.isNotEmpty())
    }}
    @Test fun shutdownDrainsConfirmationAndRetainsTheOriginalChild()=runBlocking {fixture {f->
        f.pause(); val child=f.child; val review=f.review(); val entered=CompletableDeferred<Unit>()
        f.source.before={if(it.operation==FileOperation.DOWNLOAD_STATUS) {entered.complete(Unit); awaitCancellation()}}
        val task=async {f.batches.confirmSourceReview(review)}; entered.await(); f.batches.shutdown(); refused {task.await()}; assertEquals(child,f.child)
    }}
    @Test fun failedParentWritePreventsChildAdoptionAndKeepsOriginalJournal()=runBlocking {fixture {f->
        f.pause(); val child=f.child; val journal=f.journal.readText(); val review=f.review(); val blocker=File(f.journal.path+".new").apply {check(mkdir())}
        try {refused {f.batches.confirmSourceReview(review)}} finally {check(blocker.delete())}
        assertEquals(child,f.child); assertEquals(journal,f.journal.readText())
    }}
    @Test fun failedChildWriteLeavesPreviousApprovedVersionRecoverableWithParentIntentAfterRestart()=runBlocking {fixture {f->
        f.pause(); f.changeMetadata(); f.batches.confirmSourceReview(f.review()); val child=f.child; f.changeMetadata(); val review=f.review()
        val blocker=File(f.data.directory,"${child.id}.json.new").apply {check(mkdir())}
        try {refused {f.batches.confirmSourceReview(review)}} finally {check(blocker.delete())}
        assertEquals(child,f.child); assertEquals(child.version,f.parent.items[0].sourceApproval!!.previousVersion)
        f.restart(); f.batches.confirmSourceReview(f.review()); f.finish()
    }}
    @Test fun missingParentCannotTurnItsCheckpointIntoStandaloneSourceAuthority()=runBlocking {fixture {f->
        f.pause(); val child=f.child; f.batches.shutdown(); f.files.shutdown(); check(f.journal.delete()); f.data.manager=f.data.manager(); f.batches=f.batchManager()
        refused {f.review()}; refused {f.files.prepareSourceReview(child.id,f.data.peer,f.source.selected,f::request) {true}}; assertEquals(child,f.files.state.value.single())
    }}
    @Test fun malformedParentReceiptsArePreservedAndCannotAuthorizeTheRetainedChild()=runBlocking {
        for(field in listOf("hash","child","originalVersion","destination","partIdentity","previousHash")) fixture {f->
            f.pause(); f.changeMetadata(); f.batches.confirmSourceReview(f.review()); val child=f.child; f.batches.shutdown(); f.files.shutdown()
            val json=JSONObject(f.journal.readText()); json.getJSONArray("outcomes").getJSONObject(f.index).getJSONObject("sourceApproval").put(field,if(field=="child") StorageRules.token() else if(field in listOf("originalVersion","destination")) "f".repeat(64) else "bad")
            val kept=json.toString(); f.journal.writeText(kept); f.data.manager=f.data.manager(); f.batches=f.batchManager()
            assertTrue(f.batches.state.value.isEmpty()); assertEquals(1,f.batches.recoveryIssues().size); assertEquals(kept,f.journal.readText()); assertEquals(child,f.files.state.value.single())
            refused {f.review()}; refused {f.files.prepareSourceReview(child.id,f.data.peer,f.source.selected,f::request) {true}}
        }
    }
    @Test fun emptyChecksumCheckpointAndUnallocatedPartCanRenewThenBindNativePartOnPause()=runBlocking {
        for(unallocated in listOf(false,true)) fixture {f->
            f.pause(beforePreparation=!unallocated,beforeAllocation=unallocated); assertTrue(f.child.sha256.isEmpty()); f.changeMetadata()
            val review=f.review(); assertTrue(review.source.job.sha256.isEmpty()); f.batches.confirmSourceReview(review)
            val before=f.files.sourcePartIdentity(f.child.id); assertEquals(unallocated,PhoneBatchSourceApproval.unallocated(before))
            f.source.stopAt=1048576; f.batches.resume(f.id,f.data.peer,f.epoch,f::request); withTimeout(15000) {f.source.stopped.await()}; f.batches.pause(f.id); f.batches.awaitIdle(f.id); f.source.stopAt=null
            assertFalse(PhoneBatchSourceApproval.unallocated(f.parent.items[0].sourceApproval!!.partIdentity)); assertEquals(f.files.sourcePartIdentity(f.child.id),f.parent.items[0].sourceApproval!!.partIdentity)
            f.restart(); f.finish()
        }
    }
    @Test fun zeroByteCheckpointCanExplicitlyAdoptItsFirstFullChecksum()=runBlocking {fixture {f->
        f.source.bytes=byteArrayOf(); f.source.selected=f.source.selected.toBuilder().setSize(0).build(); f.pause(beforePreparation=true)
        f.batches.confirmSourceReview(f.review()); f.finish()
    }}
    @Test fun wrongPairingAndDirectoryItemsCannotAcquireAChildReview()=runBlocking {fixture {f->
        f.pause(nested=true); val before=f.child
        refused {f.batches.prepareSourceReview(f.id,f.index,"f".repeat(64),f.epoch,f.source.selected,f::request) {true}}
        refused {f.batches.prepareSourceReview(f.id,f.index,f.data.peer,StorageRules.token(),f.source.selected,f::request) {true}}
        refused {f.batches.prepareSourceReview(f.id,0,f.data.peer,f.epoch,f.source.selected,f::request) {true}}; assertEquals(before,f.child)
    }}
    @Test fun localBatchesCannotAcquireRemoteReceivingSourceAuthority()=runBlocking {fixture {f->
        f.source.selected=f.source.selected.toBuilder().setParentId(f.rootId).build()
        val localFiles=AndroidDownloadManager(f.context,File(f.data.directory,"local-receiving"),localCopy=true,batchOnly=true)
        val local=AndroidBatchDownloads(f.context,localFiles,File(f.data.directory,"local-batches"),localCopy=true)
        try {
            val id=local.enqueue("","",f.plan(),f.data.tree,PhoneCollision.KEEP_BOTH); f.source.stopAt=1048576
            local.resume(id,"","",f::request); withTimeout(15000) {f.source.stopped.await()}; local.pause(id); local.awaitIdle(id); f.source.stopAt=null
            val before=localFiles.state.value.single()
            refused {local.prepareSourceReview(id,0,"","",f.source.selected,f::request) {true}}; assertEquals(before,localFiles.state.value.single())
            local.cancel(id); local.remove(id)
        } finally {local.shutdown(); localFiles.shutdown()}
    }}
    @Test fun uncertainProviderPublicationCannotBeReplacedByFreshSourceApproval()=runBlocking {fixture {f->
        f.pause(); f.batches.confirmSourceReview(f.review())
        f.context.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"creationMode",f.data.bundle.getString("root"),Bundle().apply {putString("mode","loseReceipt")})
        f.batches.resume(f.id,f.data.peer,f.epoch,f::request).join(); assertEquals(PhoneBatchPhase.REVIEW,f.parent.phase); assertEquals(PhoneDownloadPhase.PUBLICATION_INTERRUPTED,f.child.phase)
        val child=f.child; val objects=f.providerChildren(); refused {f.review()}; assertEquals(child,f.child); assertEquals(objects,f.providerChildren())
    }}
    @Test fun authenticatedNativeAndroidBatchRenewalDeniesExpiredTicketAndKeepsApprovalSeparateFromCopying()=runBlocking {
        val f=ActivityControlsTest.Fixture(); val context=f.application; val instrumentation=InstrumentationRegistry.getInstrumentation()
        val bundle=requireNotNull(context.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"fixture",null,null)); var now=System.currentTimeMillis()
        val bytes="Generated authenticated batch source.\n".repeat(4096).toByteArray()
        context.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"change",bundle.getString("file"),Bundle().apply {putString("content",String(bytes))})
        val guestKey=TestPeer.key(); val hostIdentity=LocalIdentity(); val guestIdentity=object:SessionIdentity {
            override val publicKey=guestKey.public.encoded; override val id=Rules.id(publicKey)
            override fun sign(transcript:ByteArray)=Signature.getInstance("SHA256withECDSA").run {initSign(guestKey.private); update(transcript); sign()}
        }
        val store=AndroidStorageGrants(context); val approved=TestPeer.approve(store.owner,guestKey)
        store.add(approved,Uri.parse(bundle.getString("tree")),bundle.getInt("flags"),"Generated source")
        val sources=AndroidDownloadSources(context,store,now={now}); val service=AndroidFileService(context,store,sources)
        val files=AndroidDownloadManager(context,now={now}); val batches=AndroidBatchDownloads(context,files)
        val scope=CoroutineScope(SupervisorJob()+Dispatchers.Main.immediate); val ready=CompletableDeferred<Unit>(); val logs=java.util.concurrent.CopyOnWriteArrayList<String>()
        val session=StorageRules.token(); var host:RtcTestSession?=null; var guest:RtcTestSession?=null
        try {
            withContext(Dispatchers.Main) {
                host=RtcTestSession(instrumentation.targetContext,scope,hostIdentity,session,guestIdentity.publicKey,{kind,body,mid->when(kind) {"offer"->guest!!.acceptOffer(body); "candidate"->guest!!.addCandidate(body,mid)}},{logs.add(it)},{},fileService=service,initiator=true)
                guest=RtcTestSession(instrumentation.targetContext,scope,guestIdentity,session,hostIdentity.publicKey,{kind,body,mid->when(kind) {"answer"->host.acceptAnswer(body); "candidate"->host.addCandidate(body,mid)}},{logs.add(it)},{if(it) ready.complete(Unit)})
                host.offer()
            }
            withTimeout(35000) {ready.await()}
            suspend fun rpc(request:FileRequest)=withContext(Dispatchers.Main) {guest!!.requestFiles(request)}
            val root=rpc(FileRequest.newBuilder().setOperation(FileOperation.LIST_ROOTS).build()).entriesList.single()
            val entry=rpc(FileRequest.newBuilder().setOperation(FileOperation.LIST_DIRECTORY).setNodeId(root.id).build()).entriesList.single {it.name=="simu.txt"}
            val manifest=PhoneTransferManifests.build(listOf(PhoneTransferSelection(entry,listOf(entry.name))),::rpc)
            val id=batches.enqueue(hostIdentity.id,approved.epoch,manifest,Uri.parse(bundle.getString("tree")),PhoneCollision.KEEP_BOTH)
            val stopped=CompletableDeferred<Unit>(); batches.resume(id,hostIdentity.id,approved.epoch,request={request->if(request.operation==FileOperation.DOWNLOAD_READ && request.offset==32768L) {stopped.complete(Unit); awaitCancellation()}; rpc(request)})
            withTimeout(10000) {stopped.await()}; batches.pause(id); batches.awaitIdle(id); val child=files.state.value.single(); val part=files.sourcePartIdentity(child.id)
            now+=25*60*60*1000L
            assertEquals(FileStatus.ACCESS_DENIED,rpc(FileRequest.newBuilder().setOperation(FileOperation.DOWNLOAD_STATUS).setTransferId(child.ticket).build()).status)
            val review=batches.prepareSourceReview(id,0,hostIdentity.id,approved.epoch,entry,::rpc) {true}; batches.confirmSourceReview(review)
            assertEquals(child.offset,files.state.value.single().offset); assertEquals(part,files.sourcePartIdentity(child.id)); assertEquals(PhoneBatchPhase.PAUSED,batches.state.value.single().phase)
            batches.resume(id,hostIdentity.id,approved.epoch,::rpc).join(); val result=batches.state.value.single()
            assertEquals(result.error,PhoneBatchPhase.COMPLETED,result.phase); assertArrayEquals(bytes,context.contentResolver.openInputStream(Uri.parse(result.items.single().published))!!.use {it.readBytes()})
            batches.remove(id)
        } catch(e:Exception) {throw AssertionError("Native batch source renewal failed: ${logs.takeLast(12)}",e)}
        finally {
            batches.shutdown(); files.shutdown(); withContext(Dispatchers.Main) {host?.close(); guest?.close()}; scope.cancel(); service.close(); sources.shutdown(); store.revokePeer(approved.id)
            context.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"dispose",bundle.getString("root"),null); f.dispose()
        }
    }
}
