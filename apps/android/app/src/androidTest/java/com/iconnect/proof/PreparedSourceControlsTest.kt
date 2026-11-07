package com.iconnect.proof

import androidx.test.ext.junit.runners.AndroidJUnit4
import com.iconnect.protocol.*
import kotlinx.coroutines.*
import org.json.JSONArray
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.util.concurrent.atomic.AtomicLong

@RunWith(AndroidJUnit4::class)
class PreparedSourceControlsTest {
    internal class Fixture {
        val base=IncomingUploadsTest.Fixture()
        val clock=AtomicLong(System.currentTimeMillis())
        var sources=AndroidDownloadSources(base.context,base.grants,now=clock::get)
        var service=AndroidFileService(base.context,base.grants,sources)
        val journal get()=File(base.root,"download-sources.json")
        val original get()=base.read("simu.txt")
        suspend fun raw(value:FileRequest,peer:String=base.approval.id)=service.execute(peer,value.toBuilder().setRequestId(StorageRules.token()).build())
        suspend fun call(value:FileRequest,peer:String=base.approval.id):FileResponse {val response=raw(value,peer); assertEquals(response.message,FileStatus.FILE_STATUS_OK,response.status); return response}
        suspend fun prepareResult(peer:String=base.approval.id):FileResponse {
            val root=call(FileRequest.newBuilder().setOperation(FileOperation.LIST_ROOTS).build(),peer).entriesList.single()
            val file=call(FileRequest.newBuilder().setOperation(FileOperation.LIST_DIRECTORY).setNodeId(root.id).build(),peer).entriesList.single {it.name=="simu.txt"}
            return raw(FileRequest.newBuilder().setOperation(FileOperation.PREPARE_DOWNLOAD).setNodeId(file.id).setExpectedVersion(file.version).build(),peer)
        }
        suspend fun prepare(peer:String=base.approval.id):DownloadInfo {val result=prepareResult(peer); assertEquals(result.message,FileStatus.FILE_STATUS_OK,result.status); return result.download}
        suspend fun ready(id:String,peer:String=base.approval.id):DownloadInfo=withTimeout(10_000) {
            var info=call(FileRequest.newBuilder().setOperation(FileOperation.DOWNLOAD_STATUS).setTransferId(id).build(),peer).download
            while(info.state!=DownloadState.DOWNLOAD_READY || sources.inventory.value.single {it.id==id}.preparing) {
                check(info.state!=DownloadState.DOWNLOAD_FAILED) {"Generated source preparation failed."}
                delay(10)
                info=call(FileRequest.newBuilder().setOperation(FileOperation.DOWNLOAD_STATUS).setTransferId(id).build(),peer).download
            }; info
        }
        suspend fun read(id:String,peer:String=base.approval.id)=raw(FileRequest.newBuilder().setOperation(FileOperation.DOWNLOAD_READ).setTransferId(id).setLength(1024).build(),peer)
        suspend fun reopen() {service.close(); sources.shutdown(); sources=AndroidDownloadSources(base.context,base.grants,now=clock::get); service=AndroidFileService(base.context,base.grants,sources)}
        suspend fun close() {service.close(); sources.shutdown(); base.close()}
    }
    private suspend fun refused(action:suspend()->Unit) {var failed=false; try {action()} catch(_:Exception) {failed=true}; assertTrue("Expected source-control refusal",failed)}
    @Test fun allPeerInventoryAndOwnerStopKeepIndependentApprovalAndProviderOriginal():Unit=runBlocking {
        val f=Fixture(); try {
            val other=TestPeer.approve(f.base.policy,TestPeer.key()); f.base.grants.add(other,f.base.tree,f.base.data.getInt("flags"),"Other computer")
            val original=f.original; val one=f.prepare(); val two=f.prepare(other.id); f.ready(one.id); f.ready(two.id,other.id)
            assertEquals(setOf(f.base.approval.id,other.id),f.sources.inventory.value.map {it.peer}.toSet())
            f.sources.stopOwned(one.id) {true}; assertEquals(two.id,f.sources.inventory.value.single().id)
            assertNotEquals(FileStatus.FILE_STATUS_OK,f.read(one.id).status); assertEquals(FileStatus.FILE_STATUS_OK,f.read(two.id,other.id).status)
            assertArrayEquals(original,f.original); assertEquals(2,f.base.grants.all().size)
        } finally {f.close()}
    }
    @Test fun successfulStopSurvivesRestartAndFolderGrantStillAllowsAFreshApproval():Unit=runBlocking {
        val f=Fixture(); try {val one=f.prepare(); f.ready(one.id); f.sources.stopOwned(one.id) {true}; f.sources.stopOwned(one.id) {true}; f.reopen()
            assertTrue(f.sources.inventory.value.isEmpty()); assertNotEquals(FileStatus.FILE_STATUS_OK,f.read(one.id).status)
            val fresh=f.prepare(); assertNotEquals(one.id,fresh.id); f.ready(fresh.id); assertEquals(FileStatus.FILE_STATUS_OK,f.read(fresh.id).status)
        } finally {f.close()}
    }
    @Test fun stoppingOneOfEightPeerSlotsLeavesSevenAndAdmitsOneReplacement():Unit=runBlocking {
        val f=Fixture(); try {val all=(0 until 8).map {f.prepare()}; assertEquals(FileStatus.RESOURCE_EXHAUSTED,f.prepareResult().status)
            f.sources.stopOwned(all.first().id) {true}; val next=f.prepare(); assertEquals(8,f.sources.inventory.value.size)
            assertFalse(f.sources.inventory.value.any {it.id==all.first().id}); assertTrue(f.sources.inventory.value.any {it.id==next.id})
        } finally {f.close()}
    }
    @Test fun ownerStopDrainsPreparationAndStatusCannotRestartTheOldTicket():Unit=runBlocking {
        val f=Fixture(); try {val source=f.prepare(); f.sources.stopOwned(source.id) {true}; assertTrue(f.sources.inventory.value.isEmpty())
            assertNotEquals(FileStatus.FILE_STATUS_OK,f.raw(FileRequest.newBuilder().setOperation(FileOperation.DOWNLOAD_STATUS).setTransferId(source.id).build()).status)
            assertTrue(f.original.isNotEmpty())
        } finally {f.close()}
    }
    @Test fun rejectedOwnerContextDoesNotChangeMetadataOrApproval():Unit=runBlocking {
        val f=Fixture(); try {val source=f.prepare(); f.ready(source.id); val before=f.journal.readBytes()
            refused {f.sources.stopOwned(source.id) {false}}; refused {f.sources.removeExpiredOwned {false}}
            assertArrayEquals(before,f.journal.readBytes()); assertEquals(FileStatus.FILE_STATUS_OK,f.read(source.id).status)
        } finally {f.close()}
    }
    @Test fun explicitExpiryCleanupKeepsTheLaterLiveApprovalAndPersistsRemoval():Unit=runBlocking {
        val f=Fixture(); try {val one=f.prepare(); f.ready(one.id); f.clock.addAndGet(12*3_600_000); val two=f.prepare(); f.ready(two.id)
            f.clock.addAndGet(13*3_600_000); f.sources.refresh(); assertTrue(f.sources.inventory.value.single {it.id==one.id}.expired)
            assertFalse(f.sources.state.value.any {it.id==one.id}); assertEquals(1,f.sources.removeExpiredOwned {true}); assertEquals(0,f.sources.removeExpiredOwned {true})
            f.reopen(); assertEquals(two.id,f.sources.inventory.value.single().id); assertEquals(FileStatus.FILE_STATUS_OK,f.read(two.id).status)
        } finally {f.close()}
    }
    @Test fun reopeningAndNewAdmissionPersistExpiredOperationalCleanup():Unit=runBlocking {
        for(reopen in listOf(false,true)) {val f=Fixture(); try {val one=f.prepare(); f.ready(one.id); val original=f.original; f.clock.addAndGet(25*3_600_000)
            if(reopen) {f.reopen(); assertTrue(f.sources.inventory.value.isEmpty()); assertEquals(0,JSONArray(f.journal.readText()).length())}
            else {val fresh=f.prepare(); f.ready(fresh.id); assertEquals(fresh.id,f.sources.inventory.value.single().id); assertFalse(f.journal.readText().contains(one.id))}
            assertArrayEquals(original,f.original)
        } finally {f.close()}}
    }
    @Test fun peerBoundRemoteReleaseCannotCancelAnotherComputersApproval():Unit=runBlocking {
        val f=Fixture(); try {val source=f.prepare(); f.ready(source.id); f.sources.release("c".repeat(64),source.id)
            assertEquals(source.id,f.sources.inventory.value.single().id); assertEquals(FileStatus.FILE_STATUS_OK,f.read(source.id).status)
        } finally {f.close()}
    }
    @Test fun expiredInventoryDoesNotConferActiveOrBackgroundAuthority():Unit=runBlocking {
        val f=Fixture(); try {val source=f.prepare(); f.ready(source.id); f.clock.addAndGet(25*3_600_000); f.sources.refresh()
            assertTrue(f.sources.inventory.value.single().expired); assertTrue(f.sources.state.value.isEmpty()); assertFalse(f.sources.canUse(f.base.approval.id,source.id,true))
            assertFalse(f.sources.isLive(f.base.approval.id,source.id,"old-scope")); assertNotEquals(FileStatus.FILE_STATUS_OK,f.read(source.id).status)
        } finally {f.close()}
    }
    @Test fun aPausedApprovalIsVisibleWithoutResumingItAndOwnerCanForgetIt():Unit=runBlocking {
        val f=Fixture(); try {val source=f.prepare(); f.ready(source.id); val revision=f.sources.block(source.id); f.sources.persistPause(source.id,revision); f.reopen()
            assertTrue(f.sources.inventory.value.single().paused); assertNotEquals(FileStatus.FILE_STATUS_OK,f.read(source.id).status)
            f.sources.stopOwned(source.id) {true}; assertTrue(f.sources.inventory.value.isEmpty())
        } finally {f.close()}
    }
    @Test fun failedStopWriteBlocksTicketedReadsAndPreservesTheLastSavedMetadataAndOriginal():Unit=runBlocking {
        val f=Fixture(); try {val one=f.prepare(); val two=f.prepare(); f.ready(one.id); f.ready(two.id); val before=f.journal.readBytes(); val original=f.original
            val blocker=File(f.journal.path+".new"); check(blocker.mkdir()); File(blocker,"keep.txt").writeText("Generated blocker")
            refused {f.sources.stopOwned(one.id) {true}}; assertFalse(f.sources.canUse(f.base.approval.id,two.id)); assertNotEquals(FileStatus.FILE_STATUS_OK,f.read(two.id).status)
            assertArrayEquals(before,f.journal.readBytes()); assertArrayEquals(original,f.original); assertTrue(f.sources.recoveryIssues().isNotEmpty())
        } finally {f.close()}
    }
    @Test fun ambiguousPrimaryAndTemporaryOrBackupStateRequireOwnerReview():Unit=runBlocking {
        for(suffix in listOf(".new",".bak")) {val f=Fixture(); try {val source=f.prepare(); f.ready(source.id); val original=f.original; f.journal.copyTo(File(f.journal.path+suffix)); f.reopen()
            assertTrue(f.sources.recoveryIssues().isNotEmpty()); assertTrue(f.sources.inventory.value.isEmpty()); assertNotEquals(FileStatus.FILE_STATUS_OK,f.read(source.id).status); assertArrayEquals(original,f.original)
        } finally {f.close()}}
    }
    @Test fun closedStoresCannotPrepareOrServeEvenWithRetainedMetadata():Unit=runBlocking {
        val f=Fixture(); try {val source=f.prepare(); f.ready(source.id); f.sources.shutdown(); assertFalse(f.sources.canUse(f.base.approval.id,source.id,true))
            assertNotEquals(FileStatus.FILE_STATUS_OK,f.read(source.id).status); refused {f.sources.stopOwned(source.id) {true}}
        } finally {f.close()}
    }
}
