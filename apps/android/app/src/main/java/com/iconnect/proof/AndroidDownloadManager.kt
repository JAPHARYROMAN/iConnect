package com.iconnect.proof

import android.content.Context
import android.net.Uri
import android.os.CancellationSignal
import android.provider.DocumentsContract as Documents
import android.util.AtomicFile
import com.iconnect.protocol.*
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit
import org.json.JSONObject
import org.json.JSONArray
import java.io.File
import java.io.FileInputStream
import java.io.RandomAccessFile
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.MessageDigest

enum class PhoneDownloadPhase { PREPARING, DOWNLOADING, PAUSED, INTERRUPTED, VERIFYING, READY_TO_SAVE,
    PUBLISHING, PUBLICATION_INTERRUPTED, COMPLETED, FAILED, CANCELED, SKIPPED }

data class PhoneDownload(val id: String, val peer: String, val name: String, val node: String, val version: String,
    val size: Long, val tree: String, val ticket: String = "", val sha256: String = "", val offset: Long = 0,
    val chain: String = "00".repeat(32), val phase: PhoneDownloadPhase = PhoneDownloadPhase.PAUSED,
    val error: String = "", val published: String = "", val publishedName: String = "", val attempts: Int = 0, val active: Boolean = false,
    val parents: List<SafFolderBinding> = emptyList(), val batchId: String = "", val collision: PhoneCollision = PhoneCollision.KEEP_BOTH,
    val sourceExpiresAt: Long = 0)

/** Private staging is resumable. SAF publication is a separate, explicitly non-atomic operation. */
class AndroidDownloadManager internal constructor(private val context: Context, private val directory: File = File(context.filesDir, "downloads"),
    private val localCopy: Boolean = false, private val batchOnly: Boolean = false,
    private val now: () -> Long = System::currentTimeMillis,
    private val elapsed: () -> Long = android.os.SystemClock::elapsedRealtime) : AutoCloseable {
    internal var activity:(PhoneActivityEvent)->Unit={}
    private val access = AndroidSafAccess(context)
    private val accessPrefix = "download:${Rules.hex(MessageDigest.getInstance("SHA-256").digest(directory.canonicalPath.toByteArray())).take(32)}:"
    private val gate = Any()
    internal val ownershipGate get()=gate
    private val records = linkedMapOf<String, PhoneDownload>()
    private data class Work(val job: Job, val finalization: PhoneIncomingFinalizationTracker)
    private val work: MutableMap<String, Work> = java.util.concurrent.ConcurrentHashMap()
    private val canceling = mutableSetOf<String>()
    private data class SourceOffer(val review:PhoneDownloadSourceReview,val node:String,val info:DownloadInfo,
        val request:suspend(FileRequest)->FileResponse,val current:()->Boolean,val started:Long,val authority:AndroidBatchDownloads.SourceAuthority?)
    private val sourceReviews=mutableMapOf<String,SourceOffer>()
    private val sourceWork=mutableMapOf<String,Job>()
    private val sourceTickets=mutableSetOf<Pair<String,String>>()
    private var closed = false
    private val lifetime = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val slots = Semaphore(2)
    private val mutable = MutableStateFlow<List<PhoneDownload>>(emptyList())
    val state = mutable.asStateFlow()
    private val payload=mutableMapOf<String,PhonePayloadProgress>()
    private val payloadEmitted=mutableMapOf<String,Long>()
    private val mutablePayload=MutableStateFlow<Map<String,PhonePayloadProgress>>(emptyMap())
    internal val payloadProgress=mutablePayload.asStateFlow()
    internal fun readFinalizations(): Map<String, PhoneIncomingFinalization> = buildMap {
        work.forEach { (id, active) ->
            if (active.job.isActive) active.finalization.readStarted()?.let { put(id, it) }
        }
    }
    private val lockFile: RandomAccessFile
    private val fileLock: java.nio.channels.FileLock
    private val recovery = StateRecovery(directory)
    fun recoveryIssues() = synchronized(gate) { recovery.all() }
    fun preservedRecords() = synchronized(gate) { recovery.archivedCount() }
    fun clearPreservedRecords() = synchronized(gate) { recovery.clearArchives() }
    internal fun requireHealthy() = synchronized(gate) { recovery.requireHealthy() }
    fun archiveRecovery(id: String) = synchronized(gate) {
        check(work.isEmpty() && canceling.isEmpty() && sourceWork.isEmpty() && sourceReviews.isEmpty()) { "Pause receiving and close source reviews before reviewing saved progress." }
        recovery.archive(id) { key -> if (Rules.tokenPattern.matches(key)) access.release(accessPrefix + key) }
        if (!recovery.needed()) access.reconcile(accessPrefix, records.values.associate { accessPrefix + it.id to Uri.parse(it.tree) })
    }
    init {
        check(directory.mkdirs() || directory.isDirectory)
        lockFile = RandomAccessFile(File(directory, "owner.lock"), "rw")
        try { fileLock = requireNotNull(lockFile.channel.tryLock()) { "The download queue is already open." } }
        catch (e: Exception) { lockFile.close(); throw e }
        try {
            val journals = StateRecovery.journals(directory)
            if (journals.size > 1024) recovery.block("More than 1,024 progress records exist. Review private queue storage and restart the app.")
            journals.take(1024).forEach { file -> try {
                require(records.size < 128)
                val row = JSONObject(PrivateJsonFile(file, 64 * 1024).read())
                val record = decode(row)
                require(file.name == "${record.id}.json" && record.id !in records)
                records[record.id] = record.copy(phase = when (record.phase) {
                    PhoneDownloadPhase.PREPARING, PhoneDownloadPhase.DOWNLOADING, PhoneDownloadPhase.VERIFYING -> PhoneDownloadPhase.INTERRUPTED
                    PhoneDownloadPhase.PUBLISHING -> PhoneDownloadPhase.PUBLICATION_INTERRUPTED
                    else -> record.phase
                })
            } catch (_: Exception) { recovery.add(file, "This file's saved progress could not be validated. Its private staging and provider copies were left untouched.") } }
            if (!recovery.needed()) access.reconcile(accessPrefix, records.values.associate { accessPrefix + it.id to Uri.parse(it.tree) })
            else records.values.forEach { access.retainRecorded(accessPrefix + it.id, Uri.parse(it.tree)) }
            emit()
        } catch (e: Exception) { fileLock.release(); lockFile.close(); lifetime.cancel(); throw e }
    }
    fun enqueue(peer: String, entry: FileEntry, tree: Uri, parents: List<SafFolderBinding> = emptyList(),
        id: String = StorageRules.token(), batchId: String = "", collision: PhoneCollision = PhoneCollision.KEEP_BOTH): String = synchronized(gate) {
        check(!closed)
        recovery.requireHealthy()
        require(validOwner(peer) && !entry.isDirectory && entry.permissions and StorageRules.READ != 0 &&
            Rules.tokenPattern.matches(entry.id) && entry.version.matches(Regex("[a-f0-9]{64}")) && entry.size in 0..StorageRules.MAX_TRANSFER_SIZE)
        require(Rules.tokenPattern.matches(id) && id !in records && (!batchOnly || batchId.isNotEmpty()) && (batchId.isEmpty() || Rules.tokenPattern.matches(batchId)))
        if (localCopy) check(!java.nio.file.Files.exists(part(id).toPath(), java.nio.file.LinkOption.NOFOLLOW_LINKS)) {
            "Private staging already exists for this local job. Keep it for review and select the original as a new copy."
        }
        StorageRules.name(entry.name); SafDestination(context, tree, parents).validate()
        check(records.size < 128) { "Remove an old download before adding another." }
        check(entry.size <= (directory.usableSpace - 64L * 1024 * 1024).coerceAtLeast(0)) { "Not enough private storage to verify this file." }
        val record = PhoneDownload(id, peer, entry.name, entry.id, entry.version, entry.size, tree.toString(),
            parents = parents.toList(), batchId = batchId, collision = collision)
        access.retainRecorded(accessPrefix + record.id, tree)
        try { put(record) } catch (e: Exception) { runCatching { access.release(accessPrefix + record.id) }; throw e }
        record.id
    }
    /** The requester must capture one authenticated session and its pinned peer. */
    fun resume(id: String, peer: String, request: suspend (FileRequest) -> FileResponse) = owned(id, "") { resumeCore(id, peer, request) }
    internal fun resumeBatchFile(id: String, peer: String, batch: String, request: suspend (FileRequest) -> FileResponse) = owned(id, batch) { resumeCore(id, peer, request) }
    private fun <T> owned(id: String, batch: String, action: () -> T): T = synchronized(gate) {
        recovery.requireHealthy(); check(records.getValue(id).batchId == batch) { "Use this file's original batch. A missing batch cannot authorize a standalone transfer." }; action()
    }
    private fun resumeCore(id: String, peer: String, request: suspend (FileRequest) -> FileResponse) = launch(id) { progress ->
        var record = get(id)
        check(record.peer == peer) { "Reconnect the computer that owns this download." }
        check(record.phase in setOf(PhoneDownloadPhase.PAUSED, PhoneDownloadPhase.INTERRUPTED, PhoneDownloadPhase.PREPARING)) { "This download cannot be resumed." }
        val part = part(id)
        var checkpoint = record
        var canCheckpoint = false
        var received = record.offset
        var chain = fromHex(record.chain)
        try {
            record = record.copy(phase = PhoneDownloadPhase.PREPARING, error = ""); put(record)
            val digest = MessageDigest.getInstance("SHA-256")
            // A zero checkpoint owns an empty private part; no provider file is ever adopted here.
            if (!part.exists()) { check(record.offset == 0L); check(part.createNewFile()) }
            RandomAccessFile(part, "rw").use { output ->
                check(output.length() >= record.offset) { "The partial download is missing bytes." }
                output.setLength(record.offset)
                val verifiedChain = hashPrefix(output, record.offset, digest)
                check(verifiedChain.contentEquals(chain)) { "The partial download changed. Cancel it and start again." }
                canCheckpoint = true
                checkpoint = record
                if (record.ticket.isEmpty() && !(record.offset == record.size && record.sha256.isNotEmpty())) {
                    val response = response(request(FileRequest.newBuilder().setOperation(FileOperation.PREPARE_DOWNLOAD)
                        .setNodeId(record.node).setExpectedVersion(record.version).build()))
                    check(response.hasDownload())
                    val info = response.download; validate(record, info)
                    record = record.copy(ticket = info.id, sourceExpiresAt = deadline(record.sourceExpiresAt,info.expiresAt)); put(record); checkpoint = record
                }
                if (record.offset != record.size || record.sha256.isEmpty()) {
                    var info: DownloadInfo
                    while (true) {
                        currentCoroutineContext().ensureActive()
                        val status = response(request(FileRequest.newBuilder().setOperation(FileOperation.DOWNLOAD_STATUS).setTransferId(record.ticket).build()))
                        check(status.hasDownload()); info = status.download; validate(record, info)
                        val expiry=deadline(record.sourceExpiresAt,info.expiresAt)
                        if(expiry!=record.sourceExpiresAt) {
                            record=record.copy(sourceExpiresAt=expiry); put(record); checkpoint=record
                        }
                        check(info.state != DownloadState.DOWNLOAD_FAILED) { "The source could not be prepared. Start a new download." }
                        if (info.state == DownloadState.DOWNLOAD_READY) break
                        delay(300)
                    }
                    val expected = Rules.hex(info.sha256.toByteArray())
                    check(record.sha256.isEmpty() || record.sha256 == expected) { "The source checksum changed." }
                    record = record.copy(sha256 = expected)
                }
                record = record.copy(phase = PhoneDownloadPhase.DOWNLOADING); put(record); checkpoint = record
                val payloadRun=StorageRules.token()
                publishPayload(record,received,payloadRun,force=true)
                output.seek(received)
                while (received < record.size) {
                    currentCoroutineContext().ensureActive()
                    check(localCopy || record.sourceExpiresAt==0L || record.sourceExpiresAt>now()) { "The source approval expired. Review a fresh source, then resume explicitly." }
                    val count = minOf(StorageRules.RANGE.toLong(), record.size - received).toInt()
                    val data = response(request(FileRequest.newBuilder().setOperation(FileOperation.DOWNLOAD_READ)
                        .setTransferId(record.ticket).setOffset(received).setLength(count).build()))
                    check(data.content.size() == count && data.endOfFile == (received + count == record.size)) { "Invalid source range." }
                    val bytes = data.content.toByteArray()
                    output.write(bytes); digest.update(bytes); chain = nextChain(chain, received, bytes); received += bytes.size
                    publishPayload(record,received,payloadRun)
                    if (received - checkpoint.offset >= 1024 * 1024 || received == record.size) {
                        output.fd.sync()
                        record = record.copy(offset = received, chain = Rules.hex(chain)); put(record); checkpoint = record
                    }
                }
                output.fd.sync()
                check(Rules.hex(digest.digest()) == record.sha256) { "The download checksum did not match." }
            }
            progress.begin(PhoneIncomingFinalizationStage.CHECKING_PRIVATE_BYTES)
            put(record.copy(phase = PhoneDownloadPhase.VERIFYING))
            check(part.length() == record.size && hashFile(part, record.size, progress::advance) == record.sha256) { "The saved staging file did not verify." }
            progress.end()
            put(record.copy(phase = PhoneDownloadPhase.READY_TO_SAVE, error = ""))
            release(record, request)
        } catch (e: Exception) {
            // Only validated prefixes may become checkpoints. A failed flush keeps the prior journal.
            if (canCheckpoint && received > checkpoint.offset) runCatching {
                RandomAccessFile(part, "rw").use { it.fd.sync() }
                checkpoint = checkpoint.copy(offset = received, chain = Rules.hex(chain)); put(checkpoint)
            }
            val stopped = e is CancellationException
            put(checkpoint.copy(phase = if (stopped) PhoneDownloadPhase.PAUSED else PhoneDownloadPhase.INTERRUPTED,
                error = if (stopped) "" else failure(e)))
            if (stopped) throw e
        }
    }
    fun publish(id: String, anotherCopy: Boolean = false) = owned(id, "") { publishCore(id, anotherCopy) }
    internal fun publishBatchFile(id: String, anotherCopy: Boolean, batch: String) = owned(id, batch) { publishCore(id, anotherCopy) }
    private fun publishCore(id: String, anotherCopy: Boolean) = launch(id) { progress ->
        var record = get(id)
        check(record.phase in setOf(PhoneDownloadPhase.READY_TO_SAVE, PhoneDownloadPhase.PUBLICATION_INTERRUPTED))
        try {
            progress.begin(PhoneIncomingFinalizationStage.PREPARING)
            val tree = Uri.parse(record.tree)
            val destination = SafDestination(context, tree, record.parents); destination.validate()
            // After a crash we only read the recorded document. Never truncate or rewrite it.
            if (record.phase == PhoneDownloadPhase.PUBLICATION_INTERRUPTED && !anotherCopy) {
                check(record.published.isNotEmpty()) { "A provider copy may have been created. Inspect the folder, then choose Save another copy." }
                val uri = Uri.parse(record.published); destination.child(uri)
                progress.begin(PhoneIncomingFinalizationStage.CHECKING_PROVIDER_BYTES)
                check(hashDocument(uri, record.size, progress::advance) == record.sha256) { "The provider copy is incomplete or changed. It was left untouched. Save another copy if needed." }
                destination.child(uri)
                put(record.copy(phase = PhoneDownloadPhase.COMPLETED, error = "")); part(id).delete(); return@launch
            }
            progress.begin(PhoneIncomingFinalizationStage.CHECKING_PRIVATE_BYTES)
            check(record.offset == record.size && part(id).length() == record.size && hashFile(part(id), record.size, progress::advance) == record.sha256) { "The verified staging file changed." }
            progress.begin(PhoneIncomingFinalizationStage.PREPARING_PROVIDER_DOCUMENT)
            check(record.attempts < 8) { "Too many publication attempts. Inspect the destination folder." }
            val existing = destination.children()
            val collision = existing.values.any { it.equals(record.name, true) }
            if (collision && record.collision == PhoneCollision.SKIP && !anotherCopy) {
                put(record.copy(phase = PhoneDownloadPhase.SKIPPED, error = "An existing destination was kept.")); check(part(id).delete()); return@launch
            }
            check(!collision || record.collision != PhoneCollision.FAIL || anotherCopy) { "An existing destination was kept. Cancel this item or choose a new copy explicitly." }
            val name = SafDestination.unusedName(record.name, existing.values)
            record = record.copy(phase = PhoneDownloadPhase.PUBLISHING, error = "", published = "", publishedName = "", attempts = record.attempts + 1)
            // Intent is durable before createDocument: a crash here requires an explicit recovery decision.
            put(record)
            val uri = requireNotNull(Documents.createDocument(context.contentResolver, destination.parent, "application/octet-stream", name))
            check(uri.scheme == "content" && uri.authority == tree.authority && uri.toString().length <= 16_384 && Documents.getDocumentId(uri) !in existing.keys) { "The provider did not create a supported new document." }
            destination.child(uri)
            record = record.copy(published = uri.toString(), publishedName = destination.created(uri, existing)); put(record)
            check(record.collision == PhoneCollision.KEEP_BOTH || anotherCopy || record.publishedName == record.name) {
                "The provider changed the destination name. The new empty copy was left untouched for review." }
            currentCoroutineContext().ensureActive()
            opened(uri, "w") { descriptor ->
                    android.os.ParcelFileDescriptor.AutoCloseOutputStream(descriptor).use { output ->
                        progress.begin(PhoneIncomingFinalizationStage.WRITING_PROVIDER_DOCUMENT)
                        FileInputStream(part(id)).use { input ->
                            val buffer = ByteArray(StorageRules.RANGE)
                            var copied = 0L
                            while (true) {
                                currentCoroutineContext().ensureActive()
                                val count = input.read(buffer)
                                if (count < 0) break
                                check(count <= record.size - copied) { "The verified staging file grew during saving." }
                                output.write(buffer, 0, count)
                                copied += count
                                progress.advance(copied)
                            }
                            check(copied == record.size) { "The verified staging file became shorter during saving." }
                        }
                        progress.begin(PhoneIncomingFinalizationStage.SYNCING_PROVIDER_DOCUMENT)
                        output.flush()
                        // Some providers expose pipes; read-back below is the acceptance check.
                        runCatching { descriptor.fileDescriptor.sync() }
                    }
            }
            destination.child(uri)
            progress.begin(PhoneIncomingFinalizationStage.CHECKING_PROVIDER_BYTES)
            check(hashDocument(uri, record.size, progress::advance) == record.sha256) { "The provider copy did not verify." }
            destination.child(uri)
            put(record.copy(phase = PhoneDownloadPhase.COMPLETED, error = "")); part(id).delete()
        } catch (e: Exception) {
            val latest = get(id)
            put(latest.copy(phase = if (latest.phase == PhoneDownloadPhase.PUBLISHING || latest.phase == PhoneDownloadPhase.PUBLICATION_INTERRUPTED)
                PhoneDownloadPhase.PUBLICATION_INTERRUPTED else PhoneDownloadPhase.READY_TO_SAVE,
                error = if (e is CancellationException) "Saving stopped. A partial provider copy may remain; it was left untouched." else failure(e)))
            if (e is CancellationException) throw e
        }
    }
    fun pause(id: String) { synchronized(gate) { work[id]?.job?.cancel() } }
    fun pauseAll() { synchronized(gate) { work.values.forEach { it.job.cancel() } }; invalidateSourceReviews() }
    suspend fun cancel(id: String, request: (suspend (FileRequest) -> FileResponse)? = null) {
        val jobs = synchronized(gate) { check(!closed && canceling.add(id)); listOfNotNull(work[id]?.job,sourceWork[id]).onEach { it.cancel() } }
        try {
            jobs.joinAll()
            synchronized(gate) { sourceReviews.values.filter {it.review.job.id==id}.map {it.review.id} }.forEach { cancelSourceReview(it) }
            val record = get(id); check(record.phase != PhoneDownloadPhase.COMPLETED)
            put(record.copy(phase = PhoneDownloadPhase.CANCELED, error = if (record.attempts > 0) "Provider copies were left untouched. Inspect the destination folder." else ""))
            if (request != null) release(record, request)
            check(!part(id).exists() || part(id).delete()) { "The private partial file could not be removed." }
        } finally { synchronized(gate) { canceling.remove(id) } }
    }
    fun remove(id: String) = synchronized(gate) {
        check(!closed && id !in work && id !in canceling)
        check(get(id).phase in setOf(PhoneDownloadPhase.COMPLETED, PhoneDownloadPhase.CANCELED, PhoneDownloadPhase.SKIPPED))
        check(!part(id).exists() || part(id).delete())
        AtomicFile(journal(id)).delete(); check(!journal(id).exists() && !File(journal(id).path + ".bak").exists())
        records.remove(id); emit(); access.release(accessPrefix + id)
    }
    private fun launch(id: String, action: suspend CoroutineScope.(PhoneIncomingFinalizationTracker) -> Unit): Job = synchronized(gate) {
        check(!closed && id in records && id !in work && id !in canceling && id !in sourceWork && sourceReviews.values.none {it.review.job.id==id}) { "This download is active or has a source review open." }
        val progress = PhoneIncomingFinalizationTracker(id, records.getValue(id).size, elapsed)
        val job = lifetime.launch(start = CoroutineStart.LAZY) {
            try { slots.withPermit { action(progress) } }
            catch (e: Exception) { if (e !is CancellationException) {
                val record = get(id)
                runCatching { put(record.copy(error = failure(e))) }.onFailure {
                    synchronized(gate) { records[id] = record.copy(error = "The queue checkpoint could not be saved. Free storage space before retrying.",
                        phase = if (record.phase in setOf(PhoneDownloadPhase.PUBLISHING, PhoneDownloadPhase.PUBLICATION_INTERRUPTED))
                            PhoneDownloadPhase.PUBLICATION_INTERRUPTED else PhoneDownloadPhase.INTERRUPTED); emit() }
                }
            } }
        }
        work[id] = Work(job, progress)
        // Completion also runs when a queued coroutine is canceled before its body starts.
        job.invokeOnCompletion { synchronized(gate) { if (work[id]?.job === job) { work.remove(id); clearPayload(id); emit() } } }
        emit(); job.start(); job
    }
    private fun get(id: String): PhoneDownload = synchronized(gate) { requireNotNull(records[id]) }
    suspend fun awaitIdle(id: String) { synchronized(gate) { work[id]?.job }?.join() }
    fun rebind(id: String, entry: FileEntry) = synchronized(gate) {
        val record = get(id)
        check(!closed && id !in work && id !in canceling && record.batchId.isNotEmpty() && record.ticket.isEmpty() && record.offset == 0L && record.sha256.isEmpty())
        require(!entry.isDirectory && Rules.tokenPattern.matches(entry.id) && entry.name == record.name && entry.version == record.version && entry.size == record.size)
        put(record.copy(node = entry.id))
    }
    private fun put(record: PhoneDownload) = synchronized(gate) {
        val previous=records[record.id]?.phase
        PrivateJsonFile(journal(record.id), 64 * 1024).write(encode(record).toString())
        records[record.id] = record
        if(record.phase!=PhoneDownloadPhase.DOWNLOADING) clearPayload(record.id)
        emit()
        if(previous!=record.phase) {
            val outcome=when(record.phase) {PhoneDownloadPhase.COMPLETED->PhoneActivityOutcome.COMPLETED; PhoneDownloadPhase.READY_TO_SAVE->PhoneActivityOutcome.ACCEPTED;
                PhoneDownloadPhase.FAILED->PhoneActivityOutcome.FAILED; PhoneDownloadPhase.PUBLICATION_INTERRUPTED->PhoneActivityOutcome.REVIEW; PhoneDownloadPhase.CANCELED->PhoneActivityOutcome.CANCELED; PhoneDownloadPhase.SKIPPED->PhoneActivityOutcome.SKIPPED;
                PhoneDownloadPhase.PAUSED,PhoneDownloadPhase.INTERRUPTED->PhoneActivityOutcome.PAUSED; else->null}
            if(outcome!=null) recordPhoneActivity(activity,PhoneActivityEvent(if(localCopy) PhoneActivityKind.LOCAL_COPY else PhoneActivityKind.DOWNLOAD,outcome,record.peer,if(outcome==PhoneActivityOutcome.COMPLETED) record.size else -1))
        }
    }
    private fun emit() { mutable.value = records.values.map { it.copy(active = it.id in work) } }
    private fun clearPayload(id:String) {if(payload.remove(id)!=null) mutablePayload.value=payload.toMap(); payloadEmitted.remove(id)}
    private fun publishPayload(row:PhoneDownload,received:Long,run:String,force:Boolean=false)=synchronized(gate) {
        val at=elapsed(); payload[row.id]=PhonePayloadProgress(row.id,row.peer,row.ticket,row.version,row.size,received,at,run)
        val previous=payloadEmitted[row.id]
        if(force || previous==null || at<previous || at-previous>=250 || received==row.size) {
            payloadEmitted[row.id]=at; mutablePayload.value=payload.toMap()
        }
    }
    private fun journal(id: String) = File(directory, "$id.json")
    private fun part(id: String) = File(directory, "$id.part")
    private fun response(value: FileResponse): FileResponse { check(value.status == FileStatus.FILE_STATUS_OK) { value.message.ifEmpty { "The source is unavailable." } }; return value }
    private fun validate(record: PhoneDownload, info: DownloadInfo) {
        check(Rules.tokenPattern.matches(info.id) && (record.ticket.isEmpty() || info.id == record.ticket) && info.name == record.name && info.size == record.size &&
            info.version == record.version && info.expiresAt in (now()+1)..StorageRules.MAX_TIMESTAMP && info.preparedBytes in 0..info.size &&
            info.state != DownloadState.UNRECOGNIZED && (if (info.state == DownloadState.DOWNLOAD_READY) info.sha256.size() == 32 else info.sha256.isEmpty)) { "The source descriptor changed." }
    }
    private suspend fun release(record: PhoneDownload, request: suspend (FileRequest) -> FileResponse) {
        if (record.ticket.isNotEmpty()) runCatching { withTimeout(2_000) { request(FileRequest.newBuilder().setOperation(FileOperation.RELEASE_DOWNLOAD).setTransferId(record.ticket).build()) } }
    }
    private fun deadline(previous:Long,fresh:Long)=if(previous==0L) fresh else if(fresh==0L) previous else minOf(previous,fresh)

    internal fun sourcePartIdentity(id:String):String=synchronized(gate) {
        require(Rules.tokenPattern.matches(id))
        val file=part(id)
        val root=android.system.Os.lstat(directory.path)
        check(android.system.OsConstants.S_ISDIR(root.st_mode)) {"The original private receiving folder changed."}
        if(!java.nio.file.Files.exists(file.toPath(),java.nio.file.LinkOption.NOFOLLOW_LINKS)) return@synchronized "${root.st_dev}:${root.st_ino}:0:0"
        check(file.canonicalFile.parentFile==directory.canonicalFile)
        val stat=android.system.Os.lstat(file.path)
        check(android.system.OsConstants.S_ISREG(stat.st_mode) && stat.st_nlink==1L) {"The original private receiving part changed."}
        "${root.st_dev}:${root.st_ino}:${stat.st_dev}:${stat.st_ino}"
    }
    private fun requireSourceJob(id:String,peer:String,authority:AndroidBatchDownloads.SourceAuthority?=null):PhoneDownload {
        recovery.requireHealthy()
        val row=get(id)
        authority?.require(row)
        check(!closed && !localCopy && (authority!=null || !batchOnly && row.batchId.isEmpty()) && row.peer==peer && id !in work && id !in canceling &&
            row.phase in setOf(PhoneDownloadPhase.PAUSED,PhoneDownloadPhase.INTERRUPTED) && row.attempts==0 && row.published.isEmpty() &&
            !(row.offset==row.size && row.sha256.isNotEmpty()) && (row.offset==0L || row.sha256.isNotEmpty())) {
            "Use an idle, unfinished standalone download from this paired computer. Provider saving and batch copies require their original workflow."
        }
        return row
    }
    private suspend fun <T> sourceTask(id:String,reserve:()->Unit={},action:suspend()->T):T {
        val caller=currentCoroutineContext()
        val task=synchronized(gate) {
            check(!closed && id !in sourceWork)
            reserve()
            lifetime.async(start=CoroutineStart.LAZY) {action()}.also { sourceWork[id]=it; it.start() }
        }
        try { return task.await() }
        finally { withContext(NonCancellable) {
            if(!task.isCompleted) task.cancelAndJoin()
            synchronized(gate) {
                if(sourceWork[id]===task) sourceWork.remove(id)
                if(!caller.isActive) sourceReviews.values.filter {it.review.job.id==id}.map {it.review.id}.forEach {cancelSourceReview(it)}
            }
        } }
    }
    private suspend fun releaseOffer(peer:String,ticket:String,request:suspend(FileRequest)->FileResponse) {
        withContext(NonCancellable) {
            try { withTimeout(2000) {request(FileRequest.newBuilder().setOperation(FileOperation.RELEASE_DOWNLOAD).setTransferId(ticket).build())} }
            catch (_:Exception) { /* An offline peer retains its bounded lease until expiry. */ }
            finally { synchronized(gate) {sourceTickets.remove(peer to ticket)} }
        }
    }
    private fun cancelSourceReviewTask(id:String):Job? = synchronized(gate) {
            val offer=sourceReviews.remove(id) ?: return@synchronized null
            // This cleanup stays in the manager lifetime; shutdown drains it or releases its offer itself.
            if(!closed) lifetime.launch(start=CoroutineStart.UNDISPATCHED) {releaseOffer(offer.review.job.peer,offer.info.id,offer.request)} else null
    }
    fun cancelSourceReview(id:String) {cancelSourceReviewTask(id)}
    internal suspend fun cancelSourceReviewAndWait(id:String)=withContext(NonCancellable) {cancelSourceReviewTask(id)?.join(); Unit}
    fun invalidateSourceReviews()=synchronized(gate) {
        sourceWork.values.forEach {it.cancel()}
        sourceReviews.keys.toList().forEach {cancelSourceReview(it)}
    }
    suspend fun prepareSourceReview(id:String,peer:String,selected:FileEntry,request:suspend(FileRequest)->FileResponse,current:()->Boolean):PhoneDownloadSourceReview =
        prepareSourceReviewCore(id,peer,selected,request,current,null)
    internal suspend fun prepareBatchSourceReview(id:String,peer:String,selected:FileEntry,request:suspend(FileRequest)->FileResponse,current:()->Boolean,
        authority:AndroidBatchDownloads.SourceAuthority)=prepareSourceReviewCore(id,peer,selected,request,current,authority)
    private suspend fun prepareSourceReviewCore(id:String,peer:String,selected:FileEntry,request:suspend(FileRequest)->FileResponse,current:()->Boolean,
        authority:AndroidBatchDownloads.SourceAuthority?):PhoneDownloadSourceReview {
        synchronized(gate) {
            sourceReviews.values.filter {elapsed()-it.started>=300_000 || !it.current()}.map {it.review.id}.forEach {cancelSourceReview(it)}
            requireSourceJob(id,peer,authority)
            check(sourceWork.size+sourceReviews.size<8 && sourceTickets.size<8 && sourceReviews.values.none {it.review.job.id==id}) {"Close another source review first."}
        }
        return sourceTask(id,{
            requireSourceJob(id,peer,authority)
            check(sourceWork.size+sourceReviews.size<8 && sourceTickets.size<8 && sourceReviews.values.none {it.review.job.id==id}) {"Close another source review first."}
        }) {
            val coroutine=currentCoroutineContext()
            val original=synchronized(gate) {requireSourceJob(id,peer,authority)}
            check(current()) {"Open the current paired connection to review a source."}
            check(!selected.isDirectory && selected.name==original.name && selected.size==original.size)
            require(Rules.tokenPattern.matches(selected.id) && selected.version.matches(Regex("[a-f0-9]{64}")))
            val metadata=response(request(FileRequest.newBuilder().setOperation(FileOperation.GET_METADATA).setNodeId(selected.id).build())).entriesList.single()
            check(metadata.id==selected.id && metadata.grantId==selected.grantId && metadata.name==selected.name && metadata.size==selected.size &&
                metadata.version==selected.version && !metadata.isDirectory && metadata.permissions and StorageRules.READ!=0)
            val candidate=original.copy(node=metadata.id,version=metadata.version,ticket="",sourceExpiresAt=0)
            var ticket=""; var retained=false
            try {
                val prepared=response(request(FileRequest.newBuilder().setOperation(FileOperation.PREPARE_DOWNLOAD).setNodeId(metadata.id).setExpectedVersion(metadata.version).build()))
                check(prepared.hasDownload()); var info=prepared.download
                // Claim even a malformed fresh descriptor so its lease can be released, but never release another job's ticket.
                synchronized(gate) {
                    check(Rules.tokenPattern.matches(info.id) && records.values.none {it.peer==peer && it.ticket==info.id} && sourceTickets.add(peer to info.id)) {"The peer reused a source ticket."}
                    ticket=info.id
                }
                val expires=info.expiresAt
                while(true) {
                    currentCoroutineContext().ensureActive(); check(current()) {"The source review connection ended."}
                    validate(candidate.copy(ticket=ticket),info)
                    check(info.expiresAt==expires && info.state!=DownloadState.DOWNLOAD_FAILED)
                    if(info.state==DownloadState.DOWNLOAD_READY) break
                    delay(300)
                    val status=response(request(FileRequest.newBuilder().setOperation(FileOperation.DOWNLOAD_STATUS).setTransferId(ticket).build()))
                    check(status.hasDownload()); info=status.download
                }
                val hash=Rules.hex(info.sha256.toByteArray())
                check(info.preparedBytes==info.size)
                check(original.sha256.isEmpty() || hash==original.sha256) {"The selected file has different bytes. The saved partial file was kept."}
                val review=PhoneDownloadSourceReview(StorageRules.token(),original,metadata.version,hash,info.expiresAt,now()+300_000)
                synchronized(gate) {
                    coroutine.ensureActive()
                    check(current() && requireSourceJob(id,peer,authority)==original)
                    sourceReviews[review.id]=SourceOffer(review,metadata.id,info,request,current,elapsed(),authority); retained=true
                }
                review
            } finally {if(!retained && ticket.isNotEmpty()) releaseOffer(peer,ticket,request)}
        }
    }
    suspend fun confirmSourceReview(review:PhoneDownloadSourceReview)=confirmSourceReviewCore(review,null,null)
    internal suspend fun confirmBatchSourceReview(review:PhoneDownloadSourceReview,authority:AndroidBatchDownloads.SourceAuthority,beforeAdoption:(PhoneDownload)->Unit)=
        confirmSourceReviewCore(review,authority,beforeAdoption)
    private suspend fun confirmSourceReviewCore(review:PhoneDownloadSourceReview,authority:AndroidBatchDownloads.SourceAuthority?,beforeAdoption:((PhoneDownload)->Unit)?) {
        val offer=synchronized(gate) {
            val found=requireNotNull(sourceReviews[review.id]) {"Review a fresh source again."}
            check(found.authority===authority && (authority==null || authority.confirming && beforeAdoption!=null)) {"Approve this file's source through its original batch review."}
            check(found.review==review && review.job.id !in sourceWork)
            // Move the offer into tracked work before consuming it; shutdown can never miss its lease.
            found
        }
        sourceTask(review.job.id) {
            val coroutine=currentCoroutineContext()
            synchronized(gate) {check(sourceReviews.remove(review.id)===offer)}
            var adopted=false
            try {
                check(elapsed()-offer.started in 0 until 300_000 && offer.current()) {"The source review expired or its connection changed."}
                val status=response(offer.request(FileRequest.newBuilder().setOperation(FileOperation.DOWNLOAD_STATUS).setTransferId(offer.info.id).build()))
                check(status.hasDownload()); val fresh=status.download
                validate(review.job.copy(ticket=fresh.id,version=review.newVersion),fresh)
                check(fresh==offer.info) {"The prepared source changed. Review it again."}
                currentCoroutineContext().ensureActive()
                synchronized(gate) {
                    coroutine.ensureActive()
                    check(elapsed()-offer.started in 0 until 300_000 && offer.current() && requireSourceJob(review.job.id,review.job.peer,authority)==review.job)
                    beforeAdoption?.invoke(review.job); coroutine.ensureActive()
                    check(elapsed()-offer.started in 0 until 300_000 && fresh.expiresAt>now() && offer.current()); authority?.require(review.job)
                    put(review.job.copy(node=offer.node,version=review.newVersion,ticket=fresh.id,sha256=review.hash,sourceExpiresAt=fresh.expiresAt,error=""))
                    sourceTickets.remove(review.job.peer to fresh.id); adopted=true
                }
                recordPhoneActivity(activity,PhoneActivityEvent(PhoneActivityKind.DOWNLOAD,PhoneActivityOutcome.ACCEPTED,review.job.peer))
                release(review.job,offer.request)
            } finally {if(!adopted) releaseOffer(review.job.peer,offer.info.id,offer.request)}
        }
    }
    private suspend fun hashDocument(uri: Uri, size: Long, progress: (Long) -> Unit): String {
        val digest = MessageDigest.getInstance("SHA-256"); var count = 0L
        opened(uri, "r") { descriptor -> android.os.ParcelFileDescriptor.AutoCloseInputStream(descriptor).use { input ->
            val buffer = ByteArray(StorageRules.RANGE)
            while (true) { currentCoroutineContext().ensureActive(); val n = input.read(buffer); if (n < 0) break
                count += n; check(count <= size) { "The provider file size changed." }; digest.update(buffer, 0, n); progress(count) }
        } }
        check(count == size) { "The provider file is incomplete." }; return Rules.hex(digest.digest())
    }
    private suspend fun <T> opened(uri: Uri, mode: String, action: suspend (android.os.ParcelFileDescriptor) -> T): T = coroutineScope {
        val signal = CancellationSignal()
        val descriptor = java.util.concurrent.atomic.AtomicReference<android.os.ParcelFileDescriptor?>()
        val watcher = launch(Dispatchers.IO, start = CoroutineStart.UNDISPATCHED) {
            try { awaitCancellation() } finally { signal.cancel(); runCatching { descriptor.getAndSet(null)?.close() } }
        }
        try {
            val file = requireNotNull(context.contentResolver.openFileDescriptor(uri, mode, signal)) { "The provider file cannot be opened." }
            descriptor.set(file); currentCoroutineContext().ensureActive()
            check(android.system.OsConstants.S_ISREG(android.system.Os.fstat(file.fileDescriptor).st_mode)) { "Choose a provider that supports normal seekable files." }
            android.system.Os.lseek(file.fileDescriptor, 0, android.system.OsConstants.SEEK_SET)
            action(file)
        } finally { runCatching { descriptor.getAndSet(null)?.close() }; watcher.cancel() }
    }
    private suspend fun hashFile(file: File, size: Long, progress: (Long) -> Unit): String {
        val digest = MessageDigest.getInstance("SHA-256")
        var count = 0L
        FileInputStream(file).use { input ->
            val buffer = ByteArray(StorageRules.RANGE)
            while (true) {
                currentCoroutineContext().ensureActive()
                val n = input.read(buffer)
                if (n < 0) break
                count += n
                check(count <= size) { "The saved staging file grew during verification." }
                digest.update(buffer, 0, n)
                progress(count)
            }
        }
        check(count == size) { "The saved staging file became shorter during verification." }
        return Rules.hex(digest.digest())
    }
    private suspend fun hashPrefix(file: RandomAccessFile, length: Long, digest: MessageDigest): ByteArray {
        var chain = ByteArray(32); var offset = 0L; file.seek(0)
        while (offset < length) { currentCoroutineContext().ensureActive(); val bytes = ByteArray(minOf(StorageRules.RANGE.toLong(), length - offset).toInt())
            file.readFully(bytes); digest.update(bytes); chain = nextChain(chain, offset, bytes); offset += bytes.size }
        return chain
    }
    private fun nextChain(previous: ByteArray, offset: Long, bytes: ByteArray): ByteArray = MessageDigest.getInstance("SHA-256").run {
        update(previous); update(ByteBuffer.allocate(8).order(ByteOrder.LITTLE_ENDIAN).putLong(offset).array()); update(MessageDigest.getInstance("SHA-256").digest(bytes)); digest()
    }
    private fun fromHex(value: String) = value.chunked(2).map { it.toInt(16).toByte() }.toByteArray()
    private fun failure(error: Exception) = when (error) {
        is SecurityException -> "Android or the document provider denied access. Choose the destination again."
        is java.io.IOException -> "Storage or the connection could not finish this operation. Check space and access, then retry."
        else -> (error.message ?: "The download stopped.").filterNot { Character.isISOControl(it) }.take(512)
    }
    private fun encode(r: PhoneDownload) = JSONObject().put("id", r.id).put("peer", r.peer).put("name", r.name).put("node", r.node)
        .put("version", r.version).put("size", r.size).put("tree", r.tree).put("ticket", r.ticket).put("sha256", r.sha256)
        .put("offset", r.offset).put("chain", r.chain).put("phase", r.phase.name).put("error", r.error)
        .put("published", r.published).put("publishedName", r.publishedName).put("attempts", r.attempts)
        .put("parents", JSONArray().apply { r.parents.forEach { put(JSONObject().put("id", it.id).put("name", it.name)) } })
        .put("batchId", r.batchId).put("collision", r.collision.name).put("sourceExpiresAt",r.sourceExpiresAt)
    private fun decode(row: JSONObject): PhoneDownload {
        val r = PhoneDownload(row.getString("id"), row.getString("peer"), row.getString("name"), row.getString("node"), row.getString("version"),
            row.getLong("size"), row.getString("tree"), row.getString("ticket"), row.getString("sha256"), row.getLong("offset"), row.getString("chain"),
            PhoneDownloadPhase.valueOf(row.getString("phase")), row.getString("error"), row.getString("published"), row.getString("publishedName"), row.getInt("attempts"),
            parents = if (row.has("parents")) row.getJSONArray("parents").let { array -> require(array.length() <= 32); (0 until array.length()).map { SafFolderBinding(array.getJSONObject(it).getString("id"), array.getJSONObject(it).getString("name")) } } else emptyList(),
            batchId = if (row.has("batchId")) row.getString("batchId") else "", collision = PhoneCollision.valueOf(if (row.has("collision")) row.getString("collision") else PhoneCollision.KEEP_BOTH.name),
            sourceExpiresAt=if(row.has("sourceExpiresAt")) row.getLong("sourceExpiresAt") else 0)
        require(Rules.tokenPattern.matches(r.id) && Rules.tokenPattern.matches(r.node) && validOwner(r.peer) &&
            r.version.matches(Regex("[a-f0-9]{64}")) && r.size in 0..StorageRules.MAX_TRANSFER_SIZE && r.offset in 0..r.size &&
            (r.offset == r.size || r.offset % StorageRules.RANGE == 0L) && r.chain.matches(Regex("[a-f0-9]{64}")) &&
            (r.sha256.isEmpty() || r.sha256.matches(Regex("[a-f0-9]{64}"))) && (r.ticket.isEmpty() || Rules.tokenPattern.matches(r.ticket)) &&
            r.error.length <= 512 && r.tree.length <= 16_384 && r.published.length <= 16_384 && r.attempts in 0..8 && r.sourceExpiresAt in 0..StorageRules.MAX_TIMESTAMP)
        StorageRules.name(r.name); AndroidSafAccess.validateTree(Uri.parse(r.tree))
        if (r.phase == PhoneDownloadPhase.COMPLETED) require(r.offset == r.size && r.sha256.length == 64 && r.published.isNotEmpty())
        SafDestination.validateBindings(r.parents); require((!batchOnly || r.batchId.isNotEmpty()) && (r.batchId.isEmpty() || Rules.tokenPattern.matches(r.batchId)))
        return r
    }
    private fun validOwner(peer: String) = if(localCopy) peer.isEmpty() else peer.matches(Regex("[a-f0-9]{64}"))
    suspend fun shutdown() {
        val offers=synchronized(gate) { if (closed) return; closed = true; lifetime.cancel(); sourceReviews.values.toList().also {sourceReviews.clear()} }
        lifetime.coroutineContext.job.join()
        try { offers.forEach {releaseOffer(it.review.job.peer,it.info.id,it.request)} }
        finally {fileLock.release(); lockFile.close()}
    }
    override fun close() { runBlocking { shutdown() } }
}
