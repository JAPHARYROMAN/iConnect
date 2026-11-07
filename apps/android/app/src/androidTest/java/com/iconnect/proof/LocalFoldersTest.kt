package com.iconnect.proof

import android.content.Context
import android.content.ContextWrapper
import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.provider.DocumentsContract as Docs
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.iconnect.protocol.*
import kotlinx.coroutines.*
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.util.concurrent.atomic.AtomicLong

@RunWith(AndroidJUnit4::class)
class LocalFoldersTest {
    private class Fixture:AutoCloseable {
        val base=InstrumentationRegistry.getInstrumentation().targetContext
        val prefix="local-folders-${StorageRules.token()}"
        val root=File(base.filesDir,prefix).apply {check(mkdirs())}
        val context=object:ContextWrapper(base) {
            override fun getFilesDir()=root
            override fun getCacheDir()=File(root,"cache").apply {mkdirs()}
            override fun getApplicationContext():Context=this
            override fun getSharedPreferences(name:String,mode:Int)=base.getSharedPreferences(prefix+name,mode)
        }
        val fixture=requireNotNull(base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"fixture",null,null))
        val tree=Uri.parse(fixture.getString("tree"))
        val flags=fixture.getInt("flags")
        val rootId=requireNotNull(fixture.getString("root"))
        val locations=LocalDocumentLocations(context)
        val source=AndroidFileService(context,locations as DocumentAccessPolicy)
        val tick=AtomicLong(android.os.SystemClock.elapsedRealtime())
        val wall=AtomicLong(System.currentTimeMillis())
        var current=true
        val directory=File(root,"folder-history")
        val journal get()=File(directory,"journal.json")
        var manager:PhoneLocalFolders
        init {locations.add(tree,Intent.FLAG_GRANT_READ_URI_PERMISSION); manager=open()}
        fun open()=PhoneLocalFolders(context,{target->current && locations.all().any {it.id==target.location && it.tree==target.tree}},directory,wall::get,tick::get)
        suspend fun restart() {manager.shutdown(); manager=open(); manager.setVisible(true)}
        suspend fun target(name:String="Created locally",nested:Boolean=false):LocalFolderTarget {
            suspend fun rpc(request:FileRequest)=source.execute(locations.ownerId(),request.toBuilder().setRequestId(StorageRules.token()).build())
            val root=rpc(FileRequest.newBuilder().setOperation(FileOperation.LIST_ROOTS).build()).entriesList.single()
            val entry=if(nested) rpc(FileRequest.newBuilder().setOperation(FileOperation.LIST_DIRECTORY).setNodeId(root.id).build()).entriesList.single {it.name=="Reports"} else root
            return source.captureLocalFolder(locations.ownerId(),entry,name).copy(captured=tick.get())
        }
        suspend fun review(name:String="Created locally",nested:Boolean=false):PhoneLocalFolderReview {
            manager.setVisible(true); val target=target(name,nested); manager.approve(target,tree,flags)
            await { !manager.state.value.busy }; return requireNotNull(manager.state.value.review) {manager.state.value.error}
        }
        fun mode(value:String,parent:String=rootId) {base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"creationMode",parent,Bundle().apply {putString("mode",value)})}
        fun children(parent:String=rootId):Map<String,String> = base.contentResolver.query(Docs.buildChildDocumentsUriUsingTree(tree,parent),
            arrayOf(Docs.Document.COLUMN_DOCUMENT_ID,Docs.Document.COLUMN_DISPLAY_NAME),null,null,null)!!.use {cursor->buildMap {while(cursor.moveToNext()) put(cursor.getString(0),cursor.getString(1))}}
        fun original()=base.contentResolver.openInputStream(Docs.buildDocumentUriUsingTree(tree,fixture.getString("file")))!!.use {it.readBytes()}
        fun writeGranted()=base.contentResolver.persistedUriPermissions.any {it.uri==tree && it.isWritePermission}
        suspend fun dispose() {
            manager.shutdown(); source.close(); locations.all().toList().forEach {locations.remove(it.id)}
            base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"dispose",rootId,null)
            listOf("iconnect.proof","iconnect.storage","iconnect.saf-access").forEach {base.deleteSharedPreferences(prefix+it)}
            check(root.canonicalFile.parentFile==base.filesDir.canonicalFile); root.deleteRecursively()
        }
        override fun close()=runBlocking {dispose()}
        companion object {suspend fun await(condition:()->Boolean)=withTimeout(15000) {while(!condition()) delay(15)}}
    }
    @Test fun separateCreationApprovalCanCancelWithoutChangingTheFolderOrReadShortcut():Unit=runBlocking {Fixture().use {f->
        val before=f.children(); val original=f.original(); val shortcuts=f.locations.all(); assertFalse(f.writeGranted())
        val review=f.review(); assertTrue(f.writeGranted()); assertTrue(review.folder.contains("Phone fixture"))
        f.manager.cancelReview().join(); assertNull(f.manager.state.value.review); assertFalse(f.writeGranted())
        assertEquals(before,f.children()); assertArrayEquals(original,f.original()); assertEquals(shortcuts,f.locations.all())
        assertTrue(AndroidStorageGrants(f.context).all().isEmpty()); assertFalse(f.journal.exists())
    }}
    @Test fun nestedCreationIsDurableNeverReplaysAndHistoryRemovalKeepsProviderContents():Unit=runBlocking {Fixture().use {f->
        val events=mutableListOf<PhoneActivityEvent>(); f.manager.activity={events.add(it)}; val original=f.original()
        val review=f.review(nested=true); f.manager.create(review.id).join(); val row=f.manager.state.value.receipts.single()
        assertEquals(PhoneLocalFolderPhase.CREATED,row.phase); assertEquals("Created locally",row.resultingName)
        assertEquals(1,f.children(requireNotNull(f.fixture.getString("folder"))).values.count {it=="Created locally"}); assertFalse(f.writeGranted())
        val before=f.children(); val journal=f.journal.readBytes(); f.manager.create(review.id).join(); assertArrayEquals(journal,f.journal.readBytes())
        assertEquals(2,events.size); assertEquals(PhoneActivityOutcome.REQUESTED,events[0].outcome); assertEquals(PhoneActivityOutcome.COMPLETED,events[1].outcome)
        assertTrue(events.all {it.kind==PhoneActivityKind.LOCAL_FOLDER_CREATE && it.peer.isEmpty() && it.bytes== -1L && it.operation== -1})
        assertFalse(events.toString().contains(review.id)); assertFalse(events.toString().contains(f.tree.toString()))
        f.restart(); assertNull(f.manager.state.value.review); assertFalse(f.writeGranted()); assertEquals(row,f.manager.state.value.receipts.single())
        f.manager.create(row.id).join(); f.manager.removeReceipt(row.id).join(); assertTrue(f.manager.state.value.receipts.isEmpty())
        assertEquals(before,f.children()); assertArrayEquals(original,f.original()); assertTrue(f.children(f.fixture.getString("folder")!!).values.contains(row.name))
    }}
    @Test fun collisionsAndWrongPickerRootsNeverCallTheProvider():Unit=runBlocking {Fixture().use {f->
        val before=f.children(); val review=f.review("reports"); f.manager.create(review.id).join()
        assertTrue(f.manager.state.value.receipts.isEmpty()); assertEquals(before,f.children()); assertFalse(f.writeGranted())
        val target=f.target(); val other=Docs.buildTreeDocumentUri(f.tree.authority,f.fixture.getString("folder"))
        f.manager.approve(target,other,f.flags); Fixture.await {!f.manager.state.value.busy}
        assertNull(f.manager.state.value.review); assertEquals(before,f.children()); assertFalse(f.writeGranted())
    }}
    @Test fun readOnlyPickerAndRemovedLocalApprovalCannotConferWriteAuthority():Unit=runBlocking {Fixture().use {f->
        f.manager.setVisible(true); val target=f.target(); f.manager.approve(target,f.tree,Intent.FLAG_GRANT_READ_URI_PERMISSION)
        Fixture.await {!f.manager.state.value.busy}; assertNull(f.manager.state.value.review); assertFalse(f.writeGranted())
        val before=f.children(); val review=f.review(); f.current=false; f.manager.create(review.id).join()
        assertTrue(f.manager.state.value.receipts.isEmpty()); assertEquals(before,f.children()); assertFalse(f.writeGranted())
    }}
    @Test fun uncertainAndInvalidProviderResultsAreRetainedForReviewAndNeverRetried():Unit=runBlocking {
        for(mode in listOf("existing","fileForFolder","nonemptyFolder","misplacedFolder","loseFolderReceipt")) Fixture().use {f->
            val original=f.original(); val before=f.children(); val review=f.review(); f.mode(mode); f.manager.create(review.id).join()
            assertEquals(mode,PhoneLocalFolderPhase.REVIEW,f.manager.state.value.receipts.single().phase)
            val after=f.children(); f.manager.create(review.id).join(); assertEquals(after,f.children()); assertArrayEquals(original,f.original()); assertFalse(f.writeGranted())
            if(mode=="existing") assertEquals(before,after)
            f.restart(); f.manager.create(review.id).join(); assertEquals(after,f.children()); assertEquals(PhoneLocalFolderPhase.REVIEW,f.manager.state.value.receipts.single().phase)
        }
    }
    @Test fun wallAndElapsedExpiryInvalidateTheSingleUseReview():Unit=runBlocking {
        for(wall in listOf(false,true)) Fixture().use {f->val before=f.children(); val review=f.review()
            if(wall) f.wall.addAndGet(300000) else f.tick.addAndGet(300000)
            f.manager.create(review.id).join(); assertNull(f.manager.state.value.review); assertTrue(f.manager.state.value.receipts.isEmpty())
            assertEquals(before,f.children()); assertFalse(f.writeGranted())
        }
    }
    @Test fun parentRenameAfterReviewStopsBeforeTheProviderCall():Unit=runBlocking {Fixture().use {f->
        val review=f.review(nested=true); val parent=f.fixture.getString("folder")!!
        f.base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"change",parent,Bundle().apply {putString("name","Changed parent")})
        val before=f.children(parent); f.manager.create(review.id).join(); assertTrue(f.manager.state.value.receipts.isEmpty()); assertEquals(before,f.children(parent)); assertFalse(f.writeGranted())
    }}
    @Test fun hidingWithdrawsTheReviewAndOnlyItsUnusedWriteAccess():Unit=runBlocking {Fixture().use {f->
        val access=AndroidSafAccess(f.context); access.acquire("other-owner",f.tree,f.flags,3)
        val before=f.children(); val review=f.review(); f.manager.setVisible(false); assertNull(f.manager.state.value.review)
        assertThrows(IllegalStateException::class.java) {f.manager.create(review.id)}
        f.manager.setVisible(true); f.manager.create(review.id).join(); assertEquals(before,f.children()); assertTrue(f.writeGranted())
        access.release("other-owner"); Fixture.await {!f.writeGranted()}; assertTrue(f.base.contentResolver.persistedUriPermissions.any {it.uri==f.tree && it.isReadPermission})
    }}
    @Test fun foregroundLossDuringAProviderCallLeavesOneUncertainResultWithoutReplay():Unit=runBlocking {Fixture().use {f->
        val review=f.review(); f.mode("blockFolderReceipt"); val work=f.manager.create(review.id); val probe=f.rootId+"/create"
        try {
            Fixture.await {f.base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"queryProbe",probe,null)?.getBoolean("entered")==true}
            f.manager.setVisible(false)
        } finally {f.base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"releaseQuery",probe,null)}
        work.join(); assertEquals(PhoneLocalFolderPhase.REVIEW,f.manager.state.value.receipts.single().phase); assertFalse(f.writeGranted())
        val after=f.children(); assertEquals(1,after.values.count {it==review.name}); f.manager.setVisible(true); f.manager.create(review.id).join(); assertEquals(after,f.children())
    }}
    @Test fun failedIntentCommitCannotCreateAFolder():Unit=runBlocking {Fixture().use {f->
        val before=f.children(); val review=f.review(); val marker=File(f.directory,"journal.json.new"); check(marker.mkdir())
        try {f.manager.create(review.id).join(); assertEquals(before,f.children()); assertTrue(f.manager.state.value.issues.isNotEmpty()); assertFalse(f.writeGranted())}
        finally {check(marker.delete())}
    }}
    @Test fun failedCompletionCommitKeepsPendingIntentAndTheProviderResult():Unit=runBlocking {Fixture().use {f->
        val review=f.review(); f.mode("blockFolderReceipt"); val work=f.manager.create(review.id); val probe=f.rootId+"/create"; val marker=File(f.directory,"journal.json.new")
        try {
            Fixture.await {f.base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"queryProbe",probe,null)?.getBoolean("entered")==true}; check(marker.mkdir())
        } finally {f.base.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"releaseQuery",probe,null)}
        work.join(); assertTrue(f.manager.state.value.issues.isNotEmpty()); assertEquals("PENDING",JSONObject(f.journal.readText()).getJSONArray("records").getJSONObject(0).getString("phase"))
        val after=f.children(); assertEquals(1,after.values.count {it==review.name}); assertFalse(f.writeGranted()); check(marker.delete())
        f.restart(); assertEquals(PhoneLocalFolderPhase.REVIEW,f.manager.state.value.receipts.single().phase); f.manager.create(review.id).join(); assertEquals(after,f.children())
    }}
    @Test fun unreadableAndAmbiguousHistoryBlocksCreationUntilMetadataIsPreserved():Unit=runBlocking {Fixture().use {f->
        f.manager.shutdown(); f.journal.writeText("{broken"); f.manager=f.open(); f.manager.setVisible(true)
        val before=f.children(); f.manager.approve(f.target(),f.tree,f.flags); Fixture.await {!f.manager.state.value.busy}; assertNull(f.manager.state.value.review); assertFalse(f.writeGranted())
        val issue=f.manager.state.value.issues.single(); assertTrue(issue.canArchive); f.manager.preserve(issue.id).join()
        assertTrue(f.manager.state.value.issues.isEmpty()); assertEquals(1,f.manager.state.value.preserved); assertEquals(before,f.children())
        f.manager.clearPreserved().join(); assertEquals(0,f.manager.state.value.preserved)
        val review=f.review(); f.manager.create(review.id).join(); assertEquals(PhoneLocalFolderPhase.CREATED,f.manager.state.value.receipts.single().phase)
        f.manager.shutdown(); File(f.directory,"journal.json.bak").writeText(f.journal.readText()); f.manager=f.open()
        assertTrue(f.manager.state.value.issues.isNotEmpty()); assertTrue(f.manager.state.value.receipts.isEmpty())
    }}
    @Test fun receiptCapacityCanBeReleasedWithoutChangingAnyProviderFolder():Unit=runBlocking {Fixture().use {f->
        val review=f.review(); f.manager.create(review.id).join(); f.manager.shutdown()
        val root=JSONObject(f.journal.readText()); val template=root.getJSONArray("records").getJSONObject(0); val rows=JSONArray()
        repeat(128) {rows.put(JSONObject(template.toString()).put("id",StorageRules.token()))}; root.put("records",rows); f.journal.writeText(root.toString())
        f.manager=f.open(); f.manager.setVisible(true); val before=f.children(); f.manager.approve(f.target("Next folder"),f.tree,f.flags); Fixture.await {!f.manager.state.value.busy}
        assertNull(f.manager.state.value.review); assertEquals(128,f.manager.state.value.receipts.size); assertEquals(before,f.children()); assertFalse(f.writeGranted())
        f.manager.removeReceipt(f.manager.state.value.receipts.first().id).join(); assertEquals(127,f.manager.state.value.receipts.size); assertEquals(before,f.children())
        val next=f.review("Next folder"); f.manager.create(next.id).join(); assertEquals(128,f.manager.state.value.receipts.size); assertEquals(1,f.children().values.count {it=="Next folder"})
    }}
    @Test fun concurrentHistoryOwnerIsRefusedAndCloseWithdrawsUnusedApproval():Unit=runBlocking {Fixture().use {f->
        assertThrows(Exception::class.java) {f.open()}; f.review(); assertTrue(f.writeGranted()); f.manager.shutdown(); assertFalse(f.writeGranted())
        assertTrue(f.locations.all().isNotEmpty()); assertTrue(f.original().isNotEmpty())
    }}
}
