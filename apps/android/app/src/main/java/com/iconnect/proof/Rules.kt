package com.iconnect.proof

import com.google.protobuf.MessageLite
import com.iconnect.protocol.*
import java.net.URI
import java.security.KeyFactory
import java.security.MessageDigest
import java.security.Signature
import java.security.interfaces.ECPublicKey
import java.security.spec.ECGenParameterSpec
import java.security.AlgorithmParameters
import java.security.spec.ECParameterSpec
import java.security.spec.X509EncodedKeySpec
import java.util.Base64

internal class UnsupportedProtocolVersionException : IllegalArgumentException() {
    override val message = "Incompatible iConnect protocol version. Use matching releases."
}

object Rules {
    private const val HEX_DIGITS="0123456789abcdef"
    const val MAX_FRAME = 64 * 1024
    const val CHUNK = 16 * 1024
    const val MAX_FILE = 1024 * 1024
    val tokenPattern = Regex("^[A-Za-z0-9_-]{43}$")
    fun stun(value: String): String {
        val message="Use stun:host[:port] or stun:[IPv6][:port], or leave STUN empty."
        require(value.length<=256) { message }; if(value.isEmpty()) return value
        val match=Regex("^stun:(\\[[0-9A-Fa-f:.]+\\]|[A-Za-z0-9.-]+)(?::([0-9]{1,5}))?$").matchEntire(value)
        require(match!=null) { message }; val host=match.groupValues[1]; val port=match.groupValues[2]
        require(port.isEmpty() || port.toIntOrNull() in 1..65535) { message }
        if(host.startsWith("[")) require(runCatching { URI("stun://$host").host!=null }.getOrDefault(false)) { message }
        else {
            val labels=host.removeSuffix(".").split('.')
            require(host.length<=253 && labels.all { it.length in 1..63 && !it.startsWith('-') && !it.endsWith('-') }) { message }
            if(host.all { it in '0'..'9' || it=='.' }) require(labels.size==4 && labels.all { it.toIntOrNull() in 0..255 && (it.length==1 || it[0]!='0') }) { message }
        }
        return value
    }
    fun b64(bytes: ByteArray): String = Base64.getUrlEncoder().withoutPadding().encodeToString(bytes)
    fun decode(value: String): ByteArray = Base64.getUrlDecoder().decode(value)
    fun digest(bytes: ByteArray): ByteArray = MessageDigest.getInstance("SHA-256").digest(bytes)
    fun hex(bytes: ByteArray): String {
        val result=CharArray(bytes.size*2)
        bytes.forEachIndexed {index,byte->
            val value=byte.toInt() and 255
            result[index*2]=HEX_DIGITS[value ushr 4]
            result[index*2+1]=HEX_DIGITS[value and 15]
        }
        return String(result)
    }
    fun id(publicKey: ByteArray): String {
        val key = KeyFactory.getInstance("EC").generatePublic(X509EncodedKeySpec(publicKey)) as ECPublicKey
        val expected = AlgorithmParameters.getInstance("EC").apply { init(ECGenParameterSpec("secp256r1")) }
            .getParameterSpec(ECParameterSpec::class.java)
        require(key.params.curve == expected.curve && key.params.generator == expected.generator &&
            key.params.order == expected.order && key.params.cofactor == expected.cofactor &&
            publicKey.contentEquals(key.encoded)) { "Canonical P-256 identity required." }
        return hex(digest(publicKey))
    }
    fun verify(key: ByteArray, transcript: ByteArray, signature: ByteArray): Boolean = try {
        id(key)
        Signature.getInstance("SHA256withECDSA").run {
            initVerify(KeyFactory.getInstance("EC").generatePublic(X509EncodedKeySpec(key)))
            update(transcript)
            verify(signature)
        }
    } catch (_: Exception) { false }
    private fun transcript(purpose: String, vararg fields: String): ByteArray {
        require(fields.none { it.contains('\n') || it.contains('\r') })
        return ("iconnect/$purpose/v1\n" + fields.joinToString("\n")).toByteArray(Charsets.UTF_8)
    }
    fun registration(nonce: ByteArray) = transcript("register", b64(nonce))
    fun pairing(p: Pairing) = transcript("pair-approve", p.transactionId, p.token,
        b64(p.host.publicKey.toByteArray()), b64(p.guest.publicKey.toByteArray()), p.expiresAt.toString())
    fun code(p: Pairing): String {
        val hash = digest(pairing(p))
        val number = java.nio.ByteBuffer.wrap(hash).int.toLong() and 0xffffffffL
        return (number % 1_000_000).toString().padStart(6, '0')
    }
    fun signal(s: Signal) = transcript("signal", s.sessionId, s.fromDevice, s.toDevice, s.kind,
        b64(s.body.toByteArray(Charsets.UTF_8)), b64(s.mid.toByteArray(Charsets.UTF_8)),
        b64(s.nonce.toByteArray()), s.createdAt.toString())
    fun session(id: String, host: String, guest: String, offer: String, answer: String,
        hostNonce: ByteArray, guestNonce: ByteArray) = transcript("session", id, host, guest,
        offer, answer, b64(hostNonce), b64(guestNonce))
    fun pack(kind: String, payload: MessageLite): ByteArray = Frame.newBuilder()
        .setVersion(1).setKind(kind).setPayload(payload.toByteString()).build().toByteArray().also {
            require(it.size <= MAX_FRAME) { "Message exceeds limit." }
        }
    fun parse(bytes: ByteArray): Frame {
        require(bytes.size <= MAX_FRAME) { "Message exceeds limit." }
        return Frame.parseFrom(bytes).also {
            if (it.version != 1) throw UnsupportedProtocolVersionException()
            require(it.kind.length in 1..40) { "Invalid protocol frame." }
        }
    }
    fun candidate(value: String) {
        require(value.length <= 2048 && !value.contains('\r') && !value.contains('\n') &&
            Regex("^(?:candidate:)?\\S+ \\d+ (?:[Uu][Dd][Pp]|[Tt][Cc][Pp]) \\d+ \\S+ \\d+ typ (host|srflx|prflx)(?:\\s.*)?$").matches(value) &&
            Regex("\\btyp\\s").findAll(value).count() == 1) {
            "Only direct ICE candidates are allowed."
        }
    }
    fun fingerprint(sdp: String): String {
        require(sdp.length <= 24 * 1024 && !sdp.contains('\u0000'))
        val lines = sdp.lines()
        require(lines.none { it.startsWith("m=") && !it.startsWith("m=application ") })
        lines.filter { it.startsWith("a=candidate:") }.forEach { candidate(it.substring(2)) }
        val fingerprints = lines.filter { it.startsWith("a=fingerprint:") }
            .map { it.substring(14).trim().lowercase() }.distinct()
        require(fingerprints.size == 1 && Regex("^sha-256 (?:[a-f0-9]{2}:){31}[a-f0-9]{2}$").matches(fingerprints[0])) {
            "Expected one SHA-256 DTLS fingerprint."
        }
        return fingerprints[0]
    }
    fun validateSignal(s: Signal, peerKey: ByteArray, ownId: String) {
        val now = System.currentTimeMillis()
        require(s.fromDevice == id(peerKey) && s.toDevice == ownId && tokenPattern.matches(s.sessionId) &&
            s.nonce.size() == 32 && s.mid.length <= 64 && s.createdAt >= now - 60_000 && s.createdAt <= now + 60_000 &&
            s.kind in listOf("offer", "answer", "candidate") && verify(peerKey, signal(s), s.signature.toByteArray())) {
            "Unauthenticated or expired connection message."
        }
        if (s.kind == "candidate") candidate(s.body) else fingerprint(s.body)
    }
    fun server(value: String, debug: Boolean): URI {
        require(value.length in 1..2048) {"Choose a coordinator address up to 2,048 characters."}
        val uri = URI(value).normalize()
        require(uri.isAbsolute && uri.host != null && uri.userInfo == null && uri.query == null &&
            uri.fragment == null && (uri.path.isNullOrEmpty() || uri.path == "/") &&
            (uri.scheme == "https" || (debug && uri.scheme == "http" && uri.host in listOf("localhost", "127.0.0.1", "10.0.2.2")))) {
            "Use an HTTPS origin. Debug HTTP is limited to emulator/loopback."
        }
        return URI("${uri.scheme}://${uri.rawAuthority}/")
    }
}
