package com.iconnect.proof

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.CancellationSignal
import androidx.core.net.toUri
import com.google.protobuf.ByteString
import com.iconnect.protocol.*
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import org.json.JSONObject
import java.io.File
import kotlin.coroutines.CoroutineContext

internal data class LocalCopySelection(val tree: Uri, val document: String, val entry: FileEntry)
data class PhoneLocalCopy(val id: String, val name: String, val size: Long, val download: PhoneDownload?)
data class PhoneLocalCopyState(val items: List<PhoneLocalCopy> = emptyList(), val issues: List<StateRecoveryIssue> = emptyList(),
    val preserved: Int = 0, val error: String = "",val liveBytes:Map<String,Long> = emptyMap(),val payloadEstimates:Map<String,PhonePayloadRateEstimate> = emptyMap(),
    val finalizations: Map<String, PhoneIncomingFinalization> = emptyMap())

/** Offline copies have no peer identity and never enter the remote file host. */
class PhoneLocalCopies internal constructor(private val context: Context,
    private val directory: File = File(context.filesDir, "local-copies")) {
    private data class Source(val id: String, val tree: Uri, val document: String, val grant: String,
        val name: String, val version: String, val size: Long) {
        fun entry() = FileEntry.newBuilder().setId(id).setGrantId(grant).setName(name).setVersion(version)
            .setSize(size).setPermissions(StorageRules.READ_ONLY).build()
    }
    private val sourceDirectory = File(directory, "sources").apply { check(mkdirs() || isDirectory) }
    private val receiver = AndroidDownloadManager(context, File(directory, "receiving"), localCopy = true)
    private val recovery = StateRecovery(sourceDirectory)
    private val access = AndroidSafAccess(context)
    private val prefix = "local-copy-source:${Rules.hex(java.security.MessageDigest.getInstance("SHA-256").digest(directory.canonicalPath.toByteArray())).take(32)}:"
    private val gate = Any()
    private val sources = linkedMapOf<String, Source>()
    private val work = mutableMapOf<String, Job>()
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    @Volatile private var visible = false
    @Volatile private var closed = false
    private data class Destination(val id: String, val tree: Uri, val flags: Int, val collision: PhoneCollision)
    private var pendingDestination: Destination? = null
    private var error = ""
    private val mutable = MutableStateFlow(PhoneLocalCopyState())
    private val payloadRates=PhoneLocalPayloadRates()
    val state = mutable.asStateFlow()
    internal var activity: (PhoneActivityEvent) -> Unit = {}

    init {
        receiver.activity = { recordPhoneActivity(activity, it) }
        val journals = StateRecovery.journals(sourceDirectory, 8)
        if (journals.size > 8) recovery.block("More than eight local copy sources exist. Review private storage before restarting.")
        journals.take(8).forEach { file -> try {
            val row = JSONObject(PrivateJsonFile(file, 64 * 1024).read())
            val source = Source(row.getString("id"), row.getString("tree").toUri(), row.getString("document"),
                row.getString("grant"), row.getString("name"), row.getString("version"), row.getLong("size"))
            validate(source); require(file.name == "${source.id}.json" && source.id !in sources)
            sources[source.id] = source
        } catch (_: Exception) { recovery.add(file, "Saved local source access could not be validated. Preserve this record before continuing. Originals and provider copies are kept.") } }
        if (!recovery.needed()) access.reconcile(prefix, sources.values.associate { prefix + it.id to it.tree }, Intent.FLAG_GRANT_READ_URI_PERMISSION)
        else sources.values.forEach { access.retainRecorded(prefix + it.id, it.tree, Intent.FLAG_GRANT_READ_URI_PERMISSION) }
        emit()
        scope.launch { receiver.state.collect { emit() } }
        scope.launch {receiver.payloadProgress.collect {refreshPayload()}}
        scope.launch {while(isActive) {delay(1000); if(visible) refreshPayload()}}
    }
    fun setVisible(value: Boolean) {
        visible = value
        refreshPayload()
        if (!value) pauseAll()
        else synchronized(gate) { pendingDestination.also { pendingDestination = null } }?.let(::applyDestination)
    }
    fun acceptDestination(id: String, tree: Uri, flags: Int, collision: PhoneCollision) {
        val destination = Destination(id, tree, flags, collision)
        synchronized(gate) { check(!closed); if (!visible) { pendingDestination = destination; return } }
        applyDestination(destination)
    }
    private fun applyDestination(destination: Destination) { scope.launch {
        try { chooseDestination(destination.id, destination.tree, destination.flags, destination.collision) }
        catch (_: Exception) { report("The destination could not be approved. Choose a writable folder again; existing files were kept.") }
    } }
    fun pauseAll() { synchronized(gate) { work.values.forEach { it.cancel() }; receiver.pauseAll() } }
    private fun requireAvailable() { check(visible && !closed) { "Return to the app to manage local copies." } }
    /** Registration and owner withdrawal share one gate; canceled workers cannot dispatch after foreground return. */
    private fun receiving(worker: CoroutineContext? = null, action: () -> Job): Job = synchronized(gate) {
        worker?.ensureActive(); requireAvailable(); action()
    }
    private fun requireHealthy() { recovery.requireHealthy(); receiver.requireHealthy() }
    private fun validate(source: Source) {
        require(Rules.tokenPattern.matches(source.id) && Rules.tokenPattern.matches(source.grant) &&
            source.document.length in 1..4096 && source.document.none(Character::isISOControl) &&
            source.version.matches(Regex("[a-f0-9]{64}")) && source.size in 0..StorageRules.MAX_TRANSFER_SIZE)
        AndroidSafAccess.validateTree(source.tree); StorageRules.name(source.name)
    }
    /** Retain the selected source before the system destination picker backgrounds the app. */
    internal suspend fun prepare(selection: LocalCopySelection): String = withContext(Dispatchers.IO) { synchronized(gate) {
        requireAvailable(); requireHealthy()
        check(sources.size < 8 && sources.values.sumOf { it.size } <= 16L * 1024 * 1024 * 1024 - selection.entry.size) {
            "Remove an old local copy record first. Up to eight files and 16 GiB can be retained."
        }
        val source = Source(StorageRules.token(), selection.tree, selection.document, selection.entry.grantId,
            selection.entry.name, selection.entry.version, selection.entry.size)
        require(!selection.entry.isDirectory && selection.entry.permissions and StorageRules.READ != 0); validate(source)
        access.acquire(prefix + source.id, source.tree, Intent.FLAG_GRANT_READ_URI_PERMISSION)
        try {
            val row = JSONObject().put("id", source.id).put("tree", source.tree.toString()).put("document", source.document)
                .put("grant", source.grant).put("name", source.name).put("version", source.version).put("size", source.size)
            PrivateJsonFile(File(sourceDirectory, "${source.id}.json"), 64 * 1024).write(row.toString())
        } catch (e: Exception) { runCatching { access.release(prefix + source.id) }; throw e }
        sources[source.id] = source; error = ""; emit(); source.id
    } }
    suspend fun chooseDestination(id: String, tree: Uri, flags: Int, collision: PhoneCollision) = withContext(Dispatchers.IO) {
        synchronized(gate) {
            requireAvailable(); requireHealthy(); val source = sources.getValue(id)
            check(receiver.state.value.none { it.id == id }) { "This copy already has a destination." }
            // This temporary lease shares the recovery prefix. Startup removes a
            // crash-left picker lease only after the receiver retained its own.
            val owner = prefix + "picker:$id"
            access.acquire(owner, tree, flags, Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION)
            try { receiver.enqueue("", source.entry(), tree, id = id, collision = collision) }
            finally { access.release(owner) }
            error = ""; emit()
        }
        resume(id)
    }
    /** Explicit foreground resume; no work restarts merely because the process was recreated. */
    fun resume(id: String): Job = synchronized(gate) {
        requireAvailable(); requireHealthy(); check(id !in work)
        val row = receiver.state.value.single { it.id == id }
        check(!row.active)
        val source = sources[id]
        if (row.phase in setOf(PhoneDownloadPhase.PAUSED, PhoneDownloadPhase.INTERRUPTED, PhoneDownloadPhase.PREPARING))
            check(source != null) { "This source record is missing. Cancel this copy and select the original again." }
        val job = scope.launch(start = CoroutineStart.LAZY) {
            try {
                if (row.phase in setOf(PhoneDownloadPhase.PAUSED, PhoneDownloadPhase.INTERRUPTED, PhoneDownloadPhase.PREPARING)) {
                    localReader(requireNotNull(source)) { request -> receiving(currentCoroutineContext()) { receiver.resume(id, "", request) }.join() }
                }
                currentCoroutineContext().ensureActive(); requireAvailable()
                if (receiver.state.value.single { it.id == id }.phase == PhoneDownloadPhase.READY_TO_SAVE)
                    receiving(currentCoroutineContext()) { receiver.publish(id) }.join()
            } catch (e: Exception) { if (e !is CancellationException) report("The local copy stopped. Review its saved progress and folder access before retrying.") }
        }
        work[id] = job
        job.invokeOnCompletion { synchronized(gate) { if (work[id] === job) work.remove(id); emit() } }
        emit(); job.start(); job
    }
    fun pause(id: String) { synchronized(gate) { work[id]?.cancel(); receiver.pause(id) } }
    suspend fun cancel(id: String) = withContext(Dispatchers.IO) {
        requireAvailable(); val job = synchronized(gate) { work[id]?.also { it.cancel() }; receiver.pause(id); work[id] }
        job?.join(); receiver.awaitIdle(id)
        if (receiver.state.value.any { it.id == id }) receiver.cancel(id)
        else removeSource(id)
        emit()
    }
    suspend fun remove(id: String) = withContext(Dispatchers.IO) {
        requireAvailable(); synchronized(gate) { check(id !in work) }
        receiver.remove(id); removeSource(id); emit()
    }
    fun save(id: String, anotherCopy: Boolean = false): Job {
        return receiving { receiver.publish(id, anotherCopy) }.also { job -> job.invokeOnCompletion { emit() } }
    }
    suspend fun preserve(id: String) = withContext(Dispatchers.IO) { synchronized(gate) {
        requireAvailable(); check(work.isEmpty() && receiver.state.value.none { it.active })
        if (recovery.all().any { it.id == id }) recovery.archive(id) { key -> if (Rules.tokenPattern.matches(key)) access.release(prefix + key) }
        else receiver.archiveRecovery(id)
        if (!recovery.needed()) access.reconcile(prefix, sources.values.associate { prefix + it.id to it.tree }, Intent.FLAG_GRANT_READ_URI_PERMISSION)
        emit()
    } }
    suspend fun clearPreserved() = withContext(Dispatchers.IO) { synchronized(gate) {
        requireAvailable(); check(work.isEmpty() && receiver.state.value.none { it.active })
        recovery.clearArchives(); receiver.clearPreservedRecords(); emit()
    } }
    private fun removeSource(id: String) = synchronized(gate) {
        require(Rules.tokenPattern.matches(id)); val file = File(sourceDirectory, "$id.json")
        android.util.AtomicFile(file).delete(); check(listOf(file, File(file.path + ".bak"), File(file.path + ".new")).none { it.exists() })
        sources.remove(id); access.release(prefix + id)
    }
    fun report(message: String) = synchronized(gate) { error = message; emit() }
    private fun emit() = synchronized(gate) {
        val receiving = receiver.state.value.associateBy { it.id }
        val items = sources.values.map { PhoneLocalCopy(it.id, it.name, it.size, receiving[it.id]) } +
            receiving.values.filter { it.id !in sources }.map { PhoneLocalCopy(it.id, it.name, it.size, it) }
        refreshPayload(PhoneLocalCopyState(items.map { it.copy(download = it.download?.let { row -> row.copy(active = row.active || it.id in work) }) },
            recovery.all() + receiver.recoveryIssues(), recovery.archivedCount() + receiver.preservedRecords(), error))
    }
    private fun refreshPayload(state: PhoneLocalCopyState? = null)=synchronized(gate) {
        val available = visible && !closed
        val view=if(available) payloadRates.refresh(receiver.state.value,receiver.payloadProgress.value,android.os.SystemClock.elapsedRealtime())
            else {payloadRates.clear(); PhoneLocalPayloadView()}
        val finalizations = if (available) receiver.readFinalizations() else emptyMap()
        mutable.value=(state ?: mutable.value).copy(liveBytes=view.bytes,payloadEstimates=view.estimates,finalizations=finalizations)
    }
    private suspend fun localReader(source: Source, action: suspend (suspend (FileRequest) -> FileResponse) -> Unit) = coroutineScope {
        // This policy exists only inside one local worker, and accepts the empty local owner.
        val grant = AndroidStorageGrant(source.grant, "", source.tree, source.name)
        val policy = object : DocumentAccessPolicy {
            override fun <T> authorized(action: () -> T): T { requireAvailable(); return action() }
            override fun forPeer(peer: String) = if (peer.isEmpty()) listOf(grant) else emptyList()
            override fun get(id: String, peer: String) = grant.takeIf { id == source.grant && peer.isEmpty() }
                ?: throw StorageFailure(FileStatus.ACCESS_DENIED, "This local source is unavailable.")
            override fun visible(grant: AndroidStorageGrant, name: String) = runCatching { StorageRules.name(name); true }.getOrDefault(false)
        }
        val signal = CancellationSignal()
        val reader = AndroidFileService(context, policy)
        val watcher = launch(start = CoroutineStart.UNDISPATCHED) { try { awaitCancellation() } finally { signal.cancel(); reader.close() } }
        var hash: ByteArray? = null
        try {
            action { value ->
                requireAvailable(); currentCoroutineContext().ensureActive()
                val request = value.toBuilder().setRequestId(StorageRules.token()).build()
                runInterruptible(Dispatchers.IO) {
                    val response = FileResponse.newBuilder().setRequestId(request.requestId)
                    when (request.operation) {
                        FileOperation.PREPARE_DOWNLOAD, FileOperation.DOWNLOAD_STATUS -> {
                            require(if (request.operation == FileOperation.PREPARE_DOWNLOAD) request.nodeId == source.id && request.expectedVersion == source.version else request.transferId == source.id)
                            val info = reader.sourceSnapshot("", source.grant, source.document, source.version, signal)
                            check(info.name == source.name && info.size == source.size) { "The selected local file changed." }
                            if (hash == null) hash = reader.hashSource("", source.grant, source.document, source.version, source.size, signal) { requireAvailable() }
                            response.download = DownloadInfo.newBuilder().setId(source.id).setName(source.name).setVersion(source.version)
                                .setSize(source.size).setPreparedBytes(source.size).setExpiresAt(StorageRules.MAX_TIMESTAMP)
                                .setState(DownloadState.DOWNLOAD_READY).setSha256(ByteString.copyFrom(requireNotNull(hash))).build()
                        }
                        FileOperation.DOWNLOAD_READ -> {
                            require(request.transferId == source.id && hash != null)
                            return@runInterruptible reader.sourceRange("", source.grant, source.document, source.version, request, signal)
                        }
                        FileOperation.RELEASE_DOWNLOAD -> require(request.transferId == source.id)
                        else -> error("This local copy request is unavailable.")
                    }
                    response.build()
                }
            }
        } finally { watcher.cancel(); signal.cancel(); reader.close() }
    }
    suspend fun shutdown() {
        closed = true; visible = false; refreshPayload(); synchronized(gate) { pendingDestination = null }; pauseAll(); scope.cancel(); scope.coroutineContext.job.join(); receiver.shutdown()
    }
}
