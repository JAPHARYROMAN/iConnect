package com.iconnect.proof

import android.content.Context
import com.google.protobuf.ByteString
import com.google.protobuf.MessageLite
import com.iconnect.protocol.*
import kotlinx.coroutines.*
import kotlinx.coroutines.channels.Channel
import org.webrtc.*
import java.io.File
import java.io.FileOutputStream
import java.nio.ByteBuffer
import java.nio.file.Files
import java.security.MessageDigest
import java.security.SecureRandom
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.coroutines.resume

/** One authenticated direct DataChannel; storage is separately owner-authorized. */
class RtcTestSession(
    context: Context,
    private val scope: CoroutineScope,
    private val identity: SessionIdentity,
    val sessionId: String,
    private val peerKey: ByteArray,
    private val emitSignal: (String, String, String) -> Unit,
    private val log: (String) -> Unit,
    private val onAuthenticated: (Boolean) -> Unit,
    stun: String? = null,
    private val fileService: AndroidFileService? = null,
    private val available: () -> Boolean = { true },
    fileRequestAllowed: (FileRequest) -> Boolean = { available() },
    val initiator: Boolean = false,
    private val activity:(PhoneActivityEvent)->Unit={},
    private val traffic:PhoneFileTrafficPolicy?=null,
) {
    private val events = Channel<suspend () -> Unit>(64)
    private val failed = AtomicBoolean(false)
    @Volatile private var closed = false
    private val ending = AtomicBoolean()
    private var authenticated = false
    private var helloSent = false
    private var proofSent = false
    private var proofAccepted = false
    private var readySent = false
    private var remoteReady = false
    private var pathVerification: Job? = null
    private val nonce = ByteArray(32).also { SecureRandom().nextBytes(it) }
    private var remoteNonce: ByteArray? = null
    private var offerFingerprint = ""
    private var answerFingerprint = ""
    private var descriptionSent = false
    private val pendingCandidates = mutableListOf<IceCandidate>()
    private val inbox = File(context.filesDir, "inbox")
    private var dataChannel: DataChannel? = null
    private var receiveStart: TransferStart? = null
    private var receiveStream: FileOutputStream? = null
    private var receiveDigest: MessageDigest? = null
    private var receiveOffset = 0L
    private var receivedFiles = 0
    private val capabilities = mutableSetOf<String>()
    private val canRequestFiles get() = isAuthenticated && RemoteFiles.CAPABILITY in capabilities
    val acceptsFolderBatches get() = canRequestFiles && UploadBatchRules.CAPABILITY in capabilities
    val acceptsPublishedBatchChecks get() = acceptsFolderBatches && UploadBatchRules.PUBLISHED_CHECK_CAPABILITY in capabilities
    val acceptsPublishedUploadChecks get() = canRequestFiles && UploadRules.CAPABILITY in capabilities &&
        UploadRules.PUBLISHED_CHECK_CAPABILITY in capabilities
    private val remoteFiles = RemoteFiles(::send, scope, Rules.id(peerKey), fileService, ::fail, requestAllowed = fileRequestAllowed,traffic=traffic)
    private var sendId: String? = null
    private var ack: CompletableDeferred<Long>? = null
    private var result: CompletableDeferred<TransferResult>? = null
    private var sendJob: Job? = null
    private val eventJob = scope.launch {
        for (event in events) {
            try { event() } catch (e: Exception) { fail(e.message ?: "Peer protocol failed.") }
        }
    }
    private val factory: PeerConnectionFactory
    private val pc: PeerConnection
    private val authTimeout: Job
    val isClosed get() = closed
    val isAuthenticated get() = authenticated && !closed

    init {
        val configuredStun=Rules.stun(stun?:"")
        synchronized(RtcTestSession::class.java) {
            if (!initialized) {
                PeerConnectionFactory.initialize(PeerConnectionFactory.InitializationOptions
                    .builder(context.applicationContext).createInitializationOptions())
                initialized = true
            }
        }
        factory = PeerConnectionFactory.builder().createPeerConnectionFactory()
        val servers = if (configuredStun.isEmpty()) emptyList() else listOf(PeerConnection.IceServer.builder(configuredStun).createIceServer())
        val config = PeerConnection.RTCConfiguration(servers).apply {
            sdpSemantics = PeerConnection.SdpSemantics.UNIFIED_PLAN
            tcpCandidatePolicy = PeerConnection.TcpCandidatePolicy.DISABLED
            continualGatheringPolicy = PeerConnection.ContinualGatheringPolicy.GATHER_ONCE
        }
        pc = requireNotNull(factory.createPeerConnection(config, object : PeerConnection.Observer {
            override fun onSignalingChange(state: PeerConnection.SignalingState?) = Unit
            override fun onIceConnectionChange(state: PeerConnection.IceConnectionState?) {
                if (state in listOf(PeerConnection.IceConnectionState.FAILED, PeerConnection.IceConnectionState.DISCONNECTED))
                    dispatch { fail("Direct connection ended. Reconnect from either device.") }
            }
            override fun onIceConnectionReceivingChange(receiving: Boolean) = Unit
            override fun onIceGatheringChange(state: PeerConnection.IceGatheringState?) = Unit
            override fun onIceCandidate(candidate: IceCandidate) = dispatch {
                Rules.candidate(candidate.sdp)
                if (descriptionSent) emitSignal("candidate", candidate.sdp, candidate.sdpMid ?: "0")
                else { require(pendingCandidates.size < 64); pendingCandidates += candidate }
            }
            override fun onIceCandidatesRemoved(candidates: Array<out IceCandidate>?) = Unit
            override fun onAddStream(stream: MediaStream?) = dispatch { fail("Media streams are unsupported.") }
            override fun onRemoveStream(stream: MediaStream?) = Unit
            override fun onRenegotiationNeeded() = Unit
            override fun onDataChannel(channel: DataChannel) = dispatch { attach(channel) }
            override fun onAddTrack(receiver: RtpReceiver?, streams: Array<out MediaStream>?) =
                dispatch { fail("Media tracks are unsupported.") }
        }))
        authTimeout = scope.launch {
            delay(30_000)
            if (!authenticated) fail("Direct connection timed out. No relay fallback is configured.")
        }
        observe(PhoneActivityOutcome.REQUESTED)
    }

    private fun dispatch(action: suspend () -> Unit) {
        if (closed) return
        if (!events.trySend(action).isSuccess && failed.compareAndSet(false, true))
            scope.launch { fail("Peer event queue exceeded.") }
    }
    private open inner class Observer : SdpObserver {
        override fun onCreateSuccess(description: SessionDescription?) = Unit
        override fun onSetSuccess() = Unit
        override fun onCreateFailure(message: String?) = dispatch { fail("Could not create the connection description.") }
        override fun onSetFailure(message: String?) = dispatch { fail("Connection description was rejected.") }
    }
    fun acceptOffer(sdp: String) {
        check(!closed && !initiator && offerFingerprint.isEmpty())
        offerFingerprint = Rules.fingerprint(sdp)
        pc.setRemoteDescription(object : Observer() {
            override fun onSetSuccess() = dispatch {
                pc.createAnswer(object : Observer() {
                    override fun onCreateSuccess(description: SessionDescription?) = dispatch {
                        pc.setLocalDescription(object : Observer() {
                            override fun onSetSuccess() = dispatch {
                                val local = requireNotNull(pc.localDescription).description
                                answerFingerprint = Rules.fingerprint(local)
                                emitSignal("answer", local, "")
                                descriptionSent = true
                                pendingCandidates.forEach { emitSignal("candidate", it.sdp, it.sdpMid ?: "0") }
                                pendingCandidates.clear()
                            }
                        }, requireNotNull(description))
                    }
                }, MediaConstraints())
            }
        }, SessionDescription(SessionDescription.Type.OFFER, sdp))
    }
    fun offer() {
        check(!closed && initiator && offerFingerprint.isEmpty())
        attach(requireNotNull(pc.createDataChannel("iconnect-v1",DataChannel.Init().apply { ordered=true })))
        pc.createOffer(object : Observer() {
            override fun onCreateSuccess(description: SessionDescription?) = dispatch {
                pc.setLocalDescription(object : Observer() {
                    override fun onSetSuccess() = dispatch {
                        val local=requireNotNull(pc.localDescription).description; offerFingerprint=Rules.fingerprint(local)
                        emitSignal("offer",local,""); descriptionSent=true
                        pendingCandidates.forEach { emitSignal("candidate",it.sdp,it.sdpMid?:"0") }; pendingCandidates.clear()
                    }
                },requireNotNull(description))
            }
        },MediaConstraints())
    }
    fun acceptAnswer(sdp: String) {
        check(!closed && initiator && offerFingerprint.isNotEmpty() && answerFingerprint.isEmpty())
        answerFingerprint=Rules.fingerprint(sdp)
        pc.setRemoteDescription(object : Observer() {},SessionDescription(SessionDescription.Type.ANSWER,sdp))
    }
    fun addCandidate(candidate: String, mid: String) {
        Rules.candidate(candidate)
        require(pc.addIceCandidate(IceCandidate(mid, 0, candidate))) { "ICE candidate rejected." }
    }
    private fun attach(channel: DataChannel) {
        require(dataChannel == null && channel.label() == "iconnect-v1") { "Unexpected data channel." }
        dataChannel = channel
        channel.registerObserver(object : DataChannel.Observer {
            override fun onBufferedAmountChange(previousAmount: Long) = Unit
            override fun onStateChange() = dispatch {
                if (channel.state() == DataChannel.State.OPEN) startAuthentication()
                else if (channel.state() == DataChannel.State.CLOSED) {
                    report("Peer closed the channel."); finish(if(authenticated) PhoneActivityOutcome.ENDED else PhoneActivityOutcome.FAILED)
                }
            }
            override fun onMessage(buffer: DataChannel.Buffer) {
                if (!buffer.binary || buffer.data.remaining() !in 1..Rules.MAX_FRAME) {
                    dispatch { fail("Invalid binary channel frame.") }; return
                }
                val bytes = ByteArray(buffer.data.remaining()).also { buffer.data.get(it) }
                dispatch { handle(Rules.parse(bytes)) }
            }
        })
        if (channel.state() == DataChannel.State.OPEN) startAuthentication()
    }
    private fun startAuthentication() {
        if (helloSent) return
        helloSent = true
        report("Direct data channel opened. Waiting for device authentication.")
        send("hello", SessionHello.newBuilder().setDeviceId(identity.id).setNonce(ByteString.copyFrom(nonce))
            .addCapabilities(RemoteFiles.CAPABILITY).addCapabilities(READY_CAPABILITY).addCapabilities(StorageRules.DOWNLOAD_CAPABILITY).addCapabilities(MutationRules.CAPABILITY)
            .also {if(fileService?.supportsIncoming==true) it.addCapabilities(UploadRules.CAPABILITY)
                .addCapabilities(UploadRules.PUBLISHED_CHECK_CAPABILITY).addCapabilities(UploadBatchRules.CAPABILITY)
                if(fileService?.supportsCreatedDirectoryReferences==true) it.addCapabilities(MutationRules.CREATED_FOLDERS_CAPABILITY)}.build())
    }
    private fun transcript(): ByteArray = Rules.session(sessionId, if(initiator) identity.id else Rules.id(peerKey), if(initiator) Rules.id(peerKey) else identity.id,
        offerFingerprint.also { require(it.isNotEmpty()) }, answerFingerprint.also { require(it.isNotEmpty()) },
        if(initiator) nonce else requireNotNull(remoteNonce), if(initiator) requireNotNull(remoteNonce) else nonce)

    private suspend fun handle(frame: Frame) {
        when (frame.kind) {
            "hello" -> {
                val hello = SessionHello.parseFrom(frame.payload)
                require(remoteNonce == null && hello.deviceId == Rules.id(peerKey) && hello.nonce.size() == 32) {"Unexpected peer identity or challenge."}
                remoteNonce = hello.nonce.toByteArray()
                require(hello.capabilitiesCount <= 16 && hello.capabilitiesList.all { it.length in 1..64 })
                capabilities.addAll(hello.capabilitiesList)
                require(READY_CAPABILITY in capabilities) { "Update the computer app to support mutual session readiness." }
                send("proof", SessionProof.newBuilder().setSignature(ByteString.copyFrom(identity.sign(transcript()))).build())
                proofSent = true
                return
            }
            "proof" -> {
                val proof = SessionProof.parseFrom(frame.payload)
                require(proofSent && !proofAccepted && Rules.verify(peerKey, transcript(), proof.signature.toByteArray())) {
                    "Device authentication failed."
                }
                proofAccepted = true
                verifyDirectPath()
                return
            }
            "ready" -> {
                require(proofAccepted && !remoteReady && Notice.parseFrom(frame.payload).message == READY_CAPABILITY)
                remoteReady = true
                publishAuthenticated()
                return
            }
        }
        require(authenticated) { "Authenticate before transferring." }
        if (remoteFiles.handle(frame)) return
        when (frame.kind) {
            "transfer-start" -> {
                traffic?.requireContent()
                check(available()) { "Open iConnect on the phone before starting a new transfer." }
                require(receiveStart == null && ++receivedFiles <= 4) { "The session's test-file receive limit was reached." }
                val start = TransferStart.parseFrom(frame.payload)
                require(Rules.tokenPattern.matches(start.transferId) && start.name == "iconnect-test.bin" &&
                    start.size in 0..Rules.MAX_FILE.toLong() && start.sha256.size() == 32) { "Invalid test file." }
                require(inbox.isDirectory || inbox.mkdirs())
                val temporary = File(inbox, start.transferId + ".part")
                require(temporary.createNewFile()) { "Destination already exists." }
                receiveStream = FileOutputStream(temporary)
                receiveStart = start
                receiveDigest = MessageDigest.getInstance("SHA-256")
                receiveOffset = 0
                send("transfer-ack", TransferAck.newBuilder().setTransferId(start.transferId).setOffset(0).build())
                report("Receiving synthetic test file...")
            }
            "transfer-chunk" -> {
                val chunk = TransferChunk.parseFrom(frame.payload)
                val start = requireNotNull(receiveStart)
                require(chunk.transferId == start.transferId && chunk.offset == receiveOffset &&
                    chunk.data.size() in 1..Rules.CHUNK && receiveOffset + chunk.data.size() <= start.size)
                val bytes = chunk.data.toByteArray()
                traffic?.pace(bytes.size,false)
                requireNotNull(receiveStream).write(bytes)
                requireNotNull(receiveDigest).update(bytes)
                receiveOffset += bytes.size
                send("transfer-ack", TransferAck.newBuilder().setTransferId(start.transferId).setOffset(receiveOffset).build())
            }
            "transfer-finish" -> {
                val finish = TransferFinish.parseFrom(frame.payload)
                val start = requireNotNull(receiveStart)
                require(finish.transferId == start.transferId && receiveOffset == start.size)
                val hash = requireNotNull(receiveDigest).digest()
                require(MessageDigest.isEqual(hash, start.sha256.toByteArray())) { "Checksum mismatch." }
                requireNotNull(receiveStream).apply { flush(); fd.sync(); close() }
                receiveStream = null
                Files.move(File(inbox, start.transferId + ".part").toPath(), File(inbox, start.transferId + ".bin").toPath())
                receiveStart = null
                receiveDigest = null
                send("transfer-result", TransferResult.newBuilder().setTransferId(start.transferId).setSha256(ByteString.copyFrom(hash)).build())
                report("Received ${start.size / 1024} KiB. Verified SHA-256: ${Rules.hex(hash)}")
            }
            "transfer-ack" -> {
                val received = TransferAck.parseFrom(frame.payload)
                require(received.transferId == sendId && ack?.complete(received.offset) == true) { "Unexpected transfer acknowledgement." }
            }
            "transfer-result" -> {
                val received = TransferResult.parseFrom(frame.payload)
                require(received.transferId == sendId && result?.complete(received) == true) { "Unexpected transfer result." }
            }
            else -> error("Unsupported peer operation.")
        }
    }

    private fun verifyDirectPath() {
        pathVerification = scope.launch {
            try {
                withTimeout(10_000) {
                    while (!closed) {
                        val stats = suspendCancellableCoroutine<RTCStatsReport> { continuation ->
                            pc.getStats { if (continuation.isActive) continuation.resume(it) }
                        }
                        val selectedId = stats.statsMap.values.firstOrNull { it.type == "transport" }
                            ?.members?.get("selectedCandidatePairId")
                        val pair = stats.statsMap[selectedId] ?: stats.statsMap.values.firstOrNull {
                            it.type == "candidate-pair" && it.members["state"] == "succeeded" && it.members["nominated"] == true
                        }
                        if (pair != null && pair.members["state"] == "succeeded") {
                            val local = stats.statsMap[pair.members["localCandidateId"]]?.members?.get("candidateType")
                            val remote = stats.statsMap[pair.members["remoteCandidateId"]]?.members?.get("candidateType")
                            require(local in listOf("host", "srflx", "prflx") && remote in listOf("host", "srflx", "prflx")) {
                                "A non-direct path was selected."
                            }
                            send("ready", Notice.newBuilder().setMessage(READY_CAPABILITY).build())
                            readySent = true
                            publishAuthenticated()
                            return@withTimeout
                        }
                        delay(100)
                    }
                }
            } catch (_: TimeoutCancellationException) { fail("Direct path verification timed out. No relay fallback is configured.") }
            catch (e: Exception) { if (e !is CancellationException) fail(e.message ?: "Direct path verification failed.") }
        }
    }
    private fun publishAuthenticated() {
        if (closed || authenticated || !readySent || !remoteReady) return
        authenticated = true
        authTimeout.cancel()
        observe(PhoneActivityOutcome.COMPLETED)
        if(!closed) notifyAuthenticated(true)
        if(!closed) report("Authenticated direct connection. Both devices are ready. No relay.")
    }

    fun sendTestFile() {
        require(authenticated && sendJob?.isActive != true) { "Wait for an authenticated, idle connection." }
        sendJob = scope.launch {
            try {
                traffic?.requireContent()
                val data = ByteArray(256 * 1024).also { SecureRandom().nextBytes(it) }
                val hash = Rules.digest(data)
                val id = Rules.b64(ByteArray(32).also { SecureRandom().nextBytes(it) })
                sendId = id
                result = CompletableDeferred()
                sendAndAck("transfer-start", TransferStart.newBuilder().setTransferId(id).setName("iconnect-test.bin")
                    .setSize(data.size.toLong()).setSha256(ByteString.copyFrom(hash)).build(), 0)
                for (offset in data.indices step Rules.CHUNK) {
                    val length = minOf(Rules.CHUNK, data.size - offset)
                    traffic?.pace(length,true)
                    sendAndAck("transfer-chunk", TransferChunk.newBuilder().setTransferId(id).setOffset(offset.toLong())
                        .setData(ByteString.copyFrom(data, offset, length)).build(), (offset + length).toLong())
                }
                send("transfer-finish", TransferFinish.newBuilder().setTransferId(id).build())
                val completed = withTimeout(15_000) { requireNotNull(result).await() }
                require(MessageDigest.isEqual(hash, completed.sha256.toByteArray())) { "Destination checksum mismatch." }
                report("Sent 256 KiB. Destination verified SHA-256: ${Rules.hex(hash)}")
            } catch (e: CancellationException) { if (!closed) fail("Transfer interrupted. Start a new test after reconnecting.") }
            catch (e: Exception) { fail(e.message ?: "Transfer failed.") }
            finally { sendId = null; ack = null; result = null }
        }
    }
    suspend fun requestFiles(request: FileRequest): FileResponse {
        if(UploadBatchRules.isRpc(request.operation)) require(UploadBatchRules.CAPABILITY in capabilities) {"This peer does not support folder-batch receiving."}
        if(request.operation==FileOperation.VERIFY_PUBLISHED_BATCH) require(UploadBatchRules.PUBLISHED_CHECK_CAPABILITY in capabilities) {"This computer does not support fresh published-batch checks."}
        if(UploadRules.isRpc(request.operation)) require(UploadRules.CAPABILITY in capabilities) {"This computer does not accept incoming file copies. Update it before sending."}
        if(request.operation==FileOperation.VERIFY_PUBLISHED_UPLOAD) require(UploadRules.PUBLISHED_CHECK_CAPABILITY in capabilities) {"This computer does not support a fresh published-copy check."}
        require(authenticated && !closed) { "Connect to the paired computer first." }
        require(RemoteFiles.CAPABILITY in capabilities) { "Update the computer app to use file browsing." }
        if (MutationRules.isRpc(request.operation)) require(MutationRules.CAPABILITY in capabilities) { "Update the computer app to use recoverable file operations." }
        if(request.operation==FileOperation.RESOLVE_CREATED_DIRECTORY) require(MutationRules.CREATED_FOLDERS_CAPABILITY in capabilities) {"This computer cannot recover created-folder references. Update it before resuming a folder copy."}
        if (RecoveryRules.isRpc(request.operation)) require(RecoveryRules.CAPABILITY in capabilities) { "This computer does not offer recoverable deletion. Update it or manage its files locally." }
        if (request.operationValue in 12..15) require(StorageRules.DOWNLOAD_CAPABILITY in capabilities) { "Update the computer app to transfer real files." }
        return remoteFiles.request(request)
    }
    fun suspendStorageAvailability(preserve: Set<String> = emptySet()) { fileService?.suspendAvailability(preserve) }
    fun interruptSource(id: String) { fileService?.interruptSource(id) }
    private suspend fun sendAndAck(kind: String, payload: MessageLite, offset: Long) {
        ack = CompletableDeferred()
        send(kind, payload)
        require(withTimeout(15_000) { requireNotNull(ack).await() } == offset) { "Incorrect transfer progress." }
    }
    private fun send(kind: String, payload: MessageLite) {
        check(!closed && (dataChannel?.bufferedAmount() ?: Long.MAX_VALUE) <= 1024 * 1024 &&
            dataChannel?.send(DataChannel.Buffer(ByteBuffer.wrap(Rules.pack(kind, payload)), true)) == true) {
            "Peer channel is unavailable."
        }
    }
    private fun fail(message: String) {
        if (closed) return
        report(message)
        finish(PhoneActivityOutcome.FAILED)
    }
    private fun report(message:String) {try {log(message)} catch(_:Exception) {}}
    private fun notifyAuthenticated(value:Boolean) {try {onAuthenticated(value)} catch(_:Exception) {}}
    private fun observe(outcome:PhoneActivityOutcome)=recordPhoneActivity(activity,PhoneActivityEvent(PhoneActivityKind.DIRECT_CONNECTION,outcome,Rules.id(peerKey)))
    fun close(reason:PhoneConnectionEnd=PhoneConnectionEnd.OWNER)=finish(reason.outcome)
    private fun finish(outcome:PhoneActivityOutcome) {
        if (!ending.compareAndSet(false,true)) return
        closed = true
        authenticated = false
        observe(outcome)
        notifyAuthenticated(false)
        authTimeout.cancel()
        pathVerification?.cancel()
        sendJob?.cancel()
        ack?.cancel(); result?.cancel()
        remoteFiles.close()
        receiveStream?.close(); receiveStream = null
        dataChannel?.unregisterObserver(); dataChannel?.close(); dataChannel?.dispose()
        pc.close(); pc.dispose(); factory.dispose()
        events.close(); eventJob.cancel()
    }
    companion object { private var initialized = false; private const val READY_CAPABILITY = "ready-v1" }
}
