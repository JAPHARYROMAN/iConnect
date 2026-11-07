package com.iconnect.proof

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.iconnect.protocol.FileOperation
import com.iconnect.protocol.FileRequest
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.security.Signature
import java.util.concurrent.CopyOnWriteArrayList

@RunWith(AndroidJUnit4::class)
class ConnectionActivityTest {
    private suspend fun waitFor(condition:()->Boolean)=withTimeout(15_000) {while(!condition()) delay(15)}
    private fun identity():SessionIdentity {
        val key=TestPeer.key()
        return object:SessionIdentity {
            override val publicKey:ByteArray get()=key.public.encoded
            override val id=Rules.id(publicKey)
            override fun sign(transcript:ByteArray)=Signature.getInstance("SHA256withECDSA").run {initSign(key.private); update(transcript); sign()}
        }
    }
    @Test fun actualNativeChannelRejectsAnUnpinnedIdentityBeforeReadyAndPersistsFailureWithoutSecrets()=runBlocking {
        val f=IncomingUploadsTest.Fixture(); val instrumentation=InstrumentationRegistry.getInstrumentation()
        val scope=CoroutineScope(SupervisorJob()+Dispatchers.Main.immediate); val history=PhoneActivity(f.context)
        val owner=LocalIdentity(); val advertised=identity(); val impostor=identity(); val messages=CopyOnWriteArrayList<String>(); val ready=CopyOnWriteArrayList<Boolean>()
        val id=StorageRules.token(); var phone:RtcTestSession?=null; var remote:RtcTestSession?=null
        try {
            instrumentation.runOnMainSync {
                phone=RtcTestSession(f.context,scope,owner,id,advertised.publicKey,
                    {kind,body,mid->when(kind) {"offer"->remote!!.acceptOffer(body); "answer"->remote!!.acceptAnswer(body); "candidate"->remote!!.addCandidate(body,mid)}},messages::add,ready::add,initiator=true,activity=history::record)
                remote=RtcTestSession(f.context,scope,impostor,id,owner.publicKey,
                    {kind,body,mid->when(kind) {"offer"->phone.acceptOffer(body); "answer"->phone.acceptAnswer(body); "candidate"->phone.addCandidate(body,mid)}},{},{})
                phone.offer()
            }
            waitFor {phone!!.isClosed}
            assertTrue(messages.contains("Direct data channel opened. Waiting for device authentication."))
            assertTrue(messages.contains("Unexpected peer identity or challenge.")); assertFalse(ready.contains(true)); assertFalse(phone!!.isAuthenticated)
            val refused=runCatching {withContext(Dispatchers.Main.immediate) {phone.requestFiles(FileRequest.newBuilder().setOperation(FileOperation.LIST_ROOTS).build())}}.exceptionOrNull()
            assertTrue(refused is IllegalArgumentException)
            history.flush(); assertEquals(listOf(PhoneActivityOutcome.FAILED,PhoneActivityOutcome.REQUESTED),history.state.value.entries.map {it.outcome})
            assertTrue(history.state.value.entries.all {it.peer==advertised.id && it.kind==PhoneActivityKind.DIRECT_CONNECTION})
            val text=File(f.root,"local-activity/activity.json").readText(); assertFalse(text.contains(id)); assertFalse(text.contains("Unexpected peer")); assertFalse(text.contains(f.root.path))
        } finally {instrumentation.runOnMainSync {phone?.close(); remote?.close()}; scope.cancel(); history.close(); f.close()}
    }
    @Test fun ownerCancellationBeforeReadyEndsNegotiationOnceEvenWhenAllObserversThrow()=runBlocking {
        val instrumentation=InstrumentationRegistry.getInstrumentation()
        for(startOffer in listOf(false,true)) {
            val scope=CoroutineScope(SupervisorJob()+Dispatchers.Main.immediate); val events=CopyOnWriteArrayList<PhoneActivityEvent>(); lateinit var active:RtcTestSession
            try {
                instrumentation.runOnMainSync {
                    active=RtcTestSession(instrumentation.targetContext,scope,LocalIdentity(),StorageRules.token(),identity().publicKey,{_,_,_->},{throw IllegalStateException("Generated status failure")},{throw IllegalStateException("Generated readiness failure")},initiator=true,
                        activity={events.add(it); throw IllegalStateException("Generated history failure")})
                    if(startOffer) active.offer()
                    active.close(); active.close(); assertTrue(active.isClosed); assertFalse(active.isAuthenticated)
                }
                delay(200); assertEquals(listOf(PhoneActivityOutcome.REQUESTED,PhoneActivityOutcome.CANCELED),events.map {it.outcome})
            } finally {instrumentation.runOnMainSync {active.close()}; scope.cancel()}
        }
    }
    @Test fun reentrantOwnerCloseAtRealReadinessCannotPublishAStaleAuthenticatedStatus()=runBlocking {
        val instrumentation=InstrumentationRegistry.getInstrumentation(); val scope=CoroutineScope(SupervisorJob()+Dispatchers.Main.immediate)
        val owner=LocalIdentity(); val other=identity(); val id=StorageRules.token()
        val events=CopyOnWriteArrayList<PhoneActivityEvent>(); val statuses=CopyOnWriteArrayList<Boolean>()
        var phone:RtcTestSession?=null; var remote:RtcTestSession?=null; var wasReady=false
        try {
            instrumentation.runOnMainSync {
                phone=RtcTestSession(instrumentation.targetContext,scope,owner,id,other.publicKey,
                    {kind,body,mid->when(kind) {"offer"->remote!!.acceptOffer(body); "answer"->remote!!.acceptAnswer(body); "candidate"->remote!!.addCandidate(body,mid)}},{},statuses::add,initiator=true,
                    activity={event->events.add(event); if(event.outcome==PhoneActivityOutcome.COMPLETED) {wasReady=phone!!.isAuthenticated; phone!!.close(); throw IllegalStateException("Generated reentrant history failure")}})
                remote=RtcTestSession(instrumentation.targetContext,scope,other,id,owner.publicKey,
                    {kind,body,mid->when(kind) {"offer"->phone.acceptOffer(body); "answer"->phone.acceptAnswer(body); "candidate"->phone.addCandidate(body,mid)}},{},{})
                phone.offer()
            }
            waitFor {phone!!.isClosed && statuses.isNotEmpty()}
            assertTrue(wasReady); assertEquals(listOf(false),statuses.toList()); assertFalse(phone!!.isAuthenticated)
            assertEquals(listOf(PhoneActivityOutcome.REQUESTED,PhoneActivityOutcome.COMPLETED,PhoneActivityOutcome.CANCELED),events.map {it.outcome})
        } finally {instrumentation.runOnMainSync {phone?.close(); remote?.close()}; scope.cancel()}
    }
    @Test fun refusedCoordinatorAttemptRecordsFixedFailureAndRecordingOffDoesNotDisableTheAttempt()=runBlocking {
        val f=ActivityControlsTest.Fixture()
        try {
            f.open(); waitFor {f.model.state.value.activity.ready}
            f.main {f.model.setServer("http://127.0.0.1:1/"); f.model.setStun(""); f.model.connect()}
            waitFor {f.model.state.value.activity.entries.any {it.kind==PhoneActivityKind.COORDINATOR_CONNECTION && it.outcome==PhoneActivityOutcome.FAILED}}
            assertFalse(f.model.state.value.connected); assertFalse(f.model.state.value.authenticated)
            assertTrue(f.model.state.value.activity.entries.any {it.kind==PhoneActivityKind.COORDINATOR_CONNECTION && it.outcome==PhoneActivityOutcome.REQUESTED})
            assertTrue(f.model.state.value.activity.entries.all {it.kind!=PhoneActivityKind.DIRECT_CONNECTION && (it.kind!=PhoneActivityKind.COORDINATOR_CONNECTION || it.peer.isEmpty())})
            assertTrue(f.model.state.value.activity.entries.none {it.outcome==PhoneActivityOutcome.COMPLETED && it.kind==PhoneActivityKind.COORDINATOR_CONNECTION})
            f.model.flushActivity(); val text=File(f.root,"local-activity/activity.json").readText(); assertFalse(text.contains("127.0.0.1")); assertFalse(text.contains("Coordinator unavailable"))
            f.closeModel(); f.open(); waitFor {f.model.state.value.activity.ready}
            assertTrue(f.model.state.value.activity.entries.any {it.kind==PhoneActivityKind.COORDINATOR_CONNECTION && it.outcome==PhoneActivityOutcome.FAILED})
            f.model.configureActivity(0); f.main {f.model.setServer("http://127.0.0.1:1/"); f.model.setStun(""); f.model.connect()}
            waitFor {f.model.state.value.events.any {it.startsWith("Coordinator unavailable:")}}
            f.model.flushActivity(); assertTrue(f.model.state.value.activity.entries.isEmpty())
        } finally {f.dispose()}
    }
    @Test fun ownerCancelAndForegroundWithdrawalKeepDistinctTerminalOutcomesAndIgnoreLateSocketFailures()=runBlocking {
        val f=ActivityControlsTest.Fixture()
        try {
            f.open(); waitFor {f.model.state.value.activity.ready}
            f.main {f.model.setServer("http://127.0.0.1:1/"); f.model.setStun(""); f.model.connect(); f.model.disconnect()}
            delay(300); f.model.flushActivity()
            assertEquals(listOf(PhoneActivityOutcome.CANCELED,PhoneActivityOutcome.REQUESTED),f.model.state.value.activity.entries.filter {it.kind==PhoneActivityKind.COORDINATOR_CONNECTION}.map {it.outcome})
            f.main {f.model.connect(); f.model.setUiVisible(false)}
            delay(300); f.model.flushActivity()
            val rows=f.model.state.value.activity.entries.filter {it.kind==PhoneActivityKind.COORDINATOR_CONNECTION}
            assertEquals(listOf(PhoneActivityOutcome.PAUSED,PhoneActivityOutcome.REQUESTED,PhoneActivityOutcome.CANCELED,PhoneActivityOutcome.REQUESTED),rows.map {it.outcome})
            assertFalse(f.model.state.value.connected); assertFalse(f.model.state.value.connecting); assertFalse(f.model.state.value.authenticated)
        } finally {f.dispose()}
    }
}
