package com.iconnect.proof

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.provider.DocumentsContract as Docs
import androidx.core.net.toUri
import com.iconnect.protocol.*
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import org.json.JSONObject
import java.io.File
import kotlin.coroutines.CoroutineContext

data class PhoneLocalBatch(val id: String, val label: String, val items: Int, val bytes: Long, val issues: Int,
    val started: Boolean, val batch: PhoneBatch?)
data class PhoneLocalBatchState(val items: List<PhoneLocalBatch> = emptyList(), val orphans: List<PhoneDownload> = emptyList(),
    val planning: Boolean = false, val issues: List<StateRecoveryIssue> = emptyList(), val preserved: Int = 0, val error: String = "",
    val liveBytes:Map<String,Long> = emptyMap(),val payloadEstimates:Map<String,PhonePayloadRateEstimate> = emptyMap(),
    val finalizations: Map<String, PhoneIncomingFinalization> = emptyMap())

/** A separate local purpose. Empty local ownership is never a synthetic paired device. */
class PhoneLocalBatches internal constructor(private val context: Context, private val directory: File = File(context.filesDir, "local-batch-copies")) {
    private val gate = Any()
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val sourceDirectory = File(directory, "sources").apply { check(mkdirs() || isDirectory) }
    private val files = AndroidDownloadManager(context, File(directory, "receiving"), localCopy = true, batchOnly = true)
    private val batches = AndroidBatchDownloads(context, files, File(directory, "batches"), localCopy = true, dispatch = ::dispatch)
    private val access = AndroidSafAccess(context)
    private val prefix = "local-batch:${Rules.hex(Rules.digest(directory.canonicalPath.toByteArray())).take(32)}:"
    private val recovery = StateRecovery(sourceDirectory)
    private val plans = linkedMapOf<String, LocalBatchPlan>()
    private val work = mutableMapOf<String, Job>()
    private val controls = mutableMapOf<String, Job>()
    private var planning: Job? = null
    @Volatile private var visible = false
    @Volatile private var closed = false
    private data class Destination(val id: String, val tree: Uri, val flags: Int, val collision: PhoneCollision)
    private var pending: Destination? = null
    private var error = ""
    private val mutable = MutableStateFlow(PhoneLocalBatchState())
    private val payloadRates=PhoneLocalPayloadRates()
    val state = mutable.asStateFlow()
    internal var activity: (PhoneActivityEvent) -> Unit = {}
    init {
        files.activity = { recordPhoneActivity(activity, it) }; batches.activity = { recordPhoneActivity(activity, it) }
        val journals = StateRecovery.journals(sourceDirectory, 8)
        if (journals.size > 8) recovery.block("More than eight local batch source records exist. Keep private progress and review storage before restarting.")
        journals.take(8).forEach { path -> try {
            val plan = LocalBatchSources.decode(JSONObject(PrivateJsonFile(path, 8 * 1024 * 1024).read()))
            require(path.name == "${plan.id}.json" && plan.id !in plans); plans[plan.id] = plan
        } catch (_: Exception) { recovery.add(path, "Local batch source access could not be validated. Preserve its metadata; originals, provider copies and receiving progress were kept.") } }
        if (!recovery.needed()) access.reconcile(prefix, plans.values.flatMap { leases(it).entries }.associate { it.toPair() }, Intent.FLAG_GRANT_READ_URI_PERMISSION)
        else plans.values.forEach { plan -> leases(plan).forEach { (owner, tree) -> access.retainRecorded(owner, tree, Intent.FLAG_GRANT_READ_URI_PERMISSION) } }
        emit(); scope.launch { batches.state.collect { emit() } }; scope.launch { files.state.collect { emit() } }
        scope.launch {files.payloadProgress.collect {refreshPayload()}}
        scope.launch {while(isActive) {delay(1000); if(visible) refreshPayload()}}
    }
    private fun leases(plan: LocalBatchPlan) = plan.roots.map { it.tree.toUri() }.distinct().mapIndexed { index, tree -> "$prefix${plan.id}:$index" to tree }.toMap()
    private fun available() = visible && !closed
    private fun requireAvailable() { check(available()) { "Return to the app to manage local folder copies." } }
    private fun healthy() { recovery.requireHealthy(); files.requireHealthy(); check(batches.recoveryIssues().isEmpty()) { "Preserve damaged receiving metadata before starting new local batches." } }
    private fun dispatch(worker: CoroutineContext, action: () -> Job): Job = synchronized(gate) { worker.ensureActive(); requireAvailable(); action() }
    fun setVisible(value: Boolean) {
        // Display measurements are withdrawn with foreground authority.
        synchronized(gate) { visible = value; if (!value) { planning?.cancel(); work.values.forEach { it.cancel() }; controls.values.forEach { it.cancel() }; batches.pauseAll(); files.pauseAll() } }
        if (value) synchronized(gate) { pending.also { pending = null } }?.let(::applyDestination)
        refreshPayload()
    }
    fun acceptDestination(id: String, tree: Uri, flags: Int, collision: PhoneCollision) {
        val value = Destination(id, tree, flags, collision)
        synchronized(gate) { check(!closed); if (!visible) { pending = value; return } }; applyDestination(value)
    }
    private fun applyDestination(value: Destination) { scope.launch { runCatching { chooseDestination(value.id, value.tree, value.flags, value.collision) }
        .onFailure { if (it !is CancellationException) report("The destination could not be approved. Review local batch progress and folder access before retrying.") } } }
    internal suspend fun prepare(selected: List<LocalCopySelection>): String = coroutineScope {
        val worker = currentCoroutineContext().job
        synchronized(gate) { requireAvailable(); healthy(); check(planning == null && state.value.items.size < 8); planning = worker; emit() }
        val id = StorageRules.token(); val roots = selected.map { it.tree }.distinct(); val owners = roots.mapIndexed { index, tree -> "$prefix$id:$index" to tree }
        try {
            require(selected.size in 1..128 && roots.size <= 32)
            withContext(Dispatchers.IO) { owners.forEach { (owner, tree) -> access.acquire(owner, tree, Intent.FLAG_GRANT_READ_URI_PERMISSION) } }
            val scanned = LocalBatchSources.scan(context, selected, ::available).copy(id = id)
            withContext(Dispatchers.IO) { synchronized(gate) {
                worker.ensureActive(); requireAvailable(); healthy()
                check(state.value.items.size < 8 && plans.values.sumOf { it.manifest.bytes } <= 16L * 1024 * 1024 * 1024 - scanned.manifest.bytes)
                save(scanned); plans[id] = scanned; error = ""
            } }; id
        } catch (failure: Exception) {
            // Cancellation may arrive after AtomicFile committed the draft. Its
            // retained source purpose must keep access even if no ID was returned.
            if (synchronized(gate) { id !in plans }) withContext(NonCancellable + Dispatchers.IO) { owners.forEach { (owner, _) -> runCatching { access.release(owner) } } }
            throw failure
        }
        finally { synchronized(gate) { if (planning === worker) planning = null; emit() } }
    }
    fun cancelPlanning() { synchronized(gate) { planning?.cancel() } }
    private fun save(plan: LocalBatchPlan) { LocalBatchSources.validate(plan); PrivateJsonFile(File(sourceDirectory, "${plan.id}.json"), 8 * 1024 * 1024).write(LocalBatchSources.encode(plan).toString()) }
    suspend fun chooseDestination(id: String, tree: Uri, flags: Int, collision: PhoneCollision): Job = withContext(Dispatchers.IO) {
        synchronized(gate) {
            requireAvailable(); healthy(); val plan = plans.getValue(id)
            check(!plan.started && batches.state.value.none { it.id == id } && files.state.value.none { it.batchId == id }) { "This source may already have produced copies. Resume its original progress or review removal before selecting a new copy." }
            AndroidSafAccess.validateTree(tree)
            val owner = "${prefix}picker:$id"; access.acquire(owner, tree, flags, Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION)
            try {
                SafDestination(context, tree).validate()
                plan.roots.filter { it.directory }.forEach { root -> if (root.tree.toUri().authority == tree.authority) {
                    val from = Docs.buildDocumentUriUsingTree(root.tree.toUri(), root.document); val target = Docs.buildDocumentUriUsingTree(tree, Docs.getTreeDocumentId(tree))
                    check(root.document != Docs.getTreeDocumentId(tree) && !Docs.isChildDocument(context.contentResolver, from, target)) { "Choose a destination outside the selected source folders." }
                } }
                val used = plan.copy(started = true); save(used); plans[id] = used; batches.enqueue("", "", used.manifest, tree, collision, id)
            }
            finally { access.release(owner) }
            error = ""; emit()
        }; resume(id)
    }
    fun resume(id: String, anotherCopy: String = ""): Job = synchronized(gate) {
        requireAvailable(); healthy(); check(id !in work); requireContextIdle(id)
        val job = scope.launch(start = CoroutineStart.LAZY) {
            try {
                val plan = synchronized(gate) { plans[id] }
                if (plan != null) LocalBatchSources.read(context, plan, ::available) { request -> dispatch(currentCoroutineContext()) { batches.resume(id, "", "", request, anotherCopy) }.join() }
                else dispatch(currentCoroutineContext()) { batches.resume(id, "", "", { error("Source access is missing. Only already received or recorded copies can finish.") }, anotherCopy) }.join()
            } catch (failure: Exception) { if (failure !is CancellationException) report("The local batch stopped. Review saved progress and provider access before retrying.") }
        }
        work[id] = job; job.invokeOnCompletion { synchronized(gate) { if (work[id] === job) work.remove(id); emit() } }; emit(); job.start(); job
    }
    fun pause(id: String) { synchronized(gate) { work[id]?.cancel(); batches.pause(id) } }
    suspend fun cancel(id: String) = withContext(Dispatchers.IO) {
        requireAvailable(); val job = synchronized(gate) { requireContextIdle(id); work[id]?.also { it.cancel() }; batches.pause(id); work[id] }; job?.join(); batches.awaitIdle(id)
        if (batches.state.value.any { it.id == id }) batches.cancel(id)
        else { check(plans[id]?.started != true && files.state.value.none { it.batchId == id }) { "Receiving history may be missing. Review retained receipts and progress references first." }; removeSource(id) }; emit()
    }
    fun skipUncertainFolder(id: String, index: Int) { synchronized(gate) { requireAvailable(); batches.skipUncertainFolder(id, index); emit() } }
    fun canRemove(id: String) = batches.canRemove(id)
    suspend fun remove(id: String) = withContext(Dispatchers.IO) { synchronized(gate) { requireAvailable(); check(id !in work); batches.remove(id); removeSource(id); emit() } }
    suspend fun removeMissing(id: String) = withContext(Dispatchers.IO) { synchronized(gate) {
        requireAvailable(); check(id !in work && batches.state.value.none { it.id == id } && files.state.value.none { it.batchId == id }); removeSource(id); emit()
    } }
    fun child(id: String) = files.state.value.singleOrNull { it.id == id }
    fun activeChild(batch: String) = files.state.value.firstOrNull { it.batchId == batch && it.active }
    suspend fun stopOrphan(id: String) = coroutineScope {
        val worker = currentCoroutineContext().job
        synchronized(gate) { requireAvailable(); requireOrphan(id); controls[id] = worker; emit() }
        try { withContext(Dispatchers.IO) { files.cancel(id) } } finally { synchronized(gate) { controls.remove(id); emit() } }
    }
    suspend fun removeOrphan(id: String) = withContext(Dispatchers.IO) { synchronized(gate) { requireAvailable(); requireOrphan(id); files.remove(id); emit() } }
    fun pauseOrphan(id: String) { synchronized(gate) { controls[id]?.cancel(); files.pause(id) } }
    fun checkOrphan(id: String): Job = synchronized(gate) {
        requireAvailable(); val row = requireOrphan(id)
        check(row.phase == PhoneDownloadPhase.PUBLICATION_INTERRUPTED && row.published.isNotEmpty() && row.offset == row.size && row.sha256.length == 64) { "Only a recorded provider copy can be rechecked. This cannot create a new copy." }
        val job = scope.launch(start = CoroutineStart.LAZY) { dispatch(currentCoroutineContext()) { files.publishBatchFile(id, false, row.batchId) }.join() }
        controls[id] = job; job.invokeOnCompletion { synchronized(gate) { controls.remove(id); emit() } }; emit(); job.start(); job
    }
    private fun requireOrphan(id: String): PhoneDownload {
        val row = files.state.value.single { it.id == id }; check(!row.active && id !in controls)
        check(batches.state.value.none { it.active || it.id == row.batchId && it.items.any { item -> item.child == id } }); return row
    }
    private fun requireContextIdle(id: String) { check(files.state.value.none { it.batchId == id && it.id in controls }) { "Pause unattached receipt work and wait for it to finish before controlling this batch." } }
    internal fun page(id: String, start: Int): List<PhoneTransferItem> = synchronized(gate) { require(start >= 0); plans[id]?.manifest?.items?.drop(start)?.take(20) ?: emptyList() }
    suspend fun preserve(id: String) = withContext(Dispatchers.IO) { synchronized(gate) {
        requireAvailable(); check(work.isEmpty() && controls.isEmpty() && planning == null && files.state.value.none { it.active } && batches.state.value.none { it.active })
        if (recovery.all().any { it.id == id }) recovery.archive(id) { key -> access.reconcile("$prefix$key:", emptyMap(), Intent.FLAG_GRANT_READ_URI_PERMISSION) }
        else if (batches.recoveryIssues().any { it.id == id }) batches.archiveRecovery(id) else files.archiveRecovery(id); emit()
    } }
    suspend fun clearPreserved() = withContext(Dispatchers.IO) { synchronized(gate) {
        requireAvailable(); check(work.isEmpty() && controls.isEmpty() && planning == null && files.state.value.none { it.active } && batches.state.value.none { it.active })
        recovery.clearArchives(); batches.clearPreservedRecords(); files.clearPreservedRecords(); emit()
    } }
    private fun removeSource(id: String) { require(Rules.tokenPattern.matches(id)); android.util.AtomicFile(File(sourceDirectory, "$id.json")).delete()
        check(listOf(File(sourceDirectory, "$id.json"), File(sourceDirectory, "$id.json.new"), File(sourceDirectory, "$id.json.bak")).none { it.exists() }); plans.remove(id); access.reconcile("$prefix$id:", emptyMap(), Intent.FLAG_GRANT_READ_URI_PERMISSION) }
    fun report(message: String) { synchronized(gate) { error = message; emit() } }
    private fun emit() = synchronized(gate) {
        val receiving = batches.state.value.associateBy { it.id }; val loose = files.state.value.filter { file -> receiving[file.batchId]?.items?.none { it.child == file.id } != false }
        val ids = plans.keys + receiving.keys + loose.map { it.batchId }.filter { it.isNotEmpty() }
        refreshPayload(PhoneLocalBatchState(ids.distinct().map { id -> val plan = plans[id]; val row = receiving[id]
            PhoneLocalBatch(id, plan?.roots?.joinToString { it.name } ?: "Source plan unavailable", plan?.nodes?.size ?: row?.items?.size ?: loose.count { it.batchId == id },
                plan?.manifest?.bytes ?: row?.items?.filter { !it.source.directory && it.source.error.isEmpty() }?.sumOf { it.source.size } ?: 0,
                plan?.nodes?.count { it.source.error.isNotEmpty() } ?: 0, plan?.started ?: true, row?.copy(active = row.active || id in work)) },
            loose.map { it.copy(active = it.active || it.id in controls) }, planning != null, recovery.all() + batches.recoveryIssues() + files.recoveryIssues(), recovery.archivedCount() + batches.preservedRecords() + files.preservedRecords(), error))
    }
    private fun refreshPayload(state: PhoneLocalBatchState? = null)=synchronized(gate) {
        val available = available()
        val view=if(available) payloadRates.refresh(files.state.value,files.payloadProgress.value,android.os.SystemClock.elapsedRealtime())
            else {payloadRates.clear(); PhoneLocalPayloadView()}
        val finalizations = if (available) files.readFinalizations() else emptyMap()
        mutable.value=(state ?: mutable.value).copy(liveBytes=view.bytes,payloadEstimates=view.estimates,finalizations=finalizations)
    }
    suspend fun shutdown() {
        val tasks = synchronized(gate) { if (closed) return; closed = true; visible = false; pending = null; planning?.cancel(); (work.values + controls.values).forEach { it.cancel() }; batches.pauseAll(); files.pauseAll(); (work.values + controls.values).toList() to planning }
        refreshPayload()
        withContext(NonCancellable) { tasks.first.joinAll(); tasks.second?.join(); try { batches.shutdown() } finally { files.shutdown() }; scope.cancel(); scope.coroutineContext.job.join() }
    }
}
