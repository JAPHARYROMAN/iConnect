package com.iconnect.proof

import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.provider.DocumentsContract as Documents
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.google.protobuf.ByteString
import com.iconnect.protocol.*
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.first
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.io.RandomAccessFile
import java.security.MessageDigest

@RunWith(AndroidJUnit4::class)
class AndroidDownloadTest {
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext
    private val peer = "a".repeat(64)
    private class Source(val bytes: ByteArray) {
        val ticket = StorageRules.token()
        val entry = FileEntry.newBuilder().setId(StorageRules.token()).setGrantId(StorageRules.token()).setName("simu.txt")
            .setVersion("b".repeat(64)).setPermissions(StorageRules.READ_ONLY).setSize(bytes.size.toLong()).build()
        val hash = MessageDigest.getInstance("SHA-256").digest(bytes)
        val offsets = mutableListOf<Long>()
        var corrupt = false
        var changed = false
        var stopAt: Long? = null
        val stopped = CompletableDeferred<Unit>()
        suspend fun request(request: FileRequest): FileResponse {
            val response = FileResponse.newBuilder().setRequestId(request.requestId)
            when (request.operation) {
                FileOperation.PREPARE_DOWNLOAD, FileOperation.DOWNLOAD_STATUS -> response.download = DownloadInfo.newBuilder()
                    .setId(ticket).setName(entry.name).setVersion(if (changed) "c".repeat(64) else entry.version).setSize(entry.size)
                    .setPreparedBytes(entry.size).setExpiresAt(System.currentTimeMillis() + 600_000).setState(DownloadState.DOWNLOAD_READY)
                    .setSha256(ByteString.copyFrom(hash)).build()
                FileOperation.DOWNLOAD_READ -> {
                    if (request.offset == stopAt) { stopped.complete(Unit); awaitCancellation() }
                    offsets.add(request.offset)
                    val bytes = bytes.copyOfRange(request.offset.toInt(), minOf(bytes.size, request.offset.toInt() + request.length))
                    if (corrupt && bytes.isNotEmpty()) bytes[0] = (bytes[0].toInt() xor 1).toByte()
                    response.content = ByteString.copyFrom(bytes); response.endOfFile = request.offset + bytes.size == entry.size
                }
                FileOperation.RELEASE_DOWNLOAD -> Unit
                else -> error("Unexpected operation")
            }
            return response.build()
        }
    }
    private suspend fun fixture(body: suspend (Bundle, Uri, File) -> Unit) {
        val fixture = requireNotNull(context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "fixture", null, null))
        val tree = Uri.parse(fixture.getString("tree"))
        val access = AndroidSafAccess(context); val owner = "test:${StorageRules.token()}"
        access.acquire(owner, tree, fixture.getInt("flags"))
        val directory = File(context.filesDir, "download-test-${StorageRules.token()}"); check(directory.mkdirs())
        try { body(fixture, tree, directory) }
        finally {
            AndroidDownloadManager(context, directory).use { manager ->
                manager.state.value.forEach { row ->
                    if (row.phase !in setOf(PhoneDownloadPhase.COMPLETED, PhoneDownloadPhase.CANCELED)) manager.cancel(row.id)
                    manager.remove(row.id)
                }
            }
            access.release(owner)
            context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "dispose", fixture.getString("root"), null)
            // This directory was created above under the test app's private files directory.
            directory.deleteRecursively()
        }
    }
    private fun bytes(size: Int = 2 * 1024 * 1024 + 17) = ByteArray(size) { (it * 31 % 251).toByte() }
    private fun original(tree: Uri, fixture: Bundle) = Documents.buildDocumentUriUsingTree(tree, fixture.getString("file"))
    private fun content(uri: Uri) = context.contentResolver.openInputStream(uri)!!.use { it.readBytes() }
    private fun row(directory: File, id: String, action: (JSONObject) -> Unit) {
        val file = File(directory, "$id.json"); val json = JSONObject(file.readText()); action(json); file.writeText(json.toString())
    }
    private fun creationMode(fixture: Bundle, mode: String) { context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "creationMode",
        fixture.getString("root"), Bundle().apply { putString("mode", mode) }) }
    private fun childNames(tree: Uri): List<String> = context.contentResolver.query(
        Documents.buildChildDocumentsUriUsingTree(tree, Documents.getTreeDocumentId(tree)), arrayOf(Documents.Document.COLUMN_DISPLAY_NAME), null, null, null)!!.use {
        buildList { while (it.moveToNext()) add(it.getString(0)) }
    }
    @Test fun savingReportsProviderWaitAndRejectsGrowthBeyondTheVerifiedLength() = runBlocking {
        fixture { fixture, tree, directory ->
            val source = Source(bytes(2 * StorageRules.RANGE + 1))
            AndroidDownloadManager(context, directory).use { manager ->
                val id = manager.enqueue(peer, source.entry, tree)
                manager.resume(id, peer, source::request).join()
                assertEquals(PhoneDownloadPhase.READY_TO_SAVE, manager.state.value.single().phase)
                assertTrue(manager.readFinalizations().isEmpty())
                creationMode(fixture, "blockWrite")
                val saving = manager.publish(id)
                val document = withTimeout(5_000) {
                    manager.state.first { it.single().published.isNotEmpty() }.single().published
                }
                val providerId = Documents.getDocumentId(Uri.parse(document))
                fun probe(release: Boolean = false) = requireNotNull(context.contentResolver.call(TestDocumentsProvider.CONTROL_URI,
                    "openProbe", providerId, Bundle().apply { putBoolean("release", release) })).getBoolean("entered")
                try {
                    withTimeout(5_000) { while (!probe()) delay(10) }
                    val progress = manager.readFinalizations().getValue(id)
                    assertEquals(PhoneIncomingFinalizationStage.PREPARING_PROVIDER_DOCUMENT, progress.stage)
                    assertNull(progress.estimate)
                    RandomAccessFile(File(directory, "$id.part"), "rw").use {
                        it.seek(it.length())
                        it.write(0)
                    }
                } finally { probe(release = true) }
                withTimeout(5_000) { saving.join() }
                val result = manager.state.value.single()
                assertEquals(PhoneDownloadPhase.PUBLICATION_INTERRUPTED, result.phase)
                assertTrue(result.error.contains("grew during saving"))
                assertTrue(manager.readFinalizations().isEmpty())
                assertArrayEquals(source.bytes.copyOf(2 * StorageRules.RANGE), content(Uri.parse(document)))
                manager.publish(id).join()
                assertEquals(PhoneDownloadPhase.PUBLICATION_INTERRUPTED, manager.state.value.single().phase)
                assertArrayEquals(source.bytes.copyOf(2 * StorageRules.RANGE), content(Uri.parse(document)))
            }
        }
    }
    @Test fun actualBytesAreVerifiedAndPublishedAsANewFileWithoutOverwritingCollision() = runBlocking {
        fixture { fixture, tree, directory ->
            val source = Source(bytes())
            val id: String
            AndroidDownloadManager(context, directory).use { manager ->
                id = manager.enqueue(peer, source.entry, tree)
                manager.resume(id, peer, source::request).join()
                assertEquals(PhoneDownloadPhase.READY_TO_SAVE, manager.state.value.single().phase)
                manager.publish(id).join()
                val result = manager.state.value.single()
                assertEquals(result.error, PhoneDownloadPhase.COMPLETED, result.phase)
                assertEquals("simu (1).txt", result.publishedName)
                assertArrayEquals(source.bytes, content(Uri.parse(result.published)))
                assertEquals("iConnect approved phone document.\nSalama!\n", content(original(tree, fixture)).toString(Charsets.UTF_8))
                assertFalse(File(directory, "$id.part").exists())
            }
            AndroidDownloadManager(context, directory).use { manager -> assertEquals(PhoneDownloadPhase.COMPLETED, manager.state.value.single().phase); manager.remove(id) }
        }
    }
    @Test fun interruptedDownloadRestartsAtVerifiedCheckpointAndRejectsAnotherPeer() = runBlocking {
        fixture { _, tree, directory ->
            val source = Source(bytes()); source.stopAt = 1024L * 1024
            val id: String
            AndroidDownloadManager(context, directory).use { manager ->
                id = manager.enqueue(peer, source.entry, tree)
                val job = manager.resume(id, peer, source::request)
                withTimeout(10_000) { source.stopped.await() }; manager.pause(id); job.join()
                assertEquals(1024L * 1024, manager.state.value.single().offset)
            }
            source.stopAt = null; source.offsets.clear()
            AndroidDownloadManager(context, directory).use { manager ->
                manager.resume(id, "d".repeat(64), source::request).join()
                assertTrue(source.offsets.isEmpty())
                manager.resume(id, peer, source::request).join()
                assertEquals(1024L * 1024, source.offsets.first())
                assertEquals(PhoneDownloadPhase.READY_TO_SAVE, manager.state.value.single().phase)
                assertArrayEquals(source.bytes, File(directory, "$id.part").readBytes())
            }
        }
    }
    @Test fun damagedPartialAndChangedSourceCannotBePromotedToSavedFiles() = runBlocking {
        fixture { _, tree, directory ->
            val source = Source(bytes()); source.stopAt = 1024L * 1024
            val id: String
            AndroidDownloadManager(context, directory).use { manager ->
                id = manager.enqueue(peer, source.entry, tree)
                val job = manager.resume(id, peer, source::request)
                withTimeout(10_000) { source.stopped.await() }; manager.pause(id); job.join()
            }
            source.stopAt = null; source.offsets.clear()
            RandomAccessFile(File(directory, "$id.part"), "rw").use { it.seek(42); it.writeByte(255) }
            AndroidDownloadManager(context, directory).use { manager ->
                manager.resume(id, peer, source::request).join()
                assertEquals(PhoneDownloadPhase.INTERRUPTED, manager.state.value.single().phase)
                assertTrue(source.offsets.isEmpty()); assertTrue(manager.state.value.single().published.isEmpty())
                manager.cancel(id); assertFalse(File(directory, "$id.part").exists()); manager.remove(id)
                val next = manager.enqueue(peer, source.entry, tree); source.changed = true
                manager.resume(next, peer, source::request).join()
                assertEquals(PhoneDownloadPhase.INTERRUPTED, manager.state.value.single().phase)
                assertTrue(source.offsets.isEmpty())
            }
        }
    }
    @Test fun corruptNetworkBytesAreNeverPublished() = runBlocking {
        fixture { _, tree, directory ->
            val source = Source(bytes(65_537)); source.corrupt = true
            AndroidDownloadManager(context, directory).use { manager ->
                val id = manager.enqueue(peer, source.entry, tree); manager.resume(id, peer, source::request).join()
                assertEquals(PhoneDownloadPhase.INTERRUPTED, manager.state.value.single().phase)
                assertTrue(manager.state.value.single().published.isEmpty())
                manager.cancel(id)
            }
        }
    }
    @Test fun publicationReceiptCanBeReconciledOfflineButChangedCopyIsLeftUntouched() = runBlocking {
        fixture { _, tree, directory ->
            val source = Source(bytes(65_537)); val id: String; val published: Uri
            AndroidDownloadManager(context, directory).use { manager ->
                id = manager.enqueue(peer, source.entry, tree); manager.resume(id, peer, source::request).join(); manager.publish(id).join()
                published = Uri.parse(manager.state.value.single().published)
            }
            // Simulate loss of the completion receipt after the provider closed the exact bytes.
            row(directory, id) { it.put("phase", PhoneDownloadPhase.PUBLISHING.name) }
            AndroidDownloadManager(context, directory).use { manager ->
                assertEquals(PhoneDownloadPhase.PUBLICATION_INTERRUPTED, manager.state.value.single().phase)
                manager.publish(id).join(); assertEquals(PhoneDownloadPhase.COMPLETED, manager.state.value.single().phase)
            }
            context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "change", Documents.getDocumentId(published), Bundle().apply { putString("content", "owner changed this file") })
            row(directory, id) { it.put("phase", PhoneDownloadPhase.PUBLISHING.name) }
            AndroidDownloadManager(context, directory).use { manager ->
                manager.publish(id).join()
                assertEquals(PhoneDownloadPhase.PUBLICATION_INTERRUPTED, manager.state.value.single().phase)
                assertEquals("owner changed this file", content(published).toString(Charsets.UTF_8))
                manager.cancel(id)
                assertEquals("owner changed this file", content(published).toString(Charsets.UTF_8))
            }
        }
    }
    @Test fun revokedAndroidDestinationAccessDoesNotTouchExistingFiles() = runBlocking {
        fixture { fixture, tree, directory ->
            val source = Source(bytes(17))
            AndroidDownloadManager(context, directory).use { manager ->
                val id = manager.enqueue(peer, source.entry, tree); manager.resume(id, peer, source::request).join()
                context.contentResolver.releasePersistableUriPermission(tree, Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION)
                manager.publish(id).join()
                assertEquals(PhoneDownloadPhase.READY_TO_SAVE, manager.state.value.single().phase)
                assertTrue(manager.state.value.single().published.isEmpty())
                assertEquals("iConnect approved phone document.\nSalama!\n", content(original(tree, fixture)).toString(Charsets.UTF_8))
            }
        }
    }
    @Test fun substitutedExistingProviderDocumentIsNeverOpenedForWriting() = runBlocking {
        fixture { fixture, tree, directory ->
            val source = Source(bytes(17))
            AndroidDownloadManager(context, directory).use { manager ->
                val id = manager.enqueue(peer, source.entry, tree); manager.resume(id, peer, source::request).join()
                creationMode(fixture, "existing"); manager.publish(id).join()
                assertEquals(PhoneDownloadPhase.PUBLICATION_INTERRUPTED, manager.state.value.single().phase)
                assertEquals("iConnect approved phone document.\nSalama!\n", content(original(tree, fixture)).toString(Charsets.UTF_8))
            }
        }
    }
    @Test fun lostCreateReceiptRequiresExplicitNewCopyDecision() = runBlocking {
        fixture { fixture, tree, directory ->
            val source = Source(bytes(17)); val id: String
            AndroidDownloadManager(context, directory).use { manager ->
                id = manager.enqueue(peer, source.entry, tree); manager.resume(id, peer, source::request).join()
                creationMode(fixture, "loseReceipt"); manager.publish(id).join()
                assertEquals(PhoneDownloadPhase.PUBLICATION_INTERRUPTED, manager.state.value.single().phase)
                assertTrue(manager.state.value.single().published.isEmpty())
            }
            creationMode(fixture, "")
            val existing = childNames(tree)
            AndroidDownloadManager(context, directory).use { manager ->
                manager.publish(id).join(); assertEquals(existing, childNames(tree))
                manager.publish(id, anotherCopy = true).join()
                val result = manager.state.value.single()
                assertEquals(result.error, PhoneDownloadPhase.COMPLETED, result.phase)
                assertEquals("simu (2).txt", result.publishedName)
                assertArrayEquals(source.bytes, content(Uri.parse(result.published)))
                assertTrue(childNames(tree).contains("simu (1).txt"))
            }
        }
    }
    @Test fun emptyUnicodeFileAndOfflineStagingVerificationDoNotNeedTheSourceAgain() = runBlocking {
        fixture { _, tree, directory ->
            val source = Source(byteArrayOf()); val entry = source.entry.toBuilder().setName("Salama-✓.txt").build()
            // Match the descriptor name while retaining the standard test source responder.
            val requester: suspend (FileRequest) -> FileResponse = { request -> source.request(request).let { response ->
                if (response.hasDownload()) response.toBuilder().setDownload(response.download.toBuilder().setName(entry.name)).build() else response } }
            val id: String
            AndroidDownloadManager(context, directory).use { manager ->
                id = manager.enqueue(peer, entry, tree); manager.resume(id, peer, requester).join()
                assertEquals(PhoneDownloadPhase.READY_TO_SAVE, manager.state.value.single().phase)
            }
            row(directory, id) { it.put("phase", PhoneDownloadPhase.DOWNLOADING.name) }
            AndroidDownloadManager(context, directory).use { manager ->
                manager.resume(id, peer) { error("No network request is allowed after all bytes and the checksum are durable.") }.join()
                assertEquals(PhoneDownloadPhase.READY_TO_SAVE, manager.state.value.single().phase)
                manager.publish(id).join()
                assertEquals(PhoneDownloadPhase.COMPLETED, manager.state.value.single().phase)
                assertEquals(0, content(Uri.parse(manager.state.value.single().published)).size)
            }
        }
    }
    @Test fun canceledQueuedJobReleasesItsSlotAndCanBeResumed() = runBlocking {
        fixture { _, tree, directory ->
            val source = Source(bytes(17)); val entered = java.util.concurrent.atomic.AtomicInteger()
            val unblock = CompletableDeferred<Unit>()
            val blocked: suspend (FileRequest) -> FileResponse = { request ->
                if (request.operation == FileOperation.DOWNLOAD_READ) { entered.incrementAndGet(); unblock.await() }
                source.request(request)
            }
            AndroidDownloadManager(context, directory).use { manager ->
                val first = manager.enqueue(peer, source.entry, tree); val second = manager.enqueue(peer, source.entry, tree)
                val firstJob = manager.resume(first, peer, blocked); val secondJob = manager.resume(second, peer, blocked)
                withTimeout(10_000) { while (entered.get() < 2) delay(10) }
                val queued = manager.enqueue(peer, source.entry, tree)
                val waiting = manager.resume(queued, peer, source::request); manager.pause(queued); waiting.join()
                assertFalse(manager.state.value.single { it.id == queued }.active)
                unblock.complete(Unit); firstJob.join(); secondJob.join()
                manager.resume(queued, peer, source::request).join()
                assertEquals(PhoneDownloadPhase.READY_TO_SAVE, manager.state.value.single { it.id == queued }.phase)
            }
        }
    }
}
