package com.iconnect.proof

import android.net.Uri
import android.os.Bundle
import android.provider.DocumentsContract as Docs
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.iconnect.protocol.*
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

@RunWith(AndroidJUnit4::class)
class LocalCopyTest {
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext
    private fun create() = requireNotNull(context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "fixture", null, null))
    private fun change(fixture: Bundle, method: String, extras: Bundle) = context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, method, fixture.getString("root"), extras)
    private fun tree(fixture: Bundle) = Uri.parse(fixture.getString("tree"))
    private fun original(fixture: Bundle) = Docs.buildDocumentUriUsingTree(tree(fixture), fixture.getString("file"))
    private fun bytes(uri: Uri) = context.contentResolver.openInputStream(uri)!!.use { it.readBytes() }
    private suspend fun waitFor(condition: () -> Boolean) = withTimeout(15_000) { while (!condition()) delay(15) }
    private suspend fun fixture(body: suspend (Bundle, Bundle, File, LocalDocumentLocations, AndroidFileService) -> Unit) {
        val from = create(); val to = create(); val locations = LocalDocumentLocations(context)
        val directory = File(context.filesDir, "local-copy-test-${StorageRules.token()}").apply { check(mkdirs()) }
        try {
            locations.add(tree(from), from.getInt("flags"))
            AndroidFileService(context, locations as DocumentAccessPolicy).use { host -> body(from, to, directory, locations, host) }
        } finally {
            // Reopen the generated queue so each purpose releases its own OS
            // access before this disposable test directory is removed.
            val cleanup = PhoneLocalCopies(context, directory); cleanup.setVisible(true)
            try {
                cleanup.state.value.issues.filter { it.canArchive }.forEach { cleanup.preserve(it.id) }
                cleanup.state.value.items.toList().forEach { item ->
                    if (item.download?.phase !in setOf(PhoneDownloadPhase.COMPLETED, PhoneDownloadPhase.CANCELED, PhoneDownloadPhase.SKIPPED)) cleanup.cancel(item.id)
                    if (cleanup.state.value.items.any { it.id == item.id && it.download != null }) cleanup.remove(item.id)
                }
            } finally { cleanup.shutdown() }
            locations.all().filter { it.tree == tree(from) }.forEach { locations.remove(it.id) }
            listOf(from, to).forEach { context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "dispose", it.getString("root"), null) }
            check(directory.canonicalFile.parentFile == context.filesDir.canonicalFile); directory.deleteRecursively()
        }
    }
    private suspend fun select(host: AndroidFileService, locations: LocalDocumentLocations): LocalCopySelection {
        suspend fun rpc(request: FileRequest) = host.execute(locations.ownerId(), request.toBuilder().setRequestId(StorageRules.token()).build())
        val root = rpc(FileRequest.newBuilder().setOperation(FileOperation.LIST_ROOTS).build()).entriesList.single()
        val entry = rpc(FileRequest.newBuilder().setOperation(FileOperation.LIST_DIRECTORY).setNodeId(root.id).build()).entriesList.single { it.name == "simu.txt" }
        return host.captureLocalFile(locations.ownerId(), entry)
    }
    @Test fun liveLocalPayloadIsSeparateFromTheCheckpointAndForegroundWithdrawalClearsIt():Unit=runBlocking {fixture {from,to,directory,locations,host->
        val expected=ByteArray(8*1024*1024+17) {(it*31%251).toByte()}
        context.contentResolver.openOutputStream(original(from),"wt")!!.use {it.write(expected)}
        change(from,"creationMode",Bundle().apply {putString("mode","slowRead")})
        val copies=PhoneLocalCopies(context,directory); copies.setVisible(true)
        try {
            val id=copies.prepare(select(host,locations)); val seen=CompletableDeferred<PhoneLocalCopyState>()
            val watcher=launch(Dispatchers.Default,start=CoroutineStart.UNDISPATCHED) {copies.state.collect {view->
                if((view.liveBytes[id]?:0)>0 && !seen.isCompleted) {seen.complete(view); copies.setVisible(false)}
            }}
            try {
                copies.chooseDestination(id,tree(to),to.getInt("flags"),PhoneCollision.KEEP_BOTH).join()
                val view=withTimeout(15000) {seen.await()}; val row=view.items.single().download!!
                assertTrue(view.liveBytes.getValue(id)>=row.offset); assertNotNull(view.payloadEstimates[id]); assertEquals("",row.peer)
                waitFor {!copies.state.value.items.single().download!!.active}
                assertTrue(copies.state.value.liveBytes.isEmpty()); assertTrue(copies.state.value.payloadEstimates.isEmpty())
                copies.setVisible(true); copies.resume(id).join(); waitFor {!copies.state.value.items.single().download!!.active}
                val done=copies.state.value.items.single().download!!; assertEquals(done.error,PhoneDownloadPhase.COMPLETED,done.phase)
                assertTrue(copies.state.value.payloadEstimates.isEmpty()); assertArrayEquals(expected,bytes(original(from))); assertArrayEquals(expected,bytes(Uri.parse(done.published)))
                assertFalse(localPayloadText(done,null).contains("limit"))
            } finally {watcher.cancelAndJoin()}
        } finally {copies.shutdown()}
        val reopened=PhoneLocalCopies(context,directory)
        try {assertTrue(reopened.state.value.liveBytes.isEmpty()); assertTrue(reopened.state.value.payloadEstimates.isEmpty())} finally {reopened.shutdown()}
    } }
    @Test fun offlineCopyVerifiesProviderBytesKeepsOriginalAndHasNoRemoteAuthority() = runBlocking { fixture { from, to, directory, locations, host ->
        val grants = AndroidStorageGrants(context); val before = grants.all(); val events = mutableListOf<PhoneActivityEvent>()
        val selected = select(host, locations); val expected = bytes(original(from)); val copies = PhoneLocalCopies(context, directory)
        try {
            copies.activity = { synchronized(events) { events.add(it) } }; copies.setVisible(true)
            val id = copies.prepare(selected)
            assertNull(copies.state.value.items.single().download)
            locations.remove(locations.all().single { it.tree == tree(from) }.id)
            assertTrue(context.contentResolver.persistedUriPermissions.any { it.uri == tree(from) && it.isReadPermission })
            copies.chooseDestination(id, tree(to), to.getInt("flags"), PhoneCollision.KEEP_BOTH).join()
            val row = copies.state.value.items.single().download!!
            assertEquals(row.error, PhoneDownloadPhase.COMPLETED, row.phase); assertEquals("", row.peer)
            assertEquals("simu (1).txt", row.publishedName)
            assertArrayEquals(expected, bytes(original(from))); assertArrayEquals(expected, bytes(Uri.parse(row.published)))
            assertEquals(before, grants.all())
            AndroidFileService(context, grants).use { remote ->
                val reply = remote.execute("f".repeat(64), FileRequest.newBuilder().setRequestId(StorageRules.token())
                    .setOperation(FileOperation.GET_METADATA).setNodeId(id).build())
                assertEquals(FileStatus.STALE_REFERENCE, reply.status)
            }
            val complete = synchronized(events) { events.single { it.outcome == PhoneActivityOutcome.COMPLETED } }
            assertEquals(PhoneActivityKind.LOCAL_COPY, complete.kind); assertTrue(complete.peer.isEmpty()); assertEquals(expected.size.toLong(), complete.bytes)
            copies.remove(id); assertTrue(copies.state.value.items.isEmpty())
            assertFalse(context.contentResolver.persistedUriPermissions.any { it.uri == tree(from) })
        } finally { copies.shutdown() }
    } }
    @Test fun destinationPickerCancellationLeavesAResumableDraftAcrossRestart() = runBlocking { fixture { _, to, directory, locations, host ->
        val first = PhoneLocalCopies(context, directory); first.setVisible(true)
        val id = first.prepare(select(host, locations)); first.setVisible(false); first.shutdown()
        val reopened = PhoneLocalCopies(context, directory)
        try {
            assertEquals(id, reopened.state.value.items.single().id); assertNull(reopened.state.value.items.single().download)
            var refused = false; try { reopened.chooseDestination(id, tree(to), to.getInt("flags"), PhoneCollision.KEEP_BOTH) } catch (_: Exception) { refused = true }
            assertTrue(refused); reopened.setVisible(true)
            reopened.chooseDestination(id, tree(to), to.getInt("flags"), PhoneCollision.KEEP_BOTH).join()
            assertEquals(PhoneDownloadPhase.COMPLETED, reopened.state.value.items.single().download!!.phase)
        } finally { reopened.shutdown() }
    } }
    @Test fun changedSourceCannotProduceAPublishedCopy() = runBlocking { fixture { from, to, directory, locations, host ->
        val copies = PhoneLocalCopies(context, directory); copies.setVisible(true)
        try {
            val id = copies.prepare(select(host, locations))
            context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "change", from.getString("file"), Bundle().apply { putString("content", "changed original") })
            copies.chooseDestination(id, tree(to), to.getInt("flags"), PhoneCollision.KEEP_BOTH).join()
            val row = copies.state.value.items.single().download!!
            assertEquals(PhoneDownloadPhase.INTERRUPTED, row.phase); assertTrue(row.published.isEmpty()); assertEquals(0, row.offset)
            assertEquals("changed original", bytes(original(from)).toString(Charsets.UTF_8))
        } finally { copies.shutdown() }
    } }
    @Test fun sourceMovedOutsideSelectedTreeIsRefused() = runBlocking { fixture { from, to, directory, locations, host ->
        val copies = PhoneLocalCopies(context, directory); copies.setVisible(true)
        try {
            val id = copies.prepare(select(host, locations))
            context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "change", from.getString("file"), Bundle().apply { putString("parent", to.getString("root")) })
            copies.chooseDestination(id, tree(to), to.getInt("flags"), PhoneCollision.KEEP_BOTH).join()
            assertEquals(PhoneDownloadPhase.INTERRUPTED, copies.state.value.items.single().download!!.phase)
            assertTrue(copies.state.value.items.single().download!!.published.isEmpty())
        } finally { copies.shutdown() }
    } }
    @Test fun skipAndStopCollisionsKeepTheExistingDestination() = runBlocking { fixture { _, to, directory, locations, host ->
        val before = bytes(original(to)); val copies = PhoneLocalCopies(context, directory); copies.setVisible(true)
        try {
            val selected = select(host, locations)
            val skip = copies.prepare(selected); copies.chooseDestination(skip, tree(to), to.getInt("flags"), PhoneCollision.SKIP).join()
            assertEquals(PhoneDownloadPhase.SKIPPED, copies.state.value.items.single().download!!.phase)
            val stop = copies.prepare(selected); copies.chooseDestination(stop, tree(to), to.getInt("flags"), PhoneCollision.FAIL).join()
            assertEquals(PhoneDownloadPhase.READY_TO_SAVE, copies.state.value.items.single { it.id == stop }.download!!.phase)
            assertArrayEquals(before, bytes(original(to)))
            copies.cancel(stop); copies.remove(stop); copies.remove(skip)
        } finally { copies.shutdown() }
    } }
    @Test fun substitutedExistingProviderDocumentIsKeptUntouchedByLocalCopy() = runBlocking { fixture { _, to, directory, locations, host ->
        val before = bytes(original(to)); val copies = PhoneLocalCopies(context, directory); copies.setVisible(true)
        try {
            val id = copies.prepare(select(host, locations)); change(to, "creationMode", Bundle().apply { putString("mode", "existing") })
            copies.chooseDestination(id, tree(to), to.getInt("flags"), PhoneCollision.KEEP_BOTH).join()
            assertEquals(PhoneDownloadPhase.PUBLICATION_INTERRUPTED, copies.state.value.items.single().download!!.phase)
            assertArrayEquals(before, bytes(original(to)))
        } finally { copies.shutdown() }
    } }
    @Test fun lostCreateReceiptRequiresOwnerDecisionAfterRestart() = runBlocking { fixture { _, to, directory, locations, host ->
        val first = PhoneLocalCopies(context, directory); first.setVisible(true)
        val id = first.prepare(select(host, locations)); change(to, "creationMode", Bundle().apply { putString("mode", "loseReceipt") })
        first.chooseDestination(id, tree(to), to.getInt("flags"), PhoneCollision.KEEP_BOTH).join()
        assertEquals(PhoneDownloadPhase.PUBLICATION_INTERRUPTED, first.state.value.items.single().download!!.phase); first.shutdown()
        change(to, "creationMode", Bundle().apply { putString("mode", "") })
        val reopened = PhoneLocalCopies(context, directory); reopened.setVisible(true)
        try {
            reopened.save(id).join(); assertEquals(PhoneDownloadPhase.PUBLICATION_INTERRUPTED, reopened.state.value.items.single().download!!.phase)
            reopened.save(id, true).join()
            assertEquals(PhoneDownloadPhase.COMPLETED, reopened.state.value.items.single().download!!.phase)
            assertEquals("simu (2).txt", reopened.state.value.items.single().download!!.publishedName)
        } finally { reopened.shutdown() }
    } }
    @Test fun foregroundWithdrawalStopsARealBlockedProviderWriteAndRetainsItsReceipt() = runBlocking { fixture { _, to, directory, locations, host ->
        val copies = PhoneLocalCopies(context, directory); copies.setVisible(true)
        try {
            val id = copies.prepare(select(host, locations)); change(to, "creationMode", Bundle().apply { putString("mode", "blockWrite") })
            val job = copies.chooseDestination(id, tree(to), to.getInt("flags"), PhoneCollision.KEEP_BOTH)
            waitFor {
                val published = copies.state.value.items.single().download?.published.orEmpty()
                published.isNotEmpty() && context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "openProbe",
                    Docs.getDocumentId(Uri.parse(published)), null)?.getBoolean("entered") == true
            }
            waitFor { id in copies.state.value.finalizations }
            val stage = copies.state.value.finalizations.getValue(id)
            assertEquals(PhoneIncomingFinalizationStage.PREPARING_PROVIDER_DOCUMENT, stage.stage)
            assertNull(stage.estimate)
            copies.setVisible(false)
            assertTrue(copies.state.value.finalizations.isEmpty())
            withTimeout(5_000) { job.join() }
            waitFor { copies.state.value.items.single().download?.active == false }
            assertEquals(PhoneDownloadPhase.PUBLICATION_INTERRUPTED, copies.state.value.items.single().download!!.phase)
            copies.setVisible(true); copies.cancel(id); assertEquals(PhoneDownloadPhase.CANCELED, copies.state.value.items.single().download!!.phase)
            assertTrue(copies.state.value.items.single().download!!.published.isNotEmpty())
        } finally { copies.shutdown() }
    } }
    @Test fun destinationResultBeforeForegroundIsDeferredWithoutCreatingRemoteApprovals() = runBlocking { fixture { _, to, directory, locations, host ->
        val copies = PhoneLocalCopies(context, directory); copies.setVisible(true)
        try {
            val id = copies.prepare(select(host, locations)); copies.setVisible(false)
            copies.acceptDestination(id, tree(to), to.getInt("flags"), PhoneCollision.KEEP_BOTH)
            delay(100); assertNull(copies.state.value.items.single().download)
            copies.setVisible(true); waitFor { copies.state.value.items.single().download?.phase == PhoneDownloadPhase.COMPLETED }
        } finally { copies.shutdown() }
    } }
    @Test fun invalidSourceJournalBlocksWorkUntilExactMetadataIsPreserved() = runBlocking { fixture { _, _, directory, locations, host ->
        val first = PhoneLocalCopies(context, directory); first.setVisible(true)
        val id = first.prepare(select(host, locations)); first.shutdown()
        val record = File(directory, "sources/$id.json"); record.writeText("{broken")
        val reopened = PhoneLocalCopies(context, directory); reopened.setVisible(true)
        try {
            val issue = reopened.state.value.issues.single(); assertTrue(issue.canArchive)
            var refused = false; try { reopened.prepare(select(host, locations)) } catch (_: Exception) { refused = true }; assertTrue(refused)
            reopened.preserve(issue.id); assertTrue(reopened.state.value.issues.isEmpty()); assertEquals(1, reopened.state.value.preserved)
            assertEquals("{broken", File(directory, "sources/recovery/${issue.id}/$id.json").readText())
            reopened.clearPreserved(); assertEquals(0, reopened.state.value.preserved)
        } finally { reopened.shutdown() }
    } }
    @Test fun sourceAdmissionIsBoundedAndCanceledDraftsReleaseOnlyTheirAccess() = runBlocking { fixture { from, _, directory, locations, host ->
        val copies = PhoneLocalCopies(context, directory); copies.setVisible(true)
        // A full source inventory refuses admission before any new permission or record is committed.
        try {
            val selected = select(host, locations)
            repeat(8) { copies.prepare(selected) }
            var refused = false; try { copies.prepare(selected) } catch (_: Exception) { refused = true }; assertTrue(refused)
            assertEquals(8, copies.state.value.items.size); assertTrue(copies.state.value.items.all { it.download == null })
            copies.state.value.items.toList().forEach { copies.cancel(it.id) }
            locations.remove(locations.all().single { it.tree == tree(from) }.id)
            assertFalse(context.contentResolver.persistedUriPermissions.any { it.uri == tree(from) })
        } finally { copies.shutdown() }
    } }
    @Test fun failedSourceJournalCommitRollsBackOnlyTheNewReadLease() = runBlocking { fixture { from, _, directory, locations, host ->
        val selected = select(host, locations); val copies = PhoneLocalCopies(context, directory); copies.setVisible(true)
        val sourceDirectory = File(directory, "sources"); val held = File(directory, "held-sources")
        check(sourceDirectory.canonicalFile.parentFile == directory.canonicalFile && held.canonicalFile.parentFile == directory.canonicalFile)
        try {
            check(sourceDirectory.renameTo(held)); sourceDirectory.writeText("generated write blocker")
            var refused = false; try { copies.prepare(selected) } catch (_: Exception) { refused = true }; assertTrue(refused)
            assertTrue(copies.state.value.items.isEmpty())
            assertTrue(context.contentResolver.persistedUriPermissions.any { it.uri == tree(from) && it.isReadPermission })
            locations.remove(locations.all().single { it.tree == tree(from) }.id)
            assertFalse(context.contentResolver.persistedUriPermissions.any { it.uri == tree(from) })
        } finally {
            check(sourceDirectory.delete()); check(held.renameTo(sourceDirectory)); copies.shutdown()
        }
    } }
    @Test fun lostReceivingMetadataCannotAdoptOrTruncateAnUnidentifiedPrivatePart() = runBlocking { fixture { _, to, directory, locations, host ->
        val first = PhoneLocalCopies(context, directory); first.setVisible(true)
        val id = first.prepare(select(host, locations)); first.shutdown()
        val part = File(directory, "receiving/$id.part"); val expected = "preserved private partial bytes".toByteArray(); part.writeBytes(expected)
        val record = File(directory, "receiving/$id.json"); record.writeText("{lost receiving metadata")
        val reopened = PhoneLocalCopies(context, directory); reopened.setVisible(true)
        try {
            reopened.preserve(reopened.state.value.issues.single().id)
            var refused = false; try { reopened.chooseDestination(id, tree(to), to.getInt("flags"), PhoneCollision.KEEP_BOTH) } catch (_: Exception) { refused = true }
            assertTrue(refused); assertArrayEquals(expected, part.readBytes()); assertNull(reopened.state.value.items.single().download)
            reopened.cancel(id); assertTrue(reopened.state.value.items.isEmpty()); assertArrayEquals(expected, part.readBytes())
        } finally { reopened.shutdown() }
    } }
    @Test fun canceledWorkerCannotDispatchAfterForegroundReturnAndFreshOwnerSaveStillWorks() = runBlocking { fixture { _, to, directory, locations, host ->
        val copies = PhoneLocalCopies(context, directory); copies.setVisible(true)
        try {
            val before = bytes(original(to)); val id = copies.prepare(select(host, locations))
            copies.chooseDestination(id, tree(to), to.getInt("flags"), PhoneCollision.FAIL).join()
            assertEquals(PhoneDownloadPhase.READY_TO_SAVE, copies.state.value.items.single().download!!.phase)
            copies.setVisible(false); copies.setVisible(true)
            val stale = Job().apply { cancel() }; var dispatched = false
            val dispatch: () -> Job = { dispatched = true; copies.save(id, true) }
            // Private worker boundary only: the test APK supplies no production
            // injection API, peer, or provider bypass. The save action is real.
            val entry = PhoneLocalCopies::class.java.getDeclaredMethod("receiving", kotlin.coroutines.CoroutineContext::class.java, Function0::class.java).apply { isAccessible = true }
            try {
                (entry.invoke(copies, stale, dispatch) as Job).join()
                fail("Canceled work must not dispatch a save after foreground restoration.")
            } catch (e: java.lang.reflect.InvocationTargetException) { assertTrue(e.cause is CancellationException) }
            assertFalse(dispatched); assertArrayEquals(before, bytes(original(to)))
            assertEquals(PhoneDownloadPhase.READY_TO_SAVE, copies.state.value.items.single().download!!.phase)
            copies.setVisible(false)
            var refused = false; try { copies.save(id, true) } catch (_: IllegalStateException) { refused = true }; assertTrue(refused)
            copies.setVisible(true); copies.save(id, true).join()
            val row = copies.state.value.items.single().download!!
            assertEquals(PhoneDownloadPhase.COMPLETED, row.phase); assertArrayEquals(before, bytes(Uri.parse(row.published)))
            assertArrayEquals(before, bytes(original(to)))
        } finally { copies.shutdown() }
    } }
}
