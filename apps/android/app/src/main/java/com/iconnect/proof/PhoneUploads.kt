package com.iconnect.proof

import android.content.Context
import android.system.Os
import android.system.OsConstants
import com.google.protobuf.ByteString
import com.iconnect.protocol.*
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.FileInputStream
import java.io.FileOutputStream
import java.security.MessageDigest

enum class PhoneUploadPhase { STAGING, READY, SENDING, PAUSED, VERIFYING, COMPLETED, REVIEW, FAILED }
data class PhoneUpload(val id:String,val owner:String,val peer:String,val pairing:String,val name:String,val size:Long,
    val identity:String="",val hash:String="",val offset:Long=0,val phase:PhoneUploadPhase=PhoneUploadPhase.STAGING,
    val remote:String="",val remoteEpoch:String="",val destination:String="",val finalName:String="",val message:String="Preparing a private copy.",val active:Boolean=false)
internal data class LocalUploadSource(val entry:FileEntry,val allowed:()->Boolean,val request:suspend (FileRequest)->FileResponse)
internal data class PhoneUploadPayloadProgress(val id:String,val run:String,val receiver:String,val epoch:String,val size:Long,val offset:Long,val at:Long)

/** Explicit sends use a private immutable snapshot. Local shortcuts never become remote storage grants. */
class PhoneUploads internal constructor(private val context:Context,private val ownerId:String,resources:PhoneOutgoingResources=PhoneOutgoingResources.isolated(PhoneOutgoingResources.SINGLE),
    private val elapsed:()->Long=android.os.SystemClock::elapsedRealtime) {
    internal var activity:(PhoneActivityEvent)->Unit={}
    private val gate=Any()
    private val scope=CoroutineScope(SupervisorJob()+Dispatchers.IO)
    private val directory=File(context.filesDir,"outgoing-upload-copies")
    private val journal=PrivateJsonFile(File(context.filesDir,"outgoing-uploads.json"),2*1024*1024)
    private val recovery=StateRecovery(context.filesDir)
    private val inventory=resources.open(PhoneOutgoingResources.SINGLE,context.filesDir.canonicalPath)
    private val records=linkedMapOf<String,PhoneUpload>()
    private val work=mutableMapOf<String,Job>()
    private var closed=false
    private val mutable=MutableStateFlow<List<PhoneUpload>>(emptyList())
    val state=mutable.asStateFlow()
    private val mutablePayload=MutableStateFlow<Map<String,PhoneUploadPayloadProgress>>(emptyMap())
    internal val payloadProgress=mutablePayload.asStateFlow()
    private fun publishPayload(row:PhoneUpload,run:String,active:Boolean)=synchronized(gate) {
        mutablePayload.value=if(active && row.offset<row.size && !closed)
            mutablePayload.value+(row.id to PhoneUploadPayloadProgress(row.id,run,row.remote,row.remoteEpoch,row.size,row.offset,elapsed()))
            else mutablePayload.value-row.id
    }
    init {
        runCatching {
            if(!journal.exists()) {check(!File(context.filesDir,"outgoing-uploads.json.new").exists()); return@runCatching}
            val saved=JSONArray(journal.read()); require(saved.length()<=8)
            repeat(saved.length()) {index->
                val x=saved.getJSONObject(index)
                val row=PhoneUpload(x.getString("id"),x.getString("owner"),x.getString("peer"),x.getString("pairing"),x.getString("name"),x.getLong("size"),
                    x.getString("identity"),x.getString("hash"),x.getLong("offset"),PhoneUploadPhase.valueOf(x.getString("phase")),x.getString("remote"),
                    x.getString("remoteEpoch"),x.getString("destination"),x.getString("finalName"),x.getString("message"))
                validate(row); check(row.id !in records)
                records[row.id]=when(row.phase) {
                    PhoneUploadPhase.STAGING->row.copy(phase=PhoneUploadPhase.FAILED,message="Private preparation was interrupted. Keep or remove its partial, then select the source again.")
                    PhoneUploadPhase.SENDING,PhoneUploadPhase.VERIFYING->row.copy(phase=PhoneUploadPhase.PAUSED,message="Sending stopped. Reconnect to the same pairing and explicitly resume or check its receipt.")
                    else->row
                }
            }
        }.onFailure {records.clear(); recovery.add(File(context.filesDir,"outgoing-uploads.json"),"Outgoing-copy metadata needs review. Original files and private snapshots are kept.")}
        syncInventory(); emit()
    }
    private fun reservations(values:Collection<PhoneUpload>)=values.map {PhoneOutgoingResources.Reservation(it.id,it.peer,it.size,if(it.phase==PhoneUploadPhase.STAGING) it.size else 0)}
    private fun syncInventory() {
        try {inventory.recover(reservations(records.values),recovery.needed() || privateFiles().any {it.name.removeSuffix(".bin") !in records})}
        catch(e:Exception) {recovery.block("Private outgoing inventory needs review. Existing copies were kept."); inventory.recover(reservations(records.values),true)}
    }
    private fun validate(row:PhoneUpload) {
        StorageRules.name(row.name)
        require(UploadBatchRules.canonicalToken(row.id) && listOf(row.owner,row.peer).all {it.matches(Regex("[a-f0-9]{64}"))} && Rules.tokenPattern.matches(row.pairing) &&
            row.size in 0..UploadRules.QUOTA && row.offset in 0..row.size && row.identity.length<=128 && row.hash.length in listOf(0,43) &&
            (row.hash.isEmpty() || Rules.decode(row.hash).size==32) && (row.remote.isEmpty() && row.remoteEpoch.isEmpty() || Rules.tokenPattern.matches(row.remote) && Rules.tokenPattern.matches(row.remoteEpoch)) &&
            row.destination.length<=255 && row.finalName.length<=255 && row.message.length<=512 && row.message.none(Character::isISOControl))
        if(row.phase !in setOf(PhoneUploadPhase.STAGING,PhoneUploadPhase.FAILED)) require(row.identity.isNotEmpty() && row.hash.isNotEmpty())
        if(row.remote.isNotEmpty()) require(MutationRules.created(row.remote) in 0..StorageRules.MAX_TIMESTAMP)
        if(row.finalName.isNotEmpty()) StorageRules.name(row.finalName)
    }
    private fun emit() {mutable.value=records.values.toList().asReversed().map {it.copy(active=it.id in work)}}
    private fun save(updated:Map<String,PhoneUpload>) {
        updated.values.forEach(::validate)
        inventory.replace(reservations(updated.values)) {directory.usableSpace}
        val rows=JSONArray(); updated.values.forEach {x->validate(x); rows.put(JSONObject().put("id",x.id).put("owner",x.owner).put("peer",x.peer).put("pairing",x.pairing)
            .put("name",x.name).put("size",x.size).put("identity",x.identity).put("hash",x.hash).put("offset",x.offset).put("phase",x.phase.name)
            .put("remote",x.remote).put("remoteEpoch",x.remoteEpoch).put("destination",x.destination).put("finalName",x.finalName).put("message",x.message))}
        try {journal.write(rows.toString()); records.clear(); records.putAll(updated); emit()}
        catch(e:Exception) {inventory.uncertain(); recovery.block("Outgoing-copy progress could not be saved. Copies were preserved; review this queue before sending."); throw e}
    }
    private fun put(row:PhoneUpload):PhoneUpload=synchronized(gate) {val previous=records[row.id]?.phase; save(records+(row.id to row))
        if(previous!=row.phase) {
            val outcome=when(row.phase) {PhoneUploadPhase.COMPLETED->PhoneActivityOutcome.COMPLETED; PhoneUploadPhase.READY->PhoneActivityOutcome.ACCEPTED;
                PhoneUploadPhase.FAILED->PhoneActivityOutcome.FAILED; PhoneUploadPhase.REVIEW->PhoneActivityOutcome.REVIEW; PhoneUploadPhase.PAUSED->PhoneActivityOutcome.PAUSED; else->null}
            if(outcome!=null) recordPhoneActivity(activity,PhoneActivityEvent(PhoneActivityKind.PRIVATE_COPY,outcome,row.peer,if(outcome==PhoneActivityOutcome.COMPLETED) row.size else -1))
        }; row}
    private fun source(row:PhoneUpload)=File(directory,row.id+".bin")
    private fun identity(stat:android.system.StructStat)="${stat.st_dev}:${stat.st_ino}"
    private fun requireSnapshot(row:PhoneUpload,stat:android.system.StructStat) {
        check(identity(stat)==row.identity && OsConstants.S_ISREG(stat.st_mode) && stat.st_nlink==1L && stat.st_size==row.size) {"The private source copy changed. Preserve it and prepare a new copy."}
    }
    private fun ensureDirectory() {
        check(directory.mkdirs() || directory.isDirectory)
        check(directory.canonicalFile.parentFile==context.filesDir.canonicalFile && OsConstants.S_ISDIR(Os.lstat(directory.path).st_mode)) {"Private upload storage changed."}
    }
    private fun privateFiles():List<File> {
        if(!directory.exists()) return emptyList()
        ensureDirectory(); val files=directory.listFiles().orEmpty().take(10)
        check(files.size<=8 && files.all {it.name.matches(Regex("[A-Za-z0-9_-]{43}\\.bin")) && Os.lstat(it.path).let {x->OsConstants.S_ISREG(x.st_mode) && x.st_nlink==1L && x.st_size in 0..UploadRules.QUOTA}}) {"Private upload copies need local review; unexpected entries were preserved."}
        return files
    }
    internal fun stage(approval:PeerApproval,selected:LocalUploadSource,approved:()->Boolean):String=synchronized(gate) {
        check(!closed); recovery.requireHealthy(); check(work.size<2 && records.size<8); StorageRules.name(selected.entry.name)
        check(!selected.entry.isDirectory && selected.entry.size in 0..UploadRules.QUOTA && approved() && selected.allowed())
        ensureDirectory(); val files=privateFiles()
        val orphanBytes=files.filter {it.name.removeSuffix(".bin") !in records}.sumOf {Os.lstat(it.path).st_size}
        if(files.any {it.name.removeSuffix(".bin") !in records}) {inventory.uncertain(); error("Review abandoned private file copies before preparing another file or folder.")}
        check(files.size<8 && selected.entry.size<=UploadRules.QUOTA-records.values.sumOf {it.size}-orphanBytes) {"Remove a private upload copy before preparing another."}
        check(selected.entry.size<=(directory.usableSpace-64L*1024*1024-inventory.otherPending()).coerceAtLeast(0)) {"Not enough private storage to prepare this copy."}
        val worker=inventory.tryWorker()?:error("Two outgoing preparations or sends are already active. Retry after one stops.")
        val row=PhoneUpload(StorageRules.token(),ownerId,approval.id,approval.epoch,selected.entry.name,selected.entry.size)
        try {put(row); launch(row.id,worker) {
            var current=row
            try {
                check(approved() && selected.allowed()); Os.open(source(row).path,OsConstants.O_WRONLY or OsConstants.O_CREAT or OsConstants.O_EXCL or OsConstants.O_NOFOLLOW,384).useOsDescriptor {descriptor->
                FileOutputStream(descriptor).use {output->
                    current=put(current.copy(identity=identity(Os.fstat(descriptor))))
                    val digest=MessageDigest.getInstance("SHA-256"); var offset=0L
                    while(offset<row.size) {
                        currentCoroutineContext().ensureActive(); check(approved() && selected.allowed()) {"Local preparation ended. Select the source again."}
                        val count=minOf(StorageRules.RANGE.toLong(),row.size-offset).toInt()
                        val response=PhoneTransferManifests.checked(selected.request,FileRequest.newBuilder().setOperation(FileOperation.READ_RANGE).setNodeId(selected.entry.id)
                            .setExpectedVersion(selected.entry.version).setOffset(offset).setLength(count).build())
                        check(response.content.size()==count && response.endOfFile==(offset+count==row.size)) {"The local source returned an invalid range."}
                        check(approved() && selected.allowed()); val bytes=response.content.toByteArray(); output.write(bytes); digest.update(bytes); offset+=count
                        if(offset%(1024*1024)==0L) synchronized(gate) {records[row.id]=current.copy(offset=offset); emit()}
                    }
                    val final=PhoneTransferManifests.checked(selected.request,FileRequest.newBuilder().setOperation(FileOperation.GET_METADATA).setNodeId(selected.entry.id).build()).entriesList.single()
                    check(final.version==selected.entry.version && final.size==row.size && final.name==row.name && !final.isDirectory && approved() && selected.allowed()) {"The source changed during private preparation."}
                    output.flush(); output.fd.sync()
                    current=put(current.copy(hash=Rules.b64(digest.digest()),offset=0,phase=PhoneUploadPhase.READY,message="Private copy ready. Open an approved computer folder and choose Send."))
                }
                }
            } catch(e:Exception) {
                runCatching {put(current.copy(offset=0,phase=PhoneUploadPhase.FAILED,message=clean(e.message?:"Private preparation stopped. Remove its partial and select again.")))}
            }
        }} catch(e:Exception) {worker.close(); throw e}; row.id
    }
    private fun launch(id:String,reservedWorker:AutoCloseable?=null,action:suspend ()->Unit):Job {
        check(id !in work)
        val worker=reservedWorker?:inventory.tryWorker()?:error("Two outgoing preparations or sends are already active. Retry after one stops.")
        val job=scope.launch(start=CoroutineStart.LAZY) {try {action()} finally {synchronized(gate) {worker.close(); work.remove(id); emit()}}}
        work[id]=job
        job.invokeOnCompletion {worker.close(); synchronized(gate) {if(work[id]===job) {work.remove(id); emit()}}}
        emit(); job.start()
        return job
    }
    internal fun send(id:String,approval:PeerApproval,destination:FileEntry?,allowed:()->Boolean,request:suspend (FileRequest)->FileResponse)=synchronized(gate) {
        check(!closed); recovery.requireHealthy(); val row=records.getValue(id)
        check(row.owner==ownerId && row.peer==approval.id && row.pairing==approval.epoch) {"This private copy belongs to an earlier pairing. Prepare a new source copy for the current computer."}
        check(allowed() && row.hash.isNotEmpty() && row.phase!=PhoneUploadPhase.COMPLETED && work.size<2)
        if(row.remote.isEmpty()) check(destination!=null && destination.isDirectory && destination.permissions and 12==12) {"Open a computer folder with Write and Create approval."}
        launch(id) {transfer(row,requireNotNull(approval),destination,allowed,request)}
        Unit
    }
    internal suspend fun verifyPublished(id:String,approval:PeerApproval,allowed:()->Boolean,request:suspend (FileRequest)->FileResponse) {
        val result=CompletableDeferred<Unit>()
        val job=synchronized(gate) {
            check(!closed); recovery.requireHealthy()
            val row=records.getValue(id)
            check(row.owner==ownerId && row.peer==approval.id && row.pairing==approval.epoch && allowed()) {
                "Connect to the original paired computer to check this copy."
            }
            check(row.phase==PhoneUploadPhase.COMPLETED && row.remote.isNotEmpty() && row.finalName.isNotEmpty() && work.size<2) {
                "Choose a completed copy with a retained receiver receipt."
            }
            launch(id) {
                try {requirePublishedProof(row,allowed,request); result.complete(Unit)}
                catch(e:Exception) {result.completeExceptionally(e)}
            }
        }
        job.invokeOnCompletion {if(it!=null) result.completeExceptionally(it)}
        try {result.await()}
        catch(e:CancellationException) {job.cancel(); throw e}
    }
    private suspend fun requirePublishedProof(row:PhoneUpload,allowed:()->Boolean,request:suspend (FileRequest)->FileResponse) {
        val challenge=StorageRules.token()
        val hash=Rules.decode(row.hash)
        val started=android.os.SystemClock.elapsedRealtime()
        val value=FileRequest.newBuilder().setOperation(FileOperation.VERIFY_PUBLISHED_UPLOAD)
            .setTransferId(row.remote).setMutationEpoch(row.remoteEpoch).setMutationId(challenge).build()
        while(android.os.SystemClock.elapsedRealtime()-started<10*60_000L) {
            currentCoroutineContext().ensureActive()
            check(allowed()) {"The authenticated receiving session changed."}
            val reply=PhoneTransferManifests.checked(request,value)
            currentCoroutineContext().ensureActive()
            check(allowed()) {"The authenticated receiving session changed."}
            val upload=reply.upload
            check(reply.mutationEpoch==row.remoteEpoch && reply.hasPublishedUploadCheck() &&
                reply.publishedUploadCheck.challenge==challenge && reply.hasUpload() && upload.id==row.remote &&
                upload.name==row.name && upload.finalName==row.finalName && upload.size==row.size &&
                upload.collision==UploadCollision.UPLOAD_KEEP_BOTH && MessageDigest.isEqual(upload.sha256.toByteArray(),hash)) {
                "The computer did not return a matching fresh check for this copy."
            }
            if(reply.publishedUploadCheck.verified) return
            delay(300)
        }
        error("The computer did not finish its published-copy check before the time limit.")
    }
    private suspend fun transfer(initial:PhoneUpload,approval:PeerApproval,destination:FileEntry?,allowed:()->Boolean,request:suspend (FileRequest)->FileResponse) {
        var row=initial; val payloadRun=StorageRules.token()
        try {
            row=put(row.copy(phase=PhoneUploadPhase.SENDING,message="Checking the retained private source copy."))
            check(row.peer==approval.id && row.pairing==approval.epoch && allowed()); ensureDirectory()
            Os.open(source(row).path,OsConstants.O_RDONLY or OsConstants.O_NOFOLLOW,0).useOsDescriptor {fd->
            FileInputStream(fd).use {input->
                requireSnapshot(row,Os.fstat(fd)); val digest=MessageDigest.getInstance("SHA-256"); val bytes=ByteArray(StorageRules.RANGE)
                while(true) {currentCoroutineContext().ensureActive(); check(allowed()); val n=input.read(bytes); if(n<0) break; digest.update(bytes,0,n)}
                check(MessageDigest.isEqual(digest.digest(),Rules.decode(row.hash))) {"The private source checksum changed."}
                suspend fun call(value:FileRequest):FileResponse {currentCoroutineContext().ensureActive(); check(allowed()); val reply=PhoneTransferManifests.checked(request,value); check(allowed()); return reply}
                fun checked(info:UploadInfo):UploadInfo {UploadRules.validate(info); check(info.id==row.remote && info.name==row.name && info.size==row.size && info.sha256.toByteArray().contentEquals(Rules.decode(row.hash)) && info.collision==UploadCollision.UPLOAD_KEEP_BOTH); return info}
                var info:UploadInfo
                if(row.remote.isEmpty()) {
                    val context=call(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_CONTEXT).build()); require(Rules.tokenPattern.matches(context.mutationEpoch))
                    row=put(row.copy(remote=MutationRules.newId(),remoteEpoch=context.mutationEpoch,destination=requireNotNull(destination).name,phase=PhoneUploadPhase.SENDING,message="Requesting a new incoming copy."))
                    val descriptor=UploadInfo.newBuilder().setId(row.remote).setName(row.name).setSize(row.size).setSha256(ByteString.copyFrom(Rules.decode(row.hash))).setCollision(UploadCollision.UPLOAD_KEEP_BOTH).build()
                    info=checked(call(FileRequest.newBuilder().setOperation(FileOperation.PREPARE_UPLOAD).setDestinationId(destination.id).setTransferId(row.remote).setMutationEpoch(row.remoteEpoch).setUpload(descriptor).build()).upload)
                } else info=checked(call(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_STATUS).setTransferId(row.remote).setMutationEpoch(row.remoteEpoch).build()).upload)
                row=put(row.copy(offset=info.offset,phase=PhoneUploadPhase.SENDING,message="Sending the retained private copy.")); Os.lseek(fd,info.offset,OsConstants.SEEK_SET)
                var savedOffset=row.offset; var savedAt=elapsed()
                // The checked receipt is a baseline, not newly acknowledged progress.
                publishPayload(row,payloadRun,info.phase==UploadPhase.UPLOAD_RECEIVING)
                while(info.phase==UploadPhase.UPLOAD_RECEIVING && info.offset<row.size) {
                    currentCoroutineContext().ensureActive(); check(allowed()); val count=minOf(bytes.size.toLong(),row.size-info.offset).toInt(); var filled=0
                    while(filled<count) {val n=input.read(bytes,filled,count-filled); check(n>0); filled+=n}
                    val expected=info.offset+count
                    info=checked(call(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_WRITE).setTransferId(row.remote).setMutationEpoch(row.remoteEpoch).setOffset(info.offset).setContent(ByteString.copyFrom(bytes,0,count)).build()).upload)
                    check(info.offset==expected) {"The receiver changed its acknowledged range. Review its receipt before continuing."}
                    // The receiver has already retained this range durably. Keep live
                    // acknowledgements separate from our restart hint; every resume
                    // rechecks the original receipt before choosing a source offset.
                    row=row.copy(offset=info.offset)
                    val now=elapsed()
                    if(row.offset==row.size || row.offset-savedOffset>=CHECKPOINT_BYTES || now<savedAt || now-savedAt>=CHECKPOINT_MILLIS) {
                        row=put(row); savedOffset=row.offset; savedAt=now
                    }
                    publishPayload(row,payloadRun,info.phase==UploadPhase.UPLOAD_RECEIVING)
                }
                if(info.phase==UploadPhase.UPLOAD_RECEIVING || info.phase==UploadPhase.UPLOAD_FINALIZING || info.phase==UploadPhase.UPLOAD_VERIFYING || info.phase==UploadPhase.UPLOAD_REVIEW && info.finalName.isNotEmpty())
                    info=checked(call(FileRequest.newBuilder().setOperation(FileOperation.FINISH_UPLOAD).setTransferId(row.remote).setMutationEpoch(row.remoteEpoch).build()).upload)
                while(info.phase in setOf(UploadPhase.UPLOAD_VERIFYING,UploadPhase.UPLOAD_FINALIZING)) {
                    row=put(row.copy(phase=PhoneUploadPhase.VERIFYING,message="Computer is verifying and publishing; the original and private copy are kept.")); delay(300)
                    info=checked(call(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_STATUS).setTransferId(row.remote).setMutationEpoch(row.remoteEpoch).build()).upload)
                }
                check(info.phase==UploadPhase.UPLOAD_COMPLETED) {info.message.ifEmpty {"The receiver did not confirm completion. Review its receipt."}}
                put(row.copy(offset=row.size,phase=PhoneUploadPhase.COMPLETED,finalName=info.finalName,message="Computer confirmed verified publication. Original and private copies are kept."))
            }
            }
        } catch(e:Exception) {
            runCatching {put(row.copy(phase=if(e is CancellationException) PhoneUploadPhase.PAUSED else PhoneUploadPhase.REVIEW,
                message=clean(e.message?:"Sending paused. Reconnect to the same pairing and check the existing receiver receipt.")))}
        }
        finally {publishPayload(row,payloadRun,false)}
    }
    fun pause(id:String)=synchronized(gate) {work[id]?.cancel()}
    fun pauseAll()=synchronized(gate) {work.values.forEach {it.cancel()}}
    fun rename(id:String,name:String)=synchronized(gate) {check(!closed); recovery.requireHealthy(); check(id !in work); val row=records.getValue(id); check(row.remote.isEmpty() && row.hash.isNotEmpty()); StorageRules.name(name); put(row.copy(name=name))}
    fun pauseSending()=synchronized(gate) {work.filterKeys {records[it]?.phase!=PhoneUploadPhase.STAGING}.values.forEach {it.cancel()}}
    fun anotherCopy(id:String)=synchronized(gate) {check(!closed); recovery.requireHealthy(); check(id !in work); val row=records.getValue(id); check(row.hash.isNotEmpty()); put(row.copy(remote="",remoteEpoch="",destination="",finalName="",offset=0,phase=PhoneUploadPhase.READY,message="Earlier receiver copies are kept. Choose a destination for a deliberately new copy."))}
    suspend fun remove(id:String) {
        val active=synchronized(gate) {check(!closed); work[id]?.also {it.cancel()}}; active?.join()
        synchronized(gate) {
            check(!closed); recovery.requireHealthy(); val row=records.getValue(id); val file=source(row)
            if(file.exists() || java.nio.file.Files.isSymbolicLink(file.toPath())) {val stat=Os.lstat(file.path); check(row.identity.isNotEmpty() && identity(stat)==row.identity && OsConstants.S_ISREG(stat.st_mode) && stat.st_nlink==1L) {"The private copy cannot be safely identified."}; check(file.delete())}
            save(records-id)
        }
    }
    fun recoveryIssues()=synchronized(gate) {recovery.all()}
    fun preservedRecords()=synchronized(gate) {recovery.archivedCount()}
    fun clearArchives()=synchronized(gate) {check(!closed); recovery.clearArchives()}
    fun archive(id:String)=synchronized(gate) {check(!closed); check(work.isEmpty()); recovery.archive(id); if(!recovery.needed()) {save(emptyMap())}; syncInventory()}
    fun orphanCount()=synchronized(gate) {runCatching {privateFiles().count {it.name.removeSuffix(".bin") !in records}}.getOrDefault(0)}
    fun removeOrphans()=synchronized(gate) {check(!closed); recovery.requireHealthy(); check(work.isEmpty()); privateFiles().filter {it.name.removeSuffix(".bin") !in records}.forEach {check(it.delete())}; syncInventory()}
    suspend fun shutdown() {
        val jobs=synchronized(gate) {closed=true; work.values.toList().also {jobs->jobs.forEach {it.cancel()}}}; jobs.joinAll()
        synchronized(gate) {scope.cancel(); inventory.close()}
    }
    private fun clean(value:String)=value.filterNot(Character::isISOControl).take(512)
    private companion object {
        const val CHECKPOINT_BYTES=1024L*1024
        const val CHECKPOINT_MILLIS=1000L
    }
}
