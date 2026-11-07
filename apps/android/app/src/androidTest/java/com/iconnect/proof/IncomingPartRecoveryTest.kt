package com.iconnect.proof

import android.system.Os
import androidx.test.ext.junit.runners.AndroidJUnit4
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

@RunWith(AndroidJUnit4::class)
class IncomingPartRecoveryTest {
    companion object {
        internal fun removeReviewed(incoming:AndroidIncomingUploads) {
            var done=incoming.confirmOrphanRemoval(incoming.reviewOrphanRemoval()); while(!done) done=incoming.continueOrphanRemoval()
        }
    }
    private fun directory(f:IncomingUploadsTest.Fixture)=File(f.root,"incoming-uploads")
    private fun parts(f:IncomingUploadsTest.Fixture)=directory(f).listFiles().orEmpty().filter {it.name.endsWith(".part")}
    private fun extra(f:IncomingUploadsTest.Fixture)=File(directory(f),StorageRules.token()+".part").apply {writeBytes(byteArrayOf(11,17,23))}
    private suspend fun partial(body:suspend (IncomingUploadsTest.Fixture,File)->Unit) {
        val f=IncomingUploadsTest.Fixture()
        try {f.initialize(); val bytes=byteArrayOf(2,3,5,7,11,13,17); val info=f.descriptor(bytes); f.begin(info); f.write(info,bytes); body(f,parts(f).single())}
        finally {f.close()}
    }
    @Test fun currentReceiptsProtectActualPartsAndProviderFilesDuringReviewedCleanup()=runBlocking {partial {f,current->
        val expected=current.readBytes(); val before=f.children(); val unknown=extra(f); val review=f.incoming.reviewOrphanRemoval()
        assertEquals(1,review.files); assertEquals(3L,review.bytes); assertEquals(unknown.name,f.incoming.inspectOrphans(0).single().name)
        assertTrue(f.incoming.confirmOrphanRemoval(review)); assertFalse(unknown.exists()); assertArrayEquals(expected,current.readBytes()); assertEquals(before,f.children()); assertEquals(0,f.incoming.orphanCount())
        assertThrows(IllegalStateException::class.java) {f.incoming.reviewOrphanRemoval()}
    }}
    @Test fun archivedActualPartRequiresSeparateReviewAndReplayCannotRemoveAnythingElse()=runBlocking {partial {f,part->
        val expected=part.readBytes(); val before=f.children(); f.incoming.archiveHistory(); val review=f.incoming.reviewOrphanRemoval()
        assertEquals(1,review.files); assertEquals(part.length(),review.bytes); assertArrayEquals(expected,part.readBytes())
        assertTrue(f.incoming.confirmOrphanRemoval(review)); assertFalse(part.exists()); assertEquals(before,f.children()); assertEquals(0,f.incoming.orphanCount())
        assertThrows(IllegalStateException::class.java) {f.incoming.confirmOrphanRemoval(review)}; assertEquals(1,f.incoming.preservedRecords())
    }}
    @Test fun forgedStaleAndChangedOwnerReviewsKeepPrivateBytes()=runBlocking {partial {f,part->
        f.incoming.archiveHistory(); var review=f.incoming.reviewOrphanRemoval()
        assertThrows(IllegalStateException::class.java) {f.incoming.confirmOrphanRemoval(review.copy(bytes=review.bytes+1))}
        review=f.incoming.reviewOrphanRemoval(); part.appendText(" owner change"); assertThrows(IllegalStateException::class.java) {f.incoming.confirmOrphanRemoval(review)}
        review=f.incoming.reviewOrphanRemoval(); f.policy.reset(); assertThrows(IllegalStateException::class.java) {f.incoming.confirmOrphanRemoval(review)}; assertTrue(part.exists())
    }}
    @Test fun boundedRemovalAndColdReopenRequireFreshReviewOfTheActualRemainder()=runBlocking {partial {f,_->
        f.incoming.archiveHistory(); repeat(65) {extra(f)}; val review=f.incoming.reviewOrphanRemoval(); assertEquals(66,review.files)
        assertEquals(50,f.incoming.inspectOrphans(0).size); assertEquals(16,f.incoming.inspectOrphans(50).size)
        assertFalse(f.incoming.confirmOrphanRemoval(review)); assertEquals(64,f.incoming.orphanRemovalProgress()!!.removed); assertEquals(2,parts(f).size)
        f.restart(reopenGrants=true); assertNull(f.incoming.orphanRemovalProgress()); assertEquals(2,f.incoming.orphanCount())
        assertThrows(IllegalStateException::class.java) {f.incoming.continueOrphanRemoval()}; removeReviewed(f.incoming); assertTrue(parts(f).isEmpty())
    }}
    @Test fun newPartsStayVisibleAfterTheReviewedRemainderIsRemoved()=runBlocking {partial {f,_->
        f.incoming.archiveHistory(); repeat(65) {extra(f)}; assertFalse(f.incoming.confirmOrphanRemoval(f.incoming.reviewOrphanRemoval()))
        val added=extra(f); assertTrue(f.incoming.continueOrphanRemoval()); assertTrue(added.exists()); assertEquals(1,f.incoming.orphanCount())
        assertEquals(added.name,parts(f).single().name); removeReviewed(f.incoming); assertFalse(added.exists())
    }}
    @Test fun changedRemainingPartsRequireFreshReview()=runBlocking {partial {f,_->
        f.incoming.archiveHistory(); repeat(65) {extra(f)}; assertFalse(f.incoming.confirmOrphanRemoval(f.incoming.reviewOrphanRemoval()))
        val changed=parts(f).sortedBy {it.name}.first(); changed.appendText(" owner data")
        assertThrows(IllegalStateException::class.java) {f.incoming.continueOrphanRemoval()}; assertTrue(changed.readText().endsWith(" owner data")); assertNull(f.incoming.orphanRemovalProgress())
        removeReviewed(f.incoming); assertTrue(parts(f).isEmpty())
    }}
    @Test fun peerRevocationAllowsLocalCleanupWhileOwnerResetEndsOldDeletionPermission()=runBlocking {partial {f,_->
        f.incoming.archiveHistory(); repeat(65) {extra(f)}; f.policy.blockPeer(f.approval)
        assertFalse(f.incoming.confirmOrphanRemoval(f.incoming.reviewOrphanRemoval())); f.policy.reset()
        assertThrows(IllegalStateException::class.java) {f.incoming.continueOrphanRemoval()}; assertNull(f.incoming.orphanRemovalProgress()); removeReviewed(f.incoming)
    }}
    @Test fun receiverEpochResetInvalidatesReviewsAndCurrentProcessCleanup()=runBlocking {partial {f,_->
        f.incoming.archiveHistory(); var review=f.incoming.reviewOrphanRemoval(); f.incoming.archiveHistory()
        assertThrows(IllegalStateException::class.java) {f.incoming.confirmOrphanRemoval(review)}
        repeat(65) {extra(f)}; review=f.incoming.reviewOrphanRemoval(); assertFalse(f.incoming.confirmOrphanRemoval(review)); f.incoming.archiveHistory()
        assertNull(f.incoming.orphanRemovalProgress()); assertThrows(IllegalStateException::class.java) {f.incoming.continueOrphanRemoval()}; removeReviewed(f.incoming)
    }}
    @Test fun foregroundWithdrawalKeepsExactPartialProgressAndOnlyReviewedFilesResume()=runBlocking {partial {f,_->
        f.incoming.archiveHistory(); repeat(5) {extra(f)}; val review=f.incoming.reviewOrphanRemoval(); var calls=0
        assertThrows(CancellationException::class.java) {f.incoming.confirmOrphanRemoval(review) {++calls<=3}}
        val progress=f.incoming.orphanRemovalProgress()!!; assertTrue(progress.removed in 1 until review.files); assertEquals(review.files-progress.removed,parts(f).size)
        assertTrue(f.incoming.continueOrphanRemoval()); assertTrue(parts(f).isEmpty())
    }}
    @Test fun changedJournalBlocksDeletionAndExplicitRecoveryKeepsPartBytes()=runBlocking {partial {f,current->
        val unknown=extra(f); val review=f.incoming.reviewOrphanRemoval(); val journal=File(directory(f),"journal.json"); journal.appendText(" generated damage")
        assertThrows(StorageFailure::class.java) {f.incoming.confirmOrphanRemoval(review)}; assertTrue(unknown.exists()); assertTrue(current.exists()); assertTrue(f.incoming.recoveryIssues().isNotEmpty())
        val issue=f.incoming.recoveryIssues().single {it.canArchive}; f.incoming.archiveRecovery(issue.id)
        assertTrue(f.incoming.recoveryIssues().isEmpty()); assertEquals(2,f.incoming.orphanCount()); removeReviewed(f.incoming); assertTrue(parts(f).isEmpty())
    }}
    @Test fun uncommittedJournalSidecarPreventsReviewAndIsNeverOverwritten()=runBlocking {partial {f,current->
        val sidecar=File(directory(f),"journal.json.new").apply {writeText("generated owner sidecar")}; val unknown=extra(f)
        assertThrows(StorageFailure::class.java) {f.incoming.reviewOrphanRemoval()}; assertEquals("generated owner sidecar",sidecar.readText()); assertTrue(current.exists()); assertTrue(unknown.exists())
    }}
    @Test fun symbolicLinksAndReplacedPrivateDirectoryCannotRedirectReviewedDeletion()=runBlocking {partial {f,current->
        f.incoming.archiveHistory(); val original=directory(f)
        val outside=File(f.root,"generated-owner-file").apply {writeText("keep")}; val link=File(original,StorageRules.token()+".part"); Os.symlink(outside.path,link.path)
        assertThrows(IllegalStateException::class.java) {f.incoming.reviewOrphanRemoval()}; assertEquals("keep",outside.readText()); check(link.delete())
        val review=f.incoming.reviewOrphanRemoval(); val moved=File(f.root,"generated-preserved-incoming")
        check(original.canonicalFile.parentFile==f.root.canonicalFile && moved.canonicalFile.parentFile==f.root.canonicalFile && original.renameTo(moved)); check(original.mkdir())
        val keep=File(original,current.name).apply {writeText("replacement owner data")}
        assertThrows(IllegalStateException::class.java) {f.incoming.confirmOrphanRemoval(review)}; assertEquals("replacement owner data",keep.readText()); assertTrue(File(moved,current.name).exists())
    }}
}
