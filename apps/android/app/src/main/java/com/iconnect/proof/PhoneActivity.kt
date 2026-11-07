package com.iconnect.proof

import android.content.Context
import android.system.Os
import android.system.OsConstants
import kotlinx.coroutines.*
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import org.json.JSONArray
import org.json.JSONObject
import org.json.JSONTokener
import java.io.File
import java.io.RandomAccessFile
import java.util.concurrent.atomic.AtomicLong

enum class PhoneActivityKind {
    CONNECTION,PAIRING,REVOCATION,SHARING,SETTINGS,DOWNLOAD,LOCAL_COPY,INCOMING_COPY,PRIVATE_COPY,BATCH,BATCH_PREPARE,BATCH_SEND,BATCH_RECEIVE,FILE_REQUEST,FILE_CHANGE,RECOVERY,
    PRESENCE_SETTINGS,RECONNECT_SETTINGS,DESTINATION_SETTINGS,PROGRESS_PRESERVATION,PROGRESS_BACKUP_REMOVAL,INCOMING_PRIVATE_CLEANUP,
    INCOMING_HISTORY_RESET,BATCH_HISTORY_RESET,RECEIVING_PART_CLEANUP,SENDER_STORE_RESET,SOURCE_STORE_RESET,SENDER_ARCHIVE_CLEANUP,SOURCE_ARCHIVE_CLEANUP,
    RECEIVER_HISTORY_RETIREMENT,SENDER_HISTORY_CLEANUP,SENDER_HISTORY_ARCHIVE,SENDER_HISTORY_RESTORE,SENDER_HISTORY_PURGE,COORDINATOR_CONNECTION,DIRECT_CONNECTION,LOCAL_FOLDER_CREATE,LOCAL_FOLDER_HISTORY
}
enum class PhoneActivityOutcome { REQUESTED,ACCEPTED,COMPLETED,REFUSED,FAILED,REVIEW,PAUSED,CANCELED,SKIPPED,COMPLETED_WITH_ISSUES,ENDED }
enum class PhoneConnectionEnd { OWNER,PAUSED,ENDED,FAILED,UNAVAILABLE,SUPERSEDED }
internal val PhoneConnectionEnd.outcome:PhoneActivityOutcome get()=when(this) {
    PhoneConnectionEnd.OWNER->PhoneActivityOutcome.CANCELED; PhoneConnectionEnd.PAUSED->PhoneActivityOutcome.PAUSED
    PhoneConnectionEnd.ENDED,PhoneConnectionEnd.SUPERSEDED->PhoneActivityOutcome.ENDED
    PhoneConnectionEnd.FAILED->PhoneActivityOutcome.FAILED; PhoneConnectionEnd.UNAVAILABLE->PhoneActivityOutcome.REFUSED
}
data class PhoneActivityEvent(val kind:PhoneActivityKind,val outcome:PhoneActivityOutcome,val peer:String="",val bytes:Long=-1,val operation:Int=-1)
data class PhoneActivityEntry(val id:String,val at:Long,val kind:PhoneActivityKind,val outcome:PhoneActivityOutcome,val peer:String,val bytes:Long,val operation:Int,val count:Int=1)
data class PhoneActivityView(val days:Int=30,val entries:List<PhoneActivityEntry> =emptyList(),val ready:Boolean=false,val issue:String="",val canReset:Boolean=false,val dropped:Long=0)
internal fun recordPhoneActivity(sink:(PhoneActivityEvent)->Unit,event:PhoneActivityEvent) {try {sink(event)} catch(_:Exception) {}}

/** Local metadata only, never an authority record. No arbitrary descriptions are accepted. */
internal class PhoneActivityCore(context:Context,private val now:()->Long=System::currentTimeMillis):AutoCloseable {
    companion object {val RETENTION=setOf(0,1,7,30,90); const val LIMIT=500}
    private val directory=File(context.filesDir,"local-activity")
    private val path=File(directory,"activity.json")
    private val file=PrivateJsonFile(path,1024*1024)
    private val recovery=StateRecovery(directory)
    private val rootIdentity:String
    private val ownerFile:RandomAccessFile
    private val lease:java.nio.channels.FileLock
    private var version=""
    private var days=30
    private var entries=emptyList<PhoneActivityEntry>()
    private var durable=entries
    private var dirty=false
    private var issue=""
    private var closed=false
    init {
        check(directory.mkdir() || directory.isDirectory)
        check(directory.canonicalFile.parentFile==context.filesDir.canonicalFile)
        val root=Os.lstat(directory.path); check(OsConstants.S_ISDIR(root.st_mode)); rootIdentity="${root.st_dev}:${root.st_ino}"
        val lock=File(directory,"owner.lock")
        if(lock.exists()) stamp(lock)
        ownerFile=RandomAccessFile(lock,"rw")
        try {check(stamp(lock)==stamp(ownerFile.fd)); lease=ownerFile.channel.tryLock()?:error("Activity storage is already open.")}
        catch(e:Exception) {ownerFile.close(); throw e}
        try {
            check(listOf(".new",".bak").none {File(path.path+it).exists()})
            if(path.exists()) {
                val loadedVersion=stamp(path); val parser=JSONTokener(file.read())
                val rootJson=parser.nextValue() as? JSONObject?:error("Activity metadata is invalid.")
                check(parser.nextClean()=='\u0000' && stamp(path)==loadedVersion)
                check(rootJson.keys().asSequence().toSet()==setOf("schema","days","entries") && integer(rootJson,"schema")==1L)
                days=boundedInt(rootJson,"days",0..90); check(days in RETENTION)
                val rows=rootJson.getJSONArray("entries"); check(rows.length()<=LIMIT)
                entries=(0 until rows.length()).map {index->val row=rows.getJSONObject(index)
                    check(row.keys().asSequence().toSet()==setOf("id","at","kind","outcome","peer","bytes","operation","count"))
                    PhoneActivityEntry(string(row,"id"),integer(row,"at"),PhoneActivityKind.valueOf(string(row,"kind")),PhoneActivityOutcome.valueOf(string(row,"outcome")),
                        string(row,"peer"),integer(row,"bytes"),boundedInt(row,"operation",-1..Int.MAX_VALUE),boundedInt(row,"count",1..1_000_000)).also(::validate)}
                check(entries.map {it.id}.distinct().size==entries.size && stamp(path)==loadedVersion); version=loadedVersion; durable=entries
                val kept=retained(entries); if(kept!=entries) {entries=kept; dirty=true; flush()}
            } else {check(!file.exists()); dirty=true; flush()}
        } catch(_:Exception) {block()}
    }
    private fun stamp(stat:android.system.StructStat):String {check(OsConstants.S_ISREG(stat.st_mode) && stat.st_nlink==1L); return "${stat.st_dev}:${stat.st_ino}:${stat.st_size}:${stat.st_mtim.tv_sec}:${stat.st_mtim.tv_nsec}:${stat.st_ctim.tv_sec}:${stat.st_ctim.tv_nsec}"}
    private fun stamp(file:File)=stamp(Os.lstat(file.path))
    private fun stamp(fd:java.io.FileDescriptor)=stamp(Os.fstat(fd))
    private fun string(row:JSONObject,key:String)=row.get(key) as? String?:error("Activity metadata is invalid.")
    private fun integer(row:JSONObject,key:String):Long=when(val value=row.get(key)) {is Int->value.toLong(); is Long->value; else->error("Activity metadata is invalid.")}
    private fun boundedInt(row:JSONObject,key:String,range:IntRange):Int {val value=integer(row,key); check(value>=range.first && value<=range.last); return value.toInt()}
    private fun checkRoot() {check(!closed) {"Activity storage is closed."}; val current=Os.lstat(directory.path); check(OsConstants.S_ISDIR(current.st_mode) && "${current.st_dev}:${current.st_ino}"==rootIdentity)}
    private fun validate(row:PhoneActivityEntry) {
        check(Rules.tokenPattern.matches(row.id) && row.at in 0..StorageRules.MAX_TIMESTAMP && (row.peer.isEmpty() || row.peer.matches(Regex("[a-f0-9]{64}"))) &&
            row.bytes in -1..Long.MAX_VALUE && row.count in 1..1_000_000 && (row.operation==-1 || com.iconnect.protocol.FileOperation.forNumber(row.operation)!=null))
    }
    private fun retained(rows:List<PhoneActivityEntry>):List<PhoneActivityEntry> =if(days==0) emptyList() else rows.filter {it.at>=now()-days*86_400_000L}.takeLast(LIMIT)
    private fun block() {entries=durable; dirty=false; if(issue.isEmpty()) {issue="Local activity could not be saved or validated. Recent events may be missing. Preserve and reset its history to retry."; recovery.add(path,issue)}}
    fun view(dropped:Long=0)=PhoneActivityView(days,entries.asReversed(),true,issue,recovery.all().let {it.isNotEmpty() && it.all {item->item.canArchive}},dropped)
    fun append(event:PhoneActivityEvent):Boolean {
        if(issue.isNotEmpty()) return false
        if(days==0) return true
        checkRoot(); val peer=event.peer.takeIf {it.matches(Regex("[a-f0-9]{64}"))}?:""
        val at=now(); check(at in 0..StorageRules.MAX_TIMESTAMP)
        val row=PhoneActivityEntry(StorageRules.token(),at,event.kind,event.outcome,peer,event.bytes.takeIf {it>=0}?:-1,event.operation)
        validate(row); val kept=retained(entries); val last=kept.lastOrNull()
        entries=if(last!=null && at>=last.at && at-last.at<=1000 && last.kind==row.kind && last.outcome==row.outcome && last.peer==row.peer && last.operation==row.operation && last.count<1_000_000)
            kept.dropLast(1)+last.copy(count=last.count+1,bytes=if(last.bytes<0 || row.bytes<0 || last.bytes>Long.MAX_VALUE-row.bytes) -1 else last.bytes+row.bytes)
        else (kept+row).takeLast(LIMIT)
        dirty=true
        return true
    }
    fun flush() {
        if(!dirty) return
        try {
            checkRoot(); check(issue.isEmpty()); check(listOf(".new",".bak").none {File(path.path+it).exists()})
            if(version.isNotEmpty()) check(stamp(path)==version) else check(!path.exists())
            val rows=JSONArray(); entries.forEach {row->rows.put(JSONObject().put("id",row.id).put("at",row.at).put("kind",row.kind.name).put("outcome",row.outcome.name)
                .put("peer",row.peer).put("bytes",row.bytes).put("operation",row.operation).put("count",row.count))}
            file.write(JSONObject().put("schema",1).put("days",days).put("entries",rows).toString()); version=stamp(path); durable=entries; dirty=false
        } catch(e:Exception) {block(); throw e}
    }
    fun configure(value:Int) {require(value in RETENTION); check(issue.isEmpty()); val old=days; try {days=value; entries=retained(entries); dirty=true; flush()} catch(e:Exception) {days=old; throw e}}
    fun clear() {check(issue.isEmpty()); entries=emptyList(); dirty=true; flush()}
    fun failed()=block()
    fun reset() {
        checkRoot(); check(issue.isNotEmpty()); val items=recovery.all(); check(items.isNotEmpty() && items.all {it.canArchive})
        items.forEach {recovery.archive(it.id)}; entries=emptyList(); durable=entries; days=30; version=""; issue=""; dirty=true; flush()
    }
    fun backupManager(revision:AtomicLong,currentContext:()->String,available:()->Boolean,elapsed:()->Long=android.os.SystemClock::elapsedRealtime)=
        AndroidActivityBackups(directory,rootIdentity,{checkRoot(); recovery.all().map {it.id}.toSet()},revision,currentContext,available,elapsed)
    override fun close() {try {flush()} finally {closed=true; lease.release(); ownerFile.close()}}
}

/** Bounded write-behind avoids storage I/O under authorization or provider locks. */
internal class PhoneActivity(context:Context,backupContext:()->String={"local"},backupAvailable:()->Boolean={true},now:()->Long=System::currentTimeMillis) {
    private data class Command(val event:PhoneActivityEvent?=null,val action:String="",val days:Int=30,val reply:CompletableDeferred<Unit>?=null,
        val backup:((AndroidActivityBackups)->Unit)?=null)
    private val writer=CoroutineScope(SupervisorJob()+Dispatchers.IO)
    private val queue=Channel<Command>(64)
    private val dropped=java.util.concurrent.atomic.AtomicLong()
    private val closed=java.util.concurrent.atomic.AtomicBoolean()
    private val lifecycle=Any()
    private val commands=Mutex()
    private val backupRevision=AtomicLong()
    private val mutable=MutableStateFlow(PhoneActivityView())
    val state=mutable.asStateFlow()
    private val work=writer.launch {
        val core=runCatching {PhoneActivityCore(context,now)}.getOrElse {mutable.value=PhoneActivityView(ready=true,issue="Local activity storage is unavailable. Restore private storage access and reopen iConnect."); null}
        val backups=core?.backupManager(backupRevision,backupContext,{!closed.get() && backupAvailable()})
        if(core!=null) mutable.value=core.view()
        for(first in queue) {
            val batch=mutableListOf(first)
            if(first.event!=null) {delay(100); repeat(63) {queue.tryReceive().getOrNull()?.let(batch::add)}}
            var stopping=false
            try {
                check(core!=null) {"Local activity storage is unavailable."}
                for(command in batch) {
                    if(command.backup!=null) {command.backup.invoke(requireNotNull(backups)); continue}
                    command.event?.let {if(!core.append(it)) dropped.incrementAndGet()}
                    when(command.action) {"configure"->core.configure(command.days); "clear"->core.clear(); "reset"->core.reset(); "close"->stopping=true}
                }
                core.flush(); val updated=core.view(dropped.get()); mutable.update {updated.copy(dropped=maxOf(updated.dropped,it.dropped))}; batch.forEach {it.reply?.complete(Unit)}
            } catch(e:Exception) {
                core?.failed()
                dropped.addAndGet(batch.count {it.event!=null}.toLong()); val updated=core?.view(dropped.get())
                mutable.update {current->(updated?:current).copy(dropped=maxOf(dropped.get(),current.dropped))}
                batch.forEach {it.reply?.completeExceptionally(e)}; stopping=batch.any {it.action=="close"}
            }
            if(stopping) break
        }
        runCatching {core?.close()}
    }
    fun record(event:PhoneActivityEvent) {
        val accepted=synchronized(lifecycle) {
            if(closed.get() || state.value.ready && state.value.days==0) return
            queue.trySend(Command(event)).isSuccess
        }
        if(!accepted) {val lost=dropped.incrementAndGet(); mutable.update {it.copy(dropped=maxOf(it.dropped,lost))}}
    }
    private suspend fun action(name:String,days:Int=30) {
        val reply=CompletableDeferred<Unit>()
        commands.withLock {check(!closed.get()); queue.send(Command(action=name,days=days,reply=reply))}
        reply.await()
    }
    suspend fun configure(days:Int) {require(days in PhoneActivityCore.RETENTION); action("configure",days)}
    suspend fun clear()=action("clear")
    suspend fun reset()=action("reset")
    suspend fun flush()=action("flush")
    private suspend fun <T> backupOperation(operation:(AndroidActivityBackups,()->Boolean)->T):T {
        val caller=currentCoroutineContext().job; val result=CompletableDeferred<T>(); val accepted=CompletableDeferred<Unit>()
        commands.withLock {
            check(!closed.get())
            queue.send(Command(action="backup",reply=accepted,backup={manager->
                try {result.complete(operation(manager) {caller.isActive})} catch(e:Exception) {result.completeExceptionally(e)}
            }))
        }
        accepted.await(); return result.await()
    }
    suspend fun inspectBackups(start:Int,continuing:()->Boolean={true})=backupOperation {manager,active->manager.inspect(start) {active() && continuing()}}
    suspend fun reviewBackup(id:String,continuing:()->Boolean={true})=backupOperation {manager,active->manager.review(id) {active() && continuing()}}
    suspend fun confirmBackup(value:PhoneActivityBackupReview,continuing:()->Boolean={true})=backupOperation {manager,active->manager.confirm(value) {active() && continuing()}}
    fun cancelBackupReview() {backupRevision.incrementAndGet()}
    suspend fun close()=withContext(NonCancellable) {
        cancelBackupReview()
        val reply=commands.withLock {
            if(synchronized(lifecycle) {closed.compareAndSet(false,true)}) CompletableDeferred<Unit>().also {queue.send(Command(action="close",reply=it))} else null
        }
        if(reply!=null) runCatching {reply.await()}
        work.join(); queue.close(); writer.cancel()
    }
}
