package com.iconnect.proof

import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.provider.DocumentsContract as Docs
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.iconnect.protocol.*
import kotlinx.coroutines.*
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

@RunWith(AndroidJUnit4::class)
class LocalBatchCopyTest {
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext
    private fun create(extra: Int = 0) = requireNotNull(context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "fixture", null, Bundle().apply { putBoolean("emptyFolder", true); putInt("extraFiles", extra); putBoolean("largeFile", true) }))
    private fun tree(value: Bundle) = Uri.parse(value.getString("tree"))
    private fun document(value: Bundle, id: String) = Docs.buildDocumentUriUsingTree(tree(value), id)
    private fun children(value: Bundle, id: String = value.getString("root")!!): Map<String, String> {
        val result = linkedMapOf<String, String>()
        context.contentResolver.query(Docs.buildChildDocumentsUriUsingTree(tree(value), id), arrayOf(Docs.Document.COLUMN_DOCUMENT_ID, Docs.Document.COLUMN_DISPLAY_NAME), null, null, null)!!.use { cursor ->
            while (cursor.moveToNext()) result[cursor.getString(1)] = cursor.getString(0)
        }; return result
    }
    private fun bytes(value: Bundle, id: String) = context.contentResolver.openInputStream(document(value, id))!!.use { it.readBytes() }
    private suspend fun waitFor(timeout: Long = 45_000, condition: () -> Boolean) = withTimeout(timeout) { while (!condition()) delay(15) }
    private suspend fun fixture(extra: Int = 0, action: suspend (Bundle, Bundle, File, LocalDocumentLocations, AndroidFileService) -> Unit) {
        val from = create(extra); val to = create(); val directory = File(context.filesDir, "local-batch-test-${StorageRules.token()}"); val locations = LocalDocumentLocations(context)
        try { locations.add(tree(from), from.getInt("flags")); AndroidFileService(context, locations as DocumentAccessPolicy).use { host -> action(from, to, directory, locations, host) } }
        finally {
            runCatching { val cleanup = PhoneLocalBatches(context, directory); cleanup.setVisible(true)
                try {
                    cleanup.state.value.issues.filter { it.canArchive }.forEach { cleanup.preserve(it.id) }
                    cleanup.state.value.items.toList().forEach { item -> runCatching { if (item.batch != null) { cleanup.cancel(item.id); cleanup.remove(item.id) } else if (item.started) cleanup.removeMissing(item.id) else cleanup.cancel(item.id) } }
                    cleanup.state.value.orphans.toList().forEach { row -> runCatching { cleanup.stopOrphan(row.id); cleanup.removeOrphan(row.id) } }
                    cleanup.state.value.items.toList().filter { it.batch == null }.forEach { runCatching { cleanup.removeMissing(it.id) } }
                    cleanup.clearPreserved()
                } finally { cleanup.shutdown() }
            }
            AndroidSafAccess(context).reconcile("local-batch:${Rules.hex(Rules.digest(directory.canonicalPath.toByteArray())).take(32)}:", emptyMap(), Intent.FLAG_GRANT_READ_URI_PERMISSION)
            locations.all().filter { it.tree == tree(from) }.forEach { locations.remove(it.id) }
            listOf(from, to).forEach { context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "dispose", it.getString("root"), null) }
            check(directory.canonicalFile.parentFile == context.filesDir.canonicalFile); directory.deleteRecursively()
        }
    }
    private suspend fun select(host: AndroidFileService, locations: LocalDocumentLocations, vararg names: String): List<LocalCopySelection> {
        suspend fun rpc(value: FileRequest) = host.execute(locations.ownerId(), value.toBuilder().setRequestId(StorageRules.token()).build())
        val root = rpc(FileRequest.newBuilder().setOperation(FileOperation.LIST_ROOTS).build()).entriesList.single()
        if (names.isEmpty()) return listOf(host.captureLocalItem(locations.ownerId(), root))
        val rows = PhoneTransferManifests.list({ rpc(it) }, root)
        return names.map { name -> host.captureLocalItem(locations.ownerId(), rows.single { it.name == name }) }
    }
    private suspend fun finish(copies: PhoneLocalBatches, id: String, timeout: Long = 90_000): PhoneBatch {
        waitFor(timeout) { copies.state.value.items.singleOrNull { it.id == id }?.batch?.let { !it.active } == true }
        return copies.state.value.items.single { it.id == id }.batch!!
    }
    @Test fun copiesNestedEmptyUnicodeAndZeroByteItemsWithoutPairingOrSharing() = runBlocking { fixture { from, to, directory, locations, host ->
        val root = document(from, from.getString("root")!!); val zero = Docs.createDocument(context.contentResolver, root, "application/octet-stream", "zero.bin")!!
        val unicode = Docs.createDocument(context.contentResolver, document(from, from.getString("folder")!!), "text/plain", "文書.txt")!!
        context.contentResolver.openOutputStream(unicode)!!.use { it.write("Generated Unicode bytes".toByteArray()) }
        val grants = AndroidStorageGrants(context).all(); val copies = PhoneLocalBatches(context, directory); copies.setVisible(true)
        try {
            val id = copies.prepare(select(host, locations, "Reports", "Empty", "zero.bin")); locations.all().filter { it.tree == tree(from) }.forEach { locations.remove(it.id) }
            copies.chooseDestination(id, tree(to), to.getInt("flags"), PhoneCollision.KEEP_BOTH).join(); val row = finish(copies, id)
            assertEquals(row.error, PhoneBatchPhase.COMPLETED, row.phase); assertTrue(row.peer.isEmpty() && row.epoch.isEmpty())
            val reports = row.items.single { it.source.name == "Reports" }; val empty = row.items.single { it.source.name == "Empty" }
            assertTrue(children(to, empty.document).isEmpty()); assertArrayEquals(byteArrayOf(), bytes(to, Docs.getDocumentId(Uri.parse(row.items.single { it.source.name == "zero.bin" }.published))))
            assertArrayEquals("Generated Unicode bytes".toByteArray(), bytes(to, children(to, reports.document).getValue("文書.txt")))
            assertArrayEquals(byteArrayOf(), context.contentResolver.openInputStream(zero)!!.use { it.readBytes() }); assertEquals(grants, AndroidStorageGrants(context).all())
            copies.remove(id); assertTrue(copies.state.value.items.isEmpty()); assertTrue(children(to).containsKey(reports.localName))
        } finally { copies.shutdown() }
    } }
    @Test fun draftAndSourceLeaseSurviveShortcutRemovalAndRestart() = runBlocking { fixture { from, to, directory, locations, host ->
        val first = PhoneLocalBatches(context, directory); first.setVisible(true); val id = first.prepare(select(host, locations, "Reports")); first.shutdown()
        locations.all().filter { it.tree == tree(from) }.forEach { locations.remove(it.id) }
        val copies = PhoneLocalBatches(context, directory)
        try {
            assertNull(copies.state.value.items.single().batch); assertTrue(context.contentResolver.persistedUriPermissions.any { it.uri == tree(from) && it.isReadPermission })
            copies.setVisible(true); copies.chooseDestination(id, tree(to), to.getInt("flags"), PhoneCollision.KEEP_BOTH).join(); assertEquals(PhoneBatchPhase.COMPLETED, finish(copies, id).phase)
            copies.remove(id); assertFalse(context.contentResolver.persistedUriPermissions.any { it.uri == tree(from) })
        } finally { copies.shutdown() }
    } }
    @Test fun allFolderCollisionChoicesKeepExistingProviderContents() = runBlocking { fixture { from, to, directory, locations, host ->
        val existing = bytes(to, to.getString("nested")!!); val copies = PhoneLocalBatches(context, directory); copies.setVisible(true)
        try { PhoneCollision.entries.forEach { collision ->
            val id = copies.prepare(select(host, locations, "Reports")); copies.chooseDestination(id, tree(to), to.getInt("flags"), collision).join(); val row = finish(copies, id)
            assertEquals(if (collision == PhoneCollision.KEEP_BOTH) PhoneBatchPhase.COMPLETED else PhoneBatchPhase.COMPLETED_WITH_ISSUES, row.phase)
            assertArrayEquals(existing, bytes(to, to.getString("nested")!!)); copies.remove(id)
        } } finally { copies.shutdown() }
    } }
    @Test fun frozenTreeExcludesLaterProviderDocuments() = runBlocking { fixture { from, to, directory, locations, host ->
        val copies = PhoneLocalBatches(context, directory); copies.setVisible(true)
        try { val id = copies.prepare(select(host, locations, "Reports")); Docs.createDocument(context.contentResolver, document(from, from.getString("folder")!!), "text/plain", "later.txt")
            copies.chooseDestination(id, tree(to), to.getInt("flags"), PhoneCollision.KEEP_BOTH).join(); val row = finish(copies, id); assertEquals(PhoneBatchPhase.COMPLETED, row.phase)
            assertFalse(children(to, row.items.first().document).containsKey("later.txt"))
        } finally { copies.shutdown() }
    } }
    @Test fun changedSourceVersionStopsBeforePublishingItsBytes() = runBlocking { fixture { from, to, directory, locations, host ->
        val copies = PhoneLocalBatches(context, directory); copies.setVisible(true)
        try { val id = copies.prepare(select(host, locations, "simu.txt")); context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "change", from.getString("file"), Bundle().apply { putString("content", "changed generated source") })
            copies.chooseDestination(id, tree(to), to.getInt("flags"), PhoneCollision.KEEP_BOTH).join(); assertEquals(PhoneBatchPhase.INTERRUPTED, finish(copies, id).phase)
            assertEquals(1, children(to).keys.count { it.startsWith("simu") })
        } finally { copies.shutdown() }
    } }
    @Test fun duplicateAndOverlappingRootsNeverCommitAPartialPlan() = runBlocking { fixture { _, _, directory, locations, host ->
        val copies = PhoneLocalBatches(context, directory); copies.setVisible(true)
        try { val whole = select(host, locations); val nested = select(host, locations, "Reports")
            var refused = false; try { copies.prepare(whole + nested) } catch (_: Exception) { refused = true }; assertTrue(refused); assertTrue(copies.state.value.items.isEmpty())
            refused = false; try { copies.prepare(nested + nested) } catch (_: Exception) { refused = true }; assertTrue(refused); assertTrue(copies.state.value.items.isEmpty())
        } finally { copies.shutdown() }
    } }
    @Test fun foregroundWithdrawalDefersDestinationAndDoesNotAutoResumeOldWork() = runBlocking { fixture { _, to, directory, locations, host ->
        val copies = PhoneLocalBatches(context, directory); copies.setVisible(true)
        try { val id = copies.prepare(select(host, locations, "Reports")); copies.setVisible(false); copies.acceptDestination(id, tree(to), to.getInt("flags"), PhoneCollision.KEEP_BOTH)
            delay(100); assertNull(copies.state.value.items.single().batch); copies.setVisible(true); waitFor { copies.state.value.items.single().batch?.phase == PhoneBatchPhase.COMPLETED }
        } finally { copies.shutdown() }
    } }
    @Test fun offlineBatchProjectsTheActiveProviderStepAndClearsItWhenTheChildFinishes() = runBlocking {
        fixture { from, to, directory, locations, host ->
            val copies = PhoneLocalBatches(context, directory)
            copies.setVisible(true)
            try {
                val expected = bytes(from, from.getString("file")!!)
                val id = copies.prepare(select(host, locations, "simu.txt"))
                context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "creationMode", to.getString("root"),
                    Bundle().apply { putString("mode", "blockWrite") })
                val work = copies.chooseDestination(id, tree(to), to.getInt("flags"), PhoneCollision.KEEP_BOTH)
                var document = ""
                waitFor(5_000) {
                    val item = copies.state.value.items.single().batch?.items?.singleOrNull()
                    document = item?.child?.let(copies::child)?.published.orEmpty()
                    document.isNotEmpty()
                }
                val provider = Docs.getDocumentId(Uri.parse(document))
                fun probe(release: Boolean = false) = context.contentResolver.call(TestDocumentsProvider.CONTROL_URI,
                    "openProbe", provider, Bundle().apply { putBoolean("release", release) })?.getBoolean("entered") == true
                try {
                    waitFor(5_000) { probe() && copies.state.value.finalizations.isNotEmpty() }
                    val view = copies.state.value
                    val active = view.items.single().batch!!.items.single().child
                    assertEquals(active, copies.activeChild(id)?.id)
                    val progress = view.finalizations.getValue(active)
                    assertEquals(PhoneIncomingFinalizationStage.PREPARING_PROVIDER_DOCUMENT, progress.stage)
                    assertNull(progress.estimate)
                } finally { probe(release = true) }
                withTimeout(5_000) { work.join() }
                assertEquals(PhoneBatchPhase.COMPLETED, finish(copies, id).phase)
                waitFor { copies.state.value.finalizations.isEmpty() }
                assertArrayEquals(expected, bytes(to, provider))
            } finally { copies.shutdown() }
        }
    }
    @Test fun lostParentHistoryCannotRestartTheSameSourceAndReferenceRemovalKeepsCopies() = runBlocking { fixture { _, to, directory, locations, host ->
        val first = PhoneLocalBatches(context, directory); first.setVisible(true); val id = first.prepare(select(host, locations, "Reports")); first.chooseDestination(id, tree(to), to.getInt("flags"), PhoneCollision.KEEP_BOTH).join(); val row = finish(first, id); first.shutdown()
        File(directory, "batches/$id.json").delete(); val copies = PhoneLocalBatches(context, directory); copies.setVisible(true)
        try { assertTrue(copies.state.value.items.single().started); var refused = false; try { copies.chooseDestination(id, tree(to), to.getInt("flags"), PhoneCollision.KEEP_BOTH) } catch (_: Exception) { refused = true }; assertTrue(refused)
            copies.removeMissing(id); assertTrue(copies.state.value.items.isEmpty()); assertTrue(children(to).containsKey(row.items.first().localName))
        } finally { copies.shutdown() }
    } }
    @Test fun corruptSourcesCanBePreservedWithoutDeletingOriginals() = runBlocking { fixture { from, _, directory, locations, host ->
        val first = PhoneLocalBatches(context, directory); first.setVisible(true); val id = first.prepare(select(host, locations, "Reports")); first.shutdown(); File(directory, "sources/$id.json").writeText("{broken generated source")
        val copies = PhoneLocalBatches(context, directory); copies.setVisible(true)
        try { val issue = copies.state.value.issues.single(); copies.preserve(issue.id); assertTrue(copies.state.value.issues.isEmpty()); assertEquals(1, copies.state.value.preserved)
            copies.clearPreserved(); assertEquals(0, copies.state.value.preserved); assertTrue(children(from).containsKey("Reports"))
        } finally { copies.shutdown() }
    } }
    @Test fun eightRetainedSourceContextsBoundAdmissionAndCancelReleasesUnusedAccess() = runBlocking { fixture { from, _, directory, locations, host ->
        val copies = PhoneLocalBatches(context, directory); copies.setVisible(true)
        try { val selected = select(host, locations, "Reports"); repeat(8) { copies.prepare(selected) }; var refused = false; try { copies.prepare(selected) } catch (_: Exception) { refused = true }; assertTrue(refused)
            copies.state.value.items.toList().forEach { copies.cancel(it.id) }; locations.all().filter { it.tree == tree(from) }.forEach { locations.remove(it.id) }
            assertFalse(context.contentResolver.persistedUriPermissions.any { it.uri == tree(from) })
        } finally { copies.shutdown() }
    } }
    @Test fun destinationInsideASelectedFolderIsRefusedBeforeSourceConsumption() = runBlocking { fixture { from, _, directory, locations, host ->
        val copies = PhoneLocalBatches(context, directory); copies.setVisible(true)
        try { val id = copies.prepare(select(host, locations)); var refused = false; try { copies.chooseDestination(id, tree(from), from.getInt("flags"), PhoneCollision.KEEP_BOTH) } catch (_: Exception) { refused = true }; assertTrue(refused)
            assertFalse(copies.state.value.items.single().started); assertNull(copies.state.value.items.single().batch)
        } finally { copies.shutdown() }
    } }
    @Test fun moreThan4096ItemsAreRefusedWithoutRetainedSourceReferences() = runBlocking { fixture(4096) { _, _, directory, locations, host ->
        val copies = PhoneLocalBatches(context, directory); copies.setVisible(true)
        try { var refused = false; try { copies.prepare(select(host, locations)) } catch (_: Exception) { refused = true }; assertTrue(refused); assertTrue(copies.state.value.items.isEmpty()) }
        finally { copies.shutdown() }
    } }
    @Test fun manySelectedFilesUsePagedRootsAndNeverCopyUnselectedSiblings() = runBlocking { fixture(40) { _, to, directory, locations, host ->
        val copies = PhoneLocalBatches(context, directory); copies.setVisible(true)
        try { val names = (0 until 40).map { "item-$it.txt" }; val id = copies.prepare(select(host, locations, *names.toTypedArray()))
            copies.chooseDestination(id, tree(to), to.getInt("flags"), PhoneCollision.KEEP_BOTH).join(); assertEquals(PhoneBatchPhase.COMPLETED, finish(copies, id, 150_000).phase)
            assertEquals(40, children(to).keys.count { it.startsWith("item-") }); assertEquals(1, children(to).keys.count { it.startsWith("simu") })
        } finally { copies.shutdown() }
    } }
    @Test fun aRealPartialCanPauseCloseRestartAndResumeVerifiedProviderBytes() = runBlocking { fixture { from, to, directory, locations, host ->
        val binary = children(from).getValue("kutuma.bin")
        context.contentResolver.openOutputStream(document(from, binary), "wt")!!.use { output ->
            val block = ByteArray(256 * 1024) { (it * 31 % 251).toByte() }; repeat(32) { output.write(block) }
        }
        val first = PhoneLocalBatches(context, directory); first.setVisible(true); val id = first.prepare(select(host, locations, "kutuma.bin"))
        val watcher = launch(Dispatchers.Default, start = CoroutineStart.UNDISPATCHED) { while (isActive) {
            val child = first.state.value.items.singleOrNull()?.batch?.items?.singleOrNull()?.child?.let(first::child)
            if (child != null && child.offset >= 1024 * 1024 && child.offset < child.size) { first.pause(id); break }
            delay(1)
        } }
        try {
            first.chooseDestination(id, tree(to), to.getInt("flags"), PhoneCollision.KEEP_BOTH).join(); val paused = finish(first, id)
            assertEquals(PhoneBatchPhase.PAUSED, paused.phase); val child = first.child(paused.items.single().child)!!
            assertTrue(child.offset >= 1024 * 1024 && child.offset < child.size); assertTrue(File(directory, "receiving/${child.id}.part").length() >= child.offset)
        }
        finally { watcher.cancelAndJoin(); first.shutdown() }
        val copies = PhoneLocalBatches(context, directory); copies.setVisible(true)
        try { assertFalse(copies.state.value.items.single().batch!!.active); copies.resume(id).join(); val row = finish(copies, id); assertEquals(row.error, PhoneBatchPhase.COMPLETED, row.phase)
            assertArrayEquals(bytes(from, children(from).getValue("kutuma.bin")), context.contentResolver.openInputStream(Uri.parse(row.items.single().published))!!.use { it.readBytes() })
        } finally { copies.shutdown() }
    } }
    @Test fun uncertainFolderCreationRequiresExplicitBranchSkipAndKeepsProviderObjects() = runBlocking { fixture { _, to, directory, locations, host ->
        context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "creationMode", to.getString("root"), Bundle().apply { putString("mode", "loseFolderReceipt") })
        val copies = PhoneLocalBatches(context, directory); copies.setVisible(true)
        try { val id = copies.prepare(select(host, locations, "Empty")); copies.chooseDestination(id, tree(to), to.getInt("flags"), PhoneCollision.KEEP_BOTH).join()
            assertEquals(PhoneBatchPhase.REVIEW, finish(copies, id).phase); val before = children(to)
            copies.skipUncertainFolder(id, 0); copies.resume(id).join(); assertEquals(PhoneBatchPhase.COMPLETED_WITH_ISSUES, finish(copies, id).phase); assertEquals(before, children(to))
        } finally { copies.shutdown() }
    } }
    @Test fun uncertainFileSaveCannotCreateAnotherCopyUntilExplicitReview() = runBlocking { fixture { _, to, directory, locations, host ->
        context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "creationMode", to.getString("root"), Bundle().apply { putString("mode", "loseReceipt") })
        val copies = PhoneLocalBatches(context, directory); copies.setVisible(true)
        try { val id = copies.prepare(select(host, locations, "simu.txt")); copies.chooseDestination(id, tree(to), to.getInt("flags"), PhoneCollision.KEEP_BOTH).join()
            val stopped = finish(copies, id); assertEquals(PhoneBatchPhase.REVIEW, stopped.phase); val before = children(to); val child = stopped.items.single().child
            copies.resume(id).join(); assertEquals(PhoneBatchPhase.REVIEW, finish(copies, id).phase); assertEquals(before, children(to))
            context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "creationMode", to.getString("root"), Bundle().apply { putString("mode", "normal") })
            copies.resume(id, child).join(); assertEquals(PhoneBatchPhase.COMPLETED, finish(copies, id).phase); assertEquals(before.size + 1, children(to).size); assertTrue(children(to).values.containsAll(before.values))
        } finally { copies.shutdown() }
    } }
    @Test fun recordedOrphanVerificationUsesOnlyTheExistingProviderCopy() = runBlocking { fixture { from, to, directory, locations, host ->
        val plan = LocalBatchSources.scan(context, select(host, locations, "simu.txt")) { true }; val node = plan.nodes.single(); val id = StorageRules.token()
        val entry = FileEntry.newBuilder().setId(node.id).setGrantId(node.source.grant).setName(node.source.name).setVersion(node.source.version).setSize(node.source.size).setPermissions(StorageRules.READ_ONLY).build()
        AndroidSafAccess(context).acquire("local-batch-orphan-fixture", tree(to), to.getInt("flags"), Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION)
        val files = AndroidDownloadManager(context, File(directory, "receiving"), localCopy = true, batchOnly = true)
        val child = try {
            val key = files.enqueue("", entry, tree(to), batchId = id)
            LocalBatchSources.read(context, plan, { true }) { request -> files.resumeBatchFile(key, "", id, request).join() }; files.publishBatchFile(key, false, id).join()
            assertEquals(PhoneDownloadPhase.COMPLETED, files.state.value.single().phase); key
        } finally { files.shutdown(); AndroidSafAccess(context).release("local-batch-orphan-fixture") }
        val record = File(directory, "receiving/$child.json"); val saved = JSONObject(record.readText()); saved.put("phase", PhoneDownloadPhase.PUBLICATION_INTERRUPTED.name); record.writeText(saved.toString())
        context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "change", from.getString("file"), Bundle().apply { putString("content", "changed source must not be read") })
        val copies = PhoneLocalBatches(context, directory); copies.setVisible(true)
        try { val before = children(to); copies.checkOrphan(child).join(); waitFor { !copies.state.value.orphans.single().active }
            assertEquals(PhoneDownloadPhase.COMPLETED, copies.state.value.orphans.single().phase); assertEquals(before, children(to)); copies.removeOrphan(child); assertTrue(copies.state.value.orphans.isEmpty())
        } finally { copies.shutdown() }
    } }
    @Test fun batchStoreRejectsAnActualSingleFileReceipt() = runBlocking { fixture { _, to, directory, locations, host ->
        val singleDirectory = File(context.filesDir, "local-single-purpose-${StorageRules.token()}"); val single = PhoneLocalCopies(context, singleDirectory); single.setVisible(true)
        try { val id = single.prepare(select(host, locations, "simu.txt").single()); single.chooseDestination(id, tree(to), to.getInt("flags"), PhoneCollision.KEEP_BOTH).join()
            assertEquals(PhoneDownloadPhase.COMPLETED, single.state.value.items.single().download!!.phase)
            File(directory, "receiving").mkdirs(); File(singleDirectory, "receiving/$id.json").copyTo(File(directory, "receiving/$id.json")); single.remove(id)
        } finally { single.shutdown(); check(singleDirectory.canonicalFile.parentFile == context.filesDir.canonicalFile); singleDirectory.deleteRecursively() }
        val copies = PhoneLocalBatches(context, directory); copies.setVisible(true)
        try { assertEquals(1, copies.state.value.issues.size); assertTrue(copies.state.value.orphans.isEmpty()); val before = children(to); copies.preserve(copies.state.value.issues.single().id); copies.clearPreserved(); assertEquals(before, children(to)) }
        finally { copies.shutdown() }
    } }
    @Test fun canceledPlanningCannotLeaveAPartialDraftOrReleaseBrowsingAccess() = runBlocking { fixture(1000) { from, _, directory, locations, host ->
        val selected = select(host, locations); val copies = PhoneLocalBatches(context, directory); copies.setVisible(true)
        try { val job = launch(start = CoroutineStart.UNDISPATCHED) { copies.prepare(selected) }; assertTrue(copies.state.value.planning); copies.cancelPlanning(); job.join()
            assertFalse(copies.state.value.planning); assertTrue(copies.state.value.items.isEmpty()); assertTrue(context.contentResolver.persistedUriPermissions.any { it.uri == tree(from) && it.isReadPermission })
        } finally { copies.shutdown() }
    } }
    @Test fun canceledWorkerCannotDispatchAfterForegroundReturns() = runBlocking { fixture { _, _, directory, _, _ ->
        val copies = PhoneLocalBatches(context, directory); copies.setVisible(true)
        try { val canceled = Job(); canceled.cancel(); copies.setVisible(false); copies.setVisible(true); var dispatched = false
            val method = PhoneLocalBatches::class.java.declaredMethods.single { it.name == "dispatch" && !it.isSynthetic }; method.isAccessible = true
            var refused = false; try { method.invoke(copies, canceled as kotlin.coroutines.CoroutineContext, { dispatched = true; Job() }) }
            catch (failure: java.lang.reflect.InvocationTargetException) { refused = failure.targetException is CancellationException }
            assertTrue(refused); assertFalse(dispatched)
        } finally { copies.shutdown() }
    } }
}
