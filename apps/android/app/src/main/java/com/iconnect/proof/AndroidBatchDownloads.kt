package com.iconnect.proof

import android.content.Context
import android.net.Uri
import android.provider.DocumentsContract as Docs
import android.util.AtomicFile
import androidx.core.net.toUri
import com.iconnect.protocol.*
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.RandomAccessFile

enum class PhoneBatchPhase { PAUSED, RUNNING, INTERRUPTED, REVIEW, COMPLETED, COMPLETED_WITH_ISSUES, CANCELED }
enum class PhoneBatchItemPhase { PENDING, CREATING, RUNNING, COMPLETED, SKIPPED, FAILED, INTERRUPTED, CANCELED }
data class PhoneBatchItem(val source: PhoneTransferItem, val phase: PhoneBatchItemPhase = PhoneBatchItemPhase.PENDING,
    val document: String = "", val localName: String = "", val child: String = "", val hash: String = "", val published: String = "", val error: String = "",
    val sourceApproval:PhoneBatchSourceApproval?=null)
data class PhoneBatch(val id: String, val peer: String, val epoch: String, val tree: String, val collision: PhoneCollision,
    val items: List<PhoneBatchItem>, val phase: PhoneBatchPhase = PhoneBatchPhase.PAUSED, val error: String = "", val active: Boolean = false) {
    val completed get() = items.count { it.phase == PhoneBatchItemPhase.COMPLETED }
    val issues get() = items.count { it.phase in setOf(PhoneBatchItemPhase.FAILED, PhoneBatchItemPhase.SKIPPED) }
    val verifiedBytes get() = items.filter { !it.source.directory && it.phase == PhoneBatchItemPhase.COMPLETED }.sumOf { it.source.size }
}

/** Foreground batch orchestration. Unknown provider objects are preserved, never adopted or rewritten. */
class AndroidBatchDownloads(private val context: Context, private val files: AndroidDownloadManager,
    private val directory: File = File(context.filesDir, "batches"), private val localCopy: Boolean = false,
    private val dispatch: ((kotlin.coroutines.CoroutineContext, () -> Job) -> Job)? = null) : AutoCloseable {
    internal var activity:(PhoneActivityEvent)->Unit={}
    private val gate = files.ownershipGate
    private val records = linkedMapOf<String, PhoneBatch>()
    private val work = mutableMapOf<String, Job>()
    private val canceling = mutableMapOf<String, CompletableDeferred<Unit>>()
    private data class SourceOffer(val review:PhoneBatchDownloadSourceReview,val authority:SourceAuthority)
    private val sourceReviews=mutableMapOf<String,SourceOffer>()
    private val sourceWork=mutableMapOf<String,Job>()
    private val lifetime = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val slots = Semaphore(2)
    private val access = AndroidSafAccess(context)
    private val prefix = "batch:${Rules.hex(Rules.digest(directory.canonicalPath.toByteArray())).take(32)}:"
    private val lockFile: RandomAccessFile
    private val lock: java.nio.channels.FileLock
    private var closed = false
    private val mutable = MutableStateFlow<List<PhoneBatch>>(emptyList())
    val state = mutable.asStateFlow()
    private val recovery = StateRecovery(directory)
    fun recoveryIssues() = synchronized(gate) { recovery.all() }
    fun preservedRecords() = synchronized(gate) { recovery.archivedCount() }
    fun clearPreservedRecords() = synchronized(gate) { recovery.clearArchives() }
    fun archiveRecovery(id: String) = synchronized(gate) {
        check(work.isEmpty() && canceling.isEmpty() && sourceWork.isEmpty() && sourceReviews.isEmpty()) { "Pause batches and close source reviews before reviewing saved progress." }
        recovery.archive(id) { key -> if (Rules.tokenPattern.matches(key)) access.release(prefix + key) }
        if (!recovery.needed()) access.reconcile(prefix, records.values.associate { prefix + it.id to Uri.parse(it.tree) })
    }
    init {
        check(directory.mkdirs() || directory.isDirectory)
        lockFile = RandomAccessFile(File(directory, "owner.lock"), "rw")
        try { lock = requireNotNull(lockFile.channel.tryLock()) { "The batch queue is already open." } } catch (e: Exception) { lockFile.close(); throw e }
        try {
            val journals = StateRecovery.journals(directory)
            if (journals.size > 1024) recovery.block("More than 1,024 batch records exist. Review private queue storage and restart the app.")
            journals.take(1024).forEach { path -> try {
                require(records.size < 8)
                var batch = decode(JSONObject(PrivateJsonFile(path, 8 * 1024 * 1024).read()))
                require(path.name == "${batch.id}.json" && batch.id !in records)
                if (batch.phase == PhoneBatchPhase.RUNNING) batch = batch.copy(phase = PhoneBatchPhase.INTERRUPTED, error = "The app stopped. Resume to check saved progress.")
                records[batch.id] = batch
            } catch (_: Exception) { recovery.add(path, "This batch's saved progress could not be validated. Its copied files, folders and staging were kept.") } }
            if (!recovery.needed()) access.reconcile(prefix, records.values.associate { prefix + it.id to Uri.parse(it.tree) })
            else records.values.forEach { access.retainRecorded(prefix + it.id, Uri.parse(it.tree)) }
            emit()
        } catch (e: Exception) { lock.release(); lockFile.close(); lifetime.cancel(); throw e }
    }
    fun enqueue(peer: String, epoch: String, manifest: PhoneTransferManifest, tree: Uri, collision: PhoneCollision, id: String = StorageRules.token()): String {
        PhoneTransferManifests.validate(manifest); SafDestination(context, tree).validate()
        require(validOwner(peer, epoch) && Rules.tokenPattern.matches(id))
        val batch = PhoneBatch(id, peer, epoch, tree.toString(), collision, manifest.items.map {
            PhoneBatchItem(it, if (it.error.isEmpty()) PhoneBatchItemPhase.PENDING else PhoneBatchItemPhase.FAILED, error = it.error) })
        return synchronized(gate) {
            recovery.requireHealthy(); files.requireHealthy()
            check(!closed && records.size < 8 && batch.id !in records) { "Remove an old batch record before adding another." }
            access.retainRecorded(prefix + batch.id, tree)
            try { put(batch) } catch (e: Exception) { runCatching { access.release(prefix + batch.id) }; throw e }
            batch.id
        }
    }
    fun resume(id: String, peer: String, epoch: String, request: suspend (FileRequest) -> FileResponse, anotherCopy: String = ""): Job = synchronized(gate) {
        recovery.requireHealthy(); files.requireHealthy()
        check(!closed && id !in work && id !in canceling && id !in sourceWork && sourceReviews.values.none {it.review.batch==id}) {"Close this batch's source review before resuming it."}
        val batch = get(id); require(batch.peer == peer && batch.epoch == epoch) { "This batch belongs to an earlier pairing. Cancel it and review a new copy." }
        check(batch.phase in setOf(PhoneBatchPhase.PAUSED, PhoneBatchPhase.INTERRUPTED, PhoneBatchPhase.REVIEW))
        if (anotherCopy.isNotEmpty()) check(batch.items.any { it.child == anotherCopy } && files.state.value.any { it.id == anotherCopy && it.phase == PhoneDownloadPhase.PUBLICATION_INTERRUPTED })
        val job = lifetime.launch(start = CoroutineStart.LAZY) { slots.withPermit { run(id, request, anotherCopy) } }
        work[id] = job
        job.invokeOnCompletion { synchronized(gate) { if (work[id] === job) { work.remove(id); emit() } } }
        emit(); job.start(); job
    }
    private suspend fun run(id: String, request: suspend (FileRequest) -> FileResponse, anotherCopy: String) {
        var index = -1; var child = ""
        try {
            put(get(id).copy(phase = PhoneBatchPhase.RUNNING, error = "")); val resolver = PhoneManifestResolver(request)
            for (i in get(id).items.indices) {
                currentCoroutineContext().ensureActive(); index = i
                var batch = get(id); var item = batch.items[i]
                if (item.phase in terminalItems) continue
                check(encode(batch).toString().toByteArray(Charsets.UTF_8).size <= 8 * 1024 * 1024 - 64 * 1024) {
                    "This batch reached its local record limit. Cancel unfinished work and use a smaller selection." }
                if (item.source.parent >= 0 && batch.items[item.source.parent].phase != PhoneBatchItemPhase.COMPLETED) {
                    putItem(batch, i, item.copy(phase = PhoneBatchItemPhase.SKIPPED, error = "The parent folder was not copied.")); continue
                }
                val parents = parents(batch, item.source.parent)
                val destination = SafDestination(context, Uri.parse(batch.tree), parents); destination.validate()
                if (item.source.directory) {
                    check(item.phase != PhoneBatchItemPhase.CREATING) { "Folder creation stopped before its ID was saved. Review the destination and skip this uncertain folder or start a new copy." }
                    resolver.resolve(item.source)
                    val existing = destination.children(); val collision = existing.values.any { it.equals(item.source.name, true) }
                    if (collision && batch.collision != PhoneCollision.KEEP_BOTH) {
                        putItem(batch, i, item.copy(phase = if (batch.collision == PhoneCollision.SKIP) PhoneBatchItemPhase.SKIPPED else PhoneBatchItemPhase.FAILED,
                            error = "An existing destination folder was kept. Folders are never merged.")); continue
                    }
                    val name = SafDestination.unusedName(item.source.name, existing.values, true)
                    batch = putItem(batch, i, item.copy(phase = PhoneBatchItemPhase.CREATING, localName = name, error = ""))
                    val uri = requireNotNull(Docs.createDocument(context.contentResolver, destination.parent, Docs.Document.MIME_TYPE_DIR, name))
                    currentCoroutineContext().ensureActive()
                    val binding = destination.createdFolder(uri, existing)
                    check(batch.collision == PhoneCollision.KEEP_BOTH || binding.name == item.source.name) { "The provider changed the folder name. Review the new folder before continuing." }
                    putItem(batch, i, batch.items[i].copy(phase = PhoneBatchItemPhase.COMPLETED, document = binding.id, localName = binding.name))
                    continue
                }
                child = item.child.ifEmpty { StorageRules.token() }
                batch = putItem(batch, i, item.copy(phase = PhoneBatchItemPhase.RUNNING, child = child, error = "")); item = batch.items[i]
                var file = files.state.value.firstOrNull { it.id == child }
                if (file == null) {
                    val entry = resolver.resolve(item.source); currentCoroutineContext().ensureActive()
                    files.enqueue(batch.peer, entry, Uri.parse(batch.tree), parents, child, id, batch.collision)
                    file = files.state.value.single { it.id == child }
                }
                check(file.batchId == id && file.peer == batch.peer && file.name == item.source.name && matchesSource(item,file) &&
                    file.size == item.source.size && file.tree == batch.tree && file.parents == parents && file.collision == batch.collision) { "The saved child transfer does not belong to this batch." }
                if (file.phase in setOf(PhoneDownloadPhase.PAUSED, PhoneDownloadPhase.INTERRUPTED, PhoneDownloadPhase.PREPARING)) {
                    if (file.ticket.isEmpty() && file.sha256.isEmpty()) files.rebind(child, resolver.resolve(item.source))
                    startFile(currentCoroutineContext()) { files.resumeBatchFile(child, batch.peer, id, request) }.join(); file = files.state.value.single { it.id == child }
                }
                val bound=bindFirstSourcePart(get(id).items[i],file)
                if(bound!=get(id).items[i]) putItem(get(id),i,bound)
                if (file.phase in setOf(PhoneDownloadPhase.READY_TO_SAVE, PhoneDownloadPhase.PUBLICATION_INTERRUPTED)) {
                    startFile(currentCoroutineContext()) { files.publishBatchFile(child, anotherCopy == child, id) }.join(); file = files.state.value.single { it.id == child }
                }
                batch = putItem(get(id), i, outcome(get(id).items[i], file))
                when (file.phase) {
                    PhoneDownloadPhase.COMPLETED, PhoneDownloadPhase.SKIPPED, PhoneDownloadPhase.CANCELED -> { files.remove(child); child = "" }
                    PhoneDownloadPhase.READY_TO_SAVE -> { putItem(batch, i, batch.items[i].copy(phase = PhoneBatchItemPhase.FAILED)); child = "" }
                    PhoneDownloadPhase.PUBLICATION_INTERRUPTED -> error("A provider copy needs review. Check it or explicitly choose another copy.")
                    else -> error(file.error.ifEmpty { "This file stopped. Reconnect and resume." })
                }
            }
            val batch = get(id); val complete = batch.items.all { it.phase == PhoneBatchItemPhase.COMPLETED }
            put(batch.copy(phase = if (complete) PhoneBatchPhase.COMPLETED else PhoneBatchPhase.COMPLETED_WITH_ISSUES,
                error = if (complete) "" else "Some items were skipped or failed. Review each item result."))
        } catch (e: Exception) {
            withContext(NonCancellable) {
                if (child.isNotEmpty()) { files.pause(child); files.awaitIdle(child) }
                var batch = get(id); val items = batch.items.toMutableList()
                if(index>=0 && child.isNotEmpty()) files.state.value.firstOrNull {it.id==child}?.let {file->items[index]=runCatching {bindFirstSourcePart(items[index],file)}.getOrDefault(items[index])}
                val error = if (e is CancellationException) "Paused. Saved files and folders were kept." else PhoneTransferManifests.safe(e.message ?: "The batch stopped.")
                if (index >= 0 && items[index].phase !in terminalItems) items[index] = items[index].copy(
                    phase = if (items[index].phase == PhoneBatchItemPhase.CREATING) PhoneBatchItemPhase.CREATING else PhoneBatchItemPhase.INTERRUPTED, error = error)
                val review = items.any { it.phase == PhoneBatchItemPhase.CREATING } || files.state.value.any { it.batchId == id && it.phase == PhoneDownloadPhase.PUBLICATION_INTERRUPTED }
                batch = batch.copy(items = items, phase = if (e is CancellationException) PhoneBatchPhase.PAUSED else if (review) PhoneBatchPhase.REVIEW else PhoneBatchPhase.INTERRUPTED, error = error)
                runCatching { put(batch) }.onFailure { synchronized(gate) { records[id] = batch.copy(phase = PhoneBatchPhase.INTERRUPTED, error = "Progress could not be saved. Free storage space and retry."); emit() } }
            }
        } finally { withContext(NonCancellable) { if (child.isNotEmpty()) { files.pause(child); files.awaitIdle(child) } } }
    }
    fun pause(id: String) { synchronized(gate) { work[id]?.cancel(); sourceWork[id]?.cancel(); sourceReviews.values.filter {it.review.batch==id}.map {it.review.id}.forEach {cancelSourceReview(it)} } }
    fun pauseAll() { synchronized(gate) { work.values.forEach { it.cancel() } }; invalidateSourceReviews() }
    suspend fun awaitIdle(id: String) { synchronized(gate) { work[id] }?.join() }
    suspend fun cancel(id: String, request: (suspend (FileRequest) -> FileResponse)? = null) {
        val own = CompletableDeferred<Unit>()
        val previous = synchronized(gate) { check(!closed); canceling.putIfAbsent(id, own) }
        if (previous != null) { previous.await(); return }
        try { withContext(NonCancellable + Dispatchers.IO) {
            cancelSourceRecovery(id)
            pause(id); awaitIdle(id)
            for (file in files.state.value.filter { it.batchId == id }) {
                if (file.phase !in setOf(PhoneDownloadPhase.COMPLETED, PhoneDownloadPhase.SKIPPED)) files.cancel(file.id, request)
                val latest = files.state.value.single { it.id == file.id }; val batch = get(id); val index = batch.items.indexOfFirst { it.child == file.id }
                if (index >= 0) putItem(batch, index, outcome(batch.items[index], latest))
                files.remove(file.id)
            }
            val batch = get(id)
            put(batch.copy(phase = PhoneBatchPhase.CANCELED, items = batch.items.map {
                if (it.phase in setOf(PhoneBatchItemPhase.COMPLETED, PhoneBatchItemPhase.SKIPPED)) it else it.copy(phase = PhoneBatchItemPhase.CANCELED)
            }, error = "Saved files, created folders and uncertain provider copies were kept."))
        }; own.complete(Unit) } catch (e: Exception) { own.completeExceptionally(e); throw e }
        finally { synchronized(gate) { canceling.remove(id); emit() } }
    }
    fun skipUncertainFolder(id: String, index: Int) = synchronized(gate) {
        check(!closed && id !in work && id !in canceling)
        val batch = get(id); val item = batch.items[index]
        check(item.source.directory && item.phase == PhoneBatchItemPhase.CREATING && item.document.isEmpty())
        putItem(batch, index, item.copy(phase = PhoneBatchItemPhase.SKIPPED, error = "Uncertain folder and its contents were kept. This branch was skipped."))
    }
    fun canRemove(id: String): Boolean {
        val child = files.state.value.any { it.batchId == id }
        return synchronized(gate) { !child && id !in work && id !in canceling && records[id]?.phase in setOf(PhoneBatchPhase.COMPLETED, PhoneBatchPhase.COMPLETED_WITH_ISSUES, PhoneBatchPhase.CANCELED) }
    }
    fun remove(id: String) {
        check(canRemove(id)) { "Cancel pending private-file cleanup before removing this record." }
        synchronized(gate) {
            check(!closed && id !in work && id !in canceling)
            AtomicFile(File(directory, "$id.json")).delete(); check(!File(directory, "$id.json").exists() && !File(directory, "$id.json.bak").exists())
            records.remove(id); emit(); access.release(prefix + id)
        }
    }
    private fun parents(batch: PhoneBatch, parent: Int): List<SafFolderBinding> {
        val path = mutableListOf<SafFolderBinding>(); var index = parent
        while (index >= 0) { val item = batch.items[index]; check(item.phase == PhoneBatchItemPhase.COMPLETED && item.source.directory); path.add(SafFolderBinding(item.document, item.localName)); index = item.source.parent }
        return path.reversed()
    }
    private fun outcome(item: PhoneBatchItem, file: PhoneDownload) = item.copy(localName = file.publishedName, hash = file.sha256, published = file.published, error = file.error,
        phase = when (file.phase) { PhoneDownloadPhase.COMPLETED -> PhoneBatchItemPhase.COMPLETED; PhoneDownloadPhase.SKIPPED -> PhoneBatchItemPhase.SKIPPED;
            PhoneDownloadPhase.CANCELED -> PhoneBatchItemPhase.CANCELED; else -> PhoneBatchItemPhase.INTERRUPTED })
    private fun get(id: String) = synchronized(gate) { requireNotNull(records[id]) }
    private fun putItem(batch: PhoneBatch, index: Int, item: PhoneBatchItem): PhoneBatch = put(batch.copy(items = batch.items.toMutableList().also { it[index] = item }))
    private fun put(batch: PhoneBatch): PhoneBatch = synchronized(gate) {
        val previous=records[batch.id]?.phase
        PrivateJsonFile(File(directory, "${batch.id}.json"), 8 * 1024 * 1024).write(encode(batch).toString()); records[batch.id] = batch; emit()
        if(previous!=batch.phase) {
            val outcome=when(batch.phase) {PhoneBatchPhase.COMPLETED->PhoneActivityOutcome.COMPLETED; PhoneBatchPhase.COMPLETED_WITH_ISSUES->PhoneActivityOutcome.COMPLETED_WITH_ISSUES; PhoneBatchPhase.REVIEW->PhoneActivityOutcome.REVIEW;
                PhoneBatchPhase.CANCELED->PhoneActivityOutcome.CANCELED; PhoneBatchPhase.PAUSED,PhoneBatchPhase.INTERRUPTED->PhoneActivityOutcome.PAUSED; else->null}
            if(outcome!=null) recordPhoneActivity(activity,PhoneActivityEvent(if (localCopy) PhoneActivityKind.LOCAL_COPY else PhoneActivityKind.BATCH_RECEIVE,outcome,batch.peer,
                if(batch.phase in setOf(PhoneBatchPhase.COMPLETED,PhoneBatchPhase.COMPLETED_WITH_ISSUES)) batch.verifiedBytes else -1))
        }; batch
    }
    private fun emit() { mutable.value = records.values.map { it.copy(active = it.id in work || it.id in canceling) } }
    private fun encode(batch: PhoneBatch): JSONObject = JSONObject().put("schema", 1).put("id", batch.id).put("peer", batch.peer).put("epoch", batch.epoch).put("tree", batch.tree)
        .put("collision", batch.collision.name).put("phase", batch.phase.name).put("error", batch.error).put("manifest", PhoneTransferManifests.encode(PhoneTransferManifest(batch.items.map { it.source })))
        .put("outcomes", JSONArray().apply { batch.items.forEach { put(JSONObject().put("phase", it.phase.name).put("document", it.document).put("name", it.localName)
            .put("child", it.child).put("hash", it.hash).put("published", it.published).put("error", it.error).put("sourceApproval",it.sourceApproval?.encode() ?: JSONObject.NULL)) } })
    private fun decode(row: JSONObject): PhoneBatch {
        require(row.getInt("schema") == 1); val manifest = PhoneTransferManifests.decode(row.getJSONArray("manifest")); val rows = row.getJSONArray("outcomes")
        require(rows.length() == manifest.items.size)
        val items = manifest.items.mapIndexed { i, source -> val value = rows.getJSONObject(i)
            PhoneBatchItem(source, PhoneBatchItemPhase.valueOf(value.getString("phase")), value.getString("document"), value.getString("name"), value.getString("child"),
                value.getString("hash"), value.getString("published"), value.getString("error"),
                if(value.has("sourceApproval") && !value.isNull("sourceApproval")) PhoneBatchSourceApproval.decode(value.getJSONObject("sourceApproval")) else null).also {
                require(it.document.length <= 4096 && (it.child.isEmpty() || Rules.tokenPattern.matches(it.child)) && (it.hash.isEmpty() || it.hash.matches(Regex("[a-f0-9]{64}"))) &&
                    it.published.length <= 16_384 && it.error.length <= 512)
                if (it.localName.isNotEmpty()) StorageRules.name(it.localName)
                if (it.phase == PhoneBatchItemPhase.COMPLETED) require(if (source.directory) it.document.isNotEmpty() && it.localName.isNotEmpty() else it.hash.isNotEmpty() && it.published.isNotEmpty())
                if (source.error.isNotEmpty()) require(it.phase in setOf(PhoneBatchItemPhase.FAILED, PhoneBatchItemPhase.CANCELED))
                it.sourceApproval?.validate(it)
            }
        }
        return PhoneBatch(row.getString("id"), row.getString("peer"), row.getString("epoch"), row.getString("tree"), PhoneCollision.valueOf(row.getString("collision")),
            items, PhoneBatchPhase.valueOf(row.getString("phase")), row.getString("error")).also {
            require(Rules.tokenPattern.matches(it.id) && validOwner(it.peer, it.epoch) && it.error.length <= 512)
            AndroidSafAccess.validateTree(Uri.parse(it.tree))
            it.items.forEach {item->item.sourceApproval?.let {receipt->require(receipt.destination==PhoneBatchSourceApproval.binding(it.tree,parents(it,item.source.parent),it.collision))}}
        }
    }
    internal class SourceAuthority(private val owner:AndroidBatchDownloads,private val batch:String,private val index:Int,
        internal var stamp:String,private val part:String,private val allowed:()->Boolean) {
        internal var invalidated=false
        internal var confirming=false
        internal fun current():Boolean=synchronized(owner.gate) {
            !invalidated && !owner.closed && batch !in owner.work && batch !in owner.canceling &&
                owner.records[batch]?.let {owner.sourceStamp(it)==stamp}==true && allowed()
        }
        internal fun require(file:PhoneDownload)=synchronized(owner.gate) {
            val parent=owner.requireSourceParent(batch)
            check(current() && index in parent.items.indices) {"The original batch or paired connection changed. Review again."}
            val item=parent.items[index]
            check(!item.source.directory && item.source.error.isEmpty() && item.child==file.id && parent.items.count {it.child==file.id}==1 &&
                file.batchId==batch && file.peer==parent.peer && file.name==item.source.name && file.size==item.source.size &&
                file.tree==parent.tree && file.parents==owner.parents(parent,item.source.parent) && file.collision==parent.collision && owner.matchesSource(item,file)) {
                "The retained receiving file does not belong to this original batch item."
            }
            SafDestination(owner.context,parent.tree.toUri(),file.parents).validate()
            check(file.offset==0L || !PhoneBatchSourceApproval.unallocated(part)) {"The saved private checkpoint is missing. Its source approval was kept."}
            check(owner.files.sourcePartIdentity(file.id)==part) {"The original private receiving part changed. Its saved bytes were kept."}
        }
    }
    private fun sourceStamp(batch:PhoneBatch)=Rules.hex(Rules.digest(encode(batch).toString().toByteArray(Charsets.UTF_8)))
    private fun requireSourceParent(id:String):PhoneBatch {
        recovery.requireHealthy()
        val batch=records[id]
        check(!closed && !localCopy && batch!=null && id !in work && id !in canceling && batch.phase in setOf(PhoneBatchPhase.PAUSED,PhoneBatchPhase.INTERRUPTED)) {
            "Use an idle unfinished remote batch with its original saved plan. Uncertain provider copies retain their review workflow."
        }
        return batch
    }
    private fun matchesSource(item:PhoneBatchItem,file:PhoneDownload):Boolean {
        val receipt=item.sourceApproval ?: return file.version==item.source.version
        val previousHash=if(receipt.previousHash.isEmpty()) file.version==receipt.originalVersion && (file.sha256.isNotEmpty() || file.offset==0L) else receipt.previousHash==file.sha256
        val descriptor=receipt.previousVersion==file.version && previousHash ||
            receipt.approvedVersion==file.version && receipt.hash==file.sha256
        val terminal=file.phase in setOf(PhoneDownloadPhase.COMPLETED,PhoneDownloadPhase.SKIPPED,PhoneDownloadPhase.CANCELED)
        return descriptor && receipt.child==file.id && receipt.originalVersion==item.source.version && receipt.destination==PhoneBatchSourceApproval.binding(file) &&
            (terminal || PhoneBatchSourceApproval.matchesPart(receipt.partIdentity,files.sourcePartIdentity(file.id)))
    }
    private fun bindFirstSourcePart(item:PhoneBatchItem,file:PhoneDownload):PhoneBatchItem {
        val receipt=item.sourceApproval ?: return item
        if(!PhoneBatchSourceApproval.unallocated(receipt.partIdentity) || !matchesSource(item,file)) return item
        val identity=files.sourcePartIdentity(file.id)
        return if(PhoneBatchSourceApproval.unallocated(identity)) item else item.copy(sourceApproval=receipt.copy(partIdentity=identity))
    }
    private suspend fun <T> sourceTask(batch:String,reserve:()->Unit={},action:suspend()->T):T {
        val caller=currentCoroutineContext()
        val task=synchronized(gate) {
            check(!closed && batch !in sourceWork); reserve()
            lifetime.async(start=CoroutineStart.LAZY) {action()}.also {sourceWork[batch]=it; it.start()}
        }
        try {return task.await()}
        finally {withContext(NonCancellable) {
            if(!task.isCompleted) task.cancelAndJoin()
            synchronized(gate) {
                if(sourceWork[batch]===task) sourceWork.remove(batch)
                if(!caller.isActive) sourceReviews.values.filter {it.review.batch==batch}.map {it.review.id}.forEach {cancelSourceReview(it)}
            }
        }}
    }
    suspend fun prepareSourceReview(batch:String,index:Int,peer:String,epoch:String,selected:FileEntry,request:suspend(FileRequest)->FileResponse,
        allowed:()->Boolean):PhoneBatchDownloadSourceReview {
        val setup=synchronized(gate) {
            val parent=requireSourceParent(batch)
            check(parent.peer==peer && parent.epoch==epoch && index in parent.items.indices) {"This review belongs to another batch or pairing."}
            val item=parent.items[index]
            val child=files.state.value.singleOrNull {it.id==item.child} ?: error("This item has no retained receiving checkpoint to renew.")
            val authority=SourceAuthority(this,batch,index,sourceStamp(parent),files.sourcePartIdentity(child.id),allowed); authority.require(child)
            Triple(child,authority,item.source.path.joinToString(" / "))
        }
        return sourceTask(batch,{
            setup.second.require(setup.first)
            check(sourceWork.size+sourceReviews.size<8 && sourceReviews.values.none {it.review.batch==batch}) {"Close this batch's earlier source review first."}
        }) {
            val coroutine=currentCoroutineContext(); var prepared:PhoneDownloadSourceReview?=null; var retained=false
            try {
                val ready=files.prepareBatchSourceReview(setup.first.id,peer,selected,request,setup.second::current,setup.second); prepared=ready
                synchronized(gate) {
                    coroutine.ensureActive(); setup.second.require(files.state.value.single {it.id==setup.first.id})
                    val value=PhoneBatchDownloadSourceReview(ready.id,batch,index,setup.third,ready)
                    sourceReviews[value.id]=SourceOffer(value,setup.second); retained=true; value
                }
            } finally {if(!retained) prepared?.let {files.cancelSourceReviewAndWait(it.id)}}
        }
    }
    suspend fun confirmSourceReview(review:PhoneBatchDownloadSourceReview) {
        val offer=synchronized(gate) {requireNotNull(sourceReviews[review.id]) {"Review this batch source again."}.also {check(it.review==review)}}
        sourceTask(review.batch, {check(sourceReviews[review.id]===offer) {"This batch source review ended."}}) {
            synchronized(gate) {check(sourceReviews.remove(review.id)===offer)}
            try {
                synchronized(gate) {offer.authority.require(files.state.value.single {it.id==review.source.job.id}); offer.authority.confirming=true}
                files.confirmBatchSourceReview(review.source,offer.authority) {file->
                    // Called under the shared owner lock after remote/record checks, before child persistence.
                    offer.authority.require(file)
                    val parent=get(review.batch); val items=parent.items.toMutableList(); val item=items[review.item]
                    val receipt=PhoneBatchSourceApproval(file.id,item.source.version,file.version,review.source.newVersion,file.sha256,review.source.hash,
                        PhoneBatchSourceApproval.binding(file),files.sourcePartIdentity(file.id))
                    receipt.validate(item); items[review.item]=item.copy(sourceApproval=receipt)
                    val next=put(parent.copy(items=items)); offer.authority.stamp=sourceStamp(next)
                }
                recordPhoneActivity(activity,PhoneActivityEvent(PhoneActivityKind.BATCH_RECEIVE,PhoneActivityOutcome.ACCEPTED,review.source.job.peer))
            } finally {offer.authority.invalidated=true; files.cancelSourceReviewAndWait(review.source.id)}
        }
    }
    fun cancelSourceReview(id:String)=synchronized(gate) {
        val offer=sourceReviews.remove(id) ?: return@synchronized
        offer.authority.invalidated=true
        lifetime.launch(start=CoroutineStart.UNDISPATCHED) {withContext(NonCancellable) {files.cancelSourceReviewAndWait(offer.review.source.id)}}
        Unit
    }
    fun invalidateSourceReviews()=synchronized(gate) {sourceWork.values.forEach {it.cancel()}; sourceReviews.keys.toList().forEach {cancelSourceReview(it)}}
    private suspend fun cancelSourceRecovery(batch:String) {
        val tasks=synchronized(gate) {sourceWork[batch]?.cancel(); sourceReviews.values.filter {it.review.batch==batch}.map {it.review.id}.forEach {cancelSourceReview(it)}; listOfNotNull(sourceWork[batch])}
        tasks.joinAll()
    }
    private fun validOwner(peer: String, epoch: String) = if (localCopy) peer.isEmpty() && epoch.isEmpty() else peer.matches(Regex("[a-f0-9]{64}")) && Rules.tokenPattern.matches(epoch)
    private fun startFile(worker: kotlin.coroutines.CoroutineContext, action: () -> Job): Job = dispatch?.invoke(worker, action) ?: run { worker.ensureActive(); action() }
    suspend fun shutdown() {
        val pending=synchronized(gate) {if(closed) return; closed=true; sourceReviews.values.toList().also {sourceReviews.clear(); it.forEach {offer->offer.authority.invalidated=true}}}
        val tasks=synchronized(gate) {work.values.forEach {it.cancel()}; sourceWork.values.forEach {it.cancel()}; work.values.toList() to canceling.values.toList()}
        withContext(NonCancellable) {
            tasks.first.joinAll(); tasks.second.forEach {runCatching {it.await()}}
            lifetime.cancel(); lifetime.coroutineContext.job.join()
            try {pending.forEach {files.cancelSourceReviewAndWait(it.review.source.id)}} finally {lock.release(); lockFile.close()}
        }
    }
    override fun close() = runBlocking { shutdown() }
    companion object { private val terminalItems = setOf(PhoneBatchItemPhase.COMPLETED, PhoneBatchItemPhase.SKIPPED, PhoneBatchItemPhase.FAILED, PhoneBatchItemPhase.CANCELED) }
}
