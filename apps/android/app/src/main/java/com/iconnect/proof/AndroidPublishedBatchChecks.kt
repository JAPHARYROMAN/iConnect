package com.iconnect.proof

import android.content.Context
import android.net.Uri
import android.os.CancellationSignal
import android.os.ParcelFileDescriptor
import android.provider.DocumentsContract as Docs
import android.system.Os
import android.system.OsConstants
import android.system.StructStat
import androidx.core.net.toUri
import com.iconnect.protocol.*
import kotlinx.coroutines.*
import java.security.MessageDigest
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicReference

internal data class PublishedBatchEntry(val parent:Int,val directory:Boolean,val document:String,val name:String,
    val originalName:String,val parents:List<SafFolderBinding>,val size:Long=0,val hash:String="")
internal data class PublishedBatchSnapshot(val entries:List<PublishedBatchEntry>,val history:List<String>)

/** Read-only checks of retained provider IDs. Never creates, repairs or adopts a document by name. */
internal class AndroidPublishedBatchChecks(private val context:Context,private val grants:AndroidStorageGrants,
    private val snapshot:(AndroidBatchPlans.Row)->PublishedBatchSnapshot,
    private val current:(AndroidBatchPlans.Row,()->Boolean,CancellationSignal)->Unit,
    private val history:(AndroidBatchPlans.Row)->List<String>,
    private val now:()->Long={android.os.SystemClock.elapsedRealtime()}) {
    private data class ProviderMetadata(val name:String,val mime:String?,val flags:Int,val size:Long?,val modified:Long?)
    private data class Verified(val saved:PublishedBatchSnapshot,val stamps:Map<Int,String>,val metadata:List<ProviderMetadata>)
    private class Check(val row:AndroidBatchPlans.Row,val scope:String,val challenge:String,val allowed:()->Boolean,val permit:AutoCloseable) {
        val signal=CancellationSignal()
        val collector=AtomicReference<CancellationSignal?>()
        val collected=CompletableDeferred<Unit>()
        lateinit var job:Job
        var finishedAt:Long?=null
        var saved:PublishedBatchSnapshot?=null
        var stamps:Map<Int,String> = emptyMap()
        var metadata:List<ProviderMetadata> = emptyList()
    }
    private val gate=grants.owner.gate
    private val lifetime=CoroutineScope(SupervisorJob()+Dispatchers.IO)
    private val checks=ConcurrentHashMap<String,Check>()
    private val workers=ConcurrentHashMap<String,Check>()

    fun execute(row:AndroidBatchPlans.Row,scope:String,request:FileRequest,allowed:()->Boolean,rpcSignal:CancellationSignal):PublishedBatchCheck {
        require(scope.isNotEmpty() && UploadBatchRules.canonicalToken(request.mutationId) && request.hasBatchHeader() && request.batchHeader==row.header &&
            !request.hasBatchItemIndex() && !request.hasUpload() && request.content.isEmpty && request.offset==0L && request.length==0 &&
            request.nodeId.isEmpty() && request.destinationId.isEmpty() && request.name.isEmpty() && request.expectedVersion.isEmpty() &&
            request.continuation.isEmpty() && request.query.isEmpty() && request.deletedId.isEmpty()) {"Use a fresh challenge and the original whole-batch intent."}
        if(row.phase!=UploadBatchPlanPhase.UPLOAD_PLAN_COMPLETED || row.retiring || row.retired || row.outcomes.isEmpty() ||
            Rules.decode(row.outcomes).any {it!=1.toByte()})
            throw StorageFailure(FileStatus.CONFLICT,"Choose a fully successful completed batch with retained item history.")
        checks.values.filter {it.finishedAt?.let {end->now()-end !in 0L until COLLECTION_MS}==true}.forEach {old->
            if(checks.remove(old.row.header.id,old)) cancel(old)
        }
        val id=row.header.id; val previous=checks[id]
        if(previous!=null) {
            if(previous.row!=row || previous.scope!=scope || previous.challenge!=request.mutationId)
                throw StorageFailure(FileStatus.CONFLICT,"A different check for this batch is awaiting completion or collection.")
            if(previous.finishedAt!=null) {
                previous.collector.set(rpcSignal)
                try {
                    val saved=previous.saved?:throw IllegalStateException("The saved batch could not be checked.")
                    requireCurrent(previous,rpcSignal)
                    check(history(row)==saved.history) {"The saved batch receipts changed."}
                    check(validateGraph(previous,saved,rpcSignal)==previous.metadata) {"Provider metadata changed before collection."}
                    saved.entries.forEachIndexed {index,entry->
                        requireCurrent(previous,rpcSignal)
                        if(!entry.directory) {
                            val uri=document(row,entry.document)
                            val client=requireNotNull(context.contentResolver.acquireUnstableContentProviderClient(uri))
                            client.use {provider->requireNotNull(provider.openFile(uri,"r",rpcSignal)).use {fd->
                                check(stamp(Os.fstat(fd.fileDescriptor),entry.size)==previous.stamps[index]) {"A saved provider file changed before collection."}
                                check(Os.lseek(fd.fileDescriptor,0,OsConstants.SEEK_SET)==0L)
                            }}
                        }
                    }
                    check(validateGraph(previous,saved,rpcSignal)==previous.metadata)
                    requireCurrent(previous,rpcSignal); check(history(row)==saved.history)
                    return PublishedBatchCheck.newBuilder().setChallenge(previous.challenge).setVerified(true).build()
                } catch(e:Exception) {
                    throw StorageFailure(FileStatus.CONFLICT,"The saved batch could not be verified. Its copies and receipts were kept.")
                } finally {
                    previous.collector.compareAndSet(rpcSignal,null)
                    checks.remove(id,previous); previous.collected.complete(Unit)
                }
            }
        } else {
            if(workers.containsKey(id)) throw StorageFailure(FileStatus.CONFLICT,"The earlier batch check is still releasing its resources.")
            val permit=grants.reserveIncomingVerification()?:throw StorageFailure(FileStatus.RESOURCE_EXHAUSTED,"Incoming verification is busy. Retry shortly.")
            val check=Check(row,scope,request.mutationId,allowed,permit)
            check.job=lifetime.launch(start=CoroutineStart.LAZY) {verify(check)}
            checks[id]=check; workers[id]=check
            check.job.invokeOnCompletion {
                // Cancellation before lazy work starts must still release the shared permit.
                check.signal.cancel(); check.collector.get()?.cancel()
                synchronized(gate) {
                    if(workers.remove(id,check)) check.permit.close()
                    if(checks[id]===check && check.finishedAt==null) check.finishedAt=now()
                }
            }
            check.job.start()
        }
        return PublishedBatchCheck.newBuilder().setChallenge(request.mutationId).build()
    }

    private suspend fun verify(check:Check) {
        try {
            val saved=withWorkDeadline(check) {
                val saved=grants.authorized {requireCurrent(check,check.signal); snapshot(check.row)}
                val metadata=validateGraph(check,saved,check.signal)
                val stamps=linkedMapOf<Int,String>()
                saved.entries.forEachIndexed {index,entry->
                    currentCoroutineContext().ensureActive(); requireCurrent(check,check.signal)
                    if(!entry.directory) stamps[index]=hashFile(check,entry)
                }
                check(validateGraph(check,saved,check.signal)==metadata) {"Provider metadata changed while checking the batch."}
                grants.authorized {requireCurrent(check,check.signal); check(history(check.row)==saved.history)}
                Verified(saved,stamps,metadata)
            }
            synchronized(gate) {
                requireCurrent(check,check.signal); check.saved=saved.saved; check.stamps=saved.stamps; check.metadata=saved.metadata; check.finishedAt=now()
            }
            withTimeout(COLLECTION_MS) {check.collected.await()}
        } catch(_:Exception) {
            // Failure retains the original durable parent and every provider document.
        } finally {
            // Cancel a blocked collection query before attempting to acquire its owner gate.
            check.signal.cancel(); check.collector.get()?.cancel()
            synchronized(gate) {
                if(checks[check.row.header.id]===check) {
                    if(check.saved!=null) checks.remove(check.row.header.id,check)
                    else check.finishedAt=now()
                }
            }
        }
    }

    private suspend fun <T> withWorkDeadline(check:Check,action:suspend ()->T):T=withTimeout(WORK_MS) {
        coroutineScope {
            val finished=java.util.concurrent.atomic.AtomicBoolean(false)
            val watcher=launch(Dispatchers.IO,start=CoroutineStart.UNDISPATCHED) {try {awaitCancellation()} finally {
                if(!finished.get()) check.signal.cancel()
            }}
            try {action()}
            finally {finished.set(true); withContext(NonCancellable) {watcher.cancelAndJoin()}}
        }
    }

    private fun requireCurrent(check:Check,signal:CancellationSignal) = synchronized(gate) {
        signal.throwIfCanceled(); check.signal.throwIfCanceled()
        check(checks[check.row.header.id]===check && (check.finishedAt?.let {now()-it in 0L until COLLECTION_MS}!=false)) {"The batch check scope changed or expired."}
        current(check.row,check.allowed,signal)
    }
    private fun document(row:AndroidBatchPlans.Row,id:String)=Docs.buildDocumentUriUsingTree(row.target.tree.toUri(),id)
    private fun validateGraph(check:Check,saved:PublishedBatchSnapshot,signal:CancellationSignal):List<ProviderMetadata> {
        val row=check.row; val tree=row.target.tree.toUri()
        val metadata=mutableListOf<ProviderMetadata>()
        val children=saved.entries.withIndex().groupBy {it.value.parent}
        saved.entries.forEachIndexed {index,entry->
            requireCurrent(check,signal)
            val destination=SafDestination(context,tree,entry.parents,signal)
            val uri=document(row,entry.document); destination.child(uri)
            context.contentResolver.query(uri,arrayOf(Docs.Document.COLUMN_DISPLAY_NAME,Docs.Document.COLUMN_MIME_TYPE,
                Docs.Document.COLUMN_FLAGS,Docs.Document.COLUMN_SIZE,Docs.Document.COLUMN_LAST_MODIFIED),null,null,null,signal)?.use {cursor->
                check(cursor.moveToFirst() && cursor.getString(0)==entry.name && (cursor.getString(1)==Docs.Document.MIME_TYPE_DIR)==entry.directory &&
                    cursor.getInt(2) and Docs.Document.FLAG_VIRTUAL_DOCUMENT==0 &&
                    (entry.directory || !cursor.isNull(3) && cursor.getLong(3)==entry.size)) {"A saved provider item changed its name, kind or size."}
                metadata+=ProviderMetadata(cursor.getString(0),cursor.getString(1),cursor.getInt(2),if(cursor.isNull(3)) null else cursor.getLong(3),if(cursor.isNull(4)) null else cursor.getLong(4))
                check(!cursor.moveToNext()) {"The provider returned ambiguous metadata rows."}
            }?:error("A saved provider item is unavailable.")
            val grant=synchronized(gate) {requireCurrent(check,signal); grants.get(row.target.grant,row.peer)}
            check(grants.visible(grant,entry.name) && grants.visible(grant,entry.originalName) && entry.parents.all {grants.visible(grant,it.name)})
            if(entry.directory) {
                val folder=SafDestination(context,tree,entry.parents+SafFolderBinding(entry.document,entry.name),signal)
                val expected=children[index].orEmpty().associate {it.value.document to it.value.name}
                check(folder.children(expected.size+1,4*1024*1024)==expected) {"A saved folder has missing or unexpected children."}
            }
            requireCurrent(check,signal)
        }
        return metadata
    }
    private suspend fun hashFile(check:Check,entry:PublishedBatchEntry):String=withReadDescriptor(check,document(check.row,entry.document)) {descriptor->
        ParcelFileDescriptor.AutoCloseInputStream(descriptor).use {input->
            val before=stamp(Os.fstat(descriptor.fileDescriptor),entry.size)
            check(Os.lseek(descriptor.fileDescriptor,0,OsConstants.SEEK_SET)==0L)
            val hash=MessageDigest.getInstance("SHA-256"); val bytes=ByteArray(StorageRules.RANGE); var count=0L
            while(count<entry.size) {
                currentCoroutineContext().ensureActive(); requireCurrent(check,check.signal)
                val read=input.read(bytes,0,minOf(bytes.size.toLong(),entry.size-count).toInt()); check(read>0)
                hash.update(bytes,0,read); count+=read
            }
            requireCurrent(check,check.signal); check(input.read()==-1)
            check(MessageDigest.isEqual(hash.digest(),Rules.decode(entry.hash))) {"Saved provider bytes changed."}
            check(stamp(Os.fstat(descriptor.fileDescriptor),entry.size)==before) {"The provider file changed during hashing."}
            before
        }
    }
    private suspend fun <T> withReadDescriptor(check:Check,uri:Uri,action:suspend (ParcelFileDescriptor)->T):T=coroutineScope {
        val opened=AtomicReference<ParcelFileDescriptor?>(); val finished=java.util.concurrent.atomic.AtomicBoolean(false)
        val watcher=launch(Dispatchers.IO,start=CoroutineStart.UNDISPATCHED) {try {awaitCancellation()} finally {
            if(!finished.get()) {check.signal.cancel(); runCatching {opened.get()?.close()}}
        }}
        try {
            val client=requireNotNull(context.contentResolver.acquireUnstableContentProviderClient(uri))
            client.use {provider->
                // Open the actual document with its cancellation signal, without typed-read conversion.
                val fd=requireNotNull(provider.openFile(uri,"r",check.signal)); opened.set(fd)
                currentCoroutineContext().ensureActive(); fd.use {action(it)}
            }
        } finally {finished.set(true); runCatching {opened.getAndSet(null)?.close()}; withContext(NonCancellable) {watcher.cancelAndJoin()}}
    }
    private fun stamp(s:StructStat,size:Long):String {
        check(OsConstants.S_ISREG(s.st_mode) && s.st_nlink==1L && s.st_size==size) {"A provider file is not a regular single-link file of the saved size."}
        return "${s.st_dev}:${s.st_ino}:${s.st_size}:${s.st_mtim.tv_sec}:${s.st_mtim.tv_nsec}:${s.st_ctim.tv_sec}:${s.st_ctim.tv_nsec}"
    }
    private fun cancel(check:Check) {
        check.signal.cancel(); check.collector.get()?.cancel(); check.collected.complete(Unit); check.job.cancel()
    }
    fun cancelScopeNow(scope:String) {checks.values.filter {it.scope==scope}.forEach {checks.remove(it.row.header.id,it); cancel(it)}}
    fun cancelAllNow() {checks.values.forEach {checks.remove(it.row.header.id,it); cancel(it)}}
    fun endScope(scope:String) {checks.values.filter {it.scope==scope}.forEach {checks.remove(it.row.header.id,it); cancel(it)}}
    fun busy(id:String)=workers.containsKey(id)
    suspend fun whenIdle() {workers.values.map {it.job}.joinAll()}
    suspend fun shutdown() {cancelAllNow(); whenIdle(); checks.clear(); lifetime.cancel()}
    companion object {private const val WORK_MS=10*60_000L; private const val COLLECTION_MS=30_000L}
}
