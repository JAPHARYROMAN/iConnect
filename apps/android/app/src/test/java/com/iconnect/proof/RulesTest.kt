package com.iconnect.proof

import com.google.protobuf.ByteString
import com.iconnect.protocol.Frame
import com.iconnect.protocol.SessionHello
import org.junit.Assert.*
import org.junit.Test

class RulesTest {
    @Test fun stunConfigurationMatchesSharedDirectOnlyUriVectors() {
        assertEquals("",Rules.stun(""))
        requireNotNull(javaClass.getResourceAsStream("/stun-uris.tsv")).bufferedReader().useLines { lines -> lines.forEach { line ->
            val parts=line.split('\t',limit=2); val value=parts[1]
            if(parts[0]=="valid") assertEquals(value,Rules.stun(value)) else assertThrows(value,IllegalArgumentException::class.java) {Rules.stun(value)}
        } }
        listOf("stun:host\n","stun:host\r","stun:ho\u0000st"," stun:host","stun:host ","stun:"+"a".repeat(64)+".example","stun:"+"a".repeat(257)).forEach { value ->
            assertThrows(IllegalArgumentException::class.java) {Rules.stun(value)}
        }
    }
    @Test fun recoveryResponsesRejectInvalidStatesTimesAndDisplayControls() {
        val id = MutationRules.newId()
        val item = com.iconnect.protocol.DeletedInfo.newBuilder().setId(id).setName("saved.txt").setDeletedAt(MutationRules.created(id))
            .setState(com.iconnect.protocol.DeletedState.DELETED_STORED).setCanRestore(true).build()
        val response = com.iconnect.protocol.FileResponse.newBuilder().setRequestId(StorageRules.token()).addDeletedItems(item).build()
        PhoneTransferManifests.validateResponse(response)
        listOf(item.toBuilder().setName("bad\u001bname").build(), item.toBuilder().setDeletedAt(item.deletedAt + 1).build(),
            item.toBuilder().setStateValue(99).build(), item.toBuilder().setState(com.iconnect.protocol.DeletedState.DELETED_RESTORED).build()).forEach { invalid ->
            assertThrows(IllegalArgumentException::class.java) { PhoneTransferManifests.validateResponse(response.toBuilder().clearDeletedItems().addDeletedItems(invalid).build()) }
        }
        assertThrows(IllegalArgumentException::class.java) { PhoneTransferManifests.validateResponse(response.toBuilder().addAllDeletedItems(List(32) { item }).build()) }
    }
    @Test fun protocolRejectsUnsupportedVersionsAndOversizedMessages() {
        assertThrows(IllegalArgumentException::class.java) { Rules.parse(Frame.newBuilder().setVersion(2).setKind("hello").build().toByteArray()) }
        assertThrows(IllegalArgumentException::class.java) { Rules.parse(ByteArray(Rules.MAX_FRAME + 1)) }
    }
    @Test fun relayCandidatesAndWeakFingerprintsAreRejected() {
        assertThrows(IllegalArgumentException::class.java) { Rules.candidate("candidate:1 1 UDP 123 192.0.2.1 1234 typ relay") }
        val fingerprint = List(32) { "ab" }.joinToString(":")
        val sdp = "v=0\r\nm=application 9 UDP/DTLS/SCTP webrtc-datachannel\r\na=fingerprint:sha-256 $fingerprint\r\n"
        assertEquals("sha-256 $fingerprint", Rules.fingerprint(sdp))
        assertThrows(IllegalArgumentException::class.java) { Rules.fingerprint(sdp.replace("sha-256", "sha-1")) }
    }
    @Test fun developmentHttpIsRestrictedAndReleaseRequiresHttps() {
        assertEquals("10.0.2.2", Rules.server("http://10.0.2.2:5078", true).host)
        assertThrows(IllegalArgumentException::class.java) { Rules.server("http://example.com", true) }
        assertThrows(IllegalArgumentException::class.java) { Rules.server("http://10.0.2.2:5078", false) }
        assertThrows(IllegalArgumentException::class.java) { Rules.server("https://example.com/path", false) }
        assertEquals("https://example.com/", Rules.server("https://example.com", false).toString())
    }
    @Test fun frameRoundTripUsesTheSharedProtobufContract() {
        val hello = SessionHello.newBuilder().setDeviceId("test").setNonce(ByteString.copyFrom(ByteArray(32) { it.toByte() })).build()
        val frame = Rules.parse(Rules.pack("hello", hello))
        assertEquals(1, frame.version)
        assertEquals("hello", frame.kind)
        assertEquals(hello, SessionHello.parseFrom(frame.payload))
    }
}
