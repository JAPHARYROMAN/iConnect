package com.iconnect.proof

import android.content.Context
import android.net.Uri
import android.os.CancellationSignal
import android.provider.DocumentsContract as Docs
import com.iconnect.protocol.*
import org.json.JSONObject
import java.io.File
import java.util.concurrent.ConcurrentHashMap
import kotlinx.coroutines.joinAll

data class PhoneIncomingBatchFileProgress(val batch: String, val index: Int, val file: PhoneIncomingCopy,
    val estimate: PhonePayloadRateEstimate?, val finalization: PhoneIncomingFinalization?)

/** Owner-gate serialized scopes; provider bytes still flow through AndroidIncomingUploads. */
internal class AndroidBatchFiles(private val context:Context,private val grants:AndroidStorageGrants,private val metadata:File,
    private val children:AndroidBatchDirectories,private val lookup:(String)->AndroidBatchPlans.Row?,private val ready:()->Unit,
    private val fail:(String)->Nothing,private val changed:()->Unit) {
    private val gate=grants.owner.gate
    private val store=AndroidBatchFileStore(context,metadata,grants.owner,fail)
    private val access=AndroidSafAccess(context)
    private val retainedLeases=mutableSetOf<String>()
    private data class FileReceiver(val uploads: AndroidIncomingUploads, val row: AndroidBatchPlans.Row, val index: Int)
    private val receivers = ConcurrentHashMap<String, FileReceiver>()
    private var closed=false
    fun readFileProgress(peer: String, pairing: String, limit: Long): List<PhoneIncomingBatchFileProgress> =
        receivers.values.mapNotNull { receiver ->
            val row = receiver.row
            if (row.peer != peer || row.target.pairing != pairing || row.phase != UploadBatchPlanPhase.UPLOAD_PLAN_READY) return@mapNotNull null
            val rows = receiver.uploads.state.value
            val file = rows.singleOrNull() ?: return@mapNotNull null
            if (file.phase != UploadPhase.UPLOAD_RECEIVING && file.phase != UploadPhase.UPLOAD_VERIFYING &&
                file.phase != UploadPhase.UPLOAD_FINALIZING) return@mapNotNull null
            PhoneIncomingBatchFileProgress(row.header.id, receiver.index, file, receiver.uploads.readPayloads(limit, rows)[file.id],
                receiver.uploads.readFinalization(file.id))
        }.sortedBy { it.index }
    fun clearFilePayloadRates() { receivers.values.forEach { it.uploads.clearPayloadRates() } }

    private fun descriptor(row:AndroidBatchPlans.Row,manifest:UploadBatchManifest,child:AndroidBatchDirectories.Child)=manifest.getItems(child.index).let {
        UploadInfo.newBuilder().setId(child.id).setName(it.name).setSize(it.size).setSha256(it.sha256).setCollision(manifest.collision).build()
    }
    private fun config(row:AndroidBatchPlans.Row,manifest:UploadBatchManifest,child:AndroidBatchDirectories.Child,target:IncomingTarget,fresh:Boolean):AndroidScopedIncoming {
        val h=row.header; val name=AndroidBatchFileStore.name(h.id,child.index); var firstCommit=fresh
        fun current() {
            ready(); check(!closed)
            val retained=lookup(h.id)?:throw StorageFailure(FileStatus.STALE_REFERENCE,"The original batch no longer owns this file child.")
            if(retained!=row) throw StorageFailure(FileStatus.STALE_REFERENCE,"The original batch no longer owns this file child.")
            val actual=children.readChild(retained,children.manifest(retained),child.index)
            if(actual!=child) fail("The retained file-child binding changed. Existing copies were kept.")
            store.checkJournal(name,firstCommit)
        }
        lateinit var scope:AndroidScopedIncoming
        scope=AndroidScopedIncoming(File(metadata,name),child.id,row.peer,descriptor(row,manifest,child),target,fresh,store,::current,persist={text->
            current()
            AndroidIncomingUploads.validateScopedSnapshot(JSONObject(text),scope,grants.owner.deviceId)
            store.write(name,text,firstCommit); firstCommit=false; changed()
        },stopOnly=row.phase==UploadBatchPlanPhase.UPLOAD_PLAN_STOPPING)
        return scope
    }
    private fun logicalTarget(row:AndroidBatchPlans.Row,manifest:UploadBatchManifest,child:AndroidBatchDirectories.Child,all:Map<Int,AndroidBatchDirectories.Child>):IncomingTarget {
        val chain=mutableListOf<SafFolderBinding>(); var parent=manifest.getItems(child.index).parent
        while(parent>=0) {
            val ancestor=all[parent]?.takeIf {it.directory && it.phase==UploadBatchItemPhase.BATCH_ITEM_SUCCEEDED}?:error("A file child lost its original parent receipt.")
            chain.add(SafFolderBinding(ancestor.document,ancestor.name)); parent=manifest.getItems(parent).parent
        }
        val parents=row.target.parents+chain.asReversed(); SafDestination.validateBindings(parents)
        require(child.parent==(parents.lastOrNull()?.id?:Docs.getTreeDocumentId(Uri.parse(row.target.tree))))
        return row.target.copy(parents=parents)
    }
    fun load(rows:Map<String,AndroidBatchPlans.Row>,paths:List<File>,retained:Map<String,Map<Int,AndroidBatchDirectories.Child>>) {
        store.load(paths); val expected=mutableSetOf<String>(); val leases=linkedMapOf<String,Uri>()
        retained.forEach {(id,items)->val row=rows.getValue(id); val manifest=children.manifest(row)
            items.values.filter {!it.directory}.forEach {child->
                val name=AndroidBatchFileStore.name(id,child.index); expected.add(name)
                AndroidIncomingUploads.validateScopedJournal(config(row,manifest,child,logicalTarget(row,manifest,child,items),false),grants.owner.deviceId)
                if(!UploadBatchRules.terminal(row.phase)) leases[leaseName(id)]=Uri.parse(row.target.tree)
            }
        }
        paths.forEach {file->val parsed=AndroidBatchFileStore.parse(file.name); if(parsed!=null) require(file.name in expected || rows[parsed.first]?.retiring==true) {"An orphaned batch file journal was kept."}}
        access.reconcile(LEASE_PREFIX,leases); retainedLeases.addAll(leases.keys)
    }
    private fun room() {
        if(receivers.size<2) return
        val idle=receivers.entries.firstOrNull {it.value.uploads.isIdle}?:return
        receivers.remove(idle.key); idle.value.uploads.closeIdle()
    }
    fun execute(original:AndroidBatchPlans.Row,session:String,request:FileRequest,allowed:()->Boolean,signal:CancellationSignal):FileResponse {
        ready(); check(!closed); signal.throwIfCanceled()
        require(request.hasBatchItemIndex() && request.batchItemIndex in 0 until original.header.itemCount)
        if(original.phase!=UploadBatchPlanPhase.UPLOAD_PLAN_READY && !(original.phase==UploadBatchPlanPhase.UPLOAD_PLAN_STOPPING && request.operation in setOf(FileOperation.CANCEL_BATCH_FILE,FileOperation.UPLOAD_BATCH_ITEM_STATUS))) throw StorageFailure(FileStatus.CONFLICT,"This batch cannot start or continue file effects.")
        var row=original; val manifest=children.manifest(row); val index=request.batchItemIndex; val item=manifest.getItems(index)
        if(item.isDirectory || item.error.isNotEmpty()) throw StorageFailure(FileStatus.UNSUPPORTED,"This planned item is not a usable file.")
        val id=UploadBatchRules.childId(row.peer,row.header,index,false)
        val expected=UploadInfo.newBuilder().setId(id).setName(item.name).setSize(item.size).setSha256(item.sha256).setCollision(manifest.collision).build()
        require(request.destinationId.isEmpty() && request.nodeId.isEmpty() && request.name.isEmpty() && (!request.hasBatchHeader() || request.batchHeader==row.header) &&
            (!request.hasUpload() || request.upload==expected) && (request.operation==FileOperation.WRITE_BATCH_FILE || request.offset==0L && request.content.isEmpty)) {"Use the original file descriptor and operation fields."}
        val destination=children.destination(row,manifest,index,signal); val parent=Docs.getDocumentId(destination.parent); val target=row.target.copy(parents=destination.parents)
        val grant=grants.get(row.target.grant,row.peer); if(!grants.visible(grant,item.name)) throw StorageFailure(FileStatus.ACCESS_DENIED,"The planned filename is excluded.")
        var attempts=children.readAttempts(row); var child=children.readChild(row,manifest,index); val fresh=child==null
        if(row.effectsStarted && attempts==null || fresh && attempts!=null && children.attempted(attempts,index)) fail("A started batch file lost its ownership metadata. Private and provider copies were kept.")
        if(fresh) {
            if(row.phase!=UploadBatchPlanPhase.UPLOAD_PLAN_READY) throw StorageFailure(FileStatus.NOT_FOUND,"The stopped batch has no retained file child.")
            if(request.operation!=FileOperation.PREPARE_BATCH_FILE) throw StorageFailure(FileStatus.NOT_FOUND,"No retained file receipt exists. Status and retry do not create another copy.")
            room(); if(receivers.size>=2) throw StorageFailure(FileStatus.RESOURCE_EXHAUSTED,"Two batch files are busy. Retry preparation after one becomes idle.")
            store.checkJournal(AndroidBatchFileStore.name(row.header.id,index),true); store.reserve(row.header.id); store.canCreate(); children.reserveBinding(row)
            val unwritten=(grants.ordinaryIncoming?.remainingBytes()?:0)+store.remainingBytes()
            if(item.size>(store.directory.usableSpace-64L*1024*1024-unwritten).coerceAtLeast(0)) throw StorageFailure(FileStatus.RESOURCE_EXHAUSTED,"Private incoming storage is full. Review existing staging before preparing another file.")
            val lease=leaseName(row.header.id)
            if(lease !in retainedLeases) {access.retainRecorded(lease,Uri.parse(row.target.tree)); retainedLeases.add(lease)}
            row=children.beginFile(row); child=AndroidBatchDirectories.Child(index,id,item.name,parent,"",UploadBatchItemPhase.BATCH_ITEM_PENDING,"The file child belongs to its original accepted plan.",false)
            children.saveChild(row,child); attempts=children.readAttempts(row)?:error("Missing file attempt index.")
            attempts.bits[index/8]=(attempts.bits[index/8].toInt() or (1 shl (index%8))).toByte(); children.saveAttempts(row,attempts)
        } else {
            if(child.directory || child.parent!=parent) throw StorageFailure(FileStatus.STALE_REFERENCE,"The file child's original provider parent changed.")
            if(attempts==null || !children.attempted(attempts,index)) fail("A retained file child lost its attempt marker.")
            store.checkJournal(AndroidBatchFileStore.name(row.header.id,index))
        }
        val scope=config(row,manifest,child,target,fresh)
        receivers[id]?.takeIf {it.row.phase!=row.phase}?.let {old->
            if(!old.uploads.isIdle) {old.uploads.pauseAll(); throw StorageFailure(FileStatus.CONFLICT,"The earlier file verification is stopping. Retry after it becomes idle.")}
            receivers.remove(id); old.uploads.closeIdle()
        }
        var receiver=receivers[id]?.uploads; var transient=false
        if(receiver==null) {
            room(); if(receivers.size>=2 && request.operation!=FileOperation.UPLOAD_BATCH_ITEM_STATUS) throw StorageFailure(FileStatus.RESOURCE_EXHAUSTED,"Two batch files are busy. Retry after verification finishes.")
            receiver=AndroidIncomingUploads(context,grants,grants.owner.deviceId,scope)
            if(receiver.recoveryIssues().isNotEmpty()) {receiver.closeIdle(); fail("The retained batch file journal needs local review.")}
            if(receivers.size<2) receivers[id]=FileReceiver(receiver,row,index) else transient=true
        }
        val operation=when(request.operation) {FileOperation.PREPARE_BATCH_FILE->FileOperation.PREPARE_UPLOAD; FileOperation.WRITE_BATCH_FILE->FileOperation.UPLOAD_WRITE
            FileOperation.FINISH_BATCH_FILE->FileOperation.FINISH_UPLOAD; FileOperation.CANCEL_BATCH_FILE->FileOperation.CANCEL_UPLOAD; FileOperation.UPLOAD_BATCH_ITEM_STATUS->FileOperation.UPLOAD_STATUS
            else->throw StorageFailure(FileStatus.UNSUPPORTED,"Unsupported batch file operation.")}
        try {
            val inner=FileRequest.newBuilder().setRequestId(request.requestId).setOperation(operation).setTransferId(id).setMutationEpoch(receiver.historyEpoch).setOffset(request.offset).setContent(request.content)
            if(operation==FileOperation.PREPARE_UPLOAD) inner.setUpload(expected)
            val response=receiver.execute(row.peer,inner.build(),{target},session,allowed,signal); val file=response.upload
            val phase=when(file.phase) {UploadPhase.UPLOAD_COMPLETED->UploadBatchItemPhase.BATCH_ITEM_SUCCEEDED; UploadPhase.UPLOAD_SKIPPED->UploadBatchItemPhase.BATCH_ITEM_SKIPPED
                UploadPhase.UPLOAD_CANCELED->UploadBatchItemPhase.BATCH_ITEM_FAILED; UploadPhase.UPLOAD_REVIEW->UploadBatchItemPhase.BATCH_ITEM_REVIEW; else->UploadBatchItemPhase.BATCH_ITEM_PENDING}
            return response.toBuilder().setBatchItem(UploadBatchItemReceipt.newBuilder().setHeader(row.header).setIndex(index).setId(id).setIsDirectory(false)
                .setPhase(phase).setResultingName(if(file.phase==UploadPhase.UPLOAD_COMPLETED) file.finalName else "").setMessage(file.message)).build()
        } catch(e:android.system.ErrnoException) {throw StorageFailure(FileStatus.IO_ERROR,"The private batch stage is unavailable. Existing copies were kept.")}
        catch(e:IllegalStateException) {throw StorageFailure(FileStatus.CONFLICT,"The private batch checkpoint or provider destination needs local review. Existing copies were kept.")}
        finally {if(transient) receiver.closeIdle()}
    }
    fun remainingBytes()=store.remainingBytes()
    internal fun inventory(row:AndroidBatchPlans.Row,manifest:UploadBatchManifest,child:AndroidBatchDirectories.Child,all:Map<Int,AndroidBatchDirectories.Child>):Pair<AndroidScopedIncoming,UploadInfo> {
        store.checkJournal(AndroidBatchFileStore.name(row.header.id,child.index))
        val scope=config(row,manifest,child,logicalTarget(row,manifest,child,all),false)
        return scope to AndroidIncomingUploads.inspectScopedJournal(scope,grants.owner.deviceId)
    }
    internal fun busy(id:String)=receivers.values.any {it.row.header.id==id && !it.uploads.isIdle}
    internal fun removeRetiredJournal(id:String,index:Int)=store.removeRetired(AndroidBatchFileStore.name(id,index))
    internal fun pausePlanNow(peer:String,id:String,epoch:String,header:UploadBatchHeader?) {
        receivers.values.forEach {receiver->val row=receiver.row; if(row.peer==peer && row.header.id==id && row.header.epoch==epoch && (header==null || header==row.header)) receiver.uploads.interruptScopedVerification()}
    }
    internal fun stopPlan(row:AndroidBatchPlans.Row,manifest:UploadBatchManifest,signal:CancellationSignal):Int? {
        require(row.phase==UploadBatchPlanPhase.UPLOAD_PLAN_STOPPING)
        val relevant=receivers.filterValues {it.row.header.id==row.header.id}
        relevant.values.forEach {it.uploads.pauseAll()}; if(relevant.values.any {!it.uploads.isIdle}) return null
        relevant.forEach {(key,receiver)->receiver.uploads.closeIdle(); receivers.remove(key)}
        var cleaned=0; var scanned=0; var cursor=row.stopCursor; val attempts=children.readAttempts(row); val bindings=linkedMapOf<Int,AndroidBatchDirectories.Child>()
        if(row.effectsStarted && attempts==null) fail("The stopped batch lost its attempt inventory.")
        fun binding(index:Int):AndroidBatchDirectories.Child? {
            bindings[index]?.let {return it}; val value=children.readChild(row,manifest,index)
            if(attempts!=null && children.attempted(attempts,index)!=(value!=null)) fail("A stopped item lost its ownership receipt.")
            if(value!=null) bindings[index]=value; return value
        }
        while(cursor<row.header.itemCount && scanned++<256) {
            signal.throwIfCanceled(); val child=binding(cursor)
            if(child!=null && !child.directory) {
                var parent=manifest.getItems(cursor).parent
                while(parent>=0) {binding(parent); parent=manifest.getItems(parent).parent}
                val file=inventory(row,manifest,child,bindings)
                if(file.second.phase !in setOf(UploadPhase.UPLOAD_COMPLETED,UploadPhase.UPLOAD_SKIPPED,UploadPhase.UPLOAD_CANCELED) || AndroidIncomingUploads.scopedPartExists(file.first,grants.owner.deviceId)) {
                    if(cleaned++==16) break
                    val host=AndroidIncomingUploads(context,grants,grants.owner.deviceId,file.first)
                    try {if(host.recoveryIssues().isNotEmpty()) fail("Stopped file ownership needs local review."); host.stopScopedPrivate()} finally {host.closeIdle()}
                }
            }
            cursor++
        }
        return cursor
    }
    internal fun releasePlanLease(id:String) {val name=leaseName(id); if(name in retainedLeases) {access.release(name); retainedLeases.remove(name)}}
    fun orphanCount()=store.orphanCount()
    private fun idleForOrphans() {check(!closed && receivers.values.all {it.uploads.isIdle}) {"Wait for batch file verification before reviewing private parts."}}
    fun orphanRemovalProgress()=store.orphanRemovalProgress()
    fun inspectOrphans(start:Int,allowed:()->Boolean):List<PhoneUnreferencedReceivingPart> {idleForOrphans(); return store.inspectOrphans(start,allowed)}
    fun reviewOrphanRemoval(allowed:()->Boolean):PhoneReceivingPartRemovalReview {idleForOrphans(); return store.reviewOrphanRemoval(allowed)}
    fun confirmOrphanRemoval(value:PhoneReceivingPartRemovalReview,allowed:()->Boolean):Boolean {idleForOrphans(); try {return store.confirmOrphanRemoval(value,allowed)} finally {changed()}}
    fun continueOrphanRemoval(allowed:()->Boolean):Boolean {idleForOrphans(); try {return store.continueOrphanRemoval(allowed)} finally {changed()}}
    fun cancelScope(scope:String) {receivers.values.forEach {it.uploads.cancelScope(scope)}}
    fun pauseAll() {receivers.values.forEach {it.uploads.pauseAll()}}
    suspend fun whenIdle() {val snapshot=synchronized(gate) {receivers.values.map {it.uploads}}; snapshot.forEach {it.whenIdle()}}
    fun resetMetadata() {check(receivers.values.all {it.uploads.isIdle}); receivers.values.forEach {it.uploads.closeIdle()}; receivers.clear(); store.resetMetadata(); access.reconcile(LEASE_PREFIX,emptyMap()); retainedLeases.clear(); changed()}
    suspend fun shutdown() {
        val snapshot=synchronized(gate) {if(closed) return; closed=true; receivers.values.map {it.uploads}}
        snapshot.forEach {it.pauseAll()}; snapshot.forEach {it.shutdown()}; synchronized(gate) {receivers.clear(); store.close()}
    }
    companion object {private const val LEASE_PREFIX="batch-incoming:"; private fun leaseName(id:String)=LEASE_PREFIX+id}
}
