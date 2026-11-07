package com.iconnect.proof

import android.app.Activity
import android.app.Instrumentation
import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.os.Process
import android.provider.DocumentsContract as Docs
import com.google.protobuf.ByteString
import com.iconnect.protocol.*
import kotlinx.coroutines.*
import org.json.JSONObject
import java.io.File
import java.security.MessageDigest

/** Opt-in external-process probe. It is not discovered by the ordinary JUnit suite. */
class ProcessRestartProbe : Instrumentation() {
    private lateinit var options: Bundle
    private val checkpoint = 1024 * 1024L
    private val payload = ByteArray(2 * 1024 * 1024 + 17) { (it * 31 % 251).toByte() }
    private val digest = MessageDigest.getInstance("SHA-256").digest(payload)
    private val hash = Rules.hex(digest)
    private val name = "process-restart.bin"
    private val peer = "a".repeat(64)

    override fun onCreate(arguments: Bundle?) {
        super.onCreate(arguments)
        options = arguments ?: Bundle()
        start()
    }

    override fun onStart() {
        try {
            val result = runBlocking {
                val run = requireNotNull(options.getString("run"))
                require(Rules.tokenPattern.matches(run))
                val directory = File(targetContext.filesDir, "process-restart-probes/$run")
                when (options.getString("phase")) {
                    "seed" -> seed(directory, run)
                    "resume" -> resume(directory, run)
                    else -> error("Choose the explicit seed or resume probe phase.")
                }
            }
            finish(Activity.RESULT_OK, Bundle().apply { putString("processRestartResult", result.toString()) })
        } catch (failure: Throwable) {
            finish(Activity.RESULT_CANCELED, Bundle().apply {
                putString("stream", "Process restart probe failed: ${failure.stackTraceToString()}")
            })
        }
    }

    private inner class Source(val node: String, val grant: String, val ticket: String, val expires: Long) {
        val entry = FileEntry.newBuilder().setId(node).setGrantId(grant).setName(name).setSize(payload.size.toLong())
            .setVersion(hash).setPermissions(StorageRules.READ_ONLY).build()
        val reads = mutableListOf<Long>()
        var prepares = 0
        var beforeRead: suspend (FileRequest) -> Unit = {}

        suspend fun request(request: FileRequest): FileResponse {
            val result = FileResponse.newBuilder().setRequestId(request.requestId)
            when (request.operation) {
                FileOperation.PREPARE_DOWNLOAD, FileOperation.DOWNLOAD_STATUS -> {
                    if (request.operation == FileOperation.PREPARE_DOWNLOAD) {
                        check(request.nodeId == node && request.expectedVersion == hash); prepares++
                    } else check(request.transferId == ticket)
                    result.download = DownloadInfo.newBuilder().setId(ticket).setName(name).setVersion(hash)
                        .setSize(payload.size.toLong()).setPreparedBytes(payload.size.toLong()).setExpiresAt(expires)
                        .setState(DownloadState.DOWNLOAD_READY).setSha256(ByteString.copyFrom(digest)).build()
                }
                FileOperation.DOWNLOAD_READ -> {
                    check(request.transferId == ticket && request.offset >= 0 && request.length in 1..StorageRules.RANGE)
                    check(request.offset + request.length <= payload.size)
                    beforeRead(request)
                    reads.add(request.offset)
                    result.content = ByteString.copyFrom(payload, request.offset.toInt(), request.length)
                    result.endOfFile = request.offset + request.length == payload.size.toLong()
                }
                FileOperation.RELEASE_DOWNLOAD -> check(request.transferId == ticket)
                else -> error("Unexpected probe source operation.")
            }
            return result.build()
        }
    }

    private fun stateFile(directory: File) = PrivateJsonFile(File(directory, "probe.json"), 16 * 1024)

    private suspend fun seed(directory: File, run: String): JSONObject {
        check(!directory.exists() && directory.mkdirs()) { "The probe run already exists." }
        val data = requireNotNull(targetContext.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "fixture", null, null))
        val tree = Uri.parse(data.getString("tree"))
        AndroidSafAccess(targetContext).acquire("process-probe:$run", tree, data.getInt("flags"),
            Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION)
        val source = Source(StorageRules.token(), StorageRules.token(), StorageRules.token(), System.currentTimeMillis() + 30 * 60_000)
        val manager = AndroidDownloadManager(targetContext, File(directory, "queue"))
        try {
            val id = manager.enqueue(peer, source.entry, tree)
            val state = JSONObject().put("schema", 1).put("run", run).put("seedPid", Process.myPid())
                .put("id", id).put("node", source.node).put("grant", source.grant).put("ticket", source.ticket)
                .put("expires", source.expires).put("hash", hash).put("size", payload.size)
                .put("tree", tree.toString()).put("root", data.getString("root"))
            stateFile(directory).write(state.toString())
            source.beforeRead = { request ->
                if (request.offset == checkpoint + StorageRules.RANGE) {
                    val row = manager.state.value.single()
                    val partSize = File(directory, "queue/$id.part").length()
                    check(row.offset == checkpoint && row.ticket == source.ticket && row.sha256 == hash)
                    check(partSize == request.offset)
                    val ready = JSONObject(state.toString()).put("phase", "waiting-for-process-kill")
                        .put("checkpoint", row.offset).put("partBytes", partSize)
                    PrivateJsonFile(File(directory, "ready.json"), 16 * 1024).write(ready.toString())
                    // The external driver now force-stops this actual process. No manager
                    // close/pause or coroutine cancellation runs on that termination path.
                    awaitCancellation()
                }
            }
            withTimeout(5 * 60_000L) {
                manager.resume(id, peer, source::request).join()
                error("The probe did not remain active for process termination: ${manager.state.value.single().error}")
            }
        } finally { withContext(NonCancellable) { manager.shutdown() } }
    }

    private suspend fun resume(directory: File, run: String): JSONObject {
        val state = JSONObject(stateFile(directory).read())
        check(state.getInt("schema") == 1 && state.getString("run") == run)
        check(state.getInt("seedPid") != Process.myPid()) { "A fresh application process is required." }
        check(state.getInt("size") == payload.size && state.getString("hash") == hash)
        val id = state.getString("id"); check(Rules.tokenPattern.matches(id))
        val source = Source(state.getString("node"), state.getString("grant"), state.getString("ticket"), state.getLong("expires"))
        check(listOf(source.node, source.grant, source.ticket).all(Rules.tokenPattern::matches))
        val tree = Uri.parse(state.getString("tree"))
        check(tree.authority == TestDocumentsProvider.AUTHORITY && Docs.getTreeDocumentId(tree) == state.getString("root"))
        check(targetContext.contentResolver.persistedUriPermissions.any { it.uri == tree && it.isReadPermission && it.isWritePermission })
        val partSize = File(directory, "queue/$id.part").length()
        check(partSize == checkpoint + StorageRules.RANGE) { "The killed worker's uncommitted tail was not retained." }
        val manager = AndroidDownloadManager(targetContext, File(directory, "queue"))
        try {
            check(manager.recoveryIssues().isEmpty())
            val reopened = manager.state.value.single()
            check(!reopened.active && reopened.phase == PhoneDownloadPhase.INTERRUPTED)
            check(reopened.offset == checkpoint && reopened.ticket == source.ticket && reopened.sha256 == hash)
            delay(100)
            check(manager.state.value.single().offset == checkpoint && source.reads.isEmpty())
            withTimeout(60_000) { manager.resume(id, peer, source::request).join() }
            val received = manager.state.value.single()
            check(received.phase == PhoneDownloadPhase.READY_TO_SAVE) { received.error }
            check(received.offset == payload.size.toLong() && received.ticket == reopened.ticket)
            check(source.prepares == 0 && source.reads.first() == checkpoint)
            withTimeout(60_000) { manager.publish(id).join() }
            val published = manager.state.value.single()
            check(published.phase == PhoneDownloadPhase.COMPLETED) { published.error }
            check(published.publishedName == name)
            val actualHash = MessageDigest.getInstance("SHA-256"); var actualSize = 0L
            requireNotNull(targetContext.contentResolver.openInputStream(Uri.parse(published.published))).use { input ->
                val buffer = ByteArray(64 * 1024)
                while (true) {
                    val count = input.read(buffer); if (count < 0) break
                    actualHash.update(buffer, 0, count); actualSize += count
                }
            }
            check(actualSize == payload.size.toLong() && Rules.hex(actualHash.digest()) == hash)
            var copies = 0
            requireNotNull(targetContext.contentResolver.query(Docs.buildChildDocumentsUriUsingTree(tree, Docs.getTreeDocumentId(tree)),
                arrayOf(Docs.Document.COLUMN_DISPLAY_NAME), null, null, null)).use { cursor ->
                while (cursor.moveToNext()) if (cursor.getString(0).startsWith("process-restart")) copies++
            }
            check(copies == 1)
            val result = JSONObject().put("run", run).put("phase", "completed").put("seedPid", state.getInt("seedPid"))
                .put("resumePid", Process.myPid()).put("checkpoint", reopened.offset).put("retainedPartBytes", partSize)
                .put("firstResumedOffset", source.reads.first()).put("preparationsAfterRestart", source.prepares)
                .put("bytes", actualSize).put("sha256", hash).put("copies", copies)
            manager.remove(id)
            AndroidSafAccess(targetContext).release("process-probe:$run")
            targetContext.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "dispose", state.getString("root"), null)
            PrivateJsonFile(File(directory, "result.json"), 16 * 1024).write(result.toString())
            return result
        } finally { withContext(NonCancellable) { manager.shutdown() } }
    }
}
