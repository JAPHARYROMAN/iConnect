package com.iconnect.proof

import android.net.Uri
import android.os.Bundle
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.iconnect.protocol.*
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class PhoneExplorerTest {
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext
    private suspend fun fixture(body: suspend (String, FileEntry, AndroidFileService, MutableList<FileOperation>) -> Unit) {
        val fixture = requireNotNull(context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "fixture", null, Bundle().apply { putInt("extraFiles", 110) }))
        val store = AndroidStorageGrants(context); val key = TestPeer.key(); val approval = TestPeer.approve(store.owner, key)
        store.add(approval, Uri.parse(fixture.getString("tree")), fixture.getInt("flags"), "Phone explorer fixture")
        val service = AndroidFileService(context, store); val operations = mutableListOf<FileOperation>()
        try {
            val root = service.execute(approval.id, FileRequest.newBuilder().setRequestId(StorageRules.token()).setOperation(FileOperation.LIST_ROOTS).build()).entriesList.single()
            body(approval.id, root, service, operations)
        } finally { service.close(); store.revokePeer(approval.id); context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "dispose", fixture.getString("root"), null) }
    }
    private suspend fun rpc(service: AndroidFileService, peer: String, value: FileRequest, operations: MutableList<FileOperation>): FileResponse {
        operations.add(value.operation); return service.execute(peer, value.toBuilder().setRequestId(StorageRules.token()).build())
    }
    @Test fun fullProviderDirectorySortsAcrossManyTransportPagesWithStableCursorTokens() = runBlocking {
        fixture { peer, root, service, operations ->
            val result = PhoneExplorerCatalog.load(listOf(root)) { rpc(service, peer, it, operations) }
            assertTrue(result.notice, result.complete); assertEquals(112, result.rows.size)
            assertTrue(operations.count { it == FileOperation.LIST_DIRECTORY } >= 4)
            val sorted = PhoneExplorerCatalog.sort(result.rows, PhoneExplorerSort.NAME, false)
            assertEquals("Reports", sorted.first().entry.name); assertEquals(50, sorted.take(PhoneExplorerCatalog.PAGE).size)
            val sizes = PhoneExplorerCatalog.sort(result.rows, PhoneExplorerSort.SIZE, true).filter { !it.entry.isDirectory }.map { it.entry.size }
            assertEquals(sizes.sortedDescending(), sizes)
            assertTrue(result.rows.all { it.route.first().isRoot && it.entry.grantId == root.grantId })
        }
    }
    @Test fun scopedSearchKeepsParentRoutesAndDoesNotReadFileContentOrExcludedFolders() = runBlocking {
        fixture { peer, root, service, operations ->
            val result = PhoneExplorerCatalog.search(listOf(root), "NOTES") { rpc(service, peer, it, operations) }
            assertTrue(result.notice, result.complete); val row = result.rows.single()
            assertEquals("Reports / notes.txt", row.relativePath)
            val selectedFolder = PhoneExplorerRow(row.route[row.route.size - 2], row.route.dropLast(1))
            assertEquals(listOf(selectedFolder), PhoneExplorerCatalog.normalizeSelection(listOf(row, selectedFolder)))
            assertFalse(operations.any { it in setOf(FileOperation.READ_RANGE, FileOperation.PREPARE_DOWNLOAD, FileOperation.DOWNLOAD_READ) })
            val excluded = PhoneExplorerCatalog.search(listOf(root), "ssh") { rpc(service, peer, it, operations) }
            assertTrue(excluded.complete); assertTrue(excluded.rows.isEmpty())
            val nested = PhoneExplorerCatalog.load(row.route.dropLast(1)) { rpc(service, peer, it, operations) }; assertEquals(1, nested.rows.size)
        }
    }
    @Test fun manifestDiscoveryAlsoAcceptsManyPagesWithTheSameStatefulCursorToken() = runBlocking {
        fixture { peer, root, service, operations ->
            val result = PhoneTransferManifests.build(listOf(PhoneTransferSelection(root, emptyList()))) { rpc(service, peer, it, operations) }
            assertEquals(114, result.items.size); assertEquals(112, result.files)
        }
    }
    @Test fun canceledDiscoveryClosesItsCursorAndWrongPeerCannotCloseAnActiveListing() = runBlocking {
        fixture { peer, root, service, operations ->
            val first = rpc(service, peer, FileRequest.newBuilder().setOperation(FileOperation.LIST_DIRECTORY).setNodeId(root.id).build(), operations)
            val close = FileRequest.newBuilder().setOperation(FileOperation.CLOSE_LISTING).setNodeId(root.id).setContinuation(first.continuation).build()
            rpc(service, "d".repeat(64), close, operations)
            val next = rpc(service, peer, FileRequest.newBuilder().setOperation(FileOperation.LIST_DIRECTORY).setNodeId(root.id).setContinuation(first.continuation).build(), operations)
            assertEquals(FileStatus.FILE_STATUS_OK, next.status)
            rpc(service, peer, close, operations)
            val stale = rpc(service, peer, FileRequest.newBuilder().setOperation(FileOperation.LIST_DIRECTORY).setNodeId(root.id).setContinuation(first.continuation).build(), operations)
            assertEquals(FileStatus.STALE_REFERENCE, stale.status)
            operations.clear()
            val work = launch { PhoneExplorerCatalog.load(listOf(root)) { value ->
                val response = rpc(service, peer, value, operations)
                if (value.operation == FileOperation.LIST_DIRECTORY) currentCoroutineContext().cancel()
                response
            } }
            work.join(); assertTrue(operations.contains(FileOperation.CLOSE_LISTING))
        }
    }
    @Test fun resultLimitsAreExplicitAndForgedParentsNeverBecomeSearchResults() = runBlocking {
        val root = FileEntry.newBuilder().setId(StorageRules.token()).setGrantId(StorageRules.token()).setName("Root").setIsRoot(true).setIsDirectory(true)
            .setVersion("b".repeat(64)).setPermissions(StorageRules.READ_ONLY).build()
        var offset = 0; var closed = false; val token = StorageRules.token()
        val request: suspend (FileRequest) -> FileResponse = { value ->
            val response = FileResponse.newBuilder().setRequestId(StorageRules.token())
            when (value.operation) {
                FileOperation.GET_METADATA -> response.addEntries(root)
                FileOperation.CLOSE_LISTING -> closed = true
                FileOperation.LIST_DIRECTORY -> { repeat(32) { response.addEntries(FileEntry.newBuilder().setId(StorageRules.token()).setGrantId(root.grantId).setParentId(root.id)
                    .setName("match-${offset++}").setVersion("c".repeat(64)).setPermissions(StorageRules.READ_ONLY)) }; response.continuation = token }
                else -> Unit
            }; response.build()
        }
        val result = PhoneExplorerCatalog.search(listOf(root), "match", request)
        assertFalse(result.complete); assertEquals(2048, result.rows.size); assertTrue(result.notice.contains("limit")); assertTrue(closed)
        var rejected = false
        try { PhoneExplorerCatalog.search(listOf(root), "match") { value -> request(value).let { response ->
            if (response.entriesCount == 0 || value.operation != FileOperation.LIST_DIRECTORY) response else response.toBuilder()
                .setEntries(0, response.getEntries(0).toBuilder().setParentId(StorageRules.token())).build()
        } } } catch (_: IllegalArgumentException) { rejected = true }
        assertTrue(rejected)
    }
}
