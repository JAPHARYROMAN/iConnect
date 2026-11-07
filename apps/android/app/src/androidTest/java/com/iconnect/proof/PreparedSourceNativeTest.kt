package com.iconnect.proof

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.iconnect.protocol.*
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class PreparedSourceNativeTest {
    @Test fun authenticatedPeerLosesTheStoppedTicketButCanRequestAFreshSourceThroughItsUnchangedGrant():Unit=runBlocking {
        val f=PreparedSourceControlsTest.Fixture(); val instrumentation=InstrumentationRegistry.getInstrumentation()
        val scope=CoroutineScope(SupervisorJob()+Dispatchers.Main.immediate); var phone:RtcTestSession?=null; var computer:RtcTestSession?=null
        val phoneReady=CompletableDeferred<Unit>(); val computerReady=CompletableDeferred<Unit>()
        val identity=object:SessionIdentity {
            override val publicKey get()=f.base.software.public.encoded
            override val id=Rules.id(publicKey)
            override fun sign(transcript:ByteArray)=java.security.Signature.getInstance("SHA256withECDSA").run {initSign(f.base.software.private); update(transcript); sign()}
        }
        try {
            val session=StorageRules.token(); val local=LocalIdentity()
            instrumentation.runOnMainSync {
                phone=RtcTestSession(instrumentation.targetContext,scope,local,session,identity.publicKey,
                    {kind,body,mid->when(kind) {"offer"->computer!!.acceptOffer(body); "answer"->computer!!.acceptAnswer(body); "candidate"->computer!!.addCandidate(body,mid)}},
                    {},{if(it) phoneReady.complete(Unit)},fileService=f.service)
                computer=RtcTestSession(instrumentation.targetContext,scope,identity,session,local.publicKey,
                    {kind,body,mid->when(kind) {"offer"->phone.acceptOffer(body); "answer"->phone.acceptAnswer(body); "candidate"->phone.addCandidate(body,mid)}},
                    {},{if(it) computerReady.complete(Unit)},initiator=true); computer.offer()
            }
            withTimeout(20_000) {phoneReady.await(); computerReady.await()}
            suspend fun request(value:FileRequest):FileResponse=withContext(Dispatchers.Main.immediate) {PhoneTransferManifests.checked(computer!!::requestFiles,value)}
            val root=request(FileRequest.newBuilder().setOperation(FileOperation.LIST_ROOTS).build()).entriesList.single()
            val file=request(FileRequest.newBuilder().setOperation(FileOperation.LIST_DIRECTORY).setNodeId(root.id).build()).entriesList.single {it.name=="simu.txt"}
            val prepare=FileRequest.newBuilder().setOperation(FileOperation.PREPARE_DOWNLOAD).setNodeId(file.id).setExpectedVersion(file.version).build()
            val old=request(prepare).download; f.sources.stopOwned(old.id) {true}
            val denied=runCatching {request(FileRequest.newBuilder().setOperation(FileOperation.DOWNLOAD_READ).setTransferId(old.id).setLength(1024).build())}.exceptionOrNull()
            assertTrue(denied is StorageFailure); assertEquals(FileStatus.ACCESS_DENIED,(denied as StorageFailure).status)
            val fresh=request(prepare).download; assertNotEquals(old.id,fresh.id)
            withTimeout(10_000) {while(request(FileRequest.newBuilder().setOperation(FileOperation.DOWNLOAD_STATUS).setTransferId(fresh.id).build()).download.state!=DownloadState.DOWNLOAD_READY) delay(20)}
            val bytes=request(FileRequest.newBuilder().setOperation(FileOperation.DOWNLOAD_READ).setTransferId(fresh.id).setLength(1024).build()).content.toByteArray()
            assertArrayEquals(f.original.copyOf(bytes.size),bytes); assertEquals(fresh.id,f.sources.inventory.value.single().id)
        } finally {instrumentation.runOnMainSync {phone?.close(); computer?.close()}; scope.cancel(); f.close()}
    }
}
