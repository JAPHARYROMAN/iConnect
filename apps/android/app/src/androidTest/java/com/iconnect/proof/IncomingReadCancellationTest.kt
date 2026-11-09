package com.iconnect.proof

import android.os.Bundle
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.iconnect.protocol.*
import kotlinx.coroutines.*
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

@RunWith(AndroidJUnit4::class)
class IncomingReadCancellationTest {
    private fun probe(f:IncomingUploadsTest.Fixture,id:String,release:Boolean=false)=requireNotNull(
        f.base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"openProbe",id,Bundle().apply {putBoolean("release",release)}))

    private suspend fun requireBlockedCancelableRead(f:IncomingUploadsTest.Fixture,id:String) {
        withTimeout(5_000) {while(!probe(f,id).getBoolean("entered")) delay(10)}
        assertTrue("The document provider must receive the read's cancellation signal.",probe(f,id).getBoolean("cancelable"))
    }

    @Test fun sessionWithdrawalCancelsOrdinaryPublishedChecksAndReleasesVerificationSlots()=runBlocking {
        val f=IncomingUploadsTest.Fixture(); var document=""
        try {
            f.initialize(); val bytes="generated publication".toByteArray(); val row=f.begin(f.descriptor(bytes))
            f.write(row,bytes); val saved=f.finish(row); assertEquals(UploadPhase.UPLOAD_COMPLETED,saved.phase)
            document=f.children().entries.single {it.value==saved.finalName}.key
            val journal=File(f.root,"incoming-uploads/journal.json"); val before=journal.readBytes()
            fun request()=FileRequest.newBuilder().setOperation(FileOperation.VERIFY_PUBLISHED_UPLOAD)
                .setTransferId(saved.id).setMutationEpoch(f.epoch).setMutationId(StorageRules.token()).build()
            f.mode("blockRead"); assertFalse(f.call(request()).publishedUploadCheck.verified)
            requireBlockedCancelableRead(f,document)
            f.service.suspendAvailability()
            withTimeout(3_000) {f.incoming.whenIdle()}
            assertFalse(probe(f,document).getBoolean("entered")); assertArrayEquals(before,journal.readBytes())
            requireNotNull(f.grants.reserveIncomingVerification()).use {
                requireNotNull(f.grants.reserveIncomingVerification()).use {}
            }
            f.mode(""); f.restart(); val fresh=request(); assertFalse(f.call(fresh).publishedUploadCheck.verified)
            withTimeout(10_000) {f.incoming.whenIdle()}
            assertTrue(f.call(fresh).publishedUploadCheck.verified)
            assertArrayEquals(before,journal.readBytes()); assertArrayEquals(bytes,f.read(saved.finalName))
        } finally {withContext(NonCancellable) {
            if(document.isNotEmpty()) probe(f,document,true)
            f.mode(""); f.close()
        }}
    }

    @Test fun invalidationCancelsRecordedProviderWindowReviewAndConfirmationWithoutChangingCopies()=runBlocking {
        for(confirm in listOf(false,true)) {
            val f=IncomingWindowRecoveryTest.Fixture(); var document=""; var pending:Deferred<*>?=null
            try {
                val bytes="generated window copy".toByteArray(); f.start(false,bytes)
                f.base.write(f.descriptor,bytes); val saved=f.base.finish(f.descriptor)
                assertEquals(UploadPhase.UPLOAD_COMPLETED,saved.phase)
                document=f.base.children().entries.single {it.value==saved.finalName}.key
                val root=JSONObject(f.journal.readText()); root.getJSONArray("rows").getJSONObject(0).put("phase",UploadPhase.UPLOAD_REVIEW.name)
                f.journal.writeText(root.toString()); f.base.restart(); f.expire()
                val offer=if(confirm) f.review() else null
                val before=f.journal.readBytes(); f.base.mode("blockRead")
                val action=async(Dispatchers.IO) {runCatching {if(offer==null) f.review() else f.base.incoming.confirmWindow(offer)}}
                pending=action; requireBlockedCancelableRead(f.base,document)
                f.base.incoming.invalidateWindowReviews()
                val failure=withTimeout(3_000) {
                    try {action.await().exceptionOrNull()}
                    catch(e:CancellationException) {if(e is TimeoutCancellationException) throw e; e}
                }
                assertTrue("The invalidated provider review must report cancellation.",failure is android.os.OperationCanceledException ||
                    failure is CancellationException && failure !is TimeoutCancellationException)
                assertTrue(f.base.incoming.isIdle); assertFalse(probe(f.base,document).getBoolean("entered"))
                assertArrayEquals(before,f.journal.readBytes()); f.base.mode("")
                assertArrayEquals(bytes,f.base.read(saved.finalName))
                val next=f.review(); f.base.incoming.cancelWindowReview(next.id)
                assertArrayEquals(before,f.journal.readBytes())
            } finally {withContext(NonCancellable) {
                if(document.isNotEmpty()) probe(f.base,document,true)
                pending?.cancelAndJoin(); f.base.mode(""); f.close()
            }}
        }
    }
}
