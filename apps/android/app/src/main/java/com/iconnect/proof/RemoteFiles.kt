package com.iconnect.proof

import com.iconnect.protocol.*
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit
import kotlinx.coroutines.withTimeout
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.Job
import kotlinx.coroutines.coroutineScope
import java.security.SecureRandom
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicBoolean

class RemoteFiles(private val send: (String, com.google.protobuf.MessageLite) -> Unit,
    scope: CoroutineScope, private val peerId: String, private val service: AndroidFileService?,
    onFailure: (String) -> Unit, private val available: () -> Boolean = { true },
    private val requestAllowed: (FileRequest) -> Boolean = { true },private val traffic:PhoneFileTrafficPolicy?=null) {
    private val lifetime=SupervisorJob(scope.coroutineContext[Job])
    private val closed=AtomicBoolean()
    private val waiters=ConcurrentHashMap.newKeySet<Job>()
    private val credits = Semaphore(16)
    private val requests = ConcurrentHashMap<String, CompletableDeferred<FileResponse>>()
    private val incoming = Channel<FileRequest>(16)
    private val worker = scope.launch(lifetime) {
        try {
            for (request in incoming) {
                val response = try {
                    traffic?.beforeRequest(request,false)
                    val value=if(available() && requestAllowed(request)) service?.execute(peerId,request) else null
                    if(value!=null && !value.content.isEmpty) traffic?.requireContent()
                    value
                } catch(e:StorageFailure) {FileResponse.newBuilder().setRequestId(request.requestId).setStatus(e.status).setMessage(e.message?:"Remote content was refused.").build()}
                val allowed = if (available() && requestAllowed(request)) response else null
                send("file-response", allowed ?: FileResponse.newBuilder().setRequestId(request.requestId)
                    .setStatus(FileStatus.ACCESS_DENIED).setMessage("Phone storage is paused or unavailable for this request.").build()
                )
            }
        } catch (e: Exception) { if (e !is CancellationException) onFailure("The document provider session ended.") }
        finally {closed.set(true); waiters.forEach {it.cancel()}; service?.close() }
    }
    suspend fun request(request: FileRequest): FileResponse = coroutineScope {
        val waiter=coroutineContext[Job]!!; waiters.add(waiter); if(closed.get()) waiter.cancel()
        try {credits.withPermit {
        traffic?.beforeRequest(request,true)
        val id = Rules.b64(ByteArray(32).also { SecureRandom().nextBytes(it) })
        val completion = CompletableDeferred<FileResponse>()
        requests[id] = completion
        try {
            traffic?.requireRequest(request)
            send("file-request", request.toBuilder().setRequestId(id).build())
            withTimeout(30_000) { completion.await() }
        } finally { requests.remove(id) }
        }} finally {waiters.remove(waiter)}
    }
    fun handle(frame: Frame): Boolean {
        if (frame.kind == "file-response") {
            val response = FileResponse.parseFrom(frame.payload)
            PhoneTransferManifests.validateResponse(response)
            requests.remove(response.requestId)?.complete(response)
            return true
        }
        if (frame.kind != "file-request") return false
        val request = FileRequest.parseFrom(frame.payload)
        require(Rules.tokenPattern.matches(request.requestId))
        service?.interruptPublishedBatchCheck(peerId,request)
        if (!incoming.trySend(request).isSuccess) send("file-response", FileResponse.newBuilder().setRequestId(request.requestId)
            .setStatus(FileStatus.RESOURCE_EXHAUSTED).setMessage("Too many document operations. Retry shortly.").build())
        return true
    }
    fun close() {
        closed.set(true); waiters.forEach {it.cancel()}
        lifetime.cancel()
        requests.values.forEach { it.cancel() }; requests.clear()
        incoming.close(); worker.cancel(); service?.close()
    }
    companion object { const val CAPABILITY = "files-v1" }
}
