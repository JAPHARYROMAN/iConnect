package com.iconnect.proof

import android.os.SystemClock
import android.system.Os
import android.system.OsConstants
import android.system.StructStat
import kotlinx.coroutines.CancellationException
import java.io.File
import java.io.FileDescriptor
import java.nio.file.Files
import java.nio.file.LinkOption
import java.security.MessageDigest
import java.util.concurrent.atomic.AtomicLong

data class PhoneActivityBackup(val id:String,val files:Int,val bytes:Long,val modifiedAt:Long)
data class PhoneActivityBackupInventory(val entries:List<PhoneActivityBackup>,val total:Int,val keptFolders:Int)
data class PhoneActivityBackupReview(val id:String,val backup:String,val files:Int,val bytes:Long)

/** Private activity metadata only. Calls are serialized with the activity writer. */
internal class AndroidActivityBackups(private val directory:File,private val rootIdentity:String,
    private val protectedIds:()->Set<String>,private val revision:AtomicLong,private val currentContext:()->String,
    private val available:()->Boolean,private val elapsed:()->Long=SystemClock::elapsedRealtime) {
    private data class Entry(val name:String,val version:String,val bytes:Long,val hash:String)
    private data class Location(val shelf:String,val archive:String)
    private data class Review(val value:PhoneActivityBackupReview,val context:String,val revision:Long,val expires:Long,val location:Location,val files:List<Entry>)
    private var pending:Review?=null
    private fun activityName(name:String)=name in setOf("activity.json","activity.json.new","activity.json.bak")
    private fun directoryId(file:File):String {val stat=Os.lstat(file.path); check(OsConstants.S_ISDIR(stat.st_mode)) {"Redirected activity storage was kept."}; return "${stat.st_dev}:${stat.st_ino}"}
    private fun root() {check(directoryId(directory)==rootIdentity) {"Activity storage changed. Its files were kept."}}
    private fun actor(continuing:()->Boolean):String {
        if(!available() || !continuing()) throw CancellationException("Open the activity-backup review to continue.")
        return currentContext().also {check(it.isNotEmpty()) {"The local owner context is unavailable."}}
    }
    private fun check(context:String,generation:Long,continuing:()->Boolean) {
        check(actor(continuing)==context && revision.get()==generation) {"This activity-backup review lost its owner or screen context. Review it again."}
    }
    private fun children(parent:File,maximum:Int):List<File> = Files.newDirectoryStream(parent.toPath()).use {stream->
        stream.asSequence().map {it.toFile()}.take(maximum+1).toList().sortedBy {it.name}}
    private fun shelf():File? {
        root(); val shelf=File(directory,"recovery")
        if(!Files.exists(shelf.toPath(),LinkOption.NOFOLLOW_LINKS)) return null
        check(shelf.canonicalFile.parentFile==directory.canonicalFile); directoryId(shelf); return shelf
    }
    private fun archive(shelf:File,id:String):File {
        require(Rules.tokenPattern.matches(id)) {"Choose a current activity backup."}
        return File(shelf,id).also {check(it.canonicalFile.parentFile==shelf.canonicalFile); directoryId(it)}
    }
    private fun names(archive:File)=children(archive,3).map {it.name}
    private fun requireNames(names:List<String>) {check(names.size in 1..3 && names.all(::activityName)) {"Unrecognized backup contents were kept."}}
    private fun stamp(stat:StructStat):String {
        check(OsConstants.S_ISREG(stat.st_mode) && stat.st_nlink==1L) {"Linked or redirected metadata was kept."}
        check(stat.st_size in 0..64L*1024*1024) {"This backup exceeds the 64 MiB per-file review limit. It was kept."}
        return "${stat.st_dev}:${stat.st_ino}:${stat.st_size}:${stat.st_mtim.tv_sec}:${stat.st_mtim.tv_nsec}:${stat.st_ctim.tv_sec}:${stat.st_ctim.tv_nsec}"
    }
    fun inspect(start:Int,continuing:()->Boolean):PhoneActivityBackupInventory {
        val context=actor(continuing); val generation=revision.get(); val protected=protectedIds(); val shelf=shelf()
        val result=mutableListOf<PhoneActivityBackup>(); var kept=0
        if(shelf!=null) {
            val folders=children(shelf,256); check(folders.size<=256) {"Recovery inventory exceeds its 256-folder limit. No backup was removed."}
            folders.forEach {folder->
                check(context,generation,continuing)
                if(!Rules.tokenPattern.matches(folder.name) || folder.name in protected) {kept++; return@forEach}
                try {
                    val archive=archive(shelf,folder.name); val names=names(archive)
                    if(names.isEmpty()) {kept++; return@forEach}
                    if(names.size<=3 && names.none(::activityName)) return@forEach
                    requireNames(names); var bytes=0L; var modified=0L
                    names.forEach {name->val file=File(archive,name); check(file.canonicalFile.parentFile==archive.canonicalFile)
                        val stat=Os.lstat(file.path); stamp(stat); bytes+=stat.st_size
                        val time=Math.addExact(Math.multiplyExact(stat.st_mtim.tv_sec,1000),stat.st_mtim.tv_nsec/1_000_000)
                        check(time in 0..StorageRules.MAX_TIMESTAMP); modified=maxOf(modified,time)}
                    result.add(PhoneActivityBackup(folder.name,names.size,bytes,modified))
                } catch(e:CancellationException) {throw e}
                catch(_:Exception) {kept++}
            }
        }
        check(context,generation,continuing); root(); require(start in 0..result.size)
        return PhoneActivityBackupInventory(result.sortedWith(compareByDescending<PhoneActivityBackup> {it.modifiedAt}.thenBy {it.id}).drop(start).take(50),result.size,kept)
    }
    private fun read(file:File,fd:FileDescriptor,context:String,generation:Long,continuing:()->Boolean):Entry {
        val before=Os.fstat(fd); val version=stamp(before); check(stamp(Os.lstat(file.path))==version)
        Os.lseek(fd,0,OsConstants.SEEK_SET); val bytes=ByteArray(64*1024); val hash=MessageDigest.getInstance("SHA-256"); var offset=0L
        while(offset<before.st_size) {
            check(context,generation,continuing)
            val count=Os.read(fd,bytes,0,minOf(bytes.size.toLong(),before.st_size-offset).toInt())
            check(count>0) {"The backup changed during review."}; hash.update(bytes,0,count); offset+=count
        }
        check(context,generation,continuing)
        check(stamp(Os.fstat(fd))==version && stamp(Os.lstat(file.path))==version) {"The backup changed during review."}
        return Entry(file.name,version,before.st_size,Rules.hex(hash.digest()))
    }
    fun review(id:String,continuing:()->Boolean):PhoneActivityBackupReview {
        pending=null; val generation=revision.incrementAndGet(); val context=actor(continuing)
        check(id !in protectedIds()) {"This backup is needed by current recovery. Finish that review first."}
        val shelf=shelf()?:error("The activity backup is no longer available."); val archive=archive(shelf,id)
        val location=Location(directoryId(shelf),directoryId(archive)); val members=names(archive); requireNames(members)
        val files=members.map {name->val file=File(archive,name); check(file.canonicalFile.parentFile==archive.canonicalFile)
            Os.open(file.path,OsConstants.O_RDONLY or OsConstants.O_NOFOLLOW or OsConstants.O_CLOEXEC,0).useOsDescriptor {fd->read(file,fd,context,generation,continuing)}}
        root(); check(names(archive)==members && Location(directoryId(shelf),directoryId(archive))==location)
        check(id !in protectedIds()); check(context,generation,continuing)
        val value=PhoneActivityBackupReview(StorageRules.token(),id,files.size,files.sumOf {it.bytes})
        pending=Review(value,context,generation,elapsed()+120_000,location,files); return value
    }
    fun confirm(value:PhoneActivityBackupReview,continuing:()->Boolean) {
        val captured=pending; pending=null
        check(captured!=null && captured.value==value && captured.expires>=elapsed()) {"This activity-backup review changed, expired or was already used. Review it again."}
        val continuingReview={val active=continuing(); check(captured.expires>=elapsed()) {"This activity-backup review expired. Review remaining metadata again."}; active}
        check(captured.context,captured.revision,continuingReview)
        check(value.backup !in protectedIds()) {"Current recovery still needs this backup. It was kept."}
        val shelf=shelf()?:error("The activity backup is no longer available."); val archive=archive(shelf,value.backup)
        fun location() {root(); check(Location(directoryId(shelf),directoryId(archive))==captured.location) {"The backup location changed. Its contents were kept."}}
        location(); check(names(archive)==captured.files.map {it.name}) {"The backup inventory changed. Its contents were kept."}
        val held=mutableListOf<Pair<Entry,FileDescriptor>>()
        try {
            captured.files.forEach {entry->
                val file=File(archive,entry.name); check(file.canonicalFile.parentFile==archive.canonicalFile)
                val fd=Os.open(file.path,OsConstants.O_RDONLY or OsConstants.O_NOFOLLOW or OsConstants.O_CLOEXEC,0); held.add(entry to fd)
                check(read(file,fd,captured.context,captured.revision,continuingReview)==entry) {"The reviewed metadata changed. It was kept."}
            }
            location(); check(names(archive)==captured.files.map {it.name}) {"The backup inventory changed. Its contents were kept."}
            held.forEach {(entry,fd)->
                check(captured.context,captured.revision,continuingReview); location(); check(value.backup !in protectedIds())
                val file=File(archive,entry.name)
                check(stamp(Os.fstat(fd))==entry.version && stamp(Os.lstat(file.path))==entry.version) {"A reviewed metadata file changed. It was kept."}
                check(captured.context,captured.revision,continuingReview)
                Os.remove(file.path)
            }
            check(captured.context,captured.revision,continuingReview); location()
            check(children(archive,1).isEmpty()) {"New backup contents were kept for another review."}; Os.remove(archive.path)
        } finally {held.forEach {(_,fd)->if(fd.valid()) Os.close(fd)}}
    }
}
