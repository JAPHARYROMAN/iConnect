package com.iconnect.proof

import androidx.test.ext.junit.runners.AndroidJUnit4
import com.google.protobuf.ByteString
import com.google.protobuf.MessageLite
import com.iconnect.protocol.Challenge
import com.iconnect.protocol.Frame
import com.iconnect.protocol.Notice
import com.iconnect.protocol.Registration
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import okhttp3.Request
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.ByteString.Companion.toByteString
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class CoordinatorCompatibilityTest {
    private class Coordinator : WebSocket.Factory {
        val attempts = Channel<Connection>(8)
        override fun newWebSocket(request: Request, listener: WebSocketListener): WebSocket =
            Connection(request, listener).also { check(attempts.trySend(it).isSuccess) }
    }

    private class Connection(private val request: Request, private val listener: WebSocketListener) : WebSocket {
        val sent = Channel<Frame>(8)
        @Volatile var canceled = false
            private set

        override fun request() = request
        override fun queueSize() = 0L
        override fun send(text: String) = false
        override fun send(bytes: okio.ByteString) = !canceled && sent.trySend(Rules.parse(bytes.toByteArray())).isSuccess
        override fun close(code: Int, reason: String?): Boolean { cancel(); return true }
        override fun cancel() { canceled = true }

        fun deliver(kind: String, payload: MessageLite, version: Int = 1) {
            val bytes = Frame.newBuilder().setVersion(version).setKind(kind).setPayload(payload.toByteString()).build().toByteArray()
            listener.onMessage(this, bytes.toByteString())
        }
    }

    private suspend fun waitFor(condition: () -> Boolean) {
        withTimeout(5_000) { while (!condition()) delay(10) }
    }

    @Test fun registrationRequiresOneChallengeAndOneAcknowledgementPerSocket() = runBlocking {
        val fixture = ActivityControlsTest.Fixture()
        val coordinator = Coordinator()
        val identity = LocalIdentity()
        val legacyNotice = Notice.newBuilder().setMessage(identity.id).build()
        val challenge = Challenge.newBuilder().setNonce(ByteString.copyFrom(ByteArray(32) { it.toByte() })).build()
        try {
            fixture.open(coordinator)
            fixture.main {
                fixture.model.setServer("https://coordinator.example")
                fixture.model.setSharePresence(true)
            }
            waitFor { fixture.model.state.value.sharePresence && !fixture.model.state.value.presenceSaving }
            suspend fun connect(): Connection {
                fixture.main { fixture.model.connect() }
                return withTimeout(5_000) { coordinator.attempts.receive() }
            }
            suspend fun sign(connection: Connection) {
                connection.deliver("challenge", challenge)
                val frame = withTimeout(5_000) { connection.sent.receive() }
                assertEquals("register", frame.kind)
                val registration = Registration.parseFrom(frame.payload)
                assertTrue(registration.signalStatus)
                assertEquals(identity.id, Rules.id(registration.identity.publicKey.toByteArray()))
                assertTrue(Rules.verify(identity.publicKey, Rules.registration(challenge.nonce.toByteArray()), registration.signature.toByteArray()))
            }
            suspend fun rejected(connection: Connection, message: String) {
                waitFor { connection.canceled && fixture.model.state.value.status == message }
                assertFalse(fixture.model.state.value.connecting)
                assertFalse(fixture.model.state.value.connected)
                assertFalse(fixture.model.state.value.authenticated)
                assertFalse(fixture.model.state.value.presenceSupported)
                assertTrue(connection.sent.tryReceive().isFailure)
            }
            val unexpected = "Unexpected coordinator registration message."
            val premature = connect()
            premature.deliver("registered", legacyNotice)
            rejected(premature, unexpected)

            val repeatedChallenge = connect()
            sign(repeatedChallenge)
            repeatedChallenge.deliver("challenge", challenge)
            rejected(repeatedChallenge, unexpected)

            val newer = connect()
            newer.deliver("challenge", challenge, version = 2)
            rejected(newer, "Incompatible iConnect protocol version. Use matching releases.")

            val legacy = connect()
            premature.deliver("registered", legacyNotice)
            sign(legacy)
            legacy.deliver("registered", legacyNotice)
            waitFor { fixture.model.state.value.connected }
            assertFalse(fixture.model.state.value.authenticated)
            assertFalse(fixture.model.state.value.presenceSupported)
            assertTrue(legacy.sent.tryReceive().isFailure)

            legacy.deliver("registered", legacyNotice.toBuilder().addCapabilities(PresenceTracker.CAPABILITY).build())
            rejected(legacy, unexpected)
        } finally { fixture.dispose() }
    }
}
