package com.iconnect.proof

import android.content.Context
import android.net.Uri
import android.os.CancellationSignal
import android.system.Os
import android.system.OsConstants
import androidx.core.net.toUri
import com.iconnect.protocol.*
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.IOException
import java.nio.file.Files
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow

data class PhoneIncomingBatchPlan(val id:String,val peer:String,val bytes:Long,val items:Int,val phase:UploadBatchPlanPhase,val effectsStarted:Boolean=false,val retiring:Boolean=false,val retired:Boolean=false,val retirementCursor:Int=0)
data class PhoneIncomingBatchRetirementReview(val id:String,val plan:String,val items:Int,val metadataFiles:Int,val metadataBytes:Long,val phase:UploadBatchPlanPhase)

/** Durable admission and scoped child effects. Never adopts a provider item by name. */
internal class AndroidBatchPlans(private val context:Context,private val grants:AndroidStorageGrants) {
    internal data class Row(val owner:String,val peer:String,val target:IncomingTarget,val header:UploadBatchHeader,val phase:UploadBatchPlanPhase,val effectsStarted:Boolean=false,val outcomes:String="",val stopCursor:Int=0,
        val retiring:Boolean=false,val retired:Boolean=false,val retirementCursor:Int=0,val retirementBits:String="")
    private data class AssemblyWork(val scope:String,val value:UploadBatchAssembly)
    private val gate=grants.owner.gate
    private val directory=File(context.filesDir,"incoming-batch-plans")
    private var recovery=StateRecovery(directory)
    private var epoch=StorageRules.token()
    private var owner=grants.owner.deviceId
    private var metadataIdentity=""
    private val records=linkedMapOf<String,Row>()
    private val assemblies=linkedMapOf<String,AssemblyWork>()
    private data class RetirementFile(val name:String,val version:String,val bytes:Long)
    private data class RetirementReview(val value:PhoneIncomingBatchRetirementReview,val files:List<RetirementFile>,val expires:Long)
    private val retirementReviews=linkedMapOf<String,RetirementReview>()
    @Volatile private var fileHost:AndroidBatchFiles?=null
    private var closed=false
    private var maintenance=false
    private val mutable=MutableStateFlow(0L)
    val changes=mutable.asStateFlow()
    private fun changed() {mutable.value=mutable.value+1}
    private val children=AndroidBatchDirectories(context,grants,directory,::accepted,::startEffects,{recovery.block(it); changed()},::changed)
    private val publishedChecks=AndroidPublishedBatchChecks(context,grants,::publishedSnapshot,::publishedCurrent,::publishedHistory)
    private fun failFiles(message:String):Nothing {recovery.block(message); changed(); throw StorageFailure(FileStatus.CONFLICT,message)}
    private fun files():AndroidBatchFiles=fileHost?:AndroidBatchFiles(context,grants,directory,children,{records[it]},::ready,::failFiles,::changed).also {fileHost=it}
    init {synchronized(gate) {runCatching {
        check(directory.mkdirs() || directory.isDirectory)
        check(directory.canonicalFile.parentFile==context.filesDir.canonicalFile && OsConstants.S_ISDIR(Os.lstat(directory.path).st_mode))
        metadataIdentity=directoryIdentity()
        val paths=StateRecovery.journals(directory,AndroidBatchDirectories.MAX_FILES); require(paths.size<=AndroidBatchDirectories.MAX_FILES)
        if(paths.isEmpty()) {
            val parts=File(context.filesDir,"incoming-batch-parts")
            if(parts.exists()) java.nio.file.Files.newDirectoryStream(parts.toPath()).use {stream->require(stream.none {it.fileName.toString()!="owner.lock"}) {"Batch metadata is missing while private copies remain."}}
            saveContext()
        }
        else {
            val root=JSONObject(PrivateJsonFile(File(directory,"context.json"),4096).read())
            require(root.getInt("schema")==1 && root.getString("owner")==owner && UploadBatchRules.canonicalToken(root.getString("epoch"))); epoch=root.getString("epoch")
            val rows=paths.filter {it.name.endsWith(".json") && UploadBatchRules.canonicalToken(it.name.removeSuffix(".json"))}; require(rows.size<=MAX_DETAILED_PLANS+MAX_RETIRED_PLANS)
            rows.forEach {file->val row=decode(JSONObject(PrivateJsonFile(file,RECORD_LIMIT).read())); require(file.name==row.header.id+".json" && records.put(row.header.id,row)==null)}
            require(records.values.count {!it.retired}<=MAX_DETAILED_PLANS && records.values.count {it.retired}<=MAX_RETIRED_PLANS && records.values.count {!UploadBatchRules.terminal(it.phase)}<=UploadBatchRules.MAX_PLANS && records.values.filter {!UploadBatchRules.terminal(it.phase)}.groupBy {it.peer}.values.all {it.size<=UploadBatchRules.MAX_PEER_PLANS})
            val reserved=records.values.filter {!UploadBatchRules.terminal(it.phase)}
            require(reserved.sumOf {it.header.totalBytes}<=UploadRules.QUOTA*4 && reserved.groupBy {it.peer}.values.all {rows->rows.sumOf {it.header.totalBytes}<=UploadRules.QUOTA})
            val detailed=records.filterValues {!it.retiring && !it.retired}
            val retained=children.load(detailed,paths.filter {file->val id=file.name.substringBefore('.'); val row=records[id]; row==null || !row.retiring && !row.retired || file.name==id+".json"})
            if(retained.values.any {group->group.values.any {!it.directory}} || paths.any {AndroidBatchFileStore.parse(it.name)!=null} || File(context.filesDir,"incoming-batch-parts").exists())
                files().load(records,paths,retained)
            records.values.filter {it.retiring || it.retired}.forEach(::validateRetirementInventory)
            records.values.filter {!it.retiring && !it.retired && it.phase in setOf(UploadBatchPlanPhase.UPLOAD_PLAN_STOPPED,UploadBatchPlanPhase.UPLOAD_PLAN_COMPLETED)}.forEach {row->
                require(outcomes(row,inventory(row),row.phase==UploadBatchPlanPhase.UPLOAD_PLAN_COMPLETED,false).contentEquals(Rules.decode(row.outcomes))) {"Terminal batch outcomes do not match their retained item receipts."}
            }
        }
    }.onFailure {
        records.clear(); recovery.block("Batch-plan metadata needs local review. Incoming writes are blocked; provider files are kept.")
        if(fileHost==null && File(context.filesDir,"incoming-batch-parts").exists()) runCatching {files()}.onFailure {recovery.block("Private batch staging could not be opened. Preserve the app's private data for local review.")}
    }; changed()}}
    private fun saveContext()=PrivateJsonFile(File(directory,"context.json"),4096).write(JSONObject().put("schema",1).put("owner",owner).put("epoch",epoch).toString())
    private fun encode(row:Row,manifest:ByteArray)=JSONObject().put("schema",3).put("owner",row.owner).put("peer",row.peer)
        .put("grant",row.target.grant).put("tree",row.target.tree).put("pairing",row.target.pairing)
        .put("parents",JSONArray().also {array->row.target.parents.forEach {array.put(JSONObject().put("id",it.id).put("name",it.name))}})
        .put("header",Rules.b64(row.header.toByteArray())).put("phase",row.phase.name).put("effectsStarted",row.effectsStarted).put("manifest",Rules.b64(manifest)).put("outcomes",row.outcomes).put("stopCursor",row.stopCursor)
        .put("retiring",row.retiring).put("retired",row.retired).put("retirementCursor",row.retirementCursor).put("retirementBits",row.retirementBits)
    private fun decode(value:JSONObject):Row {
        val schema=value.getInt("schema"); require(schema in 1..3 && (schema==3 || listOf("retiring","retired","retirementCursor","retirementBits").none(value::has))); val parents=value.getJSONArray("parents"); require(parents.length()<=32)
        val header=UploadBatchHeader.parseFrom(Rules.decode(value.getString("header"))); UploadBatchRules.validateHeader(header)
        val row=Row(value.getString("owner"),value.getString("peer"),IncomingTarget(value.getString("grant"),value.getString("tree"),value.getString("pairing"),
            (0 until parents.length()).map {parents.getJSONObject(it).let {p->SafFolderBinding(p.getString("id"),p.getString("name"))}}),header,UploadBatchPlanPhase.valueOf(value.getString("phase")),value.optBoolean("effectsStarted",false),value.optString("outcomes",""),value.optInt("stopCursor",0),
            if(schema==3) value.getBoolean("retiring") else false,if(schema==3) value.getBoolean("retired") else false,if(schema==3) value.getInt("retirementCursor") else 0,if(schema==3) value.getString("retirementBits") else "")
        require(row.owner==owner && row.header.epoch==epoch && row.peer.matches(Regex("[a-f0-9]{64}")) && UploadBatchRules.canonicalToken(row.target.pairing) &&
            row.target.grant==header.grantId && row.target.tree.length<=16_384 && row.phase!=UploadBatchPlanPhase.UNRECOGNIZED)
        AndroidSafAccess.validateTree(Uri.parse(row.target.tree)); SafDestination.validateBindings(row.target.parents)
        require(schema!=1 || row.phase in setOf(UploadBatchPlanPhase.UPLOAD_PLAN_RECEIVING,UploadBatchPlanPhase.UPLOAD_PLAN_READY,UploadBatchPlanPhase.UPLOAD_PLAN_CANCELED) && row.outcomes.isEmpty())
        require(!row.effectsStarted || UploadBatchRules.hasManifest(row.phase))
        require(row.phase !in setOf(UploadBatchPlanPhase.UPLOAD_PLAN_STOPPING,UploadBatchPlanPhase.UPLOAD_PLAN_STOPPED) || row.effectsStarted)
        val outcomes=Rules.decode(row.outcomes); require(Rules.b64(outcomes)==row.outcomes); UploadBatchRules.validateOutcomes(row.phase,header.itemCount,outcomes)
        require(row.stopCursor in 0..header.itemCount && (row.phase!=UploadBatchPlanPhase.UPLOAD_PLAN_STOPPED || row.stopCursor==header.itemCount) &&
            (row.phase in setOf(UploadBatchPlanPhase.UPLOAD_PLAN_STOPPING,UploadBatchPlanPhase.UPLOAD_PLAN_STOPPED) || row.stopCursor==0))
        val manifest=Rules.decode(value.getString("manifest"))
        validateRetirementRow(row)
        if(row.retired) require(manifest.isEmpty()) else if(UploadBatchRules.hasManifest(row.phase)) UploadBatchRules.decode(header,manifest) else require(manifest.isEmpty())
        return row
    }
    private fun accepted(row:Row):ByteArray {
        check(!row.retired) {"This compact parent receipt has no retained item manifest."}
        val value=JSONObject(PrivateJsonFile(File(directory,row.header.id+".json"),RECORD_LIMIT).read())
        require(decode(value)==row && UploadBatchRules.hasManifest(row.phase)) {"The accepted batch metadata changed."}
        return Rules.decode(value.getString("manifest"))
    }
    private fun startEffects(row:Row):Row {val encoded=accepted(row); return row.copy(effectsStarted=true).also {put(it,encoded)}}
    private fun ready() {recovery.requireHealthy(); if(closed || maintenance) throw StorageFailure(FileStatus.CONFLICT,"Batch history is closed or is being reviewed."); check(grants.owner.healthy && owner==grants.owner.deviceId) {"Batch history belongs to an earlier phone owner."}}
    private fun put(row:Row,manifest:ByteArray=byteArrayOf()) {
        val previous=records[row.header.id]?.phase
        val encoded=encode(row,manifest); require(decode(encoded)==row)
        try {PrivateJsonFile(File(directory,row.header.id+".json"),RECORD_LIMIT).write(encoded.toString()); records[row.header.id]=row; changed()
            if(previous!=row.phase && UploadBatchRules.terminal(row.phase)) {
                val allCopied=row.outcomes.isNotEmpty() && Rules.decode(row.outcomes).all {it.toInt()==1}
                grants.recordActivity(PhoneActivityEvent(PhoneActivityKind.BATCH_RECEIVE,
                    if(row.phase==UploadBatchPlanPhase.UPLOAD_PLAN_COMPLETED) if(allCopied) PhoneActivityOutcome.COMPLETED else PhoneActivityOutcome.COMPLETED_WITH_ISSUES else PhoneActivityOutcome.CANCELED,row.peer,
                    if(row.phase==UploadBatchPlanPhase.UPLOAD_PLAN_COMPLETED && allCopied) row.header.totalBytes else -1))}}
        catch(e:Exception) {recovery.block("Batch progress could not be committed. Preserve the metadata and review this queue locally."); changed(); throw e}
    }
    private fun authorize(row:Row,peer:String,allowed:()->Boolean,signal:CancellationSignal) {
        ready(); signal.throwIfCanceled(); check(allowed()) {"Phone batch admission ended."}
        if(row.peer!=peer || grants.owner.current(peer)?.epoch!=row.target.pairing) throw StorageFailure(FileStatus.ACCESS_DENIED,"This batch belongs to another pairing generation.")
        val grant=grants.get(row.target.grant,peer); StorageRules.requirePermission(grant.permissions,StorageRules.WRITE or StorageRules.CREATE)
        if(grant.tree.toString()!=row.target.tree || row.target.parents.any {!grants.visible(grant,it.name)}) throw StorageFailure(FileStatus.ACCESS_DENIED,"This batch approval changed or is excluded.")
        check(context.contentResolver.persistedUriPermissions.any {it.uri==grant.tree && it.isReadPermission && it.isWritePermission}) {"Android destination access ended."}
        try {SafDestination(context,grant.tree,row.target.parents,signal).validate()}
        catch(e:IllegalStateException) {throw StorageFailure(FileStatus.STALE_REFERENCE,"The original batch destination moved, was renamed or is no longer writable. Existing documents were kept.")}
    }
    fun reservedBytes(peer:String?):Long=synchronized(gate) {ready(); records.values.filter {!UploadBatchRules.terminal(it.phase) && (peer==null || it.peer==peer)}.sumOf {it.header.totalBytes}}
    fun all():List<PhoneIncomingBatchPlan> =synchronized(gate) {records.values.map {PhoneIncomingBatchPlan(it.header.id,it.peer,it.header.totalBytes,it.header.itemCount,it.phase,it.effectsStarted,it.retiring,it.retired,it.retirementCursor)}}
    fun recoveryIssues()=synchronized(gate) {recovery.all()}
    fun privateUnwrittenBytes():Long=synchronized(gate) {ready(); fileHost?.remainingBytes()?:0}
    fun orphanParts():Int=synchronized(gate) {fileHost?.orphanCount()?:0}
    fun orphanRemovalProgress()=synchronized(gate) {fileHost?.orphanRemovalProgress()}
    fun inspectOrphanParts(start:Int,allowed:()->Boolean={true}):List<PhoneUnreferencedReceivingPart> =grants.authorized {synchronized(gate) {ready(); files().inspectOrphans(start,allowed)}}
    fun reviewOrphanPartRemoval(allowed:()->Boolean={true}):PhoneReceivingPartRemovalReview=grants.authorized {synchronized(gate) {ready(); files().reviewOrphanRemoval(allowed)}}
    fun confirmOrphanPartRemoval(value:PhoneReceivingPartRemovalReview,allowed:()->Boolean={true}):Boolean=grants.authorized {synchronized(gate) {ready(); files().confirmOrphanRemoval(value,allowed)}}
    fun continueOrphanPartRemoval(allowed:()->Boolean={true}):Boolean=grants.authorized {synchronized(gate) {ready(); files().continueOrphanRemoval(allowed)}}
    fun pauseFiles() {publishedChecks.cancelAllNow(); fileHost?.pauseAll()}
    fun cancelPublishedChecksNow() {publishedChecks.cancelAllNow()}
    fun cancelPublishedCheckNow(peer:String,scope:String,request:FileRequest) {publishedChecks.cancelNow(peer,scope,request)}
    fun publishedCancellationRefusal(peer:String,scope:String,request:FileRequest):StorageFailure? = publishedChecks.cancellationRefusal(peer,scope,request)
    fun readFileProgress(peer: String, pairing: String, limit: Long) = fileHost?.readFileProgress(peer, pairing, limit).orEmpty()
    fun clearFilePayloadRates() {fileHost?.clearFilePayloadRates()}
    fun cancelFileScopeNow(scope:String) {publishedChecks.cancelScopeNow(scope); fileHost?.cancelScope(scope)}
    suspend fun whenFilesIdle() {val host=synchronized(gate) {fileHost}; publishedChecks.whenIdle(); host?.whenIdle()}
    private fun receipt(row:Row,scope:String):UploadBatchReceipt {
        val offset=if(UploadBatchRules.hasManifest(row.phase)) row.header.encodedLength else assemblies[row.header.id]?.takeIf {it.scope==scope}?.value?.offset?:0
        val message=if(row.retiring || row.retired) "Final parent outcomes are retained. The receiver owner is removing detailed item history; no new copy can be started from this intent." else when(row.phase) {
            UploadBatchPlanPhase.UPLOAD_PLAN_READY->if(row.effectsStarted) "Batch child effects may have started. Recover retained child receipts before continuing." else "The private batch plan is durably accepted. No provider documents have been created."
            UploadBatchPlanPhase.UPLOAD_PLAN_CANCELED->"The unstarted batch plan was canceled. Provider files were kept."
            UploadBatchPlanPhase.UPLOAD_PLAN_STOPPING->"New batch work is stopped. Repeat Stop while retained verification or private cleanup finishes."
            UploadBatchPlanPhase.UPLOAD_PLAN_STOPPED->"Stopped. Identified private staging was removed; possible provider copies were kept."
            UploadBatchPlanPhase.UPLOAD_PLAN_COMPLETED->"All planned items have durable final outcomes. Provider copies were kept."
            else->"Receiving the manifest. Check status after reconnect; assembly offsets are not durable."
        }
        return UploadBatchReceipt.newBuilder().setHeader(row.header).setPhase(row.phase).setManifestOffset(offset).setMessage(message).setEffectsStarted(row.effectsStarted).setItemOutcomes(com.google.protobuf.ByteString.copyFrom(Rules.decode(row.outcomes))).build()
    }
    fun execute(peer:String,scope:String,request:FileRequest,target:()->IncomingTarget,ordinaryReserved:(String?)->Long,allowed:()->Boolean,signal:CancellationSignal):FileResponse {
        // A provider write may be blocked while holding the owner gate. Pausing only an
        // already-bound worker is reversible; durable Stop still requires authorization below.
        // This mirrors session withdrawal's descriptor cancellation, without deleting data.
        if(!signal.isCanceled) pauseForStop(peer,request)
        return synchronized(gate) {
        ready(); signal.throwIfCanceled(); check(allowed()); if(grants.owner.current(peer)==null) throw StorageFailure(FileStatus.ACCESS_DENIED,"Batch admission requires a current paired device.")
        val result=FileResponse.newBuilder().setRequestId(request.requestId).setMutationEpoch(epoch)
        if(request.operation==FileOperation.UPLOAD_BATCH_CONTEXT) return@synchronized result.build()
        if(request.mutationEpoch!=epoch) throw StorageFailure(FileStatus.STALE_REFERENCE,"Batch history changed. Review the original intent before another batch.")
        require(UploadBatchRules.canonicalToken(request.transferId)); var row:Row
        if(request.operation==FileOperation.BEGIN_UPLOAD_BATCH) {
            require(request.hasBatchHeader()); val header=request.batchHeader; UploadBatchRules.validateHeader(header)
            require(header.id==request.transferId && header.epoch==epoch); val destination=target(); require(destination.grant==header.grantId)
            row=Row(owner,peer,destination,header,UploadBatchPlanPhase.UPLOAD_PLAN_RECEIVING); authorize(row,peer,allowed,signal)
            val old=records[header.id]
            if(old!=null) {authorize(old,peer,allowed,signal); if(old.header!=header || old.target!=destination) throw StorageFailure(FileStatus.CONFLICT,"This batch identifier already belongs to a different plan or destination."); row=old}
            else {
                MutationRules.current(header.id)
                if(records.values.count {!it.retired}>=MAX_DETAILED_PLANS || records.values.count {!UploadBatchRules.terminal(it.phase)}>=UploadBatchRules.MAX_PLANS || records.values.count {it.peer==peer && !UploadBatchRules.terminal(it.phase)}>=UploadBatchRules.MAX_PEER_PLANS ||
                    header.totalBytes>UploadRules.QUOTA-reservedBytes(peer)-ordinaryReserved(peer) || header.totalBytes>UploadRules.QUOTA*4-reservedBytes(null)-ordinaryReserved(null) ||
                    directory.usableSpace-64L*1024*1024<header.encodedLength.toLong()*2)
                    throw StorageFailure(FileStatus.RESOURCE_EXHAUSTED,"Incoming batch slots, shared reservations or private metadata storage are full.")
                put(row)
            }
        } else {
            row=records[request.transferId]?.takeIf {it.peer==peer}?:throw StorageFailure(FileStatus.NOT_FOUND,"No retained batch receipt exists. A missing receipt cannot resume another copy.")
            authorize(row,peer,allowed,signal)
            if((row.retiring || row.retired) && request.operation !in setOf(FileOperation.UPLOAD_BATCH_STATUS,FileOperation.FINISH_UPLOAD_BATCH,FileOperation.STOP_UPLOAD_BATCH)) throw StorageFailure(FileStatus.NOT_FOUND,"The receiver owner removed this plan's detailed item history. Final parent outcomes remain; no new copy is created.")
            when(request.operation) {
                FileOperation.VERIFY_PUBLISHED_BATCH,FileOperation.CANCEL_PUBLISHED_BATCH_CHECK->result.setPublishedBatchCheck(publishedChecks.execute(row,scope,request,allowed,signal))
                FileOperation.WRITE_UPLOAD_MANIFEST->{
                    if(row.phase!=UploadBatchPlanPhase.UPLOAD_PLAN_RECEIVING) throw StorageFailure(FileStatus.CONFLICT,"This plan no longer accepts manifest chunks.")
                    val work=assemblies.getOrPut(row.header.id) {AssemblyWork(scope,UploadBatchAssembly(row.header))}
                    if(work.scope!=scope) throw StorageFailure(FileStatus.CONFLICT,"Close the earlier manifest session before resuming this intent.")
                    require(request.offset in 0..Int.MAX_VALUE.toLong()); work.value.add(request.offset.toInt(),request.content.toByteArray())
                }
                FileOperation.SEAL_UPLOAD_BATCH->if(row.phase!=UploadBatchPlanPhase.UPLOAD_PLAN_READY) {
                    val work=assemblies[row.header.id]?.takeIf {it.scope==scope}
                    if(row.phase!=UploadBatchPlanPhase.UPLOAD_PLAN_RECEIVING || work==null) throw StorageFailure(FileStatus.CONFLICT,"Upload the original complete manifest before accepting this plan.")
                    try {
                        val manifest=work.value.complete(); val grant=grants.get(row.target.grant,peer)
                        manifest.itemsList.forEach {item->signal.throwIfCanceled(); if(item.error.isEmpty() && !grants.visible(grant,item.name)) throw StorageFailure(FileStatus.ACCESS_DENIED,"A planned destination name is excluded.")}
                        authorize(row,peer,allowed,signal); row=row.copy(phase=UploadBatchPlanPhase.UPLOAD_PLAN_READY); put(row,UploadBatchRules.encode(manifest))
                    } finally {endAssembly(row.header.id)}
                }
                FileOperation.CANCEL_UPLOAD_BATCH->{if(!UploadBatchRules.terminal(row.phase)) {if(row.effectsStarted || row.phase==UploadBatchPlanPhase.UPLOAD_PLAN_STOPPING) throw StorageFailure(FileStatus.CONFLICT,"Use Stop for a started batch. Provider documents are kept."); row=row.copy(phase=UploadBatchPlanPhase.UPLOAD_PLAN_CANCELED); put(row); endAssembly(row.header.id)}}
                FileOperation.FINISH_UPLOAD_BATCH,FileOperation.STOP_UPLOAD_BATCH->row=endPlan(row,request,signal)
                FileOperation.UPLOAD_BATCH_ITEM_STATUS-> {
                    if(UploadBatchRules.terminal(row.phase)) terminalItem(row,request,result)
                    else if(request.hasBatchItemIndex() && request.batchItemIndex in 0 until row.header.itemCount && UploadBatchRules.hasManifest(row.phase) && !children.manifest(row).getItems(request.batchItemIndex).isDirectory) {
                        val reply=files().execute(row,scope,request,allowed,signal); result.setUpload(reply.upload).setBatchItem(reply.batchItem).setStatus(reply.status).setMessage(reply.message); row=records.getValue(row.header.id)
                    } else {val child=children.execute(row,request,{authorize(it,peer,allowed,signal)},signal); row=records.getValue(row.header.id); result.setBatchItem(child)}
                }
                FileOperation.CREATE_BATCH_DIRECTORY->{
                    val child=children.execute(row,request,{authorize(it,peer,allowed,signal)},signal)
                    row=records.getValue(row.header.id); result.setBatchItem(child)
                }
                FileOperation.UPLOAD_BATCH_STATUS->Unit
                FileOperation.PREPARE_BATCH_FILE,FileOperation.WRITE_BATCH_FILE,FileOperation.FINISH_BATCH_FILE,FileOperation.CANCEL_BATCH_FILE->
                    files().execute(row,scope,request,allowed,signal).let {reply->result.setUpload(reply.upload).setBatchItem(reply.batchItem).setStatus(reply.status).setMessage(reply.message); row=records.getValue(row.header.id)}
                else->throw IllegalArgumentException("Unsupported batch admission request.")
            }
        }
        result.setBatch(receipt(row,scope)).build()
        }
    }
    private fun endAssembly(id:String) {assemblies.remove(id)?.value?.close()}
    internal fun pauseForStop(peer:String,request:FileRequest) {
        if(request.operation==FileOperation.STOP_UPLOAD_BATCH && !request.hasBatchItemIndex() && request.content.isEmpty && request.offset==0L && !request.hasUpload() &&
            request.nodeId.isEmpty() && request.destinationId.isEmpty() && request.name.isEmpty())
            fileHost?.pausePlanNow(peer,request.transferId,request.mutationEpoch,request.batchHeader.takeIf {request.hasBatchHeader()})
    }
    private data class Inventory(val manifest:UploadBatchManifest,val encoded:ByteArray,val children:Map<Int,AndroidBatchDirectories.Child>)
    private fun publishedSnapshot(row:Row):PublishedBatchSnapshot {
        val before=publishedHistory(row)
        val inventory=inventory(row); val entries=mutableListOf<PublishedBatchEntry>(); val tree=row.target.tree.toUri()
        inventory.manifest.itemsList.forEachIndexed {index,item->
            val child=inventory.children[index]?:error("A completed batch child receipt is missing.")
            val parents=if(item.parent<0) row.target.parents else entries[item.parent].let {parent->
                check(parent.directory); parent.parents+SafFolderBinding(parent.document,parent.name)
            }
            SafDestination.validateBindings(parents)
            check(child.parent==(parents.lastOrNull()?.id?:android.provider.DocumentsContract.getTreeDocumentId(tree))) {"A child lost its recorded provider parent."}
            entries+=if(child.directory) {
                check(child.phase==UploadBatchItemPhase.BATCH_ITEM_SUCCEEDED)
                PublishedBatchEntry(item.parent,true,child.document,child.name,item.name,parents)
            } else {
                val scope=files().inventory(row,inventory.manifest,child,inventory.children).first
                val saved=AndroidIncomingUploads.inspectScopedPublished(scope,grants.owner.deviceId)
                check(scope.target.parents==parents)
                PublishedBatchEntry(item.parent,false,android.provider.DocumentsContract.getDocumentId(saved.second),saved.first.finalName,item.name,parents,
                    item.size,Rules.b64(item.sha256.toByteArray()))
            }
        }
        check(entries.map {it.document}.distinct().size==entries.size) {"The saved batch contains ambiguous provider document IDs."}
        check(publishedHistory(row)==before) {"The saved batch receipts changed while opening their bindings."}
        return PublishedBatchSnapshot(entries,before)
    }
    private fun publishedCurrent(row:Row,allowed:()->Boolean,signal:CancellationSignal) {
        ready(); signal.throwIfCanceled(); check(allowed()) {"The receiving session ended."}
        check(records[row.header.id]==row && row.header.epoch==epoch && !row.retiring && !row.retired) {"The batch history context changed."}
        if(grants.owner.current(row.peer)?.epoch!=row.target.pairing) throw StorageFailure(FileStatus.ACCESS_DENIED,"The original batch pairing ended.")
        val grant=grants.get(row.target.grant,row.peer); StorageRules.requirePermission(grant.permissions,StorageRules.WRITE or StorageRules.CREATE)
        check(grant.tree.toString()==row.target.tree && row.target.parents.all {grants.visible(grant,it.name)})
        check(context.contentResolver.persistedUriPermissions.any {it.uri==grant.tree && it.isReadPermission && it.isWritePermission}) {"Android destination access ended."}
    }
    private fun publishedHistory(row:Row):List<String> {
        val file=File(directory,"context.json")
        check(!File(directory,"context.json.new").exists() && !File(directory,"context.json.bak").exists()) {"Batch history needs local review."}
        val saved=JSONObject(PrivateJsonFile(file,4096).read())
        check(saved.getInt("schema")==1 && saved.getString("owner")==owner && saved.getString("epoch")==epoch)
        return listOf("context.json:"+retirementStamp(file))+retirementFiles(row).map {"${it.name}:${it.version}:${it.bytes}"}
    }
    private fun inventory(row:Row):Inventory {
        val manifest=children.manifest(row); val attempts=children.readAttempts(row)
        if(row.effectsStarted && attempts==null) throw StorageFailure(FileStatus.CONFLICT,"The batch attempt inventory needs local review.")
        val retained=linkedMapOf<Int,AndroidBatchDirectories.Child>()
        repeat(manifest.itemsCount) {index->val child=children.readChild(row,manifest,index); if(child!=null) retained[index]=child
            if(attempts!=null && children.attempted(attempts,index)!=(child!=null)) throw StorageFailure(FileStatus.CONFLICT,"A batch item lost its attempt or ownership receipt. Copies were kept.")}
        return Inventory(manifest,accepted(row),retained)
    }
    private fun outcomes(row:Row,inventory:Inventory,complete:Boolean,inspectParts:Boolean):ByteArray {
        val result=ByteArray(inventory.manifest.itemsCount); val blocked=BooleanArray(result.size)
        inventory.manifest.itemsList.forEachIndexed {index,item->
            val child=inventory.children[index]; val suppressed=item.parent>=0 && blocked[item.parent]
            if((item.error.isNotEmpty() || suppressed) && child!=null) throw StorageFailure(FileStatus.CONFLICT,"An excluded batch branch has unexpected ownership metadata.")
            val phase=when {
                item.error.isNotEmpty()->UploadBatchItemPhase.BATCH_ITEM_FAILED
                suppressed->UploadBatchItemPhase.BATCH_ITEM_SKIPPED
                child==null->UploadBatchItemPhase.BATCH_ITEM_PENDING
                child.directory->if(child.phase==UploadBatchItemPhase.BATCH_ITEM_PENDING) UploadBatchItemPhase.BATCH_ITEM_REVIEW else child.phase
                else->{val file=files().inventory(row,inventory.manifest,child,inventory.children)
                    if(inspectParts && AndroidIncomingUploads.scopedPartExists(file.first,grants.owner.deviceId)) throw StorageFailure(FileStatus.CONFLICT,"The batch still owns private file staging.")
                    when(file.second.phase) {UploadPhase.UPLOAD_COMPLETED->UploadBatchItemPhase.BATCH_ITEM_SUCCEEDED; UploadPhase.UPLOAD_SKIPPED->UploadBatchItemPhase.BATCH_ITEM_SKIPPED
                        UploadPhase.UPLOAD_CANCELED->UploadBatchItemPhase.BATCH_ITEM_FAILED; UploadPhase.UPLOAD_REVIEW->UploadBatchItemPhase.BATCH_ITEM_REVIEW; else->UploadBatchItemPhase.BATCH_ITEM_PENDING}}
            }
            if(complete && phase in setOf(UploadBatchItemPhase.BATCH_ITEM_PENDING,UploadBatchItemPhase.BATCH_ITEM_REVIEW)) throw StorageFailure(FileStatus.CONFLICT,"Every planned item needs a final receipt before completing the batch.")
            result[index]=phase.number.toByte(); blocked[index]=item.isDirectory && phase!=UploadBatchItemPhase.BATCH_ITEM_SUCCEEDED
        }
        return result
    }
    private fun endPlan(original:Row,request:FileRequest,signal:CancellationSignal):Row {
        require(!request.hasBatchItemIndex() && request.content.isEmpty && request.offset==0L && !request.hasUpload() &&
            (!request.hasBatchHeader() || request.batchHeader==original.header) && request.nodeId.isEmpty() && request.destinationId.isEmpty() && request.name.isEmpty()) {"Use the original batch identifier and history namespace to finish or stop it."}
        if(UploadBatchRules.terminal(original.phase)) {
            if(request.operation==FileOperation.FINISH_UPLOAD_BATCH && original.phase!=UploadBatchPlanPhase.UPLOAD_PLAN_COMPLETED) throw StorageFailure(FileStatus.CONFLICT,"A stopped or canceled batch cannot become completed.")
            return original
        }
        var row=original
        if(request.operation==FileOperation.STOP_UPLOAD_BATCH && !row.effectsStarted) {endAssembly(row.header.id); return row.copy(phase=UploadBatchPlanPhase.UPLOAD_PLAN_CANCELED).also {put(it)}}
        if(!UploadBatchRules.hasManifest(row.phase)) throw StorageFailure(FileStatus.CONFLICT,"Accept the original complete manifest before finishing a batch.")
        if(request.operation==FileOperation.FINISH_UPLOAD_BATCH) {
            if(row.phase!=UploadBatchPlanPhase.UPLOAD_PLAN_READY || fileHost?.busy(row.header.id)==true) throw StorageFailure(FileStatus.CONFLICT,"Stop or wait for file work before completing this batch.")
            val inventory=inventory(row)
            val ended=row.copy(phase=UploadBatchPlanPhase.UPLOAD_PLAN_COMPLETED,outcomes=Rules.b64(outcomes(row,inventory,true,true)))
            put(ended,inventory.encoded); fileHost?.releasePlanLease(row.header.id); return ended
        }
        val manifest=children.manifest(row); val encoded=accepted(row)
        // Persist the barrier before canceling work; an earlier scope cannot authorize late publication.
        if(row.phase==UploadBatchPlanPhase.UPLOAD_PLAN_READY) {row=row.copy(phase=UploadBatchPlanPhase.UPLOAD_PLAN_STOPPING); put(row,encoded)}
        val cursor=files().stopPlan(row,manifest,signal)?:return row
        row=row.copy(stopCursor=cursor); put(row,encoded); if(cursor<row.header.itemCount) return row
        val inventory=inventory(row)
        val ended=row.copy(phase=UploadBatchPlanPhase.UPLOAD_PLAN_STOPPED,outcomes=Rules.b64(outcomes(row,inventory,false,true)))
        put(ended,inventory.encoded); fileHost?.releasePlanLease(row.header.id); return ended
    }
    private fun terminalItem(row:Row,request:FileRequest,response:FileResponse.Builder) {
        require(request.content.isEmpty && request.offset==0L && !request.hasUpload() && (!request.hasBatchHeader() || request.batchHeader==row.header) &&
            request.nodeId.isEmpty() && request.destinationId.isEmpty() && request.name.isEmpty()) {"Use the retained batch item binding for status."}
        if(!UploadBatchRules.hasManifest(row.phase) || !request.hasBatchItemIndex() || request.batchItemIndex !in 0 until row.header.itemCount) throw StorageFailure(FileStatus.NOT_FOUND,"This terminal plan has no retained child receipt.")
        val inventory=inventory(row); val child=inventory.children[request.batchItemIndex]?:throw StorageFailure(FileStatus.NOT_FOUND,"This item has no destination effect receipt.")
        val result=UploadBatchItemReceipt.newBuilder().setHeader(row.header).setIndex(child.index).setId(child.id).setIsDirectory(child.directory)
        if(child.directory) result.setPhaseValue(Rules.decode(row.outcomes)[child.index].toInt()).setResultingName(if(child.phase==UploadBatchItemPhase.BATCH_ITEM_SUCCEEDED) child.name else "").setMessage(child.message)
        else {
            val file=files().inventory(row,inventory.manifest,child,inventory.children).second; response.setUpload(file)
            val phase=when(file.phase) {UploadPhase.UPLOAD_COMPLETED->UploadBatchItemPhase.BATCH_ITEM_SUCCEEDED; UploadPhase.UPLOAD_SKIPPED->UploadBatchItemPhase.BATCH_ITEM_SKIPPED
                UploadPhase.UPLOAD_CANCELED->UploadBatchItemPhase.BATCH_ITEM_FAILED; UploadPhase.UPLOAD_REVIEW->UploadBatchItemPhase.BATCH_ITEM_REVIEW; else->UploadBatchItemPhase.BATCH_ITEM_PENDING}
            result.setPhase(phase).setResultingName(if(file.phase==UploadPhase.UPLOAD_COMPLETED) file.finalName else "").setMessage(file.message)
        }
        response.setBatchItem(result)
    }
    fun endScope(scope:String)=synchronized(gate) {publishedChecks.endScope(scope); assemblies.filterValues {it.scope==scope}.keys.toList().forEach(::endAssembly); fileHost?.cancelScope(scope)}
    private fun backups()=context.filesDir.listFiles().orEmpty().filter {it.name.startsWith(BACKUP_PREFIX)}.take(33)
    fun preservedRecords()=synchronized(gate) {backups().size}
    suspend fun archiveHistory(closedOnly:Boolean=false) {
      val host=grants.authorized {synchronized(gate) {check(!maintenance && !closed); if(closedOnly) check(records.values.all {UploadBatchRules.terminal(it.phase) && !it.retiring}) {"Finish all active plans and their history cleanup before resetting closed receipts."}; maintenance=true; publishedChecks.cancelAllNow(); fileHost?.pauseAll(); fileHost}}
      try {publishedChecks.whenIdle(); host?.whenIdle(); grants.authorized {synchronized(gate) {
        check(backups().size<32) {"Remove reviewed batch-plan backups before creating another archive."}
        assemblies.keys.toList().forEach(::endAssembly)
        try {
            check(directory.canonicalFile.parentFile==context.filesDir.canonicalFile)
            if(directory.exists()) {
                check(OsConstants.S_ISDIR(Os.lstat(directory.path).st_mode)); val target=File(context.filesDir,BACKUP_PREFIX+StorageRules.token())
                check(target.canonicalFile.parentFile==context.filesDir.canonicalFile); Files.move(directory.toPath(),target.toPath())
            } else check(backups().isNotEmpty()) {"No batch history or preserved archive could be identified."}
            check(directory.mkdir()); metadataIdentity=directoryIdentity(); records.clear(); children.clear(); recovery=StateRecovery(directory); owner=grants.owner.deviceId; epoch=StorageRules.token(); saveContext(); fileHost?.resetMetadata(); changed()
        } catch(e:Exception) {recovery.block("Batch history reset was interrupted. Keep the private archives and retry local recovery."); changed(); throw e}
      }}} finally {synchronized(gate) {maintenance=false; changed()}}
    }
    suspend fun shutdown() {publishedChecks.cancelAllNow(); val host=synchronized(gate) {if(closed) return; closed=true; assemblies.keys.toList().forEach(::endAssembly); fileHost}; publishedChecks.shutdown(); host?.shutdown()}
    fun clearPreservedRecords()=grants.authorized {synchronized(gate) {
        val backups=backups(); check(backups.size<=32)
        for(backup in backups) {
            check(UploadBatchRules.canonicalToken(backup.name.removePrefix(BACKUP_PREFIX)) && backup.canonicalFile.parentFile==context.filesDir.canonicalFile && OsConstants.S_ISDIR(Os.lstat(backup.path).st_mode))
            val files=backup.listFiles().orEmpty().take(AndroidBatchDirectories.MAX_FILES*3+1); check(files.size<=AndroidBatchDirectories.MAX_FILES*3 && files.all {file->val name=file.name.removeSuffix(".new").removeSuffix(".bak");
                AndroidBatchDirectories.metadataName(name) &&
                    Os.lstat(file.path).let {OsConstants.S_ISREG(it.st_mode) && it.st_nlink==1L}}) {"Unexpected backup contents were kept."}
            files.forEach {check(it.delete())}; check(backup.delete())
        }
    }}
    private fun directoryIdentity()=Os.lstat(directory.path).let {check(OsConstants.S_ISDIR(it.st_mode)); "${it.st_dev}:${it.st_ino}"}
    private fun retirementStamp(file:File)=Os.lstat(file.path).let {s->check(OsConstants.S_ISREG(s.st_mode) && s.st_nlink==1L); "${s.st_dev}:${s.st_ino}:${s.st_size}:${s.st_mtim.tv_sec}:${s.st_mtim.tv_nsec}:${s.st_ctim.tv_sec}:${s.st_ctim.tv_nsec}"}
    private fun retirementBit(row:Row,index:Int)=Rules.decode(row.retirementBits)[index/8].toInt() and (1 shl (index%8))!=0
    private fun validateRetirementRow(row:Row) {
        val bits=Rules.decode(row.retirementBits); require(Rules.b64(bits)==row.retirementBits)
        require(!(row.retiring && row.retired) && (!(row.retiring || row.retired) || UploadBatchRules.terminal(row.phase)))
        require(if(row.retiring) bits.size==(row.header.itemCount+7)/8 && row.retirementCursor in 0..row.header.itemCount*2 && (row.header.itemCount%8==0 || (bits.last().toInt() and 255) ushr (row.header.itemCount%8)==0) else bits.isEmpty() && row.retirementCursor==0)
        require(!row.retiring || row.effectsStarted || bits.all {it==0.toByte()})
    }
    private fun retirementFiles(row:Row):List<RetirementFile> {
        check(directory.canonicalFile.parentFile==context.filesDir.canonicalFile && directoryIdentity()==metadataIdentity)
        val files=Files.newDirectoryStream(directory.toPath()).use {stream->stream.asSequence().map {it.toFile()}.filter {it.name.startsWith(row.header.id+".")}.take(UploadBatchRules.MAX_ITEMS*2+3).toList()}
        require(files.size<=UploadBatchRules.MAX_ITEMS*2+2 && files.all {AndroidBatchDirectories.metadataName(it.name) && !it.name.endsWith(".new")}) {"Unexpected receiver history objects were preserved."}
        return files.sortedBy {it.name}.map {RetirementFile(it.name,retirementStamp(it),Os.lstat(it.path).st_size)}
    }
    private fun inspectRetirement(row:Row) {
        check(UploadBatchRules.terminal(row.phase) && !row.retiring && !row.retired && !publishedChecks.busy(row.header.id) && fileHost?.busy(row.header.id)!=true) {"Finish or stop this plan and wait for verification before removing item history."}
        check(records.values.count {it.retiring || it.retired}<MAX_RETIRED_PLANS) {"Compact receipt storage is full. Once all plans finish, preserve and reset closed history."}
        if(UploadBatchRules.hasManifest(row.phase)) check(outcomes(row,inventory(row),row.phase==UploadBatchPlanPhase.UPLOAD_PLAN_COMPLETED,true).contentEquals(Rules.decode(row.outcomes))) {"Final outcomes changed. Copies and history were kept."}
        else {
            check(retirementFiles(row).all {it.name==row.header.id+".json" || it.name==row.header.id+".children.json"}) {"An unstarted plan has unexpected child metadata."}
            check(children.readAttempts(row)?.bits?.all {it==0.toByte()}!=false) {"An unstarted plan has unexpected attempt ownership."}
        }
    }
    fun reviewRetirement(id:String):PhoneIncomingBatchRetirementReview=grants.authorized {synchronized(gate) {
        ready(); val row=records.getValue(id); inspectRetirement(row); val files=retirementFiles(row); val now=android.os.SystemClock.elapsedRealtime()
        retirementReviews.entries.removeAll {it.value.expires<now}; if(retirementReviews.size>=UploadBatchRules.MAX_PLANS) retirementReviews.remove(retirementReviews.keys.first())
        val value=PhoneIncomingBatchRetirementReview(StorageRules.token(),id,row.header.itemCount,files.size,files.sumOf {it.bytes},row.phase)
        retirementReviews[value.id]=RetirementReview(value,files,now+120_000); value
    }}
    fun confirmRetirement(confirmation:PhoneIncomingBatchRetirementReview):Boolean=grants.authorized {synchronized(gate) {
        ready(); val review=retirementReviews.remove(confirmation.id)
        check(review!=null && review.value==confirmation && review.expires>=android.os.SystemClock.elapsedRealtime()) {"This receipt review changed, expired or was already used."}
        var row=records.getValue(confirmation.plan); inspectRetirement(row); check(retirementFiles(row)==review.files) {"Receiver history changed after review. It was kept."}
        val bits=children.readAttempts(row)?.bits?:ByteArray((row.header.itemCount+7)/8); val encoded=if(UploadBatchRules.hasManifest(row.phase)) accepted(row) else byteArrayOf()
        row=row.copy(retiring=true,retirementBits=Rules.b64(bits),retirementCursor=if(bits.all {it==0.toByte()}) row.header.itemCount*2 else 0)
        put(row,encoded); endAssembly(row.header.id); continueRetirementCore(row)
    }}
    private fun validateRetirementInventory(row:Row) {
        val id=row.header.id; val names=retirementFiles(row).map {it.name}.toSet()
        if(row.retired) {check(names==setOf(id+".json")) {"A compact receipt has unexpected item metadata."}; return}
        val manifest=if(UploadBatchRules.hasManifest(row.phase)) children.manifest(row) else null; val n=row.header.itemCount; val cursor=row.retirementCursor
        val expected=mutableSetOf(id+".json",id+".children.json")
        repeat(n) {index->
            val owns=retirementBit(row,index); val child="$id.$index.json"; val journal=AndroidBatchFileStore.name(id,index)
            val needChild=owns && (cursor<n || index>=cursor-n); val needFile=owns && manifest!=null && !manifest.getItems(index).isDirectory && cursor<n && index>=cursor
            val childWindow=cursor>=n && index in (cursor-n) until minOf(n,cursor-n+RETIREMENT_BATCH); val fileWindow=cursor<n && index in cursor until minOf(n,cursor+RETIREMENT_BATCH)
            if(needChild) {expected.add(child); check(childWindow || child in names) {"Future child receipts are missing outside the interrupted retirement window."}}
            if(needFile) {expected.add(journal); check(fileWindow || journal in names) {"Future file journals are missing outside the interrupted retirement window."}}
        }
        check(names.all {it in expected} && (cursor==n*2 || !row.effectsStarted || id+".children.json" in names)) {"Unexpected or missing receiver ownership metadata was kept."}
        if(id+".children.json" in names) check(children.readAttempts(row)?.bits?.contentEquals(Rules.decode(row.retirementBits))==true) {"The retirement attempt inventory changed."}
    }
    fun continueRetirement(id:String):Boolean=grants.authorized {synchronized(gate) {
        ready(); val row=records.getValue(id); check(row.retiring) {"This plan has no saved item-history retirement intent."}; continueRetirementCore(row)
    }}
    private fun deleteRetirementFile(file:File) {
        val version=retirementStamp(file)
        Os.open(file.path,OsConstants.O_RDONLY or OsConstants.O_NOFOLLOW,0).useOsDescriptor {fd->
            val named=Os.lstat(file.path); val held=Os.fstat(fd)
            check(retirementStamp(file)==version && named.st_dev==held.st_dev && named.st_ino==held.st_ino && held.st_nlink==1L); check(file.delete())}
    }
    private fun continueRetirementCore(original:Row):Boolean {
        var row=original; validateRetirementInventory(row); val id=row.header.id; val n=row.header.itemCount; val cursor=row.retirementCursor
        val manifest=if(UploadBatchRules.hasManifest(row.phase)) children.manifest(row) else null; val encoded=if(UploadBatchRules.hasManifest(row.phase)) accepted(row) else byteArrayOf()
        val end=if(cursor<n) minOf(n,cursor+RETIREMENT_BATCH) else minOf(n*2,cursor+RETIREMENT_BATCH); val bindings=linkedMapOf<Int,AndroidBatchDirectories.Child>()
        fun bind(index:Int):AndroidBatchDirectories.Child=bindings.getOrPut(index) {requireNotNull(children.readChild(row,requireNotNull(manifest),index)) {"A retained child binding is missing."}}
        for(step in cursor until end) {
            val index=if(step<n) step else step-n; if(!retirementBit(row,index)) continue
            if(step<n) {
                if(requireNotNull(manifest).getItems(index).isDirectory || !File(directory,AndroidBatchFileStore.name(id,index)).exists()) continue
                val child=bind(index); var parent=manifest.getItems(index).parent; while(parent>=0) {bind(parent); parent=manifest.getItems(parent).parent}
                val file=files().inventory(row,manifest,child,bindings)
                check(!AndroidIncomingUploads.scopedPartExists(file.first,grants.owner.deviceId)) {"Private staging remains. Its ownership journal was kept."}; files().removeRetiredJournal(id,index)
            } else {
                val file=File(directory,"$id.$index.json"); if(!file.exists()) continue; bind(index); deleteRetirementFile(file)
            }
        }
        row=row.copy(retirementCursor=end); put(row,encoded); if(end<n*2) return false
        val attempts=File(directory,id+".children.json"); if(attempts.exists()) {children.readAttempts(row); deleteRetirementFile(attempts)}
        check(retirementFiles(row).all {it.name==id+".json"}) {"Unexpected receiver history objects were kept; retirement is paused."}
        put(row.copy(retiring=false,retired=true,retirementCursor=0,retirementBits="")); children.forget(id); fileHost?.releasePlanLease(id); return true
    }
    companion object {const val MAX_DETAILED_PLANS=40; const val MAX_RETIRED_PLANS=256; private const val RETIREMENT_BATCH=32; private const val RECORD_LIMIT=8*1024*1024; private const val BACKUP_PREFIX="batch-plan-backup-"}
}
