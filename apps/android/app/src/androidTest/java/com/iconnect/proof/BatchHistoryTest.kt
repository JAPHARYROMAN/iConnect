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
class BatchHistoryTest {
    private suspend fun fixture(extras:Int=0,body:suspend (BatchSenderTest.Fixture)->Unit) {val f=BatchSenderTest.Fixture(extras=extras); try {f.initialize(); body(f)} finally {f.close()}}
    private fun archive(f:BatchSenderTest.Fixture,id:String) {assertTrue(f.sender.confirmHistory(f.sender.reviewHistory(id,PhoneBatchHistoryAction.ARCHIVE)))}
    @Test fun archivedJobsKeepPrivateSourcesAndRestoreTheSameIntentWithoutAConnection()=runBlocking {fixture {f->
        val id=f.queue(); assertTrue(f.sender.sourceInUse(f.source.id)); archive(f,id)
        assertTrue(f.row().archived); assertFalse(f.sender.sourceInUse(f.source.id)); assertEquals(0,f.calls)
        f.reopen(); assertTrue(f.sender.recoveryIssues().isEmpty()); assertTrue(f.row().archived)
        assertTrue(f.sender.confirmHistory(f.sender.reviewHistory(id,PhoneBatchHistoryAction.RESTORE)))
        assertFalse(f.row().archived); assertTrue(f.sender.sourceInUse(f.source.id)); assertEquals(f.source,f.sources.state.value.single()); f.assertReleased()
    }}
    @Test fun archivedLostAdmissionCannotSendUntilRestoredAndDoesNotBeginAnotherPlan()=runBlocking {fixture {f->
        val id=f.queue(); f.send(id) {value->val reply=f.request(value); if(value.operation==FileOperation.BEGIN_UPLOAD_BATCH) throw IOException("Lost admitted reply"); reply}
        val header=f.row().header; archive(f,id); assertThrows(IllegalStateException::class.java) {f.sender.send(id,null,{true}) {f.request(it)}}
        assertThrows(IllegalStateException::class.java) {f.sender.queue(f.source.id)}; f.reopen()
        assertTrue(f.sender.confirmHistory(f.sender.reviewHistory(id,PhoneBatchHistoryAction.RESTORE))); f.send(id,null)
        assertEquals(PhoneBatchSendingPhase.COMPLETED,f.row().phase); assertEquals(header,f.row().header); assertEquals(1,f.begins)
    }}
    @Test fun activeWorkAndReusedOrModifiedReviewsCannotChangeHistory()=runBlocking {fixture {f->
        val id=f.queue(); val entered=CompletableDeferred<Unit>()
        val sending=f.sender.send(id,f.target,{true}) {entered.complete(Unit); awaitCancellation()}
        withTimeout(10_000) {entered.await()}; assertThrows(IllegalStateException::class.java) {f.sender.reviewHistory(id,PhoneBatchHistoryAction.ARCHIVE)}
        f.sender.pause(id); sending.join(); val review=f.sender.reviewHistory(id,PhoneBatchHistoryAction.ARCHIVE)
        assertThrows(IllegalStateException::class.java) {f.sender.confirmHistory(review.copy(results=999))}; assertThrows(IllegalStateException::class.java) {f.sender.confirmHistory(review)}
        val current=f.sender.reviewHistory(id,PhoneBatchHistoryAction.ARCHIVE); assertTrue(f.sender.confirmHistory(current)); assertThrows(IllegalStateException::class.java) {f.sender.confirmHistory(current)}
    }}
    @Test fun changedParentOrItemVersionsInvalidateReviewBeforeAnyRemoval()=runBlocking {fixture {f->
        val id=f.queue(); val review=f.sender.reviewHistory(id,PhoneBatchHistoryAction.ARCHIVE); f.send(id)
        assertThrows(IllegalStateException::class.java) {f.sender.confirmHistory(review)}; archive(f,id)
        val purge=f.sender.reviewHistory(id,PhoneBatchHistoryAction.PURGE); val file=File(f.root,"outgoing-batch-jobs/$id.0.json"); file.appendText(" ")
        assertThrows(IllegalStateException::class.java) {f.sender.confirmHistory(purge)}; assertTrue(file.exists()); assertTrue(f.record(id).exists())
    }}
    @Test fun revokingThePeerDoesNotPreventLocalOwnerHistoryCleanupOrRemoveAnyProviderCopy()=runBlocking {fixture {f->
        val id=f.queue(); f.send(id); val before=f.receiver.children(); val calls=f.calls; val bytes=f.sourceBytes("simu.txt"); f.policy.blockPeer(f.approval)
        archive(f,id); assertTrue(f.sender.confirmHistory(f.sender.reviewHistory(id,PhoneBatchHistoryAction.PURGE))); assertTrue(f.sender.state.value.isEmpty())
        assertEquals(calls,f.calls); assertEquals(before,f.receiver.children()); assertArrayEquals(bytes,f.sourceBytes("simu.txt")); assertEquals(f.source,f.sources.state.value.single())
        f.reopen(); assertTrue(f.sender.recoveryIssues().isEmpty())
    }}
    @Test fun historySlotsAreSeparateAndTheirBoundIsReleasedOnlyAfterReviewedPurge()=runBlocking {fixture {f->
        var id=f.queue(); val first=id
        repeat(PhoneBatchSender.MAX_HISTORY) {index->if(index!=0) id=f.sender.queue(f.source.id); archive(f,id)}
        val current=f.sender.queue(f.source.id); assertThrows(IllegalStateException::class.java) {f.sender.reviewHistory(current,PhoneBatchHistoryAction.ARCHIVE)}
        assertTrue(f.sender.confirmHistory(f.sender.reviewHistory(first,PhoneBatchHistoryAction.PURGE))); archive(f,current)
        assertEquals(PhoneBatchSender.MAX_HISTORY,f.sender.state.value.size); assertTrue(f.sender.state.value.all {it.archived}); assertEquals(f.source,f.sources.state.value.single())
        f.reopen(); assertTrue(f.sender.recoveryIssues().isEmpty()); assertEquals(PhoneBatchSender.MAX_HISTORY,f.sender.state.value.size)
    }}
    @Test fun anotherCopyRequiresExplicitChoiceAndCannotSilentlyReplaceAnActiveOriginalIntent()=runBlocking {fixture {f->
        val id=f.queue(); f.send(id) {value->val reply=f.request(value); if(value.operation==FileOperation.BEGIN_UPLOAD_BATCH) throw IOException("Lost reply"); reply}; archive(f,id)
        assertThrows(IllegalStateException::class.java) {f.sender.queue(f.source.id)}; val another=f.sender.queue(f.source.id,anotherCopy=true)
        assertThrows(IllegalStateException::class.java) {f.sender.reviewHistory(id,PhoneBatchHistoryAction.RESTORE)}; archive(f,another)
        assertTrue(f.sender.confirmHistory(f.sender.reviewHistory(id,PhoneBatchHistoryAction.RESTORE))); assertEquals(1,f.begins)
    }}
    @Test fun boundedPurgeCanRecoverDeletedFilesBeforeItsCursorCheckpointAndKeepsActualPublication()=runBlocking {fixture(extras=63) {f->
        val id=f.queue(); f.send(id); assertEquals(PhoneBatchSendingPhase.COMPLETED,f.row().phase); assertEquals(70,f.row().next); val parent=f.receivedRoot(); val bytes=f.receiver.read("simu.txt",parent); archive(f,id)
        assertFalse(f.sender.confirmHistory(f.sender.reviewHistory(id,PhoneBatchHistoryAction.PURGE))); assertTrue(f.row().purging); assertEquals(64,f.row().purgeCursor)
        f.sender.shutdown(); val file=f.record(id); file.writeText(JSONObject(file.readText()).put("purgeCursor",0).toString()); f.reopen()
        assertTrue(f.sender.recoveryIssues().isEmpty()); assertFalse(f.sender.continuePurge(id)); assertTrue(f.sender.continuePurge(id)); assertTrue(f.sender.state.value.isEmpty())
        assertTrue(File(f.root,"outgoing-batch-jobs").listFiles().orEmpty().none {it.name.startsWith(id)}); assertArrayEquals(bytes,f.receiver.read("simu.txt",parent)); assertEquals(65,f.receiver.children(parent).values.count {it.endsWith(".txt") || it=="zero.bin"}); assertEquals(f.source,f.sources.state.value.single())
        f.reopen(); assertTrue(f.sender.recoveryIssues().isEmpty()); f.assertReleased()
    }}
    @Test fun unreadableArchivedResultsCannotRestoreButCanBeOwnerReviewedAndPurged()=runBlocking {fixture {f->
        val id=f.queue(); f.send(id); val parent=f.receivedRoot(); val bytes=f.receiver.read("simu.txt",parent); archive(f,id)
        val file=File(f.root,"outgoing-batch-jobs/$id.0.json"); file.writeText("{damaged"); f.reopen(); assertTrue(f.sender.recoveryIssues().isEmpty())
        assertThrows(Exception::class.java) {f.sender.reviewHistory(id,PhoneBatchHistoryAction.RESTORE)}
        assertTrue(f.sender.confirmHistory(f.sender.reviewHistory(id,PhoneBatchHistoryAction.PURGE))); assertTrue(f.sender.state.value.isEmpty()); assertFalse(file.exists())
        assertArrayEquals(bytes,f.receiver.read("simu.txt",parent)); assertEquals(f.source,f.sources.state.value.single())
    }}
    @Test fun unexpectedJobObjectsInvalidateReviewAndArePreserved()=runBlocking {fixture {f->
        val id=f.queue(); archive(f,id); val review=f.sender.reviewHistory(id,PhoneBatchHistoryAction.PURGE)
        val foreign=File(f.root,"outgoing-batch-jobs/$id.bin"); foreign.writeText("owner data"); assertThrows(IllegalStateException::class.java) {f.sender.confirmHistory(review)}
        assertEquals("owner data",foreign.readText()); assertTrue(f.record(id).exists()); assertEquals(f.source,f.sources.state.value.single())
    }}
    @Test fun historyFromAnotherLocalOwnerCannotBeAdoptedOrPurged()=runBlocking {fixture {f->
        val id=f.queue(); archive(f,id); f.sender.shutdown(); val file=f.record(id); file.writeText(JSONObject(file.readText()).put("owner","c".repeat(64)).toString()); f.reopen()
        assertTrue(f.sender.recoveryIssues().isEmpty()); assertThrows(IllegalStateException::class.java) {f.sender.reviewHistory(id,PhoneBatchHistoryAction.PURGE)}; assertTrue(file.exists())
    }}
    @Test fun schemaOneJobsLoadWithoutChangingAuthorityAndUpgradeOnlyOnWrite()=runBlocking {fixture {f->
        val id=f.queue(); val before=f.row(); f.sender.shutdown(); val file=f.record(id); val saved=JSONObject(file.readText()).put("schema",1)
        listOf("archived","purging","purgeCursor","purgeCount").forEach {saved.remove(it)}; file.writeText(saved.toString()); f.reopen()
        assertTrue(f.sender.recoveryIssues().isEmpty()); assertEquals(before.owner,f.row().owner); assertEquals(before.pairing,f.row().pairing); assertEquals(before.source,f.row().source); assertFalse(f.row().archived)
        archive(f,id); assertEquals(2,JSONObject(file.readText()).getInt("schema"))
    }}
    @Test fun writtenItemAheadOfTheParentCursorRemainsOwnedAndDoesNotBecomeAnOrphanAfterPurge()=runBlocking {fixture {f->
        val id=f.queue(); f.send(id) {value->if(value.operation==FileOperation.FINISH_UPLOAD_BATCH) throw IOException("Crash before parent finish"); f.request(value)}
        val row=f.row(); val parent=f.receivedRoot(); val bytes=f.receiver.read("simu.txt",parent); f.sender.shutdown(); val file=f.record(id); val outcomes=Rules.decode(row.outcomes); outcomes[outcomes.lastIndex]=0
        file.writeText(JSONObject(file.readText()).put("next",row.items-1).put("attempted",true).put("outcomes",Rules.b64(outcomes)).toString()); f.reopen(); assertTrue(f.sender.recoveryIssues().isEmpty())
        archive(f,id); val review=f.sender.reviewHistory(id,PhoneBatchHistoryAction.PURGE); assertEquals(row.items,review.results); assertTrue(f.sender.confirmHistory(review)); f.reopen()
        assertTrue(f.sender.recoveryIssues().isEmpty()); assertTrue(f.sender.state.value.isEmpty()); assertTrue(f.receiver.grants.batchPlans.reservedBytes(null)>0); assertArrayEquals(bytes,f.receiver.read("simu.txt",parent))
    }}
    @Test fun missingFutureCleanupMetadataBlocksColdRecoveryAndPreservesAllProviderCopies()=runBlocking {fixture(extras=63) {f->
        val id=f.queue(); f.send(id); val parent=f.receivedRoot(); val bytes=f.receiver.read("simu.txt",parent); archive(f,id); assertFalse(f.sender.confirmHistory(f.sender.reviewHistory(id,PhoneBatchHistoryAction.PURGE)))
        f.sender.shutdown(); val file=f.record(id); file.writeText(JSONObject(file.readText()).put("purgeCursor",0).toString()); assertTrue(File(f.root,"outgoing-batch-jobs/$id.69.json").delete()); f.reopen()
        assertTrue(f.sender.recoveryIssues().isNotEmpty()); assertTrue(file.exists()); assertTrue(File(f.root,"outgoing-batch-jobs/$id.64.json").exists()); assertArrayEquals(bytes,f.receiver.read("simu.txt",parent)); assertEquals(f.source,f.sources.state.value.single())
    }}
    @Test fun purgingOneArchivedJobKeepsTheOtherJobsResultsAndEveryProviderCopy()=runBlocking {fixture {f->
        val first=f.queue(); f.send(first); archive(f,first); val firstRoot=f.receivedRoot(); val before=f.receiver.children(firstRoot)
        val second=f.sender.queue(f.source.id,anotherCopy=true); f.send(second); archive(f,second); val result=f.sender.itemResult(second,0)
        assertTrue(f.sender.confirmHistory(f.sender.reviewHistory(first,PhoneBatchHistoryAction.PURGE))); assertEquals(second,f.sender.state.value.single().id); assertEquals(result,f.sender.itemResult(second,0)); assertEquals(before,f.receiver.children(firstRoot))
        f.reopen(); assertTrue(f.sender.recoveryIssues().isEmpty()); assertEquals(result,f.sender.itemResult(second,0)); assertEquals(f.source,f.sources.state.value.single())
    }}
}
