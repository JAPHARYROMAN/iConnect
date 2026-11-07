package com.iconnect.proof

import androidx.test.ext.junit.runners.AndroidJUnit4
import com.iconnect.protocol.*
import kotlinx.coroutines.*
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

@RunWith(AndroidJUnit4::class)
class BatchCopyHistoryTest {
    private suspend fun fixture(body:suspend (BatchSenderTest.Fixture)->Unit) {val f=BatchSenderTest.Fixture(); try {f.initialize(); body(f)} finally {f.close()}}
    private fun sourceRecord(f:BatchSenderTest.Fixture)=File(f.root,"outgoing-batch-copies/${f.source.id}.json")
    private fun purge(f:BatchSenderTest.Fixture,id:String) {
        assertTrue(f.sender.confirmHistory(f.sender.reviewHistory(id,PhoneBatchHistoryAction.ARCHIVE)))
        var done=f.sender.confirmHistory(f.sender.reviewHistory(id,PhoneBatchHistoryAction.PURGE))
        while(!done) done=f.sender.continuePurge(id)
    }
    @Test fun snapshotRemembersPossibleReceiverEffectsBeforeFirstBegin()=runBlocking {fixture {f->
        val id=f.queue(); val before=f.source; assertFalse(before.mayHaveReceiverCopies); var checked=false
        f.send(id) {request->
            if(request.operation==FileOperation.BEGIN_UPLOAD_BATCH) {
                checked=true; assertTrue(f.sources.state.value.single().mayHaveReceiverCopies)
                val saved=JSONObject(sourceRecord(f).readText()); assertEquals(2,saved.getInt("schema")); assertTrue(saved.getBoolean("mayHaveReceiverCopies"))
            }
            f.request(request)
        }
        assertTrue(checked); assertEquals(PhoneBatchSendingPhase.COMPLETED,f.row().phase)
        assertEquals(before.hash,f.source.hash); assertEquals(before.bytes,f.source.bytes); assertEquals(before.preparedFiles,f.source.preparedFiles)
    }}
    @Test fun purgingEveryJobRetainsCopyReviewAcrossPrivateSourceRestart()=runBlocking {fixture {f->
        val id=f.queue(); f.send(id); val originalHeader=f.row().header; val received=f.receivedRoot(); val bytes=f.receiver.read("simu.txt",received)
        purge(f,id); assertTrue(f.sender.state.value.isEmpty()); f.reopenPrivateSources()
        assertTrue(f.source.mayHaveReceiverCopies); val calls=f.calls
        assertThrows(IllegalStateException::class.java) {f.sender.queue(f.source.id)}; assertEquals(calls,f.calls)
        val next=f.sender.queue(f.source.id,anotherCopy=true); assertTrue(f.row().header.isEmpty()); f.send(next)
        assertEquals(PhoneBatchSendingPhase.COMPLETED,f.row().phase); assertNotEquals(originalHeader,f.row().header); assertEquals(2,f.begins)
        assertArrayEquals(bytes,f.receiver.read("simu.txt",received))
    }}
    @Test fun lossOfWholeSenderStoreCannotSilentlyQueueAnotherCopy()=runBlocking {fixture {f->
        val id=f.queue(); f.send(id); val received=f.receivedRoot(); val bytes=f.receiver.read("simu.txt",received)
        f.reopenPrivateSources {
            val jobs=File(f.root,"outgoing-batch-jobs"); check(jobs.canonicalFile.parentFile==f.root.canonicalFile)
            val files=jobs.listFiles().orEmpty(); check(files.all {it.isFile && !java.nio.file.Files.isSymbolicLink(it.toPath())})
            files.forEach {check(it.delete())}; check(jobs.delete())
        }
        assertTrue(f.sender.state.value.isEmpty()); assertTrue(f.source.mayHaveReceiverCopies)
        assertThrows(IllegalStateException::class.java) {f.sender.queue(f.source.id)}; assertEquals(1,f.begins)
        assertTrue(f.sender.queue(f.source.id,anotherCopy=true).isNotEmpty()); assertArrayEquals(bytes,f.receiver.read("simu.txt",received))
    }}
    @Test fun retainedHeaderUpgradesLegacySourceBeforeHistoryIsForgotten()=runBlocking {fixture {f->
        val id=f.queue(); f.send(id); val calls=f.calls
        f.reopenPrivateSources {
            val path=sourceRecord(f); val old=JSONObject(path.readText()).put("schema",1); old.remove("mayHaveReceiverCopies"); path.writeText(old.toString())
        }
        assertTrue(f.sources.recoveryIssues().isEmpty()); assertTrue(f.sender.recoveryIssues().isEmpty()); assertEquals(calls,f.calls)
        assertTrue(f.source.mayHaveReceiverCopies); assertEquals(2,JSONObject(sourceRecord(f).readText()).getInt("schema"))
        purge(f,id); f.reopenPrivateSources(); assertThrows(IllegalStateException::class.java) {f.sender.queue(f.source.id)}
    }}
    @Test fun missingOrWronglyTypedSchemaTwoFlagBlocksWithoutAnyRequest()=runBlocking {
        for(missing in listOf(true,false)) fixture {f->
            val id=f.queue(); val original=f.source; val manifest=f.sources.manifest(original.id); val index=manifest.itemsList.indexOfFirst {it.name=="simu.txt"}
            val part=File(f.root,"outgoing-batch-copies/${original.id}/$index.bin"); val bytes=part.readBytes(); val calls=f.calls
            f.reopenPrivateSources {
                val path=sourceRecord(f); val saved=JSONObject(path.readText()); if(missing) saved.remove("mayHaveReceiverCopies") else saved.put("mayHaveReceiverCopies","false")
                path.writeText(saved.toString())
            }
            assertTrue(f.sources.recoveryIssues().isNotEmpty()); assertTrue(f.sources.state.value.isEmpty())
            f.sender.send(id,f.target,{true}) {f.request(it)}.join(); f.sender.whenIdle()
            assertEquals(calls,f.calls); assertEquals(0,f.begins); assertEquals(PhoneBatchSendingPhase.REVIEW,f.row().phase); assertArrayEquals(bytes,part.readBytes())
        }
    }
    @Test fun localStopAndPurgeWithoutAdmissionDoesNotClaimReceiverCopies()=runBlocking {fixture {f->
        val id=f.queue(); f.sender.stop(id,{true}) {f.request(it)}.join(); f.sender.whenIdle(); purge(f,id); f.reopenPrivateSources()
        assertFalse(f.source.mayHaveReceiverCopies); assertEquals(0,f.calls); assertTrue(f.sender.queue(f.source.id).isNotEmpty())
    }}
    @Test fun legacyReadySnapshotWithoutSenderHistoryNeedsConservativeCopyReview()=runBlocking {
        for(earlierOwner in listOf(false,true)) fixture {f->
            val id=f.queue(); purge(f,id)
            f.reopenPrivateSources {
                val path=sourceRecord(f); val old=JSONObject(path.readText()).put("schema",1); old.remove("mayHaveReceiverCopies")
                if(earlierOwner) old.put("owner","c".repeat(64)); path.writeText(old.toString())
            }
            assertTrue(f.sender.state.value.isEmpty()); assertTrue(f.source.mayHaveReceiverCopies); assertEquals(0,f.calls)
            if(earlierOwner) {
                assertThrows(IllegalStateException::class.java) {f.sender.queue(f.source.id,anotherCopy=true)}
                f.sources.removeReviewed(f.sources.reviewRemoval(f.source.id)); assertTrue(f.sources.state.value.isEmpty())
            } else {
                assertThrows(IllegalStateException::class.java) {f.sender.queue(f.source.id)}; assertTrue(f.sender.queue(f.source.id,anotherCopy=true).isNotEmpty())
            }
        }
    }
    @Test fun failedSourceHistoryCommitPreventsFirstAdmissionAndKeepsOwnerData()=runBlocking {fixture {f->
        val id=f.queue(); val before=f.sourceBytes("simu.txt"); val obstruction=File(sourceRecord(f).path+".new").apply {writeText("generated owner data")}
        f.send(id)
        assertEquals(0,f.begins); assertTrue(f.receiver.grants.batchPlans.all().isEmpty()); assertTrue(f.sources.recoveryIssues().isNotEmpty())
        assertEquals("generated owner data",obstruction.readText()); assertArrayEquals(before,f.sourceBytes("simu.txt"))
    }}
}
