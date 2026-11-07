package com.iconnect.proof

import android.content.Context
import android.content.ContextWrapper
import android.net.Uri
import android.provider.DocumentsContract as Docs
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.iconnect.protocol.*
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.json.JSONObject
import java.io.File
import java.nio.file.Files

@RunWith(AndroidJUnit4::class)
class ActivityTest {
    private fun fixture(body:(Context,File)->Unit) {
        val base=InstrumentationRegistry.getInstrumentation().targetContext
        val root=File(base.filesDir,"activity-test-"+StorageRules.token()).apply {check(mkdir())}
        val context=object:ContextWrapper(base) {override fun getFilesDir()=root; override fun getApplicationContext():Context=this; override fun getSharedPreferences(name:String,mode:Int)=base.getSharedPreferences(root.name+name,mode)}
        try {body(context,root)} finally {listOf("iconnect.proof","iconnect.storage","iconnect.saf-access").forEach {base.deleteSharedPreferences(root.name+it)}; check(root.canonicalFile.parentFile==base.filesDir.canonicalFile && root.name.startsWith("activity-test-")); root.deleteRecursively()}
    }
    private val peer="ab".repeat(32)
    @Test fun typedActivityPersistsCoalescedCountsAndBytesWithoutAcceptingArbitraryDescriptions()=fixture {context,root->
        var time=1_700_000_000_000L
        PhoneActivityCore(context) {time}.use {core->
            core.append(PhoneActivityEvent(PhoneActivityKind.FILE_REQUEST,PhoneActivityOutcome.ACCEPTED,peer,7,FileOperation.GET_METADATA.number))
            time+=100; core.append(PhoneActivityEvent(PhoneActivityKind.FILE_REQUEST,PhoneActivityOutcome.ACCEPTED,peer,11,FileOperation.GET_METADATA.number)); core.flush()
            val row=core.view().entries.single(); assertEquals(2,row.count); assertEquals(18,row.bytes); assertEquals(peer,row.peer)
        }
        PhoneActivityCore(context) {time}.use {core->assertEquals(2,core.view().entries.single().count); assertEquals(30,core.view().days)}
        val json=File(root,"local-activity/activity.json").readText(); assertFalse(json.contains("content://")); assertFalse(json.contains("iconnect://"))
    }
    @Test fun countAndAgeRetentionAndRecordingOffSurviveReopen()=fixture {context,_->
        var time=1_700_000_000_000L
        PhoneActivityCore(context) {time}.use {core->
            repeat(501) {core.append(PhoneActivityEvent(PhoneActivityKind.CONNECTION,PhoneActivityOutcome.COMPLETED)); time+=2000}
            core.flush(); assertEquals(500,core.view().entries.size); core.configure(1); time+=2*86_400_000L
            core.append(PhoneActivityEvent(PhoneActivityKind.SHARING,PhoneActivityOutcome.COMPLETED)); core.flush(); assertEquals(1,core.view().entries.size)
            core.configure(0); core.append(PhoneActivityEvent(PhoneActivityKind.REVOCATION,PhoneActivityOutcome.REQUESTED)); core.flush(); assertTrue(core.view().entries.isEmpty())
        }
        PhoneActivityCore(context) {time}.use {core->assertEquals(0,core.view().days); assertTrue(core.view().entries.isEmpty()); core.configure(7); core.append(PhoneActivityEvent(PhoneActivityKind.SETTINGS,PhoneActivityOutcome.COMPLETED)); core.flush()}
        PhoneActivityCore(context) {time}.use {core->assertEquals(7,core.view().days); assertEquals(1,core.view().entries.size)}
    }
    @Test fun malformedHistoryAndSidecarsArePreservedBeforeResetAndOwnerFilesStayIntact()=fixture {context,root->
        PhoneActivityCore(context).close(); val path=File(root,"local-activity/activity.json"); path.writeText("generated unreadable activity")
        File(path.path+".new").writeText("generated uncommitted history"); val keep=File(root,"owner.txt").apply {writeText("generated owner data")}
        PhoneActivityCore(context).use {core->assertTrue(core.view().issue.isNotEmpty()); assertTrue(core.view().canReset)
            assertThrows(IllegalStateException::class.java) {core.clear()}; assertEquals("generated unreadable activity",path.readText())
            core.reset(); assertEquals(30,core.view().days); assertTrue(core.view().issue.isEmpty()); assertTrue(core.view().entries.isEmpty())
        }
        val files=File(root,"local-activity/recovery").walkTopDown().filter {it.isFile}.toList()
        assertEquals(2,files.size); assertTrue(files.any {it.readText()=="generated unreadable activity"}); assertTrue(files.any {it.readText()=="generated uncommitted history"}); assertEquals("generated owner data",keep.readText())
    }
    @Test fun journalChangesBlockWritesAndAStaleResetCannotArchiveChangedBytes()=fixture {context,root->
        PhoneActivityCore(context).use {core->
            core.append(PhoneActivityEvent(PhoneActivityKind.CONNECTION,PhoneActivityOutcome.COMPLETED)); core.flush()
            val path=File(root,"local-activity/activity.json"); path.appendText(" generated external change"); core.append(PhoneActivityEvent(PhoneActivityKind.REVOCATION,PhoneActivityOutcome.REQUESTED))
            assertThrows(Exception::class.java) {core.flush()}; assertTrue(core.view().issue.isNotEmpty()); assertTrue(path.readText().endsWith(" generated external change"))
            path.appendText(" changed again"); assertThrows(Exception::class.java) {core.reset()}; assertTrue(path.readText().endsWith(" changed again"))
        }
        PhoneActivityCore(context).use {core->assertTrue(core.view().issue.isNotEmpty()); assertTrue(core.view().canReset); core.reset(); assertTrue(core.view().entries.isEmpty())}
    }
    @Test fun nativeLeaseRefusesAnotherWriterAndLinksAreKeptWithoutFollowing()=fixture {context,root->
        PhoneActivityCore(context).use {first->assertThrows(Exception::class.java) {PhoneActivityCore(context)}; first.append(PhoneActivityEvent(PhoneActivityKind.SHARING,PhoneActivityOutcome.COMPLETED)); first.flush()}
        val path=File(root,"local-activity/activity.json"); check(path.delete()); val outside=File(root,"owner.txt").apply {writeText("generated original")}
        Files.createSymbolicLink(path.toPath(),outside.toPath())
        PhoneActivityCore(context).use {core->assertTrue(core.view().issue.isNotEmpty()); assertFalse(core.view().canReset); assertThrows(IllegalStateException::class.java) {core.clear()}}
        assertEquals("generated original",outside.readText()); check(path.delete())
    }
    @Test fun schemaRejectsCoercedNumbersTextAndUnknownFieldsAndKeepsEachUnreadableRecord()=fixture {context,root->
        PhoneActivityCore(context).use {core->core.append(PhoneActivityEvent(PhoneActivityKind.CONNECTION,PhoneActivityOutcome.COMPLETED,peer)); core.flush()}
        val path=File(root,"local-activity/activity.json"); val original=path.readText()
        val variants=listOf<(JSONObject)->Unit>(
            {it.put("schema",1.5)}, {it.put("days","30")}, {it.put("extra","generated unknown field")},
            {it.getJSONArray("entries").getJSONObject(0).put("count",1.5)},
            {it.getJSONArray("entries").getJSONObject(0).put("operation",4_294_967_295L)},
            {it.getJSONArray("entries").getJSONObject(0).put("peer",12)},
            {it.getJSONArray("entries").getJSONObject(0).put("extra","generated unknown field")})
        variants.forEach {change->val text=JSONObject(original).also(change).toString(); path.writeText(text)
            PhoneActivityCore(context).use {core->assertTrue(core.view().issue.isNotEmpty()); assertTrue(core.view().canReset); assertEquals(text,path.readText()); core.reset()}
        }
        val archived=File(root,"local-activity/recovery").walkTopDown().filter {it.isFile}.map {it.readText()}.toList()
        assertEquals(variants.size,archived.size)
    }
    @Test fun boundedWriterReportsOverflowAndGracefulCloseFlushesAcceptedEvents()=fixture {context,_->runBlocking {
        val activity=PhoneActivity(context); repeat(10_000) {activity.record(PhoneActivityEvent(PhoneActivityKind.FILE_REQUEST,PhoneActivityOutcome.ACCEPTED,peer,operation=FileOperation.LIST_ROOTS.number))}
        activity.flush(); assertTrue(activity.state.value.ready); assertTrue(activity.state.value.dropped>0); assertTrue(activity.state.value.entries.isNotEmpty()); activity.close()
        PhoneActivityCore(context).use {core->assertTrue(core.view().entries.isNotEmpty()); assertTrue(core.view().entries.sumOf {it.count}<=10_000)}
    }}
    @Test fun invalidPeerTextIsRemovedAndDiagnosticSnapshotNeverIncludesActivityRowsOrErrors()=fixture {context,_->
        PhoneActivityCore(context).use {core->core.append(PhoneActivityEvent(PhoneActivityKind.PAIRING,PhoneActivityOutcome.COMPLETED,"iconnect://NEVER-EXPORT-ACTIVITY-SECRET")); core.flush(); assertEquals("",core.view().entries.single().peer)
            val state=ProofState(activity=core.view().copy(issue="NEVER-EXPORT-ACTIVITY-SECRET")); val json=PhoneDiagnosticSnapshot.create(state,true)
            assertFalse(json.contains("NEVER-EXPORT-ACTIVITY-SECRET")); assertFalse(json.contains(core.view().entries.single().id)); assertTrue(json.contains("retentionDays"))
        }
    }
    @Test fun backgroundWriteFailureIsVisibleAndOwnerResetCanRetryWithoutChangingOriginalData()=fixture {context,root->runBlocking {
        val history=PhoneActivity(context); val keep=File(root,"owner.txt").apply {writeText("generated original data")}
        try {
            history.flush(); val path=File(root,"local-activity/activity.json"); path.appendText(" generated external change")
            history.record(PhoneActivityEvent(PhoneActivityKind.CONNECTION,PhoneActivityOutcome.COMPLETED,peer))
            assertTrue(runCatching {history.flush()}.isFailure); val blocked=history.state.value
            assertTrue(blocked.issue.isNotEmpty()); assertTrue(blocked.canReset); assertTrue(blocked.dropped>=1); assertTrue(blocked.entries.isEmpty())
            assertTrue(path.readText().endsWith(" generated external change")); history.reset()
            assertTrue(history.state.value.issue.isEmpty()); assertEquals(30,history.state.value.days)
            history.record(PhoneActivityEvent(PhoneActivityKind.SHARING,PhoneActivityOutcome.COMPLETED,peer)); history.flush()
            assertEquals(PhoneActivityKind.SHARING,history.state.value.entries.single().kind); assertTrue(history.state.value.dropped>=blocked.dropped)
            assertEquals("generated original data",keep.readText())
        } finally {history.close()}
    }}
    @Test fun canceledAndConcurrentCloseWaitForTheWriterAndCommandsCannotWaitBehindTheCloseBarrier()=fixture {context,_->runBlocking {
        val history=PhoneActivity(context); history.flush()
        try {
            repeat(64) {history.record(PhoneActivityEvent(PhoneActivityKind.FILE_REQUEST,PhoneActivityOutcome.ACCEPTED,peer,operation=FileOperation.LIST_ROOTS.number))}
            val controls=(0 until 32).map {async(start=CoroutineStart.UNDISPATCHED) {runCatching {history.flush()}}}
            val first=launch(start=CoroutineStart.UNDISPATCHED) {history.close()}; first.cancel()
            val second=async(start=CoroutineStart.UNDISPATCHED) {history.close()}
            val late=async(start=CoroutineStart.UNDISPATCHED) {runCatching {history.clear()}}
            withTimeout(10_000) {first.join(); second.await(); controls.awaitAll(); assertTrue(late.await().isFailure)}
            PhoneActivityCore(context).use {core->assertTrue(core.view().issue.isEmpty()); assertTrue(core.view().entries.isNotEmpty())}
        } finally {history.close()}
    }}
    @Test fun realServiceRecordsAcceptedAndRefusedOperationsWithoutRawMetadataAndSinkFailureCannotChangePermission()=fixture {context,_->runBlocking {
        val base=InstrumentationRegistry.getInstrumentation().targetContext; val data=requireNotNull(base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"fixture",null,null)); val tree=Uri.parse(data.getString("tree"))
        try {
            val owner=AndroidOwnerPolicy(context); val storage=AndroidStorageGrants(context,owner); val sink=PhoneActivity(context)
            var available=false
            val service=AndroidFileService(context,storage,available={available}).also {it.activity=sink::record}
            val denied=service.execute(peer,FileRequest.newBuilder().setRequestId(StorageRules.token()).setOperation(FileOperation.GET_METADATA).setNodeId("unapproved-private-id").build())
            assertEquals(FileStatus.ACCESS_DENIED,denied.status)
            available=true; val roots=service.execute(peer,FileRequest.newBuilder().setRequestId(StorageRules.token()).setOperation(FileOperation.LIST_ROOTS).build()); assertEquals(FileStatus.FILE_STATUS_OK,roots.status)
            sink.flush(); assertEquals(setOf(PhoneActivityOutcome.REFUSED,PhoneActivityOutcome.ACCEPTED),sink.state.value.entries.map {it.outcome}.toSet())
            service.activity={throw IllegalStateException("generated logger fault")}
            available=false
            assertEquals(FileStatus.ACCESS_DENIED,service.execute(peer,FileRequest.newBuilder().setRequestId(StorageRules.token()).setOperation(FileOperation.GET_METADATA).setNodeId("unapproved-private-id").build()).status)
            assertTrue(storage.all().isEmpty()); assertFalse(File(root(context),"unapproved-private-id").exists()); service.close(); sink.close()
        } finally {base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"dispose",data.getString("root"),null)}
    }}
    @Test fun actualIncomingCompletionIsRecordedOnlyAfterPublicationAndALoggerFaultCannotChangeTheCopy()=runBlocking {
        val f=IncomingUploadsTest.Fixture(); val history=PhoneActivity(f.context)
        try {
            f.grants.activity=history::record; f.service.activity=history::record; f.initialize()
            val bytes="generated private file bytes".toByteArray(); val descriptor=f.descriptor(bytes,"NEVER-LOG-FILENAME.txt"); val row=f.begin(descriptor); f.write(row,bytes)
            history.flush(); assertTrue(history.state.value.entries.none {it.kind==PhoneActivityKind.INCOMING_COPY && it.outcome==PhoneActivityOutcome.COMPLETED})
            val done=f.finish(row); assertEquals(UploadPhase.UPLOAD_COMPLETED,done.phase); history.flush()
            val outcome=history.state.value.entries.single {it.kind==PhoneActivityKind.INCOMING_COPY && it.outcome==PhoneActivityOutcome.COMPLETED}; assertEquals(bytes.size.toLong(),outcome.bytes)
            assertFalse(File(f.root,"local-activity/activity.json").readText().contains("NEVER-LOG-FILENAME")); assertArrayEquals(bytes,f.read(done.finalName))
            f.grants.activity={throw IllegalStateException("generated logger fault")}; val next=f.begin(f.descriptor(bytes,"second-generated-copy.txt")); f.write(next,bytes)
            assertEquals(UploadPhase.UPLOAD_COMPLETED,f.finish(next).phase); assertArrayEquals(bytes,f.read("second-generated-copy.txt"))
        } finally {history.close(); f.close()}
    }
    private fun root(context:Context)=context.filesDir
}
