package com.iconnect.proof

import android.system.Os
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.google.protobuf.ByteString
import com.iconnect.protocol.*
import kotlinx.coroutines.*
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.io.RandomAccessFile
import java.nio.ByteBuffer
import java.util.concurrent.atomic.AtomicLong

@RunWith(AndroidJUnit4::class)
class IncomingWindowRecoveryTest {
    internal class Clock {
        val wall=AtomicLong(System.currentTimeMillis()); val elapsed=AtomicLong(1000)
        fun advance(value:Long) {wall.addAndGet(value); elapsed.addAndGet(value)}
    }
    internal class Fixture(val clock:Clock=Clock()) {
        val base=IncomingUploadsTest.Fixture(windowNow={clock.wall.get()},windowElapsed={clock.elapsed.get()})
        val bytes=ByteArray(3*StorageRules.RANGE+17) {(it*31+7).toByte()}
        lateinit var descriptor:UploadInfo
        val row get()=base.incoming.state.value.single {it.id==descriptor.id}
        val journal get()=File(base.root,"incoming-uploads/journal.json")
        fun part()=File(base.root,"incoming-uploads").listFiles()!!.single {it.name.endsWith(".part")}
        suspend fun start(acknowledge:Boolean=true,data:ByteArray=bytes) {
            base.initialize(); descriptor=base.descriptor(data)
            val token=Rules.decode(descriptor.id); ByteBuffer.wrap(token).putLong(clock.wall.get()); descriptor=descriptor.toBuilder().setId(Rules.b64(token)).build()
            base.begin(descriptor)
            if(acknowledge && data.isNotEmpty()) assertEquals(FileStatus.FILE_STATUS_OK,write(data=data).status)
        }
        fun expire()=clock.advance(MutationRules.LIFETIME+3_600_000)
        suspend fun write(offset:Long=row.offset,data:ByteArray=bytes)=base.raw(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_WRITE)
            .setTransferId(descriptor.id).setMutationEpoch(base.epoch).setOffset(offset).setContent(ByteString.copyFrom(data,offset.toInt(),minOf(StorageRules.RANGE.toLong(),data.size-offset).toInt())).build())
        suspend fun review()=base.incoming.reviewWindow(descriptor.id) {base.visible.get()}
        suspend fun close()=base.close()
    }
    private suspend fun refused(action:suspend()->Unit) {var denied=false; try {action()} catch(_:Exception) {denied=true}; assertTrue("Expected retained-window refusal",denied)}
    @Test fun incomingRateUsesOnlySavedNewRangesAndClearsOnScopeEnd() = runBlocking {
        val f = Fixture()
        try {
            f.start()
            var incoming = f.base.incoming
            val range = StorageRules.RANGE.toLong()
            fun estimate() = incoming.readPayloads(0)[f.descriptor.id]

            assertEquals(PhonePayloadRateState.MEASURING, estimate()?.state)
            f.clock.advance(2000)
            assertEquals(FileStatus.FILE_STATUS_OK, f.write().status)
            assertEquals(PhonePayloadRateEstimate(PhonePayloadRateState.AVAILABLE, range / 2, 3), estimate())
            f.clock.advance(11_000)
            assertEquals(FileStatus.FILE_STATUS_OK, f.write(offset = range).status)
            assertEquals(2 * range, f.row.offset)
            assertEquals(PhonePayloadRateState.WAITING, estimate()?.state)

            f.base.service.close()
            assertNull(estimate())
            f.base.restart()
            incoming = f.base.incoming
            assertNull(estimate())
            assertEquals(FileStatus.FILE_STATUS_OK, f.write().status)
            assertEquals(PhonePayloadRateState.MEASURING, estimate()?.state)
            assertEquals(FileStatus.FILE_STATUS_OK, f.write().status)
            assertNull(estimate())
            val done = f.base.finish(f.descriptor)
            assertEquals(UploadPhase.UPLOAD_COMPLETED, done.phase)
            assertArrayEquals(f.bytes, f.base.read(done.finalName))
        } finally { f.close() }
    }
    @Test fun expiredWritesAndFinishRefuseWhileStatusAndCancellationRetainTheirExistingAuthority():Unit=runBlocking {
        val f=Fixture(); try {f.start(); f.expire(); assertEquals(FileStatus.STALE_REFERENCE,f.write().status)
            assertEquals(FileStatus.STALE_REFERENCE,f.base.raw(FileRequest.newBuilder().setOperation(FileOperation.FINISH_UPLOAD).setTransferId(f.descriptor.id).setMutationEpoch(f.base.epoch).build()).status)
            val status=f.base.call(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_STATUS).setTransferId(f.descriptor.id).setMutationEpoch(f.base.epoch).build())
            assertEquals(StorageRules.RANGE.toLong(),status.upload.offset)
            val stopped=f.base.call(FileRequest.newBuilder().setOperation(FileOperation.CANCEL_UPLOAD).setTransferId(f.descriptor.id).setMutationEpoch(f.base.epoch).build())
            assertEquals(UploadPhase.UPLOAD_CANCELED,stopped.upload.phase); assertTrue(f.base.children().values.none {it==f.descriptor.name})
        } finally {f.close()}
    }
    @Test fun cancelKeepsExactJournalAndPrivateBytesAndCannotBeConfirmed():Unit=runBlocking {
        val f=Fixture(); try {f.start(); f.expire(); val journal=f.journal.readBytes(); val bytes=f.part().readBytes(); val review=f.review(); f.base.incoming.cancelWindowReview(review.id)
            refused {f.base.incoming.confirmWindow(review)}; assertArrayEquals(journal,f.journal.readBytes()); assertArrayEquals(bytes,f.part().readBytes()); assertEquals(FileStatus.STALE_REFERENCE,f.write().status)
        } finally {f.close()}
    }
    @Test fun reviewExtendsOnlyTheSameReceiptAndNativeCheckpointThenRestartResumesOneVerifiedProviderCopy():Unit=runBlocking {
        val f=Fixture(); try {f.start(); val part=f.part(); val identity=Os.lstat(part.path).let {it.st_dev to it.st_ino}; val before=f.base.children(); f.expire()
            val review=f.review(); f.base.incoming.confirmWindow(review); assertEquals(review.proposedExpiresAt,f.row.operationExpiresAt); assertEquals(StorageRules.RANGE.toLong(),f.row.offset)
            assertEquals(UploadPhase.UPLOAD_RECEIVING,f.row.phase); assertFalse(f.row.active); assertEquals(before,f.base.children()); assertEquals(identity,Os.lstat(part.path).let {it.st_dev to it.st_ino})
            f.base.restart(); assertEquals(review.proposedExpiresAt,f.row.operationExpiresAt); f.base.write(f.descriptor,f.bytes,f.row.offset)
            val done=f.base.finish(f.descriptor); assertEquals(UploadPhase.UPLOAD_COMPLETED,done.phase); assertEquals(f.descriptor.id,done.id); assertArrayEquals(f.bytes,f.base.read(done.finalName))
            assertEquals(before.size+1,f.base.children().size)
            val next=f.base.descriptor(ByteArray(0),"later.bin").toBuilder(); val token=Rules.decode(next.id); ByteBuffer.wrap(token).putLong(f.clock.wall.get()); next.id=Rules.b64(token)
            f.base.begin(next.build()); assertTrue(f.base.incoming.state.value.any {it.id==done.id && it.phase==UploadPhase.UPLOAD_COMPLETED}); assertEquals(f.bytes.size.toLong(),f.base.incoming.reservedBytes(f.base.approval.id))
        } finally {f.close()}
    }
    @Test fun exactShownDeadlineIsSingleUseAndStatusCannotSlideItForward():Unit=runBlocking {
        val f=Fixture(); try {f.start(); f.expire(); val review=f.review(); f.clock.advance(60_000); f.base.incoming.confirmWindow(review); refused {f.base.incoming.confirmWindow(review)}
            assertEquals(review.proposedExpiresAt,f.row.operationExpiresAt); f.clock.advance(MutationRules.LIFETIME)
            f.base.call(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_STATUS).setTransferId(f.descriptor.id).setMutationEpoch(f.base.epoch).build())
            assertEquals(review.proposedExpiresAt,f.row.operationExpiresAt); assertEquals(FileStatus.STALE_REFERENCE,f.write().status)
            val again=f.review(); f.base.incoming.confirmWindow(again); assertEquals(FileStatus.FILE_STATUS_OK,f.write().status)
        } finally {f.close()}
    }
    @Test fun anUnexpiredWindowCannotBeRenewedEarly():Unit=runBlocking {val f=Fixture(); try {f.start(); val before=f.journal.readBytes(); refused {f.review()}; assertArrayEquals(before,f.journal.readBytes())} finally {f.close()}}
    @Test fun wallAndMonotonicExpiryOrRollbackEndTheSingleUseReview():Unit=runBlocking {
        for(kind in listOf("wall","elapsed","both","wall-back","elapsed-back")) {val f=Fixture(); try {f.start(); f.expire(); val review=f.review(); val before=f.journal.readBytes()
            when(kind) {"wall"->f.clock.wall.addAndGet(300_000); "elapsed"->f.clock.elapsed.addAndGet(300_000); "both"->f.clock.advance(300_000); "wall-back"->f.clock.wall.decrementAndGet(); else->f.clock.elapsed.decrementAndGet()}
            refused {f.base.incoming.confirmWindow(review)}; assertArrayEquals(before,f.journal.readBytes())
        } finally {f.close()}}
    }
    @Test fun modifiedReviewValuesAreConsumedWithoutChangingMetadata():Unit=runBlocking {
        for(field in listOf("deadline","receipt","checkpoint","peer")) {val f=Fixture(); try {f.start(); f.expire(); val review=f.review(); val before=f.journal.readBytes()
            val changed=when(field) {"deadline"->review.copy(proposedExpiresAt=review.proposedExpiresAt+1); "receipt"->review.copy(receipt=StorageRules.token()); "peer"->review.copy(peer="c".repeat(64)); else->review.copy(checkpoint=0)}
            refused {f.base.incoming.confirmWindow(changed)}; refused {f.base.incoming.confirmWindow(review)}; assertArrayEquals(before,f.journal.readBytes())
        } finally {f.close()}}
    }
    @Test fun privateByteTamperingIsRehashedBeforeReviewAndConfirmation():Unit=runBlocking {
        for(afterReview in listOf(false,true)) {val f=Fixture(); try {f.start(); f.expire(); val review=if(afterReview) f.review() else null; val before=f.journal.readBytes()
            RandomAccessFile(f.part(),"rw").use {it.writeByte(f.bytes[0].toInt() xor 1)}
            if(review==null) refused {f.review()} else refused {f.base.incoming.confirmWindow(review)}; assertArrayEquals(before,f.journal.readBytes())
        } finally {f.close()}}
    }
    @Test fun replacingOrRemovingTheNativePartCannotRecreateItThroughReview():Unit=runBlocking {
        for(replace in listOf(false,true)) {val f=Fixture(); try {f.start(); f.expire(); val review=f.review(); val part=f.part(); val before=f.journal.readBytes(); val kept=File(part.path+".kept"); assertTrue(part.renameTo(kept)); if(replace) part.writeBytes(kept.readBytes())
            refused {f.base.incoming.confirmWindow(review)}; assertArrayEquals(before,f.journal.readBytes()); assertEquals(replace,part.exists()); assertArrayEquals(f.bytes.copyOf(StorageRules.RANGE),kept.readBytes())
        } finally {f.close()}}
    }
    @Test fun grantPausePermissionsAndExclusionsInvalidateEvenAfterRestoringTheSameGrant():Unit=runBlocking {
        for(change in listOf("pause","permissions","exclusions")) {val f=Fixture(); try {f.start(); f.expire(); val review=f.review(); val before=f.journal.readBytes()
            f.base.grants.update(f.base.grant.id,if(change=="permissions") StorageRules.READ_ONLY else 15,if(change=="exclusions") setOf(f.descriptor.name) else emptySet(),enabled=change!="pause")
            f.base.grants.update(f.base.grant.id,15); refused {f.base.incoming.confirmWindow(review)}; assertArrayEquals(before,f.journal.readBytes())
        } finally {f.close()}}
    }
    @Test fun renewedWindowCannotExtendTheGrantOrAuthorizeAnotherPeer():Unit=runBlocking {
        val f=Fixture(); try {f.start(); f.expire(); val expiry=System.currentTimeMillis()+60_000; f.base.grants.update(f.base.grant.id,15,expiresAt=expiry)
            val review=f.review(); f.base.incoming.confirmWindow(review); assertEquals(expiry,f.base.grants.all().single().expiresAt)
            val other=f.base.raw(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_STATUS).setTransferId(f.descriptor.id).setMutationEpoch(f.base.epoch).build(),"c".repeat(64)); assertNotEquals(FileStatus.FILE_STATUS_OK,other.status)
            f.base.grants.update(f.base.grant.id,15,expiresAt=System.currentTimeMillis()-1); assertEquals(FileStatus.ACCESS_DENIED,f.write().status); assertEquals(review.checkpoint,f.row.offset)
        } finally {f.close()}
    }
    @Test fun foregroundWithdrawalExplicitInvalidationAndRestartDropPendingOffers():Unit=runBlocking {
        val f=Fixture(); try {f.start(); f.expire(); val before=f.journal.readBytes(); var review=f.review(); f.base.visible.set(false); refused {f.base.incoming.confirmWindow(review)}
            f.base.visible.set(true); review=f.review(); f.base.incoming.invalidateWindowReviews(); refused {f.base.incoming.confirmWindow(review)}
            review=f.review(); f.base.restart(); refused {f.base.incoming.confirmWindow(review)}; assertArrayEquals(before,f.journal.readBytes())
        } finally {f.close()}
    }
    @Test fun ownerResetAndHistoryRotationPreserveStagingAndEndOldReviewAuthority():Unit=runBlocking {
        for(reset in listOf(false,true)) {val f=Fixture(); try {f.start(); f.expire(); val review=f.review(); val part=f.part(); val bytes=part.readBytes()
            if(reset) f.base.policy.reset() else f.base.incoming.archiveHistory()
            refused {f.base.incoming.confirmWindow(review)}; assertArrayEquals(bytes,part.readBytes())
        } finally {f.close()}}
    }
    @Test fun contextWithdrawalDuringPrefixCheckingLeavesTheSlotAndMetadataUnchanged():Unit=runBlocking {
        val f=Fixture(); try {f.start(); f.expire(); val before=f.journal.readBytes(); var checks=0
            refused {f.base.incoming.reviewWindow(f.descriptor.id) {++checks<3}}; assertArrayEquals(before,f.journal.readBytes())
            val review=f.review(); f.base.incoming.cancelWindowReview(review.id)
        } finally {f.close()}
    }
    @Test fun emptyCheckpointsIncludingZeroByteFilesNeedSeparateReceivingAndSaving():Unit=runBlocking {
        for(size in listOf(0,17)) {val f=Fixture(); try {val bytes=ByteArray(size) {17}; f.start(false,bytes); f.expire(); val review=f.review(); f.base.incoming.confirmWindow(review)
            assertEquals(0L,f.row.offset); assertTrue(f.row.finalName.isEmpty()); f.base.write(f.descriptor,bytes); val done=f.base.finish(f.descriptor)
            assertEquals(UploadPhase.UPLOAD_COMPLETED,done.phase); assertArrayEquals(bytes,f.base.read(done.finalName))
        } finally {f.close()}}
    }
    @Test fun invalidationCancelsAnActiveCheckpointCheckAndReleasesItsSlot():Unit=runBlocking {
        val f=Fixture(); try {f.start(); f.expire(); val before=f.journal.readBytes(); var checks=0
            refused {f.base.incoming.reviewWindow(f.descriptor.id) {if(++checks==4) f.base.incoming.invalidateWindowReviews(); true}}
            assertArrayEquals(before,f.journal.readBytes()); val review=f.review(); f.base.incoming.cancelWindowReview(review.id)
        } finally {f.close()}
    }
    @Test fun unsupportedPersistedAuthorityBlocksRatherThanRenewingAndLegacyMetadataKeepsItsDeadline():Unit=runBlocking {
        for(value in listOf("legacy","negative","overflow","malformed")) {val f=Fixture(); try {f.start(); val root=JSONObject(f.journal.readText()); val row=root.getJSONArray("rows").getJSONObject(0)
            when(value) {"legacy"->row.remove("operationApprovedAt"); "negative"->row.put("operationApprovedAt",-1); "overflow"->row.put("operationApprovedAt",Long.MAX_VALUE); else->row.put("operationApprovedAt","not a timestamp")}
            f.journal.writeText(root.toString()); f.base.restart(selectTarget=false)
            if(value=="legacy") {assertEquals(MutationRules.created(f.descriptor.id)+MutationRules.LIFETIME,f.row.operationExpiresAt); f.expire(); assertEquals(FileStatus.STALE_REFERENCE,f.write().status)}
            else assertTrue(f.base.incoming.recoveryIssues().isNotEmpty()); assertTrue(f.part().exists())
        } finally {f.close()}}
    }
    @Test fun aRecordedProviderCopyIsVerifiedWithoutRewritingOrCreatingAnotherDocument():Unit=runBlocking {
        val f=Fixture(); try {f.start(); f.base.write(f.descriptor,f.bytes,f.row.offset); val done=f.base.finish(f.descriptor); assertEquals(UploadPhase.UPLOAD_COMPLETED,done.phase)
            val before=f.base.children(); val root=JSONObject(f.journal.readText()); root.getJSONArray("rows").getJSONObject(0).put("phase",UploadPhase.UPLOAD_REVIEW.name); f.journal.writeText(root.toString())
            f.base.restart(); f.expire(); val review=f.review(); assertEquals(done.finalName,review.publishedName); f.base.incoming.confirmWindow(review); assertEquals(before,f.base.children())
            assertEquals(UploadPhase.UPLOAD_COMPLETED,f.base.finish(f.descriptor).phase); assertEquals(before,f.base.children()); assertArrayEquals(f.bytes,f.base.read(done.finalName))
        } finally {f.close()}
    }
    @Test fun uncertainPublicationWithoutARecordedDocumentCannotObtainNewWriteAuthority():Unit=runBlocking {
        val f=Fixture(); try {f.start(); f.base.write(f.descriptor,f.bytes,f.row.offset); f.base.mode("loseReceipt"); assertEquals(UploadPhase.UPLOAD_REVIEW,f.base.finish(f.descriptor).phase)
            val before=f.base.children(); val journal=f.journal.readBytes(); f.expire(); refused {f.review()}; assertEquals(before,f.base.children()); assertArrayEquals(journal,f.journal.readBytes())
        } finally {f.close()}
    }
}
