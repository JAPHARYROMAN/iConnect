package com.iconnect.proof

import android.system.Os
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.iconnect.protocol.*
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.io.IOException

@RunWith(AndroidJUnit4::class)
class BatchSenderRecoveryTest {
    private suspend fun fixture(extras:Int=0,body:suspend (BatchSenderTest.Fixture)->Unit) {
        val f=BatchSenderTest.Fixture(extras=extras); try {f.initialize(); body(f)} finally {f.close()}
    }
    private suspend fun damage(f:BatchSenderTest.Fixture,id:String,missing:Boolean=false) {
        f.sender.shutdown(); if(missing) check(f.record(id).delete()) else f.record(id).writeText("generated damaged sender intent")
        f.reopen(); assertTrue(f.sender.recoveryIssues().isNotEmpty())
    }
    private fun archive(f:BatchSenderTest.Fixture,id:String)=File(f.root,"outgoing-batch-sender-backup-$id")
    @Test fun damagedAndMissingParentsPreserveActualReceivedBytesAndIndependentPrivateSnapshots()=runBlocking {
        for(missing in listOf(false,true)) fixture {f->
            val id=f.queue(); f.send(id); val received=f.receivedRoot(); val bytes=f.receiver.read("simu.txt",received); val original=f.sourceBytes("simu.txt")
            damage(f,id,missing); val review=f.sender.reviewStoreReset(); val calls=f.calls
            val backup=f.sender.confirmStoreReset(review); val folder=archive(f,backup)
            assertTrue(f.sender.recoveryIssues().isEmpty()); assertTrue(f.sender.state.value.isEmpty()); assertEquals(calls,f.calls)
            if(missing) assertFalse(File(folder,"$id.json").exists()) else assertEquals("generated damaged sender intent",File(folder,"$id.json").readText())
            assertTrue(File(folder,"$id.0.json").isFile); assertTrue(f.sources.state.value.single().mayHaveReceiverCopies)
            assertThrows(IllegalStateException::class.java) {f.sender.queue(f.source.id)}
            assertArrayEquals(bytes,f.receiver.read("simu.txt",received)); assertArrayEquals(original,f.sourceBytes("simu.txt"))
            assertThrows(IllegalStateException::class.java) {f.sender.confirmStoreReset(review)}
            f.reopen(); assertEquals(backup,f.sender.recoveryArchives().single().id); assertTrue(f.sender.queue(f.source.id,anotherCopy=true).isNotEmpty())
        }
    }
    @Test fun recoveryConservativelyMarksAnUnadmittedSnapshotWithoutAnyReceiverRequest()=runBlocking {fixture {f->
        val id=f.queue(); val before=f.source; assertFalse(before.mayHaveReceiverCopies); damage(f,id)
        f.sender.confirmStoreReset(f.sender.reviewStoreReset()); val after=f.sources.state.value.single()
        assertTrue(after.mayHaveReceiverCopies); assertEquals(before.owner,after.owner); assertEquals(before.peer,after.peer); assertEquals(before.hash,after.hash)
        assertThrows(IllegalStateException::class.java) {f.sender.queue(before.id)}; assertEquals(0,f.calls)
    }}
    @Test fun changedAndForgedReviewsCannotMoveMetadataAndPolicyResetInvalidatesTheOwnerReview()=runBlocking {fixture {f->
        val id=f.queue(); damage(f,id); var review=f.sender.reviewStoreReset()
        assertThrows(IllegalStateException::class.java) {f.sender.confirmStoreReset(review.copy(files=review.files+1))}
        review=f.sender.reviewStoreReset(); f.record(id).appendText(" changed")
        assertThrows(IllegalStateException::class.java) {f.sender.confirmStoreReset(review)}; assertTrue(f.sender.recoveryArchives().isEmpty())
        review=f.sender.reviewStoreReset(); f.policy.reset()
        assertThrows(IllegalStateException::class.java) {f.sender.confirmStoreReset(review)}; assertTrue(f.record(id).exists()); assertTrue(f.sender.recoveryArchives().isEmpty())
    }}
    @Test fun revokedPeerDoesNotPreventCurrentLocalOwnerRecoveryAndArchiveRemoval()=runBlocking {fixture {f->
        val id=f.queue(); damage(f,id); f.policy.blockPeer(f.approval)
        val backup=f.sender.confirmStoreReset(f.sender.reviewStoreReset()); assertEquals(backup,f.sender.recoveryArchives().single().id)
        assertTrue(f.sender.confirmArchiveRemoval(f.sender.reviewArchiveRemoval(backup))); assertTrue(f.sender.recoveryArchives().isEmpty())
        assertEquals(0,f.calls); assertTrue(f.sources.state.value.isNotEmpty())
    }}
    @Test fun unknownRegularFilesArePreservedAndOnlyExplicitReviewedRemovalDeletesThem()=runBlocking {fixture {f->
        val id=f.queue(); f.sender.shutdown(); File(f.root,"outgoing-batch-jobs/owner-added.txt").writeText("generated owner data"); f.reopen()
        val review=f.sender.reviewStoreReset(); assertEquals(1,review.unrecognizedFiles)
        val backup=f.sender.confirmStoreReset(review); val file=File(archive(f,backup),"owner-added.txt")
        val removal=f.sender.reviewArchiveRemoval(backup); assertEquals(1,removal.unrecognizedFiles); assertEquals("generated owner data",file.readText())
        assertTrue(f.sender.confirmArchiveRemoval(removal)); assertFalse(file.exists()); assertTrue(f.sources.state.value.isNotEmpty()); assertEquals(0,f.calls)
    }}
    @Test fun boundedRemovalRequiresFreshReviewOfRemainingFilesAfterRestart()=runBlocking {fixture(extras=63) {f->
        val id=f.queue(); f.send(id); val received=f.receivedRoot(); val bytes=f.receiver.read("simu.txt",received); damage(f,id)
        val backup=f.sender.confirmStoreReset(f.sender.reviewStoreReset()); val folder=archive(f,backup); val review=f.sender.reviewArchiveRemoval(backup)
        assertTrue(review.files>64); assertFalse(f.sender.confirmArchiveRemoval(review)); assertEquals(review.files-64,folder.listFiles()!!.size)
        assertEquals(64,f.sender.recoveryArchives().single().removed); f.reopen(); assertFalse(f.sender.recoveryArchives().single().removing)
        assertThrows(IllegalStateException::class.java) {f.sender.continueArchiveRemoval(backup)}
        val remaining=f.sender.reviewArchiveRemoval(backup); assertEquals(review.files-64,remaining.files)
        var done=f.sender.confirmArchiveRemoval(remaining); while(!done) done=f.sender.continueArchiveRemoval(backup)
        assertFalse(folder.exists()); assertTrue(f.sender.recoveryArchives().isEmpty()); assertArrayEquals(bytes,f.receiver.read("simu.txt",received))
    }}
    @Test fun changedAndNewArchiveEntriesRemainForFreshReview()=runBlocking {fixture(extras=63) {f->
        val id=f.queue(); f.send(id); damage(f,id); val backup=f.sender.confirmStoreReset(f.sender.reviewStoreReset()); val folder=archive(f,backup)
        assertFalse(f.sender.confirmArchiveRemoval(f.sender.reviewArchiveRemoval(backup))); val remaining=folder.listFiles()!!.first(); remaining.appendText(" owner change")
        assertThrows(IllegalStateException::class.java) {f.sender.continueArchiveRemoval(backup)}; assertTrue(remaining.exists())
        val review=f.sender.reviewArchiveRemoval(backup); File(folder,"new-owner-file.txt").writeText("keep")
        assertThrows(IllegalStateException::class.java) {f.sender.confirmArchiveRemoval(review)}; assertEquals("keep",File(folder,"new-owner-file.txt").readText())
    }}
    @Test fun liveWorkAndHealthyStoresCannotResetAndTheExternalLeaseProtectsTheWholeProfile()=runBlocking {fixture {f->
        val id=f.queue(); assertThrows(IllegalStateException::class.java) {f.sender.reviewStoreReset()}
        assertThrows(Exception::class.java) {PhoneBatchSender(f.context,f.policy,f.sources)}
        val entered=CompletableDeferred<Unit>()
        val sending=f.sender.send(id,f.target,{true}) {request->if(request.operation==FileOperation.WRITE_BATCH_FILE) {entered.complete(Unit); awaitCancellation()}; f.request(request)}
        withTimeout(15_000) {entered.await()}; f.record(id).appendText(" owner changed running metadata")
        assertThrows(IOException::class.java) {f.sender.queue(f.source.id)}; assertTrue(f.sender.recoveryIssues().isNotEmpty())
        assertThrows(IllegalStateException::class.java) {f.sender.reviewStoreReset()}; f.sender.pauseAndWait(id); sending.join()
        assertNotNull(f.sender.reviewStoreReset())
    }}
    @Test fun archiveRemovalKeepsANewCurrentJobAndPrivateSnapshot()=runBlocking {fixture {f->
        val id=f.queue(); damage(f,id); val backup=f.sender.confirmStoreReset(f.sender.reviewStoreReset()); val current=f.sender.queue(f.source.id,anotherCopy=true)
        assertTrue(f.sender.confirmArchiveRemoval(f.sender.reviewArchiveRemoval(backup))); assertEquals(current,f.row().id); assertTrue(f.sources.state.value.isNotEmpty())
        f.reopen(); assertEquals(current,f.row().id); assertTrue(f.sender.recoveryIssues().isEmpty())
    }}
    @Test fun archiveAdmissionIsBoundedAndExplicitRemovalReleasesOneSlot()=runBlocking {fixture {f->
        var id=f.queue(); repeat(8) {damage(f,id); f.sender.confirmStoreReset(f.sender.reviewStoreReset()); id=f.sender.queue(f.source.id,anotherCopy=true)}
        damage(f,id); assertEquals(8,f.sender.recoveryArchives().size); assertThrows(IllegalStateException::class.java) {f.sender.reviewStoreReset()}
        val backup=f.sender.recoveryArchives().first().id; assertTrue(f.sender.confirmArchiveRemoval(f.sender.reviewArchiveRemoval(backup)))
        f.sender.confirmStoreReset(f.sender.reviewStoreReset()); assertEquals(8,f.sender.recoveryArchives().size)
    }}
    @Test fun foregroundWithdrawalStopsReviewedDeletionAndKeepsItsExactRemainingAuthorization()=runBlocking {fixture(extras=63) {f->
        val id=f.queue(); f.send(id); damage(f,id); val backup=f.sender.confirmStoreReset(f.sender.reviewStoreReset())
        assertFalse(f.sender.confirmArchiveRemoval(f.sender.reviewArchiveRemoval(backup))); val before=f.sender.recoveryArchives().single()
        assertThrows(CancellationException::class.java) {f.sender.continueArchiveRemoval(backup) {false}}
        assertEquals(before,f.sender.recoveryArchives().single()); var done=false; while(!done) done=f.sender.continueArchiveRemoval(backup)
        assertTrue(f.sender.recoveryArchives().isEmpty())
    }}
    @Test fun policyResetEndsDeletionAndRequiresFreshReview()=runBlocking {fixture(extras=63) {f->
        val id=f.queue(); f.send(id); damage(f,id); val backup=f.sender.confirmStoreReset(f.sender.reviewStoreReset())
        assertFalse(f.sender.confirmArchiveRemoval(f.sender.reviewArchiveRemoval(backup))); f.policy.reset()
        assertThrows(IllegalStateException::class.java) {f.sender.continueArchiveRemoval(backup)}; assertFalse(f.sender.recoveryArchives().single().removing)
        var done=f.sender.confirmArchiveRemoval(f.sender.reviewArchiveRemoval(backup)); while(!done) done=f.sender.continueArchiveRemoval(backup)
        assertTrue(f.sender.recoveryArchives().isEmpty())
    }}
    @Test fun aliasesAndNestedFoldersArePreservedWithoutFollowingOrDeletingThem()=runBlocking {fixture {f->
        val id=f.queue(); f.sender.shutdown(); val jobs=File(f.root,"outgoing-batch-jobs")
        val nested=File(jobs,"owner-folder").apply {check(mkdir())}; val keep=File(nested,"keep.txt").apply {writeText("keep")}
        f.reopen(); assertThrows(IllegalStateException::class.java) {f.sender.reviewStoreReset()}; assertEquals("keep",keep.readText())
        f.sender.shutdown(); check(nested.canonicalFile.parentFile==jobs.canonicalFile); check(keep.delete()); check(nested.delete())
        val outside=File(f.root,"generated-owner-file").apply {writeText("outside sender store")}; Os.symlink(outside.path,File(jobs,"owner-alias.json").path)
        f.reopen(); assertThrows(IllegalStateException::class.java) {f.sender.reviewStoreReset()}; assertTrue(f.sender.recoveryArchives().isEmpty()); assertEquals("outside sender store",outside.readText())
    }}
    @Test fun restartAfterPreservationBeforeFreshQueueCreationRetainsArchiveAndCopyReview()=runBlocking {fixture {f->
        val id=f.queue(); damage(f,id); val backup=f.sender.confirmStoreReset(f.sender.reviewStoreReset()); f.sender.shutdown()
        val fresh=File(f.root,"outgoing-batch-jobs"); check(fresh.canonicalFile.parentFile==f.root.canonicalFile); assertEquals("owner.lock",fresh.listFiles()!!.single().name)
        check(File(fresh,"owner.lock").delete()); check(fresh.delete()); f.reopen()
        assertTrue(f.sender.recoveryIssues().isEmpty()); assertTrue(f.sender.state.value.isEmpty()); assertEquals(backup,f.sender.recoveryArchives().single().id)
        assertEquals("generated damaged sender intent",File(archive(f,backup),"$id.json").readText()); assertThrows(IllegalStateException::class.java) {f.sender.queue(f.source.id)}; assertEquals(0,f.calls)
    }}
}
