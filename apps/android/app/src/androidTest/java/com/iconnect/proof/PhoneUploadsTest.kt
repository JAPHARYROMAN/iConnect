package com.iconnect.proof

import android.content.Context
import android.content.ContextWrapper
import android.net.Uri
import android.os.Bundle
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import androidx.test.core.app.ActivityScenario
import androidx.activity.compose.setContent
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import android.view.accessibility.AccessibilityNodeInfo
import com.google.protobuf.ByteString
import com.iconnect.protocol.*
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.json.JSONArray
import java.io.ByteArrayOutputStream
import java.io.File
import java.io.IOException
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLong

/** Real local provider/browser/private snapshots, with deliberately scripted untrusted receiver replies. */
@RunWith(AndroidJUnit4::class)
class PhoneUploadsTest {
    private val instrumentation get()=InstrumentationRegistry.getInstrumentation()
    private fun main(action:()->Unit)=instrumentation.runOnMainSync(action)
    private fun waitFor(label:String,condition:()->Boolean) {
        val end=android.os.SystemClock.elapsedRealtime()+12_000
        while(android.os.SystemClock.elapsedRealtime()<end) {if(condition()) return; Thread.sleep(15)}
        fail(label)
    }
    @Test fun publishedProofBindsReceiptChallengeAndCurrentSession():Unit=runBlocking {
        fixture {_,policy,approved,_,source,uploads,_->
            val prepared=stage(uploads,approved,source) {policy.isCurrent(approved)}
            val receiver=Receiver()
            uploads.send(prepared.id,approved,destination(),{policy.isCurrent(approved)},receiver::request)
            waitFor("completed upload") {uploads.state.value.single().let {!it.active && it.phase==PhoneUploadPhase.COMPLETED}}
            val row=uploads.state.value.single()
            val challenges=mutableListOf<String>()
            val current=AtomicBoolean(true)
            val allowed={current.get() && policy.isCurrent(approved)}
            fun response(request:FileRequest,verified:Boolean)=FileResponse.newBuilder().setRequestId(request.requestId)
                .setMutationEpoch(receiver.epoch).setUpload(requireNotNull(receiver.descriptor))
                .setPublishedUploadCheck(PublishedUploadCheck.newBuilder().setChallenge(request.mutationId).setVerified(verified))
            uploads.verifyPublished(row.id,approved,allowed) {request->
                assertEquals(FileOperation.VERIFY_PUBLISHED_UPLOAD,request.operation)
                assertEquals(row.remote,request.transferId); assertEquals(row.remoteEpoch,request.mutationEpoch)
                challenges.add(request.mutationId)
                response(request,challenges.size==2).build()
            }
            assertEquals(2,challenges.size); assertEquals(1,challenges.distinct().size)
            waitFor("released check") {!uploads.state.value.single().active}
            val unbound=runCatching {uploads.verifyPublished(row.id,approved,allowed) {request->
                assertNotEquals(challenges.first(),request.mutationId)
                response(request,true).clearPublishedUploadCheck().build()
            }}
            assertTrue(unbound.isFailure)
            waitFor("released failed check") {!uploads.state.value.single().active}
            val ended=runCatching {uploads.verifyPublished(row.id,approved,allowed) {request->
                current.set(false)
                response(request,true).build()
            }}
            assertTrue(ended.isFailure)
            waitFor("released ended check") {!uploads.state.value.single().active}
            assertEquals(row,uploads.state.value.single())
        }
    }
    private class Receiver {
        val epoch=StorageRules.token(); var descriptor:UploadInfo?=null; val bytes=ByteArrayOutputStream(); var prepares=0; var calls=0; var loseFirstAck=false
        suspend fun request(request:FileRequest):FileResponse {
            calls++; val result=FileResponse.newBuilder().setRequestId(StorageRules.token()).setMutationEpoch(epoch)
            when(request.operation) {
                FileOperation.UPLOAD_CONTEXT->Unit
                FileOperation.PREPARE_UPLOAD->{prepares++; assertEquals(epoch,request.mutationEpoch); descriptor=request.upload.toBuilder().setPhase(UploadPhase.UPLOAD_RECEIVING).build(); result.upload=descriptor}
                FileOperation.UPLOAD_STATUS->result.upload=requireNotNull(descriptor).toBuilder().setOffset(bytes.size().toLong()).build()
                FileOperation.UPLOAD_WRITE->{assertEquals(bytes.size().toLong(),request.offset); assertTrue(request.content.size()<=StorageRules.RANGE); bytes.write(request.content.toByteArray())
                    descriptor=requireNotNull(descriptor).toBuilder().setOffset(bytes.size().toLong()).build()
                    if(loseFirstAck) {loseFirstAck=false; throw IOException("Fixture lost the range reply after accepting bytes.")}; result.upload=descriptor}
                FileOperation.FINISH_UPLOAD->{val x=requireNotNull(descriptor); assertEquals(x.size,bytes.size().toLong()); assertArrayEquals(x.sha256.toByteArray(),Rules.digest(bytes.toByteArray()))
                    descriptor=x.toBuilder().setPhase(UploadPhase.UPLOAD_COMPLETED).setFinalName("received.bin").build(); result.upload=descriptor}
                else->error("Unexpected scripted operation ${request.operation}")
            }
            return result.build()
        }
    }
    private fun destination()=FileEntry.newBuilder().setId(StorageRules.token()).setGrantId(StorageRules.token()).setName("Approved incoming")
        .setIsDirectory(true).setPermissions(12).setVersion("0".repeat(64)).build()
    private suspend fun fixture(elapsed:()->Long=android.os.SystemClock::elapsedRealtime,body:suspend (Context,AndroidOwnerPolicy,PeerApproval,PhoneLocalBrowser,LocalUploadSource,PhoneUploads,()->PhoneUploads)->Unit) {
        val base=instrumentation.targetContext; val prefix="outgoing-upload-"+StorageRules.token(); val root=File(base.filesDir,prefix).apply {check(mkdir())}
        val context=object:ContextWrapper(base) {
            override fun getFilesDir()=root
            override fun getCacheDir()=File(root,"cache").apply {mkdirs()}
            override fun getApplicationContext():Context=this
            override fun getSharedPreferences(name:String,mode:Int)=base.getSharedPreferences(prefix+name,mode)
        }
        val data=requireNotNull(base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"fixture",null,Bundle().apply {putBoolean("largeFile",true)}))
        val policy=AndroidOwnerPolicy(context); val approved=TestPeer.approve(policy,TestPeer.key()); val managers=mutableListOf<PhoneUploads>()
        fun manager()=PhoneUploads(context,LocalIdentity().id,elapsed=elapsed).also {managers.add(it)}
        val uploads=manager(); lateinit var browser:PhoneLocalBrowser
        main {browser=PhoneLocalBrowser(context); browser.setVisible(true); browser.add(Uri.parse(data.getString("tree")),data.getInt("flags"))}
        try {
            waitFor("local shortcut") {!browser.state.value.busy && browser.state.value.locations.size==1}
            main {browser.browse()}; waitFor("local root") {!browser.state.value.busy && browser.state.value.rows.size==1}
            main {browser.browse(browser.state.value.rows.single().entry)}
            waitFor("local real binary") {!browser.state.value.busy && browser.state.value.rows.any {it.entry.name=="kutuma.bin"}}
            lateinit var selected:LocalUploadSource; main {selected=browser.captureUpload(browser.state.value.rows.single {it.entry.name=="kutuma.bin"}.entry)}
            body(context,policy,approved,browser,selected,uploads,::manager)
        } finally {
            managers.forEach {it.shutdown()}
            lateinit var closing:Job; main {closing=browser.close()}; closing.join()
            LocalDocumentLocations(context).all().forEach {LocalDocumentLocations(context).remove(it.id)}
            base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"dispose",data.getString("root"),null)
            listOf("iconnect.proof","iconnect.storage","iconnect.saf-access").forEach {base.deleteSharedPreferences(prefix+it)}
            root.deleteRecursively()
        }
    }
    @Test fun acknowledgedPayloadHasAFreshResumeBaselineAndDisplayReadsDoNotRewriteReceipts():Unit=runBlocking {
        val ticks=AtomicLong()
        fixture(elapsed=ticks::get) {context,policy,approved,_,source,uploads,_->
            val prepared=stage(uploads,approved,source) {policy.isCurrent(approved)}; val receiver=Receiver(); val rates=PhoneUploadPayloadRates()
            val first=CompletableDeferred<Unit>(); val release=CompletableDeferred<Unit>(); val later=CompletableDeferred<Unit>(); var writes=0
            uploads.send(prepared.id,approved,destination(),{policy.isCurrent(approved)}) {request->
                if(request.operation==FileOperation.UPLOAD_WRITE) {
                    writes++
                    if(writes==1) {first.complete(Unit); release.await()}
                    if(writes==5) {later.complete(Unit); awaitCancellation()}
                }
                receiver.request(request).also {if(request.operation==FileOperation.UPLOAD_WRITE) ticks.addAndGet(500)}
            }
            withTimeout(12000) {first.await()}
            rates.refresh(uploads.state.value,uploads.payloadProgress.value,"session",65536,ticks.get())
            release.complete(Unit); withTimeout(12000) {later.await()}
            val p=uploads.payloadProgress.value.getValue(prepared.id); val before=uploads.state.value.single()
            assertEquals(4L*StorageRules.RANGE,p.offset)
            val journal=File(context.filesDir,"outgoing-uploads.json"); val hash=Rules.digest(journal.readBytes())
            val value=rates.refresh(uploads.state.value,uploads.payloadProgress.value,"session",65536,ticks.get()).getValue(prepared.id)
            assertEquals(2L*StorageRules.RANGE,value.bytesPerSecond); assertEquals((p.size-p.offset+65535)/65536,value.remainingSeconds)
            assertEquals(PhonePayloadRateState.WAITING,rates.refresh(uploads.state.value,uploads.payloadProgress.value,"session",65536,ticks.get()+10001).getValue(prepared.id).state)
            assertEquals(before,uploads.state.value.single()); assertArrayEquals(hash,Rules.digest(journal.readBytes()))
            uploads.pause(prepared.id); waitFor("paused payload cleared") {!uploads.state.value.single().active}
            assertTrue(uploads.payloadProgress.value.isEmpty()); assertEquals(p.offset,receiver.bytes.size().toLong())
            val resumed=CompletableDeferred<Unit>()
            uploads.send(prepared.id,approved,null,{policy.isCurrent(approved)}) {request->
                if(request.operation==FileOperation.UPLOAD_WRITE) {resumed.complete(Unit); awaitCancellation()}; receiver.request(request)
            }
            withTimeout(12000) {resumed.await()}; val next=uploads.payloadProgress.value.getValue(prepared.id)
            assertNotEquals(p.run,next.run); assertEquals(p.offset,next.offset)
            assertNull(rates.refresh(uploads.state.value,uploads.payloadProgress.value,"session",65536,ticks.get()).getValue(prepared.id).remainingSeconds)
            uploads.pause(prepared.id); waitFor("resumed send paused") {!uploads.state.value.single().active}
            uploads.send(prepared.id,approved,null,{policy.isCurrent(approved)},receiver::request)
            waitFor("same receipt completed") {uploads.state.value.single().let {!it.active && it.phase==PhoneUploadPhase.COMPLETED}}
            assertTrue(uploads.payloadProgress.value.isEmpty()); assertFalse(journal.readText().contains(next.run))
            assertArrayEquals(File(context.filesDir,"outgoing-upload-copies/${prepared.id}.bin").readBytes(),receiver.bytes.toByteArray())
            assertTrue(AndroidStorageGrants(context,policy).all().isEmpty()); assertTrue(source.allowed())
        }
    }
    @Test fun anInvalidRangeAcknowledgementCannotPublishProgressAndResumeChecksTheRetainedReceipt():Unit=runBlocking {fixture {context,policy,approved,_,source,uploads,_->
        val prepared=stage(uploads,approved,source) {policy.isCurrent(approved)}; val receiver=Receiver()
        uploads.send(prepared.id,approved,destination(),{policy.isCurrent(approved)}) {request->
            val reply=receiver.request(request)
            if(request.operation==FileOperation.UPLOAD_WRITE) reply.toBuilder().setUpload(reply.upload.toBuilder().setOffset(reply.upload.offset+1)).build() else reply
        }
        waitFor("invalid acknowledgement refused") {!uploads.state.value.single().active}
        assertEquals(PhoneUploadPhase.REVIEW,uploads.state.value.single().phase); assertEquals(0L,uploads.state.value.single().offset)
        assertTrue(uploads.payloadProgress.value.isEmpty()); assertEquals(StorageRules.RANGE,receiver.bytes.size())
        uploads.send(prepared.id,approved,null,{policy.isCurrent(approved)},receiver::request)
        waitFor("retained receipt verified") {uploads.state.value.single().let {!it.active && it.phase==PhoneUploadPhase.COMPLETED}}
        assertEquals(1,receiver.prepares); assertTrue(source.allowed()); assertTrue(AndroidStorageGrants(context,policy).all().isEmpty())
    } }
    private fun stage(uploads:PhoneUploads,approved:PeerApproval,source:LocalUploadSource,allowed:()->Boolean):PhoneUpload {
        val id=uploads.stage(approved,source,allowed)
        waitFor("private copy prepared") {uploads.state.value.any {it.id==id && !it.active && it.phase==PhoneUploadPhase.READY}}
        return uploads.state.value.single {it.id==id}
    }
    private fun savedUpload(context:Context)=JSONArray(File(context.filesDir,"outgoing-uploads.json").readText()).getJSONObject(0)

    @Test fun liveAcknowledgementsRemainSeparateFromBoundedSenderCheckpointsAndPauseSavesTheLatest():Unit=runBlocking {
        fixture(elapsed={0L}) {context,policy,approved,_,source,uploads,_->
            val prepared=stage(uploads,approved,source) {policy.isCurrent(approved)}; val receiver=Receiver()
            val blocked=CompletableDeferred<Unit>(); var writes=0
            uploads.send(prepared.id,approved,destination(),{policy.isCurrent(approved)}) {request->
                if(request.operation==FileOperation.UPLOAD_WRITE && ++writes==34) {blocked.complete(Unit); awaitCancellation()}
                receiver.request(request)
            }
            withTimeout(12000) {blocked.await()}
            val acknowledged=33L*StorageRules.RANGE
            assertEquals(acknowledged,receiver.bytes.size().toLong())
            assertEquals(acknowledged,uploads.payloadProgress.value.getValue(prepared.id).offset)
            assertEquals(1024L*1024,uploads.state.value.single().offset)
            assertEquals(1024L*1024,savedUpload(context).getLong("offset"))
            uploads.pause(prepared.id); waitFor("latest acknowledgement saved on pause") {!uploads.state.value.single().active}
            assertEquals(PhoneUploadPhase.PAUSED,uploads.state.value.single().phase)
            assertEquals(acknowledged,savedUpload(context).getLong("offset")); assertTrue(uploads.payloadProgress.value.isEmpty())
            uploads.send(prepared.id,approved,null,{policy.isCurrent(approved)},receiver::request)
            waitFor("same copy completed after checkpointed pause") {uploads.state.value.single().let {!it.active && it.phase==PhoneUploadPhase.COMPLETED}}
            assertEquals(1,receiver.prepares)
            assertArrayEquals(File(context.filesDir,"outgoing-upload-copies/${prepared.id}.bin").readBytes(),receiver.bytes.toByteArray())
            assertTrue(source.allowed())
        }
    }

    @Test fun senderCheckpointDeadlineAndMonotonicClockRollbackSaveAcknowledgedProgress():Unit=runBlocking {
        val ticks=AtomicLong()
        fixture(elapsed=ticks::get) {context,policy,approved,_,source,uploads,_->
            val prepared=stage(uploads,approved,source) {policy.isCurrent(approved)}; val receiver=Receiver()
            val fourth=CompletableDeferred<Unit>(); val release=CompletableDeferred<Unit>(); val fifth=CompletableDeferred<Unit>(); var writes=0
            uploads.send(prepared.id,approved,destination(),{policy.isCurrent(approved)}) {request->
                if(request.operation==FileOperation.UPLOAD_WRITE) {
                    writes++
                    if(writes==4) {fourth.complete(Unit); release.await(); ticks.set(600)}
                    if(writes==5) {fifth.complete(Unit); awaitCancellation()}
                }
                receiver.request(request).also {if(request.operation==FileOperation.UPLOAD_WRITE && writes<=3) ticks.addAndGet(400)}
            }
            withTimeout(12000) {fourth.await()}
            assertEquals(3L*StorageRules.RANGE,savedUpload(context).getLong("offset"))
            release.complete(Unit); withTimeout(12000) {fifth.await()}
            assertEquals(4L*StorageRules.RANGE,savedUpload(context).getLong("offset"))
            assertEquals(4L*StorageRules.RANGE,uploads.payloadProgress.value.getValue(prepared.id).offset)
            uploads.pause(prepared.id); waitFor("clock fixture paused") {!uploads.state.value.single().active}
            assertEquals(4L*StorageRules.RANGE,savedUpload(context).getLong("offset")); assertTrue(source.allowed())
        }
    }

    @Test fun reopeningAnOlderSenderCheckpointResumesTheSameReceiptFromItsFreshDurableOffset():Unit=runBlocking {
        fixture(elapsed={0L}) {context,policy,approved,_,source,uploads,_->
            val prepared=stage(uploads,approved,source) {policy.isCurrent(approved)}; val receiver=Receiver()
            val blocked=CompletableDeferred<Unit>(); var writes=0
            uploads.send(prepared.id,approved,destination(),{policy.isCurrent(approved)}) {request->
                if(request.operation==FileOperation.UPLOAD_WRITE && ++writes==6) {blocked.complete(Unit); awaitCancellation()}
                receiver.request(request)
            }
            withTimeout(12000) {blocked.await()}
            val acknowledged=5L*StorageRules.RANGE
            assertEquals(acknowledged,receiver.bytes.size().toLong()); assertEquals(0L,savedUpload(context).getLong("offset"))
            val root=File(context.filesDir,"checkpoint-image-"+StorageRules.token()).apply {check(mkdir())}
            val originalSnapshot=File(context.filesDir,"outgoing-upload-copies/${prepared.id}.bin")
            var relocated:File?=null; var restored:PhoneUploads?=null
            try {
                // Capture the persisted restart image before graceful cancellation can
                // flush newer progress. This is an on-disk recovery fixture, not a process kill.
                val snapshots=File(root,"outgoing-upload-copies").apply {check(mkdir())}
                val retained=File(snapshots,"${prepared.id}.bin")
                check(originalSnapshot.renameTo(retained)); relocated=retained // Keep the recorded inode and bytes.
                File(context.filesDir,"outgoing-uploads.json").copyTo(File(root,"outgoing-uploads.json"))
                uploads.pause(prepared.id); waitFor("original send stopped after image capture") {!uploads.state.value.single().active}
                assertEquals(acknowledged,savedUpload(context).getLong("offset"))
                val recoveredContext=object:ContextWrapper(context) {
                    override fun getFilesDir()=root
                    override fun getApplicationContext():Context=this
                }
                val again=PhoneUploads(recoveredContext,prepared.owner,elapsed={0L}); restored=again
                val row=again.state.value.single()
                assertEquals(PhoneUploadPhase.PAUSED,row.phase); assertEquals(0L,row.offset)
                assertEquals(receiver.descriptor!!.id,row.remote); assertEquals(receiver.epoch,row.remoteEpoch)
                var resumedFrom=-1L
                again.send(row.id,approved,null,{policy.isCurrent(approved)}) {request->
                    if(request.operation==FileOperation.UPLOAD_WRITE && resumedFrom<0) resumedFrom=request.offset
                    receiver.request(request)
                }
                waitFor("retained receipt completed from its fresh offset") {again.state.value.single().let {!it.active && it.phase==PhoneUploadPhase.COMPLETED}}
                assertEquals(acknowledged,resumedFrom); assertEquals(1,receiver.prepares)
                assertArrayEquals(retained.readBytes(),receiver.bytes.toByteArray())
                assertTrue(again.payloadProgress.value.isEmpty()); assertTrue(source.allowed())
            } finally {
                restored?.shutdown()
                relocated?.let {check(!originalSnapshot.exists() && it.renameTo(originalSnapshot))}
                check(root.deleteRecursively())
            }
        }
    }
    @Test fun localSnapshotCanResumeALostRangeReplyAfterRestartWithoutSharingTheOriginal()=runBlocking {fixture {context,policy,approved,_,source,uploads,restart->
        fun assertDescriptorsClosed() {
            val retained=File("/proc/self/fd").listFiles().orEmpty().count {file->runCatching {android.system.Os.readlink(file.path)}.getOrDefault("").startsWith(File(context.filesDir,"outgoing-upload-copies").canonicalPath+"/")}
            assertEquals("Private staging and completed/failed sending must close their owned descriptors.",0,retained)
        }
        assertTrue(AndroidStorageGrants(context,policy).all().isEmpty())
        val prepared=stage(uploads,approved,source) {policy.isCurrent(approved)}
        assertDescriptorsClosed()
        assertEquals(2L*1024*1024+17,prepared.size)
        val expected=File(context.filesDir,"outgoing-upload-copies/${prepared.id}.bin").readBytes()
        assertEquals("2ce3172ab5811f22d8e2d9d45d2c71397e38a636b4a5fd5a09517b7454fb2368",Rules.hex(Rules.digest(expected)))
        val receiver=Receiver().apply {loseFirstAck=true}
        uploads.send(prepared.id,approved,destination(),{policy.isCurrent(approved)},receiver::request)
        waitFor("lost acknowledgement retained") {uploads.state.value.single().let {!it.active && it.phase==PhoneUploadPhase.REVIEW}}
        assertDescriptorsClosed()
        assertEquals(StorageRules.RANGE,receiver.bytes.size()); assertEquals(1,receiver.prepares)
        assertTrue(uploads.payloadProgress.value.isEmpty()); uploads.shutdown(); val again=restart(); assertTrue(again.payloadProgress.value.isEmpty())
        again.send(prepared.id,approved,null,{policy.isCurrent(approved)},receiver::request)
        waitFor("resumed verified receipt") {again.state.value.single().let {!it.active && it.phase==PhoneUploadPhase.COMPLETED}}
        assertDescriptorsClosed()
        assertEquals(1,receiver.prepares); assertArrayEquals(expected,receiver.bytes.toByteArray()); assertEquals("received.bin",again.state.value.single().finalName)
        assertTrue(AndroidStorageGrants(context,policy).all().isEmpty())
        AndroidFileService(context,AndroidStorageGrants(context,policy)).use {host->assertTrue(host.execute(approved.id,FileRequest.newBuilder().setRequestId(StorageRules.token()).setOperation(FileOperation.LIST_ROOTS).build()).entriesList.isEmpty())}
        again.remove(prepared.id); assertTrue(again.state.value.isEmpty()); assertTrue(source.allowed())
        assertDescriptorsClosed()
        val metadata=PhoneTransferManifests.checked(source.request,FileRequest.newBuilder().setOperation(FileOperation.GET_METADATA).setNodeId(source.entry.id).build()).entriesList.single()
        assertEquals(source.entry.version,metadata.version); assertEquals(source.entry.size,metadata.size)
    } }
    @Test fun wrongPairingAndForegroundWithdrawalCannotStartOrContinueRemoteWrites()=runBlocking {fixture {_,policy,approved,browser,source,uploads,_->
        val prepared=stage(uploads,approved,source) {policy.isCurrent(approved)}; val receiver=Receiver()
        assertThrows(IllegalStateException::class.java) {uploads.send(prepared.id,approved.copy(epoch=StorageRules.token()),destination(),{true},receiver::request)}
        assertEquals(0,receiver.calls)
        val visible=AtomicBoolean(true)
        uploads.send(prepared.id,approved,destination(),{visible.get() && policy.isCurrent(approved)}) {request->
            val result=receiver.request(request); if(request.operation==FileOperation.UPLOAD_WRITE) {visible.set(false); uploads.pauseAll()}; result
        }
        waitFor("foreground pause") {uploads.state.value.single().let {!it.active && it.phase in setOf(PhoneUploadPhase.PAUSED,PhoneUploadPhase.REVIEW)}}
        val count=receiver.calls; Thread.sleep(200); assertEquals(count,receiver.calls); assertEquals(StorageRules.RANGE,receiver.bytes.size())
        main {browser.cancel()}; assertFalse(source.allowed())
        uploads.remove(prepared.id); assertTrue(uploads.state.value.isEmpty())
    } }
    @Test fun corruptSnapshotsAndDamagedMetadataCannotSilentlySendOrDeleteOriginalFiles()=runBlocking {fixture {context,policy,approved,_,source,uploads,restart->
        val prepared=stage(uploads,approved,source) {policy.isCurrent(approved)}
        val file=File(context.filesDir,"outgoing-upload-copies/${prepared.id}.bin"); java.io.RandomAccessFile(file,"rw").use {it.write(1)}
        val receiver=Receiver(); uploads.send(prepared.id,approved,destination(),{true},receiver::request)
        waitFor("private corruption denied") {uploads.state.value.single().let {!it.active && it.phase==PhoneUploadPhase.REVIEW}}
        assertEquals(0,receiver.calls); uploads.shutdown(); File(context.filesDir,"outgoing-uploads.json").writeText("damaged progress")
        val again=restart(); val issue=again.recoveryIssues().single(); assertTrue(file.exists()); again.archive(issue.id)
        assertEquals(1,again.orphanCount()); assertTrue(file.exists()); again.removeOrphans(); assertFalse(file.exists())
        val metadata=PhoneTransferManifests.checked(source.request,FileRequest.newBuilder().setOperation(FileOperation.GET_METADATA).setNodeId(source.entry.id).build()).entriesList.single()
        assertEquals(source.entry.version,metadata.version)
    } }
    @Test fun nativeLocalBrowserCanPrepareAPrivateSendCopyWithoutCreatingRemoteSharing()=runBlocking {fixture {context,policy,approved,browser,_,uploads,_->
        fun nodes(root:AccessibilityNodeInfo?):List<AccessibilityNodeInfo> = if(root==null) emptyList() else listOf(root)+(0 until root.childCount).flatMap {nodes(root.getChild(it))}
        fun click(text:String) {
            val deadline=android.os.SystemClock.elapsedRealtime()+10_000
            while(android.os.SystemClock.elapsedRealtime()<deadline) {
                val all=nodes(instrumentation.uiAutomation.rootInActiveWindow)
                var item=all.firstOrNull {it.text?.toString()==text && it.isVisibleToUser}
                while(item!=null && !item.isClickable) item=item.parent
                if(item?.performAction(AccessibilityNodeInfo.ACTION_CLICK)==true) return
                all.firstOrNull {it.isScrollable}?.performAction(AccessibilityNodeInfo.ACTION_SCROLL_FORWARD); Thread.sleep(80)
            }
            fail("Missing native local upload action: $text")
        }
        ActivityScenario.launch(MainActivity::class.java).use {scenario->
            scenario.onActivity {activity->activity.setContent {MaterialTheme {
                val rows by uploads.state.collectAsState()
                LocalFilesScreen(browser,stageUpload={entry->uploads.stage(approved,browser.captureUpload(entry)) {policy.isCurrent(approved)}},
                    uploadNotice=rows.firstOrNull()?.message?:"") {}
            }}}
            waitFor("native local roots") {!browser.state.value.busy && browser.state.value.breadcrumbs.isEmpty()}
            click("Open local folder"); waitFor("native source files") {!browser.state.value.busy && browser.state.value.rows.any {!it.entry.isDirectory}}
            click("Prepare to send to computer")
            waitFor("native source preparation") {uploads.state.value.singleOrNull()?.let {!it.active && it.phase==PhoneUploadPhase.READY}==true}
            assertTrue(AndroidStorageGrants(context,policy).all().isEmpty())
            assertTrue(File(context.filesDir,"outgoing-upload-copies/${uploads.state.value.single().id}.bin").isFile)
        }
    } }
}
