package com.iconnect.proof

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.SocketTimeoutException
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.atomic.AtomicInteger

@RunWith(AndroidJUnit4::class)
class ConnectionTest {
    @Test fun configuredStunEmitsReflexiveCandidateWithoutAnExternalService() = runBlocking {
        val instrumentation=InstrumentationRegistry.getInstrumentation(); val scope=CoroutineScope(SupervisorJob()+Dispatchers.Main.immediate)
        val requests=AtomicInteger(); val signals=CopyOnWriteArrayList<String>(); var session: RtcTestSession?=null
        val server=DatagramSocket(InetSocketAddress(InetAddress.getByName("127.0.0.1"),0)).apply {soTimeout=200}
        val serve=launch(Dispatchers.IO) {
            val buffer=ByteArray(2048)
            while(isActive) {
                val packet=DatagramPacket(buffer,buffer.size)
                try {server.receive(packet)} catch(_:SocketTimeoutException) {continue}
                if(packet.length<20 || buffer[0]!=0.toByte() || buffer[1]!=1.toByte() || !buffer.copyOfRange(4,8).contentEquals(byteArrayOf(0x21,0x12,0xa4.toByte(),0x42))) continue
                requests.incrementAndGet(); val response=buffer.copyOfRange(0,32); response[0]=1; response[1]=1; response[2]=0; response[3]=12
                response[20]=0; response[21]=0x20; response[22]=0; response[23]=8; response[24]=0; response[25]=1
                val port=packet.port xor 0x2112; response[26]=(port ushr 8).toByte(); response[27]=port.toByte()
                val mapped=byteArrayOf(198.toByte(),51,100,77); for(index in 0..3) response[28+index]=(mapped[index].toInt() xor buffer[4+index].toInt()).toByte()
                server.send(DatagramPacket(response,response.size,packet.socketAddress))
            }
        }
        try {
            instrumentation.runOnMainSync {
                session=RtcTestSession(instrumentation.targetContext,scope,LocalIdentity(),StorageRules.token(),TestPeer.key().public.encoded,
                    {kind,body,_->if(kind=="candidate") signals.add(body)}, {}, {},stun="stun:127.0.0.1:${server.localPort}",initiator=true)
                session.offer()
            }
            withTimeout(15_000) {while(signals.none {it.contains("198.51.100.77") && it.contains("typ srflx")}) delay(20)}
            assertTrue(requests.get()>0); assertTrue(signals.none {it.contains("typ relay")})
        } finally {
            instrumentation.runOnMainSync {session?.close()}; scope.cancel(); serve.cancelAndJoin(); server.close()
        }
    }
    @Test fun nativePeersAuthenticateInBothRolesAndExchangeBoundedFileRequests() = runBlocking {
        val instrumentation=InstrumentationRegistry.getInstrumentation()
        for(phoneInitiates in listOf(true,false)) {
            val scope=CoroutineScope(SupervisorJob()+Dispatchers.Main.immediate); val phoneReady=CompletableDeferred<Unit>(); val peerReady=CompletableDeferred<Unit>()
            val phoneIdentity=LocalIdentity(); val softwareKey=TestPeer.key()
            val peerIdentity=object:SessionIdentity {
                override val publicKey:ByteArray get()=softwareKey.public.encoded
                override val id=Rules.id(publicKey)
                override fun sign(transcript:ByteArray)=java.security.Signature.getInstance("SHA256withECDSA").run {initSign(softwareKey.private); update(transcript); sign()}
            }
            val id=StorageRules.token(); var phone:RtcTestSession?=null; var peer:RtcTestSession?=null; val messages=CopyOnWriteArrayList<String>()
            val observed=CopyOnWriteArrayList<PhoneActivityEvent>()
            try {
                instrumentation.runOnMainSync {
                    phone=RtcTestSession(instrumentation.targetContext,scope,phoneIdentity,id,peerIdentity.publicKey,
                        {kind,body,mid->when(kind){"offer"->peer!!.acceptOffer(body);"answer"->peer!!.acceptAnswer(body);"candidate"->peer!!.addCandidate(body,mid)}},
                        {message->messages.add(message); if(phoneInitiates) throw IllegalStateException("Generated status observer failure")},
                        {if(it) phoneReady.complete(Unit); if(phoneInitiates) throw IllegalStateException("Generated readiness observer failure")},initiator=phoneInitiates,
                        activity={event->observed.add(event); if(phoneInitiates) throw IllegalStateException("Generated history observer failure")})
                    peer=RtcTestSession(instrumentation.targetContext,scope,peerIdentity,id,phoneIdentity.publicKey,
                        {kind,body,mid->when(kind){"offer"->phone.acceptOffer(body);"answer"->phone.acceptAnswer(body);"candidate"->phone.addCandidate(body,mid)}},
                        messages::add,{if(it) peerReady.complete(Unit)},initiator=!phoneInitiates)
                    if(phoneInitiates) phone.offer() else peer.offer()
                }
                try {withTimeout(15_000){phoneReady.await(); peerReady.await()}}
                catch(error:TimeoutCancellationException) {throw AssertionError("Role $phoneInitiates did not authenticate: ${messages.joinToString()}",error)}
                val replies=withContext(Dispatchers.Main.immediate) {listOf(phone!!,peer!!).map {active->async {active.requestFiles(com.iconnect.protocol.FileRequest.newBuilder().setOperation(com.iconnect.protocol.FileOperation.LIST_ROOTS).build())}}.awaitAll()}
                replies.forEach {reply->assertEquals(com.iconnect.protocol.FileStatus.ACCESS_DENIED,reply.status); assertEquals(0,reply.entriesCount); assertTrue(reply.content.isEmpty)}
                assertTrue(phone!!.isAuthenticated); assertTrue(peer!!.isAuthenticated)
                assertEquals(listOf(PhoneActivityOutcome.REQUESTED,PhoneActivityOutcome.COMPLETED),observed.map {it.outcome})
                assertTrue(observed.all {it.kind==PhoneActivityKind.DIRECT_CONNECTION && it.peer==peerIdentity.id})
                instrumentation.runOnMainSync {phone.close(); phone.close()}
                assertEquals(listOf(PhoneActivityOutcome.REQUESTED,PhoneActivityOutcome.COMPLETED,PhoneActivityOutcome.CANCELED),observed.map {it.outcome})
            } finally {instrumentation.runOnMainSync {phone?.close(); peer?.close()}; scope.cancel()}
        }
    }
    @Test fun invalidStunAndOfflineInitiationCannotStartAConnection() {
        val instrumentation=InstrumentationRegistry.getInstrumentation(); val app=instrumentation.targetContext.applicationContext as IConnectApplication
        lateinit var model: PhoneController
        instrumentation.runOnMainSync {
            model=app.controller(); model.setUiVisible(true); model.disconnect(); model.setServer("http://10.0.2.2:5078"); model.setStun("turn:relay.example.test"); model.connect()
            assertFalse(model.state.value.connecting); assertFalse(model.state.value.connected); assertTrue(model.state.value.status.contains("Use stun:"))
            model.setStun(""); model.connectPeer(); assertFalse(model.state.value.peerConnecting); assertFalse(model.state.value.authenticated)
            assertTrue(model.state.value.status.contains("connect to the coordinator"))
        }
        lateinit var closing: Job; instrumentation.runOnMainSync {closing=model.close()}; runBlocking {closing.join()}; instrumentation.runOnMainSync {app.releaseClosed(model)}
    }
    @Test fun roleAndDescriptionOrderAreCheckedBeforeNativeNegotiation() {
        val instrumentation=InstrumentationRegistry.getInstrumentation(); val scope=CoroutineScope(SupervisorJob()+Dispatchers.Main.immediate)
        instrumentation.runOnMainSync {
            val active=RtcTestSession(instrumentation.targetContext,scope,LocalIdentity(),StorageRules.token(),TestPeer.key().public.encoded,{_,_,_->},{},{},initiator=true)
            try { assertThrows(IllegalStateException::class.java) {active.acceptOffer("not an offer")}; assertThrows(IllegalStateException::class.java) {active.acceptAnswer("not an answer")}; active.close(); assertTrue(active.isClosed) }
            finally {active.close(); scope.cancel()}
        }
    }
}
