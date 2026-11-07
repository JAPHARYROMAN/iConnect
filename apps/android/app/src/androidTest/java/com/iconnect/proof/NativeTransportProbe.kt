package com.iconnect.proof

import android.app.Activity
import android.app.Instrumentation
import android.os.Bundle
import com.iconnect.protocol.FileOperation
import com.iconnect.protocol.FileRequest
import com.iconnect.protocol.FileStatus
import kotlinx.coroutines.*
import org.json.JSONObject
import java.io.File
import java.security.MessageDigest
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicReference

/** Explicit native transport probe; ordinary JUnit discovery never starts it. */
class NativeTransportProbe : Instrumentation() {
    private lateinit var options: Bundle

    override fun onCreate(arguments: Bundle?) {
        super.onCreate(arguments)
        options = arguments ?: Bundle()
        start()
    }

    override fun onStart() {
        try {
            val result = runBlocking { withTimeout(150_000) { exercise() } }
            finish(Activity.RESULT_OK, Bundle().apply { putString("nativeTransportResult", result.toString()) })
        } catch (failure: Throwable) {
            finish(Activity.RESULT_CANCELED, Bundle().apply {
                putString("stream", "Native transport probe failed: ${failure.stackTraceToString()}")
            })
        }
    }

    private fun status(value: JSONObject) = sendStatus(0, Bundle().apply {
        putString("nativeProbe", Rules.b64(value.toString().toByteArray(Charsets.UTF_8)))
    })

    private suspend fun exercise(): JSONObject = coroutineScope {
        val run = requireNotNull(options.getString("run"))
        val role = requireNotNull(options.getString("role"))
        val hostKey = Rules.decode(requireNotNull(options.getString("hostKey")))
        require(Rules.tokenPattern.matches(run) && role in listOf("windows", "android") && hostKey.size in 64..256)
        val directory = File(targetContext.filesDir, "native-transport-probes/$run")
        check(!directory.exists() && directory.mkdirs())
        val inbox = File(targetContext.filesDir, "inbox")
        val originalNames = inbox.listFiles().orEmpty().map { it.name }.toSet()
        val identity = LocalIdentity()
        val messages = CopyOnWriteArrayList<String>()
        val sentHash = AtomicReference<String?>(null)
        val signals = AtomicInteger()
        val finishRequested = CompletableDeferred<Unit>()
        val ready = CompletableDeferred<Unit>()
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
        lateinit var peer: RtcTestSession
        status(JSONObject().put("event", "identity").put("key", Rules.b64(identity.publicKey)).put("device", identity.id)
            .put("pid", android.os.Process.myPid()))
        withContext(Dispatchers.Main.immediate) {
            peer = RtcTestSession(targetContext, scope, identity, run, hostKey,
                { kind, body, mid ->
                    check(signals.incrementAndGet() <= 128)
                    status(JSONObject().put("event", "signal").put("kind", kind).put("body", body).put("mid", mid))
                },
                { message ->
                    if (messages.size < 128) messages.add(message)
                    val prefix = "Sent 256 KiB. Destination verified SHA-256: "
                    if (message.startsWith(prefix)) {
                        val hash = message.removePrefix(prefix)
                        check(hash.matches(Regex("[a-f0-9]{64}")))
                        sentHash.set(hash)
                    }
                }, { if (it) ready.complete(Unit) else ready.completeExceptionally(IllegalStateException(messages.joinToString(" | "))) },
                initiator = role == "android")
            if (role == "android") peer.offer()
        }
        val commands = launch(Dispatchers.IO) {
            var sequence = 0
            while (isActive) {
                val command = File(directory, "$sequence.command")
                if (!command.exists()) { delay(15); continue }
                check(command.length() in 1..196_608)
                val value = JSONObject(String(Rules.decode(command.readText(Charsets.UTF_8).trim()), Charsets.UTF_8))
                check(value.getInt("sequence") == sequence && ++sequence <= 132)
                when (value.getString("op")) {
                    "signal" -> withContext(Dispatchers.Main.immediate) {
                        val body = value.getString("body")
                        check(body.length <= 65_536)
                        when (value.getString("kind")) {
                            "offer" -> peer.acceptOffer(body)
                            "answer" -> peer.acceptAnswer(body)
                            "candidate" -> peer.addCandidate(body, value.getString("mid"))
                            else -> error("Unexpected fixture signal.")
                        }
                    }
                    "finish" -> { finishRequested.complete(Unit); return@launch }
                    else -> error("Unexpected fixture command.")
                }
            }
        }
        var created: File? = null
        try {
            withTimeout(40_000) { ready.await() }
            status(JSONObject().put("event", "authenticated"))
            val denied = withContext(Dispatchers.Main.immediate) {
                peer.requestFiles(FileRequest.newBuilder().setOperation(FileOperation.LIST_ROOTS).build())
            }
            check(denied.status == FileStatus.ACCESS_DENIED && denied.entriesCount == 0 && denied.content.isEmpty)
            withContext(Dispatchers.Main.immediate) { peer.sendTestFile() }
            withTimeout(45_000) {
                while (true) {
                    check(!peer.isClosed) { messages.joinToString(" | ") }
                    val added = inbox.listFiles().orEmpty().filter { it.name !in originalNames && it.name.endsWith(".bin") }
                    if (sentHash.get() != null && added.isNotEmpty()) {
                        check(added.size == 1)
                        created = added.single()
                        break
                    }
                    delay(15)
                }
            }
            val received = requireNotNull(created)
            check(received.length() == 256L * 1024 && Rules.tokenPattern.matches(received.name.removeSuffix(".bin")))
            val receivedHash = received.inputStream().use { Rules.hex(MessageDigest.getInstance("SHA-256").digest(it.readBytes())) }
            val result = JSONObject().put("event", "verified").put("run", run).put("role", role)
                .put("sentHash", requireNotNull(sentHash.get())).put("receivedHash", receivedHash).put("bytes", received.length())
            status(result)
            withTimeout(30_000) { finishRequested.await() }
            result
        } finally {
            withContext(NonCancellable) {
                commands.cancelAndJoin()
                withContext(Dispatchers.Main.immediate) { peer.close() }
                scope.cancel(); scope.coroutineContext.job.join()
                // Only the exact new synthetic payload observed by this run is removed.
                created?.let { file -> check(file.parentFile == inbox && file.name !in originalNames); check(file.delete()) }
            }
        }
    }
}
