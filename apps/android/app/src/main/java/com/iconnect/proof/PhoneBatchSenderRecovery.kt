package com.iconnect.proof

import android.content.Context
import android.os.SystemClock
import android.system.Os
import android.system.OsConstants
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.io.File
import java.io.IOException
import java.io.RandomAccessFile
import java.nio.file.Files

data class PhoneSenderResetReview(val id:String,val files:Int,val bytes:Long,val unrecognizedFiles:Int)
data class PhoneSenderArchiveRemovalReview(val id:String,val archive:String,val files:Int,val bytes:Long,val unrecognizedFiles:Int)
data class PhoneSenderRecoveryArchive(val id:String,val removing:Boolean=false,val removed:Int=0,val reviewedFiles:Int=0)
data class PhoneSenderArchiveFile(val name:String,val bytes:Long,val recognized:Boolean)

/** App-private flat stores only. No provider document or original source URI is opened here. */
internal class PhoneBatchSenderRecovery(private val context:Context,private val owner:AndroidOwnerPolicy,private val sources:AndroidBatchSources,
    private val idle:()->Boolean,private val blocked:()->Boolean,private val block:(String)->Unit,private val reset:()->Unit):AutoCloseable {
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
    private data class Entry(val name:String,val version:String,val size:Long)
    private data class Reset(val value:PhoneSenderResetReview,val owner:String,val root:String,val entries:List<Entry>,val expires:Long)
    private data class Removal(val value:PhoneSenderArchiveRemovalReview,val owner:String,val root:String,val entries:List<Entry>,val expires:Long)
    private class Deletion(val review:Removal,var cursor:Int=0)
    private var resetReview:Reset?=null
    private val removalReviews=mutableMapOf<String,Removal>()
    private val deletions=mutableMapOf<String,Deletion>()
    private var archiveIds=emptyList<String>()
    private val mutableChanges=MutableStateFlow(0L)
    val changes=mutableChanges.asStateFlow()
    init {
        check(OsConstants.S_ISDIR(Os.lstat(profile.path).st_mode))
        globalLease=lease(File(profile,".outgoing-batch-sender.lock"))
        try {val opened=openStore(); innerLease=opened.first; identity=opened.second}
        catch(e:Exception) {globalLease.close(); throw e}
        runCatching {refreshArchives()}.onFailure {block("Preserved sender stores need local owner review. Their files were kept.")}
    }
    private fun changed() {mutableChanges.value++}
    private fun nativeIdentity(stat:android.system.StructStat)="${stat.st_dev}:${stat.st_ino}"
    private fun directoryIdentity(file:File):String=Os.lstat(file.path).let {check(OsConstants.S_ISDIR(it.st_mode)); nativeIdentity(it)}
    private fun stamp(stat:android.system.StructStat):String {
        check(OsConstants.S_ISREG(stat.st_mode) && stat.st_nlink==1L) {"Links and redirected sender metadata were kept."}
        return "${nativeIdentity(stat)}:${stat.st_size}:${stat.st_mtim.tv_sec}:${stat.st_mtim.tv_nsec}:${stat.st_ctim.tv_sec}:${stat.st_ctim.tv_nsec}"
    }
    private fun stamp(file:File)=stamp(Os.lstat(file.path))
    private fun lease(file:File):Lease {
        if(file.exists() || Files.isSymbolicLink(file.toPath())) stamp(file)
        val opened=RandomAccessFile(file,"rw")
        try {
            val held=Os.fstat(opened.fd); stamp(held); check(nativeIdentity(held)==nativeIdentity(Os.lstat(file.path)))
            return Lease(opened,requireNotNull(opened.channel.tryLock()) {"This sender profile is already open."})
        } catch(e:Exception) {opened.close(); throw e}
    }
    private fun currentProfile() {check(!closed && profile.canonicalPath==profilePath && directoryIdentity(profile)==profileIdentity) {"The original sender profile changed."}}
    private fun authorized(allowed:()->Boolean):String {
        currentProfile(); check(owner.healthy) {"Review the current phone identity before sender recovery."}
        if(!allowed()) throw CancellationException("Open iConnect to continue local sender recovery.")
        return owner.recoveryStamp
    }
    private fun openStore():Pair<Lease,String> {
        currentProfile(); check(directory.mkdir() || directory.isDirectory)
        check(directory.canonicalFile.parentFile==profile.canonicalFile)
        val root=directoryIdentity(directory); return lease(File(directory,"owner.lock")) to root
    }
    private fun ensureStore() {if(!open) {val opened=openStore(); innerLease=opened.first; identity=opened.second; open=true}}
    private fun list(folder:File,limit:Int):List<File> =Files.newDirectoryStream(folder.toPath()).use {stream->
        stream.asSequence().take(limit+1).map {it.toFile()}.toList().also {check(it.size<=limit) {"The private sender store has too many entries."}}
    }
    private fun inventory(folder:File):List<Entry> {
        check(folder.canonicalFile.parentFile==profile.canonicalFile); directoryIdentity(folder)
        var bytes=0L
        return list(folder,MAX_ENTRIES).sortedBy {it.name}.map {file->
            check(file.canonicalFile.parentFile==folder.canonicalFile)
            val stat=Os.lstat(file.path); val version=stamp(stat); bytes=Math.addExact(bytes,stat.st_size)
            check(bytes<=MAX_BYTES) {"The sender store exceeds its recovery size limit. Its files were kept."}
            Entry(file.name,version,stat.st_size)
        }
    }
    private fun known(name:String):Boolean {
        if(name=="owner.lock") return true
        val plain=name.removeSuffix(".new").removeSuffix(".bak")
        if(!plain.endsWith(".json")) return false
        val stem=plain.removeSuffix(".json"); if(UploadBatchRules.canonicalToken(stem)) return true
        val at=stem.indexOf('.'); if(at<0 || !UploadBatchRules.canonicalToken(stem.take(at))) return false
        val index=stem.substring(at+1).toIntOrNull()?:return false
        return index in 0 until UploadBatchRules.MAX_ITEMS && index.toString()==stem.substring(at+1)
    }
    private fun refreshArchives() {
        currentProfile()
        val names=Files.newDirectoryStream(profile.toPath()).use {stream->stream.asSequence().map {it.toFile()}
            .filter {it.name.startsWith(PREFIX)}.take(MAX_ARCHIVES+1).toList()}
        check(names.size<=MAX_ARCHIVES) {"Too many preserved sender stores. Review the private profile."}
        names.forEach {check(UploadBatchRules.canonicalToken(it.name.removePrefix(PREFIX)) && it.canonicalFile.parentFile==profile.canonicalFile); directoryIdentity(it)}
        archiveIds=names.map {it.name.removePrefix(PREFIX)}.sorted()
    }
    private fun archive(id:String):File {
        check(UploadBatchRules.canonicalToken(id) && id in archiveIds) {"This preserved sender store is no longer available."}
        return File(profile,PREFIX+id).also {check(it.canonicalFile.parentFile==profile.canonicalFile); directoryIdentity(it)}
    }
    fun archives()=synchronized(gate) {archiveIds.map {id->deletions[id]?.let {PhoneSenderRecoveryArchive(id,true,it.cursor,it.review.entries.size)}?:PhoneSenderRecoveryArchive(id)}}
    fun reviewReset(allowed:()->Boolean):PhoneSenderResetReview=synchronized(gate) {
        val actor=authorized(allowed); check(blocked() && idle()) {"Pause batch sending and review the blocked sender store first."}
        ensureStore(); refreshArchives(); check(archiveIds.size<MAX_ARCHIVES) {"Review and remove a preserved sender store before making another archive."}
        check(directoryIdentity(directory)==identity); val entries=inventory(directory)
        PhoneSenderResetReview(StorageRules.token(),entries.count {it.name!="owner.lock"},entries.sumOf {it.size},entries.count {!known(it.name)}).also {
            resetReview=Reset(it,actor,identity,entries,SystemClock.elapsedRealtime()+120_000)
        }
    }
    fun confirmReset(value:PhoneSenderResetReview,allowed:()->Boolean):String=synchronized(gate) {
        val actor=authorized(allowed); val review=resetReview; resetReview=null
        check(review!=null && review.value==value && review.owner==actor && review.expires>=SystemClock.elapsedRealtime() && idle() && blocked()) {"The sender recovery review changed or expired. Review it again."}
        ensureStore(); refreshArchives(); check(archiveIds.size<MAX_ARCHIVES && directoryIdentity(directory)==review.root && inventory(directory)==review.entries) {"The sender store changed after review. Its files were kept."}
        if(sources.recoveryIssues().isEmpty()) sources.state.value.filter {it.phase==PhoneBatchSourcePhase.READY}.forEach {
            authorized(allowed); sources.noteLocalCopyUncertainty(it.id)
        }
        check(authorized(allowed)==actor)
        val id=StorageRules.token(); val target=File(profile,PREFIX+id)
        try {
            check(!Files.exists(target.toPath(),java.nio.file.LinkOption.NOFOLLOW_LINKS)); innerLease.close(); open=false
            check(directoryIdentity(directory)==review.root && inventory(directory)==review.entries)
            Files.move(directory.toPath(),target.toPath())
            check(directoryIdentity(target)==review.root)
            val opened=openStore(); innerLease=opened.first; identity=opened.second; open=true
            refreshArchives(); reset(); changed(); id
        } catch(e:Exception) {
            block("Sender recovery was interrupted. Preserved stores and private snapshots were kept; restore access and review recovery again.")
            runCatching {ensureStore(); refreshArchives()}; changed(); throw e
        }
    }
    fun reviewRemoval(id:String,allowed:()->Boolean):PhoneSenderArchiveRemovalReview=synchronized(gate) {
        val actor=authorized(allowed); refreshArchives()
        deletions[id]?.let {check(it.review.owner!=actor) {"Continue the already reviewed archive removal first."}; deletions.remove(id)}
        val folder=archive(id); val entries=inventory(folder)
        PhoneSenderArchiveRemovalReview(StorageRules.token(),id,entries.size,entries.sumOf {it.size},entries.count {!known(it.name)}).also {
            removalReviews[id]=Removal(it,actor,directoryIdentity(folder),entries,SystemClock.elapsedRealtime()+120_000)
        }
    }
    fun confirmRemoval(value:PhoneSenderArchiveRemovalReview,allowed:()->Boolean):Boolean=synchronized(gate) {
        val actor=authorized(allowed); val review=removalReviews.remove(value.archive)
        check(review!=null && review.value==value && review.owner==actor && review.expires>=SystemClock.elapsedRealtime()) {"The archive removal review changed or expired. Review it again."}
        val folder=archive(value.archive); check(directoryIdentity(folder)==review.root && inventory(folder)==review.entries) {"The preserved sender store changed. Its files were kept."}
        deletions[value.archive]=Deletion(review); changed(); continueRemoval(value.archive,allowed)
    }
    fun continueRemoval(id:String,allowed:()->Boolean):Boolean=synchronized(gate) {
        val actor=authorized(allowed); val deletion=deletions[id]
        if(deletion==null || deletion.review.owner!=actor) {deletions.remove(id); changed(); error("Review the remaining archive files under the current phone identity.")}
        val folder=archive(id)
        if(directoryIdentity(folder)!=deletion.review.root) {deletions.remove(id); changed(); error("The preserved sender store changed. Its remaining files were kept.")}
        val end=minOf(deletion.review.entries.size,deletion.cursor+BATCH)
        try {
            while(deletion.cursor<end) {
                check(authorized(allowed)==actor); val expected=deletion.review.entries[deletion.cursor]; val file=File(folder,expected.name)
                Os.open(file.path,OsConstants.O_RDONLY or OsConstants.O_NOFOLLOW,0).useOsDescriptor {fd->
                    check(stamp(Os.fstat(fd))==expected.version && stamp(file)==expected.version) {"An archived file changed. Its remaining files were kept."}
                    check(file.delete()) {"An archived file could not be removed."}
                }
                deletion.cursor++
            }
            if(deletion.cursor!=deletion.review.entries.size) {changed(); false} else {
                check(list(folder,1).isEmpty()) {"Reviewed files were removed; additional entries were kept. Review the remaining store again."}
                check(authorized(allowed)==actor && directoryIdentity(folder)==deletion.review.root); check(folder.delete())
                deletions.remove(id); refreshArchives(); changed(); true
            }
        } catch(e:CancellationException) {changed(); throw e}
        catch(e:Exception) {deletions.remove(id); changed(); throw e}
    }
    fun archiveFiles(id:String,start:Int,allowed:()->Boolean):List<PhoneSenderArchiveFile> =synchronized(gate) {
        authorized(allowed); require(start>=0); val entries=inventory(archive(id)); require(start<=entries.size)
        entries.drop(start).take(50).map {PhoneSenderArchiveFile(it.name,it.size,known(it.name))}
    }
    override fun close():Unit=synchronized(gate) {
        if(closed) return; closed=true; resetReview=null; removalReviews.clear(); deletions.clear()
        if(open) innerLease.close(); globalLease.close()
    }
    companion object {
        private const val STORE="outgoing-batch-jobs"; private const val PREFIX="outgoing-batch-sender-backup-"
        private const val MAX_ARCHIVES=8; private const val BATCH=64
        private const val MAX_ENTRIES=40*(UploadBatchRules.MAX_ITEMS+1)*3+1
        private const val MAX_BYTES=3L*40*(16L*1024*1024+64*1024)
    }
}
