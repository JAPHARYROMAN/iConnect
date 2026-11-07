package com.iconnect.proof

import android.content.Context
import android.system.Os
import android.system.OsConstants
import com.iconnect.protocol.UploadInfo
import com.iconnect.protocol.UploadPhase
import com.iconnect.protocol.FileStatus
import org.json.JSONObject
import java.io.File
import java.io.RandomAccessFile
import java.nio.file.Files

/** Constructed by the accepted-plan host; never supplied by a standalone request. */
internal data class AndroidScopedIncoming(val journalFile:File,val id:String,val peer:String,val descriptor:UploadInfo,val target:IncomingTarget,
    val fresh:Boolean,val store:AndroidBatchFileStore,val authorize:()->Unit,val persist:(String)->Unit,val stopOnly:Boolean=false)

/** App-private parts stay outside the metadata archives. Called under the owner gate. */
internal class AndroidBatchFileStore(private val context:Context,private val metadata:File,private val owner:AndroidOwnerPolicy,private val fail:(String)->Nothing) {
    internal val directory=File(context.filesDir,"incoming-batch-parts")
    private data class Entry(val stamp:String,val bytes:Long,val temporary:String,val remaining:Long)
    private val entries=linkedMapOf<String,Entry>()
    private val totals=mutableMapOf<String,Long>()
    private val temporaryOwners=mutableMapOf<String,String>()
    private val partNames=mutableSetOf<String>()
    private var orphans=0
    private var metadataIdentity=""
    private var partsIdentity=""
    private var partsVersion=""
    private var partCount=0
    private var closed=false
    private val orphanRecovery=AndroidReceivingPartRecovery(owner,::recoveryBinding,::unreferencedParts,
        {name->root(); name.removeSuffix(".part") !in temporaryOwners}, {name->deleted(name.removeSuffix(".part"))})
    private val fileLock:RandomAccessFile
    private val lease:java.nio.channels.FileLock
    init {
        check(directory.mkdirs() || directory.isDirectory)
        check(directory.canonicalFile.parentFile==context.filesDir.canonicalFile && OsConstants.S_ISDIR(Os.lstat(directory.path).st_mode))
        val lock=File(directory,"owner.lock")
        if(Files.exists(lock.toPath(),java.nio.file.LinkOption.NOFOLLOW_LINKS)) stamp(lock)
        fileLock=RandomAccessFile(lock,"rw")
        try {val stat=Os.fstat(fileLock.fd); check(stamp(stat)==stamp(lock)); lease=requireNotNull(fileLock.channel.tryLock()) {"Batch incoming parts are already open."}} catch(e:Exception) {fileLock.close(); throw e}
        try {partsIdentity=identity(directory); resetMetadata(); scanParts()} catch(e:Exception) {lease.release(); fileLock.close(); throw e}
    }
    private fun identity(file:File)=Os.lstat(file.path).let {check(OsConstants.S_ISDIR(it.st_mode)); "${it.st_dev}:${it.st_ino}"}
    private fun stamp(s:android.system.StructStat):String {
        check(OsConstants.S_ISREG(s.st_mode) && s.st_nlink==1L)
        return "${s.st_dev}:${s.st_ino}:${s.st_size}:${s.st_mtim.tv_sec}:${s.st_mtim.tv_nsec}:${s.st_ctim.tv_sec}:${s.st_ctim.tv_nsec}"
    }
    private fun stamp(file:File)=stamp(Os.lstat(file.path))
    private fun directoryVersion()=Os.lstat(directory.path).let {s->check(OsConstants.S_ISDIR(s.st_mode) && "${s.st_dev}:${s.st_ino}"==partsIdentity); "${s.st_mtim.tv_sec}:${s.st_mtim.tv_nsec}:${s.st_ctim.tv_sec}:${s.st_ctim.tv_nsec}"}
    private fun root() {check(!closed && metadata.canonicalFile.parentFile==context.filesDir.canonicalFile && identity(metadata)==metadataIdentity && directory.canonicalFile.parentFile==context.filesDir.canonicalFile && identity(directory)==partsIdentity) {"The batch metadata or private staging directory changed."}}
    fun resetMetadata() {orphanRecovery.invalidate(); metadataIdentity=identity(metadata); entries.clear(); totals.clear(); temporaryOwners.clear(); orphans=partNames.size}
    private fun stats(file:File,text:String):Entry {
        val root=JSONObject(text); val rows=root.getJSONArray("rows"); require(rows.length()==1)
        val row=rows.getJSONObject(0); val size=row.getLong("size"); val offset=row.getLong("offset"); val temporary=row.getString("temporary"); val phase=UploadPhase.valueOf(row.getString("phase"))
        require(size in 0..UploadRules.QUOTA && offset in 0..size && UploadBatchRules.canonicalToken(temporary) && phase!=UploadPhase.UNRECOGNIZED)
        val terminal=phase in setOf(UploadPhase.UPLOAD_COMPLETED,UploadPhase.UPLOAD_CANCELED,UploadPhase.UPLOAD_SKIPPED)
        return Entry(stamp(file),file.length(),temporary,if(terminal) 0 else size-offset)
    }
    fun load(paths:List<File>) {
        resetMetadata()
        paths.forEach {file->val parsed=parse(file.name)?:return@forEach
            val text=PrivateJsonFile(file,RECORD_LIMIT).read(); val entry=stats(file,text); require(entries.size<AndroidBatchPlans.MAX_DETAILED_PLANS*UploadBatchRules.MAX_ITEMS && entries.put(file.name,entry)==null)
            require(temporaryOwners.put(entry.temporary,file.name)==null)
            val total=(totals[parsed.first]?:0)+entry.bytes; require(total<=BATCH_LIMIT); totals[parsed.first]=total
        }
        orphans=partNames.count {it.removeSuffix(".part") !in temporaryOwners}
    }
    fun remainingBytes()=entries.values.sumOf {it.remaining}
    fun checkJournal(name:String,fresh:Boolean=false) {
        try {
            root(); val file=File(metadata,name); val json=PrivateJsonFile(file,RECORD_LIMIT)
            if(fresh) check(name !in entries && !json.exists()) {"An unowned batch file journal was kept."}
            else check(entries[name]?.stamp==stamp(file)) {"A retained batch file journal is missing or changed."}
        } catch(e:Exception) {fail("Batch file ownership needs local review. Private and provider copies were kept; new incoming copies are blocked.")}
    }
    fun reserve(batch:String) {if((totals[batch]?:0)>BATCH_LIMIT-RECORD_LIMIT) throw StorageFailure(FileStatus.RESOURCE_EXHAUSTED,"Batch file journal capacity is full.")}
    fun removeRetired(name:String) {
        checkJournal(name); val entry=entries.getValue(name); val file=File(metadata,name)
        check(!File(directory,entry.temporary+".part").exists()) {"Private staging remains; its journal was kept."}
        Os.open(file.path,OsConstants.O_RDONLY or OsConstants.O_NOFOLLOW,0).useOsDescriptor {fd->
            check(stamp(file)==entry.stamp && Os.fstat(fd).let {s->s.st_dev==Os.lstat(file.path).st_dev && s.st_ino==Os.lstat(file.path).st_ino && s.st_nlink==1L}); check(file.delete())}
        entries.remove(name); temporaryOwners.remove(entry.temporary); val batch=requireNotNull(parse(name)).first; totals[batch]=(totals[batch]?:0)-entry.bytes
    }
    fun write(name:String,text:String,allowNew:Boolean) {
        val parsed=requireNotNull(parse(name)); val bytes=text.toByteArray(Charsets.UTF_8).size
        if(bytes>RECORD_LIMIT) throw StorageFailure(FileStatus.RESOURCE_EXHAUSTED,"The batch file journal exceeds its record limit.")
        checkJournal(name,allowNew && name !in entries)
        val total=(totals[parsed.first]?:0)-(entries[name]?.bytes?:0)+bytes
        if(total>BATCH_LIMIT) throw StorageFailure(FileStatus.RESOURCE_EXHAUSTED,"Batch file journal storage is full.")
        try {
            val row=JSONObject(text).getJSONArray("rows").getJSONObject(0); val temporary=row.getString("temporary"); val owner=temporaryOwners[temporary]
            require(owner==null || owner==name); require(entries[name]?.temporary.let {it==null || it==temporary})
            val file=File(metadata,name); PrivateJsonFile(file,RECORD_LIMIT).write(text); entries[name]=stats(file,text); totals[parsed.first]=total
            if(temporaryOwners.put(temporary,name)==null && temporary+".part" in partNames) orphans--
        }
        catch(e:Exception) {fail("Batch file progress could not be committed. Private and provider copies were kept for local review.")}
    }
    private fun scanParts():List<File> = Files.newDirectoryStream(directory.toPath()).use {stream->
        val files=stream.asSequence().map {it.toFile()}.filter {it.name!="owner.lock"}.take(MAX_PARTS+1).toList()
        check(files.size<=MAX_PARTS && files.all {file->file.name.matches(Regex("[A-Za-z0-9_-]{43}\\.part")) && Os.lstat(file.path).let {s->OsConstants.S_ISREG(s.st_mode) && s.st_nlink==1L && s.st_size in 0..UploadRules.QUOTA}})
        partCount=files.size; partNames.clear(); partNames.addAll(files.map {it.name}); orphans=partNames.count {it.removeSuffix(".part") !in temporaryOwners}; partsVersion=directoryVersion(); files
    }
    fun canCreate() {root(); if(directoryVersion()!=partsVersion) scanParts(); if(partCount>=MAX_PARTS) throw StorageFailure(FileStatus.RESOURCE_EXHAUSTED,"Private batch staging is full. Review abandoned parts locally.")}
    fun created(temporary:String) {if(partNames.add(temporary+".part")) {partCount++; if(temporary !in temporaryOwners) orphans++}; partsVersion=directoryVersion()}
    fun deleted(temporary:String) {if(partNames.remove(temporary+".part")) {partCount--; if(temporary !in temporaryOwners) orphans--}; partsVersion=directoryVersion()}
    fun orphanCount():Int {root(); if(directoryVersion()!=partsVersion) scanParts(); return orphans}
    private fun recoveryBinding():String {
        root()
        val names=Files.newDirectoryStream(metadata.toPath()).use {stream->stream.asSequence().map {it.fileName.toString()}
            .filter {parse(it.removeSuffix(".new").removeSuffix(".bak"))!=null}.take(AndroidBatchPlans.MAX_DETAILED_PLANS*UploadBatchRules.MAX_ITEMS+1).toList()}
        if(names.size!=entries.size || names.toSet()!=entries.keys) fail("Batch file journals changed. Preserve and review receiver history before private-part cleanup.")
        entries.keys.forEach {checkJournal(it)}
        return "$metadataIdentity/$partsIdentity"
    }
    private fun unreferencedParts():List<File> {root(); return scanParts().filter {it.name.removeSuffix(".part") !in temporaryOwners}}
    fun orphanRemovalProgress()=orphanRecovery.progress()
    fun inspectOrphans(start:Int,allowed:()->Boolean)=orphanRecovery.inspect(start,allowed)
    fun reviewOrphanRemoval(allowed:()->Boolean)=orphanRecovery.review(allowed)
    fun confirmOrphanRemoval(value:PhoneReceivingPartRemovalReview,allowed:()->Boolean):Boolean=try {orphanRecovery.confirm(value,allowed)} finally {partsVersion=""}
    fun continueOrphanRemoval(allowed:()->Boolean):Boolean=try {orphanRecovery.continueRemoval(allowed)} finally {partsVersion=""}
    fun close() {if(closed) return; closed=true; orphanRecovery.invalidate(); lease.release(); fileLock.close()}
    companion object {
        const val RECORD_LIMIT=256*1024
        const val BATCH_LIMIT=16*1024*1024
        const val MAX_PARTS=UploadBatchRules.MAX_PLANS*UploadBatchRules.MAX_ITEMS
        fun name(batch:String,index:Int)="$batch.$index.upload.json"
        fun parse(name:String):Pair<String,Int>? {
            if(!name.endsWith(".upload.json")) return null
            val stem=name.removeSuffix(".upload.json"); val split=stem.indexOf('.'); if(split<0) return null
            val batch=stem.take(split); val text=stem.drop(split+1); val index=text.toIntOrNull()?:return null
            return if(UploadBatchRules.canonicalToken(batch) && index in 0 until UploadBatchRules.MAX_ITEMS && text==index.toString()) batch to index else null
        }
    }
}
