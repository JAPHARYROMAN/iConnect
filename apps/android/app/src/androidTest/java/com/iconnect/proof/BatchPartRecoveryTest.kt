package com.iconnect.proof

import android.system.Os
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.iconnect.protocol.*
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

@RunWith(AndroidJUnit4::class)
class BatchPartRecoveryTest {
    companion object {
        internal fun removeReviewed(plans:AndroidBatchPlans) {
            var done=plans.confirmOrphanPartRemoval(plans.reviewOrphanPartRemoval()); while(!done) done=plans.continueOrphanPartRemoval()
        }
    }
    private fun directory(f:BatchSenderTest.Fixture)=File(f.receiver.root,"incoming-batch-parts")
    private fun parts(f:BatchSenderTest.Fixture)=directory(f).listFiles().orEmpty().filter {it.name!="owner.lock"}
    private fun extra(f:BatchSenderTest.Fixture)=File(directory(f),StorageRules.token()+".part").apply {writeBytes(byteArrayOf(11,17,23))}
    private suspend fun partial(body:suspend (BatchSenderTest.Fixture,File)->Unit) {
        val f=BatchSenderTest.Fixture()
        try {
            f.initialize(); val id=f.queue(); var lost=false
            f.send(id) {request->val response=f.request(request); if(!lost && request.operation==FileOperation.WRITE_BATCH_FILE) {lost=true; throw java.io.IOException("Generated lost accepted range reply")}; response}
            assertTrue(lost); body(f,parts(f).single())
        } finally {f.close()}
    }
    @Test fun currentReceiptsProtectActualPartialBytesAndOnlySeparateReviewedOrphansAreRemoved()=runBlocking {partial {f,owned->
        val plans=f.receiver.grants.batchPlans; val bytes=owned.readBytes(); val destinations=f.receiver.children(); val unknown=extra(f)
        assertEquals(1,plans.orphanParts()); val review=plans.reviewOrphanPartRemoval(); assertEquals(1,review.files); assertEquals(3L,review.bytes)
        assertEquals(unknown.name,plans.inspectOrphanParts(0).single().name); assertTrue(plans.confirmOrphanPartRemoval(review))
        assertFalse(unknown.exists()); assertArrayEquals(bytes,owned.readBytes()); assertEquals(destinations,f.receiver.children()); assertEquals(0,plans.orphanParts())
        assertThrows(IllegalStateException::class.java) {plans.reviewOrphanPartRemoval()}
    }}
    @Test fun archivedActualPartialRequiresSeparateReviewAndReplayCannotRemoveAnythingElse()=runBlocking {partial {f,part->
        val plans=f.receiver.grants.batchPlans; val expected=part.readBytes(); val before=f.receiver.children(); plans.archiveHistory()
        val review=plans.reviewOrphanPartRemoval(); assertArrayEquals(expected,part.readBytes()); assertEquals(1,review.files); assertEquals(part.length(),review.bytes)
        assertTrue(plans.confirmOrphanPartRemoval(review)); assertFalse(part.exists()); assertEquals(before,f.receiver.children()); assertEquals(0,plans.orphanParts())
        assertThrows(IllegalStateException::class.java) {plans.confirmOrphanPartRemoval(review)}; assertEquals(1,plans.preservedRecords())
    }}
    @Test fun staleAndForgedReviewsKeepAllActualFilesAndAnOwnerChangeInvalidatesConfirmation()=runBlocking {partial {f,part->
        val plans=f.receiver.grants.batchPlans; plans.archiveHistory(); var review=plans.reviewOrphanPartRemoval()
        assertThrows(IllegalStateException::class.java) {plans.confirmOrphanPartRemoval(review.copy(bytes=review.bytes+1))}
        review=plans.reviewOrphanPartRemoval(); part.appendText(" changed")
        assertThrows(IllegalStateException::class.java) {plans.confirmOrphanPartRemoval(review)}; assertTrue(part.exists())
        review=plans.reviewOrphanPartRemoval(); f.receiver.policy.reset()
        assertThrows(IllegalStateException::class.java) {plans.confirmOrphanPartRemoval(review)}; assertTrue(part.exists())
    }}
    @Test fun boundedRemovalAndRestartKeepRemainingPartsAndRequireFreshReview()=runBlocking {partial {f,_->
        var plans=f.receiver.grants.batchPlans; plans.archiveHistory(); repeat(65) {extra(f)}
        val review=plans.reviewOrphanPartRemoval(); assertEquals(66,review.files); assertEquals(50,plans.inspectOrphanParts(0).size); assertEquals(16,plans.inspectOrphanParts(50).size)
        assertFalse(plans.confirmOrphanPartRemoval(review)); assertEquals(2,parts(f).size); assertEquals(64,plans.orphanRemovalProgress()!!.removed)
        f.receiver.restart(reopenGrants=true); plans=f.receiver.grants.batchPlans; assertNull(plans.orphanRemovalProgress()); assertEquals(2,plans.orphanParts())
        assertThrows(IllegalStateException::class.java) {plans.continueOrphanPartRemoval()}; removeReviewed(plans); assertTrue(parts(f).isEmpty())
    }}
    @Test fun newPartsAreNeverAddedToAnExistingRemovalAuthorization()=runBlocking {partial {f,_->
        val plans=f.receiver.grants.batchPlans; plans.archiveHistory(); repeat(65) {extra(f)}; assertFalse(plans.confirmOrphanPartRemoval(plans.reviewOrphanPartRemoval()))
        val added=extra(f); assertTrue(plans.continueOrphanPartRemoval()); assertTrue(added.exists()); assertEquals(added.name,parts(f).single().name)
        assertNull(plans.orphanRemovalProgress()); assertEquals(1,plans.orphanParts()); removeReviewed(plans); assertFalse(added.exists())
    }}
    @Test fun changedRemainingPartStopsCleanupAndKeepsItsBytesForFreshReview()=runBlocking {partial {f,_->
        val plans=f.receiver.grants.batchPlans; plans.archiveHistory(); repeat(65) {extra(f)}; assertFalse(plans.confirmOrphanPartRemoval(plans.reviewOrphanPartRemoval()))
        val changed=parts(f).sortedBy {it.name}.first(); changed.appendText(" owner data")
        assertThrows(IllegalStateException::class.java) {plans.continueOrphanPartRemoval()}; assertTrue(changed.readText().endsWith(" owner data")); assertNull(plans.orphanRemovalProgress())
        removeReviewed(plans); assertTrue(parts(f).isEmpty())
    }}
    @Test fun peerRevocationAllowsLocalCleanupAndOwnerResetRequiresFreshRemainingReview()=runBlocking {partial {f,_->
        val plans=f.receiver.grants.batchPlans; plans.archiveHistory(); repeat(65) {extra(f)}; f.receiver.policy.blockPeer(f.receiver.approval)
        assertFalse(plans.confirmOrphanPartRemoval(plans.reviewOrphanPartRemoval())); f.receiver.policy.reset()
        assertThrows(IllegalStateException::class.java) {plans.continueOrphanPartRemoval()}; assertNull(plans.orphanRemovalProgress()); removeReviewed(plans); assertTrue(parts(f).isEmpty())
    }}
    @Test fun receiverContextResetEndsOldReviewsAndDeletionPermission()=runBlocking {partial {f,_->
        val plans=f.receiver.grants.batchPlans; plans.archiveHistory(); var review=plans.reviewOrphanPartRemoval(); plans.archiveHistory()
        assertThrows(IllegalStateException::class.java) {plans.confirmOrphanPartRemoval(review)}
        repeat(65) {extra(f)}; review=plans.reviewOrphanPartRemoval(); assertFalse(plans.confirmOrphanPartRemoval(review)); plans.archiveHistory()
        assertNull(plans.orphanRemovalProgress()); assertThrows(IllegalStateException::class.java) {plans.continueOrphanPartRemoval()}; removeReviewed(plans)
    }}
    @Test fun foregroundWithdrawalAfterSomeDeletesKeepsExactProgressAndResumesOnlyItsReviewedFiles()=runBlocking {partial {f,_->
        val plans=f.receiver.grants.batchPlans; plans.archiveHistory(); repeat(5) {extra(f)}; val review=plans.reviewOrphanPartRemoval(); var calls=0
        assertThrows(CancellationException::class.java) {plans.confirmOrphanPartRemoval(review) {++calls<=3}}
        val progress=plans.orphanRemovalProgress()!!; assertTrue(progress.removed in 1 until review.files); assertEquals(review.files-progress.removed,parts(f).size)
        assertTrue(plans.continueOrphanPartRemoval()); assertTrue(parts(f).isEmpty())
    }}
    @Test fun changedJournalInventoryBlocksCleanupUntilExplicitMetadataRecovery()=runBlocking {partial {f,owned->
        val plans=f.receiver.grants.batchPlans; val unknown=extra(f); val review=plans.reviewOrphanPartRemoval()
        val journal=File(f.receiver.root,"incoming-batch-plans").listFiles()!!.single {it.name.endsWith(".upload.json")}; journal.appendText(" generated damage")
        assertThrows(StorageFailure::class.java) {plans.confirmOrphanPartRemoval(review)}; assertTrue(unknown.exists()); assertTrue(owned.exists()); assertTrue(plans.recoveryIssues().isNotEmpty())
        plans.archiveHistory(); assertEquals(2,plans.orphanParts()); removeReviewed(plans); assertTrue(parts(f).isEmpty())
    }}
    @Test fun symbolicLinksAndNestedFoldersAreKeptWithoutDeletingTheirTargets()=runBlocking {partial {f,_->
        val plans=f.receiver.grants.batchPlans; plans.archiveHistory(); val nested=File(directory(f),"owner-folder").apply {check(mkdir())}
        assertThrows(IllegalStateException::class.java) {plans.reviewOrphanPartRemoval()}; check(nested.delete())
        val outside=File(f.receiver.root,"generated-owner-file").apply {writeText("keep")}; Os.symlink(outside.path,File(directory(f),StorageRules.token()+".part").path)
        assertThrows(IllegalStateException::class.java) {plans.reviewOrphanPartRemoval()}; assertEquals("keep",outside.readText())
    }}
    @Test fun aPartThatBecomesReferencedIsKeptEvenAfterRemovalWasReviewed()=runBlocking {partial {f,_->
        val files=(0 until 65).map {extra(f)}; val owned=mutableSetOf<String>(); val manager=AndroidReceivingPartRecovery(f.receiver.policy,{"fixture-context"},
            {files.filter {it.exists() && it.name !in owned}},{it !in owned},{})
        assertFalse(manager.confirm(manager.review {true}) {true}); val remaining=files.single {it.exists()}; owned.add(remaining.name)
        assertThrows(IllegalStateException::class.java) {manager.continueRemoval {true}}; assertTrue(remaining.exists())
    }}
}
