package com.iconnect.proof

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.google.protobuf.ByteString
import com.iconnect.protocol.*
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class IncomingWindowNativeTest {
    @Test fun authenticatedAndroidPeersResumeOnlyTheOriginalReceiptAfterReceivingOwnerReview():Unit=runBlocking {
        val f=IncomingWindowRecoveryTest.Fixture(); val instrumentation=InstrumentationRegistry.getInstrumentation()
        val scope=CoroutineScope(SupervisorJob()+Dispatchers.Main.immediate); var receiver:RtcTestSession?=null; var sender:RtcTestSession?=null
        val receiverReady=CompletableDeferred<Unit>(); val senderReady=CompletableDeferred<Unit>()
        val identity=object:SessionIdentity {
            override val publicKey:ByteArray get()=f.base.software.public.encoded
            override val id=Rules.id(publicKey)
            override fun sign(transcript:ByteArray)=java.security.Signature.getInstance("SHA256withECDSA").run {initSign(f.base.software.private); update(transcript); sign()}
        }
        try {
            val session=StorageRules.token(); val local=LocalIdentity()
            instrumentation.runOnMainSync {
                receiver=RtcTestSession(instrumentation.targetContext,scope,local,session,identity.publicKey,
                    {kind,body,mid->when(kind) {"offer"->sender!!.acceptOffer(body); "answer"->sender!!.acceptAnswer(body); "candidate"->sender!!.addCandidate(body,mid)}},
                    {},{if(it) receiverReady.complete(Unit)},fileService=f.base.service,available={f.base.visible.get()})
                sender=RtcTestSession(instrumentation.targetContext,scope,identity,session,local.publicKey,
                    {kind,body,mid->when(kind) {"offer"->receiver.acceptOffer(body); "answer"->receiver.acceptAnswer(body); "candidate"->receiver.addCandidate(body,mid)}},
                    {},{if(it) senderReady.complete(Unit)},initiator=true); sender.offer()
            }
            withTimeout(20_000) {receiverReady.await(); senderReady.await()}; var calls=0
            suspend fun request(value:FileRequest):FileResponse=withContext(Dispatchers.Main.immediate) {calls++; PhoneTransferManifests.checked(sender!!::requestFiles,value)}
            val context=request(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_CONTEXT).build()); f.base.epoch=context.mutationEpoch; f.descriptor=f.base.descriptor(f.bytes)
            val descriptor=f.descriptor
            assertEquals(FileStatus.FILE_STATUS_OK,request(FileRequest.newBuilder().setOperation(FileOperation.PREPARE_UPLOAD).setDestinationId(context.entriesList.single().id)
                .setTransferId(descriptor.id).setMutationEpoch(context.mutationEpoch).setUpload(descriptor).build()).status)
            fun write(offset:Long)=FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_WRITE).setTransferId(descriptor.id).setMutationEpoch(context.mutationEpoch)
                .setOffset(offset).setContent(ByteString.copyFrom(f.bytes,offset.toInt(),minOf(StorageRules.RANGE.toLong(),f.bytes.size-offset).toInt())).build()
            assertEquals(FileStatus.FILE_STATUS_OK,request(write(0)).status); f.expire()
            val denied=runCatching {request(write(StorageRules.RANGE.toLong()))}.exceptionOrNull()
            assertTrue(denied is StorageFailure); assertEquals(FileStatus.STALE_REFERENCE,(denied as StorageFailure).status)
            val count=f.base.children().size; val before=calls; val review=f.review(); f.base.incoming.confirmWindow(review); assertEquals(before,calls)
            assertEquals(StorageRules.RANGE.toLong(),f.row.offset); assertEquals(count,f.base.children().size)
            var offset=f.row.offset; while(offset<f.bytes.size) {val response=request(write(offset)); assertEquals(FileStatus.FILE_STATUS_OK,response.status); offset=response.upload.offset}
            assertEquals(FileStatus.FILE_STATUS_OK,request(FileRequest.newBuilder().setOperation(FileOperation.FINISH_UPLOAD).setTransferId(descriptor.id).setMutationEpoch(context.mutationEpoch).build()).status)
            withTimeout(10_000) {while(f.row.active) delay(20)}
            val done=request(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_STATUS).setTransferId(descriptor.id).setMutationEpoch(context.mutationEpoch).build()).upload
            assertEquals(UploadPhase.UPLOAD_COMPLETED,done.phase); assertEquals(descriptor.id,done.id); assertArrayEquals(f.bytes,f.base.read(done.finalName)); assertEquals(count+1,f.base.children().size)
        } finally {instrumentation.runOnMainSync {receiver?.close(); sender?.close()}; scope.cancel(); f.close()}
    }
}
