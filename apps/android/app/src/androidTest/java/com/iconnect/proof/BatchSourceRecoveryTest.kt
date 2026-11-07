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
class BatchSourceRecoveryTest {
    private suspend fun fixture(extras:Int=0,body:suspend (BatchSenderTest.Fixture)->Unit) {
        val f=BatchSenderTest.Fixture(extras=extras); try {f.initialize(); body(f)} finally {f.close()}
    }
    private fun copies(f:BatchSenderTest.Fixture)=File(f.root,"outgoing-batch-copies")
    private fun record(f:BatchSenderTest.Fixture,id:String=f.source.id)=File(copies(f),"$id.json")
    private fun archive(f:BatchSenderTest.Fixture,id:String)=File(f.root,"outgoing-batch-source-backup-$id")
    private fun bytes(folder:File)=folder.walkTopDown().filter {it.isFile}.sumOf {it.length()}
    private suspend fun damage(f:BatchSenderTest.Fixture,missing:Boolean=false) {
        val id=f.source.id; f.reopenPrivateSources {if(missing) check(record(f,id).delete()) else record(f,id).writeText("generated unreadable snapshot ownership")}
        assertTrue(f.sources.recoveryIssues().isNotEmpty()); assertFalse(f.resources.inventoryReady)
    }
    private fun preserve(f:BatchSenderTest.Fixture)=f.sender.confirmSourceStoreReset(f.sender.reviewSourceStoreReset())
    @Test fun damagedAndMissingParentsPreserveActualSnapshotsReceiptsOriginalsAndReceivedFilesWithoutAdoptingOwnership()=runBlocking {
        for(missing in listOf(false,true)) fixture {f->
            val source=f.source; val index=f.sources.manifest(source.id).itemsList.indexOfFirst {it.name=="simu.txt"}; val privateBytes=File(copies(f),"${source.id}/$index.bin").readBytes()
            val id=f.queue(); f.send(id); val received=f.receivedRoot(); val receivedBytes=f.receiver.read("simu.txt",received); val original=f.sourceBytes("simu.txt")
            val header=f.row().header; damage(f,missing); val calls=f.calls; val review=f.sender.reviewSourceStoreReset(); val backup=f.sender.confirmSourceStoreReset(review); val saved=archive(f,backup)
            assertTrue(f.sources.state.value.isEmpty()); assertTrue(f.sources.recoveryIssues().isEmpty()); assertTrue(f.resources.inventoryReady); assertEquals(calls,f.calls); assertEquals(header,f.row().header)
            assertArrayEquals(privateBytes,File(saved,"${source.id}/$index.bin").readBytes()); assertArrayEquals(receivedBytes,f.receiver.read("simu.txt",received)); assertArrayEquals(original,f.sourceBytes("simu.txt"))
            if(missing) assertFalse(File(saved,"${source.id}.json").exists()) else assertEquals("generated unreadable snapshot ownership",File(saved,"${source.id}.json").readText())
            assertEquals(bytes(saved),f.resources.reservedBytes); assertEquals(bytes(saved),f.resources.reservedForPeer("d".repeat(64)))
            assertThrows(IllegalStateException::class.java) {f.sender.confirmSourceStoreReset(review)}
            f.reopenPrivateSources(); assertEquals(backup,f.sources.recoveryArchives().single().id); assertEquals(bytes(saved),f.resources.reservedBytes)
            assertTrue(f.sources.confirmArchiveRemoval(f.sources.reviewArchiveRemoval(backup))); assertEquals(0L,f.resources.reservedBytes)
            f.reopenPrivateSources(); val next=f.prepareAnother(); assertTrue(f.source.mayHaveReceiverCopies)
            assertThrows(IllegalStateException::class.java) {f.sender.queue(next)}; assertTrue(f.sender.queue(next,anotherCopy=true).isNotEmpty())
        }
    }
    @Test fun originalReceiverStopControlsRemainUsableAfterSourceRecoveryWithoutAnotherAdmission()=runBlocking {fixture {f->
        val id=f.queue(); var lost=false
        f.send(id) {request->val response=f.request(request); if(!lost && request.operation==FileOperation.WRITE_BATCH_FILE) {lost=true; throw IOException("Generated lost reply")}; response}
        assertTrue(lost); val header=f.row().header; damage(f); preserve(f)
        f.sender.stop(id,{true}) {f.request(it)}.join(); f.sender.whenIdle()
        assertEquals(PhoneBatchSendingPhase.STOPPED,f.row().phase); assertEquals(header,f.row().header); assertEquals(1,f.begins); assertTrue(f.sources.state.value.isEmpty()); assertEquals(1,f.sources.recoveryArchives().size)
    }}
    @Test fun healthyStoresAndLiveSenderWorkRefuseResetUntilCleanupCompletes()=runBlocking {fixture {f->
        val id=f.queue(); assertThrows(IllegalStateException::class.java) {f.sender.reviewSourceStoreReset()}; val entered=CompletableDeferred<Unit>()
        val active=f.sender.send(id,f.target,{true}) {request->if(request.operation==FileOperation.WRITE_BATCH_FILE) {entered.complete(Unit); awaitCancellation()}; f.request(request)}
        withTimeout(15_000) {entered.await()}; record(f).appendText(" generated metadata change")
        assertThrows(IllegalStateException::class.java) {f.sources.manifest(f.source.id)}; assertThrows(IllegalStateException::class.java) {f.sender.reviewSourceStoreReset()}
        f.sender.pauseAndWait(id); active.join(); assertNotNull(f.sender.reviewSourceStoreReset())
    }}
    @Test fun openVerifiedReadersPreventPreservationUntilTheirDescriptorsClose()=runBlocking {fixture {f->
        val index=f.sources.manifest(f.source.id).itemsList.indexOfFirst {it.name=="simu.txt"}; val expected=f.sourceBytes("simu.txt")
        val reader=f.sources.openFile(f.source.id,index) {true}
        try {record(f).appendText(" generated metadata change"); assertThrows(IllegalStateException::class.java) {f.sources.manifest(f.source.id)}; assertThrows(IllegalStateException::class.java) {f.sender.reviewSourceStoreReset()}}
        finally {reader.close()}
        val backup=preserve(f); assertArrayEquals(expected,File(archive(f,backup),"${f.source.id}/$index.bin").readBytes())
    }}
    @Test fun forgedStaleAndChangedOwnerReviewsCannotMoveAnySourceStore()=runBlocking {fixture {f->
        damage(f); var review=f.sender.reviewSourceStoreReset(); assertThrows(IllegalStateException::class.java) {f.sender.confirmSourceStoreReset(review.copy(bytes=review.bytes+1))}
        review=f.sender.reviewSourceStoreReset(); record(f).appendText(" owner change"); assertThrows(IllegalStateException::class.java) {f.sender.confirmSourceStoreReset(review)}
        review=f.sender.reviewSourceStoreReset(); f.policy.reset(); assertThrows(IllegalStateException::class.java) {f.sender.confirmSourceStoreReset(review)}; assertTrue(record(f).exists()); assertTrue(f.sources.recoveryArchives().isEmpty())
    }}
    @Test fun boundedRemovalKeepsRemainingBytesReservedAndRequiresFreshReviewAfterRestart()=runBlocking {fixture(extras=40) {f->
        damage(f); val backup=preserve(f); val saved=archive(f,backup); val review=f.sources.reviewArchiveRemoval(backup)
        assertTrue(review.files>64); assertEquals(50,f.sources.archiveEntries(backup,0).size); assertTrue(f.sources.archiveEntries(backup,50).isNotEmpty())
        assertFalse(f.sources.confirmArchiveRemoval(review)); assertEquals(64,f.sources.recoveryArchives().single().removed); assertEquals(bytes(saved),f.resources.reservedBytes)
        f.reopenPrivateSources(); assertFalse(f.sources.recoveryArchives().single().removing); assertEquals(bytes(saved),f.resources.reservedBytes)
        assertThrows(IllegalStateException::class.java) {f.sources.continueArchiveRemoval(backup)}
        var done=f.sources.confirmArchiveRemoval(f.sources.reviewArchiveRemoval(backup)); while(!done) done=f.sources.continueArchiveRemoval(backup)
        assertTrue(f.sources.recoveryArchives().isEmpty()); assertEquals(0L,f.resources.reservedBytes); assertTrue(f.sourceBytes("simu.txt").isNotEmpty())
    }}
    @Test fun changedAndNewArchiveEntriesStayForFreshReviewWhileCurrentSnapshotsRemainAvailable()=runBlocking {fixture(extras=40) {f->
        damage(f); val backup=preserve(f); val saved=archive(f,backup); val current=f.prepareAnother(); assertFalse(f.sources.confirmArchiveRemoval(f.sources.reviewArchiveRemoval(backup)))
        val remaining=saved.walkTopDown().first {it.isFile}; remaining.appendText(" generated owner change")
        assertThrows(IllegalStateException::class.java) {f.sources.continueArchiveRemoval(backup)}; assertTrue(remaining.exists())
        val review=f.sources.reviewArchiveRemoval(backup); File(saved,"owner-added.txt").writeText("keep")
        assertThrows(IllegalStateException::class.java) {f.sources.confirmArchiveRemoval(review)}; assertEquals("keep",File(saved,"owner-added.txt").readText())
        var done=f.sources.confirmArchiveRemoval(f.sources.reviewArchiveRemoval(backup)); while(!done) done=f.sources.continueArchiveRemoval(backup)
        assertEquals(current,f.sources.state.value.single().id); assertTrue(f.sources.manifest(current).itemsCount>0); assertTrue(f.resources.reservedBytes>0)
    }}
    @Test fun peerRevocationAllowsLocalRecoveryButOwnerResetEndsOldArchiveDeletion()=runBlocking {fixture(extras=40) {f->
        damage(f); f.policy.blockPeer(f.approval); val backup=preserve(f); assertFalse(f.sources.confirmArchiveRemoval(f.sources.reviewArchiveRemoval(backup)))
        f.policy.reset(); assertThrows(IllegalStateException::class.java) {f.sources.continueArchiveRemoval(backup)}; assertFalse(f.sources.recoveryArchives().single().removing)
        var done=f.sources.confirmArchiveRemoval(f.sources.reviewArchiveRemoval(backup)); while(!done) done=f.sources.continueArchiveRemoval(backup)
        assertTrue(f.sources.recoveryArchives().isEmpty())
    }}
    @Test fun foregroundWithdrawalStopsRemovalAndRetainsItsExactCurrentProcessAuthorization()=runBlocking {fixture(extras=40) {f->
        damage(f); val backup=preserve(f); assertFalse(f.sources.confirmArchiveRemoval(f.sources.reviewArchiveRemoval(backup))); val before=f.sources.recoveryArchives().single()
        assertThrows(CancellationException::class.java) {f.sources.continueArchiveRemoval(backup) {false}}; assertEquals(before,f.sources.recoveryArchives().single()); assertEquals(bytes(archive(f,backup)),f.resources.reservedBytes)
        var done=false; while(!done) done=f.sources.continueArchiveRemoval(backup); assertTrue(f.sources.recoveryArchives().isEmpty())
    }}
    @Test fun unknownRegularFilesIncludingChildOwnerLockAreCountedAndPreservedButNestedFoldersAndAliasesAreRefused()=runBlocking {fixture {f->
        damage(f); File(copies(f),"owner-added.txt").writeText("keep"); File(copies(f),"${f.source.id}/owner.lock").writeText("generated owner child")
        val review=f.sender.reviewSourceStoreReset(); assertEquals(2,review.unrecognizedEntries); assertEquals(copies(f).walkTopDown().count {it.isFile}-1,review.files)
        val backup=f.sender.confirmSourceStoreReset(review); assertEquals("generated owner child",File(archive(f,backup),"${f.source.id}/owner.lock").readText())
        f.prepareAnother(); damage(f); val nested=File(copies(f),"${f.source.id}/nested").apply {check(mkdir())}
        assertThrows(IllegalStateException::class.java) {f.sender.reviewSourceStoreReset()}; check(nested.delete())
        val outside=File(f.root,"generated-owner-file").apply {writeText("outside")}; Os.symlink(outside.path,File(copies(f),"owner-alias.txt").path)
        assertThrows(IllegalStateException::class.java) {f.sender.reviewSourceStoreReset()}; assertEquals("outside",outside.readText())
    }}
    @Test fun restartAfterPreservationBeforeFreshDirectoryCreationReloadsReservedBytesAndCopyHistory()=runBlocking {fixture {f->
        damage(f); val backup=preserve(f); val reserved=f.resources.reservedBytes
        f.reopenPrivateSources {val folder=copies(f); check(folder.canonicalFile.parentFile==f.root.canonicalFile); check(folder.listFiles()!!.single().name=="owner.lock"); check(File(folder,"owner.lock").delete()); check(folder.delete())}
        assertTrue(f.sources.state.value.isEmpty()); assertTrue(f.resources.inventoryReady); assertEquals(reserved,f.resources.reservedBytes); assertEquals(backup,f.sources.recoveryArchives().single().id)
        assertThrows(Exception::class.java) {AndroidBatchSources(f.context,f.policy)}; f.prepareAnother(); assertTrue(f.source.mayHaveReceiverCopies)
    }}
    @Test fun archiveAdmissionIsBoundedAndExplicitRemovalReleasesOneSlot()=runBlocking {fixture {f->
        repeat(8) {if(it>0) f.prepareAnother(); damage(f); preserve(f)}
        f.prepareAnother(); damage(f); assertEquals(8,f.sources.recoveryArchives().size); assertThrows(IllegalStateException::class.java) {f.sender.reviewSourceStoreReset()}
        val backup=f.sources.recoveryArchives().first().id; assertTrue(f.sources.confirmArchiveRemoval(f.sources.reviewArchiveRemoval(backup))); preserve(f); assertEquals(8,f.sources.recoveryArchives().size)
    }}
    @Test fun damagedManifestsMissingReceiptsAndPayloadsAndUncommittedParentsPreserveTheActualRemainder()=runBlocking {
        for(kind in listOf("manifest","receipt","payload","uncommitted")) fixture {f->
            val id=f.source.id; val index=f.sources.manifest(id).itemsList.indexOfFirst {it.name=="simu.txt"}; val original=f.sourceBytes("simu.txt")
            f.reopenPrivateSources {val folder=File(copies(f),id); when(kind) {
                "manifest"->File(folder,"manifest.bin").writeText("generated damaged manifest")
                "receipt"->check(File(folder,"$index.json").delete())
                "payload"->check(File(folder,"$index.bin").delete())
                else->File(copies(f),"$id.json.new").writeText("generated uncommitted parent")
            }}
            assertTrue(f.sources.recoveryIssues().isNotEmpty()); val backup=preserve(f); val saved=archive(f,backup)
            assertTrue(f.sources.state.value.isEmpty()); assertTrue(f.resources.inventoryReady); assertEquals(bytes(saved),f.resources.reservedBytes); assertArrayEquals(original,f.sourceBytes("simu.txt"))
            if(kind=="manifest") assertEquals("generated damaged manifest",File(saved,"$id/manifest.bin").readText())
            if(kind=="receipt") assertFalse(File(saved,"$id/$index.json").exists())
            if(kind=="payload") assertFalse(File(saved,"$id/$index.bin").exists())
            if(kind=="uncommitted") assertEquals("generated uncommitted parent",File(saved,"$id.json.new").readText())
        }
    }
    @Test fun restoringAValidOlderSourceStoreAloneKeepsIndependentRecoveryCopyReview()=runBlocking {fixture {f->
        val job=f.queue(); val source=f.source; assertFalse(source.mayHaveReceiverCopies); val validRecord=record(f).readText()
        val index=f.sources.manifest(source.id).itemsList.indexOfFirst {it.name=="simu.txt"}; val original=f.sourceBytes("simu.txt")
        damage(f); val backup=preserve(f); val saved=archive(f,backup)
        f.reopenPrivateSources {
            val fresh=copies(f); check(fresh.canonicalFile.parentFile==f.root.canonicalFile); assertEquals("owner.lock",fresh.listFiles()!!.single().name)
            check(File(fresh,"owner.lock").delete()); check(fresh.delete()); File(saved,"${source.id}.json").writeText(validRecord)
            check(saved.canonicalFile.parentFile==f.root.canonicalFile && saved.renameTo(fresh)); check(f.record(job).delete())
        }
        assertTrue(f.sources.recoveryIssues().isEmpty()); assertTrue(f.sources.recoveryArchives().isEmpty()); assertTrue(f.sender.state.value.isEmpty())
        assertTrue(f.source.mayHaveReceiverCopies); assertThrows(IllegalStateException::class.java) {f.sender.queue(source.id)}
        assertTrue(f.sender.queue(source.id,anotherCopy=true).isNotEmpty()); assertArrayEquals(original,File(copies(f),"${source.id}/$index.bin").readBytes())
    }}
}
