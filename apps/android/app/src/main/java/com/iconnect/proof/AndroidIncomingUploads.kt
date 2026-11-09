package com.iconnect.proof

import android.content.Context
import android.content.ContentResolver
import android.net.Uri
import android.os.CancellationSignal
import android.os.ParcelFileDescriptor
import android.provider.DocumentsContract as Docs
import android.system.Os
import android.system.OsConstants
import androidx.core.net.toUri
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
import java.io.RandomAccessFile
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.file.Files
import java.nio.file.LinkOption
import java.security.MessageDigest
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicReference

internal data class IncomingTarget(val grant:String,val tree:String,val pairing:String,val parents:List<SafFolderBinding>)
data class PhoneIncomingCopy(val id:String,val peer:String,val name:String,val size:Long,val offset:Long,val phase:UploadPhase,val finalName:String,val message:String,val active:Boolean,val operationExpiresAt:Long=0)
data class PhoneIncomingWindowReview(val id:String,val receipt:String,val name:String,val size:Long,val checkpoint:Long,val destination:String,
    val publishedName:String,val currentExpiresAt:Long,val proposedExpiresAt:Long,val reviewExpiresAt:Long,val peer:String,val peerName:String)

/** Incoming bytes stay private until verified. Provider publication is separately journaled and never rewritten on recovery. */
class AndroidIncomingUploads internal constructor(private val context:Context,private val grants:AndroidStorageGrants,private val ownerId:String=LocalIdentity().id,private val scoped:AndroidScopedIncoming?=null,
    private val windowNow:()->Long={System.currentTimeMillis()},private val windowElapsed:()->Long={android.os.SystemClock.elapsedRealtime()}) {
    private data class Row(val owner:String,val peer:String,val target:IncomingTarget,val id:String,val name:String,val size:Long,val hash:String,val collision:UploadCollision,
        val temporary:String,val identity:String="",val offset:Long=0,val chain:String="00".repeat(32),val phase:UploadPhase=UploadPhase.UPLOAD_PENDING,
        val finalName:String="",val published:String="",val message:String="Preparing incoming copy.",val operationApprovedAt:Long=0)
    private data class Work(val scope:String,val job:Job,val signal:CancellationSignal,val permit:AutoCloseable,
        val finalization:PhoneIncomingFinalizationTracker?=null)
    private class PublishedCheck(val row:Row,val scope:String,val challenge:String) {
        var finishedAt:Long?=null
        var verified=false
    }
    private data class WindowProof(val row:Row,val authority:String,val epoch:String,val generation:Long,val wall:Long,val elapsed:Long,val current:()->Boolean)
    private data class WindowOffer(val value:PhoneIncomingWindowReview,val proof:WindowProof,val version:String)
    private data class WindowWork(val receipt:String,val job:Job,val signal:CancellationSignal)
    private val gate=grants.owner.gate
    private val directory=scoped?.store?.directory?:File(context.filesDir,"incoming-uploads")
    private val journalPath=scoped?.journalFile?:File(directory,"journal.json")
    private val journal=PrivateJsonFile(journalPath,if(scoped==null) 2*1024*1024 else AndroidBatchFileStore.RECORD_LIMIT)
    private val recovery=StateRecovery(journalPath.parentFile!!)
    private val access=AndroidSafAccess(context)
    private val records=linkedMapOf<String,Row>()
    private data class IncomingPayloadPoint(val scope: String, val offset: Long)
    private val payloadGate = Any()
    private val payloadPoints = mutableMapOf<String, IncomingPayloadPoint>()
    private val incomingRates = PhonePayloadRateTracker()
    private val work=ConcurrentHashMap<String,Work>()
    private val publishedChecks=mutableMapOf<String,PublishedCheck>()
    private val checked=mutableSetOf<String>()
    private val windowReviews=linkedMapOf<String,WindowOffer>()
    private var windowGeneration=0L
    @Volatile private var windowWork:WindowWork?=null
    private val lifetime=CoroutineScope(SupervisorJob()+Dispatchers.IO)
    private val mutable=MutableStateFlow<List<PhoneIncomingCopy>>(emptyList())
    private val closed=java.util.concurrent.atomic.AtomicBoolean(false)
    val state=mutable.asStateFlow()
    private val mutablePartChanges=MutableStateFlow(0L)
    val partChanges=mutablePartChanges.asStateFlow()
    private var directoryIdentity=""
    private var journalVersion=""
    private var epoch=StorageRules.token()
    private var blocked=false
    private var maintenance=false
    private val fileLock:RandomAccessFile?
    private val lease:java.nio.channels.FileLock?
    private val orphanRecovery=if(scoped==null) AndroidReceivingPartRecovery(grants.owner,::orphanBinding,::unreferencedParts,
        {name->checkDirectory(); records.values.none {part(it).name==name}}, {}) else null
    init {
        check(directory.mkdirs() || directory.isDirectory)
        check(directory.canonicalFile.parentFile==context.filesDir.canonicalFile && OsConstants.S_ISDIR(Os.lstat(directory.path).st_mode))
        directoryIdentity=directoryId()
        val lock=File(directory,"owner.lock")
        if(scoped==null && Files.exists(lock.toPath(),LinkOption.NOFOLLOW_LINKS)) stamp(lock)
        fileLock=if(scoped==null) RandomAccessFile(lock,"rw") else null
        try {lease=fileLock?.let {check(stamp(Os.fstat(it.fd))==stamp(lock)); requireNotNull(it.channel.tryLock()) {"Incoming copies are already open."}}} catch(e:Exception) {fileLock?.close(); throw e}
        runCatching {
            if(scoped?.fresh==true) {check(!journal.exists()); return@runCatching}
            if(scoped!=null) check(journal.exists()) {"A retained batch file journal is missing."}
            if(!journal.exists()) {check(!File(directory,"journal.json.new").exists() && privateFiles().isEmpty()); save(); return@runCatching}
            val root=JSONObject(journal.read()); require(root.getInt("schema")==1 && Rules.tokenPattern.matches(root.getString("epoch")))
            validateScope(root,scoped,ownerId)
            epoch=root.getString("epoch"); val rows=root.getJSONArray("rows"); require(rows.length()<=128)
            val loaded=linkedMapOf<String,Row>()
            repeat(rows.length()) {index->val row=decode(rows.getJSONObject(index)); require(row.id !in loaded); loaded[row.id]=row}
            records.putAll(loaded.mapValues {(_,row)->if(row.phase==UploadPhase.UPLOAD_VERIFYING) row.copy(phase=UploadPhase.UPLOAD_RECEIVING,message="Private verification was interrupted. Finish explicitly.") else row})
            journalVersion=stamp(journalPath)
            reconcileAccess()
        }.onFailure {records.clear(); blocked=true; recovery.add(journalPath,"Incoming-copy metadata needs review. Private and provider copies are kept; incoming writes are blocked.")}
        if(scoped==null) synchronized(gate) {grants.ordinaryIncoming=this}
        emit()
    }
    private fun leaseId(row:Row)="incoming:${row.id}"
    private fun reconcileAccess() {if(scoped==null) access.reconcile("incoming:",records.values.associate {leaseId(it) to Uri.parse(it.target.tree)})}
    private fun part(row:Row)=File(directory,row.temporary+".part")
    private fun identity(stat:android.system.StructStat)="${stat.st_dev}:${stat.st_ino}"
    private fun directoryId()=Os.lstat(directory.path).let {check(OsConstants.S_ISDIR(it.st_mode)); identity(it)}
    private fun checkDirectory() {check(directory.canonicalFile.parentFile==context.filesDir.canonicalFile && directoryId()==directoryIdentity) {"The original incoming directory changed. Files were kept."}}
    private fun stamp(stat:android.system.StructStat):String {
        check(OsConstants.S_ISREG(stat.st_mode) && stat.st_nlink==1L) {"Linked or redirected incoming metadata was kept."}
        return "${identity(stat)}:${stat.st_size}:${stat.st_mtim.tv_sec}:${stat.st_mtim.tv_nsec}:${stat.st_ctim.tv_sec}:${stat.st_ctim.tv_nsec}"
    }
    private fun stamp(file:File)=stamp(Os.lstat(file.path))
    private fun privateFiles():List<File> {
        checkDirectory()
        val files=Files.newDirectoryStream(directory.toPath()).use {stream->stream.asSequence().map {it.toFile()}.filter {it.name.endsWith(".part")}.take(129).toList()}
        check(files.size<=128 && files.all {it.name.matches(Regex("[A-Za-z0-9_-]{43}\\.part")) && Os.lstat(it.path).let {s->OsConstants.S_ISREG(s.st_mode) && s.st_nlink==1L && s.st_size in 0..UploadRules.QUOTA}}) {"Private incoming copies need review."}
        return files
    }
    private fun info(row:Row)=rowInfo(row)
    private fun emit() {mutable.value=records.values.toList().asReversed().map {PhoneIncomingCopy(it.id,it.peer,it.name,it.size,it.offset,it.phase,it.finalName,it.message,work.containsKey(it.id),if(scoped==null) windowExpiry(it) else 0)}}
    private fun encode(row:Row)=JSONObject().put("owner",row.owner).put("peer",row.peer).put("grant",row.target.grant).put("tree",row.target.tree).put("pairing",row.target.pairing)
        .put("parents",JSONArray().also {a->row.target.parents.forEach {a.put(JSONObject().put("id",it.id).put("name",it.name))}})
        .put("id",row.id).put("name",row.name).put("size",row.size).put("hash",row.hash).put("collision",row.collision.name).put("temporary",row.temporary).put("identity",row.identity)
        .put("offset",row.offset).put("chain",row.chain).put("phase",row.phase.name).put("finalName",row.finalName).put("published",row.published).put("message",row.message).put("operationApprovedAt",row.operationApprovedAt)
    private fun decode(x:JSONObject):Row=decodeRow(x,ownerId)
    private fun save() {
        val root=JSONObject().put("schema",1).put("epoch",epoch).put("scope",scoped?.id?:"").put("rows",JSONArray().also {a->records.values.forEach {a.put(encode(it))}})
        try {if(scoped==null) journal.write(root.toString()) else scoped.persist(root.toString()); journalVersion=stamp(journalPath)} catch(e:Exception) {blocked=true; recovery.block("Incoming progress could not be saved. Preserve the copies and review this queue."); throw e}
    }
    internal val isIdle get()=work.isEmpty() && windowWork==null
    internal val historyEpoch get()=synchronized(gate) {ready(); epoch}
    internal fun remainingBytes():Long=synchronized(gate) {ready(); records.values.filter {it.phase !in terminal}.sumOf {it.size-it.offset}}
    internal fun closeIdle() {synchronized(gate) {check(scoped!=null && work.isEmpty()); if(closed.compareAndSet(false,true)) lifetime.cancel()}}
    internal suspend fun whenIdle() {work.values.map {it.job}.joinAll()}
    private fun put(row:Row, writeScope: String? = null):Row {val before=records[row.id]; records[row.id]=row; try {save()} catch(e:Exception) {if(before==null) records.remove(row.id) else records[row.id]=before; clearPayloadRates(); throw e}
        updatePayload(row, before, writeScope)
        emit()
        if(before?.phase!=row.phase) when(row.phase) {
            UploadPhase.UPLOAD_COMPLETED->grants.recordActivity(PhoneActivityEvent(if(scoped==null) PhoneActivityKind.INCOMING_COPY else PhoneActivityKind.BATCH,PhoneActivityOutcome.COMPLETED,row.peer,row.size))
            UploadPhase.UPLOAD_REVIEW->grants.recordActivity(PhoneActivityEvent(if(scoped==null) PhoneActivityKind.INCOMING_COPY else PhoneActivityKind.BATCH,PhoneActivityOutcome.REVIEW,row.peer))
            UploadPhase.UPLOAD_CANCELED->grants.recordActivity(PhoneActivityEvent(if(scoped==null) PhoneActivityKind.INCOMING_COPY else PhoneActivityKind.BATCH,PhoneActivityOutcome.CANCELED,row.peer))
            UploadPhase.UPLOAD_SKIPPED->grants.recordActivity(PhoneActivityEvent(if(scoped==null) PhoneActivityKind.INCOMING_COPY else PhoneActivityKind.BATCH,PhoneActivityOutcome.SKIPPED,row.peer))
            else->{}
        }; return row}

    internal fun readPayloads(limit: Long, rows: List<PhoneIncomingCopy> = state.value): Map<String, PhonePayloadRateEstimate> = synchronized(payloadGate) {
        if (payloadPoints.isEmpty()) return@synchronized emptyMap()
        val now = windowElapsed()
        val wall = windowNow()
        buildMap {
            for (row in rows) {
                val point = payloadPoints[row.id] ?: continue
                if (row.phase == UploadPhase.UPLOAD_RECEIVING && row.offset == point.offset && (scoped != null || row.operationExpiresAt >= wall))
                    put(row.id, incomingRates.read(row.id, point.scope, row.size, row.offset, true, now, limit))
            }
        }
    }

    private fun updatePayload(row: Row, previous: Row?, writeScope: String?) {
        synchronized(payloadGate) {
            if (!writeScope.isNullOrEmpty() && row.offset < row.size) {
                incomingRates.observe(row.id, writeScope, row.size, row.offset, true, windowElapsed())
                payloadPoints[row.id] = IncomingPayloadPoint(writeScope, row.offset)
            } else if ((row.offset == row.size || row.phase != UploadPhase.UPLOAD_RECEIVING || previous?.operationApprovedAt != row.operationApprovedAt) &&
                payloadPoints.remove(row.id) != null) {
                incomingRates.retain(payloadPoints.keys)
            }
        }
    }

    internal fun clearPayloadRates() = synchronized(payloadGate) {
        payloadPoints.clear()
        incomingRates.clear()
    }
    internal fun readFinalization(id: String): PhoneIncomingFinalization? = work[id]?.takeIf { it.job.isActive && !it.signal.isCanceled }?.finalization?.read()
    internal fun readFinalizations(): Map<String, PhoneIncomingFinalization> = buildMap {
        work.keys.forEach { id -> readFinalization(id)?.let { put(id, it) } }
    }
    private fun ready() {recovery.requireHealthy(); if(blocked || maintenance || closed.get()) throw StorageFailure(FileStatus.CONFLICT,"Incoming history needs local review, is closed or is being archived.")}
    internal fun reservedBytes(peer:String?):Long=synchronized(gate) {ready(); val oldest=windowNow()-MutationRules.LIFETIME
        records.values.filter {(peer==null || it.peer==peer) && it.phase !in released && (it.phase !in terminal || windowStart(it)>=oldest)}.sumOf {it.size}}
    private fun authorize(row:Row,allowed:()->Boolean) {
        ready(); if(!allowed()) throw StorageFailure(FileStatus.ACCESS_DENIED,"Phone incoming availability ended.")
        scoped?.let {it.authorize(); require(matchesScope(row,it)) {"The scoped incoming descriptor changed."}}
        if(row.owner!=ownerId || grants.owner.current(row.peer)?.epoch!=row.target.pairing) throw StorageFailure(FileStatus.ACCESS_DENIED,"This incoming copy belongs to an earlier pairing.")
        val grant=grants.get(row.target.grant,row.peer); StorageRules.requirePermission(grant.permissions,StorageRules.WRITE or StorageRules.CREATE)
        if(grant.tree.toString()!=row.target.tree || row.target.parents.any {!grants.visible(grant,it.name)} || !grants.visible(grant,row.name) || row.finalName.isNotEmpty() && !grants.visible(grant,row.finalName))
            throw StorageFailure(FileStatus.ACCESS_DENIED,"This incoming location is excluded or changed.")
        check(context.contentResolver.persistedUriPermissions.any {it.uri==grant.tree && it.isReadPermission && it.isWritePermission}) {"Android destination access ended."}
    }
    private fun requirePart(row:Row,fd:java.io.FileDescriptor) {
        val stat=Os.fstat(fd); check(identity(stat)==row.identity && OsConstants.S_ISREG(stat.st_mode) && stat.st_nlink==1L && stat.st_size>=row.offset) {"The private incoming partial changed."}
    }
    private fun chain(previous:ByteArray,offset:Long,bytes:ByteArray,count:Int):ByteArray=Rules.digest(ByteBuffer.allocate(72).order(ByteOrder.LITTLE_ENDIAN).put(previous).putLong(offset)
        .put(MessageDigest.getInstance("SHA-256").digest(bytes.copyOf(count))).array())
    private fun checkPrefix(row:Row,input:FileInputStream,signal:CancellationSignal) {
        requirePart(row,input.fd); if(row.id in checked) return
        verifyWindowPrefix(row,input,signal)
        checked.add(row.id)
    }
    private fun verifyWindowPrefix(row:Row,input:FileInputStream,signal:CancellationSignal,checkCurrent:()->Unit={}) {
        requirePart(row,input.fd)
        val bytes=ByteArray(StorageRules.RANGE); var offset=0L; var hash=ByteArray(32)
        Os.lseek(input.fd,0,OsConstants.SEEK_SET)
        while(offset<row.offset) {signal.throwIfCanceled(); checkCurrent(); val n=minOf(bytes.size.toLong(),row.offset-offset).toInt(); readExactly(input,bytes,n); hash=chain(hash,offset,bytes,n); offset+=n}
        check(MessageDigest.isEqual(hash,hex(row.chain))) {"The acknowledged incoming prefix changed."}
    }
    private fun hex(value:String)=ByteArray(value.length/2) {value.substring(it*2,it*2+2).toInt(16).toByte()}
    private fun readExactly(input:FileInputStream,bytes:ByteArray,count:Int) {var offset=0; while(offset<count) {val n=input.read(bytes,offset,count-offset); check(n>0) {"Private incoming file ended unexpectedly."}; offset+=n}}
    internal fun execute(peer:String,request:FileRequest,target:()->IncomingTarget,scope:String,allowed:()->Boolean,signal:CancellationSignal):FileResponse=synchronized(gate) {
        ready(); signal.throwIfCanceled(); if(!allowed()) throw StorageFailure(FileStatus.ACCESS_DENIED,"Phone incoming availability ended."); val result=FileResponse.newBuilder().setRequestId(request.requestId).setMutationEpoch(epoch)
        if(request.operation==FileOperation.UPLOAD_CONTEXT) return@synchronized result.build()
        if(request.mutationEpoch!=epoch) throw StorageFailure(FileStatus.STALE_REFERENCE,"Incoming history changed. Review a fresh copy context.")
        if(request.operation==FileOperation.PREPARE_UPLOAD) {
            require(request.hasUpload()); val descriptor=request.upload; UploadRules.validate(descriptor)
            if(scoped==null) MutationRules.current(descriptor.id,windowNow()) else require(descriptor==scoped.descriptor)
            require(request.transferId==descriptor.id && descriptor.offset==0L && descriptor.phase==UploadPhase.UPLOAD_PENDING && descriptor.finalName.isEmpty() && descriptor.message.isEmpty())
            val destination=target(); val row=Row(ownerId,peer,destination,descriptor.id,descriptor.name,descriptor.size,Rules.b64(descriptor.sha256.toByteArray()),descriptor.collision,StorageRules.token())
            authorize(row,allowed); SafDestination(context,Uri.parse(destination.tree),destination.parents,signal).validate()
            records[row.id]?.let {old->
                if(old.peer!=row.peer || old.target!=row.target || old.name!=row.name || old.size!=row.size || old.hash!=row.hash || old.collision!=row.collision) throw StorageFailure(FileStatus.CONFLICT,"This incoming identifier describes another copy.")
                return@synchronized result.setUpload(info(old)).build()
            }
            val oldest=windowNow()-MutationRules.LIFETIME
            val kept=if(scoped==null) records.values.filter {it.phase !in terminal || windowStart(it)>=oldest} else records.values.toList()
            val unwritten=if(scoped==null) kept.filter {it.phase !in terminal}.sumOf {it.size-it.offset}+grants.batchPlans.privateUnwrittenBytes() else
                (grants.ordinaryIncoming?.remainingBytes()?:0)+scoped.store.remainingBytes()
            if((if(scoped!=null) kept.isNotEmpty() else kept.size>=128 || kept.count {it.peer==peer}>=32 || row.size>UploadRules.QUOTA-kept.filter {it.peer==peer && it.phase !in released}.sumOf {it.size}-grants.batchPlans.reservedBytes(peer) ||
                row.size>UploadRules.QUOTA*4-kept.filter {it.phase !in released}.sumOf {it.size}-grants.batchPlans.reservedBytes(null) || privateFiles().size>=128) ||
                row.size>(directory.usableSpace-64L*1024*1024-unwritten).coerceAtLeast(0))
                throw StorageFailure(FileStatus.RESOURCE_EXHAUSTED,"Incoming reservations or private storage are full. Review existing copies.")
            scoped?.store?.canCreate()
            if(scoped==null) access.retainRecorded(leaseId(row),Uri.parse(row.target.tree)); val oldRecords=records.toMap(); records.keys.retainAll(kept.map {it.id}.toSet())
            try {put(row)} catch(e:Exception) {records.clear(); records.putAll(oldRecords); if(scoped==null) runCatching {access.release(leaseId(row))}; throw e}
            oldRecords.values.filter {it.id !in records}.forEach {runCatching {deletePrivate(it)}}
            reconcileAccess()
            try {
                authorize(row,allowed); signal.throwIfCanceled(); Os.open(part(row).path,OsConstants.O_RDWR or OsConstants.O_CREAT or OsConstants.O_EXCL or OsConstants.O_NOFOLLOW,384).useOsDescriptor {fd->
                scoped?.store?.created(row.temporary)
                FileOutputStream(fd).use {it.fd.sync(); val receiving=put(row.copy(identity=identity(Os.fstat(fd)),phase=UploadPhase.UPLOAD_RECEIVING,message="Receiving into private staging; provider files are kept.")); checked.add(row.id); return@synchronized result.setUpload(info(receiving)).build()}
                }
            } catch(e:Exception) {if(!blocked) put(row.copy(phase=UploadPhase.UPLOAD_REVIEW,message="Private creation was interrupted. Preserve the partial for review.")); throw e}
        }
        if(request.operation==FileOperation.LIST_UPLOADS) {
            require(request.offset in 0..128)
            val rows=records.values.filter {it.peer==peer}.sortedByDescending {MutationRules.created(it.id)}
            rows.drop(request.offset.toInt()).take(32).forEach {row->runCatching {authorize(row,allowed)}.onSuccess {result.addUploads(info(row))}}
            return@synchronized result.setEndOfFile(request.offset+32>=rows.size).build()
        }
        var row=records[request.transferId]?.takeIf {it.peer==peer}?:throw StorageFailure(FileStatus.NOT_FOUND,"No matching incoming receipt exists for this device.")
        authorize(row,allowed)
        if(windowWork?.receipt==row.id && request.operation in setOf(FileOperation.UPLOAD_WRITE,FileOperation.FINISH_UPLOAD))
            throw StorageFailure(FileStatus.CONFLICT,"The receiving owner is checking this retained receipt. Resume after that review finishes.")
        when(request.operation) {
            FileOperation.VERIFY_PUBLISHED_UPLOAD->return@synchronized checkPublished(row,request,scope,allowed,result)
            FileOperation.UPLOAD_STATUS->Unit
            FileOperation.CANCEL_UPLOAD->{
                if(row.phase !in terminal) {
                    if(row.finalName.isNotEmpty()) throw StorageFailure(FileStatus.CONFLICT,"A provider copy may exist. Reconcile its receipt; provider documents are kept.")
                    work[row.id]?.let {it.signal.cancel(); it.job.cancel(); return@synchronized result.setStatus(FileStatus.CONFLICT).setMessage("Verification is stopping; check status before repeating cancellation.").setUpload(info(row)).build()}
                    row=put(row.copy(phase=UploadPhase.UPLOAD_REVIEW,message="Cancellation cleanup is pending.")); deletePrivate(row)
                    row=put(row.copy(phase=UploadPhase.UPLOAD_CANCELED,message="Canceled private incoming copy. Provider documents were kept."))
                }
            }
            FileOperation.UPLOAD_WRITE->{
                if(scoped==null) requireWindowCurrent(row)
                if(row.phase!=UploadPhase.UPLOAD_RECEIVING || request.offset<0 || request.offset>=row.size || request.offset%StorageRules.RANGE!=0L || request.offset>row.offset ||
                    request.content.size()!=minOf(StorageRules.RANGE.toLong(),row.size-request.offset).toInt()) throw StorageFailure(FileStatus.CONFLICT,"Refresh the durable incoming offset before sending a bounded range.")
                Os.open(part(row).path,OsConstants.O_RDWR or OsConstants.O_NOFOLLOW,0).useOsDescriptor {fd->
                FileInputStream(fd).use {input->
                    checkPrefix(row,input,signal); val data=request.content.toByteArray()
                    if(request.offset<row.offset) {
                        require(request.offset+data.size<=row.offset); val retained=ByteArray(data.size); Os.lseek(fd,request.offset,OsConstants.SEEK_SET); readExactly(input,retained,retained.size)
                        if(!MessageDigest.isEqual(data,retained)) throw StorageFailure(FileStatus.CONFLICT,"Repeated range contains different bytes.")
                    } else {
                        authorize(row,allowed); signal.throwIfCanceled(); Os.ftruncate(fd,row.offset); Os.lseek(fd,row.offset,OsConstants.SEEK_SET)
                        var written=0; while(written<data.size) {signal.throwIfCanceled(); written+=Os.write(fd,data,written,data.size-written)}; Os.fsync(fd)
                        row=put(row.copy(offset=row.offset+data.size,chain=Rules.hex(chain(hex(row.chain),row.offset,data,data.size))), scope)
                    }
                }
                }
            }
            FileOperation.FINISH_UPLOAD->{
                if(row.phase==UploadPhase.UPLOAD_COMPLETED) return@synchronized result.setUpload(info(row)).build()
                if(scoped==null) requireWindowCurrent(row)
                if(row.offset!=row.size || row.phase !in setOf(UploadPhase.UPLOAD_RECEIVING,UploadPhase.UPLOAD_VERIFYING,UploadPhase.UPLOAD_FINALIZING,UploadPhase.UPLOAD_REVIEW) || row.phase==UploadPhase.UPLOAD_REVIEW && row.finalName.isEmpty())
                    throw StorageFailure(FileStatus.CONFLICT,"Receive all bytes before finishing; uncertain copies need review.")
                if(!work.containsKey(row.id)) {
                    if(work.size>=2) throw StorageFailure(FileStatus.RESOURCE_EXHAUSTED,"Two incoming copies are already being verified. Retry shortly.")
                    if(row.finalName.isNotEmpty() && row.published.isEmpty()) return@synchronized result.setUpload(info(row.copy(phase=UploadPhase.UPLOAD_REVIEW,message="A provider copy may have been created without a recorded ID. Inspect the destination before deliberately starting a new copy."))).build()
                    val permit=grants.reserveIncomingVerification()?:throw StorageFailure(FileStatus.RESOURCE_EXHAUSTED,"Two incoming copies are already being verified. Retry shortly.")
                    try {row=put(row.copy(phase=if(row.finalName.isEmpty()) UploadPhase.UPLOAD_VERIFYING else UploadPhase.UPLOAD_FINALIZING,message="Verifying private or recorded provider bytes."))} catch(e:Exception) {permit.close(); throw e}
                    val id=row.id; val cancellation=CancellationSignal()
                    val progress=PhoneIncomingFinalizationTracker(id,row.size,windowElapsed)
                    val job=lifetime.launch(start=CoroutineStart.LAZY) {verifyPublish(id,allowed,cancellation,progress)}
                    val active=Work(scope,job,cancellation,permit,progress); work[id]=active
                    job.invokeOnCompletion {if(work.remove(id,active)) {active.permit.close(); CoroutineScope(Dispatchers.IO).launch {synchronized(gate) {emit()}}}}
                    emit(); job.start()
                }
            }
            else->throw StorageFailure(FileStatus.UNSUPPORTED,"Unsupported incoming-copy operation.")
        }
        result.setUpload(info(row)).build()
    }
    private fun checkPublished(row:Row,request:FileRequest,scope:String,allowed:()->Boolean,result:FileResponse.Builder):FileResponse {
        if(scoped!=null) throw StorageFailure(FileStatus.UNSUPPORTED,"Published-copy checks apply to ordinary incoming copies.")
        val challenge=request.mutationId
        if(!UploadBatchRules.canonicalToken(challenge) || request.hasUpload() || request.content.size()!=0 || request.offset!=0L)
            throw StorageFailure(FileStatus.INVALID_REQUEST,"A fresh published-copy challenge is required.")
        if(row.phase!=UploadPhase.UPLOAD_COMPLETED || row.published.isEmpty())
            throw StorageFailure(FileStatus.CONFLICT,"Only a completed published copy can be checked.")
        val now=windowElapsed()
        publishedChecks.entries.removeAll {(id,check)->records[id]!=check.row || check.finishedAt?.let {now-it !in 0L..30_000L}==true}
        val previous=publishedChecks[row.id]
        if(previous?.challenge==challenge && previous.scope==scope) {
            if(previous.finishedAt!=null) {
                publishedChecks.remove(row.id)
                if(!previous.verified) throw StorageFailure(FileStatus.CONFLICT,"The published copy could not be verified. Its receipt was kept.")
                return result.setUpload(info(row)).setPublishedUploadCheck(PublishedUploadCheck.newBuilder().setChallenge(challenge).setVerified(true)).build()
            }
        } else {
            if(work.containsKey(row.id) || windowWork?.receipt==row.id)
                throw StorageFailure(FileStatus.CONFLICT,"A check for this copy is already running.")
            val permit=grants.reserveIncomingVerification()
                ?:throw StorageFailure(FileStatus.RESOURCE_EXHAUSTED,"Incoming verification is busy. Retry shortly.")
            val check=PublishedCheck(row,scope,challenge)
            val cancellation=CancellationSignal()
            val job=lifetime.launch(start=CoroutineStart.LAZY) {
                val verified=try {
                    withTimeout(10*60_000L) {
                        checkAllowed(row,allowed,cancellation)
                        val destination=SafDestination(context,Uri.parse(row.target.tree),row.target.parents,cancellation)
                        val uri=Uri.parse(row.published)
                        destination.child(uri); requireName(row,destination,uri)
                        check(hashProvider(row,uri,allowed,cancellation)==row.hash)
                        checkAllowed(row,allowed,cancellation)
                        destination.child(uri); requireName(row,destination,uri)
                    }
                    true
                } catch(_:Exception) {false}
                synchronized(gate) {
                    if(publishedChecks[row.id]===check && records[row.id]==row) {
                        check.verified=verified && runCatching {authorize(row,allowed); cancellation.throwIfCanceled()}.isSuccess
                        check.finishedAt=windowElapsed()
                    }
                }
            }
            val active=Work(scope,job,cancellation,permit)
            publishedChecks[row.id]=check
            work[row.id]=active
            job.invokeOnCompletion {
                if(work.remove(row.id,active)) {
                    active.permit.close()
                    synchronized(gate) {
                        if(publishedChecks[row.id]===check && check.finishedAt==null) check.finishedAt=windowElapsed()
                        emit()
                    }
                }
            }
            emit()
            job.start()
        }
        return result.setUpload(info(row)).setPublishedUploadCheck(PublishedUploadCheck.newBuilder().setChallenge(challenge)).build()
    }
    private suspend fun checkAllowed(row:Row,allowed:()->Boolean,signal:CancellationSignal) {currentCoroutineContext().ensureActive(); signal.throwIfCanceled(); synchronized(gate) {authorize(row,allowed)}}
    private suspend fun verifyPublish(id:String,allowed:()->Boolean,signal:CancellationSignal,progress:PhoneIncomingFinalizationTracker) {
        try {
            var row=synchronized(gate) {records.getValue(id)}; checkAllowed(row,allowed,signal)
            val tree=Uri.parse(row.target.tree)
            // Retain only this attempt's provider connection, never permission or
            // metadata. Every existing per-range and final validation still runs.
            val client=requireNotNull(context.contentResolver.acquireUnstableContentProviderClient(tree)) {"The destination provider is unavailable."}
            client.use {
            val provider=ContentResolver.wrap(it)
            val destination=SafDestination(context,tree,row.target.parents,signal,provider)
            if(row.published.isNotEmpty()) {
                val uri=Uri.parse(row.published); destination.child(uri); requireName(row,destination,uri)
                progress.begin(PhoneIncomingFinalizationStage.CHECKING_PROVIDER_BYTES)
                check(hashProvider(row,uri,allowed,signal,progress::advance,provider)==row.hash) {"The recorded provider copy is incomplete or changed. It was kept."}
                checkAllowed(row,allowed,signal); destination.child(uri); requireName(row,destination,uri)
                synchronized(gate) {authorize(row,allowed); put(row.copy(phase=UploadPhase.UPLOAD_COMPLETED,message="Recorded provider copy verified without rewriting it.")); runCatching {deletePrivate(row)}}; return
            }
            Os.open(part(row).path,OsConstants.O_RDONLY or OsConstants.O_NOFOLLOW,0).useOsDescriptor {fd->
            FileInputStream(fd).use {input->
                requirePart(row,fd); check(Os.fstat(fd).st_size==row.size); val hash=MessageDigest.getInstance("SHA-256"); val bytes=ByteArray(StorageRules.RANGE); var count=0L
                progress.begin(PhoneIncomingFinalizationStage.CHECKING_PRIVATE_BYTES)
                while(count<row.size) {
                    checkAllowed(row,allowed,signal); val length=minOf(bytes.size.toLong(),row.size-count).toInt()
                    readExactly(input,bytes,length); hash.update(bytes,0,length); count+=length; progress.advance(count)
                }
                check(MessageDigest.isEqual(hash.digest(),Rules.decode(row.hash))) {"The incoming private checksum did not match."}
                progress.begin(PhoneIncomingFinalizationStage.PREPARING_PROVIDER_DOCUMENT)
                checkAllowed(row,allowed,signal); destination.validate(); val previous=if(scoped==null) destination.children() else destination.children(8192,1024*1024)
                val grant=synchronized(gate) {grants.get(row.target.grant,row.peer)}
                val blind=grant.permissions and StorageRules.LIST==0
                if(!blind && previous.values.any {it.equals(row.name,true)}) {
                    if(row.collision==UploadCollision.UPLOAD_SKIP) {synchronized(gate) {authorize(row,allowed); put(row.copy(phase=UploadPhase.UPLOAD_SKIPPED,message="Existing destination was kept.")); runCatching {deletePrivate(row)}}; return}
                    check(row.collision!=UploadCollision.UPLOAD_FAIL) {"An existing destination was kept. Review this copy."}
                }
                val name=if(blind) uniqueName(row.name) else SafDestination.unusedName(row.name,previous.values)
                synchronized(gate) {authorize(row,allowed); row=put(row.copy(phase=UploadPhase.UPLOAD_FINALIZING,finalName=name,message="Provider publication is pending; an interrupted copy will be preserved."))}
                checkAllowed(row,allowed,signal)
                val uri=requireNotNull(Docs.createDocument(provider,destination.parent,"application/octet-stream",name))
                check(uri.scheme=="content" && uri.authority==Uri.parse(row.target.tree).authority && uri.userInfo==null && uri.query==null && uri.fragment==null && uri.toString().length<=16_384 && Docs.getDocumentId(uri) !in previous)
                destination.child(uri)
                synchronized(gate) {authorize(row,allowed); row=put(row.copy(published=uri.toString()))}
                val actual=destination.created(uri,previous); check(actual==name) {"The provider changed the requested name; its empty new document was kept for review."}
                checkAllowed(row,allowed,signal); Os.lseek(fd,0,OsConstants.SEEK_SET)
                withProvider(uri,"rw",signal,provider) {descriptor->
                    val stat=Os.fstat(descriptor.fileDescriptor)
                    if(!OsConstants.S_ISREG(stat.st_mode) || stat.st_nlink!=1L || stat.st_size!=0L) throw UnsupportedOperationException("Provider did not return a supported empty new file.")
                    Os.lseek(descriptor.fileDescriptor,0,OsConstants.SEEK_SET)
                    ParcelFileDescriptor.AutoCloseOutputStream(descriptor).use {output->
                        progress.begin(PhoneIncomingFinalizationStage.WRITING_PROVIDER_DOCUMENT)
                        var sent=0L
                        while(sent<row.size) {checkAllowed(row,allowed,signal); destination.child(uri); requireName(row,destination,uri); val n=minOf(bytes.size.toLong(),row.size-sent).toInt(); readExactly(input,bytes,n)
                            synchronized(gate) {authorize(row,allowed); signal.throwIfCanceled(); output.write(bytes,0,n)}; sent+=n; progress.advance(sent)}
                        progress.begin(PhoneIncomingFinalizationStage.SYNCING_PROVIDER_DOCUMENT)
                        output.flush(); descriptor.fileDescriptor.sync()
                    }
                }
                checkAllowed(row,allowed,signal); destination.child(uri)
                progress.begin(PhoneIncomingFinalizationStage.CHECKING_PROVIDER_BYTES)
                check(hashProvider(row,uri,allowed,signal,progress::advance,provider)==row.hash) {"The provider copy did not verify; it was kept."}
                destination.child(uri); requireName(row,destination,uri); synchronized(gate) {authorize(row,allowed); put(row.copy(phase=UploadPhase.UPLOAD_COMPLETED,message="Provider copy reread and verified; the identified private stage can be cleaned up.")); runCatching {deletePrivate(row)}}
            }
            }
            }
        } catch(e:Exception) {
            synchronized(gate) {
                val row=records.getValue(id)
                if(!blocked && row.phase in setOf(UploadPhase.UPLOAD_VERIFYING,UploadPhase.UPLOAD_FINALIZING)) runCatching {put(row.copy(
                    phase=if(e is CancellationException && row.finalName.isEmpty()) UploadPhase.UPLOAD_RECEIVING else UploadPhase.UPLOAD_REVIEW,
                    message=if(row.finalName.isEmpty()) "Private verification stopped or failed. The partial was kept; resume or review explicitly." else "Provider publication is uncertain. Its recorded copy is kept; finish only verifies, never rewrites it."))}
            }
        } finally {synchronized(gate) {work.remove(id)?.permit?.close(); emit()}}
    }
    private suspend fun hashProvider(row:Row,uri:Uri,allowed:()->Boolean,signal:CancellationSignal,progress:((Long)->Unit)?=null,
        provider:ContentResolver?=null):String {
        val hash=MessageDigest.getInstance("SHA-256"); var count=0L
        withProvider(uri,"r",signal,provider) {descriptor->ParcelFileDescriptor.AutoCloseInputStream(descriptor).use {input->
            val bytes=ByteArray(StorageRules.RANGE)
            while(true) {checkAllowed(row,allowed,signal); val n=input.read(bytes); if(n<0) break; count+=n; check(count<=row.size); hash.update(bytes,0,n); progress?.invoke(count)}
        }}
        check(count==row.size); return Rules.b64(hash.digest())
    }
    private fun requireName(row:Row,destination:SafDestination,uri:Uri) {
        val name=destination.name(uri); val grant=synchronized(gate) {grants.get(row.target.grant,row.peer)}
        check(name==row.finalName && grants.visible(grant,name)) {"The provider copy was renamed or excluded; it was kept."}
    }
    private suspend fun <T> withProvider(uri:Uri,mode:String,signal:CancellationSignal,provider:ContentResolver?=null,
        action:suspend (ParcelFileDescriptor)->T):T=coroutineScope {
        val opened=AtomicReference<ParcelFileDescriptor?>()
        val finished=java.util.concurrent.atomic.AtomicBoolean(false)
        val watcher=launch(Dispatchers.IO,start=CoroutineStart.UNDISPATCHED) {try {awaitCancellation()} finally {if(!finished.get()) {signal.cancel(); runCatching {opened.get()?.close()}}}}
        suspend fun useDescriptor(value:ParcelFileDescriptor?):T {
            val descriptor=requireNotNull(value); opened.set(descriptor); currentCoroutineContext().ensureActive()
            return descriptor.use {action(it)}
        }
        try {
            // Publication supplies its already retained, wrapped provider client.
            // Other checks open the actual document with a cancellable raw read.
            if(provider!=null) useDescriptor(provider.openFileDescriptor(uri,mode,signal))
            else requireNotNull(context.contentResolver.acquireUnstableContentProviderClient(uri)).use {client->
                useDescriptor(client.openFile(uri,mode,signal))
            }
        }
        finally {finished.set(true); runCatching {opened.getAndSet(null)?.close()}; withContext(NonCancellable) {watcher.cancelAndJoin()}}
    }
    private fun uniqueName(name:String):String {
        val dot=name.lastIndexOf('.'); val extension=if(dot>0) name.substring(dot) else ""; var stem=if(dot>0) name.substring(0,dot) else name
        val suffix=" ("+StorageRules.token().take(16)+")"; val limit=255-extension.length-suffix.length; check(limit>0) {"Choose a shorter incoming filename."}
        stem=stem.take(limit).trimEnd(' ','.'); if(stem.lastOrNull()?.isHighSurrogate()==true) stem=stem.dropLast(1)
        return (stem+suffix+extension).also(StorageRules::name)
    }
    private fun windowStart(row:Row)=row.operationApprovedAt.takeIf {it!=0L}?:MutationRules.created(row.id)
    private fun windowExpiry(row:Row)=minOf(StorageRules.MAX_TIMESTAMP,windowStart(row)+MutationRules.LIFETIME)
    private fun requireWindowCurrent(row:Row) {
        val now=windowNow(); val at=windowStart(row)
        if(at<=0 || at<now-MutationRules.LIFETIME || at>now+300_000)
            throw StorageFailure(FileStatus.STALE_REFERENCE,"This incoming copy's 24-hour write window expired or the clocks disagree. Ask the receiving owner to review this existing receipt; resume does not require another copy.")
    }
    private fun requireWindowRow(row:Row) {
        ready(); check(scoped==null && !work.containsKey(row.id) && row.identity.isNotEmpty() &&
            (row.phase==UploadPhase.UPLOAD_RECEIVING && row.finalName.isEmpty() || row.offset==row.size && row.published.isNotEmpty() && row.phase in setOf(UploadPhase.UPLOAD_REVIEW,UploadPhase.UPLOAD_FINALIZING))) {
            "Choose an idle ordinary receipt with identified private staging or a recorded provider copy. Uncertain publication without a recorded document keeps its existing review."
        }
        check(windowNow()>windowExpiry(row)) {"This receipt's write window has not expired. Resume the existing receipt explicitly."}
    }
    private fun requireWindowProof(proof:WindowProof,active:WindowWork) {
        active.job.ensureActive(); active.signal.throwIfCanceled(); checkDirectory(); ready()
        check(proof.current() && windowGeneration==proof.generation && epoch==proof.epoch && records[proof.row.id]==proof.row &&
            windowNow()-proof.wall in 0 until 300_000 && windowElapsed()-proof.elapsed in 0 until 300_000 && grants.owner.approvalStamp==proof.authority) {
            "This incoming-window review changed, expired or lost foreground owner context. Review the current receipt again."
        }
        check(stamp(journalPath)==journalVersion && listOf(".new",".bak").none {Files.exists(File(journalPath.path+it).toPath(),LinkOption.NOFOLLOW_LINKS)}) {"Incoming ownership metadata changed; preserve its copies for local review."}
        requireWindowRow(proof.row); authorize(proof.row,proof.current)
    }
    private suspend fun <T> withWindowCheck(id:String,action:suspend (WindowWork)->T):T=withContext(Dispatchers.IO) {
        coroutineScope {
            val active=WindowWork(id,currentCoroutineContext().job,CancellationSignal())
            synchronized(gate) {ready(); check(windowWork==null) {"Finish or cancel the current incoming-window check first."}; windowWork=active}
            val watcher=launch(Dispatchers.IO,start=CoroutineStart.UNDISPATCHED) {try {awaitCancellation()} finally {active.signal.cancel()}}
            try {action(active)} finally {
                synchronized(gate) {if(windowWork===active) windowWork=null}
                withContext(NonCancellable) {watcher.cancelAndJoin()}
            }
        }
    }
    // Both paths are read-only. Final publication and cleanup retain their original decisions.
    private suspend fun <T> inspectWindow(proof:WindowProof,active:WindowWork,expected:String?=null,finish:(String)->T):T {
        val row=proof.row
        fun checkCurrent()=synchronized(gate) {requireWindowProof(proof,active)}
        fun version(value:String):String {check(expected==null || expected==value) {"The reviewed bytes changed. Review the current receipt again."}; return value}
        checkCurrent()
        if(row.published.isEmpty()) {
            return Os.open(part(row).path,OsConstants.O_RDONLY or OsConstants.O_NOFOLLOW,0).useOsDescriptor {fd->FileInputStream(fd).use {input->
                val before=synchronized(gate) {requireWindowProof(proof,active); requirePart(row,fd); stamp(Os.fstat(fd)).also {check(it==stamp(part(row)))}}
                val bound=version("private/$before"); verifyWindowPrefix(row,input,active.signal,::checkCurrent)
                synchronized(gate) {
                    requireWindowProof(proof,active); requirePart(row,fd); check(stamp(Os.fstat(fd))==before && stamp(part(row))==before) {"The private checkpoint changed during review."}; finish(bound)
                }
            }}
        }
        val uri=Uri.parse(row.published); val destination=SafDestination(context,Uri.parse(row.target.tree),row.target.parents,active.signal)
        destination.validate(); destination.child(uri); requireName(row,destination,uri); checkCurrent()
        return withProvider(uri,"r",active.signal) {descriptor->ParcelFileDescriptor.AutoCloseInputStream(descriptor).use {input->
            val before=stamp(Os.fstat(descriptor.fileDescriptor)); check(Os.fstat(descriptor.fileDescriptor).st_size==row.size)
            val bound=version("provider/${row.published}/$before"); val digest=MessageDigest.getInstance("SHA-256"); val bytes=ByteArray(StorageRules.RANGE); var offset=0L
            while(offset<row.size) {checkCurrent(); val count=minOf(bytes.size.toLong(),row.size-offset).toInt(); readExactly(input,bytes,count); digest.update(bytes,0,count); offset+=count}
            check(MessageDigest.isEqual(digest.digest(),Rules.decode(row.hash))) {"The recorded provider copy is incomplete or changed. It was kept."}
            destination.child(uri); requireName(row,destination,uri); checkCurrent()
            synchronized(gate) {requireWindowProof(proof,active); check(stamp(Os.fstat(descriptor.fileDescriptor))==before) {"The recorded provider file changed during review."}; finish(bound)}
        }}
    }
    suspend fun reviewWindow(id:String,current:()->Boolean):PhoneIncomingWindowReview=withWindowCheck(id) {active->
        val proof=synchronized(gate) {
            ready(); val row=records.getValue(id); requireWindowRow(row); authorize(row,current); val now=windowNow()
            check(now<=StorageRules.MAX_TIMESTAMP-MutationRules.LIFETIME)
            WindowProof(row,grants.owner.approvalStamp,epoch,windowGeneration,now,windowElapsed(),current)
        }
        inspectWindow(proof,active) {version->
            windowReviews.values.filter {it.proof.row.id==id || windowElapsed()-it.proof.elapsed !in 0 until 300_000}.map {it.value.id}.forEach(windowReviews::remove)
            check(windowReviews.size<128) {"Cancel an earlier incoming-window review first."}
            val row=proof.row; val label=grants.get(row.target.grant,row.peer).label
            val value=PhoneIncomingWindowReview(StorageRules.token(),id,row.name,row.size,row.offset,
                (listOf(label)+row.target.parents.map {it.name}).joinToString(" / "),row.finalName,windowExpiry(row),proof.wall+MutationRules.LIFETIME,proof.wall+300_000,
                row.peer,grants.owner.capture(row.peer).name)
            windowReviews[value.id]=WindowOffer(value,proof,version); value
        }
    }
    suspend fun confirmWindow(value:PhoneIncomingWindowReview):Unit=withWindowCheck(value.receipt) {active->
        val offer=synchronized(gate) {val old=requireNotNull(windowReviews.remove(value.id)) {"This incoming-window review is unavailable."}; check(old.value==value) {"This incoming-window review changed."}; old}
        inspectWindow(offer.proof,active,offer.version) {put(offer.proof.row.copy(operationApprovedAt=offer.proof.wall)); checked.remove(value.receipt); Unit}
    }
    fun cancelWindowReview(id:String)=synchronized(gate) {windowReviews.remove(id); Unit}
    fun invalidateWindowReviews() {
        val active=synchronized(gate) {windowGeneration++; windowReviews.clear(); windowWork}
        active?.signal?.cancel(); active?.job?.cancel()
    }
    private fun deletePrivate(row:Row) {
        val file=part(row)
        if(file.exists() || java.nio.file.Files.isSymbolicLink(file.toPath())) {val stat=Os.lstat(file.path); check(row.identity.isNotEmpty() && identity(stat)==row.identity && OsConstants.S_ISREG(stat.st_mode) && stat.st_nlink==1L); check(file.delete()); scoped?.store?.deleted(row.temporary)}
    }
    internal fun stopScopedPrivate():UploadInfo=synchronized(gate) {
        check(scoped?.stopOnly==true && isIdle); ready(); scoped.authorize(); val row=records.values.single(); deletePrivate(row)
        info(if(row.phase in terminal) row else put(row.copy(phase=if(row.finalName.isEmpty()) UploadPhase.UPLOAD_CANCELED else UploadPhase.UPLOAD_REVIEW,
            message="The parent batch stopped. Identified private staging was removed; possible provider copies were kept.")))
    }
    fun cancelScope(scope:String) {
        val active=synchronized(gate) {
            publishedChecks.values.removeAll {it.scope==scope}
            synchronized(payloadGate) {
                if (payloadPoints.values.removeAll { it.scope == scope }) incomingRates.retain(payloadPoints.keys)
            }
            work.values.filter {it.scope==scope}
        }
        active.forEach {it.signal.cancel(); it.job.cancel()}
    }
    fun pauseAll() {
        val active=synchronized(gate) {publishedChecks.clear(); clearPayloadRates(); work.values.toList()}
        active.forEach {it.signal.cancel(); it.job.cancel()}
    }
    internal fun interruptScopedVerification() {
        check(scoped != null)
        // Stop can interrupt a blocked provider before it acquires the owner gate.
        work.values.forEach { it.signal.cancel(); it.job.cancel() }
    }
    suspend fun discardPrivate(id:String) {
        val active=work[id]; active?.signal?.cancel(); active?.job?.cancel(); active?.job?.join()
        synchronized(gate) {ready(); val row=records.getValue(id); deletePrivate(row); put(row.copy(phase=if(row.phase in terminal) row.phase else if(row.finalName.isNotEmpty()) UploadPhase.UPLOAD_REVIEW else UploadPhase.UPLOAD_CANCELED,message="Owner removed private staging. Any provider copies were kept."))}
    }
    fun orphanCount()=synchronized(gate) {runCatching {privateFiles().count {file->records.values.none {part(it).name==file.name}}}.getOrDefault(0)}
    private fun partChanged() {mutablePartChanges.value++}
    private fun orphanBinding():String {
        check(scoped==null); ready(); check(work.isEmpty() && windowWork==null) {"Wait for incoming verification or window review before reviewing private staging."}; checkDirectory()
        try {
            check(stamp(journalPath)==journalVersion && listOf(".new",".bak").none {Files.exists(File(journalPath.path+it).toPath(),LinkOption.NOFOLLOW_LINKS)})
        } catch(e:Exception) {
            blocked=true; orphanRecovery?.invalidate(); recovery.add(journalPath,"Incoming ownership metadata changed. Preserve and reset its history before private-part cleanup."); partChanged()
            throw StorageFailure(FileStatus.CONFLICT,"Incoming ownership metadata changed. Private files were kept for local review.")
        }
        return "$directoryIdentity/$epoch"
    }
    private fun unreferencedParts():List<File> {val known=records.values.map {part(it).name}.toSet(); return privateFiles().filter {it.name !in known}}
    fun orphanRemovalProgress():PhoneReceivingPartRemovalProgress?=synchronized(gate) {orphanRecovery?.progress()}
    fun inspectOrphans(start:Int,allowed:()->Boolean={true}):List<PhoneUnreferencedReceivingPart> =grants.authorized {synchronized(gate) {requireNotNull(orphanRecovery).inspect(start,allowed)}}
    fun reviewOrphanRemoval(allowed:()->Boolean={true}):PhoneReceivingPartRemovalReview=grants.authorized {synchronized(gate) {requireNotNull(orphanRecovery).review(allowed)}}
    fun confirmOrphanRemoval(value:PhoneReceivingPartRemovalReview,allowed:()->Boolean={true}):Boolean=grants.authorized {synchronized(gate) {
        try {requireNotNull(orphanRecovery).confirm(value,allowed)} finally {partChanged()}
    }}
    fun continueOrphanRemoval(allowed:()->Boolean={true}):Boolean=grants.authorized {synchronized(gate) {
        try {requireNotNull(orphanRecovery).continueRemoval(allowed)} finally {partChanged()}
    }}
    suspend fun archiveHistory() {
        val jobs=synchronized(gate) {ready(); maintenance=true; pauseAll(); work.values.map {it.job}}
        try {
            jobs.joinAll()
            synchronized(gate) {
                check(work.isEmpty()); recovery.requireHealthy(); check(!blocked)
                val shelf=File(directory,"recovery"); check(shelf.mkdir() || shelf.isDirectory)
                check(shelf.canonicalFile.parentFile==directory.canonicalFile && OsConstants.S_ISDIR(Os.lstat(shelf.path).st_mode))
                check(shelf.list().orEmpty().size<256) {"Review preserved metadata before archiving more history."}
                val archive=File(shelf,StorageRules.token()); check(archive.mkdir())
                PrivateJsonFile(File(archive,"journal.json"),2*1024*1024).write(journal.read())
                val before=records.toMap(); val previousEpoch=epoch
                records.clear(); epoch=StorageRules.token(); checked.clear()
                try {save()} catch(e:Exception) {records.putAll(before); epoch=previousEpoch; throw e}
                orphanRecovery?.invalidate(); reconcileAccess(); emit(); partChanged()
            }
        } finally {synchronized(gate) {maintenance=false; emit()}}
    }
    fun recoveryIssues()=synchronized(gate) {recovery.all()}
    fun archiveRecovery(id:String)=synchronized(gate) {check(work.isEmpty()); recovery.archive(id); if(!recovery.needed()) {records.clear(); clearPayloadRates(); epoch=StorageRules.token(); blocked=false; save(); orphanRecovery?.invalidate(); access.reconcile("incoming:",emptyMap()); emit(); partChanged()}}
    fun preservedRecords()=synchronized(gate) {recovery.archivedCount()}
    fun clearArchives()=synchronized(gate) {recovery.clearArchives()}
    suspend fun shutdown() {if(!closed.compareAndSet(false,true)) return; invalidateWindowReviews(); windowWork?.job?.cancelAndJoin(); pauseAll(); work.values.map {it.job}.joinAll(); lifetime.cancel(); lease?.release(); fileLock?.close(); synchronized(gate) {orphanRecovery?.invalidate(); if(grants.ordinaryIncoming===this) grants.ordinaryIncoming=null}}
    companion object {
        private fun rowInfo(row:Row)=UploadInfo.newBuilder().setId(row.id).setName(row.name).setSize(row.size).setSha256(ByteString.copyFrom(Rules.decode(row.hash)))
            .setOffset(row.offset).setPhase(row.phase).setCollision(row.collision).setFinalName(row.finalName).setMessage(row.message).build()
        private fun decodeRow(x:JSONObject,ownerId:String):Row {
            val path=x.getJSONArray("parents"); require(path.length()<=32)
            val row=Row(x.getString("owner"),x.getString("peer"),IncomingTarget(x.getString("grant"),x.getString("tree"),x.getString("pairing"),
                (0 until path.length()).map {path.getJSONObject(it).let {p->SafFolderBinding(p.getString("id"),p.getString("name"))}}),
                x.getString("id"),x.getString("name"),x.getLong("size"),x.getString("hash"),UploadCollision.valueOf(x.getString("collision")),x.getString("temporary"),x.getString("identity"),
                x.getLong("offset"),x.getString("chain"),UploadPhase.valueOf(x.getString("phase")),x.getString("finalName"),x.getString("published"),x.getString("message"),if(x.has("operationApprovedAt")) x.getLong("operationApprovedAt") else 0)
            UploadRules.validate(rowInfo(row)); AndroidSafAccess.validateTree(Uri.parse(row.target.tree)); SafDestination.validateBindings(row.target.parents)
            require(row.owner==ownerId && row.peer.matches(Regex("[a-f0-9]{64}")) && Rules.tokenPattern.matches(row.target.pairing) && Rules.tokenPattern.matches(row.target.grant) &&
                Rules.tokenPattern.matches(row.temporary) && row.identity.length<=128 && row.chain.matches(Regex("[a-f0-9]{64}")) && row.size<=UploadRules.QUOTA &&
                (row.offset==row.size || row.offset%StorageRules.RANGE==0L) && (row.offset==0L || row.identity.isNotEmpty()) && row.published.length<=16_384 && row.target.tree.length<=16_384)
            require(row.operationApprovedAt==0L || row.operationApprovedAt in MutationRules.created(row.id)..(StorageRules.MAX_TIMESTAMP-MutationRules.LIFETIME))
            if(row.phase==UploadPhase.UPLOAD_COMPLETED) require(row.published.isNotEmpty())
            if(row.published.isNotEmpty()) {
                val uri=Uri.parse(row.published); val tree=Uri.parse(row.target.tree); val document=Docs.getDocumentId(uri)
                require(row.finalName.isNotEmpty() && row.phase in setOf(UploadPhase.UPLOAD_FINALIZING,UploadPhase.UPLOAD_REVIEW,UploadPhase.UPLOAD_COMPLETED) &&
                    uri.scheme=="content" && uri.authority==tree.authority && uri.userInfo==null && uri.query==null && uri.fragment==null && document!=Docs.getTreeDocumentId(tree) && row.target.parents.none {it.id==document})
            }
            return row
        }
        private fun matchesScope(row:Row,scope:AndroidScopedIncoming)=row.operationApprovedAt==0L && row.id==scope.id && row.peer==scope.peer && row.target==scope.target && row.name==scope.descriptor.name &&
            row.size==scope.descriptor.size && row.hash==Rules.b64(scope.descriptor.sha256.toByteArray()) && row.collision==scope.descriptor.collision
        private fun validateScope(root:JSONObject,scope:AndroidScopedIncoming?,ownerId:String) {
            require(root.getInt("schema")==1 && UploadBatchRules.canonicalToken(root.getString("epoch")))
            if(scope==null) require(root.optString("scope","").isEmpty()) else {
                val rows=root.getJSONArray("rows"); require(root.getString("scope")==scope.id && rows.length()==1 && matchesScope(decodeRow(rows.getJSONObject(0),ownerId),scope)) {"The file journal does not belong to this batch child."}
            }
        }
        internal fun validateScopedJournal(scope:AndroidScopedIncoming,ownerId:String) {
            validateScope(JSONObject(PrivateJsonFile(scope.journalFile,AndroidBatchFileStore.RECORD_LIMIT).read()),scope,ownerId)
        }
        internal fun inspectScopedJournal(scope:AndroidScopedIncoming,ownerId:String):UploadInfo {
            val root=JSONObject(PrivateJsonFile(scope.journalFile,AndroidBatchFileStore.RECORD_LIMIT).read()); validateScope(root,scope,ownerId)
            return rowInfo(decodeRow(root.getJSONArray("rows").getJSONObject(0),ownerId))
        }
        internal fun inspectScopedPublished(scope:AndroidScopedIncoming,ownerId:String):Pair<UploadInfo,Uri> {
            val root=JSONObject(PrivateJsonFile(scope.journalFile,AndroidBatchFileStore.RECORD_LIMIT).read()); validateScope(root,scope,ownerId)
            val row=decodeRow(root.getJSONArray("rows").getJSONObject(0),ownerId)
            check(row.phase==UploadPhase.UPLOAD_COMPLETED && row.published.isNotEmpty()) {"The batch file has no completed provider publication receipt."}
            return rowInfo(row) to row.published.toUri()
        }
        internal fun scopedPartExists(scope:AndroidScopedIncoming,ownerId:String):Boolean {
            val root=JSONObject(PrivateJsonFile(scope.journalFile,AndroidBatchFileStore.RECORD_LIMIT).read()); validateScope(root,scope,ownerId)
            val row=decodeRow(root.getJSONArray("rows").getJSONObject(0),ownerId); val file=File(scope.store.directory,row.temporary+".part")
            if(!file.exists() && !java.nio.file.Files.isSymbolicLink(file.toPath())) return false
            val stat=Os.lstat(file.path); require(row.identity.isNotEmpty() && "${stat.st_dev}:${stat.st_ino}"==row.identity && OsConstants.S_ISREG(stat.st_mode) && stat.st_nlink==1L)
            return true
        }
        internal fun validateScopedSnapshot(root:JSONObject,scope:AndroidScopedIncoming,ownerId:String)=validateScope(root,scope,ownerId)
        private val terminal=setOf(UploadPhase.UPLOAD_COMPLETED,UploadPhase.UPLOAD_CANCELED,UploadPhase.UPLOAD_SKIPPED)
        private val released=setOf(UploadPhase.UPLOAD_CANCELED,UploadPhase.UPLOAD_SKIPPED)
    }
}
