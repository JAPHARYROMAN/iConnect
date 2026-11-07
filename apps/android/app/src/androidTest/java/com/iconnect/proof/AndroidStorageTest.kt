package com.iconnect.proof

import android.content.Intent
import android.net.Uri
import android.os.Bundle
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.iconnect.protocol.*
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.security.MessageDigest

@RunWith(AndroidJUnit4::class)
class AndroidStorageTest {
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext
    private val peerKey = TestPeer.key()
    private val peer = Rules.id(peerKey.public.encoded)
    private class Fixture(val bundle: Bundle) {
        val tree: Uri = Uri.parse(bundle.getString("tree"))
        val flags = bundle.getInt("flags")
    }
    private fun fixture(extra: Int = 0) = Fixture(requireNotNull(context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "fixture", null,
        Bundle().apply { putInt("extraFiles", extra) })))
    private fun change(id: String, extras: Bundle) { context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "change", id, extras) }
    private fun request(operation: FileOperation, node: String = "", version: String = "") = FileRequest.newBuilder()
        .setRequestId(StorageRules.token()).setOperation(operation).setNodeId(node).setExpectedVersion(version)
    private fun withStorage(extra: Int = 0, body: suspend (Fixture, AndroidStorageGrants, AndroidFileService, AndroidStorageGrant) -> Unit) = runBlocking {
        val fixture = fixture(extra)
        val store = AndroidStorageGrants(context)
        val approval = TestPeer.approve(store.owner, peerKey)
        val grant = store.add(approval, fixture.tree, fixture.flags, "Phone documents")
        val service = AndroidFileService(context, store)
        try { body(fixture, store, service, grant) }
        finally {
            service.close(); store.revokePeer(peer)
            context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "dispose", fixture.bundle.getString("root"), null)
        }
    }
    private suspend fun roots(service: AndroidFileService) = service.execute(peer, request(FileOperation.LIST_ROOTS).build())
    private suspend fun listing(service: AndroidFileService, node: FileEntry) = service.execute(peer, request(FileOperation.LIST_DIRECTORY, node.id).build())
    private suspend fun file(service: AndroidFileService): FileEntry = listing(service, roots(service).entriesList.single()).entriesList.single { it.name == "simu.txt" }

    @Test fun grantsArePersistedPerPeerAndNoPathsCrossTheProtocol() = withStorage { fixture, _, service, grant ->
        assertTrue(context.contentResolver.persistedUriPermissions.any { it.uri == fixture.tree && it.isReadPermission })
        assertEquals(grant, AndroidStorageGrants(context).get(grant.id, peer))
        assertEquals(0, service.execute("f".repeat(64), request(FileOperation.LIST_ROOTS).build()).entriesCount)
        val root = roots(service).entriesList.single()
        assertTrue(root.isRoot && Rules.tokenPattern.matches(root.id) && root.permissions == StorageRules.READ_ONLY)
        val response = listing(service, root)
        assertFalse(response.entriesList.any { it.name == ".ssh" })
        assertFalse(response.toString().contains(fixture.bundle.getString("root")!!))
        val entry = response.entriesList.single { it.name == "simu.txt" }
        val read = service.execute(peer, request(FileOperation.READ_RANGE, entry.id, entry.version).setLength(32768).build())
        assertEquals(FileStatus.FILE_STATUS_OK, read.status)
        assertEquals("iConnect approved phone document.\nSalama!\n", read.content.toStringUtf8())
        assertTrue(read.endOfFile)
    }
    @Test fun livePermissionsExclusionsExpiryAndRevocationApplyToIssuedReferences() = withStorage { _, store, service, grant ->
        val entry = file(service)
        val read = request(FileOperation.READ_RANGE, entry.id, entry.version).setLength(10).build()
        store.update(grant.id, StorageRules.LIST)
        assertEquals(FileStatus.ACCESS_DENIED, service.execute(peer, read).status)
        store.update(grant.id, StorageRules.READ_ONLY, setOf("simu.txt"))
        assertEquals(FileStatus.ACCESS_DENIED, service.execute(peer, read).status)
        store.update(grant.id, StorageRules.READ_ONLY, expiresAt = System.currentTimeMillis() - 1)
        assertEquals(0, roots(service).entriesCount)
        store.remove(grant.id)
        assertEquals(FileStatus.ACCESS_DENIED, service.execute(peer, read).status)
    }
    @Test fun staleVersionsAndUnboundedRangesAreRejected() = withStorage { fixture, _, service, _ ->
        val entry = file(service)
        assertEquals(FileStatus.INVALID_REQUEST, service.execute(peer,
            request(FileOperation.READ_RANGE, entry.id, entry.version).setLength(32769).build()).status)
        assertEquals(FileStatus.INVALID_REQUEST, service.execute(peer,
            request(FileOperation.READ_RANGE, entry.id, entry.version).setLength(1).setOffset(-1).build()).status)
        change(fixture.bundle.getString("file")!!, Bundle().apply { putString("content", "changed") })
        assertEquals(FileStatus.CONFLICT, service.execute(peer,
            request(FileOperation.READ_RANGE, entry.id, entry.version).setLength(1).build()).status)
    }
    @Test fun providerMovesAndRenamedExcludedAncestorsCannotEscapePolicy() = withStorage { fixture, _, service, _ ->
        val root = roots(service).entriesList.single()
        val folder = listing(service, root).entriesList.single { it.name == "Reports" }
        val nested = listing(service, folder).entriesList.single()
        change(fixture.bundle.getString("folder")!!, Bundle().apply { putString("name", ".ssh") })
        assertEquals(FileStatus.ACCESS_DENIED, service.execute(peer,
            request(FileOperation.READ_RANGE, nested.id, nested.version).setLength(2).build()).status)
        val entry = file(service)
        change(fixture.bundle.getString("file")!!, Bundle().apply { putString("parent", "outside") })
        assertEquals(FileStatus.ACCESS_DENIED, service.execute(peer,
            request(FileOperation.READ_RANGE, entry.id, entry.version).setLength(1).build()).status)
    }
    private fun rangeMutation(mode: String, expected: FileStatus) {
        listOf(false, true).forEach { ticketed ->
            withStorage { fixture, store, _, _ ->
                val sources = AndroidDownloadSources(context, store)
                val reader = AndroidFileService(context, store, sources)
                try {
                    val entry = file(reader)
                    val read = if (ticketed) {
                        val prepared = reader.execute(peer, request(FileOperation.PREPARE_DOWNLOAD, entry.id, entry.version).build())
                        assertEquals(prepared.message, FileStatus.FILE_STATUS_OK, prepared.status)
                        kotlinx.coroutines.withTimeout(15_000) {
                            while (true) {
                                val status = reader.execute(peer, request(FileOperation.DOWNLOAD_STATUS).setTransferId(prepared.download.id).build())
                                assertEquals(status.message, FileStatus.FILE_STATUS_OK, status.status)
                                if (status.download.state == DownloadState.DOWNLOAD_READY) break
                                kotlinx.coroutines.delay(10)
                            }
                        }
                        request(FileOperation.DOWNLOAD_READ).setTransferId(prepared.download.id).setLength(8).build()
                    } else request(FileOperation.READ_RANGE, entry.id, entry.version).setLength(8).build()
                    context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "creationMode", fixture.bundle.getString("root"),
                        Bundle().apply { putString("mode", mode) })
                    val result = reader.execute(peer, read)
                    assertEquals("$mode, ticketed=$ticketed: ${result.message}", expected, result.status)
                    assertTrue(result.content.isEmpty); assertEquals(0, result.entriesCount)
                } finally { reader.close(); sources.shutdown() }
            }
        }
    }
    @Test fun rangesWithholdBytesWhenTheProviderChangesVersionDuringOpen() =
        rangeMutation("changeVersionOnRead", FileStatus.CONFLICT)
    @Test fun rangesWithholdBytesWhenTheProviderMovesTheSourceDuringOpen() =
        rangeMutation("moveOutsideOnRead", FileStatus.ACCESS_DENIED)
    @Test fun rangesWithholdBytesWhenAnAncestorBecomesExcludedDuringOpen() =
        rangeMutation("excludeAncestorOnRead", FileStatus.ACCESS_DENIED)

    @Test fun createRequiresIndependentPermissionAndProviderSupport() = withStorage { _, store, service, grant ->
        val root = roots(service).entriesList.single()
        val epoch = service.execute(peer, request(FileOperation.MUTATION_CONTEXT).build()).mutationEpoch
        val create = request(FileOperation.CREATE_DIRECTORY).setDestinationId(root.id).setName("New folder").setMutationId(MutationRules.newId()).setMutationEpoch(epoch).build()
        assertEquals(FileStatus.ACCESS_DENIED, service.execute(peer, create).status)
        store.update(grant.id, StorageRules.READ_ONLY or StorageRules.CREATE)
        val result = service.execute(peer, create)
        assertEquals(result.message, FileStatus.FILE_STATUS_OK, result.status)
        assertEquals("New folder", result.entriesList.single().name)
        assertEquals(FileStatus.INVALID_REQUEST, service.execute(peer, create.toBuilder().setMutationId(MutationRules.newId()).setName("../outside").build()).status)
        assertEquals(FileStatus.ACCESS_DENIED, service.execute(peer, create.toBuilder().setMutationId(MutationRules.newId()).setName(".ssh").build()).status)
    }
    private suspend fun createRequest(service: AndroidFileService, name: String): FileRequest {
        val epoch = service.execute(peer, request(FileOperation.MUTATION_CONTEXT).build()).mutationEpoch
        return request(FileOperation.CREATE_DIRECTORY).setDestinationId(roots(service).entriesList.single().id).setName(name)
            .setMutationId(MutationRules.newId()).setMutationEpoch(epoch).build()
    }
    @Test fun createReceiptSurvivesSessionRestartAndCannotBeRepurposed() = withStorage { _, store, service, grant ->
        store.update(grant.id, StorageRules.READ_ONLY or StorageRules.CREATE)
        val create = createRequest(service, "Created once")
        assertEquals(MutationPhase.CHANGE_SUCCEEDED, service.execute(peer, create).mutation.phase)
        val restarted = AndroidFileService(context, AndroidStorageGrants(context))
        try {
            val replay = restarted.execute(peer, create.toBuilder().setRequestId(StorageRules.token()).build())
            assertEquals(FileStatus.FILE_STATUS_OK, replay.status)
            assertEquals(create.mutationId, replay.mutation.id)
            assertEquals(1, replay.entriesCount)
            assertEquals(1, listing(restarted, roots(restarted).entriesList.single()).entriesList.count { it.name == "Created once" })
            assertEquals(FileStatus.CONFLICT, restarted.execute(peer, create.toBuilder().setName("Different").build()).status)
            val query = request(FileOperation.MUTATION_STATUS).setMutationId(create.mutationId).setMutationEpoch(create.mutationEpoch).build()
            assertEquals(FileStatus.NOT_FOUND, restarted.execute("f".repeat(64), query).status)
            store.remove(grant.id)
            assertEquals(FileStatus.ACCESS_DENIED, restarted.execute(peer, query).status)
        } finally { restarted.close() }
    }
    @Test fun lostProviderFolderReceiptIsReviewedAndNeverReplayed() = withStorage { fixture, store, service, grant ->
        store.update(grant.id, StorageRules.READ_ONLY or StorageRules.CREATE)
        context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "creationMode", fixture.bundle.getString("root"), Bundle().apply { putString("mode", "loseFolderReceipt") })
        val create = createRequest(service, "Uncertain folder")
        val uncertain = service.execute(peer, create)
        assertEquals(FileStatus.CONFLICT, uncertain.status)
        assertEquals(MutationPhase.CHANGE_REVIEW, uncertain.mutation.phase)
        assertEquals(1, listing(service, roots(service).entriesList.single()).entriesList.count { it.name == "Uncertain folder" })
        context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "creationMode", fixture.bundle.getString("root"), Bundle().apply { putString("mode", "normal") })
        assertEquals(MutationPhase.CHANGE_REVIEW, service.execute(peer, create).mutation.phase)
        assertEquals(1, listing(service, roots(service).entriesList.single()).entriesList.count { it.name == "Uncertain folder" })
    }
    @Test fun providerReturningAFileForFolderCreationIsNotReportedAsSuccess() = withStorage { fixture, store, service, grant ->
        store.update(grant.id, StorageRules.READ_ONLY or StorageRules.CREATE)
        context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "creationMode", fixture.bundle.getString("root"), Bundle().apply { putString("mode", "fileForFolder") })
        val reply = service.execute(peer, createRequest(service, "Wrong type"))
        assertEquals(MutationPhase.CHANGE_REVIEW, reply.mutation.phase); assertEquals(0, reply.entriesCount)
    }
    @Test fun providerCreatingFolderUnderAnotherApprovedParentRequiresReview() = withStorage { fixture, store, service, grant ->
        store.update(grant.id, StorageRules.READ_ONLY or StorageRules.CREATE)
        context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "creationMode", fixture.bundle.getString("root"), Bundle().apply { putString("mode", "misplacedFolder") })
        val reply = service.execute(peer, createRequest(service, "Wrong parent"))
        assertEquals(MutationPhase.CHANGE_REVIEW, reply.mutation.phase); assertEquals(0, reply.entriesCount)
    }
    @Test fun pendingPrivateReceiptAfterRestartDoesNotAdoptAnExistingFolder() = withStorage { _, store, service, grant ->
        store.update(grant.id, StorageRules.READ_ONLY or StorageRules.CREATE)
        val create = createRequest(service, "Receipt interrupted")
        assertEquals(FileStatus.FILE_STATUS_OK, service.execute(peer, create).status)
        val journal = java.io.File(context.filesDir, "file-operations/journal.json")
        val json = org.json.JSONObject(journal.readText()); val rows = json.getJSONArray("records")
        (0 until rows.length()).map { rows.getJSONObject(it) }.single { it.getString("id") == create.mutationId }
            .put("phase", MutationPhase.CHANGE_PENDING.number).put("result", "").put("created", "")
        journal.writeText(json.toString())
        val restarted = AndroidFileService(context, AndroidStorageGrants(context))
        try {
            assertEquals(MutationPhase.CHANGE_REVIEW, restarted.execute(peer, create).mutation.phase)
            assertEquals(1, listing(restarted, roots(restarted).entriesList.single()).entriesList.count { it.name == "Receipt interrupted" })
        } finally { restarted.close() }
    }
    @Test fun failedIntentCommitMakesNoFolderAndHistoryResetRejectsOldContext() = withStorage { _, store, service, grant ->
        store.update(grant.id, StorageRules.READ_ONLY or StorageRules.CREATE)
        val create = createRequest(service, "Saved intent first")
        val journal = java.io.File(context.filesDir, "file-operations/journal.json")
        val blocker = java.io.File(journal.path + ".new"); assertTrue(blocker.mkdir())
        val marker = java.io.File(blocker, "owned-test-blocker").also { it.writeText("Keep this directory in place during the fault test.") }
        try {
            assertEquals(FileStatus.IO_ERROR, service.execute(peer, create).status)
            assertFalse(listing(service, roots(service).entriesList.single()).entriesList.any { it.name == "Saved intent first" })
        } finally { assertTrue(marker.delete()); assertTrue(blocker.delete()) }
        assertEquals(FileStatus.FILE_STATUS_OK, service.execute(peer, create).status)
        val original = journal.readBytes()
        try {
            journal.writeText("damaged")
            assertEquals(FileStatus.CONFLICT, service.execute(peer, request(FileOperation.MUTATION_CONTEXT).build()).status)
            store.mutations.archiveRecovery(store.mutations.recoveryIssues().single().id)
            assertEquals(FileStatus.STALE_REFERENCE, service.execute(peer, create).status)
            assertEquals(1, listing(service, roots(service).entriesList.single()).entriesList.count { it.name == "Saved intent first" })
        } finally { journal.writeBytes(original) }
    }

    @Test fun realFrameworkPermissionRevocationDeniesReads() = withStorage { fixture, _, service, _ ->
        val entry = file(service)
        context.contentResolver.releasePersistableUriPermission(fixture.tree,
            Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION)
        // Revoke also the provider's temporary grant, not just its persisted part.
        context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "revoke", fixture.tree.toString(), null)
        assertEquals(FileStatus.ACCESS_DENIED, service.execute(peer,
            request(FileOperation.READ_RANGE, entry.id, entry.version).setLength(1).build()).status)
    }
    @Test fun directoryPagingStaysBoundedAndReturnsEachAccessibleItem() = withStorage(95) { _, _, service, _ ->
        val root = roots(service).entriesList.single()
        var continuation = ""
        val names = mutableListOf<String>()
        do {
            val result = service.execute(peer, request(FileOperation.LIST_DIRECTORY, root.id).setContinuation(continuation).build())
            assertEquals(FileStatus.FILE_STATUS_OK, result.status)
            assertTrue(result.entriesCount <= 32)
            names.addAll(result.entriesList.map { it.name })
            continuation = result.continuation
        } while (continuation.isNotEmpty())
        assertEquals(97, names.size)
        assertEquals(names.size, names.distinct().size)
    }
    @Test fun sourceDownloadTicketsArePersistentPeerBoundAndRecheckReadPermission() = withStorage { _, store, _, grant ->
        val sources = AndroidDownloadSources(context, store)
        val source = AndroidFileService(context, store, sources)
        try {
            val entry = file(source)
            val prepared = source.execute(peer, request(FileOperation.PREPARE_DOWNLOAD, entry.id, entry.version).build())
            assertEquals(FileStatus.FILE_STATUS_OK, prepared.status)
            val ticket = prepared.download.id
            var status = prepared
            repeat(100) {
                if (status.download.state == DownloadState.DOWNLOAD_READY) return@repeat
                kotlinx.coroutines.delay(10)
                status = source.execute(peer, request(FileOperation.DOWNLOAD_STATUS).setTransferId(ticket).build())
            }
            assertEquals(DownloadState.DOWNLOAD_READY, status.download.state)
            val expected = "iConnect approved phone document.\nSalama!\n".toByteArray()
            assertArrayEquals(MessageDigest.getInstance("SHA-256").digest(expected), status.download.sha256.toByteArray())
            val read = source.execute(peer, request(FileOperation.DOWNLOAD_READ).setTransferId(ticket).setLength(32768).build())
            assertArrayEquals(expected, read.content.toByteArray())
            assertTrue(read.endOfFile)
            assertEquals(FileStatus.ACCESS_DENIED, source.execute("f".repeat(64), request(FileOperation.DOWNLOAD_STATUS).setTransferId(ticket).build()).status)
            store.update(grant.id, StorageRules.LIST)
            assertEquals(FileStatus.ACCESS_DENIED, source.execute(peer, request(FileOperation.DOWNLOAD_READ).setTransferId(ticket).setLength(1).build()).status)
            store.update(grant.id, StorageRules.READ_ONLY)
            sources.close()
            val reopened = AndroidDownloadSources(context, store)
            val reader = AndroidFileService(context, store, reopened)
            try {
                assertEquals(DownloadState.DOWNLOAD_READY, reader.execute(peer, request(FileOperation.DOWNLOAD_STATUS).setTransferId(ticket).build()).download.state)
                reader.execute(peer, request(FileOperation.RELEASE_DOWNLOAD).setTransferId(ticket).build())
            } finally { reader.close(); reopened.close() }
        } finally { source.close(); sources.close() }
    }
    @Test fun releasingOneTicketDoesNotRevokeAnotherDownloadOfTheSameFile() = withStorage { _, store, _, _ ->
        val sources = AndroidDownloadSources(context, store); val reader = AndroidFileService(context, store, sources)
        try {
            val entry = file(reader)
            val prepare = request(FileOperation.PREPARE_DOWNLOAD, entry.id, entry.version).build()
            val first = reader.execute(peer, prepare).download.id; val second = reader.execute(peer, prepare).download.id
            assertNotEquals(first, second)
            var status = reader.execute(peer, request(FileOperation.DOWNLOAD_STATUS).setTransferId(second).build())
            repeat(100) { if (status.download.state != DownloadState.DOWNLOAD_READY) {
                kotlinx.coroutines.delay(10); status = reader.execute(peer, request(FileOperation.DOWNLOAD_STATUS).setTransferId(second).build())
            } }
            assertEquals(DownloadState.DOWNLOAD_READY, status.download.state)
            reader.execute(peer, request(FileOperation.RELEASE_DOWNLOAD).setTransferId(first).build())
            val read = reader.execute(peer, request(FileOperation.DOWNLOAD_READ).setTransferId(second).setLength(32).build())
            assertEquals(FileStatus.FILE_STATUS_OK, read.status); assertEquals(32, read.content.size())
            reader.execute(peer, request(FileOperation.RELEASE_DOWNLOAD).setTransferId(second).build())
        } finally { reader.close(); sources.close() }
    }
    @Test fun inactiveAvailabilityDeniesExistingReferencesWithoutWideningOrRemovingTheGrant() = withStorage { _, store, _, _ ->
        val available = java.util.concurrent.atomic.AtomicBoolean(true)
        val reader = AndroidFileService(context, store, available = available::get)
        try {
            val entry = file(reader)
            available.set(false); reader.suspendAvailability()
            val denied = reader.execute(peer, request(FileOperation.READ_RANGE, entry.id, entry.version).setLength(17).build())
            assertEquals(FileStatus.ACCESS_DENIED, denied.status); assertTrue(denied.content.isEmpty)
            assertEquals(FileStatus.ACCESS_DENIED, reader.execute(peer, request(FileOperation.LIST_ROOTS).build()).status)
            available.set(true)
            assertEquals(FileStatus.FILE_STATUS_OK, reader.execute(peer, request(FileOperation.READ_RANGE, entry.id, entry.version).setLength(17).build()).status)
        } finally { reader.close() }
    }
    @Test fun queuedSourcePreparationCanRestartAfterAvailabilityEndsBeforeItExecutes() = withStorage { _, store, _, _ ->
        val queued = java.util.concurrent.ConcurrentLinkedQueue<Runnable>()
        val dispatcher = object : kotlinx.coroutines.CoroutineDispatcher() {
            override fun dispatch(context: kotlin.coroutines.CoroutineContext, block: Runnable) { queued.add(block) }
        }
        fun drain() { while (true) (queued.poll() ?: break).run() }
        val sources = AndroidDownloadSources(context, store, dispatcher); val reader = AndroidFileService(context, store, sources)
        try {
            val entry = file(reader)
            val ticket = reader.execute(peer, request(FileOperation.PREPARE_DOWNLOAD, entry.id, entry.version).build()).download.id
            reader.suspendAvailability(); drain()
            reader.execute(peer, request(FileOperation.DOWNLOAD_STATUS).setTransferId(ticket).build()); drain()
            assertEquals(DownloadState.DOWNLOAD_READY, reader.execute(peer, request(FileOperation.DOWNLOAD_STATUS).setTransferId(ticket).build()).download.state)
            reader.execute(peer, request(FileOperation.RELEASE_DOWNLOAD).setTransferId(ticket).build())
        } finally { reader.close(); sources.close(); drain() }
    }
    @Test fun backgroundSourcePolicyAllowsOnlyTheSelectedLiveTicket() = withStorage { _, store, _, _ ->
        val sources = AndroidDownloadSources(context, store)
        var foreground = true; var selected = ""
        val reader = AndroidFileService(context, store, sources, requestAllowed = { request -> foreground ||
            request.transferId == selected && request.operation in setOf(FileOperation.DOWNLOAD_STATUS, FileOperation.DOWNLOAD_READ) })
        try {
            val entry = file(reader)
            val first = reader.execute(peer, request(FileOperation.PREPARE_DOWNLOAD, entry.id, entry.version).build()).download.id
            val second = reader.execute(peer, request(FileOperation.PREPARE_DOWNLOAD, entry.id, entry.version).build()).download.id
            var status = reader.execute(peer, request(FileOperation.DOWNLOAD_STATUS).setTransferId(first).build())
            repeat(100) { if (status.download.state != DownloadState.DOWNLOAD_READY) { kotlinx.coroutines.delay(10)
                status = reader.execute(peer, request(FileOperation.DOWNLOAD_STATUS).setTransferId(first).build()) } }
            assertEquals(DownloadState.DOWNLOAD_READY, status.download.state)
            val scope = sources.state.value.single { it.id == first }.scope
            selected = first; foreground = false; reader.suspendAvailability(setOf(first))
            assertTrue(sources.isLive(peer, first, scope)); assertFalse(sources.state.value.single { it.id == second }.active)
            assertEquals(FileStatus.FILE_STATUS_OK, reader.execute(peer, request(FileOperation.DOWNLOAD_READ).setTransferId(first).setLength(17).build()).status)
            assertEquals(FileStatus.ACCESS_DENIED, reader.execute(peer, request(FileOperation.DOWNLOAD_READ).setTransferId(second).setLength(1).build()).status)
            assertEquals(FileStatus.ACCESS_DENIED, reader.execute(peer, request(FileOperation.READ_RANGE, entry.id, entry.version).setLength(1).build()).status)
            assertEquals(FileStatus.ACCESS_DENIED, reader.execute(peer, request(FileOperation.LIST_ROOTS).build()).status)
            assertEquals(FileStatus.ACCESS_DENIED, reader.execute(peer, request(FileOperation.PREPARE_DOWNLOAD, entry.id, entry.version).build()).status)
            assertEquals(FileStatus.ACCESS_DENIED, reader.execute("f".repeat(64), request(FileOperation.DOWNLOAD_READ).setTransferId(first).setLength(1).build()).status)
            assertFalse(sources.isLive(peer, first, "another-session"))
        } finally { reader.close(); sources.state.value.filter { it.peer == peer }.forEach { sources.release(peer, it.id) }; sources.shutdown() }
    }
    @Test fun sendingPausePersistsAndResumeCannotOverrideANewerPause() = withStorage { _, store, _, _ ->
        var sources = AndroidDownloadSources(context, store); var reader = AndroidFileService(context, store, sources)
        try {
            val entry = file(reader)
            val id = reader.execute(peer, request(FileOperation.PREPARE_DOWNLOAD, entry.id, entry.version).build()).download.id
            val revision = sources.block(id); sources.persistPause(id, revision)
            assertEquals(FileStatus.ACCESS_DENIED, reader.execute(peer, request(FileOperation.DOWNLOAD_STATUS).setTransferId(id).build()).status)
            reader.close(); sources.shutdown()
            sources = AndroidDownloadSources(context, store); reader = AndroidFileService(context, store, sources)
            assertTrue(sources.state.value.single { it.id == id }.paused)
            assertFalse(sources.canUse(peer, id))
            val resume = sources.reserveResume(id); val newerPause = sources.block(id)
            var rejected = false
            try { sources.resume(id, peer, resume) } catch (_: IllegalStateException) { rejected = true }
            assertTrue(rejected); sources.persistPause(id, newerPause)
            sources.resume(id, peer, sources.reserveResume(id))
            assertFalse(sources.state.value.single { it.id == id }.paused)
            assertEquals(FileStatus.FILE_STATUS_OK, reader.execute(peer, request(FileOperation.DOWNLOAD_STATUS).setTransferId(id).build()).status)
            reader.execute(peer, request(FileOperation.RELEASE_DOWNLOAD).setTransferId(id).build())
        } finally { reader.close(); sources.shutdown() }
    }
    @Test fun statusPollingAndRepeatedRangesDoNotRenewIdleBackgroundAuthority() = withStorage { _, store, _, _ ->
        val clock = java.util.concurrent.atomic.AtomicLong(1_000)
        val sources = AndroidDownloadSources(context, store, elapsed = clock::get)
        var restricted = false; var boundScope = ""
        val reader = AndroidFileService(context, store, sources, requestAllowed = { value -> !restricted ||
            sources.isLive(peer, value.transferId, boundScope) && value.operation in setOf(FileOperation.DOWNLOAD_STATUS, FileOperation.DOWNLOAD_READ) })
        try {
            val entry = file(reader)
            val id = reader.execute(peer, request(FileOperation.PREPARE_DOWNLOAD, entry.id, entry.version).build()).download.id
            var status = reader.execute(peer, request(FileOperation.DOWNLOAD_STATUS).setTransferId(id).build())
            repeat(100) { if (status.download.state != DownloadState.DOWNLOAD_READY) { kotlinx.coroutines.delay(10)
                status = reader.execute(peer, request(FileOperation.DOWNLOAD_STATUS).setTransferId(id).build()) } }
            assertEquals(DownloadState.DOWNLOAD_READY, status.download.state)
            val scope = sources.state.value.single { it.id == id }.scope
            reader.execute(peer, request(FileOperation.DOWNLOAD_READ).setTransferId(id).setLength(1).build())
            boundScope = scope; restricted = true
            clock.addAndGet(AndroidDownloadSources.IDLE_MS - 1)
            assertTrue(sources.isLive(peer, id, scope))
            reader.execute(peer, request(FileOperation.DOWNLOAD_STATUS).setTransferId(id).build())
            reader.execute(peer, request(FileOperation.DOWNLOAD_READ).setTransferId(id).setLength(1).build())
            clock.addAndGet(2)
            assertFalse(sources.isLive(peer, id, scope)); sources.refresh()
            assertFalse(sources.state.value.single { it.id == id }.active)
            assertEquals(FileStatus.ACCESS_DENIED, reader.execute(peer, request(FileOperation.DOWNLOAD_STATUS).setTransferId(id).build()).status)
            restricted = false
            reader.execute(peer, request(FileOperation.RELEASE_DOWNLOAD).setTransferId(id).build())
        } finally { reader.close(); sources.shutdown() }
    }
    @Test fun oldPeerCleanupCannotRemoveANewerSendingTicket() = withStorage { _, store, _, _ ->
        val sources = AndroidDownloadSources(context, store); val reader = AndroidFileService(context, store, sources)
        try {
            val entry = file(reader); val prepare = request(FileOperation.PREPARE_DOWNLOAD, entry.id, entry.version).build()
            val old = reader.execute(peer, prepare).download.id
            val staleResume = sources.reserveResume(old)
            val captured = sources.blockPeer(peer); assertFalse(sources.canUse(peer, old))
            var rejected = false
            try { sources.resume(old, peer, staleResume) } catch (_: IllegalStateException) { rejected = true }
            assertTrue(rejected)
            val fresh = reader.execute(peer, prepare).download.id
            sources.releasePeer(peer, captured)
            assertFalse(sources.canUse(peer, old)); assertTrue(sources.canUse(peer, fresh))
            reader.execute(peer, request(FileOperation.RELEASE_DOWNLOAD).setTransferId(fresh).build())
        } finally { reader.close(); sources.shutdown() }
    }
}
