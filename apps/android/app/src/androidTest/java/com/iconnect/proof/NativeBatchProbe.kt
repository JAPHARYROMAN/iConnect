package com.iconnect.proof

import android.app.Activity
import android.app.Instrumentation
import android.content.Context
import android.content.ContextWrapper
import android.net.Uri
import android.os.Bundle
import com.google.protobuf.ByteString
import com.iconnect.protocol.*
import kotlinx.coroutines.*
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.IOException
import java.security.MessageDigest
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.atomic.AtomicInteger

/** Explicit native interop probe, outside JUnit discovery and ordinary app flows. */
class NativeBatchProbe:Instrumentation() {
    private lateinit var options:Bundle
    private val messages=CopyOnWriteArrayList<String>()
    override fun onCreate(arguments:Bundle?) {super.onCreate(arguments); options=arguments?:Bundle(); start()}
    override fun onStart() {
        try {val result=runBlocking {withTimeout(280_000) {exercise()}}
            finish(Activity.RESULT_OK,Bundle().apply {putString("nativeTransportResult",result.toString())})
        } catch(failure:Throwable) {finish(Activity.RESULT_CANCELED,Bundle().apply {putString("stream","Native batch peer messages: ${messages.joinToString(" | ")}\nNative batch probe failed: ${failure.stackTraceToString()}")})}
    }
    private fun status(value:JSONObject)=sendStatus(0,Bundle().apply {putString("nativeProbe",Rules.b64(value.toString().toByteArray(Charsets.UTF_8)))})
    private suspend fun exercise():JSONObject=coroutineScope {
        val run=requireNotNull(options.getString("run")); val role=requireNotNull(options.getString("role"))
        val hostKey=Rules.decode(requireNotNull(options.getString("hostKey")))
        val mode=requireNotNull(options.getString("listing")); require(mode in listOf("true","false")); val listing=mode=="true"
        require(Rules.tokenPattern.matches(run) && role in listOf("windows","android") && hostKey.size in 64..256)
        val directory=File(targetContext.filesDir,"native-transport-probes/$run"); check(!directory.exists() && directory.mkdirs())
        val root=File(directory,"batch-owner").apply {check(mkdir())}; val prefix="native-batch-$run-"
        val context=object:ContextWrapper(targetContext) {
            override fun getFilesDir()=root
            override fun getCacheDir()=File(root,"cache").apply {mkdirs()}
            override fun getApplicationContext():Context=this
            override fun getSharedPreferences(name:String,mode:Int)=targetContext.getSharedPreferences(prefix+name,mode)
        }
        var sequence=0
        suspend fun command():JSONObject=withContext(Dispatchers.IO) {
            val file=File(directory,"$sequence.command")
            while(!file.exists()) {currentCoroutineContext().ensureActive(); delay(15)}
            check(file.length() in 1..196_608)
            val value=JSONObject(String(Rules.decode(file.readText(Charsets.UTF_8).trim()),Charsets.UTF_8))
            check(value.getInt("sequence")==sequence && ++sequence<=144); value
        }
        val identity=LocalIdentity(); val policy=AndroidOwnerPolicy(context,identity.id); val attempt=policy.beginPair(hostKey)
        val unsigned=Pairing.newBuilder().setTransactionId(StorageRules.token()).setToken(StorageRules.token())
            .setHost(Identity.newBuilder().setName("Native interoperability fixture").setPublicKey(ByteString.copyFrom(hostKey)))
            .setGuest(identity.descriptor("Native batch fixture phone")).setExpiresAt(System.currentTimeMillis()+115_000).build()
        val submitted=unsigned.toBuilder().setGuestApproval(ByteString.copyFrom(identity.sign(Rules.pairing(unsigned)))).build()
        status(JSONObject().put("event","identity").put("key",Rules.b64(identity.publicKey)).put("device",identity.id)
            .put("pairing",Rules.b64(submitted.toByteArray())).put("pid",android.os.Process.myPid()))
        val authorization=command(); check(authorization.getString("op")=="pairing")
        val encoded=authorization.getString("pairing"); check(encoded.length in 1..8192)
        val approved=Pairing.parseFrom(Rules.decode(encoded))
        check(MessageDigest.isEqual(Rules.pairing(submitted),Rules.pairing(approved)) && submitted.host.name==approved.host.name && submitted.guest.name==approved.guest.name)
        val approval=policy.commitPair(attempt,approved)
        fun fixture()=requireNotNull(targetContext.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"fixture",null,Bundle()))
        val outgoingData=fixture(); val incomingData=fixture()
        val outgoingTree=Uri.parse(outgoingData.getString("tree")); val incomingTree=Uri.parse(incomingData.getString("tree"))
        val documents=NativeBatchDocuments(context.contentResolver,outgoingTree,incomingTree)
        val grants=AndroidStorageGrants(context,policy)
        val grant=grants.add(approval,incomingTree,incomingData.getInt("flags"),"Fixture receiving inbox"); grants.update(grant.id,if(listing) 13 else 12)
        val incoming=AndroidIncomingUploads(context,grants)
        val service=AndroidFileService(context,grants,available={policy.isCurrent(approval)},incoming=incoming)
        val scope=CoroutineScope(SupervisorJob()+Dispatchers.Main.immediate)
        val ready=CompletableDeferred<Unit>(); val sent=CompletableDeferred<Unit>(); val verified=CompletableDeferred<JSONObject>(); val finished=CompletableDeferred<Unit>()
        val signals=AtomicInteger()
        var peer:RtcTestSession?=null; var browser:PhoneLocalBrowser?=null; var sources:AndroidBatchSources?=null; var sender:PhoneBatchSender?=null; var commands:Job?=null
        var target:FileEntry?=null; var completed:PhoneBatchSend?=null; var begins=0; var lost=false; var revoked=false; var success=false
        suspend fun raw(value:FileRequest)=withContext(Dispatchers.Main.immediate) {requireNotNull(peer).requestFiles(value)}
        suspend fun waitFor(label:String,condition:()->Boolean) {withTimeout(45_000) {while(!condition()) {check(peer?.isClosed!=true) {"Native peer ended while waiting for $label."}; delay(15)}}}
        try {
            withContext(Dispatchers.Main.immediate) {
                peer=RtcTestSession(context,scope,identity,run,hostKey,
                    {kind,body,mid->check(signals.incrementAndGet()<=128); status(JSONObject().put("event","signal").put("kind",kind).put("body",body).put("mid",mid))},
                    {if(messages.size<128) messages.add(it)},
                    {if(it) ready.complete(Unit) else ready.completeExceptionally(IOException(messages.joinToString(" | ")))},
                    fileService=service,available={policy.isCurrent(approval)},initiator=role=="android")
            }
            commands=launch(Dispatchers.IO) {
                while(isActive) {
                    val value=command()
                    when(value.getString("op")) {
                        "start"->if(role=="android") withContext(Dispatchers.Main.immediate) {requireNotNull(peer).offer()}
                        "signal"->withContext(Dispatchers.Main.immediate) {
                            val body=value.getString("body"); check(body.length<=65_536)
                            when(value.getString("kind")) {
                                "offer"->requireNotNull(peer).acceptOffer(body)
                                "answer"->requireNotNull(peer).acceptAnswer(body)
                                "candidate"->requireNotNull(peer).addCandidate(body,value.getString("mid"))
                                else->error("Unexpected native signal.")
                            }
                        }
                        "revoke"->{check(sent.isCompleted && !revoked); grants.remove(grant.id); revoked=true; status(JSONObject().put("event","revoked"))}
                        "check"->{
                            check(revoked && sent.isCompleted && !verified.isCompleted)
                            val done=requireNotNull(completed); val original=UploadBatchHeader.parseFrom(Rules.decode(done.header))
                            val header=original.toBuilder().setId(MutationRules.newId()).build()
                            val denied=raw(FileRequest.newBuilder().setOperation(FileOperation.BEGIN_UPLOAD_BATCH).setTransferId(header.id)
                                .setDestinationId(requireNotNull(target).id).setMutationEpoch(header.epoch).setBatchHeader(header).build())
                            check(denied.status in setOf(FileStatus.ACCESS_DENIED,FileStatus.STALE_REFERENCE) && !denied.hasBatch() && denied.entriesCount==0 && denied.content.isEmpty)
                            grants.batchPlans.whenFilesIdle(); val plan=grants.batchPlans.all().single()
                            check(plan.phase==UploadBatchPlanPhase.UPLOAD_PLAN_COMPLETED && plan.items==6 && plan.bytes==NativeBatchDocuments.BYTES.toLong())
                            val names=value.getJSONArray("names"); check(names.length()==6)
                            val received=documents.verifyIncoming((0 until 6).map {names.getString(it)},listing)
                            val result=JSONObject().put("event","verified").put("suite","batches").put("run",run).put("role",role)
                                .put("sentHash",documents.original.hash).put("receivedHash",received.hash).put("bytes",NativeBatchDocuments.BYTES).put("items",6)
                                .put("preparations",begins).put("lostAcknowledgements",1).put("resumedOriginalReceipt",true)
                                .put("collisionOriginalKept",true).put("originalSourceKept",true).put("emptyDirectoriesKept",true).put("unicodeNameTextKept",true)
                                .put("zeroLengthFilesKept",true).put("parentOutcomesMatch",true).put("blindNamesRetained",!listing).put("withdrawnInboxDenied",true).put("publishedBatchChecked",true)
                            status(result); verified.complete(result)
                        }
                        "finish"->{check(verified.isCompleted); finished.complete(Unit); return@launch}
                        else->error("Unexpected native batch fixture command.")
                    }
                }
            }
            status(JSONObject().put("event","bound").put("peer",approval.id))
            withTimeout(40_000) {ready.await()}; status(JSONObject().put("event","authenticated"))
            check(requireNotNull(peer).acceptsFolderBatches)
            check(requireNotNull(peer).acceptsPublishedBatchChecks)
            val receiving=raw(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_CONTEXT).build())
            check(receiving.status==FileStatus.FILE_STATUS_OK && receiving.entriesCount==1); target=receiving.entriesList.single()
            val roots=raw(FileRequest.newBuilder().setOperation(FileOperation.LIST_ROOTS).build())
            check(target.permissions==(if(listing) 13 else 12) && roots.status in setOf(FileStatus.FILE_STATUS_OK,FileStatus.ACCESS_DENIED) &&
                roots.entriesCount==(if(listing) 1 else 0) && roots.content.isEmpty)
            withContext(Dispatchers.Main.immediate) {browser=PhoneLocalBrowser(context).also {it.setVisible(true); it.add(outgoingTree,outgoingData.getInt("flags"))}}
            val local=requireNotNull(browser)
            waitFor("local owner shortcut") {!local.state.value.busy && local.state.value.locations.size==1}
            withContext(Dispatchers.Main.immediate) {local.browse()}; waitFor("local root") {!local.state.value.busy && local.state.value.rows.size==1}
            withContext(Dispatchers.Main.immediate) {local.browse(local.state.value.rows.single().entry)}
            waitFor("actual local folder") {!local.state.value.busy && local.state.value.rows.any {it.entry.name==NativeBatchDocuments.FOLDER}}
            val selected=withContext(Dispatchers.Main.immediate) {local.captureBatch(listOf(local.state.value.rows.single {it.entry.name==NativeBatchDocuments.FOLDER}.entry))}
            var privateSources=AndroidBatchSources(context,policy); sources=privateSources
            val sourceId=privateSources.stage(approval,selected,UploadCollision.UPLOAD_KEEP_BOTH) {policy.isCurrent(approval)}
            privateSources.whenIdle(); check(privateSources.state.value.single().let {it.phase==PhoneBatchSourcePhase.READY && it.items==6 && it.bytes==NativeBatchDocuments.BYTES.toLong()})
            val manifest=privateSources.manifest(sourceId)
            check(manifest.itemsList.map {it.name}==NativeBatchDocuments.NAMES && manifest.itemsList.map {it.parent}==NativeBatchDocuments.PARENTS)
            var manager=PhoneBatchSender(context,policy,privateSources); sender=manager; val id=manager.queue(sourceId)
            suspend fun request(value:FileRequest):FileResponse {
                if(value.operation==FileOperation.BEGIN_UPLOAD_BATCH) begins++
                val response=raw(value)
                if(value.operation==FileOperation.WRITE_BATCH_FILE && value.batchItemIndex==4 && !lost && response.status==FileStatus.FILE_STATUS_OK) {
                    check(response.hasUpload() && response.upload.offset==StorageRules.RANGE.toLong()); lost=true
                    throw IOException("Generated loss of an accepted native batch range reply.")
                }; return response
            }
            manager.send(id,target,{policy.isCurrent(approval)},::request).join(); manager.whenIdle()
            val stopped=manager.state.value.single(); val header=UploadBatchHeader.parseFrom(Rules.decode(stopped.header))
            val accepted=raw(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_BATCH_ITEM_STATUS).setTransferId(header.id)
                .setMutationEpoch(header.epoch).setBatchItemIndex(4).build())
            check(lost && begins==1 && stopped.phase==PhoneBatchSendingPhase.REVIEW && stopped.next==4 && stopped.offset==0L && accepted.status==FileStatus.FILE_STATUS_OK &&
                accepted.hasUpload() && accepted.upload.offset==StorageRules.RANGE.toLong())
            status(JSONObject().put("event","checkpoint").put("receipt",header.id).put("senderOffset",stopped.offset).put("receiverOffset",accepted.upload.offset))
            manager.shutdown(); privateSources.shutdown()
            privateSources=AndroidBatchSources(context,policy); sources=privateSources; manager=PhoneBatchSender(context,policy,privateSources); sender=manager
            check(manager.state.value.single().header==stopped.header)
            manager.send(id,null,{policy.isCurrent(approval)},::request).join(); manager.whenIdle(); completed=manager.state.value.single()
            val done=requireNotNull(completed)
            check(done.phase==PhoneBatchSendingPhase.COMPLETED && done.next==6 && done.header==stopped.header && begins==1 &&
                done.outcomes==done.receiverOutcomes && Rules.decode(done.outcomes).let {it.size==6 && it.all {outcome->outcome.toInt()==1}})
            documents.verifyOriginal()
            manager.verifyPublished(id,approval,{policy.isCurrent(approval)},::request)
            val names=JSONArray((0 until 6).map {manager.itemResult(id,it).name})
            sent.complete(Unit); status(JSONObject().put("event","sent").put("sha256",documents.original.hash).put("bytes",NativeBatchDocuments.BYTES).put("items",6)
                .put("preparations",begins).put("lostAcknowledgements",1).put("resumedOriginalReceipt",true).put("names",names).put("publishedBatchChecked",true))
            val result=verified.await(); finished.await(); success=true; result
        } catch(failure:Throwable) {
            failure.addSuppressed(IOException("Native batch peer messages before cleanup: "+messages.joinToString(" | ")))
            throw failure
        } finally {withContext(NonCancellable) {
            commands?.cancelAndJoin(); withContext(Dispatchers.Main.immediate) {peer?.close()}; scope.cancel(); scope.coroutineContext.job.join()
            sender?.shutdown(); sources?.shutdown(); service.close(); incoming.shutdown(); grants.batchPlans.shutdown()
            browser?.let {local->withContext(Dispatchers.Main.immediate) {local.close()}.join()}
            LocalDocumentLocations(context).all().forEach {LocalDocumentLocations(context).remove(it.id)}
            AndroidSafAccess(context).reconcile("incoming:",emptyMap()); AndroidSafAccess(context).reconcile("batch-incoming:",emptyMap())
            grants.all().forEach {grants.remove(it.id)}
            if(success) listOf(outgoingData,incomingData).forEach {targetContext.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"dispose",it.getString("root"),null)}
            listOf("iconnect.proof","iconnect.storage","iconnect.saf-access").forEach {targetContext.deleteSharedPreferences(prefix+it)}
        }}
    }
}
