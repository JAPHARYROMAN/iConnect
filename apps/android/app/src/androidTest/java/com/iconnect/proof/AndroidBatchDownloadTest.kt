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
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

@RunWith(AndroidJUnit4::class)
class AndroidBatchDownloadTest {
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext
    private val peer = "a".repeat(64)
    private val epoch = StorageRules.token()
    private class Source {
        val grant = StorageRules.token()
        val data = ByteArray(2 * 1024 * 1024 + 17) { (it * 31 % 251).toByte() }
        private val paths = listOf(emptyList(), listOf("Empty"), listOf("Reports"), listOf("Reports", "data-文書.bin"), listOf("z.txt"))
        private var ids = paths.associateWith { StorageRules.token() }
        private val tickets = mutableMapOf<String, List<String>>()
        val offsets = mutableListOf<Long>()
        var stopAt: Long? = null
        var changed = false
        val stopped = CompletableDeferred<Unit>()
        fun rebind() { ids = paths.associateWith { StorageRules.token() } }
        fun entry(path: List<String>): FileEntry = FileEntry.newBuilder().setId(ids.getValue(path)).setGrantId(grant)
            .setParentId(if (path.isEmpty()) "" else ids.getValue(path.dropLast(1))).setIsRoot(path.isEmpty())
            .setName(path.lastOrNull() ?: "Bundle").setIsDirectory(path.isEmpty() || path in listOf(listOf("Empty"), listOf("Reports")))
            .setSize(bytes(path).size.toLong()).setPermissions(StorageRules.READ_ONLY)
            .setVersion(if (changed && path.lastOrNull() == "data-文書.bin") "c".repeat(64) else "b".repeat(64)).build()
        private fun bytes(path: List<String>) = if (path.lastOrNull() == "data-文書.bin") data else if (path.lastOrNull() == "z.txt") "small document".toByteArray() else byteArrayOf()
        suspend fun request(request: FileRequest): FileResponse {
            val response = FileResponse.newBuilder().setRequestId(request.requestId)
            when (request.operation) {
                FileOperation.LIST_ROOTS -> response.addEntries(entry(emptyList()))
                FileOperation.GET_METADATA -> response.addEntries(entry(ids.entries.single { it.value == request.nodeId }.key))
                FileOperation.LIST_DIRECTORY -> {
                    val parent = ids.entries.single { it.value == request.nodeId }.key
                    paths.filter { it.isNotEmpty() && it.dropLast(1) == parent }.forEach { response.addEntries(entry(it)) }
                }
                FileOperation.PREPARE_DOWNLOAD, FileOperation.DOWNLOAD_STATUS -> {
                    val ticket = if (request.operation == FileOperation.PREPARE_DOWNLOAD) StorageRules.token().also { tickets[it] = ids.entries.single { pair -> pair.value == request.nodeId }.key } else request.transferId
                    val path = tickets.getValue(ticket); val entry = entry(path); val bytes = bytes(path)
                    response.download = DownloadInfo.newBuilder().setId(ticket).setName(entry.name).setVersion(entry.version).setSize(entry.size).setPreparedBytes(entry.size)
                        .setExpiresAt(System.currentTimeMillis() + 600_000).setState(DownloadState.DOWNLOAD_READY).setSha256(ByteString.copyFrom(Rules.digest(bytes))).build()
                }
                FileOperation.DOWNLOAD_READ -> {
                    if (request.offset == stopAt) { stopped.complete(Unit); awaitCancellation() }
                    val data = bytes(tickets.getValue(request.transferId)); offsets.add(request.offset)
                    val bytes = data.copyOfRange(request.offset.toInt(), minOf(data.size, request.offset.toInt() + request.length))
                    response.content = ByteString.copyFrom(bytes); response.endOfFile = request.offset + bytes.size == data.size.toLong()
                }
                FileOperation.RELEASE_DOWNLOAD -> Unit
                else -> error("Unexpected request")
            }
            return response.build()
        }
        suspend fun plan(children: Boolean = false): PhoneTransferManifest = PhoneTransferManifests.build(
            (if (children) listOf(listOf("Reports"), listOf("z.txt")) else listOf(emptyList())).map { PhoneTransferSelection(entry(it), it) }, ::request)
    }
    private suspend fun fixture(body: suspend (Context, File, Uri, Bundle, Source) -> Unit) {
        val row = requireNotNull(context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "fixture", null, null)); val tree = Uri.parse(row.getString("tree"))
        val prefix = "batch-test-${StorageRules.token()}-"; val directory = File(context.filesDir, prefix).apply { check(mkdir()) }
        val local = object : ContextWrapper(context) { override fun getFilesDir() = directory
            override fun getSharedPreferences(name: String, mode: Int) = context.getSharedPreferences(prefix + name, mode) }
        val access = AndroidSafAccess(local); access.acquire("fixture", tree, row.getInt("flags"))
        try { body(local, directory, tree, row, Source()) }
        finally {
            AndroidDownloadManager(local).use { files -> AndroidBatchDownloads(local, files).use { batches ->
                batches.state.value.forEach { batches.cancel(it.id); batches.remove(it.id) }
                files.state.value.forEach { if (it.phase !in setOf(PhoneDownloadPhase.COMPLETED, PhoneDownloadPhase.SKIPPED)) files.cancel(it.id); files.remove(it.id) }
            } }
            access.release("fixture"); context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "dispose", row.getString("root"), null)
            listOf("iconnect.storage", "iconnect.saf-access", "iconnect.proof").forEach { context.deleteSharedPreferences(prefix + it) }; directory.deleteRecursively()
        }
    }
    private fun uri(tree: Uri, id: String) = Docs.buildDocumentUriUsingTree(tree, id)
    private fun names(tree: Uri, parent: String = Docs.getTreeDocumentId(tree)): Map<String, String> = context.contentResolver.query(
        Docs.buildChildDocumentsUriUsingTree(tree, parent), arrayOf(Docs.Document.COLUMN_DOCUMENT_ID, Docs.Document.COLUMN_DISPLAY_NAME), null, null, null)!!.use {
        buildMap { while (it.moveToNext()) put(it.getString(0), it.getString(1)) }
    }
    private fun mode(root: String, value: String) { context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "creationMode", root, Bundle().apply { putString("mode", value) }) }

    @Test fun nestedEmptyDirectoriesAndVerifiedFilesKeepExistingContentAndFoldChildReceipts() = runBlocking {
        fixture { local, _, tree, _, source ->
            val existing = requireNotNull(Docs.createDocument(context.contentResolver, uri(tree, Docs.getTreeDocumentId(tree)), Docs.Document.MIME_TYPE_DIR, "Bundle"))
            val marker = requireNotNull(Docs.createDocument(context.contentResolver, existing, "text/plain", "owner.txt"))
            context.contentResolver.openOutputStream(marker)!!.use { it.write("owner data".toByteArray()) }
            AndroidDownloadManager(local).use { files -> AndroidBatchDownloads(local, files).use { batches ->
                val id = batches.enqueue(peer, epoch, source.plan(), tree, PhoneCollision.KEEP_BOTH); batches.resume(id, peer, epoch, source::request).join()
                val result = batches.state.value.single(); assertEquals(result.error, PhoneBatchPhase.COMPLETED, result.phase); assertEquals(5, result.completed)
                assertEquals("Bundle (1)", result.items[0].localName)
                val empty = result.items.single { it.source.name == "Empty" }; assertTrue(names(tree, empty.document).isEmpty())
                val file = result.items.single { it.source.name == "data-文書.bin" }; assertArrayEquals(source.data, context.contentResolver.openInputStream(Uri.parse(file.published))!!.use { it.readBytes() })
                assertEquals("owner data", context.contentResolver.openInputStream(marker)!!.bufferedReader().use { it.readText() })
                assertTrue(files.state.value.isEmpty()); assertTrue(batches.canRemove(id)); batches.remove(id)
            } }
        }
    }
    @Test fun skipAndFailCollisionsPreserveAnExistingFolderAndCopyIndependentSibling() = runBlocking {
        for (collision in listOf(PhoneCollision.SKIP, PhoneCollision.FAIL)) fixture { local, _, tree, row, source ->
            AndroidDownloadManager(local).use { files -> AndroidBatchDownloads(local, files).use { batches ->
                val id = batches.enqueue(peer, epoch, source.plan(children = true), tree, collision); batches.resume(id, peer, epoch, source::request).join()
                val result = batches.state.value.single(); assertEquals(result.error, PhoneBatchPhase.COMPLETED_WITH_ISSUES, result.phase)
                assertFalse(result.items.first().phase == PhoneBatchItemPhase.COMPLETED)
                assertEquals(mapOf(requireNotNull(row.getString("nested")) to "notes.txt"), names(tree, requireNotNull(row.getString("folder"))))
                assertEquals(PhoneBatchItemPhase.COMPLETED, result.items.last().phase); assertTrue(files.state.value.isEmpty())
            } }
        }
    }
    @Test fun restartResumesCheckpointAndRebindsAnUnstartedFileWithoutWideningOsAccess() = runBlocking {
        fixture { local, _, tree, _, source ->
            source.stopAt = 1024 * 1024; val id: String
            AndroidDownloadManager(local).use { files -> AndroidBatchDownloads(local, files).use { batches ->
                id = batches.enqueue(peer, epoch, source.plan(), tree, PhoneCollision.KEEP_BOTH); val work = batches.resume(id, peer, epoch, source::request)
                withTimeout(10_000) { source.stopped.await() }; batches.pause(id); work.join()
                assertEquals(PhoneBatchPhase.PAUSED, batches.state.value.single().phase); assertEquals(1024 * 1024L, files.state.value.single().offset)
            } }
            source.stopAt = null; source.rebind(); source.offsets.clear()
            AndroidDownloadManager(local).use { files -> AndroidBatchDownloads(local, files).use { batches ->
                batches.resume(id, peer, epoch, source::request).join(); val result = batches.state.value.single()
                assertEquals(result.error, PhoneBatchPhase.COMPLETED, result.phase); assertTrue(source.offsets.first() >= 1024 * 1024)
                assertEquals(2, result.items.count { !it.source.directory && it.hash.length == 64 }); assertTrue(files.state.value.isEmpty())
            } }
            assertTrue(context.contentResolver.persistedUriPermissions.any { it.uri == tree && it.isWritePermission })
        }
    }
    @Test fun movedDestinationAncestorAndChangedPairingAreRejectedBeforeAdditionalReads() = runBlocking {
        fixture { local, _, tree, _, source ->
            source.stopAt = 1024 * 1024
            AndroidDownloadManager(local).use { files -> AndroidBatchDownloads(local, files).use { batches ->
                val id = batches.enqueue(peer, epoch, source.plan(), tree, PhoneCollision.KEEP_BOTH); val work = batches.resume(id, peer, epoch, source::request)
                withTimeout(10_000) { source.stopped.await() }; batches.pause(id); work.join()
                val bundle = batches.state.value.single().items.first().document
                val other = requireNotNull(context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "fixture", null, null))
                try {
                    context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "change", bundle, Bundle().apply { putString("parent", other.getString("root")) })
                    source.stopAt = null; source.offsets.clear()
                    assertThrows(IllegalArgumentException::class.java) { batches.resume(id, peer, StorageRules.token(), source::request) }
                    batches.resume(id, peer, epoch, source::request).join()
                    assertEquals(PhoneBatchPhase.INTERRUPTED, batches.state.value.single().phase); assertTrue(source.offsets.isEmpty())
                    assertTrue(files.state.value.single().published.isEmpty())
                } finally { context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "dispose", other.getString("root"), null) }
            } }
        }
    }
    @Test fun lostFolderReceiptCannotBeAdoptedAndExplicitSkipAllowsOtherSelectedItems() = runBlocking {
        fixture { local, _, tree, row, source ->
            mode(requireNotNull(row.getString("root")), "loseFolderReceipt"); val id: String
            AndroidDownloadManager(local).use { files -> AndroidBatchDownloads(local, files).use { batches ->
                id = batches.enqueue(peer, epoch, source.plan(children = true), tree, PhoneCollision.KEEP_BOTH); batches.resume(id, peer, epoch, source::request).join()
                assertEquals(PhoneBatchPhase.REVIEW, batches.state.value.single().phase)
            } }
            mode(requireNotNull(row.getString("root")), ""); val before = names(tree)
            AndroidDownloadManager(local).use { files -> AndroidBatchDownloads(local, files).use { batches ->
                batches.resume(id, peer, epoch, source::request).join(); assertEquals(before, names(tree)); assertEquals(PhoneBatchPhase.REVIEW, batches.state.value.single().phase)
                batches.skipUncertainFolder(id, 0); batches.resume(id, peer, epoch, source::request).join()
                assertEquals(PhoneBatchPhase.COMPLETED_WITH_ISSUES, batches.state.value.single().phase)
                assertEquals(PhoneBatchItemPhase.COMPLETED, batches.state.value.single().items.last().phase)
            } }
        }
    }
    @Test fun lostFileReceiptNeedsExplicitAnotherCopyAndNeverRewritesTheUnknownCopy() = runBlocking {
        fixture { local, _, tree, row, source ->
            val manifest = PhoneTransferManifests.build(listOf(PhoneTransferSelection(source.entry(listOf("z.txt")), listOf("z.txt"))), source::request)
            mode(requireNotNull(row.getString("root")), "loseReceipt")
            AndroidDownloadManager(local).use { files -> AndroidBatchDownloads(local, files).use { batches ->
                val id = batches.enqueue(peer, epoch, manifest, tree, PhoneCollision.KEEP_BOTH); batches.resume(id, peer, epoch, source::request).join()
                assertEquals(PhoneBatchPhase.REVIEW, batches.state.value.single().phase); val child = files.state.value.single().id
                mode(requireNotNull(row.getString("root")), ""); val before = names(tree)
                batches.resume(id, peer, epoch, source::request).join(); assertEquals(before, names(tree)); assertEquals(PhoneBatchPhase.REVIEW, batches.state.value.single().phase)
                batches.resume(id, peer, epoch, source::request, child).join()
                assertEquals(PhoneBatchPhase.COMPLETED, batches.state.value.single().phase); assertTrue(names(tree).values.containsAll(listOf("z.txt", "z (1).txt")))
                assertTrue(files.state.value.isEmpty())
            } }
        }
    }
    @Test fun malformedManifestParentDuplicateAndRepeatedPageDoNotCreateDestinationObjects() = runBlocking {
        fixture { _, _, tree, _, source ->
            val before = names(tree)
            for (fault in listOf("parent", "duplicate", "page")) {
                var rejected = false
                try { PhoneTransferManifests.build(listOf(PhoneTransferSelection(source.entry(emptyList()), emptyList()))) { request ->
                    val response = source.request(request).toBuilder()
                    if (request.operation == FileOperation.LIST_DIRECTORY) when (fault) {
                        "parent" -> response.setEntries(0, response.getEntries(0).toBuilder().setParentId(StorageRules.token()))
                        "duplicate" -> response.setEntries(1, response.getEntries(1).toBuilder().setName(response.getEntries(0).name.uppercase()))
                        "page" -> response.clearEntries().setContinuation("x".repeat(43))
                    }
                    response.build()
                } } catch (_: Exception) { rejected = true }
                assertTrue("Malformed $fault must reject planning", rejected)
            }
            assertEquals(before, names(tree))
        }
    }
    @Test fun fileCollisionPoliciesNeverRewriteExistingDocumentsAndFailedPartialsCanBeCleaned() = runBlocking {
        for (collision in listOf(PhoneCollision.SKIP, PhoneCollision.FAIL)) fixture { local, _, tree, _, source ->
            val marker = requireNotNull(Docs.createDocument(context.contentResolver, uri(tree, Docs.getTreeDocumentId(tree)), "text/plain", "z.txt"))
            context.contentResolver.openOutputStream(marker)!!.use { it.write("existing owner data".toByteArray()) }
            val manifest = PhoneTransferManifests.build(listOf(PhoneTransferSelection(source.entry(listOf("z.txt")), listOf("z.txt"))), source::request)
            AndroidDownloadManager(local).use { files -> AndroidBatchDownloads(local, files).use { batches ->
                val id = batches.enqueue(peer, epoch, manifest, tree, collision); batches.resume(id, peer, epoch, source::request).join()
                val result = batches.state.value.single(); assertEquals(PhoneBatchPhase.COMPLETED_WITH_ISSUES, result.phase)
                assertEquals("existing owner data", context.contentResolver.openInputStream(marker)!!.bufferedReader().use { it.readText() })
                assertEquals(1, names(tree).values.count { it == "z.txt" })
                if (collision == PhoneCollision.FAIL) { assertFalse(batches.canRemove(id)); batches.cancel(id); assertTrue(files.state.value.isEmpty()) }
                assertTrue(batches.canRemove(id))
            } }
        }
    }
    @Test fun sharedResponseValidationRejectsMalformedDescriptorsInEveryDownloadState() {
        for (state in listOf(DownloadState.DOWNLOAD_READY, DownloadState.DOWNLOAD_PREPARING, DownloadState.DOWNLOAD_FAILED)) {
            val info = DownloadInfo.newBuilder().setId(StorageRules.token()).setName("safe.txt").setVersion("b".repeat(64)).setSize(1).setPreparedBytes(1)
                .setExpiresAt(System.currentTimeMillis() + 60_000).setState(state).setSha256(if (state == DownloadState.DOWNLOAD_READY) ByteString.copyFrom(ByteArray(32)) else ByteString.EMPTY).build()
            for (bad in listOf(info.toBuilder().setId("invalid").build(), info.toBuilder().setSize(StorageRules.MAX_TRANSFER_SIZE + 1).build(),
                info.toBuilder().setVersion("invalid").build(), info.toBuilder().setPreparedBytes(2).build(), info.toBuilder().setExpiresAt(-1).build())) {
                assertThrows(Exception::class.java) { PhoneTransferManifests.validateResponse(FileResponse.newBuilder().setRequestId(StorageRules.token()).setDownload(bad).build()) }
            }
        }
    }
}
