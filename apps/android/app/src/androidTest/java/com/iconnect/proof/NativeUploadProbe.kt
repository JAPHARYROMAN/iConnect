package com.iconnect.proof

import android.app.Activity
import android.app.Instrumentation
import android.content.Context
import android.content.ContextWrapper
import android.net.Uri
import android.os.Bundle
import android.provider.DocumentsContract as Docs
import com.google.protobuf.ByteString
import com.iconnect.protocol.*
import kotlinx.coroutines.*
import org.json.JSONObject
import java.io.File
import java.io.IOException
import java.security.MessageDigest
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.atomic.AtomicInteger

/** Explicit fixture only: production upload engines and real signed owner policy over native peers. */
class NativeUploadProbe:Instrumentation() {
    private lateinit var options:Bundle
    override fun onCreate(arguments:Bundle?) {super.onCreate(arguments); options=arguments?:Bundle(); start()}
    override fun onStart() {
        try {val result=runBlocking {withTimeout(260_000) {exercise()}}
            finish(Activity.RESULT_OK,Bundle().apply {putString("nativeTransportResult",result.toString())})
        } catch(failure:Throwable) {finish(Activity.RESULT_CANCELED,Bundle().apply {putString("stream","Native upload probe failed: ${failure.stackTraceToString()}")})}
    }
    private fun status(value:JSONObject)=sendStatus(0,Bundle().apply {putString("nativeProbe",Rules.b64(value.toString().toByteArray(Charsets.UTF_8)))})

    private suspend fun exercise():JSONObject=coroutineScope {
        val run=requireNotNull(options.getString("run")); val role=requireNotNull(options.getString("role"))
        val hostKey=Rules.decode(requireNotNull(options.getString("hostKey")))
        require(Rules.tokenPattern.matches(run) && role in listOf("windows","android") && hostKey.size in 64..256)
        val directory=File(targetContext.filesDir,"native-transport-probes/$run"); check(!directory.exists() && directory.mkdirs())
        val root=File(directory,"upload-owner").apply {check(mkdir())}; val prefix="native-upload-$run-"
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
            .setGuest(identity.descriptor("Native upload fixture phone")).setExpiresAt(System.currentTimeMillis()+115_000).build()
        val submitted=unsigned.toBuilder().setGuestApproval(ByteString.copyFrom(identity.sign(Rules.pairing(unsigned)))).build()
        status(JSONObject().put("event","identity").put("key",Rules.b64(identity.publicKey)).put("device",identity.id)
            .put("pairing",Rules.b64(submitted.toByteArray())).put("pid",android.os.Process.myPid()))
        val authorization=command(); check(authorization.getString("op")=="pairing")
        val encoded=authorization.getString("pairing"); check(encoded.length in 1..8192)
        val approvedPairing=Pairing.parseFrom(Rules.decode(encoded))
        check(MessageDigest.isEqual(Rules.pairing(submitted),Rules.pairing(approvedPairing)) &&
            submitted.host.name==approvedPairing.host.name && submitted.guest.name==approvedPairing.guest.name)
        val approval=policy.commitPair(attempt,approvedPairing)
        val data=requireNotNull(context.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"fixture",null,Bundle().apply {putBoolean("largeFile",true)}))
        val tree=Uri.parse(data.getString("tree")); val resolver=context.contentResolver
        val grants=AndroidStorageGrants(context,policy)
        val grant=grants.add(approval,tree,data.getInt("flags"),"Fixture receive-only inbox"); grants.update(grant.id,12)
        val incoming=AndroidIncomingUploads(context,grants)
        val service=AndroidFileService(context,grants,available={policy.isCurrent(approval)},incoming=incoming)
        val scope=CoroutineScope(SupervisorJob()+Dispatchers.Main.immediate)
        val ready=CompletableDeferred<Unit>(); val verified=CompletableDeferred<JSONObject>(); val finished=CompletableDeferred<Unit>()
        val sent=CompletableDeferred<Unit>(); val messages=CopyOnWriteArrayList<String>(); val signals=AtomicInteger()
        var peer:RtcTestSession?=null; var browser:PhoneLocalBrowser?=null; var uploads:PhoneUploads?=null; var commands:Job?=null
        var originalHash=""; var target:FileEntry?=null; var completed:PhoneUpload?=null; var preparations=0; var lost=false; var revoked=false; var success=false
        val existing="Generated existing owner file; keep these bytes."
        fun children():Map<String,String> {
            val rows=linkedMapOf<String,String>()
            resolver.query(Docs.buildChildDocumentsUriUsingTree(tree,Docs.getTreeDocumentId(tree)),arrayOf(Docs.Document.COLUMN_DOCUMENT_ID,Docs.Document.COLUMN_DISPLAY_NAME),null,null,null)!!.use {
                while(it.moveToNext()) {check(rows.size<32); rows[it.getString(0)]=it.getString(1)}
            }; return rows
        }
        fun bytes(name:String):ByteArray {
            val id=children().entries.single {it.value==name}.key
            return requireNotNull(resolver.openInputStream(Docs.buildDocumentUriUsingTree(tree,id))).use {it.readBytes()}
        }
        fun digest(value:ByteArray)=Rules.hex(MessageDigest.getInstance("SHA-256").digest(value))
        suspend fun raw(value:FileRequest)=withContext(Dispatchers.Main.immediate) {requireNotNull(peer).requestFiles(value)}
        suspend fun waitFor(label:String,condition:()->Boolean) {withTimeout(45_000) {while(!condition()) {check(peer?.isClosed!=true) {"Native peer ended while waiting for $label."}; delay(15)}}}
        try {
            val collision=requireNotNull(Docs.createDocument(resolver,Docs.buildDocumentUriUsingTree(tree,Docs.getTreeDocumentId(tree)),"application/octet-stream","from-windows.bin"))
            requireNotNull(resolver.openOutputStream(collision,"wt")).use {it.write(existing.toByteArray(Charsets.UTF_8))}
            val initialChildren=children(); originalHash=digest(bytes("kutuma.bin")); check(bytes("kutuma.bin").size==PAYLOAD_BYTES)
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
                            val done=requireNotNull(completed); val destination=requireNotNull(target)
                            val descriptor=UploadInfo.newBuilder().setId(MutationRules.newId()).setName("blocked-after-withdrawal.bin").setSize(0)
                                .setSha256(ByteString.copyFrom(Rules.digest(ByteArray(0)))).setCollision(UploadCollision.UPLOAD_KEEP_BOTH).build()
                            val denied=raw(FileRequest.newBuilder().setOperation(FileOperation.PREPARE_UPLOAD).setTransferId(descriptor.id)
                                .setDestinationId(destination.id).setMutationEpoch(done.remoteEpoch).setUpload(descriptor).build())
                            check(denied.status in setOf(FileStatus.ACCESS_DENIED,FileStatus.STALE_REFERENCE) && !denied.hasUpload() && denied.entriesCount==0 && denied.content.isEmpty)
                            val roots=raw(FileRequest.newBuilder().setOperation(FileOperation.LIST_ROOTS).build())
                            check(roots.status in setOf(FileStatus.FILE_STATUS_OK,FileStatus.ACCESS_DENIED) && roots.entriesCount==0 && roots.content.isEmpty)
                            incoming.whenIdle(); val received=incoming.state.value.single()
                            check(received.phase==UploadPhase.UPLOAD_COMPLETED && received.name=="from-windows.bin" && received.size==PAYLOAD_BYTES.toLong() && received.finalName!="from-windows.bin")
                            val saved=bytes(received.finalName); check(saved.size==PAYLOAD_BYTES && bytes("from-windows.bin").toString(Charsets.UTF_8)==existing)
                            val after=children(); check(after.size==initialChildren.size+1 && initialChildren.all {after[it.key]==it.value})
                            check(digest(bytes("kutuma.bin"))==originalHash)
                            val result=JSONObject().put("event","verified").put("suite","uploads").put("run",run).put("role",role)
                                .put("sentHash",originalHash).put("receivedHash",digest(saved)).put("bytes",PAYLOAD_BYTES).put("preparations",preparations)
                                .put("lostAcknowledgements",1).put("resumedOriginalReceipt",true).put("collisionOriginalKept",true).put("originalSourceKept",true)
                                .put("publishedCopyChecked",true).put("withdrawnInboxDenied",true)
                            status(result); verified.complete(result)
                        }
                        "finish"->{check(verified.isCompleted); finished.complete(Unit); return@launch}
                        else->error("Unexpected native upload fixture command.")
                    }
                }
            }
            status(JSONObject().put("event","bound").put("peer",approval.id))
            withTimeout(40_000) {ready.await()}; status(JSONObject().put("event","authenticated"))
            val receiving=raw(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_CONTEXT).build())
            check(receiving.status==FileStatus.FILE_STATUS_OK && receiving.entriesCount==1); target=receiving.entriesList.single()
            val roots=raw(FileRequest.newBuilder().setOperation(FileOperation.LIST_ROOTS).build())
            check(target.permissions==12 && roots.status in setOf(FileStatus.FILE_STATUS_OK,FileStatus.ACCESS_DENIED) && roots.entriesCount==0 && roots.content.isEmpty)
            withContext(Dispatchers.Main.immediate) {browser=PhoneLocalBrowser(context).also {it.setVisible(true); it.add(tree,data.getInt("flags"))}}
            val local=requireNotNull(browser)
            waitFor("local owner shortcut") {!local.state.value.busy && local.state.value.locations.size==1}
            withContext(Dispatchers.Main.immediate) {local.browse()}; waitFor("local root") {!local.state.value.busy && local.state.value.rows.size==1}
            withContext(Dispatchers.Main.immediate) {local.browse(local.state.value.rows.single().entry)}
            waitFor("actual local binary") {!local.state.value.busy && local.state.value.rows.any {it.entry.name=="kutuma.bin"}}
            val source=withContext(Dispatchers.Main.immediate) {local.captureUpload(local.state.value.rows.single {it.entry.name=="kutuma.bin"}.entry)}
            var manager=PhoneUploads(context,identity.id); uploads=manager
            val id=manager.stage(approval,source) {policy.isCurrent(approval)}
            waitFor("private preparation") {manager.state.value.single().let {!it.active && it.phase==PhoneUploadPhase.READY}}
            suspend fun request(value:FileRequest):FileResponse {
                if(value.operation==FileOperation.PREPARE_UPLOAD) preparations++
                val response=raw(value)
                if(value.operation==FileOperation.UPLOAD_WRITE && !lost && response.status==FileStatus.FILE_STATUS_OK) {
                    check(response.upload.offset==StorageRules.RANGE.toLong()); lost=true; throw IOException("Generated loss of an accepted native range acknowledgement.")
                }; return response
            }
            manager.send(id,approval,target,{policy.isCurrent(approval)},::request)
            waitFor("lost acknowledgement checkpoint") {!manager.state.value.single().active}
            val stopped=manager.state.value.single()
            val accepted=raw(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_STATUS).setTransferId(stopped.remote).setMutationEpoch(stopped.remoteEpoch).build())
            check(lost && preparations==1 && stopped.phase==PhoneUploadPhase.REVIEW && stopped.offset==0L && accepted.status==FileStatus.FILE_STATUS_OK &&
                accepted.upload.id==stopped.remote && accepted.upload.offset==StorageRules.RANGE.toLong())
            status(JSONObject().put("event","checkpoint").put("receipt",stopped.remote).put("senderOffset",stopped.offset).put("receiverOffset",accepted.upload.offset))
            manager.shutdown(); manager=PhoneUploads(context,identity.id); uploads=manager
            check(manager.state.value.single().remote==stopped.remote)
            manager.send(id,approval,null,{policy.isCurrent(approval)},::request); waitFor("resumed original upload") {!manager.state.value.single().active}
            completed=manager.state.value.single(); val done=requireNotNull(completed)
            check(done.phase==PhoneUploadPhase.COMPLETED && done.remote==stopped.remote && preparations==1 && done.size==PAYLOAD_BYTES.toLong() &&
                Rules.hex(Rules.decode(done.hash))==originalHash && digest(bytes("kutuma.bin"))==originalHash)
            manager.verifyPublished(id,approval,{policy.isCurrent(approval)},::raw); waitFor("published-copy check") {!manager.state.value.single().active}
            sent.complete(Unit); status(JSONObject().put("event","sent").put("sha256",originalHash).put("bytes",PAYLOAD_BYTES)
                .put("preparations",preparations).put("lostAcknowledgements",1).put("resumedOriginalReceipt",true))
            val result=verified.await(); finished.await(); success=true; result
        } finally {withContext(NonCancellable) {
            commands?.cancelAndJoin(); withContext(Dispatchers.Main.immediate) {peer?.close()}; scope.cancel(); scope.coroutineContext.job.join()
            uploads?.shutdown(); service.close(); incoming.shutdown(); grants.batchPlans.shutdown()
            browser?.let {local->withContext(Dispatchers.Main.immediate) {local.close()}.join()}
            LocalDocumentLocations(context).all().forEach {LocalDocumentLocations(context).remove(it.id)}
            AndroidSafAccess(context).reconcile("incoming:",emptyMap()); AndroidSafAccess(context).reconcile("batch-incoming:",emptyMap())
            grants.all().forEach {grants.remove(it.id)}
            if(success) targetContext.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"dispose",data.getString("root"),null)
            listOf("iconnect.proof","iconnect.storage","iconnect.saf-access").forEach {targetContext.deleteSharedPreferences(prefix+it)}
        }}
    }
    companion object {private const val PAYLOAD_BYTES=2*1024*1024+17}
}
