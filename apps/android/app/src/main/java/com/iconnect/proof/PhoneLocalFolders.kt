package com.iconnect.proof

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.CancellationSignal
import android.provider.DocumentsContract as Docs
import androidx.core.net.toUri
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.RandomAccessFile
import java.util.concurrent.ConcurrentHashMap

internal data class LocalFolderTarget(val location:String,val tree:Uri,val rootName:String,val parents:List<SafFolderBinding>,val name:String,val captured:Long)
enum class PhoneLocalFolderPhase { PENDING, CREATED, REVIEW }
data class PhoneLocalFolderReceipt(val id:String,val folder:String,val name:String,val resultingName:String,val phase:PhoneLocalFolderPhase,val created:Long)
data class PhoneLocalFolderReview(val id:String,val folder:String,val name:String)
data class PhoneLocalFolderState(val receipts:List<PhoneLocalFolderReceipt> = emptyList(),val review:PhoneLocalFolderReview?=null,
    val busy:Boolean=false,val issues:List<StateRecoveryIssue> = emptyList(),val preserved:Int=0,val error:String="")

/** Local one-operation approvals. Receipts and browsing shortcuts cannot authorize a provider write. */
class PhoneLocalFolders internal constructor(private val context:Context,
    private val current:(LocalFolderTarget)->Boolean,
    private val directory:File=File(context.filesDir,"local-folder-operations"),
    private val now:()->Long=System::currentTimeMillis,
    private val elapsed:()->Long=android.os.SystemClock::elapsedRealtime) {
    private data class Record(val receipt:PhoneLocalFolderReceipt,val tree:Uri,val rootName:String,val parents:List<SafFolderBinding>)
    private data class Offer(val review:PhoneLocalFolderReview,val target:LocalFolderTarget,val epoch:Long,val time:Long,val wall:Long,val lease:String)
    private data class Approval(val target:LocalFolderTarget,val tree:Uri,val flags:Int)
    private val authority=Any()
    private val scope=CoroutineScope(SupervisorJob()+Dispatchers.IO)
    private val access=AndroidSafAccess(context)
    private val prefix="local-folder:${Rules.hex(Rules.digest(directory.canonicalPath.toByteArray())).take(32)}:"
    private val journal=PrivateJsonFile(File(directory,"journal.json"),8*1024*1024)
    private val recovery=StateRecovery(directory)
    private val offers=ConcurrentHashMap<String,Offer>()
    private val signals=ConcurrentHashMap<String,CancellationSignal>()
    private val leases=ConcurrentHashMap.newKeySet<String>()
    private var records=emptyList<Record>()
    private var pending:Approval?=null
    @Volatile private var visible=false
    @Volatile private var closed=false
    @Volatile private var epoch=0L
    private val mutable=MutableStateFlow(PhoneLocalFolderState())
    val state=mutable.asStateFlow()
    internal var activity:(PhoneActivityEvent)->Unit={}
    private val ownerFile:RandomAccessFile
    private val ownerLock:java.nio.channels.FileLock
    init {
        check(directory.mkdirs() || directory.isDirectory)
        ownerFile=RandomAccessFile(File(directory,"owner.lock"),"rw")
        try {ownerLock=checkNotNull(ownerFile.channel.tryLock()) {"Local folder history is already open."}}
        catch(e:Exception) {ownerFile.close(); scope.cancel(); throw e}
        try {
            try {load()} catch(_:Exception) {records=emptyList(); recovery.add(File(directory,"journal.json"),"Local folder history needs review. Existing provider folders were kept; creation is blocked.")}
            // No receipt is resumable. Restart withdraws only this purpose's unused OS access.
            access.reconcile(prefix,emptyMap(),ACCESS)
            emit()
        } catch(e:Exception) {ownerLock.release(); ownerFile.close(); scope.cancel(); throw e}
    }
    private fun validate(record:Record) {
        val row=record.receipt
        require(Rules.tokenPattern.matches(row.id) && row.created in 0..StorageRules.MAX_TIMESTAMP)
        AndroidSafAccess.validateTree(record.tree); StorageRules.name(record.rootName); StorageRules.name(row.name)
        if(row.resultingName.isNotEmpty()) StorageRules.name(row.resultingName)
        SafDestination.validateBindings(record.parents)
        require(row.folder==label(record.rootName,record.parents) && row.folder.length<=16384 &&
            record.parents.none {it.id==Docs.getTreeDocumentId(record.tree)} && (row.phase!=PhoneLocalFolderPhase.CREATED || row.resultingName.isNotEmpty()))
    }
    private fun load() {
        check(!File(directory,"journal.json.new").exists() && !File(directory,"journal.json.bak").exists()) {"Ambiguous local history."}
        if(!journal.exists()) return
        val root=JSONObject(journal.read()); require(root.getInt("schema")==1); val rows=root.getJSONArray("records"); require(rows.length()<=128)
        records=(0 until rows.length()).map {index->val row=rows.getJSONObject(index); val parents=row.getJSONArray("parents")
            require(parents.length()<=32)
            Record(PhoneLocalFolderReceipt(row.getString("id"),row.getString("folder"),row.getString("name"),row.getString("result"),
                PhoneLocalFolderPhase.valueOf(row.getString("phase")),row.getLong("created")),row.getString("tree").toUri(),row.getString("root"),
                (0 until parents.length()).map {parents.getJSONObject(it).let {p->SafFolderBinding(p.getString("id"),p.getString("name"))}}).also(::validate)
        }
        require(records.map {it.receipt.id}.distinct().size==records.size)
        if(records.any {it.receipt.phase==PhoneLocalFolderPhase.PENDING}) save(records.map {
            if(it.receipt.phase==PhoneLocalFolderPhase.PENDING) it.copy(receipt=it.receipt.copy(phase=PhoneLocalFolderPhase.REVIEW)) else it
        })
    }
    private fun save(updated:List<Record>) {
        require(updated.size<=128); updated.forEach(::validate)
        val rows=JSONArray(); updated.forEach {record->val row=record.receipt; rows.put(JSONObject().put("id",row.id).put("folder",row.folder).put("name",row.name)
            .put("result",row.resultingName).put("phase",row.phase.name).put("created",row.created).put("tree",record.tree.toString()).put("root",record.rootName)
            .put("parents",JSONArray().also {array->record.parents.forEach {array.put(JSONObject().put("id",it.id).put("name",it.name))}}))}
        try {journal.write(JSONObject().put("schema",1).put("records",rows).toString()); records=updated}
        catch(e:Exception) {recovery.block("Local folder progress could not be saved. Provider folders were kept; review private history before another creation."); throw e}
        emit()
    }
    private fun put(record:Record) {
        val previous=records.firstOrNull {it.receipt.id==record.receipt.id}?.receipt?.phase
        save(records.filterNot {it.receipt.id==record.receipt.id}+record)
        if(previous!=record.receipt.phase) recordPhoneActivity(activity,PhoneActivityEvent(PhoneActivityKind.LOCAL_FOLDER_CREATE,when(record.receipt.phase) {
            PhoneLocalFolderPhase.PENDING->PhoneActivityOutcome.REQUESTED; PhoneLocalFolderPhase.CREATED->PhoneActivityOutcome.COMPLETED; PhoneLocalFolderPhase.REVIEW->PhoneActivityOutcome.REVIEW
        }))
    }
    private fun emit() {mutable.update {it.copy(receipts=records.asReversed().map {row->row.receipt},issues=recovery.all(),preserved=recovery.archivedCount())}}
    private fun available(expected:Long) {check(!closed && visible && epoch==expected) {"Return to iConnect and approve this folder action again."}}
    private fun validTarget(target:LocalFolderTarget,expected:Long,signal:CancellationSignal) {
        available(expected); signal.throwIfCanceled(); check(current(target)) {"This local shortcut was removed. Choose the folder again."}
        check(elapsed()-target.captured in 0 until REVIEW_TIME) {"The folder selection expired. Start a fresh review."}
        SafDestination(context,target.tree,target.parents,signal).validate()
        val root=Docs.buildDocumentUriUsingTree(target.tree,Docs.getTreeDocumentId(target.tree))
        val rootName=context.contentResolver.query(root,arrayOf(Docs.Document.COLUMN_DISPLAY_NAME),null,null,null,signal)?.use {cursor->check(cursor.moveToFirst()); cursor.getString(0)}
        check(rootName==target.rootName) {"The selected root folder changed. Choose it again."}
        available(expected); signal.throwIfCanceled(); check(current(target))
    }
    private fun action(block:suspend (Long)->Unit):Job=synchronized(authority) {
        check(!closed && visible) {"Open iConnect to manage local folders."}
        if(state.value.busy) return@synchronized Job().apply {complete()}
        val expected=epoch; mutable.update {it.copy(busy=true,error="")}
        scope.launch {try {available(expected); block(expected)} catch(e:CancellationException) {throw e}
            catch(_:Exception) {mutable.update {it.copy(error="The folder action could not be confirmed. Inspect the chosen folder and its receipt before trying another action.")}}
            finally {emit(); mutable.update {it.copy(busy=false)}; drainPending()}}
    }
    fun setVisible(value:Boolean) {
        val withdrawn=synchronized(authority) {if(closed && value) return; visible=value; if(!value) {epoch++; val old=offers.values.toList(); offers.clear(); mutable.update {it.copy(review=null)}; old} else emptyList()}
        if(!value) signals.values.forEach {it.cancel()}
        withdrawn.forEach {offer->scope.launch {release(offer.lease)}}
        if(value) drainPending()
    }
    private fun drainPending() {
        val next=synchronized(authority) {if(!closed && visible && !state.value.busy && offers.isEmpty()) pending.also {pending=null} else null}?:return
        approve(next.target,next.tree,next.flags)
    }
    internal fun approve(target:LocalFolderTarget,tree:Uri,flags:Int) {
        synchronized(authority) {check(!closed); if(!visible || state.value.busy) {pending=Approval(target,tree,flags); return}}
        action {expected->
            recovery.requireHealthy(); check(records.size<128) {"Review or remove old local folder receipts first."}
            require(tree==target.tree) {"Choose the same local root folder."}; StorageRules.name(target.name)
            val id=StorageRules.token(); val lease=prefix+id; val signal=CancellationSignal(); signals[id]=signal; var retained=false
            try {
                leases.add(lease); access.acquire(lease,tree,flags,ACCESS); validTarget(target,expected,signal)
                val review=PhoneLocalFolderReview(id,label(target.rootName,target.parents),target.name)
                synchronized(authority) {available(expected); check(offers.isEmpty()); offers[id]=Offer(review,target,expected,elapsed(),now(),lease); retained=true; mutable.update {it.copy(review=review)}}
            } finally {signals.remove(id); if(!retained) release(lease)}
        }
    }
    fun cancelReview():Job=action {_->val old=synchronized(authority) {offers.values.toList().also {offers.clear(); mutable.update {it.copy(review=null)}}}; old.forEach {release(it.lease)}}
    fun create(id:String):Job=action {expected->
        if(records.any {it.receipt.id==id}) return@action // A receipt is inspectable, never replayable.
        val offer=synchronized(authority) {available(expected); offers.remove(id).also {mutable.update {it.copy(review=null)}}}?:error("Approve a fresh folder action.")
        val signal=CancellationSignal(); signals[id]=signal; var intent:Record?=null
        try {
            recovery.requireHealthy(); check(records.size<128)
            check(offer.epoch==expected && elapsed()-offer.time in 0 until REVIEW_TIME && now()-offer.wall in 0 until REVIEW_TIME)
            val target=offer.target; validTarget(target,expected,signal)
            val destination=SafDestination(context,target.tree,target.parents,signal); val before=destination.children(4096,1024*1024)
            check(before.values.none {it.equals(target.name,true)}) {"An existing item was kept. Choose a different name."}
            intent=Record(PhoneLocalFolderReceipt(id,offer.review.folder,target.name,"",PhoneLocalFolderPhase.PENDING,now()),target.tree,target.rootName,target.parents)
            put(requireNotNull(intent)); validTarget(target,expected,signal)
            val created=Docs.createDocument(context.contentResolver,destination.parent,Docs.Document.MIME_TYPE_DIR,target.name)?:error("Provider creation is uncertain.")
            val result=destination.createdFolder(created,before); validTarget(target,expected,signal)
            put(requireNotNull(intent).copy(receipt=requireNotNull(intent).receipt.copy(resultingName=result.name,phase=PhoneLocalFolderPhase.CREATED)))
        } catch(e:Exception) {
            withContext(NonCancellable) {intent?.let {record->runCatching {put(record.copy(receipt=record.receipt.copy(phase=PhoneLocalFolderPhase.REVIEW)))}}}
            throw e
        } finally {signals.remove(id); withContext(NonCancellable) {release(offer.lease)}}
    }
    fun removeReceipt(id:String):Job=action {expected->available(expected); recovery.requireHealthy(); check(offers.isEmpty()); val had=records.any {it.receipt.id==id}; save(records.filterNot {it.receipt.id==id}); if(had) recordPhoneActivity(activity,PhoneActivityEvent(PhoneActivityKind.LOCAL_FOLDER_HISTORY,PhoneActivityOutcome.COMPLETED))}
    fun preserve(id:String):Job=action {expected->available(expected); check(offers.isEmpty()); recovery.archive(id); if(!recovery.needed()) save(emptyList()); emit(); recordPhoneActivity(activity,PhoneActivityEvent(PhoneActivityKind.PROGRESS_PRESERVATION,PhoneActivityOutcome.COMPLETED))}
    fun clearPreserved():Job=action {expected->available(expected); check(offers.isEmpty()); recovery.clearArchives(); emit(); recordPhoneActivity(activity,PhoneActivityEvent(PhoneActivityKind.PROGRESS_BACKUP_REMOVAL,PhoneActivityOutcome.COMPLETED))}
    private fun release(lease:String) {runCatching {access.release(lease); leases.remove(lease)}.onFailure {mutable.update {it.copy(error="Unused local creation access could not be released. Existing files and other folder access were kept; review private access storage.")}}}
    suspend fun shutdown() {
        val withdrawn=synchronized(authority) {if(closed) return; closed=true; visible=false; epoch++; pending=null; offers.values.toList().also {offers.clear(); mutable.update {it.copy(review=null)}}}
        signals.values.forEach {it.cancel()}; scope.cancel(); scope.coroutineContext.job.join()
        withContext(Dispatchers.IO+NonCancellable) {withdrawn.forEach {release(it.lease)}; leases.toList().forEach(::release); ownerLock.release(); ownerFile.close()}
    }
    companion object {
        private const val REVIEW_TIME=300_000L
        private const val ACCESS=Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION
        private fun label(root:String,parents:List<SafFolderBinding>)=(listOf(root)+parents.map {it.name}).joinToString(" / ")
    }
}
