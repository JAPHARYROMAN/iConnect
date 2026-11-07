package com.iconnect.proof

import androidx.test.ext.junit.runners.AndroidJUnit4
import com.google.protobuf.ByteString
import com.iconnect.protocol.*
import kotlinx.coroutines.*
import kotlinx.coroutines.channels.Channel
import okhttp3.Request
import okhttp3.WebSocket
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.lang.reflect.InvocationTargetException
import java.net.URI
import java.security.Signature
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CopyOnWriteArrayList
import kotlin.coroutines.intrinsics.COROUTINE_SUSPENDED
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException
import kotlin.coroutines.suspendCoroutine

/** In-memory socket seam, production controller handler and real native peers. No coordinator is started. */
@RunWith(AndroidJUnit4::class)
class LateSignalTest {
    private class Fixture {
        val f=ActivityControlsTest.Fixture()
        val owner=LocalIdentity(); val key=TestPeer.key()
        val approval=TestPeer.approve(AndroidOwnerPolicy(f.application),key)
        val peer=object:SessionIdentity {
            override val publicKey:ByteArray get()=key.public.encoded
            override val id=Rules.id(publicKey)
            override fun sign(transcript:ByteArray)=Signature.getInstance("SHA256withECDSA").run {initSign(key.private); update(transcript); sign()}
        }
        val scope=CoroutineScope(SupervisorJob()+Dispatchers.Main.immediate)
        val outgoing=Channel<Signal>(64)
        val remotes=ConcurrentHashMap<String,RtcTestSession>()
        val answers=ConcurrentHashMap<String,String>()
        val errors=CopyOnWriteArrayList<Throwable>()
        val socket=object:WebSocket {
            private var closed=false
            override fun request()=Request.Builder().url("https://coordinator.example/signal").build()
            override fun queueSize()=0L
            override fun send(text:String)=false
            override fun send(bytes:okio.ByteString):Boolean {
                if(closed) return false
                val frame=Rules.parse(bytes.toByteArray())
                return frame.kind!="signal" || outgoing.trySend(Signal.parseFrom(frame.payload)).isSuccess
            }
            override fun close(code:Int,reason:String?):Boolean {closed=true; return true}
            override fun cancel() {closed=true}
        }
        private fun field(name:String)=PhoneController::class.java.getDeclaredField(name).apply {isAccessible=true}
        fun active()=field("session").get(f.model) as? RtcTestSession
        suspend fun inject(frame:Frame)=withContext(Dispatchers.Main.immediate) {
            suspendCoroutine<Unit> {reply->
                try {
                    val method=PhoneController::class.java.getDeclaredMethod("handle",Frame::class.java,kotlin.coroutines.Continuation::class.java).apply {isAccessible=true}
                    if(method.invoke(f.model,frame,reply)!==COROUTINE_SUSPENDED) reply.resume(Unit)
                } catch(error:InvocationTargetException) {reply.resumeWithException(error.targetException)}
            }
        }
        fun signed(id:String,kind:String,body:String)=Signal.newBuilder().setSessionId(id).setFromDevice(peer.id).setToDevice(owner.id).setKind(kind).setBody(body).setMid("0")
            .setCreatedAt(System.currentTimeMillis()).setNonce(ByteString.copyFrom(Rules.decode(StorageRules.token()))).build().let {it.toBuilder().setSignature(ByteString.copyFrom(peer.sign(Rules.signal(it)))).build()}
        suspend fun signal(value:Signal)=inject(Frame.newBuilder().setVersion(1).setKind("signal").setPayload(value.toByteString()).build())
        suspend fun unavailable(id:String,peer:String=approval.id)=inject(Frame.newBuilder().setVersion(1).setKind("signal-unavailable")
            .setPayload(SignalUnavailable.newBuilder().setSessionId(id).setPeerId(peer).build().toByteString()).build())
        suspend fun open() {
            f.open()
            f.main {field("scope").set(f.model,scope); field("lifetime").set(f.model,scope.coroutineContext[Job]); field("socket").set(f.model,socket); field("endpoint").set(f.model,URI("https://coordinator.example"))}
            inject(Frame.newBuilder().setVersion(1).setKind("challenge")
                .setPayload(Challenge.newBuilder().setNonce(ByteString.copyFrom(ByteArray(32))).build().toByteString()).build())
            inject(Frame.newBuilder().setVersion(1).setKind("registered").setPayload(Notice.newBuilder().setMessage(owner.id).build().toByteString()).build())
            scope.launch {
                for(value in outgoing) try {
                    Rules.validateSignal(value,owner.publicKey,peer.id)
                    if(value.kind=="offer") {
                        val id=value.sessionId
                        val remote=RtcTestSession(f.application,scope,peer,id,owner.publicKey,{kind,body,_->
                            if(kind=="answer") answers[id]=body
                            scope.launch {runCatching {signal(signed(id,kind,body))}.onFailure(errors::add)}
                        },{},{})
                        remotes[id]=remote; remote.acceptOffer(value.body)
                    } else remotes[value.sessionId]?.takeUnless {it.isClosed}?.let {remote->if(value.kind=="candidate") remote.addCandidate(value.body,value.mid) else remote.acceptAnswer(value.body)}
                } catch(error:Exception) {errors.add(error)}
            }
        }
        suspend fun connect():RtcTestSession {
            f.main {f.model.connectPeer()}
            withTimeout(15_000) {while(!f.model.state.value.authenticated || active()?.isAuthenticated!=true || remotes[active()?.sessionId?:""]?.isAuthenticated!=true) {check(errors.isEmpty()) {errors.joinToString()}; delay(15)}}
            return requireNotNull(active())
        }
        suspend fun dispose() {f.main {remotes.values.forEach {it.close()}}; f.dispose(); scope.cancel(); outgoing.close()}
    }

    @Test fun unavailableAttemptKeepsFreshSignedLateMessagesOutOfTheNextRealAuthenticatedSession()=runBlocking {
        val f=Fixture()
        try {
            f.open(); val first=f.connect()
            f.unavailable(StorageRules.token()); f.unavailable(first.sessionId,"a".repeat(64)); assertTrue(first.isAuthenticated)
            f.unavailable(first.sessionId); assertTrue(first.isClosed); assertNull(f.active()); assertTrue(f.f.model.state.value.connected)
            val late=f.signed(first.sessionId,"candidate","candidate:fixture 1 UDP 123 192.0.2.1 9999 typ host")
            f.signal(late); f.signal(f.signed(first.sessionId,"answer",requireNotNull(f.answers[first.sessionId])))
            val next=f.connect(); assertNotEquals(first.sessionId,next.sessionId)
            f.signal(f.signed(first.sessionId,"candidate",late.body)); f.signal(f.signed(first.sessionId,"answer",requireNotNull(f.answers[first.sessionId])))
            assertSame(next,f.active()); assertTrue(next.isAuthenticated); assertTrue(f.f.model.state.value.connected); assertTrue(f.errors.isEmpty())
            val response=withContext(Dispatchers.Main.immediate) {next.requestFiles(FileRequest.newBuilder().setOperation(FileOperation.LIST_ROOTS).build())}
            assertEquals(FileStatus.ACCESS_DENIED,response.status)
            assertTrue(runCatching {f.signal(late)}.exceptionOrNull() is IllegalArgumentException)
            val forged=f.signed(first.sessionId,"candidate",late.body).toBuilder().setSignature(ByteString.EMPTY).build()
            assertTrue(runCatching {f.signal(forged)}.exceptionOrNull() is IllegalArgumentException)
            assertTrue(runCatching {f.signal(f.signed(StorageRules.token(),"candidate",late.body))}.exceptionOrNull() is IllegalArgumentException)
        } finally {f.dispose()}
    }
}
