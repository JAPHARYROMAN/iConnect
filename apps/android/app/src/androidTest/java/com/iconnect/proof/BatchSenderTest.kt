package com.iconnect.proof

import android.content.Context
import android.content.ContextWrapper
import android.net.Uri
import android.os.Bundle
import android.provider.DocumentsContract as Docs
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.google.protobuf.ByteString
import com.iconnect.protocol.*
import kotlinx.coroutines.*
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.io.IOException
import java.security.Signature
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLong

/** Both sides use production private snapshots, durable receivers and real SAF publication. */
@RunWith(AndroidJUnit4::class)
class BatchSenderTest {
    internal class Fixture(val large:Boolean=false,extras:Int=0) {
        val receiver=IncomingUploadsTest.Fixture()
        val base=receiver.base
        val prefix="batch-send-test-"+StorageRules.token()
        val root=File(base.filesDir,prefix).apply {check(mkdir())}
        val context=object:ContextWrapper(base) {
            override fun getFilesDir()=root
            override fun getCacheDir()=File(root,"cache").apply {mkdirs()}
            override fun getApplicationContext():Context=this
            override fun getSharedPreferences(name:String,mode:Int)=base.getSharedPreferences(prefix+name,mode)
        }
        // A separate software identity is confined to the test APK. Both approvals have real signatures.
        val policy=AndroidOwnerPolicy(context,receiver.approval.id)
        val approval:PeerApproval
        val resources=PhoneOutgoingResources.isolated(PhoneOutgoingResources.BATCH)
        var sources=AndroidBatchSources(context,policy,resources)
        val payloadNow = AtomicLong(10_000)
        var sender=PhoneBatchSender(context,policy,sources,payloadNow::get)
        val data=requireNotNull(base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"fixture",null,Bundle().apply {putBoolean("emptyFolder",true); putBoolean("largeFile",large); putInt("extraFiles",extras)}))
        val tree=Uri.parse(data.getString("tree"))
        val allowed=AtomicBoolean(true)
        lateinit var browser:PhoneLocalBrowser
        lateinit var source:PhoneBatchSource
        lateinit var target:FileEntry
        var begins=0; var directories=0; var writes=0; var calls=0
        init {
            val own=LocalIdentity(); val key=receiver.software
            val unsigned=Pairing.newBuilder().setTransactionId(StorageRules.token()).setToken(StorageRules.token()).setExpiresAt(System.currentTimeMillis()+120_000)
                .setHost(own.descriptor("Receiving phone"))
                .setGuest(Identity.newBuilder().setName("Sending phone").setPublicKey(ByteString.copyFrom(key.public.encoded))).build()
            val transcript=Rules.pairing(unsigned)
            val guest=Signature.getInstance("SHA256withECDSA").run {initSign(key.private); update(transcript); sign()}
            approval=policy.commitPair(policy.beginPair(own.publicKey),unsigned.toBuilder().setHostApproval(ByteString.copyFrom(own.sign(transcript))).setGuestApproval(ByteString.copyFrom(guest)).build())
            val zero=requireNotNull(Docs.createDocument(base.contentResolver,Docs.buildDocumentUriUsingTree(tree,Docs.getTreeDocumentId(tree)),"application/octet-stream","zero.bin"))
            base.contentResolver.openOutputStream(zero)!!.use {it.write(byteArrayOf())}
        }
        fun main(action:()->Unit)=InstrumentationRegistry.getInstrumentation().runOnMainSync(action)
        suspend fun initialize() {
            receiver.initialize(); target=receiver.call(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_CONTEXT).build()).entriesList.single()
            main {browser=PhoneLocalBrowser(context); browser.setVisible(true); browser.add(tree,data.getInt("flags"))}
            withTimeout(20_000) {while(browser.state.value.busy || browser.state.value.rows.size!=1) delay(15)}
            prepareAnother()
            assertTrue(AndroidStorageGrants(context,policy).all().isEmpty())
        }
        suspend fun prepareAnother(collision:UploadCollision=UploadCollision.UPLOAD_KEEP_BOTH):String {
            lateinit var selected:LocalUploadSource; main {selected=browser.captureBatch(listOf(browser.state.value.rows.single().entry)).single()}
            val id=sources.stage(approval,listOf(selected),collision) {allowed.get()}
            withTimeout(30_000) {sources.whenIdle()}; source=sources.state.value.single {it.id==id}
            assertEquals(source.message,PhoneBatchSourcePhase.READY,source.phase)
            return id
        }
        fun queue()=sender.queue(source.id)
        fun row()=sender.state.value.single()
        suspend fun request(value:FileRequest):FileResponse {
            calls++; if(value.operation==FileOperation.BEGIN_UPLOAD_BATCH) begins++
            if(value.operation==FileOperation.CREATE_BATCH_DIRECTORY) directories++
            if(value.operation==FileOperation.WRITE_BATCH_FILE) writes++
            return receiver.raw(value)
        }
        suspend fun send(id:String,destination:FileEntry?=target,request:suspend (FileRequest)->FileResponse={this.request(it)}) {
            // Functional recovery fixtures also send 70 real provider items.
            // Keep a finite per-item budget; throughput is a separate release gate.
            val items=sender.state.value.single {it.id==id}.items
            val limit=maxOf(60_000L,minOf(180_000L,items.toLong()*2_000L))
            val started=android.os.SystemClock.elapsedRealtime(); val initialCalls=calls
            try {withTimeout(limit) {sender.send(id,destination,{allowed.get()},request).join()}}
            catch(e:TimeoutCancellationException) {
                val current=sender.state.value.singleOrNull {it.id==id}
                throw AssertionError("Native fixture send timed out: items=$items next=${current?.next} phase=${current?.phase} limitMs=$limit calls=${calls-initialCalls}",e)
            } finally {
                val current=sender.state.value.singleOrNull {it.id==id}
                android.util.Log.i("IConnectBatchFixture","items=$items next=${current?.next} phase=${current?.phase} elapsedMs=${android.os.SystemClock.elapsedRealtime()-started} limitMs=$limit calls=${calls-initialCalls}")
            }
            sender.whenIdle(); source=sources.state.value.single {it.id==source.id}
        }
        suspend fun reopen(both:Boolean=false) {sender.shutdown(); sender=PhoneBatchSender(context,policy,sources,payloadNow::get); if(both) receiver.restart(reopenGrants=true)}
        suspend fun reopenPrivateSources(change:()->Unit={}) {
            sender.shutdown(); sources.shutdown(); change(); sources=AndroidBatchSources(context,policy,resources); sender=PhoneBatchSender(context,policy,sources,payloadNow::get)
            sources.state.value.singleOrNull {it.id==source.id}?.let {source=it}
        }
        fun sourceBytes(name:String)=base.contentResolver.openInputStream(Docs.buildDocumentUriUsingTree(tree,when(name) {"simu.txt"->data.getString("file")!!; else->children(tree).entries.single {it.value==name}.key}))!!.use {it.readBytes()}
        fun children(tree:Uri,parent:String=Docs.getTreeDocumentId(tree)):Map<String,String> {
            val result=linkedMapOf<String,String>()
            base.contentResolver.query(Docs.buildChildDocumentsUriUsingTree(tree,parent),arrayOf(Docs.Document.COLUMN_DOCUMENT_ID,Docs.Document.COLUMN_DISPLAY_NAME),null,null,null)!!.use {while(it.moveToNext()) result[it.getString(0)]=it.getString(1)}
            return result
        }
        fun receivedRoot()=receiver.children().entries.single {it.value==sender.itemResult(row().id,0).name}.key
        fun record(id:String)=File(root,"outgoing-batch-jobs/$id.json")
        fun assertReleased() {assertEquals(0,resources.activeWorkers)}
        suspend fun close() {
            sender.shutdown(); sources.shutdown()
            if(::browser.isInitialized) {lateinit var job:Job; main {job=browser.close()}; job.join()}
            LocalDocumentLocations(context).all().forEach {LocalDocumentLocations(context).remove(it.id)}
            base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"dispose",data.getString("root"),null)
            listOf("iconnect.proof","iconnect.storage","iconnect.saf-access").forEach {base.deleteSharedPreferences(prefix+it)}
            check(root.canonicalFile.parentFile==base.filesDir.canonicalFile && root.name==prefix); root.deleteRecursively()
            receiver.close()
        }
    }
    private suspend fun fixture(large:Boolean=false,body:suspend (Fixture)->Unit) {val f=Fixture(large); try {f.initialize(); body(f)} finally {f.close()}}
    private fun completed(f:Fixture) {val row=f.row(); assertEquals(row.message,PhoneBatchSendingPhase.COMPLETED,row.phase); assertEquals(row.items,row.next); assertEquals(row.outcomes,row.receiverOutcomes); assertEquals(1,f.begins); f.assertReleased()}

    @Test fun batchPayloadEstimatesFollowValidatedFileAcknowledgementsAndForgetPausedAttempts() = runBlocking {
        fixture(large = true) { f ->
            val id = f.queue()
            val bytes = f.sourceBytes("kutuma.bin")
            val item = f.sources.manifest(f.source.id).itemsList.indexOfFirst { it.name == "kutuma.bin" }
            val first = CompletableDeferred<Unit>()
            val release = CompletableDeferred<Unit>()
            val second = CompletableDeferred<Unit>()
            val range = StorageRules.RANGE.toLong()
            val limit = 65_536L
            fun payload(cap: Long = limit) = f.sender.readPayloads(cap)[id]

            val sending = f.sender.send(id, f.target, { f.allowed.get() }) { request ->
                val response = f.request(request)
                if (request.operation == FileOperation.WRITE_BATCH_FILE && request.batchItemIndex == item) {
                    if (request.offset == 0L) {
                        first.complete(Unit)
                        release.await()
                    } else {
                        second.complete(Unit)
                        awaitCancellation()
                    }
                }
                response
            }
            withTimeout(20_000) { first.await() }
            val baseline = requireNotNull(payload())
            assertEquals("kutuma.bin", baseline.name)
            assertEquals(bytes.size.toLong(), baseline.size)
            assertEquals(0L, baseline.offset)
            assertEquals(PhonePayloadRateState.MEASURING, baseline.estimate.state)
            f.payloadNow.addAndGet(2500)
            release.complete(Unit)
            withTimeout(20_000) { second.await() }
            val measured = requireNotNull(payload())
            assertEquals(range, measured.offset)
            assertEquals(PhonePayloadRateState.AVAILABLE, measured.estimate.state)
            assertEquals(range * 1000 / 2500, measured.estimate.bytesPerSecond)
            f.payloadNow.addAndGet(10_001)
            assertEquals(PhonePayloadRateState.WAITING, requireNotNull(payload()).estimate.state)
            assertEquals(PhonePayloadRateState.MEASURING, requireNotNull(payload(0)).estimate.state)

            f.sender.pause(id)
            assertNull(payload())
            withTimeout(20_000) { sending.join(); f.sender.whenIdle() }
            assertEquals(range, f.row().offset)
            f.reopen()
            assertNull(payload())
            var resumed = false
            var finished = false
            f.send(id, null) { request ->
                if (request.operation == FileOperation.WRITE_BATCH_FILE && request.batchItemIndex == item && !resumed) {
                    val current = requireNotNull(payload())
                    assertEquals(range * 2, current.offset)
                    assertEquals(PhonePayloadRateState.MEASURING, current.estimate.state)
                    resumed = true
                }
                if (request.operation == FileOperation.FINISH_BATCH_FILE && request.batchItemIndex == item) {
                    val current = requireNotNull(payload())
                    assertEquals(bytes.size.toLong(), current.offset)
                    assertEquals(PhonePayloadRateState.RECEIVED, current.estimate.state)
                    finished = true
                }
                f.request(request)
            }
            assertTrue(resumed && finished)
            completed(f)
            assertNull(payload())
            assertArrayEquals(bytes, f.receiver.read("kutuma.bin", f.receivedRoot()))
        }
    }

    @Test fun batchForecastCarriesAcknowledgementsAcrossFilesAndDropsSkippedBranchesOnResume() = runBlocking {
        fixture { f ->
            val resolver = f.base.contentResolver
            val root = Docs.buildDocumentUriUsingTree(f.tree, Docs.getTreeDocumentId(f.tree))
            val later = ByteArray(1024) { (it % 251).toByte() }
            for ((name, bytes) in listOf("first.bin" to ByteArray(8192), "second.bin" to ByteArray(4096), "later.bin" to later)) {
                val uri = requireNotNull(Docs.createDocument(resolver, root, "application/octet-stream", name))
                requireNotNull(resolver.openOutputStream(uri)).use { it.write(bytes) }
            }
            val reports = f.receiver.children().entries.single { it.value == "Reports" }.key
            val original = f.receiver.read("notes.txt", reports)
            f.main { f.browser.browse(f.browser.state.value.rows.single().entry) }
            val names = listOf("first.bin", "Reports", "second.bin", "later.bin")
            withTimeout(20_000) {
                while (f.browser.state.value.busy || !f.browser.state.value.rows.map { it.entry.name }.containsAll(names)) delay(15)
            }
            lateinit var selection: List<LocalUploadSource>
            f.main {
                val rows = f.browser.state.value.rows
                selection = f.browser.captureBatch(names.map { name -> rows.single { it.entry.name == name }.entry })
            }
            val source = f.sources.stage(f.approval, selection, UploadCollision.UPLOAD_SKIP) { f.allowed.get() }
            withTimeout(30_000) { f.sources.whenIdle() }
            f.source = f.sources.state.value.single { it.id == source }
            val manifest = f.sources.manifest(source)
            fun index(name: String) = manifest.itemsList.indexOfFirst { it.name == name }
            val id = f.queue()
            val firstIndex = index("first.bin")
            val secondIndex = index("second.bin")
            val blockedIndex = index("notes.txt")
            val blockedBytes = manifest.getItems(blockedIndex).size
            assertTrue(blockedBytes > 0)
            fun payload() = requireNotNull(f.sender.readPayloads(0)[id])

            var sampledSecond = false
            f.send(id) { request ->
                if (request.operation == FileOperation.WRITE_BATCH_FILE) {
                    assertNotEquals(blockedIndex, request.batchItemIndex)
                    if (request.batchItemIndex == secondIndex) {
                        val current = payload()
                        assertEquals(4096L + 1024L, current.batchRemaining)
                        assertEquals(PhonePayloadRateState.MEASURING, current.batchEstimate.state)
                    }
                    f.payloadNow.addAndGet(if (request.batchItemIndex == firstIndex) 1000 else 1500)
                }
                if (request.operation == FileOperation.FINISH_BATCH_FILE) {
                    val current = payload()
                    assertEquals(PhonePayloadRateState.RECEIVED, current.estimate.state)
                    if (request.batchItemIndex == firstIndex) assertEquals(blockedBytes + 4096L + 1024L, current.batchRemaining)
                    if (request.batchItemIndex == secondIndex) {
                        assertEquals(1024L, current.batchRemaining)
                        assertEquals(PhonePayloadRateState.AVAILABLE, current.batchEstimate.state)
                        assertEquals((8192L + 4096L) * 1000 / 2500, current.batchEstimate.bytesPerSecond)
                        assertEquals(1L, current.batchEstimate.remainingSeconds)
                        sampledSecond = true
                        throw IOException("Fixture interruption before saving the second file.")
                    }
                }
                f.request(request)
            }
            assertTrue(sampledSecond)
            assertEquals(PhoneBatchSendingPhase.REVIEW, f.row().phase)
            assertTrue(f.sender.readPayloads(0).isEmpty())
            f.reopen()
            var reconciled = false
            f.send(id, null) { request ->
                if (request.operation == FileOperation.FINISH_BATCH_FILE && request.batchItemIndex == secondIndex) {
                    val current = payload()
                    assertEquals(1024L, current.batchRemaining)
                    assertEquals(PhonePayloadRateState.MEASURING, current.batchEstimate.state)
                    reconciled = true
                }
                f.request(request)
            }
            assertTrue(reconciled)
            completed(f)
            assertArrayEquals(original, f.receiver.read("notes.txt", reports))
            assertArrayEquals(later, f.receiver.read("later.bin"))
        }
    }

    @Test fun negotiatedNativeBatchPublishesToAnApprovedReceiveOnlyFolder():Unit=runBlocking {fixture {f->
        f.receiver.grants.update(f.receiver.grant.id,StorageRules.WRITE or StorageRules.CREATE)
        val scope=CoroutineScope(SupervisorJob()+Dispatchers.Main.immediate)
        val senderReady=CompletableDeferred<Unit>()
        val receiverReady=CompletableDeferred<Unit>()
        val local=LocalIdentity()
        val key=f.receiver.software
        val identity=object:SessionIdentity {
            override val publicKey=key.public.encoded
            override val id=Rules.id(publicKey)
            override fun sign(transcript:ByteArray)=Signature.getInstance("SHA256withECDSA").run {initSign(key.private); update(transcript); sign()}
        }
        lateinit var sender:RtcTestSession
        lateinit var receiver:RtcTestSession
        val session=StorageRules.token()
        fun signal(peer:RtcTestSession,kind:String,body:String,mid:String) {
            when(kind) {"offer"->peer.acceptOffer(body); "answer"->peer.acceptAnswer(body); "candidate"->peer.addCandidate(body,mid)}
        }
        try {
            f.main {
                receiver=RtcTestSession(f.base,scope,local,session,identity.publicKey,
                    {kind,body,mid->signal(sender,kind,body,mid)}, {}, {if(it) receiverReady.complete(Unit)},fileService=f.receiver.service)
                sender=RtcTestSession(f.base,scope,identity,session,local.publicKey,
                    {kind,body,mid->signal(receiver,kind,body,mid)}, {}, {if(it) senderReady.complete(Unit)},initiator=true)
                sender.offer()
            }
            withTimeout(20_000) {senderReady.await(); receiverReady.await()}
            withContext(Dispatchers.Main.immediate) {
                assertTrue(sender.acceptsFolderBatches)
                assertFalse(receiver.acceptsFolderBatches)
                val unsupported=runCatching {receiver.requestFiles(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_BATCH_CONTEXT).build())}
                assertTrue(unsupported.exceptionOrNull() is IllegalArgumentException)
            }
            suspend fun request(value:FileRequest):FileResponse=withContext(Dispatchers.Main.immediate) {
                sender.requestFiles(value.toBuilder().setRequestId(StorageRules.token()).build())
            }
            val target=request(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_CONTEXT).build()).entriesList.single()
            assertTrue(request(FileRequest.newBuilder().setOperation(FileOperation.LIST_ROOTS).build()).entriesList.isEmpty())
            val expected=f.sourceBytes("simu.txt")
            f.send(f.queue(),target,::request)
            val row=f.row()
            assertEquals(row.message,PhoneBatchSendingPhase.COMPLETED,row.phase)
            assertEquals(row.outcomes,row.receiverOutcomes)
            val manifest=f.sources.manifest(f.source.id)
            fun publishedName(name:String)=f.sender.itemResult(row.id,manifest.itemsList.indexOfFirst {it.name==name}).name
            val parent=f.receivedRoot()
            assertNotEquals(manifest.itemsList.first().name,f.receiver.children()[parent])
            assertArrayEquals(expected,f.receiver.read(publishedName("simu.txt"),parent))
            assertArrayEquals(byteArrayOf(),f.receiver.read(publishedName("zero.bin"),parent))
            assertTrue(f.receiver.children(parent).values.contains(publishedName("Empty")))
            assertArrayEquals(expected,f.sourceBytes("simu.txt"))
            val header=UploadBatchHeader.parseFrom(Rules.decode(row.header))
            f.receiver.grants.update(f.receiver.grant.id,StorageRules.READ_ONLY)
            val denied=request(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_BATCH_STATUS).setTransferId(header.id).setMutationEpoch(header.epoch).build())
            assertEquals(FileStatus.ACCESS_DENIED,denied.status)
        } finally {f.main {runCatching {sender.close()}; runCatching {receiver.close()}}; scope.cancel()}
    }}

    @Test fun actualPrivateFilesEmptyFoldersAndZeroBytesPublishWithoutReopeningOriginals()=runBlocking {fixture {f->
        val events=mutableListOf<PhoneActivityEvent>(); val receiverEvents=mutableListOf<PhoneActivityEvent>()
        f.sender.activity=events::add; f.receiver.grants.activity=receiverEvents::add
        val before=f.sourceBytes("simu.txt"); val id=f.queue()
        f.base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"change",f.data.getString("file"),Bundle().apply {putString("content","owner changed original")})
        f.send(id); completed(f); val parent=f.receivedRoot()
        assertArrayEquals(before,f.receiver.read("simu.txt",parent)); assertArrayEquals(byteArrayOf(),f.receiver.read("zero.bin",parent)); assertTrue(f.receiver.children(parent).values.contains("Empty"))
        assertEquals("owner changed original",String(f.sourceBytes("simu.txt"))); assertEquals(0L,f.receiver.grants.batchPlans.reservedBytes(null))
        assertTrue(f.sources.manifest(f.source.id).itemsList.any {it.name==".ssh" && it.error.isNotEmpty()})
        assertTrue(Rules.decode(f.row().receiverOutcomes).any {it.toInt()==UploadBatchItemPhase.BATCH_ITEM_FAILED.number})
        assertEquals(PhoneActivityOutcome.COMPLETED_WITH_ISSUES,events.last().outcome)
        assertEquals(PhoneActivityOutcome.COMPLETED_WITH_ISSUES,receiverEvents.last {it.kind==PhoneActivityKind.BATCH_RECEIVE}.outcome)
        assertFalse(f.record(id).readText().contains(f.tree.toString())); f.reopen(true); completed(f)
        repeat(f.row().next) {assertNotNull(f.sender.itemResult(id,it))}
    }}
    @Test fun stoppingCompletedBatchCheckUsesNegotiatedRemoteCancellationAndKeepsCompletedHistory()=runBlocking {
        for(negotiated in listOf(false,true)) fixture {f->
            f.main {f.browser.browse(f.browser.state.value.rows.single().entry)}
            withTimeout(20_000) {while(f.browser.state.value.busy || f.browser.state.value.rows.none {it.entry.name=="simu.txt"}) delay(15)}
            lateinit var selected:List<LocalUploadSource>
            f.main {selected=f.browser.captureBatch(f.browser.state.value.rows.filter {it.entry.name in setOf("simu.txt","zero.bin","Empty","Reports")}.map {it.entry})}
            assertEquals(4,selected.size)
            val source=f.sources.stage(f.approval,selected,UploadCollision.UPLOAD_KEEP_BOTH) {f.allowed.get()}
            withTimeout(30_000) {f.sources.whenIdle()}; f.source=f.sources.state.value.single {it.id==source}
            val id=f.queue(); f.send(id); completed(f)
            val before=f.record(id).readBytes(); val bytes=f.sourceBytes("simu.txt")
            val entered=CompletableDeferred<FileRequest>(); val stops=java.util.concurrent.CopyOnWriteArrayList<FileRequest>()
            val checking=async(Dispatchers.IO) {runCatching {
                f.sender.verifyPublished(id,f.approval,{f.allowed.get()},negotiated) {value->
                    when(value.operation) {
                        FileOperation.VERIFY_PUBLISHED_BATCH->{f.request(value); entered.complete(value); awaitCancellation()}
                        FileOperation.CANCEL_PUBLISHED_BATCH_CHECK->{stops.add(value); f.request(value)}
                        else->error("Unexpected batch check operation")
                    }
                }
            }}
            val original=withTimeout(5_000) {entered.await()}; f.sender.pause(id)
            assertTrue(withTimeout(8_000) {checking.await()}.exceptionOrNull() is CancellationException)
            withTimeout(3_000) {f.sender.whenIdle()}
            assertEquals(negotiated,stops.isNotEmpty())
            stops.forEach {assertEquals(original.mutationId,it.mutationId); assertEquals(original.batchHeader,it.batchHeader); assertEquals(original.mutationEpoch,it.mutationEpoch); assertEquals(original.transferId,it.transferId)}
            if(negotiated) withTimeout(3_000) {f.receiver.grants.batchPlans.whenFilesIdle()}
            else {f.receiver.service.suspendAvailability(); withTimeout(3_000) {f.receiver.grants.batchPlans.whenFilesIdle()}; f.receiver.restart()}
            assertArrayEquals(before,f.record(id).readBytes()); assertArrayEquals(bytes,f.sourceBytes("simu.txt")); completed(f)
            f.sender.verifyPublished(id,f.approval,{f.allowed.get()},true) {f.request(it)}
        }
    }

    @Test fun completedBatchChecksBindFreshChallengesAndRejectChangedParentEvidence()=runBlocking {fixture {f->
        f.main {f.browser.browse(f.browser.state.value.rows.single().entry)}
        withTimeout(20_000) {while(f.browser.state.value.busy || f.browser.state.value.rows.none {it.entry.name=="simu.txt"}) delay(15)}
        lateinit var selected:List<LocalUploadSource>
        f.main {selected=f.browser.captureBatch(f.browser.state.value.rows.filter {it.entry.name in setOf("simu.txt","zero.bin","Empty","Reports")}.map {it.entry})}
        assertEquals(4,selected.size)
        val source=f.sources.stage(f.approval,selected,UploadCollision.UPLOAD_KEEP_BOTH) {f.allowed.get()}
        withTimeout(30_000) {f.sources.whenIdle()}; f.source=f.sources.state.value.single {it.id==source}
        val id=f.queue(); f.send(id); completed(f); val row=f.row(); val saved=f.record(id).readBytes()
        val h=UploadBatchHeader.parseFrom(Rules.decode(row.header))
        for(kind in listOf("match","old-receipt","challenge","epoch","header","mixed","effect","canceled")) {
            val seen=mutableListOf<String>()
            val failure=runCatching {
                withTimeout(15_000) {f.sender.verifyPublished(id,f.approval,{f.allowed.get()}) {request->
                    assertEquals(FileOperation.VERIFY_PUBLISHED_BATCH,request.operation); assertEquals(h,request.batchHeader)
                    seen+=request.mutationId
                    val reply=f.request(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_BATCH_STATUS)
                        .setTransferId(h.id).setMutationEpoch(h.epoch).build()).toBuilder()
                    if(kind!="old-receipt") reply.setPublishedBatchCheck(PublishedBatchCheck.newBuilder()
                        .setChallenge(if(kind=="challenge") StorageRules.token() else request.mutationId).setVerified(seen.size>1))
                    if(kind=="epoch") reply.setMutationEpoch(StorageRules.token())
                    if(kind=="header") reply.setBatch(reply.batch.toBuilder().setHeader(h.toBuilder().setSha256(ByteString.copyFrom(ByteArray(32)))))
                    if(kind=="mixed") reply.setBatch(reply.batch.toBuilder().setItemOutcomes(ByteString.copyFrom(ByteArray(row.items) {2})))
                    if(kind=="effect") reply.setBatch(reply.batch.toBuilder().setEffectsStarted(false))
                    if(kind=="canceled") reply.setPublishedBatchCheck(reply.publishedBatchCheck.toBuilder().setCanceled(true))
                    reply.build()
                }}
            }.exceptionOrNull()
            f.sender.whenIdle()
            if(kind=="match") {assertNull(failure); assertEquals(2,seen.size); assertEquals(1,seen.distinct().size)}
            else {
                assertTrue("$kind: $failure",failure !is CancellationException && (failure is IllegalArgumentException || failure is IllegalStateException))
                assertEquals(1,seen.size)
            }
            assertArrayEquals(saved,f.record(id).readBytes()); assertFalse(f.row().active)
        }
        val entered=CompletableDeferred<Unit>()
        val check=async {f.sender.verifyPublished(id,f.approval,{f.allowed.get()}) {entered.complete(Unit); awaitCancellation()}}
        withTimeout(5_000) {entered.await()}; f.sender.pause(id)
        assertTrue(runCatching {check.await()}.exceptionOrNull() is CancellationException)
        f.sender.whenIdle(); assertArrayEquals(saved,f.record(id).readBytes())
    }}
    @Test fun cleanSelectedFilesAndFoldersReportVerifiedCompletionOnBothSides()=runBlocking {fixture {f->
        f.main {f.browser.browse(f.browser.state.value.rows.single().entry)}
        withTimeout(20_000) {while(f.browser.state.value.busy || f.browser.state.value.rows.none {it.entry.name=="simu.txt"}) delay(15)}
        lateinit var selected:List<LocalUploadSource>
        f.main {selected=f.browser.captureBatch(f.browser.state.value.rows.filter {it.entry.name in setOf("simu.txt","zero.bin","Empty","Reports")}.map {it.entry})}
        assertEquals(4,selected.size)
        val sourceId=f.sources.stage(f.approval,selected,UploadCollision.UPLOAD_KEEP_BOTH) {f.allowed.get()}
        withTimeout(30_000) {f.sources.whenIdle()}; f.source=f.sources.state.value.single {it.id==sourceId}
        assertEquals(PhoneBatchSourcePhase.READY,f.source.phase)
        val manifest=f.sources.manifest(sourceId); assertTrue(manifest.itemsList.all {it.error.isEmpty()})
        val events=mutableListOf<PhoneActivityEvent>(); val receiverEvents=mutableListOf<PhoneActivityEvent>()
        f.sender.activity=events::add; f.receiver.grants.activity=receiverEvents::add
        val expected=f.sourceBytes("simu.txt"); val id=f.queue(); f.send(id); completed(f)
        assertTrue(Rules.decode(f.row().receiverOutcomes).all {it.toInt()==UploadBatchItemPhase.BATCH_ITEM_SUCCEEDED.number})
        assertEquals(PhoneActivityOutcome.COMPLETED,events.last().outcome); assertEquals(f.source.bytes,events.last().bytes)
        assertEquals(PhoneActivityOutcome.COMPLETED,receiverEvents.last {it.kind==PhoneActivityKind.BATCH_RECEIVE}.outcome)
        val result=requireNotNull(f.sender.itemResult(id,manifest.itemsList.indexOfFirst {it.name=="simu.txt"}))
        assertArrayEquals(expected,f.receiver.read(result.name))
    }}
    @Test fun existingDestinationFolderIsSkippedAndBothSidesReportIssuesWithoutChangingOwnerBytes()=runBlocking {fixture {f->
        f.prepareAnother(UploadCollision.UPLOAD_SKIP)
        val name=f.sources.manifest(f.source.id).itemsList.first().name
        val existing=requireNotNull(Docs.createDocument(f.base.contentResolver,Docs.buildDocumentUriUsingTree(f.receiver.tree,Docs.getTreeDocumentId(f.receiver.tree)),Docs.Document.MIME_TYPE_DIR,name))
        val sentinel=requireNotNull(Docs.createDocument(f.base.contentResolver,existing,"text/plain","owner.txt"))
        f.base.contentResolver.openOutputStream(sentinel)!!.use {it.write("Keep owner bytes".toByteArray())}
        val events=mutableListOf<PhoneActivityEvent>(); val receiverEvents=mutableListOf<PhoneActivityEvent>()
        f.sender.activity=events::add; f.receiver.grants.activity=receiverEvents::add
        val id=f.queue(); f.send(id); completed(f)
        val terminal=events.single {it.outcome==PhoneActivityOutcome.COMPLETED_WITH_ISSUES}
        assertEquals(-1L,terminal.bytes); assertFalse(events.any {it.outcome==PhoneActivityOutcome.COMPLETED})
        assertEquals(PhoneActivityOutcome.COMPLETED_WITH_ISSUES,receiverEvents.last {it.kind==PhoneActivityKind.BATCH_RECEIVE}.outcome)
        assertEquals(listOf("owner.txt"),f.receiver.children(Docs.getDocumentId(existing)).values.toList())
        assertArrayEquals("Keep owner bytes".toByteArray(),f.receiver.read("owner.txt",Docs.getDocumentId(existing)))
    }}
    @Test fun lostAdmissionReplyChecksTheOriginalIntentAfterRestart()=runBlocking {fixture {f->
        val id=f.queue(); var lost=false
        f.send(id) {value->val response=f.request(value); if(value.operation==FileOperation.BEGIN_UPLOAD_BATCH && !lost) {lost=true; throw IOException("Lost admission reply")}; response}
        assertTrue(lost); assertEquals(PhoneBatchSendingPhase.REVIEW,f.row().phase); val header=f.row().header
        f.reopen(true); f.send(id,null); completed(f); assertEquals(header,f.row().header)
    }}
    @Test fun lostDirectoryReplyUsesStatusWithoutRepeatingCreation()=runBlocking {fixture {f->
        val id=f.queue(); var lost=false
        f.send(id) {value->val response=f.request(value); if(value.operation==FileOperation.CREATE_BATCH_DIRECTORY && !lost) {lost=true; throw IOException("Lost directory reply")}; response}
        assertTrue(lost); val attempted=f.directories; f.reopen(true); f.send(id,null); completed(f)
        assertEquals(f.sources.manifest(f.source.id).itemsList.count {it.isDirectory && it.error.isEmpty()},f.directories); assertEquals(1,attempted)
    }}
    @Test fun lostRangeReplyAndBothHostRestartsResumeVerifiedPrivateBytes()=runBlocking {fixture(true) {f->
        val before=f.sourceBytes("kutuma.bin"); val id=f.queue(); var lost=false
        f.send(id) {value->val response=f.request(value); if(value.operation==FileOperation.WRITE_BATCH_FILE && !lost) {lost=true; throw IOException("Lost accepted range reply")}; response}
        assertTrue(lost); assertEquals(PhoneBatchSendingPhase.REVIEW,f.row().phase); f.assertReleased(); val header=f.row().header
        f.reopen(true); f.send(id,null); completed(f); assertEquals(header,f.row().header); assertArrayEquals(before,f.receiver.read("kutuma.bin",f.receivedRoot()))
    }}
    @Test fun lostFinalReplyUsesCompletedParentReceipt()=runBlocking {fixture {f->
        val id=f.queue(); var lost=false
        f.send(id) {value->val response=f.request(value); if(value.operation==FileOperation.FINISH_UPLOAD_BATCH && !lost) {lost=true; throw IOException("Lost completion reply")}; response}
        assertTrue(lost); assertEquals(f.row().items,f.row().next); f.reopen(true); f.send(id,null); completed(f)
    }}
    @Test fun missingOriginalReceiptCannotBeginAnotherPlan()=runBlocking {fixture {f->
        val id=f.queue(); f.send(id) {value->val response=f.request(value); if(value.operation==FileOperation.BEGIN_UPLOAD_BATCH) throw IOException("Lost reply"); response}
        f.reopen(); f.send(id,null) {value->if(value.operation==FileOperation.UPLOAD_BATCH_STATUS) FileResponse.newBuilder().setRequestId(value.requestId).setStatus(FileStatus.NOT_FOUND).setMessage("Original receipt missing").build() else f.request(value)}
        assertEquals(PhoneBatchSendingPhase.REVIEW,f.row().phase); assertEquals(1,f.begins); assertEquals(0,f.directories)
    }}
    @Test fun substitutedFileDigestIsRejectedBeforeSendingBytes()=runBlocking {fixture {f->
        val id=f.queue(); f.send(id) {value->val response=f.request(value); if(value.operation==FileOperation.PREPARE_BATCH_FILE) response.toBuilder().setUpload(response.upload.toBuilder().setSha256(ByteString.copyFrom(ByteArray(32)))).build() else response}
        assertEquals(PhoneBatchSendingPhase.REVIEW,f.row().phase); assertEquals(0,f.writes); f.assertReleased()
    }}
    @Test fun substitutedParentOnValidChildReceiptCannotAdvanceTheCursor()=runBlocking {fixture {f->
        val id=f.queue(); f.send(id) {value->val response=f.request(value); if(value.operation==FileOperation.CREATE_BATCH_DIRECTORY) response.toBuilder().setBatch(response.batch.toBuilder().setHeader(response.batch.header.toBuilder().setId(MutationRules.newId()))).build() else response}
        assertEquals(PhoneBatchSendingPhase.REVIEW,f.row().phase); assertEquals(0,f.row().next); assertEquals(0,f.writes)
    }}
    @Test fun pauseDuringAnAcceptedRangeReleasesWorkerAndResumesOriginalReceipt()=runBlocking {fixture(true) {f->
        val id=f.queue(); var paused=false
        f.send(id) {value->val response=f.request(value); if(value.operation==FileOperation.WRITE_BATCH_FILE && !paused) {paused=true; f.sender.pause(id)}; response}
        assertTrue(paused); assertEquals(PhoneBatchSendingPhase.PAUSED,f.row().phase); f.assertReleased(); f.send(id,null); completed(f)
    }}
    @Test fun pauseAndWaitReleasesTheActiveSlotBeforeAnImmediateOriginalPlanStop()=runBlocking {fixture {f->
        val id=f.queue(); val entered=CompletableDeferred<Unit>()
        val sending=f.sender.send(id,f.target,{f.allowed.get()}) {value->
            val response=f.request(value)
            if(value.operation==FileOperation.WRITE_BATCH_FILE) {entered.complete(Unit); awaitCancellation()}
            response
        }
        withTimeout(15_000) {entered.await()}; withTimeout(5_000) {f.sender.pauseAndWait(id)}
        assertFalse(f.row().active); assertEquals(PhoneBatchSendingPhase.PAUSED,f.row().phase); f.assertReleased()
        f.sender.stop(id,{f.allowed.get()}) {f.request(it)}.join(); f.sender.whenIdle(); sending.join()
        assertFalse(f.row().active); assertEquals(PhoneBatchSendingPhase.STOPPED,f.row().phase); assertEquals(1,f.begins); f.assertReleased()
    }}
    @Test fun withdrawingForegroundPreventsAnotherRequestAndKeepsOriginalFiles()=runBlocking {fixture {f->
        val before=f.sourceBytes("simu.txt"); val id=f.queue(); var withdrawn=false
        f.send(id) {value->assertFalse(withdrawn); val response=f.request(value); if(value.operation==FileOperation.PREPARE_BATCH_FILE) {withdrawn=true; f.allowed.set(false)}; response}
        assertTrue(withdrawn); assertEquals(PhoneBatchSendingPhase.REVIEW,f.row().phase); assertEquals(0,f.writes); f.assertReleased(); assertArrayEquals(before,f.sourceBytes("simu.txt"))
    }}
    @Test fun revokedPairingCannotSendAQueuedSnapshot()=runBlocking {fixture {f->
        val id=f.queue(); f.policy.blockPeer(f.approval); val before=f.calls
        assertThrows(IllegalStateException::class.java) {f.sender.send(id,f.target,{true}) {f.request(it)}}
        assertEquals(before,f.calls); assertEquals(PhoneBatchSendingPhase.READY,f.row().phase)
    }}
    @Test fun interruptedStopRetainsItsBarrierAndContinuesAfterBothHostRestarts()=runBlocking {fixture {f->
        val id=f.queue(); var lost=false
        f.send(id) {value->val response=f.request(value); if(value.operation==FileOperation.WRITE_BATCH_FILE && !lost) {lost=true; throw IOException("Lost accepted range")}; response}
        assertTrue(lost); val original=f.row().header
        f.sender.stop(id,{true}) {value->val response=f.request(value); if(value.operation==FileOperation.STOP_UPLOAD_BATCH) throw CancellationException("Interrupted stop"); response}.join()
        assertEquals(PhoneBatchSendingPhase.STOPPING,f.row().phase); f.reopen(true)
        withTimeout(30_000) {f.sender.stop(id,{true}) {f.request(it)}.join()}
        assertEquals(PhoneBatchSendingPhase.STOPPED,f.row().phase); assertEquals(original,f.row().header); assertEquals(1,f.begins); assertEquals(0L,f.receiver.grants.batchPlans.reservedBytes(null)); f.assertReleased()
    }}
    @Test fun missingRetainedItemResultBlocksColdRecoveryWithoutRemovingCopies()=runBlocking {fixture {f->
        val id=f.queue(); f.send(id); completed(f); val before=f.receiver.children(); f.sender.shutdown()
        assertTrue(File(f.root,"outgoing-batch-jobs/$id.0.json").delete()); f.reopen(); assertTrue(f.sender.recoveryIssues().isNotEmpty()); assertTrue(f.sender.state.value.isEmpty()); assertEquals(before,f.receiver.children())
    }}
    @Test fun changedPrivateSnapshotCannotPrepareOrTransmitThatFile()=runBlocking {fixture {f->
        val id=f.queue(); val index=f.sources.manifest(f.source.id).itemsList.indexOfFirst {it.name=="simu.txt"}; val private=File(f.root,"outgoing-batch-copies/${f.source.id}/$index.bin"); private.writeText("changed private copy")
        var prepared=false
        f.send(id) {value->if(value.operation==FileOperation.PREPARE_BATCH_FILE && value.batchItemIndex==index) prepared=true; f.request(value)}
        assertEquals(PhoneBatchSendingPhase.REVIEW,f.row().phase); assertFalse(prepared); f.assertReleased()
    }}
    @Test fun maximumUnicodeResultTextSurvivesItsBoundedJournalAndColdRead()=runBlocking {fixture {f->
        val id=f.queue(); val name="界".repeat(255); val message="界".repeat(512)
        f.send(id) {value->val response=f.request(value); if(value.operation==FileOperation.CREATE_BATCH_DIRECTORY && value.batchItemIndex==0) response.toBuilder().setBatchItem(response.batchItem.toBuilder().setResultingName(name).setMessage(message)).build() else response}
        completed(f); f.reopen(); assertEquals(name,f.sender.itemResult(id,0).name); assertEquals(message,f.sender.itemResult(id,0).message)
    }}
    @Test fun changedRetainedTerminalOutcomesCannotBeAdoptedOnColdStart()=runBlocking {fixture {f->
        val id=f.queue(); f.send(id); completed(f); val before=f.receiver.children(); f.sender.shutdown()
        val file=f.record(id); val value=JSONObject(file.readText()); val changed=Rules.decode(value.getString("receiverOutcomes")); changed[0]=3
        file.writeText(value.put("receiverOutcomes",Rules.b64(changed)).toString()); f.reopen()
        assertTrue(f.sender.recoveryIssues().isNotEmpty()); assertTrue(f.sender.state.value.isEmpty()); assertEquals(before,f.receiver.children())
    }}
    @Test fun destinationDisplayLabelsAreNotInterpretedAsFileNames()=runBlocking {fixture {f->
        val id=f.queue(); val label="Work: Receive / Inbox"; f.send(id,f.target.toBuilder().setName(label).build()); completed(f)
        f.reopen(); assertTrue(f.sender.recoveryIssues().isEmpty()); assertEquals(label,f.row().destination)
    }}
}
