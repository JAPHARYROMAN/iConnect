package com.iconnect.proof

import android.content.Context
import android.system.Os
import android.system.OsConstants
import android.system.StructStat
import com.google.protobuf.ByteString
import com.iconnect.protocol.*
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import org.json.JSONObject
import java.io.File
import java.io.FileDescriptor
import java.io.RandomAccessFile
import java.nio.file.Files
import java.security.MessageDigest

enum class PhoneBatchSourcePhase { PREPARING, READY, FAILED }
data class PhoneBatchSource(val id:String,val owner:String,val peer:String,val pairing:String,val phase:PhoneBatchSourcePhase=PhoneBatchSourcePhase.PREPARING,
    val items:Int=0,val bytes:Long=0,val directory:String="",val manifest:String="",val hash:String="",val encodedLength:Int=0,
    val preparedFiles:Int=0,val preparedBytes:Long=0,val message:String="Preparing private folder copies.",val active:Boolean=false,val label:String="",val mayHaveReceiverCopies:Boolean=false)
data class PhoneBatchSourceRemoval(val id:String,val plan:String,val files:Int,val bytes:Long)

/** Owner-selected snapshots. Local browser access never becomes a remote grant. */
internal class AndroidBatchSources(private val context:Context,private val owner:AndroidOwnerPolicy,resources:PhoneOutgoingResources=PhoneOutgoingResources.isolated(PhoneOutgoingResources.BATCH)) {
    internal var activity:(PhoneActivityEvent)->Unit={}
    internal data class Snapshot(val index:Int,val size:Long,val identity:String,val hash:String="",val stamp:String="")
    private data class Source(val index:Int,val access:LocalUploadSource,val entry:FileEntry)
    private data class Folder(val access:LocalUploadSource,val entry:FileEntry)
    private data class ManifestCache(val stamp:String,val hash:String,val value:UploadBatchManifest)
    private data class FileStamp(val name:String,val identity:String,val size:Long,val stamp:String)
    private data class Review(val value:PhoneBatchSourceRemoval,val record:String,val files:List<FileStamp>,val expires:Long)
    private val gate=owner.gate
    private val root=File(context.filesDir,"outgoing-batch-copies")
    private var recovery=StateRecovery(root)
    private val records=linkedMapOf<String,PhoneBatchSource>()
    private val versions=mutableMapOf<String,String>()
    private val manifests=linkedMapOf<String,ManifestCache>()
    private val reviews=linkedMapOf<String,Review>()
    private val work=mutableMapOf<String,Job>()
    private val completions=mutableMapOf<String,CompletableDeferred<Unit>>()
    private val readers=mutableSetOf<SourceReader>()
    private val lifetime=CoroutineScope(SupervisorJob()+Dispatchers.IO)
    private val mutable=MutableStateFlow<List<PhoneBatchSource>>(emptyList())
    val state=mutable.asStateFlow()
    private val inventory:PhoneOutgoingResources.Participant
    private var closed=false
    private val storage=PhoneBatchSourceRecovery(context,owner,{work.isEmpty() && readers.isEmpty()},{recovery.needed()},
        {message->recovery.block(message)},
        {records.clear(); versions.clear(); manifests.clear(); reviews.clear(); recovery=StateRecovery(root); emit()},
        {refreshAccounting()}, {inventoryUncertain()})
    private val rootIdentity get()=storage.identity
    val recoveryChanges get()=storage.changes
    init {
        try {inventory=resources.open(PhoneOutgoingResources.BATCH,context.filesDir.canonicalPath)} catch(e:Exception) {storage.close(); throw e}
        val copyReview=mutableListOf<String>()
        synchronized(gate) {runCatching {
            val paths=StateRecovery.journals(root,UploadBatchRules.MAX_PLANS); require(paths.size<=UploadBatchRules.MAX_PLANS)
            paths.forEach {path->
                require(path.name==path.name.removeSuffix(".json")+".json" && UploadBatchRules.canonicalToken(path.name.removeSuffix(".json")))
                require(!File(path.path+".new").exists() && !File(path.path+".bak").exists())
                val saved=JSONObject(PrivateJsonFile(path,RECORD_LIMIT).read()); val row=decode(saved); require(path.name==row.id+".json" && records.put(row.id,row)==null)
                if(row.phase==PhoneBatchSourcePhase.READY && (saved.getInt("schema")==1 || storage.additionalCopyReview && !row.mayHaveReceiverCopies)) copyReview.add(row.id)
                versions[row.id]=stamp(path)
                if(row.directory.isEmpty()) require(!folder(row).exists()) {"An unbound private folder was kept."} else plan(row)
                if(row.phase==PhoneBatchSourcePhase.READY) readManifest(row,true)
                else if(row.phase==PhoneBatchSourcePhase.PREPARING) records[row.id]=row.copy(phase=PhoneBatchSourcePhase.FAILED,message="Private preparation was interrupted. Original files and partial copies were kept.")
            }
            require(records.values.groupBy {it.peer}.values.all {it.size<=UploadBatchRules.MAX_PEER_PLANS && it.sumOf {row->row.bytes}<=UploadRules.QUOTA} && records.values.sumOf {it.bytes}<=UploadRules.QUOTA*4)
            val names=children(root,UploadBatchRules.MAX_PLANS*4+1)
            require(names.all {file->if(OsConstants.S_ISDIR(Os.lstat(file.path).st_mode)) file.name in records else file.name=="owner.lock" || records.keys.any {file.name in setOf("$it.json","$it.json.new","$it.json.bak")}})
        }.onFailure {records.clear(); recovery.block("Private batch ownership needs local review. Original and private copies were kept; new preparation is blocked.")}
            inventory.recover(reservations(records.values),recovery.needed(),storage.retainedBytes)
            if(!recovery.needed()) runCatching {copyReview.forEach {id->ready(); val row=records.getValue(id); put(row.copy(mayHaveReceiverCopies=true))}}
                .onFailure {inventory.uncertain(); recovery.block("Private batch copy review history could not be preserved. Copies were kept for local review.")}
            emit()
        }
    }
    private fun reservations(values:Collection<PhoneBatchSource>)=values.map {PhoneOutgoingResources.Reservation(it.id,it.peer,it.bytes,if(it.phase==PhoneBatchSourcePhase.PREPARING) it.bytes-it.preparedBytes else 0)}
    private fun identity(stat:StructStat)="${stat.st_dev}:${stat.st_ino}"
    private fun regular(stat:StructStat) {check(OsConstants.S_ISREG(stat.st_mode) && stat.st_nlink==1L)}
    private fun directoryIdentity(file:File)=Os.lstat(file.path).let {check(OsConstants.S_ISDIR(it.st_mode)); identity(it)}
    private fun stamp(stat:StructStat)="${identity(stat)}:${stat.st_size}:${stat.st_mtim.tv_sec}:${stat.st_mtim.tv_nsec}:${stat.st_ctim.tv_sec}:${stat.st_ctim.tv_nsec}"
    private fun stamp(file:File)=Os.lstat(file.path).let {regular(it); stamp(it)}
    private fun children(directory:File,limit:Int):List<File> = Files.newDirectoryStream(directory.toPath()).use {stream->stream.asSequence().take(limit+1).map {it.toFile()}.toList().also {check(it.size<=limit)}}
    private fun folder(row:PhoneBatchSource)=File(root,row.id)
    private fun plan(row:PhoneBatchSource):File=folder(row).also {check(it.canonicalFile.parentFile==root.canonicalFile && row.directory.isNotEmpty() && directoryIdentity(it)==row.directory) {"The original private batch folder changed."}}
    private fun part(row:PhoneBatchSource,index:Int)=File(plan(row),"$index.bin")
    private fun receipt(row:PhoneBatchSource,index:Int)=File(plan(row),"$index.json")
    private fun ready() {
        recovery.requireHealthy(); check(!closed && owner.healthy && root.canonicalFile.parentFile==context.filesDir.canonicalFile && directoryIdentity(root)==rootIdentity)
        try {records.forEach {(id,_)->check(stamp(File(root,"$id.json"))==versions[id]) {"Private ownership metadata changed."}}}
        catch(e:Exception) {inventory.uncertain(); recovery.block("Private batch ownership changed. Copies were kept for local review."); emit(); throw e}
    }
    private fun authorize(row:PhoneBatchSource) {ready(); check(row.owner==owner.deviceId && owner.current(row.peer)?.epoch==row.pairing) {"This private batch belongs to an earlier identity or pairing."}}
    private fun emit() {mutable.value=records.values.toList().asReversed().map {it.copy(active=it.id in work)}}
    private fun validate(row:PhoneBatchSource) {
        require(UploadBatchRules.canonicalToken(row.id) && listOf(row.owner,row.peer).all {it.matches(Regex("[a-f0-9]{64}"))} && UploadBatchRules.canonicalToken(row.pairing) &&
            row.items in 0..UploadBatchRules.MAX_ITEMS && row.bytes in 0..UploadRules.QUOTA && row.directory.length<=128 && row.manifest.length<=128 && row.hash.length in setOf(0,43) &&
            (row.hash.isEmpty() || Rules.decode(row.hash).size==32 && Rules.b64(Rules.decode(row.hash))==row.hash) && row.encodedLength in 0..UploadBatchRules.MAX_ENCODED_BYTES &&
            row.preparedFiles in 0..row.items && row.preparedBytes in 0..row.bytes && row.message.length<=512 && row.message.none(Character::isISOControl) && row.label.length<=512 && row.label.none(Character::isISOControl))
        if(row.phase==PhoneBatchSourcePhase.READY) require(row.items>0 && row.directory.isNotEmpty() && row.manifest.isNotEmpty() && row.hash.isNotEmpty() && row.encodedLength>0 && row.preparedBytes==row.bytes)
    }
    private fun encode(row:PhoneBatchSource)=JSONObject().put("schema",2).put("id",row.id).put("owner",row.owner).put("peer",row.peer).put("pairing",row.pairing).put("phase",row.phase.name)
        .put("items",row.items).put("bytes",row.bytes).put("directory",row.directory).put("manifest",row.manifest).put("hash",row.hash).put("encodedLength",row.encodedLength)
        .put("preparedFiles",row.preparedFiles).put("preparedBytes",row.preparedBytes).put("message",row.message).put("label",row.label).put("mayHaveReceiverCopies",row.mayHaveReceiverCopies)
    private fun decode(value:JSONObject):PhoneBatchSource {
        val schema=value.getInt("schema"); require(schema in 1..2 && (if(schema==1) !value.has("mayHaveReceiverCopies") else value.get("mayHaveReceiverCopies") is Boolean))
        return PhoneBatchSource(value.getString("id"),value.getString("owner"),value.getString("peer"),value.getString("pairing"),PhoneBatchSourcePhase.valueOf(value.getString("phase")),
            value.getInt("items"),value.getLong("bytes"),value.getString("directory"),value.getString("manifest"),value.getString("hash"),value.getInt("encodedLength"),value.getInt("preparedFiles"),value.getLong("preparedBytes"),value.getString("message"),label=value.optString("label",""),mayHaveReceiverCopies=if(schema==2) value.getBoolean("mayHaveReceiverCopies") else false).also(::validate)
    }
    private fun put(row:PhoneBatchSource):PhoneBatchSource=synchronized(gate) {
        val previous=records[row.id]?.phase
        validate(row)
        inventory.replace(reservations(records.values.filter {it.id!=row.id}+row)) {root.usableSpace}
        try {val path=File(root,row.id+".json")
            check(listOf(".new",".bak").none {Files.exists(File(path.path+it).toPath(),java.nio.file.LinkOption.NOFOLLOW_LINKS)}) {"Uncommitted private batch metadata was kept for review."}
            PrivateJsonFile(path,RECORD_LIMIT).write(encode(row).toString()); versions[row.id]=stamp(path); records[row.id]=row; emit()
            if(previous!=row.phase && row.phase!=PhoneBatchSourcePhase.PREPARING) recordPhoneActivity(activity,PhoneActivityEvent(PhoneActivityKind.BATCH_PREPARE,
                if(row.phase==PhoneBatchSourcePhase.READY) PhoneActivityOutcome.ACCEPTED else PhoneActivityOutcome.FAILED,row.peer)); row}
        catch(e:Exception) {inventory.uncertain(); recovery.block("Private batch progress could not be committed. All copies were kept for local review."); emit(); throw e}
    }
    fun recoveryIssues()=synchronized(gate) {recovery.all()}
    private fun inventoryUncertain() {inventory.uncertain()}
    private fun refreshAccounting() {try {storage.refreshArchives(); inventory.recover(reservations(records.values),recovery.needed(),storage.retainedBytes)} catch(e:Exception) {inventory.uncertain(); throw e}}
    fun recoveryArchives()=storage.archives()
    internal fun reviewStoreReset(allowed:()->Boolean)=storage.reviewReset(allowed)
    internal fun confirmStoreReset(review:PhoneSourceResetReview,allowed:()->Boolean)=storage.confirmReset(review,allowed)
    fun reviewArchiveRemoval(id:String,allowed:()->Boolean={true})=storage.reviewRemoval(id,allowed)
    fun confirmArchiveRemoval(review:PhoneSourceArchiveRemovalReview,allowed:()->Boolean={true})=storage.confirmRemoval(review,allowed)
    fun continueArchiveRemoval(id:String,allowed:()->Boolean={true})=storage.continueRemoval(id,allowed)
    fun archiveEntries(id:String,start:Int,allowed:()->Boolean={true})=storage.archiveEntries(id,start,allowed)
    fun stage(approval:PeerApproval,selected:List<LocalUploadSource>,collision:UploadCollision,allowed:()->Boolean):String=synchronized(gate) {
        refreshAccounting()
        ready(); require(selected.size in 1..UploadBatchRules.MAX_ROOTS && selected.map {it.entry.id}.distinct().size==selected.size && collision!=UploadCollision.UNRECOGNIZED)
        check(owner.isCurrent(approval) && allowed() && selected.all {it.allowed()})
        check(work.size<2 && records.size<UploadBatchRules.MAX_PLANS && records.values.count {it.peer==approval.id}<UploadBatchRules.MAX_PEER_PLANS) {"Private batch slots are full. Review retained copies or wait for preparation."}
        val worker=inventory.tryWorker()?:error("Two outgoing preparations or sends are already active. Retry after one stops.")
        val label=selected.first().entry.name+if(selected.size>1) " + ${selected.size-1} more" else ""
        val row=try {put(PhoneBatchSource(StorageRules.token(),owner.deviceId,approval.id,approval.epoch,label=label,mayHaveReceiverCopies=storage.additionalCopyReview))} catch(e:Exception) {worker.close(); throw e}
        val job=lifetime.launch(start=CoroutineStart.LAZY) {prepare(row,selected,collision,allowed)}
        val done=CompletableDeferred<Unit>(); work[row.id]=job; completions[row.id]=done
        job.invokeOnCompletion {synchronized(gate) {try {
            worker.close()
            if(work[row.id]===job) {work.remove(row.id); if(records[row.id]?.phase==PhoneBatchSourcePhase.PREPARING && !recovery.needed()) runCatching {put(records.getValue(row.id).copy(phase=PhoneBatchSourcePhase.FAILED,message="Private preparation stopped. Original and partial copies were kept."))}; emit()}
        } finally {completions.remove(row.id); done.complete(Unit)}
        }}
        emit(); job.start(); row.id
    }
    private fun same(expected:FileEntry,actual:FileEntry) {check(actual.id==expected.id && actual.grantId==expected.grantId && actual.name==expected.name && actual.isDirectory==expected.isDirectory && actual.size==expected.size && actual.version==expected.version) {"The selected source changed. Prepare a new copy explicitly."}}
    private suspend fun prepare(initial:PhoneBatchSource,selected:List<LocalUploadSource>,collision:UploadCollision,allowed:()->Boolean) {
        var row=initial
        try {
            suspend fun checkCurrent() {currentCoroutineContext().ensureActive(); synchronized(gate) {authorize(row)}; check(allowed() && selected.all {it.allowed()}) {"Local preparation ended. Open the phone and select the sources again."}}
            suspend fun call(access:LocalUploadSource,request:FileRequest):FileResponse {checkCurrent(); val response=PhoneTransferManifests.checked(access.request,request); checkCurrent(); return response}
            suspend fun metadata(access:LocalUploadSource,entry:FileEntry) {same(entry,call(access,FileRequest.newBuilder().setOperation(FileOperation.GET_METADATA).setNodeId(entry.id).build()).entriesList.single())}
            val builder=UploadBatchManifest.newBuilder().setCollision(collision); val sources=mutableListOf<Source>(); val folders=mutableListOf<Folder>(); val ids=mutableSetOf<String>()
            suspend fun visit(access:LocalUploadSource,entry:FileEntry,parent:Int,depth:Int,pathChars:Int) {
                checkCurrent(); require(builder.itemsCount<UploadBatchRules.MAX_ITEMS && depth<=UploadBatchRules.MAX_DEPTH && pathChars+entry.name.length<=UploadBatchRules.MAX_PATH_CHARS && ids.add(entry.id)) {"The selected tree repeats items or exceeds its planning limits."}
                val index=builder.itemsCount; val item=UploadBatchItem.newBuilder().setParent(parent).setName(entry.name).setIsDirectory(entry.isDirectory)
                builder.addItems(item)
                if(!StorageRules.visible(entry.name,emptySet())) {builder.setItems(index,item.setError("This name or sensitive location is excluded from private folder copies.")); return}
                if(entry.permissions and (if(entry.isDirectory) StorageRules.LIST else StorageRules.READ)==0 || entry.size>UploadRules.QUOTA) {builder.setItems(index,item.setError("This item is unavailable or exceeds private copy limits.")); return}
                metadata(access,entry)
                if(entry.isDirectory) {
                    val children=try {PhoneTransferManifests.list({value->call(access,value)},entry)} catch(e:StorageFailure) {
                        checkCurrent(); if(e.status==FileStatus.ACCESS_DENIED) throw e
                        builder.setItems(index,item.setError("This folder could not be enumerated safely. Its contents were not copied.")); return
                    }
                    metadata(access,entry); folders.add(Folder(access,entry))
                    for(child in children.sortedBy {it.name}) visit(access,child,index,depth+1,pathChars+entry.name.length)
                    metadata(access,entry)
                } else {
                    builder.setItems(index,item.setSize(entry.size).setSha256(ByteString.copyFrom(ByteArray(32)))); sources.add(Source(index,access,entry))
                }
            }
            for(source in selected) {metadata(source,source.entry); visit(source,source.entry,-1,1,0)}
            val bytes=UploadBatchRules.validate(builder.build())
            synchronized(gate) {
                authorize(row); val others=records.values.filter {it.id!=row.id}
                check(bytes<=UploadRules.QUOTA-others.filter {it.peer==row.peer}.sumOf {it.bytes} && bytes<=UploadRules.QUOTA*4-others.sumOf {it.bytes}) {"Private batch reservations are full."}
                check(bytes<=(root.usableSpace-64L*1024*1024-inventory.otherPending(row.id)).coerceAtLeast(0)) {"Not enough private storage for these snapshots."}
                row=put(row.copy(items=builder.itemsCount,bytes=bytes))
                val path=folder(row); check(path.mkdir()); row=put(row.copy(directory=directoryIdentity(path)))
            }
            var prepared=0; var preparedBytes=0L
            for(source in sources) {
                checkCurrent(); metadata(source.access,source.entry)
                lateinit var snapshot:Snapshot
                val digest=MessageDigest.getInstance("SHA-256")
                Os.open(part(row,source.index).path,OsConstants.O_WRONLY or OsConstants.O_CREAT or OsConstants.O_EXCL or OsConstants.O_NOFOLLOW,384).useOsDescriptor {fd->
                    snapshot=Snapshot(source.index,source.entry.size,identity(Os.fstat(fd))); writeSnapshot(row,snapshot)
                    var offset=0L
                    while(offset<source.entry.size) {
                        checkCurrent(); val count=minOf(StorageRules.RANGE.toLong(),source.entry.size-offset).toInt()
                        val response=call(source.access,FileRequest.newBuilder().setOperation(FileOperation.READ_RANGE).setNodeId(source.entry.id).setExpectedVersion(source.entry.version).setOffset(offset).setLength(count).build())
                        check(response.content.size()==count && response.endOfFile==(offset+count==source.entry.size)) {"The local source returned an invalid range."}
                        val content=response.content.toByteArray(); var written=0; while(written<count) {checkCurrent(); val n=Os.write(fd,content,written,count-written); check(n>0); written+=n}; digest.update(content); offset+=count
                    }
                    metadata(source.access,source.entry); checkCurrent(); Os.fsync(fd)
                }
                val hash=Rules.b64(digest.digest())
                Os.open(part(row,source.index).path,OsConstants.O_RDONLY or OsConstants.O_NOFOLLOW,0).useOsDescriptor {fd->
                    val stat=Os.fstat(fd); regular(stat); check(stat.st_size==snapshot.size && identity(stat)==snapshot.identity && stamp(Os.lstat(part(row,source.index).path))==stamp(stat))
                    val verify=MessageDigest.getInstance("SHA-256"); var offset=0L
                    while(offset<snapshot.size) {checkCurrent(); val content=read(fd,offset,minOf(StorageRules.RANGE.toLong(),snapshot.size-offset).toInt()); verify.update(content); offset+=content.size}
                    check(MessageDigest.isEqual(verify.digest(),Rules.decode(hash))) {"The private snapshot did not verify after flushing."}
                    snapshot=snapshot.copy(hash=hash,stamp=stamp(stat))
                }
                writeSnapshot(row,snapshot); builder.setItems(source.index,builder.getItems(source.index).toBuilder().setSha256(ByteString.copyFrom(Rules.decode(hash))))
                prepared++; preparedBytes+=snapshot.size
                if(prepared%32==0 || preparedBytes-row.preparedBytes>=1024*1024) row=put(row.copy(preparedFiles=prepared,preparedBytes=preparedBytes))
            }
            for(folder in folders) {checkCurrent(); metadata(folder.access,folder.entry)}
            val encoded=UploadBatchRules.encode(builder.build()); checkCurrent()
            Os.open(File(plan(row),"manifest.bin").path,OsConstants.O_WRONLY or OsConstants.O_CREAT or OsConstants.O_EXCL or OsConstants.O_NOFOLLOW,384).useOsDescriptor {fd->
                row=put(row.copy(manifest=identity(Os.fstat(fd)))); var offset=0
                while(offset<encoded.size) {checkCurrent(); val n=Os.write(fd,encoded,offset,encoded.size-offset); check(n>0); offset+=n}; Os.fsync(fd)
            }
            checkCurrent(); put(row.copy(phase=PhoneBatchSourcePhase.READY,hash=Rules.b64(Rules.digest(encoded)),encodedLength=encoded.size,preparedFiles=prepared,preparedBytes=preparedBytes,
                message="Private folder copies are ready. Original locations remain unshared; choose a receiving location explicitly."))
        } catch(e:Exception) {synchronized(gate) {if(!recovery.needed()) runCatching {put(row.copy(phase=PhoneBatchSourcePhase.FAILED,message="Private preparation stopped. Original files and owned partial copies were kept; select sources again deliberately."))}}}
    }
    private fun writeSnapshot(row:PhoneBatchSource,snapshot:Snapshot) {
        PrivateJsonFile(receipt(row,snapshot.index),RECORD_LIMIT).write(JSONObject().put("schema",1).put("index",snapshot.index).put("size",snapshot.size).put("identity",snapshot.identity).put("hash",snapshot.hash).put("stamp",snapshot.stamp).toString())
    }
    private fun snapshot(row:PhoneBatchSource,index:Int):Snapshot {
        val value=JSONObject(PrivateJsonFile(receipt(row,index),RECORD_LIMIT).read()); require(value.getInt("schema")==1)
        return Snapshot(value.getInt("index"),value.getLong("size"),value.getString("identity"),value.getString("hash"),value.getString("stamp")).also {
            require(it.index==index && index in 0 until UploadBatchRules.MAX_ITEMS && it.size in 0..UploadRules.QUOTA && it.identity.length in 1..128 && it.stamp.length<=256 &&
                (it.hash.isEmpty() || it.hash.length==43 && Rules.b64(Rules.decode(it.hash))==it.hash && Rules.decode(it.hash).size==32))
        }
    }
    private fun requireSnapshot(snapshot:Snapshot,fd:FileDescriptor,path:File) {val stat=Os.fstat(fd); regular(stat); check(identity(stat)==snapshot.identity && stat.st_size==snapshot.size && stamp(stat)==snapshot.stamp && stamp(path)==snapshot.stamp) {"The private snapshot changed. Copies were kept for review."}}
    private fun read(fd:FileDescriptor,offset:Long,length:Int):ByteArray {val result=ByteArray(length); var count=0; while(count<length) {val n=Os.pread(fd,result,count,length-count,offset+count); check(n>0) {"The private snapshot is incomplete."}; count+=n}; return result}
    private fun readManifest(row:PhoneBatchSource,files:Boolean):UploadBatchManifest {
        check(row.phase==PhoneBatchSourcePhase.READY); val path=File(plan(row),"manifest.bin"); val version=stamp(path)
        val cached=manifests[row.id]
        val value=if(cached?.stamp==version && cached.hash==row.hash) cached.value else {
            val encoded=Os.open(path.path,OsConstants.O_RDONLY or OsConstants.O_NOFOLLOW,0).useOsDescriptor {fd->val stat=Os.fstat(fd); regular(stat); check(identity(stat)==row.manifest && stat.st_size==row.encodedLength.toLong() && stamp(stat)==version); read(fd,0,row.encodedLength)}
            check(MessageDigest.isEqual(Rules.digest(encoded),Rules.decode(row.hash)))
            UploadBatchRules.decode(encoded).also {manifest->check(manifest.itemsCount==row.items && UploadBatchRules.validate(manifest)==row.bytes && manifest.itemsList.count {!it.isDirectory && it.error.isEmpty()}==row.preparedFiles)
                if(manifests.size>=2) manifests.remove(manifests.keys.first()); manifests[row.id]=ManifestCache(version,row.hash,manifest)}
        }
        if(files) {
            val expected=mutableSetOf("manifest.bin")
            value.itemsList.forEachIndexed {index,item->if(!item.isDirectory && item.error.isEmpty()) {
                val snapshot=snapshot(row,index); check(snapshot.size==item.size && snapshot.hash.isNotEmpty() && Rules.decode(snapshot.hash).contentEquals(item.sha256.toByteArray()))
                Os.open(part(row,index).path,OsConstants.O_RDONLY or OsConstants.O_NOFOLLOW,0).useOsDescriptor {requireSnapshot(snapshot,it,part(row,index))}
                expected.add("$index.bin"); expected.add("$index.json")
            }}
            val names=children(plan(row),UploadBatchRules.MAX_ITEMS*2+1); check(names.size==expected.size && names.all {it.name in expected && OsConstants.S_ISREG(Os.lstat(it.path).st_mode)}) {"Unowned private batch entries were kept for review."}
        }
        return value
    }
    fun manifest(id:String):UploadBatchManifest=synchronized(gate) {val row=records.getValue(id); authorize(row); readManifest(row,true)}
    fun inspectManifest(id:String):UploadBatchManifest=synchronized(gate) {ready(); val row=records.getValue(id); check(row.owner==owner.deviceId); readManifest(row,false)}
    fun noteReceiverIntent(id:String,peer:String,pairing:String,hash:String)=synchronized(gate) {
        ready(); val row=records.getValue(id)
        check(row.owner==owner.deviceId && row.peer==peer && row.pairing==pairing && row.hash==hash && row.phase==PhoneBatchSourcePhase.READY) {"The original private snapshot changed before saving its receiver-copy history."}
        if(!row.mayHaveReceiverCopies) put(row.copy(mayHaveReceiverCopies=true))
    }
    fun noteLocalCopyUncertainty(id:String)=synchronized(gate) {ready(); val row=records.getValue(id); check(row.phase==PhoneBatchSourcePhase.READY); if(!row.mayHaveReceiverCopies) put(row.copy(mayHaveReceiverCopies=true))}
    inner class SourceReader internal constructor(private val row:PhoneBatchSource,private val snapshot:Snapshot,private val fd:FileDescriptor,private val allowed:()->Boolean,private val worker:AutoCloseable):AutoCloseable {
        private var stopped=false
        val size get()=snapshot.size
        internal val expectedHash get()=snapshot.hash
        internal fun verifyCurrent()=synchronized(gate) {check(!stopped); authorize(row); check(allowed()); requireSnapshot(snapshot,fd,part(row,snapshot.index))}
        fun range(offset:Long,length:Int):ByteArray=synchronized(gate) {
            check(!stopped); authorize(row); check(allowed()); require(offset>=0 && length in 1..StorageRules.RANGE && offset<=snapshot.size-length)
            requireSnapshot(snapshot,fd,part(row,snapshot.index)); read(fd,offset,length)
        }
        override fun close()=synchronized(gate) {if(!stopped) {stopped=true; try {if(fd.valid()) Os.close(fd)} finally {worker.close(); readers.remove(this)}}}
        internal fun belongs(id:String)=row.id==id
    }
    suspend fun openFile(id:String,index:Int,allowed:()->Boolean):SourceReader {
        val reader=synchronized(gate) {
            val row=records.getValue(id); authorize(row); check(allowed()); val manifest=readManifest(row,false); require(index in 0 until manifest.itemsCount)
            val item=manifest.getItems(index); require(!item.isDirectory && item.error.isEmpty()); val snapshot=snapshot(row,index)
            check(snapshot.size==item.size && snapshot.hash.isNotEmpty() && Rules.decode(snapshot.hash).contentEquals(item.sha256.toByteArray()))
            val worker=inventory.tryWorker()?:error("Two outgoing preparations or sends are already active. Retry after one stops.")
            var opened:FileDescriptor?=null
            try {val fd=Os.open(part(row,index).path,OsConstants.O_RDONLY or OsConstants.O_NOFOLLOW,0); opened=fd
                requireSnapshot(snapshot,fd,part(row,index)); SourceReader(row,snapshot,fd,allowed,worker).also {readers.add(it)}}
            catch(e:Exception) {try {opened?.let {if(it.valid()) Os.close(it)}} finally {worker.close()}; throw e}
        }
        try {
            val digest=MessageDigest.getInstance("SHA-256"); var offset=0L
            while(offset<reader.size) {currentCoroutineContext().ensureActive(); val content=reader.range(offset,minOf(StorageRules.RANGE.toLong(),reader.size-offset).toInt()); digest.update(content); offset+=content.size}
            currentCoroutineContext().ensureActive(); reader.verifyCurrent(); check(MessageDigest.isEqual(digest.digest(),Rules.decode(reader.expectedHash))) {"The private checksum changed. No remote effect may be started."}
            return reader
        } catch(e:Exception) {reader.close(); throw e}
    }
    fun reviewRemoval(id:String):PhoneBatchSourceRemoval=synchronized(gate) {
        ready(); check(id !in work && readers.none {it.belongs(id)}) {"Stop private preparation and sending before reviewing its copies."}
        val row=records.getValue(id); val stamps=mutableListOf<FileStamp>()
        if(row.directory.isNotEmpty()) {
            val files=children(plan(row),UploadBatchRules.MAX_ITEMS*2+1); check(files.all {OsConstants.S_ISREG(Os.lstat(it.path).st_mode)})
            val receipts=files.filter {it.name.endsWith(".json")}.associate {file->val text=file.name.removeSuffix(".json"); val index=text.toInt(); require(text==index.toString()); index to snapshot(row,index)}
            files.forEach {file->
                val stat=Os.lstat(file.path); regular(stat)
                if(file.name=="manifest.bin") check(row.manifest.isNotEmpty() && identity(stat)==row.manifest)
                else {val extension=file.extension; val text=file.nameWithoutExtension; val index=text.toInt(); require(text==index.toString() && extension in setOf("bin","json")); val owned=receipts.getValue(index)
                    if(extension=="bin") check(identity(stat)==owned.identity && stat.st_size<=UploadRules.QUOTA)}
                stamps.add(FileStamp(file.name,identity(stat),stat.st_size,stamp(stat)))
            }
        } else check(!folder(row).exists()) {"An unbound private folder was kept; it cannot be removed by name."}
        val now=android.os.SystemClock.elapsedRealtime(); reviews.entries.removeAll {it.value.expires<now}; if(reviews.size>=UploadBatchRules.MAX_PLANS) reviews.remove(reviews.keys.first())
        val payload=stamps.filter {it.name.endsWith(".bin") && it.name!="manifest.bin"}; val value=PhoneBatchSourceRemoval(StorageRules.token(),id,payload.size,payload.sumOf {it.size})
        reviews[value.id]=Review(value,versions.getValue(id),stamps,now+120_000); value
    }
    fun removeReviewed(confirmation:PhoneBatchSourceRemoval)=synchronized(gate) {
        ready(); val review=reviews.remove(confirmation.id)
        check(review!=null && review.value==confirmation && review.expires>=android.os.SystemClock.elapsedRealtime() && versions[confirmation.plan]==review.record && confirmation.plan !in work && readers.none {it.belongs(confirmation.plan)}) {"The private-copy review changed or expired. Review it again."}
        val row=records.getValue(confirmation.plan)
        if(row.directory.isNotEmpty()) {
            val directory=plan(row); val names=children(directory,review.files.size); check(names.size==review.files.size && names.all {file->review.files.any {it.name==file.name}})
            fun checkFile(file:File,expected:FileStamp) {check(stamp(file)==expected.stamp)}
            review.files.forEach {checkFile(File(directory,it.name),it)}
            review.files.sortedBy {it.name.endsWith(".json")}.forEach {expected->val file=File(directory,expected.name)
                Os.open(file.path,OsConstants.O_RDONLY or OsConstants.O_NOFOLLOW,0).useOsDescriptor {fd->checkFile(file,expected); check(stamp(Os.fstat(fd))==expected.stamp); check(file.delete())}
            }
            check(directoryIdentity(directory)==row.directory && directory.delete())
        }
        val record=File(root,row.id+".json"); check(stamp(record)==review.record); check(record.delete()); records.remove(row.id); versions.remove(row.id); manifests.remove(row.id); inventory.replace(reservations(records.values)); emit()
    }
    fun cancel(id:String)=synchronized(gate) {work[id]?.cancel()}
    fun pauseAll()=synchronized(gate) {work.values.toList().forEach {it.cancel()}}
    // Job.join may observe terminal state before its completion callbacks release the slot.
    suspend fun whenIdle() {synchronized(gate) {completions.values.toList()}.awaitAll()}
    suspend fun shutdown() {
        synchronized(gate) {if(closed) return; closed=true; pauseAll()}; whenIdle()
        synchronized(gate) {readers.toList().forEach {it.close()}; lifetime.cancel(); inventory.close(); storage.close()}
    }
    companion object {private const val RECORD_LIMIT=64*1024}
}
