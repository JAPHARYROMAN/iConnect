package com.iconnect.proof

import android.content.Context
import android.content.ContextWrapper
import android.net.Uri
import android.os.Bundle
import android.provider.DocumentsContract as Docs
import android.system.Os
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.iconnect.protocol.*
import kotlinx.coroutines.*
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.util.concurrent.atomic.AtomicBoolean

@RunWith(AndroidJUnit4::class)
class BatchSourceTest {
    private class Fixture(extras:Int=0,large:Boolean=false,val resources:PhoneOutgoingResources=PhoneOutgoingResources.isolated(PhoneOutgoingResources.BATCH)) {
        private val instrumentation=InstrumentationRegistry.getInstrumentation()
        val base=instrumentation.targetContext
        private val prefix="batch-source-test-"+StorageRules.token()
        val root=File(base.filesDir,prefix).apply {check(mkdir())}
        val context=object:ContextWrapper(base) {
            override fun getFilesDir()=root
            override fun getCacheDir()=File(root,"cache").apply {mkdirs()}
            override fun getApplicationContext():Context=this
            override fun getSharedPreferences(name:String,mode:Int)=base.getSharedPreferences(prefix+name,mode)
        }
        val data=requireNotNull(base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"fixture",null,Bundle().apply {putBoolean("emptyFolder",true); putBoolean("largeFile",large); putInt("extraFiles",extras)}))
        val tree=Uri.parse(data.getString("tree"))
        val policy=AndroidOwnerPolicy(context)
        val key=TestPeer.key()
        var approval=TestPeer.approve(policy,key)
        var sources=AndroidBatchSources(context,policy,resources)
            private set
        lateinit var browser:PhoneLocalBrowser
        lateinit var selected:LocalUploadSource
        val allowed=AtomicBoolean(true)
        init {
            val zero=requireNotNull(Docs.createDocument(base.contentResolver,Docs.buildDocumentUriUsingTree(tree,Docs.getTreeDocumentId(tree)),"application/octet-stream","zero.bin"))
            base.contentResolver.openOutputStream(zero)!!.use {it.write(byteArrayOf())}
        }
        fun main(action:()->Unit)=instrumentation.runOnMainSync(action)
        suspend fun initialize() {
            main {browser=PhoneLocalBrowser(context); browser.setVisible(true); browser.add(tree,data.getInt("flags"))}
            withTimeout(20_000) {while(browser.state.value.busy || browser.state.value.rows.size!=1) delay(15)}
            main {selected=browser.captureBatch(listOf(browser.state.value.rows.single().entry)).single()}
            assertTrue(AndroidStorageGrants(context,policy).all().isEmpty())
        }
        suspend fun stage(source:LocalUploadSource=selected,collision:UploadCollision=UploadCollision.UPLOAD_KEEP_BOTH):PhoneBatchSource {
            val id=sources.stage(approval,listOf(source),collision) {allowed.get()}
            withTimeout(30_000) {sources.whenIdle()}
            return sources.state.value.single {it.id==id}
        }
        suspend fun ready(source:LocalUploadSource=selected):PhoneBatchSource=stage(source).also {assertEquals(it.message,PhoneBatchSourcePhase.READY,it.phase); assertFalse(it.active)}
        suspend fun reopen() {sources.shutdown(); sources=AndroidBatchSources(context,policy,resources)}
        suspend fun open(id:String,index:Int,available:()->Boolean={allowed.get()})=sources.openFile(id,index,available)
        fun privateFile(id:String,name:String)=File(root,"outgoing-batch-copies/$id/$name")
        fun record(id:String)=File(root,"outgoing-batch-copies/$id.json")
        fun change(id:String,content:String) {base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"change",id,Bundle().apply {putString("content",content)})}
        fun providerBytes(id:String)=base.contentResolver.openInputStream(Docs.buildDocumentUriUsingTree(tree,id))!!.use {it.readBytes()}
        fun assertNoDescriptors() {
            val count=File("/proc/self/fd").listFiles().orEmpty().count {file->runCatching {Os.readlink(file.path)}.getOrDefault("").let {it.startsWith(root.canonicalPath+"/outgoing-batch-copies/") && (it.substringBefore(" (deleted)").endsWith(".bin") || it.substringBefore(" (deleted)").endsWith(".json"))}}
            assertEquals("Private snapshot operations must close their owned descriptors.",0,count)
        }
        suspend fun close() {
            sources.shutdown()
            if(::browser.isInitialized) {lateinit var closing:Job; main {closing=browser.close()}; closing.join()}
            LocalDocumentLocations(context).all().forEach {LocalDocumentLocations(context).remove(it.id)}
            base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"dispose",data.getString("root"),null)
            listOf("iconnect.proof","iconnect.storage","iconnect.saf-access").forEach {base.deleteSharedPreferences(prefix+it)}
            check(root.canonicalFile.parentFile==base.filesDir.canonicalFile && root.name==prefix); root.deleteRecursively()
        }
    }
    private suspend fun fixture(extras:Int=0,large:Boolean=false,body:suspend (Fixture)->Unit) {
        val f=Fixture(extras,large); try {f.initialize(); body(f)} finally {f.close()}
    }
    private suspend fun sharedFixture(body:suspend (Fixture,PhoneUploads)->Unit) {
        val f=Fixture(resources=PhoneOutgoingResources()); val uploads=PhoneUploads(f.context,f.policy.deviceId,f.resources)
        try {f.initialize(); body(f,uploads)} finally {uploads.shutdown(); f.close()}
    }
    private suspend fun localFile(f:Fixture):LocalUploadSource {
        f.main {f.browser.browse(f.browser.state.value.rows.single().entry)}
        withTimeout(20_000) {while(f.browser.state.value.busy || f.browser.state.value.rows.none {it.entry.name=="simu.txt"}) delay(15)}
        lateinit var source:LocalUploadSource; f.main {source=f.browser.captureUpload(f.browser.state.value.rows.single {it.entry.name=="simu.txt"}.entry)}
        return source
    }
    private suspend fun staged(f:Fixture,uploads:PhoneUploads,source:LocalUploadSource):PhoneUpload {
        val id=uploads.stage(f.approval,source) {f.policy.isCurrent(f.approval)}
        withTimeout(20_000) {while(uploads.state.value.single {it.id==id}.active) delay(15)}
        return uploads.state.value.single {it.id==id}.also {assertEquals(it.message,PhoneUploadPhase.READY,it.phase)}
    }
    @Test fun sharedSingleSendAndBatchReadersUseTwoSlotsAndRetainBothReservations()=runBlocking {sharedFixture {f,uploads->
        val batch=f.ready(); val manifest=f.sources.manifest(batch.id); val index=manifest.itemsList.indexOfFirst {it.name=="simu.txt"}
        val source=localFile(f); val single=staged(f,uploads,source); val reserved=batch.bytes+single.size
        assertEquals(reserved,f.resources.reservedBytes); assertEquals(0,f.resources.activeWorkers)
        val reader=f.open(batch.id,index); val entered=CompletableDeferred<Unit>()
        try {
            val destination=FileEntry.newBuilder().setIsDirectory(true).setPermissions(12).build()
            uploads.send(single.id,f.approval,destination,{true}) {request->assertEquals(FileOperation.UPLOAD_CONTEXT,request.operation); entered.complete(Unit); awaitCancellation()}
            withTimeout(10_000) {entered.await()}; assertEquals(2,f.resources.activeWorkers)
            try {f.open(batch.id,index).close(); fail("A third outgoing worker must be refused.")} catch(_:IllegalStateException) {}
            try {uploads.stage(f.approval,source) {true}; fail("Preparation also uses the shared workers.")} catch(_:IllegalStateException) {}
            assertEquals(1,uploads.state.value.size); assertEquals(1,f.sources.state.value.size)
            uploads.pause(single.id); withTimeout(10_000) {while(uploads.state.value.single().active) delay(15)}
            assertEquals(PhoneUploadPhase.PAUSED,uploads.state.value.single().phase); assertEquals(1,f.resources.activeWorkers)
            f.open(batch.id,index).use {assertEquals(2,f.resources.activeWorkers); assertArrayEquals(f.providerBytes(f.data.getString("file")!!),it.range(0,it.size.toInt()))}
        } finally {reader.close()}
        assertEquals(0,f.resources.activeWorkers); assertEquals(reserved,f.resources.reservedBytes)
        uploads.remove(single.id); f.sources.removeReviewed(f.sources.reviewRemoval(batch.id)); assertEquals(0L,f.resources.reservedBytes); f.assertNoDescriptors()
    }}
    @Test fun sharedInventoryStaysReservedAcrossHostShutdownAndReopening()=runBlocking {sharedFixture {f,uploads->
        val row=f.ready(); val index=f.sources.manifest(row.id).itemsList.indexOfFirst {it.name=="simu.txt"}; val reader=f.open(row.id,index)
        val source=localFile(f); f.sources.shutdown(); assertEquals(0,f.resources.activeWorkers); assertEquals(row.bytes,f.resources.reservedBytes); assertFalse(f.resources.inventoryReady)
        try {reader.range(0,1); fail("Host shutdown must close retained readers.")} catch(_:IllegalStateException) {}
        try {uploads.stage(f.approval,source) {true}; fail("Unknown inventory cannot admit preparation.")} catch(_:java.io.IOException) {}
        assertTrue(uploads.state.value.isEmpty()); assertEquals(0,f.resources.activeWorkers)
        f.reopen(); assertTrue(f.resources.inventoryReady); val single=staged(f,uploads,source); assertEquals(row.bytes+single.size,f.resources.reservedBytes); reader.close(); f.assertNoDescriptors()
    }}
    @Test fun damagedBatchOwnershipBlocksOrdinaryPreparationAndPreservesItsPrivateBytes()=runBlocking {sharedFixture {f,uploads->
        val row=f.ready(); val index=f.sources.manifest(row.id).itemsList.indexOfFirst {it.name=="simu.txt"}; val bytes=f.privateFile(row.id,"$index.bin").readBytes()
        val source=localFile(f); f.sources.shutdown(); f.record(row.id).writeText("{damaged"); f.reopen()
        assertFalse(f.resources.inventoryReady); assertTrue(f.sources.recoveryIssues().isNotEmpty())
        try {uploads.stage(f.approval,source) {true}; fail("Damaged folder inventory must block new file reservations.")} catch(_:java.io.IOException) {}
        assertEquals(0,f.resources.activeWorkers); assertTrue(uploads.state.value.isEmpty()); assertArrayEquals(bytes,f.privateFile(row.id,"$index.bin").readBytes()); f.assertNoDescriptors()
    }}
    @Test fun closedSingleHostCannotMutateCopiesOrReleaseAReopenedHostsReservations()=runBlocking {sharedFixture {f,uploads->
        val batch=f.ready(); val source=localFile(f); val single=staged(f,uploads,source); uploads.shutdown()
        assertFalse(f.resources.inventoryReady); assertEquals(batch.bytes+single.size,f.resources.reservedBytes)
        assertThrows(IllegalStateException::class.java) {uploads.stage(f.approval,source) {true}}
        assertThrows(IllegalStateException::class.java) {uploads.rename(single.id,"renamed.txt")}
        try {f.sources.stage(f.approval,listOf(source),UploadCollision.UPLOAD_FAIL) {true}; fail("Closed ordinary inventory must remain reserved and unknown.")} catch(_:java.io.IOException) {}
        val again=PhoneUploads(f.context,f.policy.deviceId,f.resources)
        try {assertTrue(f.resources.inventoryReady); uploads.shutdown(); assertTrue(f.resources.inventoryReady); assertEquals(single.id,again.state.value.single().id)} finally {again.shutdown()}
        assertEquals(0,f.resources.activeWorkers); f.assertNoDescriptors()
    }}
    @Test fun orphanSingleCopiesBlockFolderPreparationUntilExplicitOwnerCleanup()=runBlocking {
        val f=Fixture(resources=PhoneOutgoingResources()); val directory=File(f.root,"outgoing-upload-copies").apply {check(mkdir())}; val orphan=File(directory,StorageRules.token()+".bin").apply {writeBytes(byteArrayOf(9))}
        val uploads=PhoneUploads(f.context,f.policy.deviceId,f.resources)
        try {
            f.initialize(); assertFalse(f.resources.inventoryReady); assertEquals(1,uploads.orphanCount())
            try {f.sources.stage(f.approval,listOf(f.selected),UploadCollision.UPLOAD_FAIL) {true}; fail("Unknown file inventory must block new folder preparation.")} catch(_:java.io.IOException) {}
            assertTrue(orphan.exists()); assertTrue(f.sources.state.value.isEmpty()); assertEquals(0,f.resources.activeWorkers)
            uploads.removeOrphans(); assertFalse(orphan.exists()); assertTrue(f.resources.inventoryReady); val row=f.ready(); assertEquals(row.bytes,f.resources.reservedBytes)
        } finally {uploads.shutdown(); f.close()}
    }
    @Test fun actualPrivateFolderCopiesKeepEmptyNestedAndZeroByteItemsWithoutRemoteSharing()=runBlocking {fixture(large=true) {f->
        val before=f.providerBytes(f.data.getString("file")!!); val row=f.ready(); val manifest=f.sources.manifest(row.id)
        assertEquals(8,manifest.itemsCount); assertEquals(UploadCollision.UPLOAD_KEEP_BOTH,manifest.collision)
        assertNotEquals("",manifest.itemsList.single {it.name==".ssh"}.error)
        assertTrue(manifest.itemsList.single {it.name=="Empty"}.isDirectory)
        assertEquals(manifest.itemsCount,row.items); assertEquals(row.bytes,row.preparedBytes); assertEquals(4,row.preparedFiles)
        val binary=manifest.itemsList.indexOfFirst {it.name=="kutuma.bin"}; val output=java.io.ByteArrayOutputStream()
        f.open(row.id,binary).use {source->var offset=0L; while(offset<source.size) {val bytes=source.range(offset,minOf(StorageRules.RANGE.toLong(),source.size-offset).toInt()); output.write(bytes); offset+=bytes.size}}
        assertEquals("2ce3172ab5811f22d8e2d9d45d2c71397e38a636b4a5fd5a09517b7454fb2368",Rules.hex(Rules.digest(output.toByteArray())))
        val zero=manifest.itemsList.indexOfFirst {it.name=="zero.bin"}; f.open(row.id,zero).use {assertEquals(0L,it.size)}
        assertArrayEquals(before,f.providerBytes(f.data.getString("file")!!)); assertTrue(AndroidStorageGrants(f.context,f.policy).all().isEmpty()); f.assertNoDescriptors()
    }}
    @Test fun originalChangesAfterPreparationDoNotChangePrivateBytesOrColdReceipts()=runBlocking {fixture {f->
        val original=f.providerBytes(f.data.getString("file")!!); val row=f.ready(); val manifest=f.sources.manifest(row.id); val index=manifest.itemsList.indexOfFirst {it.name=="simu.txt"}
        f.change(f.data.getString("file")!!,"owner changed original"); f.reopen(); assertTrue(f.sources.recoveryIssues().isEmpty()); assertEquals(manifest,f.sources.manifest(row.id))
        f.open(row.id,index).use {assertArrayEquals(original,it.range(0,original.size))}; assertEquals("owner changed original",f.providerBytes(f.data.getString("file")!!).decodeToString()); f.assertNoDescriptors()
    }}
    @Test fun changedSourceDuringPreparationKeepsAnOwnedFailedPartialAndClosesItsDescriptor()=runBlocking {fixture {f->
        val changed=AtomicBoolean(false); val source=f.selected.copy(request={value->val reply=f.selected.request(value)
            if(value.operation==FileOperation.READ_RANGE && changed.compareAndSet(false,true)) f.change(f.data.getString("nested")!!,"changed during copying")
            reply})
        val row=f.stage(source); assertEquals(PhoneBatchSourcePhase.FAILED,row.phase); assertTrue(changed.get()); f.assertNoDescriptors()
        val review=f.sources.reviewRemoval(row.id); f.sources.removeReviewed(review); assertTrue(f.sources.state.value.isEmpty()); assertEquals("changed during copying",f.providerBytes(f.data.getString("nested")!!).decodeToString())
    }}
    @Test fun foregroundWithdrawalStopsPrivatePreparationWithoutDeletingOriginals()=runBlocking {fixture {f->
        val original=f.providerBytes(f.data.getString("file")!!); val source=f.selected.copy(request={value->val reply=f.selected.request(value); if(value.operation==FileOperation.READ_RANGE) f.allowed.set(false); reply})
        val row=f.stage(source); assertEquals(PhoneBatchSourcePhase.FAILED,row.phase); f.assertNoDescriptors(); assertArrayEquals(original,f.providerBytes(f.data.getString("file")!!))
        f.sources.removeReviewed(f.sources.reviewRemoval(row.id)); assertTrue(f.sources.state.value.isEmpty())
    }}
    @Test fun aNewPairingCannotReadAnOldPrivatePlanButTheOwnerCanRemoveIt()=runBlocking {fixture {f->
        val row=f.ready(); val original=f.providerBytes(f.data.getString("file")!!)
        assertTrue(f.policy.revoke(setOf(RevokedApproval(f.approval.id,f.approval.epoch)))); f.approval=TestPeer.approve(f.policy,f.key)
        try {f.sources.manifest(row.id); fail("Old pairing must not read source metadata for sending.")} catch(_:IllegalStateException) {}
        f.sources.removeReviewed(f.sources.reviewRemoval(row.id)); assertArrayEquals(original,f.providerBytes(f.data.getString("file")!!)); assertTrue(f.sources.state.value.isEmpty())
    }}
    @Test fun fullChecksumRejectsChangedPrivateBytesEvenWhenReceiptVersionsAreRewritten()=runBlocking {fixture {f->
        val row=f.ready(); val manifest=f.sources.manifest(row.id); val index=manifest.itemsList.indexOfFirst {it.name=="simu.txt"}; val path=f.privateFile(row.id,"$index.bin")
        val bytes=path.readBytes(); bytes[0]=(bytes[0].toInt() xor 1).toByte(); path.writeBytes(bytes)
        val stat=Os.lstat(path.path); val version="${stat.st_dev}:${stat.st_ino}:${stat.st_size}:${stat.st_mtim.tv_sec}:${stat.st_mtim.tv_nsec}:${stat.st_ctim.tv_sec}:${stat.st_ctim.tv_nsec}"
        val receipt=f.privateFile(row.id,"$index.json"); receipt.writeText(JSONObject(receipt.readText()).put("stamp",version).toString())
        try {f.open(row.id,index).close(); fail("Complete digest must bind the bytes before remote effects.")} catch(_:IllegalStateException) {}
        f.assertNoDescriptors(); assertFalse(f.providerBytes(f.data.getString("file")!!).contentEquals(bytes))
    }}
    @Test fun damagedParentAndMissingMetadataCannotAdoptRetainedPrivateCopies()=runBlocking {fixture {f->
        val row=f.ready(); val files=f.privateFile(row.id,"manifest.bin").parentFile!!.listFiles()!!.size; f.record(row.id).writeText("{damaged")
        try {f.sources.manifest(row.id); fail("Changed ownership must block reuse.")} catch(_:IllegalStateException) {}
        f.reopen(); assertTrue(f.sources.recoveryIssues().isNotEmpty()); assertEquals(files,f.privateFile(row.id,"manifest.bin").parentFile!!.listFiles()!!.size)
        check(f.record(row.id).delete()); f.reopen(); assertTrue(f.sources.recoveryIssues().isNotEmpty()); assertTrue(f.privateFile(row.id,"manifest.bin").exists()); f.assertNoDescriptors()
    }}
    @Test fun replacedPrivateFoldersCannotBeAdoptedByTheSamePlanName()=runBlocking {fixture {f->
        val row=f.ready(); val folder=f.privateFile(row.id,"manifest.bin").parentFile!!; val kept=File(folder.path+".kept"); check(folder.renameTo(kept)); check(folder.mkdir())
        kept.listFiles()!!.forEach {check(it.renameTo(File(folder,it.name)))}
        try {f.sources.manifest(row.id); fail("The original private folder identity must still own the plan.")} catch(_:IllegalStateException) {}
        f.reopen(); assertTrue(f.sources.recoveryIssues().isNotEmpty()); assertTrue(File(folder,"manifest.bin").exists())
    }}
    @Test fun reviewedRemovalIsSingleUseAndRejectsAddedUnreviewedFilesBeforeDeletingAnything()=runBlocking {fixture {f->
        val row=f.ready(); val review=f.sources.reviewRemoval(row.id)
        try {f.sources.removeReviewed(review.copy(bytes=review.bytes+1)); fail("Changed confirmation must fail.")} catch(_:IllegalStateException) {}
        val again=f.sources.reviewRemoval(row.id); val added=f.privateFile(row.id,"owner-added.txt").apply {writeText("keep")}
        try {f.sources.removeReviewed(again); fail("Added owner data must stay untouched.")} catch(_:IllegalStateException) {}
        assertEquals("keep",added.readText()); assertTrue(f.privateFile(row.id,"manifest.bin").exists()); check(added.delete())
        val final=f.sources.reviewRemoval(row.id); f.sources.removeReviewed(final)
        try {f.sources.removeReviewed(final); fail("A review must not replay.")} catch(_:IllegalStateException) {}
        assertTrue(f.sources.state.value.isEmpty()); f.reopen(); assertTrue(f.sources.recoveryIssues().isEmpty()); f.assertNoDescriptors()
    }}
    @Test fun heldReadersBlockOwnerCleanupAndCheckAvailabilityBeforeEveryRange()=runBlocking {fixture {f->
        val row=f.ready(); val manifest=f.sources.manifest(row.id); val index=manifest.itemsList.indexOfFirst {it.name=="simu.txt"}; val allowed=AtomicBoolean(true)
        f.open(row.id,index) {allowed.get()}.use {source->
            try {f.sources.reviewRemoval(row.id); fail("Stop sending before cleanup review.")} catch(_:IllegalStateException) {}
            allowed.set(false); try {source.range(0,1); fail("A retained file is not continuing authority.")} catch(_:IllegalStateException) {}
            allowed.set(true); assertEquals(1,source.range(0,1).size)
        }
        f.assertNoDescriptors(); f.sources.removeReviewed(f.sources.reviewRemoval(row.id)); assertTrue(f.sources.state.value.isEmpty())
    }}
    @Test fun canceledPreparationAndOneOwnerLockDoNotLeaveActiveOrUnownedWork()=runBlocking {fixture {f->
        try {AndroidBatchSources(f.context,f.policy); fail("Only one preparation host may own the parts.")} catch(_:Exception) {}
        repeat(16) {
            val entered=CompletableDeferred<Unit>(); val source=f.selected.copy(request={value->if(value.operation==FileOperation.READ_RANGE) {entered.complete(Unit); awaitCancellation()}; f.selected.request(value)})
            val id=f.sources.stage(f.approval,listOf(source),UploadCollision.UPLOAD_FAIL) {true}; withTimeout(10_000) {entered.await()}; f.sources.cancel(id); withTimeout(5000) {f.sources.whenIdle()}
            assertEquals(PhoneBatchSourcePhase.FAILED,f.sources.state.value.single().phase); assertFalse(f.sources.state.value.single().active); f.assertNoDescriptors()
            f.sources.removeReviewed(f.sources.reviewRemoval(id)); assertTrue(f.sources.state.value.isEmpty())
        }
    }}
    @Test fun localCaptureRemainsBoundToTheVisibleCatalogAndDoesNotForgeSingleFileAuthority()=runBlocking {fixture {f->
        f.main {try {f.browser.captureUpload(f.selected.entry.toBuilder().setIsDirectory(false).build()); fail("A forged kind must not substitute for the catalog entry.")} catch(_:IllegalStateException) {}}
        f.main {f.browser.browse(f.selected.entry)}
        try {f.sources.stage(f.approval,listOf(f.selected),UploadCollision.UPLOAD_FAIL) {true}; fail("Navigation invalidates the captured local scope.")} catch(_:IllegalStateException) {}
        assertTrue(f.sources.state.value.isEmpty()); assertTrue(AndroidStorageGrants(f.context,f.policy).all().isEmpty())
    }}
    @Test fun unboundFoldersBesideInterruptedIntentsArePreservedAndBlockColdAdoption()=runBlocking {fixture {f->
        val entered=CompletableDeferred<Unit>(); val source=f.selected.copy(request={value->if(value.operation==FileOperation.GET_METADATA) {entered.complete(Unit); awaitCancellation()}; f.selected.request(value)})
        val id=f.sources.stage(f.approval,listOf(source),UploadCollision.UPLOAD_FAIL) {true}; withTimeout(5000) {entered.await()}; f.sources.cancel(id); withTimeout(5000) {f.sources.whenIdle()}
        assertEquals("",f.sources.state.value.single().directory)
        val unknown=f.privateFile(id,"owner-added.txt"); check(unknown.parentFile!!.mkdir()); unknown.writeText("keep")
        f.reopen(); assertTrue(f.sources.recoveryIssues().isNotEmpty()); assertEquals("keep",unknown.readText())
        try {f.sources.stage(f.approval,listOf(f.selected),UploadCollision.UPLOAD_FAIL) {true}; fail("Unbound owner data must not become another source copy.")} catch(e:StorageFailure) {assertEquals(FileStatus.CONFLICT,e.status)}
    }}
    @Test fun invalidRangeEofCannotProduceAReadySnapshotOrChangeTheOriginal()=runBlocking {fixture {f->
        val original=f.providerBytes(f.data.getString("nested")!!)
        val source=f.selected.copy(request={value->val response=f.selected.request(value); if(value.operation==FileOperation.READ_RANGE) response.toBuilder().setEndOfFile(!response.endOfFile).build() else response})
        assertEquals(PhoneBatchSourcePhase.FAILED,f.stage(source).phase); f.assertNoDescriptors(); assertArrayEquals(original,f.providerBytes(f.data.getString("nested")!!))
    }}
    @Test fun full4096ItemPlanCopiesActualPrivateFilesAndReopensTheLastVerifiedSource()=runBlocking {fixture(extras=4089) {f->
        val id=f.sources.stage(f.approval,listOf(f.selected),UploadCollision.UPLOAD_KEEP_BOTH) {true}
        withTimeout(30*60_000L) {f.sources.whenIdle()}; val row=f.sources.state.value.single {it.id==id}; assertEquals(row.message,PhoneBatchSourcePhase.READY,row.phase)
        val manifest=f.sources.manifest(id); assertEquals(4096,manifest.itemsCount); assertEquals(4092,row.preparedFiles); f.assertNoDescriptors()
        assertEquals(4092,f.privateFile(id,"manifest.bin").parentFile!!.listFiles()!!.count {it.name.endsWith(".bin") && it.name!="manifest.bin"})
        f.reopen(); assertTrue(f.sources.recoveryIssues().isEmpty()); val last=manifest.itemsList.indexOfLast {!it.isDirectory && it.error.isEmpty() && it.size>0}
        f.open(id,last).use {source->if(source.size>0) assertArrayEquals(manifest.getItems(last).sha256.toByteArray(),Rules.digest(source.range(0,source.size.toInt())))}
        assertTrue(AndroidStorageGrants(f.context,f.policy).all().isEmpty()); f.assertNoDescriptors()
    }}
}
