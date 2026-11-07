package com.iconnect.proof

import android.net.Uri
import android.os.Bundle
import android.provider.DocumentsContract as Docs
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.filters.LargeTest
import com.google.protobuf.ByteString
import com.iconnect.protocol.*
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.first
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.FileOutputStream
import java.io.IOException
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.MessageDigest

/** Generated provider files, native private checkpoints and independently verified provider output. */
@RunWith(AndroidJUnit4::class)
@LargeTest
class LargeFileTest {
    private val signedBoundary = 1L shl 31
    private val unsignedBoundary = 1L shl 32
    private val size = unsignedBoundary + 8L * StorageRules.RANGE + 17

    private inner class Fixture {
        val receiver = IncomingUploadsTest.Fixture()
        val context = receiver.context
        val sourceData = requireNotNull(context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "fixture", null, Bundle()))
        val sourceTree = Uri.parse(sourceData.getString("tree"))
        val sourceUri = Docs.buildDocumentUriUsingTree(sourceTree, sourceData.getString("file"))
        val locations = LocalDocumentLocations(context)
        val localHost: AndroidFileService
        val sourceGrant = receiver.grants.add(receiver.approval, sourceTree, sourceData.getInt("flags"), "Generated large source")
        val downloadSources = AndroidDownloadSources(context, receiver.grants)
        val remoteHost = AndroidFileService(context, receiver.grants, downloadSources)
        lateinit var entry: FileEntry
        lateinit var remoteEntry: FileEntry
        lateinit var hash: ByteArray

        init {
            assertTrue("Large-file verification needs room for source, snapshot, private receiving and provider publication.",
                context.filesDir.usableSpace > 4 * size + 1024L * 1024 * 1024)
            locations.add(sourceTree, sourceData.getInt("flags"))
            receiver.grants.update(sourceGrant.id, StorageRules.READ_ONLY)
            localHost = AndroidFileService(context, locations as DocumentAccessPolicy)
        }

        suspend fun initialize() = withContext(Dispatchers.IO) {
            val bytes = ByteArray(1024 * 1024).also { java.security.SecureRandom().nextBytes(it) }
            val digest = MessageDigest.getInstance("SHA-256")
            requireNotNull(context.contentResolver.openOutputStream(sourceUri, "wt")).use { output ->
                var offset = 0L
                while (offset < size) {
                    currentCoroutineContext().ensureActive()
                    ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN).putLong(offset)
                    val count = minOf(bytes.size.toLong(), size - offset).toInt()
                    output.write(bytes, 0, count); digest.update(bytes, 0, count); offset += count
                }
                output.flush()
                (output as FileOutputStream).fd.sync()
            }
            hash = digest.digest()
            val root = local(FileRequest.newBuilder().setOperation(FileOperation.LIST_ROOTS).build()).entriesList.single()
            entry = local(FileRequest.newBuilder().setOperation(FileOperation.LIST_DIRECTORY).setNodeId(root.id).build())
                .entriesList.single { it.name == "simu.txt" }
            assertEquals(size, entry.size)
            val remoteRoot = remote(FileRequest.newBuilder().setOperation(FileOperation.LIST_ROOTS).build()).entriesList.single { it.grantId == sourceGrant.id }
            remoteEntry = remote(FileRequest.newBuilder().setOperation(FileOperation.LIST_DIRECTORY).setNodeId(remoteRoot.id).build()).entriesList.single { it.name == entry.name }
            println("LARGE_FILE generated $size bytes SHA-256 ${Rules.hex(hash)}")
            receiver.initialize()
        }

        suspend fun local(request: FileRequest): FileResponse {
            val response = localHost.execute(locations.ownerId(), request.toBuilder().setRequestId(StorageRules.token()).build())
            check(response.status == FileStatus.FILE_STATUS_OK) { response.message }
            PhoneTransferManifests.validateResponse(response)
            return response
        }

        suspend fun remote(request: FileRequest): FileResponse {
            val response = remoteHost.execute(receiver.approval.id, request.toBuilder().setRequestId(StorageRules.token()).build())
            check(response.status == FileStatus.FILE_STATUS_OK) { response.message }
            PhoneTransferManifests.validateResponse(response)
            return response
        }

        suspend fun receive(request: FileRequest): FileResponse {
            val response = receiver.raw(request)
            check(response.status == FileStatus.FILE_STATUS_OK) { response.message }
            PhoneTransferManifests.validateResponse(response)
            return response
        }

        private fun digest(uri: Uri): Pair<Long, ByteArray> {
            val digest = MessageDigest.getInstance("SHA-256"); val bytes = ByteArray(1024 * 1024); var count = 0L
            requireNotNull(context.contentResolver.openInputStream(uri)).use { input ->
                while (true) {
                    val read = input.read(bytes); if (read < 0) break
                    digest.update(bytes, 0, read); count += read
                }
            }
            return count to digest.digest()
        }

        fun verify(published: Uri, name: String) {
            assertEquals(1, receiver.children().values.count { it == name })
            listOf(sourceUri, published).forEach { uri ->
                val actual = digest(uri); assertEquals(size, actual.first); assertArrayEquals(hash, actual.second)
            }
            println("LARGE_FILE verified original and provider publication $size bytes")
        }

        suspend fun close() {
            remoteHost.close(); downloadSources.shutdown()
            localHost.close()
            locations.all().filter { it.tree == sourceTree }.forEach { locations.remove(it.id) }
            context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "dispose", sourceData.getString("root"), null)
            receiver.close()
        }
    }

    private suspend fun settled(manager: PhoneUploads, id: String): PhoneUpload =
        manager.state.first { rows -> rows.any { it.id == id && !it.active } }.single { it.id == id }

    @Test fun uploadAndIncomingProviderOver4GiBReopenTheOriginalReceipts(): Unit = runBlocking {
        withTimeout(45 * 60 * 1000L) {
            val fixture = Fixture()
            val managers = mutableListOf<PhoneUploads>()
            try {
                fixture.initialize()
                val receiving = fixture.receiver
                val identity = LocalIdentity()
                val peer = PeerApproval(identity.id, Rules.b64(identity.publicKey), "Fixture receiver", StorageRules.token())
                val allowed = { receiving.policy.isCurrent(receiving.approval) }
                fun manager() = PhoneUploads(fixture.context, receiving.approval.id).also { managers.add(it) }
                val first = manager()
                val id = first.stage(peer, LocalUploadSource(fixture.entry, { true }, fixture::local), allowed)
                val prepared = settled(first, id)
                assertEquals(prepared.message, PhoneUploadPhase.READY, prepared.phase)
                assertEquals(size, prepared.size); assertArrayEquals(fixture.hash, Rules.decode(prepared.hash))
                val target = receiving.call(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_CONTEXT).build()).entriesList.single()
                var lost = false
                first.send(id, peer, target, allowed) { request ->
                    val response = fixture.receive(request)
                    if (request.operation == FileOperation.UPLOAD_WRITE && request.offset == unsignedBoundary) {
                        lost = true; throw IOException("Lost accepted range beyond 4 GiB.")
                    }
                    response
                }
                val paused = settled(first, id)
                assertTrue(paused.message, lost); assertEquals(PhoneUploadPhase.REVIEW, paused.phase)
                assertEquals(unsignedBoundary, paused.offset)
                assertEquals(unsignedBoundary + StorageRules.RANGE, receiving.incoming.state.value.single().offset)
                println("LARGE_FILE sender checkpoint ${paused.offset}, receiver checkpoint ${unsignedBoundary + StorageRules.RANGE}")
                first.shutdown()
                receiving.restart(selectTarget = false, reopenGrants = true)
                assertEquals(unsignedBoundary + StorageRules.RANGE, receiving.incoming.state.value.single().offset)
                val reopened = manager()
                assertEquals(unsignedBoundary, reopened.state.value.single().offset)
                reopened.send(id, peer, null, allowed, fixture::receive)
                val completed = settled(reopened, id)
                assertEquals(completed.message, PhoneUploadPhase.COMPLETED, completed.phase)
                assertEquals(size, completed.offset); assertEquals(paused.remote, completed.remote)
                val receipt = receiving.incoming.state.value.single()
                assertEquals(UploadPhase.UPLOAD_COMPLETED, receipt.phase)
                val document = receiving.children().entries.single { it.value == completed.finalName }.key
                fixture.verify(Docs.buildDocumentUriUsingTree(receiving.tree, document), completed.finalName)
            } finally {
                withContext(NonCancellable) {
                    managers.forEach { it.shutdown() }
                    fixture.close()
                }
            }
        }
    }

    @Test fun downloadOver4GiBReopensItsCheckpointBeforeExplicitProviderSaving(): Unit = runBlocking {
        withTimeout(45 * 60 * 1000L) {
            val fixture = Fixture()
            val managers = mutableListOf<AndroidDownloadManager>()
            try {
                fixture.initialize()
                fun manager() = AndroidDownloadManager(fixture.context).also { managers.add(it) }
                val first = manager()
                val id = first.enqueue(fixture.receiver.approval.id, fixture.remoteEntry, fixture.receiver.tree)
                val entered = CompletableDeferred<Unit>()
                val transfer = first.resume(id, fixture.receiver.approval.id) { request ->
                    val response = fixture.remote(request)
                    if (request.operation == FileOperation.DOWNLOAD_READ && request.offset >= unsignedBoundary) {
                        entered.complete(Unit); awaitCancellation()
                    }
                    response
                }
                while (!entered.isCompleted && transfer.isActive) delay(25)
                assertTrue(first.state.value.single().error, entered.isCompleted)
                first.pause(id); transfer.join()
                val paused = first.state.value.single()
                assertEquals(PhoneDownloadPhase.PAUSED, paused.phase); assertEquals(unsignedBoundary, paused.offset)
                println("LARGE_FILE download checkpoint ${paused.offset}")
                first.shutdown()
                val reopened = manager()
                assertEquals(unsignedBoundary, reopened.state.value.single().offset)
                reopened.resume(id, fixture.receiver.approval.id, fixture::remote).join()
                val received = reopened.state.value.single()
                assertEquals(received.error, PhoneDownloadPhase.READY_TO_SAVE, received.phase)
                assertEquals(size, received.offset); assertEquals(paused.ticket, received.ticket)
                reopened.publish(id).join()
                val published = reopened.state.value.single()
                assertEquals(published.error, PhoneDownloadPhase.COMPLETED, published.phase)
                fixture.verify(Uri.parse(published.published), published.publishedName)
                reopened.remove(id)
            } finally {
                withContext(NonCancellable) {
                    managers.forEach { it.shutdown() }
                    fixture.close()
                }
            }
        }
    }

    @Test fun offlineProviderCopyOver4GiBKeepsItsObserved64BitCheckpoint(): Unit = runBlocking {
        withTimeout(45 * 60 * 1000L) {
            val fixture = Fixture()
            val managers = mutableListOf<PhoneLocalCopies>()
            try {
                fixture.initialize()
                fun manager() = PhoneLocalCopies(fixture.context).also { managers.add(it); it.setVisible(true) }
                val first = manager()
                val selected = fixture.localHost.captureLocalFile(fixture.locations.ownerId(), fixture.entry)
                val id = first.prepare(selected)
                val observer = launch(Dispatchers.Default, start = CoroutineStart.UNDISPATCHED) {
                    first.state.collect { state ->
                        if ((state.liveBytes[id] ?: 0) >= signedBoundary) first.pause(id)
                    }
                }
                try {
                    first.chooseDestination(id, fixture.receiver.tree, fixture.receiver.data.getInt("flags"), PhoneCollision.KEEP_BOTH).join()
                } finally { observer.cancelAndJoin() }
                val paused = first.state.value.items.single().download!!
                assertEquals(paused.error, PhoneDownloadPhase.PAUSED, paused.phase)
                assertTrue(paused.offset in signedBoundary..size)
                println("LARGE_FILE offline checkpoint ${paused.offset}")
                first.shutdown()
                val reopened = manager()
                assertEquals(paused.offset, reopened.state.value.items.single().download!!.offset)
                reopened.resume(id).join()
                val completed = reopened.state.value.items.single().download!!
                assertEquals(completed.error, PhoneDownloadPhase.COMPLETED, completed.phase)
                assertEquals(size, completed.offset); assertEquals("", completed.peer)
                fixture.verify(Uri.parse(completed.published), completed.publishedName)
                reopened.remove(id)
            } finally {
                withContext(NonCancellable) {
                    managers.forEach { it.shutdown() }
                    fixture.close()
                }
            }
        }
    }
}
