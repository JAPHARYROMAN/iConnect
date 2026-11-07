package com.iconnect.proof

import android.content.Context
import android.os.SystemClock
import android.system.Os
import android.system.OsConstants
import android.system.StructStat
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.io.File
import java.io.RandomAccessFile
import java.nio.file.Files
import java.nio.file.LinkOption

data class PhoneSourceResetReview(val id:String,val files:Int,val directories:Int,val bytes:Long,val unrecognizedEntries:Int)
data class PhoneSourceArchiveRemovalReview(val id:String,val archive:String,val files:Int,val directories:Int,val bytes:Long,val unrecognizedEntries:Int)
data class PhoneSourceRecoveryArchive(val id:String,val removing:Boolean=false,val removed:Int=0,val reviewedEntries:Int=0)
data class PhoneSourceArchiveEntry(val path:String,val directory:Boolean,val bytes:Long,val recognized:Boolean)

/** App-private metadata and flat snapshot trees only. Never opens an original provider document. */
internal class PhoneBatchSourceRecovery(context:Context,private val owner:AndroidOwnerPolicy,private val idle:()->Boolean,
    private val blocked:()->Boolean,private val block:(String)->Unit,private val reset:()->Unit,
    private val accounting:()->Unit,private val uncertain:()->Unit):AutoCloseable {
    private val gate=owner.gate
    private val profile=context.filesDir
    private val directory=File(profile,STORE)
    private val profilePath=profile.canonicalPath
    private val profileIdentity=directoryIdentity(profile)
    private class Lease(val file:RandomAccessFile,val lock:java.nio.channels.FileLock):AutoCloseable {
        override fun close() {if(lock.isValid) lock.release(); file.close()}
    }
    private val globalLease:Lease
    private var innerLease:Lease
    var identity:String; private set
    private var open=true
    private var closed=false
    private data class Entry(val parent:String,val name:String,val directory:Boolean,val version:String,val bytes:Long) {
        val path get()=if(parent.isEmpty()) name else "$parent/$name"
    }
    private data class Reset(val value:PhoneSourceResetReview,val owner:String,val root:String,val entries:List<Entry>,val expires:Long)
    private data class Removal(val value:PhoneSourceArchiveRemovalReview,val owner:String,val root:String,val entries:List<Entry>,val expires:Long)
    private class Deletion(val review:Removal,var cursor:Int=0) {
        val directories=review.entries.filter {it.directory && it.parent.isEmpty()}.associate {it.name to it.version}
    }
    private var resetReview:Reset?=null
    private val removalReviews=mutableMapOf<String,Removal>()
    private val deletions=mutableMapOf<String,Deletion>()
    private var archiveIds=emptyList<String>()
    var retainedBytes=0L; private set
    var additionalCopyReview=false; private set
    private val mutableChanges=MutableStateFlow(0L)
    val changes=mutableChanges.asStateFlow()
    init {
        globalLease=lease(File(profile,".outgoing-batch-sources.lock"))
        try {val opened=openStore(); innerLease=opened.first; identity=opened.second}
        catch(e:Exception) {globalLease.close(); throw e}
        runCatching {refreshArchives(); loadCopyReview()}.onFailure {block("Preserved private snapshots need local owner review. Their bytes remain uncertain; new outgoing copies are blocked.")}
    }
    private fun changed() {mutableChanges.value++}
    private fun nativeIdentity(stat:StructStat)="${stat.st_dev}:${stat.st_ino}"
    private fun directoryIdentity(file:File)=Os.lstat(file.path).let {check(OsConstants.S_ISDIR(it.st_mode)); nativeIdentity(it)}
    private fun stamp(stat:StructStat):String {
        check(OsConstants.S_ISREG(stat.st_mode) && stat.st_nlink==1L) {"Links and redirected private snapshots were kept."}
        return "${nativeIdentity(stat)}:${stat.st_size}:${stat.st_mtim.tv_sec}:${stat.st_mtim.tv_nsec}:${stat.st_ctim.tv_sec}:${stat.st_ctim.tv_nsec}"
    }
    private fun stamp(file:File)=stamp(Os.lstat(file.path))
    private fun lease(file:File):Lease {
        if(Files.exists(file.toPath(),LinkOption.NOFOLLOW_LINKS)) stamp(file)
        val opened=RandomAccessFile(file,"rw")
        try {
            val held=Os.fstat(opened.fd); stamp(held); check(nativeIdentity(held)==nativeIdentity(Os.lstat(file.path)))
            return Lease(opened,requireNotNull(opened.channel.tryLock()) {"This private snapshot profile is already open."})
        } catch(e:Exception) {opened.close(); throw e}
    }
    private fun currentProfile() {check(!closed && profile.canonicalPath==profilePath && directoryIdentity(profile)==profileIdentity) {"The original private snapshot profile changed."}}
    private fun authorized(allowed:()->Boolean):String {
        currentProfile(); check(owner.healthy) {"Review the current phone identity before private snapshot recovery."}
        if(!allowed()) throw CancellationException("Open iConnect to continue local private snapshot recovery.")
        return owner.recoveryStamp
    }
    private fun openStore():Pair<Lease,String> {
        currentProfile(); check(directory.mkdir() || directory.isDirectory); check(directory.canonicalFile.parentFile==profile.canonicalFile)
        val id=directoryIdentity(directory); return lease(File(directory,"owner.lock")) to id
    }
    private fun ensureStore() {if(!open) {val opened=openStore(); innerLease=opened.first; identity=opened.second; open=true}}
    private fun list(folder:File,limit:Int):List<File> =Files.newDirectoryStream(folder.toPath()).use {stream->
        stream.asSequence().take(limit+1).map {it.toFile()}.toList().also {check(it.size<=limit) {"Too many private snapshot entries. Everything was kept."}}
    }
    private fun inventory(folder:File):List<Entry> {
        check(folder.canonicalFile.parentFile==profile.canonicalFile); directoryIdentity(folder)
        val entries=mutableListOf<Entry>(); var bytes=0L; var directories=0
        fun add(parent:File,parentName:String,file:File) {
            check(file.canonicalFile.parentFile==parent.canonicalFile)
            val stat=Os.lstat(file.path); val isDirectory=OsConstants.S_ISDIR(stat.st_mode)
            val version=if(isDirectory) nativeIdentity(stat) else stamp(stat)
            val size=if(isDirectory) 0L else stat.st_size; bytes=Math.addExact(bytes,size)
            check(entries.size<MAX_ENTRIES && bytes<=MAX_BYTES) {"Private snapshot recovery exceeds its file or byte limit. Everything was kept."}
            entries.add(Entry(parentName,file.name,isDirectory,version,size))
        }
        list(folder,MAX_ENTRIES).sortedBy {it.name}.forEach {file->
            if(OsConstants.S_ISDIR(Os.lstat(file.path).st_mode)) {
                check(++directories<=UploadBatchRules.MAX_PLANS*3) {"Too many preserved private snapshot folders."}
                check(file.canonicalFile.parentFile==folder.canonicalFile); val original=directoryIdentity(file)
                list(file,MAX_ENTRIES-entries.size).sortedBy {it.name}.forEach {child->
                    check(!OsConstants.S_ISDIR(Os.lstat(child.path).st_mode)) {"Unexpected nested snapshot folders were kept for local investigation."}
                    add(file,file.name,child)
                }
                check(directoryIdentity(file)==original); add(folder,"",file)
            } else add(folder,"",file)
        }
        return entries
    }
    private fun known(entry:Entry):Boolean {
        if(entry.parent.isEmpty()) {
            if(entry.directory) return UploadBatchRules.canonicalToken(entry.name)
            if(entry.name=="owner.lock") return true
            val name=when {entry.name.endsWith(".new") || entry.name.endsWith(".bak")->entry.name.dropLast(4); else->entry.name}
            return name.endsWith(".json") && UploadBatchRules.canonicalToken(name.removeSuffix(".json"))
        }
        if(!UploadBatchRules.canonicalToken(entry.parent) || entry.directory) return false
        if(entry.name=="manifest.bin") return true
        val extension=entry.name.substringAfterLast('.',""); val number=entry.name.substringBeforeLast('.',""); val index=number.toIntOrNull()?:return false
        return extension in setOf("bin","json") && index in 0 until UploadBatchRules.MAX_ITEMS && number==index.toString()
    }
    fun refreshArchives()=synchronized(gate) {
        currentProfile()
        val folders=Files.newDirectoryStream(profile.toPath()).use {stream->stream.asSequence().map {it.toFile()}
            .filter {it.name.startsWith(PREFIX)}.take(MAX_ARCHIVES+1).toList()}
        check(folders.size<=MAX_ARCHIVES) {"Too many preserved private snapshot stores. Review the private profile."}
        var bytes=0L
        folders.forEach {folder->
            check(UploadBatchRules.canonicalToken(folder.name.removePrefix(PREFIX)) && folder.canonicalFile.parentFile==profile.canonicalFile); directoryIdentity(folder)
            bytes=Math.addExact(bytes,inventory(folder).sumOf {it.bytes})
        }
        archiveIds=folders.map {it.name.removePrefix(PREFIX)}.sorted(); retainedBytes=bytes
    }
    private fun loadCopyReview() {
        val marker=File(profile,MARKER)
        if(Files.exists(marker.toPath(),LinkOption.NOFOLLOW_LINKS)) {stamp(marker); check(Os.lstat(marker.path).st_size==0L); additionalCopyReview=true}
        else if(archiveIds.isNotEmpty()) ensureCopyReview()
    }
    private fun ensureCopyReview() {
        val marker=File(profile,MARKER)
        val flags=OsConstants.O_WRONLY or OsConstants.O_CREAT or OsConstants.O_EXCL or OsConstants.O_NOFOLLOW
        if(!Files.exists(marker.toPath(),LinkOption.NOFOLLOW_LINKS)) {
            Os.open(marker.path,flags,384).useOsDescriptor {fd->check(stamp(Os.fstat(fd))==stamp(marker)); Os.fsync(fd)}
        } else Os.open(marker.path,OsConstants.O_RDONLY or OsConstants.O_NOFOLLOW,0).useOsDescriptor {fd->
            check(stamp(Os.fstat(fd))==stamp(marker) && Os.fstat(fd).st_size==0L)
        }
        additionalCopyReview=true
    }
    private fun archive(id:String):File {
        check(UploadBatchRules.canonicalToken(id) && id in archiveIds) {"This preserved private snapshot store is no longer available."}
        return File(profile,PREFIX+id).also {check(it.canonicalFile.parentFile==profile.canonicalFile); directoryIdentity(it)}
    }
    fun archives()=synchronized(gate) {archiveIds.map {id->deletions[id]?.let {PhoneSourceRecoveryArchive(id,true,it.cursor,it.review.entries.size)}?:PhoneSourceRecoveryArchive(id)}}
    fun reviewReset(allowed:()->Boolean):PhoneSourceResetReview=synchronized(gate) {
        val actor=authorized(allowed); check(blocked() && idle()) {"Stop private preparation and sending before reviewing the blocked snapshot store."}
        ensureStore(); accounting(); check(archiveIds.size<MAX_ARCHIVES) {"Review and remove a preserved snapshot store before preserving another."}
        check(directoryIdentity(directory)==identity); val entries=inventory(directory)
        PhoneSourceResetReview(StorageRules.token(),entries.count {!it.directory && !(it.parent.isEmpty() && it.name=="owner.lock")},entries.count {it.directory},entries.sumOf {it.bytes},entries.count {!known(it)}).also {
            resetReview=Reset(it,actor,identity,entries,SystemClock.elapsedRealtime()+120_000)
        }
    }
    fun confirmReset(value:PhoneSourceResetReview,allowed:()->Boolean):String=synchronized(gate) {
        val actor=authorized(allowed); val review=resetReview; resetReview=null
        check(review!=null && review.value==value && review.owner==actor && review.expires>=SystemClock.elapsedRealtime() && idle() && blocked()) {"The private snapshot recovery review changed or expired. Review it again."}
        ensureStore(); accounting(); check(archiveIds.size<MAX_ARCHIVES && directoryIdentity(directory)==review.root && inventory(directory)==review.entries) {"The private snapshots changed after review. Everything was kept."}
        ensureCopyReview(); check(authorized(allowed)==actor); val id=StorageRules.token(); val target=File(profile,PREFIX+id)
        try {
            check(!Files.exists(target.toPath(),LinkOption.NOFOLLOW_LINKS)); innerLease.close(); open=false
            check(directoryIdentity(directory)==review.root && inventory(directory)==review.entries)
            Files.move(directory.toPath(),target.toPath()); check(directoryIdentity(target)==review.root)
            val opened=openStore(); innerLease=opened.first; identity=opened.second; open=true
            reset(); accounting(); changed(); id
        } catch(e:Exception) {
            block("Private snapshot recovery was interrupted. Preserved copies remain; restore access and review recovery again."); uncertain()
            runCatching {ensureStore(); refreshArchives()}; changed(); throw e
        }
    }
    fun archiveEntries(id:String,start:Int,allowed:()->Boolean):List<PhoneSourceArchiveEntry> =synchronized(gate) {
        authorized(allowed); require(start>=0); val entries=inventory(archive(id)); require(start<=entries.size)
        entries.drop(start).take(50).map {PhoneSourceArchiveEntry(it.path,it.directory,it.bytes,known(it))}
    }
    fun reviewRemoval(id:String,allowed:()->Boolean):PhoneSourceArchiveRemovalReview=synchronized(gate) {
        val actor=authorized(allowed); accounting()
        deletions[id]?.let {check(it.review.owner!=actor) {"Continue the already reviewed snapshot removal first."}; deletions.remove(id)}
        val folder=archive(id); val entries=inventory(folder)
        PhoneSourceArchiveRemovalReview(StorageRules.token(),id,entries.count {!it.directory},entries.count {it.directory},entries.sumOf {it.bytes},entries.count {!known(it)}).also {
            removalReviews[id]=Removal(it,actor,directoryIdentity(folder),entries,SystemClock.elapsedRealtime()+120_000)
        }
    }
    fun confirmRemoval(value:PhoneSourceArchiveRemovalReview,allowed:()->Boolean):Boolean=synchronized(gate) {
        val actor=authorized(allowed); val review=removalReviews.remove(value.archive)
        check(review!=null && review.value==value && review.owner==actor && review.expires>=SystemClock.elapsedRealtime()) {"The snapshot archive removal review changed or expired. Review it again."}
        val folder=archive(value.archive); check(directoryIdentity(folder)==review.root && inventory(folder)==review.entries) {"The preserved private snapshots changed. Everything was kept."}
        deletions[value.archive]=Deletion(review); changed(); continueRemoval(value.archive,allowed)
    }
    private fun keepAccounting() {runCatching {accounting()}.onFailure {uncertain()}}
    fun continueRemoval(id:String,allowed:()->Boolean):Boolean=synchronized(gate) {
        val actor=authorized(allowed); val deletion=deletions[id]
        if(deletion==null || deletion.review.owner!=actor) {deletions.remove(id); changed(); error("Review the remaining private snapshots under the current phone identity.")}
        try {
            val folder=archive(id); check(directoryIdentity(folder)==deletion.review.root) {"The preserved snapshot store changed. Its files were kept."}
            val end=minOf(deletion.review.entries.size,deletion.cursor+BATCH)
            while(deletion.cursor<end) {
                check(authorized(allowed)==actor); val entry=deletion.review.entries[deletion.cursor]
                val parent=if(entry.parent.isEmpty()) folder else File(folder,entry.parent).also {
                    check(it.canonicalFile.parentFile==folder.canonicalFile && directoryIdentity(it)==deletion.directories.getValue(entry.parent)) {"A preserved snapshot folder changed. Remaining files were kept."}
                }
                val file=File(parent,entry.name); check(file.canonicalFile.parentFile==parent.canonicalFile)
                if(entry.directory) {check(directoryIdentity(file)==entry.version && list(file,1).isEmpty()); check(file.delete())}
                else Os.open(file.path,OsConstants.O_RDONLY or OsConstants.O_NOFOLLOW,0).useOsDescriptor {fd->
                    check(stamp(Os.fstat(fd))==entry.version && stamp(file)==entry.version) {"A preserved snapshot file changed. Its remaining files were kept."}; check(file.delete())
                }
                deletion.cursor++
            }
            if(deletion.cursor!=deletion.review.entries.size) {accounting(); changed(); false} else {
                check(list(folder,1).isEmpty()) {"Additional private snapshot entries were kept. Review the remaining store again."}
                check(authorized(allowed)==actor && directoryIdentity(folder)==deletion.review.root && folder.delete())
                deletions.remove(id); accounting(); changed(); true
            }
        } catch(e:CancellationException) {keepAccounting(); changed(); throw e}
        catch(e:Exception) {deletions.remove(id); keepAccounting(); changed(); throw e}
    }
    override fun close():Unit=synchronized(gate) {
        if(closed) return; closed=true; resetReview=null; removalReviews.clear(); deletions.clear()
        if(open) innerLease.close(); globalLease.close()
    }
    companion object {
        private const val STORE="outgoing-batch-copies"; private const val PREFIX="outgoing-batch-source-backup-"; private const val MARKER=".outgoing-batch-source-copy-review"
        private const val MAX_ARCHIVES=8; private const val BATCH=64
        private const val MAX_ENTRIES=UploadBatchRules.MAX_PLANS*(UploadBatchRules.MAX_ITEMS*2+4)*3+1
        private const val MAX_BYTES=UploadRules.QUOTA*4+32L*1024*1024
    }
}
