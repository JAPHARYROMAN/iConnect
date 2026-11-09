package com.iconnect.proof

import android.content.Context
import android.system.Os
import android.system.OsConstants
import com.google.protobuf.ByteString
import com.iconnect.protocol.*
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import org.json.JSONObject
import java.io.File
import java.io.IOException
import java.io.RandomAccessFile
import java.nio.file.Files

enum class PhoneBatchSendingPhase { READY, ADMITTING, SENDING, PAUSED, REVIEW, STOPPING, STOPPED, COMPLETED }
data class PhoneBatchSend(val id:String,val source:String,val owner:String,val peer:String,val pairing:String,val sourceHash:String,val items:Int,val bytes:Long,val encodedLength:Int,
    val phase:PhoneBatchSendingPhase=PhoneBatchSendingPhase.READY,val header:String="",val next:Int=0,val attempted:Boolean=false,val offset:Long=0,
    val outcomes:String,val receiverOutcomes:String="",val effectsStarted:Boolean=false,val destination:String="",val message:String="Private batch ready. Choose an approved receiving folder.",val active:Boolean=false,
    val archived:Boolean=false,val purging:Boolean=false,val purgeCursor:Int=0,val purgeCount:Int=0,val label:String="")
data class PhoneBatchItemResult(val index:Int,val phase:UploadBatchItemPhase,val name:String,val message:String)
data class PhoneBatchPayload(val index: Int, val name: String, val size: Long, val offset: Long, val estimate: PhonePayloadRateEstimate,
    val batchRemaining: Long, val batchEstimate: PhonePayloadRateEstimate)
enum class PhoneBatchHistoryAction { ARCHIVE, RESTORE, PURGE }
data class PhoneBatchHistoryReview(val id:String,val job:String,val action:PhoneBatchHistoryAction,val source:String,val results:Int,val bytes:Long,val receiverMayHaveCopies:Boolean)

/** Native private snapshots and durable requests. No original source URI is retained here. */
internal class PhoneBatchSender(private val context:Context,private val owner:AndroidOwnerPolicy,private val sources:AndroidBatchSources,
    private val payloadNow: () -> Long = android.os.SystemClock::elapsedRealtime) {
    internal var activity:(PhoneActivityEvent)->Unit={}
    private val gate=owner.gate
    private val directory=File(context.filesDir,"outgoing-batch-jobs")
    private var recovery=StateRecovery(directory)
    private val rows=linkedMapOf<String,PhoneBatchSend>()
    private val versions=mutableMapOf<String,String>()
    private val resultBytes=mutableMapOf<String,Long>()
    private val work=mutableMapOf<String,Job>()
    private data class PayloadSample(val index: Int, val name: String, val size: Long, val offset: Long, val run: String,
        val batchRemaining: Long, val batchAcknowledged: Long) {
        val scope get() = "$run/$index"
    }
    // The validated manifest orders parents before children, including noncontiguous branches.
    private class PendingPayload(manifest: UploadBatchManifest, row: PhoneBatchSend) {
        private val branchBytes = LongArray(manifest.itemsCount)
        var remaining = 0L
            private set

        init {
            val excluded = BooleanArray(manifest.itemsCount)
            val outcomes = Rules.decode(row.outcomes)
            manifest.itemsList.forEachIndexed { index, item ->
                excluded[index] = item.error.isNotEmpty() || item.parent >= 0 && excluded[item.parent] ||
                    index < row.next && item.isDirectory && outcomes[index].toInt() != UploadBatchItemPhase.BATCH_ITEM_SUCCEEDED.number
                if (index >= row.next && !excluded[index] && !item.isDirectory) {
                    branchBytes[index] = item.size
                    remaining += item.size
                }
            }
            for (index in branchBytes.indices.reversed()) {
                val parent = manifest.getItems(index).parent
                if (parent >= 0) branchBytes[parent] += branchBytes[index]
            }
        }

        fun removeBranch(index: Int) { remaining -= branchBytes[index] }
    }
    private val payload = mutableMapOf<String, PayloadSample>()
    private val payloadRates = PhonePayloadRateTracker()
    private val batchPayloadRates = PhonePayloadRateTracker()
    private var payloadLimit = 0L
    private val completions=mutableMapOf<String,CompletableDeferred<Unit>>()
    private data class HistoryFile(val name:String,val version:String,val size:Long)
    private data class HistoryReview(val value:PhoneBatchHistoryReview,val parent:String,val files:List<HistoryFile>,val expires:Long)
    private val historyReviews=linkedMapOf<String,HistoryReview>()
    private val lifetime=CoroutineScope(SupervisorJob()+Dispatchers.IO)
    private val mutable=MutableStateFlow<List<PhoneBatchSend>>(emptyList())
    val state=mutable.asStateFlow()
    private var closed=false
    private val storage=PhoneBatchSenderRecovery(context,owner,sources,{work.isEmpty()},{recovery.needed()},
        {message->recovery.block(message)}, {rows.clear(); versions.clear(); resultBytes.clear(); historyReviews.clear(); recovery=StateRecovery(directory); emit()})
    private val identity get()=storage.identity
    val recoveryChanges get()=storage.changes
    init {
        synchronized(gate) {runCatching {
            val paths=StateRecovery.journals(directory,MAX_RECORDS*(UploadBatchRules.MAX_ITEMS+1)); val metadataNames=paths.map {it.name}.toSet()
            val parents=paths.filter {it.name.endsWith(".json") && UploadBatchRules.canonicalToken(it.name.removeSuffix(".json"))}; require(parents.size<=MAX_RECORDS)
            parents.forEach {file->val row=decode(JSONObject(PrivateJsonFile(file,RECORD_LIMIT).read())); require(file.name==row.id+".json" && rows.put(row.id,row)==null); versions[row.id]=stamp(file)}
            require(rows.values.count {it.archived}<=MAX_HISTORY && rows.values.count {!it.archived}<=UploadBatchRules.MAX_PLANS && rows.values.filter {!it.archived}.groupBy {it.peer}.values.all {it.size<=UploadBatchRules.MAX_PEER_PLANS})
            rows.values.toList().forEach {row->
                if(!row.purging) repeat(row.next) {if(row.archived) require(resultFile(row.id,it).name in metadataNames) else readResult(row,it)}
                if(!row.archived && row.phase in setOf(PhoneBatchSendingPhase.ADMITTING,PhoneBatchSendingPhase.SENDING)) rows[row.id]=row.copy(phase=PhoneBatchSendingPhase.PAUSED,message="Sending was interrupted. Reconnect and explicitly check the original receiver receipts.")
            }
            paths.filter {it !in parents}.forEach {file->val stem=file.name.removeSuffix(".json"); val split=stem.indexOf('.'); require(split>0)
                val row=requireNotNull(rows[stem.take(split)]); val text=stem.substring(split+1); val index=text.toInt(); require(text==index.toString() && index in 0 until row.items && index<=row.next)
                if(row.purging) require(index>=row.purgeCursor && index<row.purgeCount)
                stamp(file); val size=Os.lstat(file.path).st_size; require(size<=RESULT_LIMIT)
                if(!row.archived) readResult(row,index,index<row.next); resultBytes[row.id]=(resultBytes[row.id]?:0)+size; require(resultBytes.getValue(row.id)<=RESULTS_LIMIT)}
            rows.values.filter {it.purging}.forEach(::validatePurgeInventory)
            val names=children(); val expected=paths.map {it.name}.toSet()+"owner.lock"; require(names.all {it.name in expected && !it.isDirectory} && names.size<=expected.size)
            rows.values.filter {it.header.isNotEmpty() && it.owner==owner.deviceId}.forEach {row->
                if(sources.state.value.any {it.id==row.source && it.owner==row.owner && it.peer==row.peer && it.pairing==row.pairing && it.hash==row.sourceHash && it.phase==PhoneBatchSourcePhase.READY})
                    sources.noteReceiverIntent(row.source,row.peer,row.pairing,row.sourceHash)
            }
        }.onFailure {rows.clear(); recovery.block("Outgoing batch intents need local review. Private snapshots and receiver copies were kept; new sends are blocked.")}; emit()}
    }
    private fun directoryIdentity()=Os.lstat(directory.path).let {check(OsConstants.S_ISDIR(it.st_mode)); "${it.st_dev}:${it.st_ino}"}
    private fun stamp(file:File)=stamp(Os.lstat(file.path))
    private fun stamp(x:android.system.StructStat):String {check(OsConstants.S_ISREG(x.st_mode) && x.st_nlink==1L); return "${x.st_dev}:${x.st_ino}:${x.st_size}:${x.st_mtim.tv_sec}:${x.st_mtim.tv_nsec}:${x.st_ctim.tv_sec}:${x.st_ctim.tv_nsec}"}
    private fun children()=Files.newDirectoryStream(directory.toPath()).use {it.asSequence().map {p->p.toFile()}.take(MAX_RECORDS*(UploadBatchRules.MAX_ITEMS+1)+2).toList()}
    private fun resultFile(id:String,index:Int)=File(directory,"$id.$index.json")
    private fun header(row:PhoneBatchSend)=UploadBatchHeader.parseFrom(Rules.decode(row.header))
    private fun encode(row:PhoneBatchSend)=JSONObject().put("schema",2).put("id",row.id).put("source",row.source).put("owner",row.owner).put("peer",row.peer).put("pairing",row.pairing)
        .put("sourceHash",row.sourceHash).put("items",row.items).put("bytes",row.bytes).put("encodedLength",row.encodedLength).put("phase",row.phase.name).put("header",row.header)
        .put("next",row.next).put("attempted",row.attempted).put("offset",row.offset).put("outcomes",row.outcomes).put("receiverOutcomes",row.receiverOutcomes).put("effectsStarted",row.effectsStarted)
        .put("destination",row.destination).put("message",row.message).put("archived",row.archived).put("purging",row.purging).put("purgeCursor",row.purgeCursor).put("purgeCount",row.purgeCount).put("label",row.label)
    private fun decode(x:JSONObject):PhoneBatchSend {
        val schema=x.getInt("schema"); require(schema in 1..2 && (schema!=1 || listOf("archived","purging","purgeCursor","purgeCount").none(x::has)))
        return PhoneBatchSend(x.getString("id"),x.getString("source"),x.getString("owner"),x.getString("peer"),x.getString("pairing"),x.getString("sourceHash"),x.getInt("items"),x.getLong("bytes"),x.getInt("encodedLength"),
            PhoneBatchSendingPhase.valueOf(x.getString("phase")),x.getString("header"),x.getInt("next"),x.getBoolean("attempted"),x.getLong("offset"),x.getString("outcomes"),x.getString("receiverOutcomes"),
            x.getBoolean("effectsStarted"),x.getString("destination"),x.getString("message"),archived=if(schema==2) x.getBoolean("archived") else false,purging=if(schema==2) x.getBoolean("purging") else false,purgeCursor=if(schema==2) x.getInt("purgeCursor") else 0,purgeCount=if(schema==2) x.getInt("purgeCount") else 0,label=x.optString("label","")).also(::validate)
    }
    private fun validate(row:PhoneBatchSend) {
        require(UploadBatchRules.canonicalToken(row.id) && UploadBatchRules.canonicalToken(row.source) && listOf(row.owner,row.peer).all {it.matches(Regex("[a-f0-9]{64}"))} && UploadBatchRules.canonicalToken(row.pairing) &&
            UploadBatchRules.canonicalToken(row.sourceHash) && row.items in 1..UploadBatchRules.MAX_ITEMS && row.bytes in 0..UploadRules.QUOTA && row.encodedLength in 1..UploadBatchRules.MAX_ENCODED_BYTES &&
            row.next in 0..row.items && row.offset in 0..UploadRules.QUOTA && !(row.next==row.items && row.attempted) && row.destination.length<=255 && row.destination.none(Character::isISOControl) && row.message.length<=512 && row.message.none(Character::isISOControl) && row.label.length<=512 && row.label.none(Character::isISOControl))
        val outcomes=Rules.decode(row.outcomes); require(Rules.b64(outcomes)==row.outcomes && outcomes.size==row.items && outcomes.all {(it.toInt() and 255) in 0..4})
        repeat(row.next) {require(outcomes[it].toInt() in 1..3)}
        require((row.next until row.items).all {outcomes[it].toInt()==0})
        if(row.header.isNotEmpty()) {
            val bytes=Rules.decode(row.header); require(bytes.size<=1024 && Rules.b64(bytes)==row.header); val h=header(row); UploadBatchRules.validateHeader(h)
            require(h.itemCount==row.items && h.totalBytes==row.bytes && h.encodedLength==row.encodedLength && Rules.b64(h.sha256.toByteArray())==row.sourceHash)
        } else require(row.next==0 && !row.attempted && row.offset==0L && !row.effectsStarted && row.phase !in setOf(PhoneBatchSendingPhase.ADMITTING,PhoneBatchSendingPhase.SENDING,PhoneBatchSendingPhase.STOPPING,PhoneBatchSendingPhase.COMPLETED))
        if(row.phase==PhoneBatchSendingPhase.COMPLETED) require(row.next==row.items && row.receiverOutcomes==row.outcomes)
        if(row.receiverOutcomes.isNotEmpty()) {val terminal=Rules.decode(row.receiverOutcomes); require(Rules.b64(terminal)==row.receiverOutcomes && terminal.size==row.items && terminal.all {(it.toInt() and 255) in 0..4} && row.phase in setOf(PhoneBatchSendingPhase.COMPLETED,PhoneBatchSendingPhase.STOPPED) && (0 until row.next).all {terminal[it]==outcomes[it]})}
        if(row.phase==PhoneBatchSendingPhase.STOPPED && row.effectsStarted) require(row.receiverOutcomes.isNotEmpty())
        require(if(row.purging) row.archived && row.purgeCount in row.next..minOf(row.items,row.next+1) && row.purgeCursor in 0..row.purgeCount else row.purgeCursor==0 && row.purgeCount==0)
    }
    private fun ready() {
        check(!closed); recovery.requireHealthy(); check(owner.healthy && directory.canonicalFile.parentFile==context.filesDir.canonicalFile && directoryIdentity()==identity)
        try {rows.values.forEach {check(stamp(File(directory,it.id+".json"))==versions[it.id])}}
        catch(e:Exception) {recovery.block("Outgoing batch ownership changed or is missing. Copies were kept for local review."); emit(); throw IOException("Outgoing batch ownership needs review.",e)}
    }
    private fun current(row:PhoneBatchSend)=row.owner==owner.deviceId && owner.current(row.peer)?.epoch==row.pairing
    private fun authorize(row:PhoneBatchSend)=synchronized(gate) {ready(); check(current(row)) {"This batch belongs to an earlier identity or pairing."}}
    private fun emit() {mutable.value=rows.values.toList().asReversed().map {it.copy(active=it.id in work)}}

    fun readPayloads(limit: Long): Map<String, PhoneBatchPayload> = synchronized(gate) {
        if (payloadLimit != limit) {
            payloadLimit = limit
            payloadRates.clear()
            batchPayloadRates.clear()
        }
        payload.mapNotNull { (id, sample) ->
            val row = rows[id]
            if (work[id]?.isActive != true || row?.phase != PhoneBatchSendingPhase.SENDING || row.next != sample.index || row.offset != sample.offset)
                return@mapNotNull null
            val now = payloadNow()
            val estimate = payloadRates.read(id, sample.scope, sample.size, sample.offset, true, now, limit)
            val batchEstimate = if (sample.batchRemaining == 0L) PhonePayloadRateEstimate(PhonePayloadRateState.RECEIVED)
                else batchPayloadRates.read(id, sample.run, row.bytes, sample.batchAcknowledged, true, now, limit, sample.batchRemaining)
            id to PhoneBatchPayload(sample.index, sample.name, sample.size, sample.offset, estimate, sample.batchRemaining, batchEstimate)
        }.toMap()
    }

    private fun savePayload(row: PhoneBatchSend, run: String, receipt: UploadInfo, planned: Long, acknowledged: Long): PhoneBatchSend {
        synchronized(gate) {
            if (receipt.phase == UploadPhase.UPLOAD_RECEIVING || receipt.offset == receipt.size &&
                (receipt.phase == UploadPhase.UPLOAD_VERIFYING || receipt.phase == UploadPhase.UPLOAD_FINALIZING)) {
                val sample = PayloadSample(row.next, receipt.name, receipt.size, receipt.offset, run, planned - receipt.offset, acknowledged)
                val now = payloadNow()
                payloadRates.observe(row.id, sample.scope, sample.size, sample.offset, true, now)
                batchPayloadRates.observe(row.id, run, row.bytes, acknowledged, true, now)
                payload[row.id] = sample
            }
        }
        return put(row.copy(offset = receipt.offset))
    }

    private fun endPayload(id: String) = synchronized(gate) {
        payload.remove(id)?.let { sample -> payloadRates.observe(id, sample.scope, sample.size, sample.offset, false, payloadNow()) }
    }

    private fun put(row:PhoneBatchSend):PhoneBatchSend=synchronized(gate) {
        val previous=rows[row.id]?.phase
        validate(row); recovery.requireHealthy()
        try {val file=File(directory,row.id+".json"); PrivateJsonFile(file,RECORD_LIMIT).write(encode(row).toString()); rows[row.id]=row; versions[row.id]=stamp(file); emit()
            if(previous!=row.phase) {
                val allCopied=row.receiverOutcomes.isNotEmpty() && Rules.decode(row.receiverOutcomes).all {it.toInt()==1}
                val outcome=when(row.phase) {PhoneBatchSendingPhase.READY->PhoneActivityOutcome.ACCEPTED; PhoneBatchSendingPhase.COMPLETED->if(allCopied) PhoneActivityOutcome.COMPLETED else PhoneActivityOutcome.COMPLETED_WITH_ISSUES; PhoneBatchSendingPhase.STOPPED->PhoneActivityOutcome.CANCELED;
                    PhoneBatchSendingPhase.PAUSED->PhoneActivityOutcome.PAUSED; PhoneBatchSendingPhase.REVIEW->PhoneActivityOutcome.REVIEW; else->null}
                if(outcome!=null) recordPhoneActivity(activity,PhoneActivityEvent(PhoneActivityKind.BATCH_SEND,outcome,row.peer,if(outcome==PhoneActivityOutcome.COMPLETED) row.bytes else -1))
            }; row}
        catch(e:Exception) {recovery.block("Outgoing batch progress could not be saved. Existing snapshots and receiver copies were kept."); emit(); throw e}
    }
    fun queue(id:String,anotherCopy:Boolean=false):String=synchronized(gate) {
        ready(); val source=sources.state.value.single {it.id==id}; check(source.phase==PhoneBatchSourcePhase.READY && source.owner==owner.deviceId && owner.current(source.peer)?.epoch==source.pairing)
        check(rows.size<MAX_RECORDS && rows.values.count {!it.archived}<UploadBatchRules.MAX_PLANS && rows.values.count {!it.archived && it.peer==source.peer}<UploadBatchRules.MAX_PEER_PLANS && rows.values.none {!it.archived && it.source==id && it.phase !in setOf(PhoneBatchSendingPhase.COMPLETED,PhoneBatchSendingPhase.STOPPED)}) {"Outgoing batch slots are full or this source already has a retained send."}
        check(anotherCopy || !source.mayHaveReceiverCopies && rows.values.none {it.source==id && it.header.isNotEmpty()}) {"Review earlier receiver copies and explicitly choose another copy before queueing this snapshot again."}
        val manifest=sources.manifest(id); require(Rules.b64(Rules.digest(UploadBatchRules.encode(manifest)))==source.hash)
        val row=PhoneBatchSend(StorageRules.token(),id,source.owner,source.peer,source.pairing,source.hash,source.items,source.bytes,source.encodedLength,outcomes=Rules.b64(ByteArray(source.items)),label=source.label.ifEmpty {manifest.itemsList.first {it.parent<0}.name}); put(row); row.id
    }
    private fun source(row:PhoneBatchSend):UploadBatchManifest {
        authorize(row); val source=sources.state.value.single {it.id==row.source}
        require(source.owner==row.owner && source.peer==row.peer && source.pairing==row.pairing && source.phase==PhoneBatchSourcePhase.READY && source.hash==row.sourceHash && source.items==row.items && source.bytes==row.bytes && source.encodedLength==row.encodedLength)
        return sources.manifest(row.source)
    }
    fun send(id:String,destination:FileEntry?,allowed:()->Boolean,request:suspend (FileRequest)->FileResponse)=start(id,destination,allowed,request,false)
    fun stop(id:String,allowed:()->Boolean,request:suspend (FileRequest)->FileResponse)=start(id,null,allowed,request,true)
    internal suspend fun verifyPublished(id:String,approval:PeerApproval,allowed:()->Boolean,request:suspend (FileRequest)->FileResponse) {
        verifyPublished(id,approval,allowed,false,request)
    }
    internal suspend fun verifyPublished(id:String,approval:PeerApproval,allowed:()->Boolean,cancelReceiver:Boolean,request:suspend (FileRequest)->FileResponse) {
        val result=CompletableDeferred<Unit>()
        val (job,done)=synchronized(gate) {
            ready(); val row=rows.getValue(id)
            check(current(row) && row.peer==approval.id && row.pairing==approval.epoch && allowed()) {"Connect to the original paired receiver to check this batch."}
            check(!row.archived && !row.purging && row.phase==PhoneBatchSendingPhase.COMPLETED && row.header.isNotEmpty() &&
                row.receiverOutcomes.isNotEmpty() && Rules.decode(row.receiverOutcomes).all {it==1.toByte()}) {"Choose a fully copied completed batch with retained receiver history."}
            check(id !in work && work.size<2) {"This batch or two outgoing batch controls are already active."}
            val pending=lifetime.launch(start=CoroutineStart.LAZY) {
                try {requirePublishedBatchProof(row,allowed,cancelReceiver,request); result.complete(Unit)}
                catch(e:Exception) {result.completeExceptionally(e)}
            }
            val done=CompletableDeferred<Unit>(); work[id]=pending; completions[id]=done
            pending.invokeOnCompletion {failure->
                synchronized(gate) {
                    try {if(work[id]===pending) {work.remove(id); emit()}}
                    finally {completions.remove(id); done.complete(Unit)}
                }
                if(failure!=null) result.completeExceptionally(failure)
            }
            emit(); pending.start(); pending to done
        }
        try {result.await()}
        catch(e:Throwable) {job.cancel(); throw e}
        finally {
            // The result can arrive before the completion callback unregisters
            // the control. Return only after that callback has released it, on
            // success, failure and caller cancellation alike.
            withContext(NonCancellable) {job.join(); done.await()}
        }
    }
    private suspend fun requirePublishedBatchProof(row:PhoneBatchSend,allowed:()->Boolean,cancelReceiver:Boolean,request:suspend (FileRequest)->FileResponse) {
        val h=header(row); val challenge=StorageRules.token(); val started=android.os.SystemClock.elapsedRealtime()
        val value=FileRequest.newBuilder().setOperation(FileOperation.VERIFY_PUBLISHED_BATCH).setTransferId(h.id)
            .setMutationEpoch(h.epoch).setBatchHeader(h).setMutationId(challenge).build()
        var verified=false
        try {
            while(android.os.SystemClock.elapsedRealtime()-started<10*60_000L) {
                currentCoroutineContext().ensureActive(); authorize(row); check(allowed()) {"The authenticated receiving session changed."}
                val reply=PhoneTransferManifests.checked(request,value)
                currentCoroutineContext().ensureActive(); authorize(row); check(allowed()) {"The authenticated receiving session changed."}
                val receipt=UploadBatchRules.checkReceipt(h,reply)
                check(receipt.phase==UploadBatchPlanPhase.UPLOAD_PLAN_COMPLETED && Rules.b64(receipt.itemOutcomes.toByteArray())==row.receiverOutcomes &&
                    (!row.effectsStarted || receipt.effectsStarted) && reply.hasPublishedBatchCheck() && reply.publishedBatchCheck.challenge==challenge && !reply.publishedBatchCheck.canceled) {
                    "The receiver did not return a matching fresh check for this whole batch."
                }
                if(reply.publishedBatchCheck.verified) {verified=true; return}
                delay(300)
            }
            error("The receiver did not finish its saved-batch check before the time limit.")
            } finally {if(cancelReceiver && !verified) cancelPublishedBatchProof(row,allowed,value,request)}
    }
    private suspend fun cancelPublishedBatchProof(row:PhoneBatchSend,allowed:()->Boolean,original:FileRequest,request:suspend (FileRequest)->FileResponse)=withContext(NonCancellable) {
        try {withTimeout(5_000) {
            val value=original.toBuilder().setOperation(FileOperation.CANCEL_PUBLISHED_BATCH_CHECK).build()
            while(true) {
                authorize(row); check(allowed())
                val reply=PhoneTransferManifests.checked(request,value); val receipt=UploadBatchRules.checkReceipt(original.batchHeader,reply)
                check(receipt.phase==UploadBatchPlanPhase.UPLOAD_PLAN_COMPLETED && Rules.b64(receipt.itemOutcomes.toByteArray())==row.receiverOutcomes &&
                    (!row.effectsStarted || receipt.effectsStarted) && reply.hasPublishedBatchCheck() &&
                    reply.publishedBatchCheck.challenge==original.mutationId && !reply.publishedBatchCheck.verified)
                if(reply.publishedBatchCheck.canceled) return@withTimeout
                delay(100)
            }
        }} catch(_:Exception) {
            // A lost stop acknowledgement does not replace the original result.
        }
    }
    private fun start(id:String,destination:FileEntry?,allowed:()->Boolean,request:suspend (FileRequest)->FileResponse,stop:Boolean):Job=synchronized(gate) {
        ready(); val row=rows.getValue(id); check(current(row) && allowed()); check(id !in work && work.size<2) {"This batch or two outgoing batch controls are already active."}
        check(!row.archived) {"Restore this saved history before checking or sending its original receiver plan."}
        if(!stop) {check(row.phase !in setOf(PhoneBatchSendingPhase.COMPLETED,PhoneBatchSendingPhase.STOPPED,PhoneBatchSendingPhase.STOPPING)); if(row.header.isEmpty()) check(destination!=null && destination.isDirectory && destination.permissions and 12==12)}
        val job=lifetime.launch(start=CoroutineStart.LAZY) {run(row,destination,allowed,request,stop)}
        val done=CompletableDeferred<Unit>(); work[id]=job; completions[id]=done
        job.invokeOnCompletion {synchronized(gate) {try {if(work[id]===job) {work.remove(id); emit()}}
            finally {completions.remove(id); done.complete(Unit)}}}; emit(); job.start(); job
    }
    private suspend fun run(initial:PhoneBatchSend,destination:FileEntry?,allowed:()->Boolean,request:suspend (FileRequest)->FileResponse,stop:Boolean) {
        var row=initial
        val payloadRun = StorageRules.token()
        try {
            suspend fun call(value:FileRequest.Builder):FileResponse {
                currentCoroutineContext().ensureActive(); authorize(row); check(allowed()); val input=value.setRequestId(StorageRules.token()).build(); val response=request(input)
                PhoneTransferManifests.validateResponse(response); authorize(row); check(allowed()); if(response.status!=FileStatus.FILE_STATUS_OK) throw StorageFailure(response.status,response.message.ifEmpty {"Check the original receiver receipt before another copy."})
                if(row.header.isNotEmpty() && input.operation!=FileOperation.UPLOAD_BATCH_CONTEXT) {
                    val parent=UploadBatchRules.checkReceipt(header(row),response); require(!row.effectsStarted || parent.effectsStarted) {"The receiver lost its retained effect barrier."}
                    if(parent.effectsStarted && !row.effectsStarted) row=put(row.copy(effectsStarted=true))
                }
                return response
            }
            fun op(operation:FileOperation,index:Int?=null):FileRequest.Builder {val h=header(row); return FileRequest.newBuilder().setOperation(operation).setTransferId(h.id).setMutationEpoch(h.epoch).also {if(index!=null) it.setBatchItemIndex(index)}}
            fun checked(response:FileResponse)=UploadBatchRules.checkReceipt(header(row),response)
            if(stop && row.header.isEmpty()) {put(row.copy(phase=PhoneBatchSendingPhase.STOPPED,message="The unstarted local job stopped. Private snapshots and originals were kept.")); return}
            if(stop) {
                row=put(row.copy(phase=PhoneBatchSendingPhase.STOPPING,message="Stopping the original receiver plan. Published and uncertain copies are kept."))
                while(true) {val result=checked(call(op(FileOperation.STOP_UPLOAD_BATCH))); if(UploadBatchRules.terminal(result.phase)) {acceptTerminal(row,result); return}; require(result.phase==UploadBatchPlanPhase.UPLOAD_PLAN_STOPPING); delay(250)}
            }
            val manifest=source(row); val encoded=UploadBatchRules.encode(manifest); var admitted:UploadBatchReceipt
            if(row.header.isEmpty()) {
                val context=call(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_BATCH_CONTEXT)); require(UploadBatchRules.canonicalToken(context.mutationEpoch) && UploadBatchRules.canonicalToken(requireNotNull(destination).grantId))
                val h=UploadBatchRules.header(MutationRules.newId(),context.mutationEpoch,destination.grantId,manifest)
                row=put(row.copy(header=Rules.b64(h.toByteArray()),destination=destination.name,phase=PhoneBatchSendingPhase.ADMITTING,message="The original receiver intent is saved before admission."))
                sources.noteReceiverIntent(row.source,row.peer,row.pairing,row.sourceHash)
                admitted=checked(call(op(FileOperation.BEGIN_UPLOAD_BATCH).setDestinationId(destination.id).setBatchHeader(h)))
            } else admitted=checked(call(op(FileOperation.UPLOAD_BATCH_STATUS)))
            if(UploadBatchRules.terminal(admitted.phase)) {acceptTerminal(row,admitted); return}
            if(admitted.phase==UploadBatchPlanPhase.UPLOAD_PLAN_STOPPING) {put(row.copy(phase=PhoneBatchSendingPhase.STOPPING,message="The receiver is stopping this original plan. Explicitly continue stopping it.")); return}
            if(admitted.phase==UploadBatchPlanPhase.UPLOAD_PLAN_RECEIVING) {
                var offset=admitted.manifestOffset; require(offset==encoded.size || offset%StorageRules.RANGE==0)
                while(offset<encoded.size) {val count=minOf(StorageRules.RANGE,encoded.size-offset); admitted=checked(call(op(FileOperation.WRITE_UPLOAD_MANIFEST).setOffset(offset.toLong()).setContent(ByteString.copyFrom(encoded,offset,count))))
                    require(admitted.phase==UploadBatchPlanPhase.UPLOAD_PLAN_RECEIVING && admitted.manifestOffset==offset+count); offset+=count}
                admitted=checked(call(op(FileOperation.SEAL_UPLOAD_BATCH)))
            }
            require(admitted.phase==UploadBatchPlanPhase.UPLOAD_PLAN_READY)
            val pendingPayload = PendingPayload(manifest, row)
            var acknowledgedPayload = 0L
            row=put(row.copy(phase=PhoneBatchSendingPhase.SENDING,message="Sending retained private files through their original item bindings."))
            while(row.next<row.items) {
                currentCoroutineContext().ensureActive(); authorize(row); check(allowed()); val index=row.next; val item=manifest.getItems(index); val outcomes=Rules.decode(row.outcomes)
                if(item.error.isNotEmpty() || item.parent>=0 && outcomes[item.parent].toInt()!=1) {
                    row=commit(row,PhoneBatchItemResult(index,if(item.error.isNotEmpty()) UploadBatchItemPhase.BATCH_ITEM_FAILED else UploadBatchItemPhase.BATCH_ITEM_SKIPPED,"",item.error.ifEmpty {"The parent branch was not copied."})); continue
                }
                if(item.isDirectory) {
                    val attempted=row.attempted; if(!attempted) row=put(row.copy(attempted=true,offset=0))
                    val reply=call(op(if(attempted) FileOperation.UPLOAD_BATCH_ITEM_STATUS else FileOperation.CREATE_BATCH_DIRECTORY,index)); val result=UploadBatchRules.checkItemReceipt(row.owner,header(row),index,true,reply)
                    if(result.phase in setOf(UploadBatchItemPhase.BATCH_ITEM_PENDING,UploadBatchItemPhase.BATCH_ITEM_REVIEW)) throw IOException("Directory creation needs review. Its original attempt will not be replayed.")
                    if (result.phase != UploadBatchItemPhase.BATCH_ITEM_SUCCEEDED) pendingPayload.removeBranch(index)
                    row=commit(row,PhoneBatchItemResult(index,result.phase,result.resultingName,result.message)); continue
                }
                sources.openFile(row.source,index) {allowed() && current(row)}.use {source->
                    val descriptor=UploadInfo.newBuilder().setId(UploadBatchRules.childId(row.owner,header(row),index,false)).setName(item.name).setSize(item.size).setSha256(item.sha256).setCollision(manifest.collision).build()
                    val attempted=row.attempted; if(!attempted) row=put(row.copy(attempted=true,offset=0))
                    fun file(response:FileResponse)=UploadBatchRules.checkFileReceipt(row.owner,header(row),index,descriptor,response)
                    var progress=file(call(op(if(attempted) FileOperation.UPLOAD_BATCH_ITEM_STATUS else FileOperation.PREPARE_BATCH_FILE,index)))
                    require(progress.offset == item.size || progress.offset % StorageRules.RANGE == 0L)
                    row = savePayload(row, payloadRun, progress, pendingPayload.remaining, acknowledgedPayload)
                    while(progress.phase==UploadPhase.UPLOAD_RECEIVING && progress.offset<item.size) {
                        val count=minOf(StorageRules.RANGE.toLong(),item.size-progress.offset).toInt(); val expected=progress.offset+count
                        progress=file(call(op(FileOperation.WRITE_BATCH_FILE,index).setOffset(progress.offset).setContent(ByteString.copyFrom(source.range(progress.offset,count)))))
                        require(progress.offset == expected && progress.phase == UploadPhase.UPLOAD_RECEIVING)
                        acknowledgedPayload += count
                        row = savePayload(row, payloadRun, progress, pendingPayload.remaining, acknowledgedPayload)
                    }
                    if(progress.phase in setOf(UploadPhase.UPLOAD_RECEIVING,UploadPhase.UPLOAD_VERIFYING,UploadPhase.UPLOAD_FINALIZING) || progress.phase==UploadPhase.UPLOAD_REVIEW && progress.finalName.isNotEmpty()) progress=file(call(op(FileOperation.FINISH_BATCH_FILE,index)))
                    while(progress.phase in setOf(UploadPhase.UPLOAD_VERIFYING,UploadPhase.UPLOAD_FINALIZING)) {delay(250); progress=file(call(op(FileOperation.UPLOAD_BATCH_ITEM_STATUS,index)))}
                    val outcome=when(progress.phase) {UploadPhase.UPLOAD_COMPLETED->UploadBatchItemPhase.BATCH_ITEM_SUCCEEDED; UploadPhase.UPLOAD_SKIPPED->UploadBatchItemPhase.BATCH_ITEM_SKIPPED; UploadPhase.UPLOAD_CANCELED->UploadBatchItemPhase.BATCH_ITEM_FAILED; else->UploadBatchItemPhase.BATCH_ITEM_REVIEW}
                    if(outcome==UploadBatchItemPhase.BATCH_ITEM_REVIEW) throw IOException("The receiver kept an uncertain copy. Review or stop the original plan before deliberately starting another.")
                    endPayload(row.id)
                    pendingPayload.removeBranch(index)
                    row=commit(row,PhoneBatchItemResult(index,outcome,progress.finalName,progress.message))
                }
            }
            val finished=checked(call(op(FileOperation.FINISH_UPLOAD_BATCH))); require(finished.phase==UploadBatchPlanPhase.UPLOAD_PLAN_COMPLETED); acceptTerminal(row,finished)
        } catch(e:Exception) {
            runCatching {put(row.copy(phase=if(row.phase==PhoneBatchSendingPhase.STOPPING) PhoneBatchSendingPhase.STOPPING else if(e is CancellationException) PhoneBatchSendingPhase.PAUSED else PhoneBatchSendingPhase.REVIEW,
                message=if(row.phase==PhoneBatchSendingPhase.STOPPING) "Stopping was interrupted. Reconnect and explicitly continue the original saved stop." else "Sending stopped. Check the original receiver receipts; earlier copies and private snapshots were kept."))}
        } finally {
            endPayload(row.id)
            batchPayloadRates.observe(row.id, payloadRun, row.bytes, 0, false, payloadNow())
        }
    }
    private fun commit(row:PhoneBatchSend,result:PhoneBatchItemResult):PhoneBatchSend=synchronized(gate) {
        require(result.index==row.next && result.phase !in setOf(UploadBatchItemPhase.BATCH_ITEM_PENDING,UploadBatchItemPhase.BATCH_ITEM_REVIEW))
        val file=resultFile(row.id,result.index); val text=JSONObject().put("schema",1).put("job",row.id).put("hash",row.sourceHash).put("index",result.index).put("phase",result.phase.name).put("name",result.name).put("message",result.message).toString()
        val previous=if(file.exists()) file.length() else 0L; if(previous!=0L) require(readResult(row,result.index,false)==result) {"The receiver changed an already retained item result."}
        val bytes=text.toByteArray(Charsets.UTF_8).size; require(bytes<=RESULT_LIMIT && (resultBytes[row.id]?:0)-previous+bytes<=RESULTS_LIMIT)
        try {PrivateJsonFile(file,RESULT_LIMIT).write(text); resultBytes[row.id]=(resultBytes[row.id]?:0)-previous+bytes}
        catch(e:Exception) {recovery.block("Outgoing item results could not be saved. Existing snapshots and receiver copies were kept."); emit(); throw e}
        val outcomes=Rules.decode(row.outcomes); outcomes[result.index]=result.phase.number.toByte(); put(row.copy(next=row.next+1,attempted=false,offset=0,outcomes=Rules.b64(outcomes)))
    }
    private fun readResult(row:PhoneBatchSend,index:Int,committed:Boolean=true):PhoneBatchItemResult {
        val x=JSONObject(PrivateJsonFile(resultFile(row.id,index),RESULT_LIMIT).read()); val result=PhoneBatchItemResult(x.getInt("index"),UploadBatchItemPhase.valueOf(x.getString("phase")),x.getString("name"),x.getString("message"))
        require(x.getInt("schema")==1 && x.getString("job")==row.id && x.getString("hash")==row.sourceHash && result.index==index && result.phase !in setOf(UploadBatchItemPhase.UNRECOGNIZED,UploadBatchItemPhase.BATCH_ITEM_PENDING,UploadBatchItemPhase.BATCH_ITEM_REVIEW) &&
            result.name.length<=255 && result.message.length<=512 && result.message.none(Character::isISOControl) && (!committed || Rules.decode(row.outcomes)[index].toInt()==result.phase.number))
        if(result.name.isNotEmpty()) StorageRules.name(result.name); return result
    }
    fun itemResult(id:String,index:Int):PhoneBatchItemResult=synchronized(gate) {ready(); val row=rows.getValue(id); require(!row.purging && index in 0 until row.next); readResult(row,index)}
    private fun acceptTerminal(row:PhoneBatchSend,receipt:UploadBatchReceipt) {
        require(receipt.phase!=UploadBatchPlanPhase.UPLOAD_PLAN_COMPLETED || row.next==row.items) {"The receiver claims completion before local item results were retained."}
        require(receipt.phase!=UploadBatchPlanPhase.UPLOAD_PLAN_CANCELED || !row.effectsStarted)
        if(receipt.phase!=UploadBatchPlanPhase.UPLOAD_PLAN_CANCELED) {val outcomes=Rules.decode(row.outcomes); repeat(row.next) {require(receipt.itemOutcomes.byteAt(it)==outcomes[it]) {"The receiver changed a retained final item outcome."}}}
        put(row.copy(phase=if(receipt.phase==UploadBatchPlanPhase.UPLOAD_PLAN_COMPLETED) PhoneBatchSendingPhase.COMPLETED else PhoneBatchSendingPhase.STOPPED,receiverOutcomes=Rules.b64(receipt.itemOutcomes.toByteArray()),
            message=if(receipt.phase==UploadBatchPlanPhase.UPLOAD_PLAN_COMPLETED) "Receiver confirmed all final outcomes. Originals and private snapshots were kept." else "The original receiver plan stopped. Published and uncertain copies were kept."))
    }
    private fun ownHistory(row:PhoneBatchSend) {ready(); check(row.owner==owner.deviceId && row.id !in work) {"Review the local owner identity, pause this batch and wait before changing its history."}}
    fun sourceInUse(id:String)=synchronized(gate) {rows.values.any {it.source==id && (it.id in work || !it.archived && it.phase !in setOf(PhoneBatchSendingPhase.COMPLETED,PhoneBatchSendingPhase.STOPPED))}}
    private fun historyFiles(row:PhoneBatchSend,validateResults:Boolean=false):List<HistoryFile> {
        val files=mutableListOf<HistoryFile>()
        for(index in 0..minOf(row.next,row.items-1)) {
            val file=resultFile(row.id,index)
            if(!file.exists()) {check(index>=row.next) {"Saved item results are missing. Copies were kept for review."}; continue}
            val version=stamp(file); val size=Os.lstat(file.path).st_size; require(size<=RESULT_LIMIT)
            if(validateResults) readResult(row,index,index<row.next)
            check(stamp(file)==version) {"An item result changed during review."}; files.add(HistoryFile(file.name,version,size))
        }
        val expected=files.map {it.name}.toSet(); val names=children().filter {it.name.startsWith(row.id+".") && it.name!=row.id+".json"}
        check(names.size==files.size && names.all {it.name in expected && !it.isDirectory}) {"Unexpected history objects were kept for review."}; return files
    }
    private fun historyActionAllowed(row:PhoneBatchSend,action:PhoneBatchHistoryAction) {
        check(!row.purging && if(action==PhoneBatchHistoryAction.ARCHIVE) !row.archived && rows.values.count {it.archived}<MAX_HISTORY else row.archived) {"This history action is unavailable. Review its current state."}
        if(action==PhoneBatchHistoryAction.RESTORE) {
            check(rows.values.count {!it.archived}<UploadBatchRules.MAX_PLANS && rows.values.count {!it.archived && it.peer==row.peer}<UploadBatchRules.MAX_PEER_PLANS) {"Active queue slots are full. Archive another inactive job first."}
            check(row.phase in setOf(PhoneBatchSendingPhase.COMPLETED,PhoneBatchSendingPhase.STOPPED) || rows.values.none {!it.archived && it.source==row.source && it.phase !in setOf(PhoneBatchSendingPhase.COMPLETED,PhoneBatchSendingPhase.STOPPED)}) {"Another active job already uses this private source. Review it before restoring the original intent."}
        }
    }
    fun reviewHistory(id:String,action:PhoneBatchHistoryAction):PhoneBatchHistoryReview=synchronized(gate) {
        val row=rows.getValue(id); ownHistory(row); historyActionAllowed(row,action); val files=historyFiles(row,action==PhoneBatchHistoryAction.RESTORE)
        val now=android.os.SystemClock.elapsedRealtime(); historyReviews.entries.removeAll {it.value.expires<now}; if(historyReviews.size>=UploadBatchRules.MAX_PLANS) historyReviews.remove(historyReviews.keys.first())
        val value=PhoneBatchHistoryReview(StorageRules.token(),id,action,row.source,files.size,files.sumOf {it.size},row.header.isNotEmpty())
        historyReviews[value.id]=HistoryReview(value,versions.getValue(id),files,now+120_000); value
    }
    fun confirmHistory(confirmation:PhoneBatchHistoryReview):Boolean=synchronized(gate) {
        ready(); val review=historyReviews.remove(confirmation.id); val row=rows.getValue(confirmation.job)
        check(review!=null && review.value==confirmation && review.expires>=android.os.SystemClock.elapsedRealtime() && versions[row.id]==review.parent) {"This history review changed, expired or was already used. Review it again."}
        ownHistory(row); historyActionAllowed(row,confirmation.action); val files=historyFiles(row,confirmation.action==PhoneBatchHistoryAction.RESTORE)
        check(files==review.files) {"Item history changed after review. It was kept."}
        if(confirmation.action!=PhoneBatchHistoryAction.PURGE) {put(row.copy(archived=confirmation.action==PhoneBatchHistoryAction.ARCHIVE)); true}
        else {put(row.copy(purging=true,purgeCount=files.size)); continuePurge(row.id)}
    }
    private fun validatePurgeInventory(row:PhoneBatchSend) {
        for(index in minOf(row.purgeCount,row.purgeCursor+PURGE_BATCH) until row.purgeCount) check(resultFile(row.id,index).exists()) {"History outside the interrupted cleanup window is missing."}
    }
    fun continuePurge(id:String):Boolean=synchronized(gate) {
        var row=rows.getValue(id); ownHistory(row); check(row.archived && row.purging) {"This job has no saved history-removal intent."}; validatePurgeInventory(row)
        val end=minOf(row.purgeCount,row.purgeCursor+PURGE_BATCH); val batch=mutableListOf<HistoryFile>()
        for(index in row.purgeCursor until end) {val file=resultFile(id,index); if(!file.exists()) continue
            val version=stamp(file); val size=Os.lstat(file.path).st_size; require(size<=RESULT_LIMIT); batch.add(HistoryFile(file.name,version,size))}
        batch.forEach {saved->val file=File(directory,saved.name); Os.open(file.path,OsConstants.O_RDONLY or OsConstants.O_NOFOLLOW,0).useOsDescriptor {fd->
            check(stamp(file)==saved.version && stamp(Os.fstat(fd))==saved.version) {"History changed during cleanup. It was kept."}; check(file.delete())}}
        row=put(row.copy(purgeCursor=end)); resultBytes[id]=maxOf(0,(resultBytes[id]?:0)-batch.sumOf {it.size})
        if(end!=row.purgeCount) false else {
            check(children().none {it.name.startsWith(id+".") && it.name!=id+".json"}) {"Unexpected history objects were preserved; cleanup is paused."}
            val file=File(directory,id+".json"); Os.open(file.path,OsConstants.O_RDONLY or OsConstants.O_NOFOLLOW,0).useOsDescriptor {fd->
                check(stamp(file)==versions.getValue(id) && stamp(Os.fstat(fd))==versions.getValue(id)); check(file.delete())}
            rows.remove(id); versions.remove(id); resultBytes.remove(id); emit(); true
        }
    }
    fun pause(id:String)=synchronized(gate) {work[id]?.cancel()}
    suspend fun pauseAndWait(id:String) {val done=synchronized(gate) {completions[id].also {work[id]?.cancel()}}; done?.await()}
    fun pauseAll()=synchronized(gate) {work.values.toList().forEach {it.cancel()}}
    suspend fun whenIdle() {synchronized(gate) {completions.values.toList()}.awaitAll()}
    fun recoveryIssues()=synchronized(gate) {recovery.all()}
    fun recoveryArchives()=storage.archives()
    fun reviewStoreReset(allowed:()->Boolean={true})=storage.reviewReset(allowed)
    fun confirmStoreReset(review:PhoneSenderResetReview,allowed:()->Boolean={true})=storage.confirmReset(review,allowed)
    fun reviewArchiveRemoval(id:String,allowed:()->Boolean={true})=storage.reviewRemoval(id,allowed)
    fun confirmArchiveRemoval(review:PhoneSenderArchiveRemovalReview,allowed:()->Boolean={true})=storage.confirmRemoval(review,allowed)
    fun continueArchiveRemoval(id:String,allowed:()->Boolean={true})=storage.continueRemoval(id,allowed)
    fun archiveFiles(id:String,start:Int,allowed:()->Boolean={true})=storage.archiveFiles(id,start,allowed)
    fun reviewSourceStoreReset(allowed:()->Boolean={true})=synchronized(gate) {
        check(!closed && work.isEmpty()) {"Pause batch sending before reviewing private snapshot recovery."}; sources.reviewStoreReset(allowed)
    }
    fun confirmSourceStoreReset(review:PhoneSourceResetReview,allowed:()->Boolean={true})=synchronized(gate) {
        check(!closed && work.isEmpty()) {"Pause batch sending before preserving private snapshots."}; sources.confirmStoreReset(review,allowed)
    }
    suspend fun shutdown() {
        synchronized(gate) {if(closed) return; closed=true; pauseAll()}; whenIdle()
        synchronized(gate) {lifetime.cancel(); storage.close()}
    }
    companion object {const val MAX_HISTORY=32; private const val MAX_RECORDS=UploadBatchRules.MAX_PLANS+MAX_HISTORY; private const val PURGE_BATCH=64; private const val RECORD_LIMIT=64*1024; private const val RESULT_LIMIT=8*1024; private const val RESULTS_LIMIT=16L*1024*1024}
}
