package com.iconnect.proof

import android.app.Activity
import android.app.Instrumentation
import android.net.Uri
import android.os.Bundle
import android.os.Debug
import android.os.Process
import android.os.SystemClock
import android.provider.DocumentsContract as Docs
import androidx.test.platform.app.InstrumentationRegistry
import com.iconnect.protocol.*
import kotlinx.coroutines.*
import org.json.JSONArray
import org.json.JSONObject
import java.io.FileOutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.MessageDigest
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicLong
import java.util.concurrent.atomic.AtomicReference

/** Manual generated-data diagnostic. It never replaces the full-size LargeFileTest. */
class UploadCostProbe:Instrumentation() {
    private lateinit var options:Bundle
    private data class Mark(val nanos:Long,val cpuMillis:Long,val binderSent:Int)
    private class Calls {val count=AtomicLong(); val nanos=AtomicLong()}
    private fun mark()=Mark(SystemClock.elapsedRealtimeNanos(),Process.getElapsedCpuTime(),Debug.getBinderSentTransactions())
    private fun elapsed(start:Mark,end:Mark)=JSONObject().put("wallMillis",(end.nanos-start.nanos)/1_000_000.0)
        .put("processCpuMillis",end.cpuMillis-start.cpuMillis)
        .put("processBinderSent",if(start.binderSent>=0 && end.binderSent>=start.binderSent) end.binderSent-start.binderSent else JSONObject.NULL)

    override fun onCreate(arguments:Bundle?) {
        super.onCreate(arguments); options=arguments?:Bundle()
        InstrumentationRegistry.registerInstance(this,options); start()
    }
    override fun onStart() {
        try {
            val result=runBlocking {withTimeout(20*60_000L) {exercise()}}
            finish(Activity.RESULT_OK,Bundle().apply {putString("uploadCostResult",result.toString())})
        } catch(failure:Throwable) {
            finish(Activity.RESULT_CANCELED,Bundle().apply {putString("stream","Upload cost probe failed: ${failure.stackTraceToString()}")})
        }
    }
    private fun status(name:String)=sendStatus(0,Bundle().apply {putString("uploadCostPhase",name)})

    private suspend fun exercise():JSONObject=withContext(Dispatchers.IO) {
        require(options.keySet().all {it=="payloadMiB"}) {"Only the generated payload size is configurable."}
        val supplied=options.getString("payloadMiB")
        require(!options.containsKey("payloadMiB") || supplied!=null) {"The payload size must be a numeric string."}
        val mib=if(supplied==null) 64 else requireNotNull(supplied.toIntOrNull()) {"The payload size must be numeric."}
        require(mib in 8..128) {"Use a generated payload from 8 to 128 MiB."}
        val size=mib*1024L*1024+17
        val started=mark(); val phases=JSONArray(); val finalizationSamples=JSONArray()
        val localCalls=ConcurrentHashMap<String,Calls>(); val receivingCalls=ConcurrentHashMap<String,Calls>()
        fun record(name:String,from:Mark,to:Mark) {phases.put(elapsed(from,to).put("name",name)); status(name+" completed")}
        suspend fun measured(calls:ConcurrentHashMap<String,Calls>,request:FileRequest,action:suspend ()->FileResponse):FileResponse {
            val before=SystemClock.elapsedRealtimeNanos()
            try {return action()} finally {
                val value=calls.getOrPut(request.operation.name) {Calls()}
                value.count.incrementAndGet(); value.nanos.addAndGet(SystemClock.elapsedRealtimeNanos()-before)
            }
        }
        fun summarize(calls:Map<String,Calls>)=JSONObject().apply {calls.toSortedMap().forEach {(operation,value)->
            put(operation,JSONObject().put("calls",value.count.get()).put("wallMillis",value.nanos.get()/1_000_000.0))
        }}
        val receiver=IncomingUploadsTest.Fixture()
        var sourceData:Bundle?=null; var sourceTree:Uri?=null
        var locations:LocalDocumentLocations?=null; var localHost:AndroidFileService?=null; var manager:PhoneUploads?=null
        try {
            val context=receiver.context
            check(context.filesDir.usableSpace>4*size+128L*1024*1024) {"The diagnostic needs room for four generated copies."}
            val data=requireNotNull(context.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"fixture",null,Bundle()))
            sourceData=data
            val tree=Uri.parse(data.getString("tree")); sourceTree=tree
            val sourceUri=Docs.buildDocumentUriUsingTree(tree,data.getString("file"))
            val grant=receiver.grants.add(receiver.approval,tree,data.getInt("flags"),"Generated cost-profile source")
            receiver.grants.update(grant.id,StorageRules.READ_ONLY)
            val local=LocalDocumentLocations(context); locations=local; local.add(tree,data.getInt("flags"))
            val host=AndroidFileService(context,local as DocumentAccessPolicy); localHost=host
            suspend fun read(request:FileRequest)=measured(localCalls,request) {
                host.execute(local.ownerId(),request.toBuilder().setRequestId(StorageRules.token()).build()).also {
                    check(it.status==FileStatus.FILE_STATUS_OK) {it.message}; PhoneTransferManifests.validateResponse(it)
                }
            }
            status("source generation")
            val generatedAt=mark(); val digest=MessageDigest.getInstance("SHA-256")
            val block=ByteArray(1024*1024).also {java.security.SecureRandom().nextBytes(it)}
            requireNotNull(context.contentResolver.openOutputStream(sourceUri,"wt")).use {output->
                var offset=0L
                while(offset<size) {
                    currentCoroutineContext().ensureActive(); ByteBuffer.wrap(block).order(ByteOrder.LITTLE_ENDIAN).putLong(offset)
                    val count=minOf(block.size.toLong(),size-offset).toInt(); output.write(block,0,count); digest.update(block,0,count); offset+=count
                }
                output.flush(); (output as FileOutputStream).fd.sync()
            }
            val expectedHash=digest.digest(); record("sourceGeneration",generatedAt,mark())
            val root=read(FileRequest.newBuilder().setOperation(FileOperation.LIST_ROOTS).build()).entriesList.single()
            val entry=read(FileRequest.newBuilder().setOperation(FileOperation.LIST_DIRECTORY).setNodeId(root.id).build()).entriesList.single {it.name=="simu.txt"}
            check(entry.size==size)
            receiver.initialize(); val collisionOriginal=receiver.read("simu.txt")
            val identity=LocalIdentity(); val peer=PeerApproval(identity.id,Rules.b64(identity.publicKey),"Fixture receiver",StorageRules.token())
            val allowed={receiver.policy.isCurrent(receiver.approval)}
            val outgoing=PhoneUploads(context,receiver.approval.id); manager=outgoing
            var lastFinalization:String?=null
            suspend fun settled(id:String,sample:Boolean=false):PhoneUpload {
                while(true) {
                    if(sample) receiver.incoming.readFinalizations().values.singleOrNull()?.let {progress->
                        if(progress.stage.name!=lastFinalization) {
                            lastFinalization=progress.stage.name
                            finalizationSamples.put(JSONObject().put("stage",progress.stage.name).put("offset",progress.offset)
                                .put("sinceProbeStart",elapsed(started,mark())))
                            status("receiver "+progress.stage.name)
                        }
                    }
                    val row=outgoing.state.value.single {it.id==id}
                    if(!row.active) return row
                    delay(20)
                }
            }
            status("private source preparation")
            val stagingAt=mark(); val id=outgoing.stage(peer,LocalUploadSource(entry,{true},::read),allowed)
            val prepared=settled(id)
            check(prepared.phase==PhoneUploadPhase.READY) {prepared.message}
            check(prepared.size==size && MessageDigest.isEqual(expectedHash,Rules.decode(prepared.hash)))
            record("privateSnapshotPreparation",stagingAt,mark())
            val target=receiver.call(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_CONTEXT).build()).entriesList.single()
            val finalizationAt=AtomicReference<Mark?>(); val sendingAt=mark(); status("payload and sender hash")
            outgoing.send(id,peer,target,allowed) {request->
                if(request.operation==FileOperation.FINISH_UPLOAD) finalizationAt.compareAndSet(null,mark())
                measured(receivingCalls,request) {receiver.raw(request).also {
                    check(it.status==FileStatus.FILE_STATUS_OK) {it.message}; PhoneTransferManifests.validateResponse(it)
                }}
            }
            val completed=settled(id,true); val sentAt=mark()
            check(completed.phase==PhoneUploadPhase.COMPLETED) {completed.message}
            check(completed.offset==size && completed.remote.isNotEmpty())
            check(receiver.incoming.state.value.single().phase==UploadPhase.UPLOAD_COMPLETED)
            val boundary=requireNotNull(finalizationAt.get())
            record("payloadAndSenderHash",sendingAt,boundary); record("receiverFinalization",boundary,sentAt)
            val document=receiver.children().entries.single {it.value==completed.finalName}.key
            val published=Docs.buildDocumentUriUsingTree(receiver.tree,document)
            val checkingAt=mark(); status("independent hashes")
            for(uri in listOf(sourceUri,published)) {
                val hash=MessageDigest.getInstance("SHA-256"); var bytes=0L
                requireNotNull(context.contentResolver.openInputStream(uri)).use {input->
                    while(true) {currentCoroutineContext().ensureActive(); val count=input.read(block); if(count<0) break; bytes+=count; hash.update(block,0,count)}
                }
                check(bytes==size && MessageDigest.isEqual(hash.digest(),expectedHash)) {"Generated source or provider output did not verify."}
            }
            check(receiver.read("simu.txt").contentEquals(collisionOriginal)) {"The pre-existing receiver file changed."}
            val ranges=(size+StorageRules.RANGE-1)/StorageRules.RANGE
            check(localCalls[FileOperation.READ_RANGE.name]?.count?.get()==ranges && receivingCalls[FileOperation.UPLOAD_WRITE.name]?.count?.get()==ranges)
            record("independentSourceAndOutputHash",checkingAt,mark())
            JSONObject().put("schemaVersion",1).put("payloadBytes",size).put("rangeBytes",StorageRules.RANGE)
                .put("sha256",Rules.hex(expectedHash)).put("phases",phases).put("localRequests",summarize(localCalls))
                .put("receivingRequests",summarize(receivingCalls)).put("sampledFinalizationStages",finalizationSamples)
                .put("samplingIntervalMillis",20).put("total",elapsed(started,mark()))
                .put("sourceAndOutputIndependentlyVerified",true).put("collisionOriginalKept",true)
                .put("counterScope","Target process, including probe/polling/GC; provider-process CPU is excluded. Short sampled stages can be missed.")
                .put("encryptedTransportVerified",false).put("largeFileMatrixProven",false).put("physicalNetworkVerified",false).put("fullCompletion",false)
        } finally {withContext(NonCancellable) {
            manager?.shutdown(); localHost?.close()
            locations?.let {saved->saved.all().filter {it.tree==sourceTree}.forEach {saved.remove(it.id)}}
            sourceData?.let {receiver.context.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"dispose",it.getString("root"),null)}
            receiver.close()
        }}
    }
}
