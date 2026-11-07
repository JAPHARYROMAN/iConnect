package com.iconnect.proof

import android.net.Uri
import android.os.Bundle
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.google.protobuf.ByteString
import com.iconnect.protocol.*
import kotlinx.coroutines.*
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.io.RandomAccessFile
import java.security.MessageDigest
import java.security.Signature

@RunWith(AndroidJUnit4::class)
class DownloadSourceRecoveryTest {
    internal class Fixture {
        val context=InstrumentationRegistry.getInstrumentation().targetContext
        val bundle=requireNotNull(context.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"fixture",null,null))
        val tree=Uri.parse(bundle.getString("tree"))
        val directory=File(context.filesDir,"source-recovery-${StorageRules.token()}").apply {check(mkdirs())}
        val peer="a".repeat(64)
        var now=System.currentTimeMillis(); var elapsed=1000L
        @Volatile var allowed=true
        val owner="test:${StorageRules.token()}"
        val access=AndroidSafAccess(context).apply {acquire(owner,tree,bundle.getInt("flags"))}
        var manager=manager()
        fun manager(local:Boolean=false,batch:Boolean=false)=AndroidDownloadManager(context,directory,local,batch,{now},{elapsed})
        val source=Source(this)
        var id=""
        val row get()=manager.state.value.single {it.id==id}
        suspend fun paused() {
            id=manager.enqueue(peer,source.selected,tree)
            source.stopAt=1048576
            manager.resume(id,peer,source::request)
            withTimeout(10000) {source.stopped.await()}; manager.pause(id); manager.awaitIdle(id); source.stopAt=null
            assertEquals(1048576L,row.offset); assertEquals(PhoneDownloadPhase.PAUSED,row.phase)
        }
        suspend fun review()=manager.prepareSourceReview(id,peer,source.selected,source::request) {allowed}
        suspend fun dispose() {
            manager.shutdown(); manager=manager()
            manager.state.value.forEach {if(it.phase !in setOf(PhoneDownloadPhase.COMPLETED,PhoneDownloadPhase.CANCELED,PhoneDownloadPhase.SKIPPED)) manager.cancel(it.id); manager.remove(it.id)}
            manager.shutdown(); access.release(owner)
            context.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"dispose",bundle.getString("root"),null)
            check(directory.canonicalFile.parentFile==context.filesDir.canonicalFile); check(directory.deleteRecursively())
        }
    }
    internal class Source(private val fixture:Fixture) {
        var bytes=ByteArray(2*1024*1024+17) {(it*31%251).toByte()}
        var selected=FileEntry.newBuilder().setId(StorageRules.token()).setGrantId(StorageRules.token()).setName("simu.txt")
            .setVersion("b".repeat(64)).setPermissions(StorageRules.READ_ONLY).setSize(bytes.size.toLong()).build()
        val infos=linkedMapOf<String,DownloadInfo>(); val payloads=mutableMapOf<String,ByteArray>(); val released=java.util.concurrent.CopyOnWriteArrayList<String>()
        var stopAt:Long?=null; val stopped=CompletableDeferred<Unit>(); var reads=0
        var transform:(DownloadInfo)->DownloadInfo={it}
        var before:suspend(FileRequest)->Unit={}
        var reuse=""
        suspend fun request(request:FileRequest):FileResponse {
            before(request)
            val response=FileResponse.newBuilder()
            when(request.operation) {
                FileOperation.GET_METADATA->response.addEntries(selected)
                FileOperation.PREPARE_DOWNLOAD->{
                    val id=reuse.ifEmpty {StorageRules.token()}
                    val info=DownloadInfo.newBuilder().setId(id).setName(selected.name).setVersion(selected.version).setSize(selected.size)
                        .setPreparedBytes(selected.size).setExpiresAt(fixture.now+86400000).setState(DownloadState.DOWNLOAD_READY)
                        .setSha256(ByteString.copyFrom(MessageDigest.getInstance("SHA-256").digest(bytes))).build()
                    infos[id]=info; payloads[id]=bytes.copyOf(); response.download=transform(info)
                }
                FileOperation.DOWNLOAD_STATUS->response.download=transform(infos.getValue(request.transferId))
                FileOperation.DOWNLOAD_READ->{
                    if(request.offset==stopAt) {stopped.complete(Unit); awaitCancellation()}
                    reads++; val data=payloads.getValue(request.transferId); val end=minOf(data.size,request.offset.toInt()+request.length)
                    response.content=ByteString.copyFrom(data.copyOfRange(request.offset.toInt(),end)); response.endOfFile=end==data.size
                }
                FileOperation.RELEASE_DOWNLOAD->released.add(request.transferId)
                else->error("Unexpected operation ${request.operation}")
            }
            return response.build()
        }
    }
    private suspend fun fixture(work:suspend(Fixture)->Unit) {val f=Fixture(); try {work(f)} finally {f.dispose()}}
    private suspend fun rejected(work:suspend()->Unit) {try {work(); fail("Expected refusal")} catch(_:Exception) {}}
    private suspend fun waitFor(condition:()->Boolean) {withTimeout(5000) {while(!condition()) delay(10)}}

    @Test fun freshSourceAdoptionKeepsThePrivatePrefixAndDestinationThenResumesAfterRestart()=runBlocking {fixture {f->
        f.paused(); val before=f.row; val part=File(f.directory,"${f.id}.part"); val prefix=part.readBytes()
        f.now+=25*60*60*1000L
        f.source.selected=f.source.selected.toBuilder().setId(StorageRules.token()).setVersion("c".repeat(64)).build()
        val review=f.review(); assertEquals(before,f.row); f.manager.confirmSourceReview(review)
        assertEquals(before.copy(node=f.source.selected.id,version=f.source.selected.version,ticket=f.row.ticket,sourceExpiresAt=f.row.sourceExpiresAt),f.row)
        assertArrayEquals(prefix,part.readBytes()); assertTrue(f.source.released.contains(before.ticket)); assertFalse(f.row.active)
        f.manager.shutdown(); f.manager=f.manager(); f.manager.resume(f.id,f.peer,f.source::request).join()
        assertEquals(f.row.error,PhoneDownloadPhase.READY_TO_SAVE,f.row.phase); f.manager.publish(f.id).join()
        assertEquals(f.row.error,PhoneDownloadPhase.COMPLETED,f.row.phase)
        assertEquals("simu (1).txt",f.row.publishedName)
        assertArrayEquals(f.source.bytes,f.context.contentResolver.openInputStream(Uri.parse(f.row.published))!!.use {it.readBytes()})
    }}
    @Test fun differentFullHashRefusesEvenWhenNameSizeAndPrefixMatch()=runBlocking {fixture {f->
        f.paused(); val before=f.row; f.source.bytes[f.source.bytes.lastIndex]=(f.source.bytes.last().toInt() xor 1).toByte()
        rejected {f.review()}; assertEquals(before,f.row); assertTrue(f.source.released.any {it!=before.ticket})
    }}
    @Test fun cancellationReleasesOnlyTheUnusedOfferAndLeavesTheJournalUntouched()=runBlocking {fixture {f->
        f.paused(); val before=f.row; val review=f.review(); f.manager.cancelSourceReview(review.id)
        waitFor {f.source.released.size==1}; assertFalse(f.source.released.contains(before.ticket)); assertEquals(before,f.row)
        rejected {f.manager.confirmSourceReview(review)}
    }}
    @Test fun monotonicReviewExpiryCannotBeRevivedByMovingWallClockBackward()=runBlocking {fixture {f->
        f.paused(); val before=f.row; val review=f.review(); f.elapsed+=300000; f.now-=600000
        rejected {f.manager.confirmSourceReview(review)}; assertEquals(before,f.row); assertTrue(f.source.released.any {it!=before.ticket})
    }}
    @Test fun changedConnectionAndHideThenShowInvalidateTheReview()=runBlocking {fixture {f->
        f.paused(); val before=f.row; val review=f.review(); f.allowed=false
        rejected {f.manager.confirmSourceReview(review)}; assertEquals(before,f.row)
        f.allowed=true; val second=f.review(); f.manager.invalidateSourceReviews(); rejected {f.manager.confirmSourceReview(second)}; assertEquals(before,f.row)
    }}
    @Test fun changedStatusHashExpiryAndVersionCannotReplaceTheReviewedDescriptor()=runBlocking {fixture {f->
        f.paused(); val before=f.row
        for(change in listOf<(DownloadInfo)->DownloadInfo>({it.toBuilder().setSha256(ByteString.copyFrom(ByteArray(32))).build()},
            {it.toBuilder().setExpiresAt(it.expiresAt+1).build()},{it.toBuilder().setVersion("d".repeat(64)).build()})) {
            val review=f.review(); f.source.transform=change; rejected {f.manager.confirmSourceReview(review)}; f.source.transform={it}; assertEquals(before,f.row)
        }
    }}
    @Test fun sourceCannotClearOrSilentlyExtendTheSavedDeadline()=runBlocking {fixture {f->
        f.paused(); val expiry=f.row.sourceExpiresAt
        f.source.transform={it.toBuilder().setExpiresAt(expiry+1000000).build()}; f.manager.resume(f.id,f.peer,f.source::request).join()
        assertEquals(expiry,f.row.sourceExpiresAt)
    }}
    @Test fun shortenedStatusDeadlineIsSavedAndAnExpiredDeadlineRequestsNoMoreRanges()=runBlocking {fixture {f->
        f.paused(); val expiry=f.row.sourceExpiresAt; f.source.transform={it.toBuilder().setExpiresAt(expiry-1000).build()}
        val entered=CompletableDeferred<Unit>(); f.source.before={if(it.operation==FileOperation.DOWNLOAD_READ) {entered.complete(Unit); awaitCancellation()}}
        f.manager.resume(f.id,f.peer,f.source::request); withTimeout(10000) {entered.await()}; f.manager.pause(f.id); f.manager.awaitIdle(f.id); f.source.before={}
        assertEquals(expiry-1000,f.row.sourceExpiresAt); f.source.stopAt=null; val count=f.source.reads; f.now=expiry
        f.source.transform={it.toBuilder().setExpiresAt(expiry+1000000).build()}; f.manager.resume(f.id,f.peer,f.source::request).join()
        assertEquals(count,f.source.reads); assertEquals(1048576L,f.row.offset); assertEquals(PhoneDownloadPhase.INTERRUPTED,f.row.phase)
    }}
    @Test fun corruptPrefixStillFailsAfterApprovalAndNeverPublishes()=runBlocking {fixture {f->
        f.paused(); f.manager.confirmSourceReview(f.review()); RandomAccessFile(File(f.directory,"${f.id}.part"),"rw").use {it.write(99)}
        f.manager.resume(f.id,f.peer,f.source::request).join(); assertEquals(PhoneDownloadPhase.INTERRUPTED,f.row.phase); assertTrue(f.row.error.contains("partial")); assertTrue(f.row.published.isEmpty())
    }}
    @Test fun shortenedDeadlineSurvivesFailedPreparationAndCannotBeRaisedOnRetry()=runBlocking {fixture {f->
        f.paused(); val original=f.row.sourceExpiresAt; var statuses=0
        f.source.before={if(it.operation==FileOperation.DOWNLOAD_STATUS && ++statuses==2) error("Generated interrupted preparation")}
        f.source.transform={it.toBuilder().setExpiresAt(original-1000).setState(DownloadState.DOWNLOAD_PREPARING).setPreparedBytes(0).clearSha256().build()}
        f.manager.resume(f.id,f.peer,f.source::request).join(); assertEquals(original-1000,f.row.sourceExpiresAt)
        f.now=original-500; f.source.before={}; f.source.transform={it.toBuilder().setExpiresAt(original+1000000).build()}; val reads=f.source.reads
        f.manager.resume(f.id,f.peer,f.source::request).join(); assertEquals(original-1000,f.row.sourceExpiresAt); assertEquals(reads,f.source.reads)
    }}
    @Test fun clearedDeadlineIsRejectedAndCanceledPreparationReleasesItsNewLease()=runBlocking {fixture {f->
        f.paused(); val before=f.row; f.source.transform={it.toBuilder().setExpiresAt(0).build()}
        f.manager.resume(f.id,f.peer,f.source::request).join(); assertEquals(before.sourceExpiresAt,f.row.sourceExpiresAt); assertEquals(before.offset,f.row.offset)
        val paused=f.row; val entered=CompletableDeferred<Unit>()
        f.source.transform={it.toBuilder().setState(DownloadState.DOWNLOAD_PREPARING).setPreparedBytes(0).clearSha256().build()}
        f.source.before={if(it.operation==FileOperation.DOWNLOAD_STATUS) {entered.complete(Unit); awaitCancellation()}}
        val task=async {f.review()}; entered.await(); task.cancelAndJoin(); f.source.before={}; f.source.transform={it}
        assertEquals(paused,f.row); assertTrue(f.source.released.any {it!=before.ticket})
    }}
    @Test fun batchAndLocalCopiesCannotAcquireStandaloneSourceAuthority()=runBlocking {fixture {f->
        f.manager.shutdown(); f.manager=f.manager(batch=true); f.id=f.manager.enqueue(f.peer,f.source.selected,f.tree,batchId=StorageRules.token())
        rejected {f.review()}; f.manager.cancel(f.id); f.manager.remove(f.id); f.manager.shutdown(); f.manager=f.manager(local=true)
        f.id=f.manager.enqueue("",f.source.selected,f.tree); rejected {f.manager.prepareSourceReview(f.id,"",f.source.selected,f.source::request) {true}}
        f.manager.cancel(f.id); f.manager.remove(f.id); f.manager.shutdown(); f.manager=f.manager()
    }}
    @Test fun wrongPeerNameSizeAndReusedTicketsCannotChangeTheOldReceipt()=runBlocking {fixture {f->
        f.paused(); val before=f.row
        rejected {f.manager.prepareSourceReview(f.id,"f".repeat(64),f.source.selected,f.source::request) {true}}
        for(entry in listOf(f.source.selected.toBuilder().setName("other.txt").build(),f.source.selected.toBuilder().setSize(1).build())) rejected {f.manager.prepareSourceReview(f.id,f.peer,entry,f.source::request) {true}}
        f.source.reuse=before.ticket; rejected {f.review()}; assertEquals(before,f.row); assertTrue(f.source.released.isEmpty())
    }}
    @Test fun emptyCheckpointCanExplicitlyAdoptWithoutAnOriginalChecksumIncludingZeroBytes()=runBlocking {fixture {f->
        f.source.bytes=byteArrayOf(); f.source.selected=f.source.selected.toBuilder().setSize(0).build(); f.id=f.manager.enqueue(f.peer,f.source.selected,f.tree)
        val review=f.review(); assertTrue(review.job.sha256.isEmpty()); f.manager.confirmSourceReview(review)
        f.manager.resume(f.id,f.peer,f.source::request).join(); assertEquals(PhoneDownloadPhase.READY_TO_SAVE,f.row.phase)
    }}
    @Test fun receivingCannotStartWhileAReviewIsOpenAndCancelThenRemoveInvalidatesIt()=runBlocking {fixture {f->
        f.paused(); val review=f.review(); rejected {f.manager.resume(f.id,f.peer,f.source::request)}
        f.manager.cancel(f.id); f.manager.remove(f.id); rejected {f.manager.confirmSourceReview(review)}; assertTrue(f.manager.state.value.isEmpty())
    }}
    @Test fun reviewsAreSingleUseBoundedAndCannotOutliveTheirPreparedSource()=runBlocking {fixture {f->
        val offers=mutableListOf<PhoneDownloadSourceReview>()
        repeat(8) {f.id=f.manager.enqueue(f.peer,f.source.selected,f.tree); offers.add(f.review())}
        f.id=f.manager.enqueue(f.peer,f.source.selected,f.tree); rejected {f.review()}
        offers.forEach {f.manager.cancelSourceReview(it.id)}; waitFor {f.source.released.size==8}
        val review=f.review(); val before=f.row; f.now=review.sourceExpiresAt
        rejected {f.manager.confirmSourceReview(review)}; assertEquals(before,f.row)
        f.now-=1000; val next=f.review(); f.manager.confirmSourceReview(next); val adopted=f.row
        rejected {f.manager.confirmSourceReview(next)}; assertEquals(adopted,f.row)
    }}
    @Test fun shutdownCancelsPreparationAndConfirmationThenReleasesTheirFreshTickets()=runBlocking {
        for(operation in listOf(FileOperation.DOWNLOAD_STATUS,FileOperation.GET_METADATA)) fixture {f->
            f.paused(); val before=f.row; val offer=if(operation==FileOperation.DOWNLOAD_STATUS) f.review() else null
            val entered=CompletableDeferred<Unit>(); f.source.before={if(it.operation==operation) {entered.complete(Unit); awaitCancellation()}}
            val task=async {runCatching {if(offer==null) f.review() else f.manager.confirmSourceReview(offer)}}
            entered.await(); f.manager.shutdown(); task.join(); f.source.before={}
            f.manager=f.manager(); assertEquals(before,f.row)
            if(offer!=null) assertTrue(f.source.released.any {it!=before.ticket})
        }
    }
    @Test fun legacyExpiryRemainsUnknownAndInvalidExpiryUsesTheExistingRecoveryFlow()=runBlocking {fixture {f->
        f.paused(); val file=File(f.directory,"${f.id}.json"); f.manager.shutdown(); val json=JSONObject(file.readText()); json.remove("sourceExpiresAt"); file.writeText(json.toString())
        f.manager=f.manager(); assertEquals(0L,f.row.sourceExpiresAt); f.manager.shutdown(); json.put("sourceExpiresAt",-1); file.writeText(json.toString()); f.manager=f.manager()
        assertTrue(f.manager.state.value.isEmpty()); assertEquals(1,f.manager.recoveryIssues().size)
        // Preserve the generated invalid record through its normal explicit recovery operation before cleanup.
        f.manager.archiveRecovery(f.manager.recoveryIssues().single().id)
    }}

    @Test fun authenticatedNativeAndroidChannelsRenewAnActuallyExpiredProviderSourceAndKeepSavingSeparate()=runBlocking {
        val f=ActivityControlsTest.Fixture(); val context=f.application; val instrumentation=InstrumentationRegistry.getInstrumentation()
        val bundle=requireNotNull(context.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"fixture",null,null)); var now=System.currentTimeMillis()
        val bytes="Generated approved Android source.\n".repeat(4096).toByteArray()
        context.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"change",bundle.getString("file"),Bundle().apply {putString("content",String(bytes))})
        val guestKey=TestPeer.key(); val hostIdentity=LocalIdentity(); val guestIdentity=object:SessionIdentity {
            override val publicKey=guestKey.public.encoded; override val id=Rules.id(publicKey)
            override fun sign(transcript:ByteArray)=Signature.getInstance("SHA256withECDSA").run {initSign(guestKey.private); update(transcript); sign()}
        }
        val store=AndroidStorageGrants(context); val approved=TestPeer.approve(store.owner,guestKey)
        store.add(approved,Uri.parse(bundle.getString("tree")),bundle.getInt("flags"),"Generated source")
        val sources=AndroidDownloadSources(context,store,now={now}); val service=AndroidFileService(context,store,sources)
        val manager=AndroidDownloadManager(context,now={now}); val scope=CoroutineScope(SupervisorJob()+Dispatchers.Main.immediate)
        val session=StorageRules.token(); val ready=CompletableDeferred<Unit>(); val logs=java.util.concurrent.CopyOnWriteArrayList<String>()
        var host:RtcTestSession?=null; var guest:RtcTestSession?=null
        try {
            withContext(Dispatchers.Main) {
                host=RtcTestSession(instrumentation.targetContext,scope,hostIdentity,session,guestIdentity.publicKey,{kind,body,mid->when(kind) {"offer"->guest!!.acceptOffer(body); "candidate"->guest!!.addCandidate(body,mid)}},{logs.add(it)},{},fileService=service,initiator=true)
                guest=RtcTestSession(instrumentation.targetContext,scope,guestIdentity,session,hostIdentity.publicKey,{kind,body,mid->when(kind) {"answer"->host!!.acceptAnswer(body); "candidate"->host!!.addCandidate(body,mid)}},{logs.add(it)},{if(it) ready.complete(Unit)})
                host!!.offer()
            }
            withTimeout(35000) {ready.await()}
            suspend fun rpc(request:FileRequest)=withContext(Dispatchers.Main) {guest!!.requestFiles(request)}
            val root=rpc(FileRequest.newBuilder().setOperation(FileOperation.LIST_ROOTS).build()).entriesList.single()
            val entry=rpc(FileRequest.newBuilder().setOperation(FileOperation.LIST_DIRECTORY).setNodeId(root.id).build()).entriesList.single {it.name=="simu.txt"}
            val id=manager.enqueue(hostIdentity.id,entry,Uri.parse(bundle.getString("tree")))
            val stopped=CompletableDeferred<Unit>(); manager.resume(id,hostIdentity.id) {request->if(request.operation==FileOperation.DOWNLOAD_READ && request.offset==32768L) {stopped.complete(Unit); awaitCancellation()}; rpc(request)}
            withTimeout(10000) {stopped.await()}; manager.pause(id); manager.awaitIdle(id); val before=manager.state.value.single()
            now+=25*60*60*1000L
            assertEquals(FileStatus.ACCESS_DENIED,rpc(FileRequest.newBuilder().setOperation(FileOperation.DOWNLOAD_STATUS).setTransferId(before.ticket).build()).status)
            val review=manager.prepareSourceReview(id,hostIdentity.id,entry,::rpc) {true}; manager.confirmSourceReview(review)
            assertEquals(before.offset,manager.state.value.single().offset); assertEquals(PhoneDownloadPhase.PAUSED,manager.state.value.single().phase)
            manager.resume(id,hostIdentity.id,::rpc).join(); assertEquals(manager.state.value.single().error,PhoneDownloadPhase.READY_TO_SAVE,manager.state.value.single().phase)
            manager.publish(id).join(); val completed=manager.state.value.single(); assertEquals(completed.error,PhoneDownloadPhase.COMPLETED,completed.phase)
            assertArrayEquals(bytes,context.contentResolver.openInputStream(Uri.parse(completed.published))!!.use {it.readBytes()}); manager.remove(id)
        } catch(e:Exception) {throw AssertionError("Native source renewal failed: ${logs.takeLast(12)}",e)}
        finally {
            manager.shutdown(); withContext(Dispatchers.Main) {host?.close(); guest?.close()}; scope.cancel(); service.close(); sources.shutdown(); store.revokePeer(approved.id)
            context.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"dispose",bundle.getString("root"),null); f.dispose()
        }
    }
}
