package com.iconnect.proof

import android.content.Context
import android.os.CancellationSignal
import com.google.protobuf.ByteString
import com.iconnect.protocol.*
import kotlinx.coroutines.*
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import org.json.JSONArray
import org.json.JSONObject
import java.io.File

data class PhoneSend(val id: String, val peer: String, val scope: String, val name: String, val size: Long,
    val state: DownloadState, val prepared: Long, val bytesRead: Long, val active: Boolean, val paused: Boolean)
data class PhonePreparedSource(val id:String,val peer:String,val name:String,val size:Long,val prepared:Long,val state:DownloadState,
    val expiresAt:Long,val preparing:Boolean,val paused:Boolean,val expired:Boolean)

/** Persistent peer/grant-bound source tickets; never exposes document IDs or URIs. */
class AndroidDownloadSources(private val context: Context, private val grants: AndroidStorageGrants,
    dispatcher: CoroutineDispatcher = Dispatchers.IO,
    private val elapsed: () -> Long = { android.os.SystemClock.elapsedRealtime() },
    private val now: () -> Long = System::currentTimeMillis) {
    private data class Source(val id: String, val peer: String, val grant: String, val document: String,
        val name: String, val version: String, val size: Long, val expires: Long,
        val state: DownloadState = DownloadState.DOWNLOAD_PREPARING, val hash: ByteArray = byteArrayOf(), val prepared: Long = 0, val paused: Boolean = false)
    private data class Work(val scope: String, val job: Job, val signal: CancellationSignal)
    private data class Activity(val scope: String, val progressAt: Long, val highWater: Long = 0, val bytesRead: Long = 0)
    private val gate = Any()
    private val records = mutableMapOf<String, Source>()
    private val work = mutableMapOf<String, Work>()
    private val activity = mutableMapOf<String, Activity>()
    private val blocked = mutableSetOf<String>()
    private val revisions = mutableMapOf<String, Long>()
    private val mutable = MutableStateFlow<List<PhoneSend>>(emptyList())
    val state = mutable.asStateFlow()
    private val mutableInventory=MutableStateFlow<List<PhonePreparedSource>>(emptyList())
    val inventory=mutableInventory.asStateFlow()
    private val closed=java.util.concurrent.atomic.AtomicBoolean(false)
    private var lastEmission = 0L
    private val lifetime = CoroutineScope(SupervisorJob() + dispatcher)
    private val hashing = Semaphore(2)
    private val stateFile = PrivateJsonFile(File(context.filesDir, "download-sources.json"), 4 * 1024 * 1024)
    private val recovery = StateRecovery(context.filesDir)
    fun recoveryIssues() = synchronized(gate) { recovery.all() }
    fun preservedRecords() = synchronized(gate) { recovery.archivedCount() }
    fun clearPreservedRecords() = synchronized(gate) { recovery.clearArchives() }
    fun archiveRecovery(id: String) = synchronized(gate) { check(work.isEmpty()) { "Pause sending before reviewing saved source state." }; recovery.archive(id) }
    init {
        runCatching {
            check(listOf(".new",".bak").none {File(context.filesDir,"download-sources.json"+it).exists()}) {"Uncommitted source approvals need local review."}
            if (!stateFile.exists()) { check(!File(context.filesDir, "download-sources.json.new").exists()); return@runCatching }
            val saved = JSONArray(stateFile.read())
            require(saved.length() <= 128)
            val parsed = (0 until saved.length()).map { index ->
                val row = saved.getJSONObject(index)
                val id = row.getString("id"); val peer = row.getString("peer"); val grant = row.getString("grant")
                val name = row.getString("name"); val version = row.getString("version"); val size = row.getLong("size")
                require(Rules.tokenPattern.matches(id) && Rules.tokenPattern.matches(grant) && peer.matches(Regex("[a-f0-9]{64}")) &&
                    version.matches(Regex("[a-f0-9]{64}")) && size in 0..StorageRules.MAX_TRANSFER_SIZE)
                StorageRules.name(name)
                val hash = Rules.decode(row.optString("hash"))
                require(hash.size in listOf(0, 32))
                val document = row.getString("document"); require(document.length in 1..4096)
                val expires = row.getLong("expires"); require(expires in 0..StorageRules.MAX_TIMESTAMP)
                if (row.has("paused")) require(row.get("paused") is Boolean)
                Source(id, peer, grant, document, name, version, size, expires,
                    if (hash.size == 32) DownloadState.DOWNLOAD_READY else DownloadState.DOWNLOAD_PREPARING, hash,
                    if (hash.size == 32) size else 0, row.optBoolean("paused", false))
            }
            require(parsed.map { it.id }.distinct().size == parsed.size)
            records.putAll(parsed.associateBy { it.id })
            purgeExpired()
        }.onFailure {
            records.clear()
            recovery.add(File(context.filesDir, "download-sources.json"), "Saved sending tickets could not be validated. Archive this record before preparing new transfers; file copies are kept.")
        }
        synchronized(gate) { emit() }
        lifetime.launch { while (isActive) { delay(1_000); refresh() } }
    }
    fun prepare(peer: String, grant: String, document: String, name: String, version: String, size: Long, scope: String): DownloadInfo = synchronized(gate) {
        if(closed.get()) throw StorageFailure(FileStatus.ACCESS_DENIED,"The source store is closed."); recovery.requireHealthy()
        purgeExpired()
        // Independent jobs need independent leases, even for the same file.
        if (records.size >= 128 || records.values.count { it.peer == peer } >= 8) throw StorageFailure(FileStatus.RESOURCE_EXHAUSTED, "Close another download before preparing a new one.")
        val record = Source(StorageRules.token(), peer, grant, document, name, version, size, now() + 24 * 60 * 60 * 1000)
        records[record.id] = record
        try { save() } catch (e: Exception) { records.remove(record.id); throw e }
        bind(record, scope); start(record, scope); emit(); describe(record)
    }
    fun status(peer: String, id: String, scope: String, reader: AndroidFileService, signal: CancellationSignal): DownloadInfo {
        val source = find(peer, id)
        reader.sourceSnapshot(peer, source.grant, source.document, source.version, signal)
        synchronized(gate) { val current = find(peer, id); bind(current, scope); start(current, scope); emit(); return describe(current) }
    }
    fun read(peer: String, request: FileRequest, reader: AndroidFileService, signal: CancellationSignal, scope: String): FileResponse {
        val source = find(peer, request.transferId)
        if (source.state != DownloadState.DOWNLOAD_READY) throw StorageFailure(FileStatus.CONFLICT, "Wait for source preparation.")
        val result = reader.sourceRange(peer, source.grant, source.document, source.version, request, signal)
        synchronized(gate) {
            find(peer, request.transferId) // A concurrent owner pause cannot publish an old read.
            bind(source, scope)
            activity[source.id]?.let {
                val end = request.offset + result.content.size()
                activity[source.id] = it.copy(progressAt = if (end > it.highWater) elapsed() else it.progressAt,
                    highWater = maxOf(end, it.highWater), bytesRead = it.bytesRead + result.content.size())
            }
            emit(throttle = true)
        }
        return result
    }
    private fun find(peer: String, id: String): Source = synchronized(gate) {
        if(closed.get()) throw StorageFailure(FileStatus.ACCESS_DENIED,"The source store is closed."); recovery.requireHealthy()
        records[id]?.takeIf { it.peer == peer && it.expires > now() && !it.paused && id !in blocked }
            ?: throw StorageFailure(FileStatus.ACCESS_DENIED, "The source download expired or belongs to another device.")
    }
    fun release(peer: String, id: String) = synchronized(gate) {
        if (records[id]?.peer != peer) return@synchronized
        blocked.add(id); cancelWork(id); activity.remove(id); emit()
        val after = records - id; save(after); records.remove(id); blocked.remove(id); revisions.remove(id); emit()
    }
    fun blockPeer(peer: String): Set<String> = synchronized(gate) {
        val ids = records.values.filter { it.peer == peer }.map { it.id }.toSet()
        ids.forEach { revisions[it] = (revisions[it] ?: 0) + 1; blocked.add(it); activity.remove(it); cancelWork(it) }; emit()
        ids
    }
    fun releasePeer(peer: String, captured: Set<String>) = synchronized(gate) {
        val ids = captured.filter { records[it]?.peer == peer }.toSet()
        val after = records.filterKeys { it !in ids }; save(after)
        ids.forEach { records.remove(it); blocked.remove(it); revisions.remove(it) }; emit()
    }
    fun cancelScope(scope: String, preserve: Set<String> = emptySet()) = synchronized(gate) {
        work.filter { it.value.scope == scope && it.key !in preserve }.keys.toList().forEach(::cancelWork)
        activity.filter { it.value.scope == scope && it.key !in preserve }.keys.toList().forEach(activity::remove)
        emit()
    }
    /** Immediate runtime stop; persistPause() then makes it survive process recreation. */
    fun block(id: String): Long = synchronized(gate) {
        require(id in records); val revision = (revisions[id] ?: 0) + 1; revisions[id] = revision
        blocked.add(id); cancelWork(id); emit(); revision
    }
    fun reserveResume(id: String): Long = synchronized(gate) { require(id in records); ((revisions[id] ?: 0) + 1).also { revisions[id] = it } }
    fun persistPause(id: String, revision: Long? = null) = synchronized(gate) {
        val source = records[id] ?: return@synchronized
        if (revision != null && revision != revisions[id]) return@synchronized
        val paused = source.copy(paused = true); save(records + (id to paused)); records[id] = paused; emit()
    }
    fun resume(id: String, peer: String, revision: Long? = null) {
        val (source, expected) = synchronized(gate) { requireNotNull(records[id]?.takeIf { it.peer == peer }) to (revision ?: revisions[id] ?: 0) }
        StorageRules.requirePermission(grants.get(source.grant, peer).permissions, StorageRules.READ)
        synchronized(gate) {
            val current = records[id]
            check(current != null && current.peer == peer && (revisions[id] ?: 0) == expected && source.expires > now()) { "This sending transfer changed. Retry the current action." }
            val resumed = current.copy(paused = false); save(records + (id to resumed)); records[id] = resumed
            blocked.remove(id); activity.remove(id); emit()
        }
    }
    fun canUse(peer: String, id: String, includePaused: Boolean = false) = synchronized(gate) {
        !closed.get() && !recovery.needed() && records[id]?.let { it.peer == peer && it.expires > now() &&
            (includePaused || !it.paused && id !in blocked) } == true
    }
    fun isLive(peer: String, id: String, scope: String) = synchronized(gate) {
        canUse(peer, id) && records[id]?.state != DownloadState.DOWNLOAD_FAILED &&
            activity[id]?.let { it.scope == scope && elapsed() - it.progressAt < IDLE_MS } == true
    }
    fun refresh() {
        val snapshot = synchronized(gate) { records.values.toList() }
        // Never acquire grant policy while holding the source gate: requests use the opposite order.
        val denied = snapshot.filter { source -> runCatching {
            grants.get(source.grant, source.peer).permissions and StorageRules.READ == 0
        }.getOrDefault(true) }.map { it.id }.toSet()
        synchronized(gate) {
            activity.filter { (id, value) -> id in denied || elapsed() - value.progressAt >= IDLE_MS ||
                records[id]?.let { it.paused || it.state == DownloadState.DOWNLOAD_FAILED || it.expires <= now() } != false }
                .keys.toList().forEach { activity.remove(it); cancelWork(it) }
            emit()
        }
    }
    private fun bind(source: Source, scope: String) {
        val old = activity[source.id]
        if (old == null || old.scope != scope) activity[source.id] = Activity(scope, elapsed())
    }
    private fun cancelWork(id: String) { work[id]?.let { it.signal.cancel(); it.job.cancel() } }
    private fun start(source: Source, scope: String) {
        // The provider lookup in status() can overlap completion. Never restart
        // hashing from an older PREPARING snapshot after the record became ready.
        if (records[source.id]?.let { it.state != DownloadState.DOWNLOAD_PREPARING || it.paused } != false || source.id in blocked || source.id in work) return
        val signal = CancellationSignal()
        val job = lifetime.launch(start = CoroutineStart.LAZY) {
            val reader = AndroidFileService(context, grants)
            try {
                hashing.withPermit {
                    val hash = runInterruptible {
                        reader.hashSource(source.peer, source.grant, source.document, source.version, source.size, signal) { offset ->
                            synchronized(gate) { records[source.id]?.let { records[source.id] = it.copy(prepared = offset)
                                if (offset > it.prepared) activity[source.id]?.takeIf { active -> active.scope == scope }?.let { active -> activity[source.id] = active.copy(progressAt = elapsed()) }
                                emit(throttle = true) } }
                        }
                    }
                    currentCoroutineContext().ensureActive()
                    synchronized(gate) { records[source.id]?.let {
                        if (source.id !in blocked && !it.paused) {
                            records[source.id] = it.copy(state = DownloadState.DOWNLOAD_READY, hash = hash, prepared = it.size); save()
                            activity[source.id]?.takeIf { active -> active.scope == scope }?.let { active -> activity[source.id] = active.copy(progressAt = elapsed()) }
                            emit()
                        }
                    } }
                }
            } catch (e: Exception) {
                synchronized(gate) { records[source.id]?.let { records[source.id] = it.copy(state =
                    if (e is CancellationException || signal.isCanceled) DownloadState.DOWNLOAD_PREPARING else DownloadState.DOWNLOAD_FAILED, hash = byteArrayOf(), prepared = 0)
                    runCatching { save() }; emit() } }
            } finally { reader.close() }
        }
        work[source.id] = Work(scope, job, signal)
        job.invokeOnCompletion { synchronized(gate) { if (work[source.id]?.job === job) {work.remove(source.id); emit()} } }
        job.start()
    }
    private fun describe(source: Source) = DownloadInfo.newBuilder().setId(source.id).setName(source.name).setSize(source.size)
        .setVersion(source.version).setState(source.state).setExpiresAt(source.expires).setPreparedBytes(source.prepared)
        .setSha256(ByteString.copyFrom(source.hash)).build()
    private fun emit(throttle: Boolean = false) {
        if (throttle && elapsed() - lastEmission < 100) return
        lastEmission = elapsed()
        val at=now()
        mutableInventory.value=records.values.toList().asReversed().map {source->PhonePreparedSource(source.id,source.peer,source.name,source.size,source.prepared,
            source.state,source.expires,source.id in work,source.paused || source.id in blocked,source.expires<=at)}
        mutable.value = records.values.filter { it.expires > now() }.map { source ->
            val current = activity[source.id]; val paused = source.paused || source.id in blocked
            PhoneSend(source.id, source.peer, current?.scope ?: "", source.name, source.size, source.state, source.prepared,
                current?.bytesRead ?: 0, !closed.get() && !recovery.needed() && current != null && !paused && source.state != DownloadState.DOWNLOAD_FAILED && elapsed() - current.progressAt < IDLE_MS, paused)
        }
    }
    private fun save(values: Map<String, Source> = records) {
        if (recovery.needed() && values.isEmpty()) return
        recovery.requireHealthy()
        val array = JSONArray()
        values.values.forEach { source -> array.put(JSONObject().put("id", source.id).put("peer", source.peer).put("grant", source.grant)
            .put("document", source.document).put("name", source.name).put("version", source.version).put("size", source.size)
            .put("expires", source.expires).put("hash", Rules.b64(source.hash)).put("paused", source.paused)) }
        val bytes = array.toString().toByteArray(Charsets.UTF_8)
        if (bytes.size > 4 * 1024 * 1024 - 32768) throw java.io.IOException("Source state is full.")
        try {stateFile.write(bytes.toString(Charsets.UTF_8))}
        catch(e:Exception) {recovery.block("Saved source approvals could not be updated. Prepared downloads are paused; original files were kept. Restore storage access and reopen iConnect to review current progress."); emit(); throw e}
    }
    // No provider operation or grant change is performed by operational ticket cleanup.
    private fun purgeExpired():Pair<Int,List<Job>> {
        val at=now(); val ids=records.values.filter {it.expires<=at}.map {it.id}.toSet()
        if(ids.isEmpty()) return 0 to emptyList()
        val jobs=ids.mapNotNull {work[it]?.job}
        ids.forEach {blocked.add(it); activity.remove(it); cancelWork(it)}
        save(records.filterKeys {it !in ids})
        ids.forEach {records.remove(it); blocked.remove(it); revisions.remove(it)}
        emit(); return ids.size to jobs
    }
    suspend fun stopOwned(id:String,allowed:()->Boolean):Unit=withContext(Dispatchers.IO) {
        val stopping=grants.authorized {synchronized(gate) {
            check(!closed.get() && allowed()) {"Reopen source controls in the foreground."}; recovery.requireHealthy()
            if(id !in records) return@synchronized null
            val job=work[id]?.job; blocked.add(id); activity.remove(id); cancelWork(id); emit()
            save(records-id); records.remove(id); blocked.remove(id); revisions.remove(id); emit(); job
        }}
        stopping?.join()
    }
    suspend fun removeExpiredOwned(allowed:()->Boolean):Int=withContext(Dispatchers.IO) {
        val result=grants.authorized {synchronized(gate) {
            check(!closed.get() && allowed()) {"Reopen source controls in the foreground."}; recovery.requireHealthy(); purgeExpired()
        }}
        result.second.joinAll(); result.first
    }
    fun close() {closed.set(true); synchronized(gate) {work.values.forEach {it.signal.cancel()}; lifetime.cancel(); emit()}}
    suspend fun shutdown() { close(); lifetime.coroutineContext.job.join() }
    companion object { const val IDLE_MS = 120_000L }
}
