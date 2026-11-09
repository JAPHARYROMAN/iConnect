package com.iconnect.proof

import android.content.Context
import android.net.Uri
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.iconnect.protocol.*
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

@RunWith(AndroidJUnit4::class)
class QueueRecoveryTest {
    private var isolatedContext: Context? = null
    private val context get() = checkNotNull(isolatedContext) { "Open an isolated storage fixture first." }
    private val peer = "b".repeat(64)
    private suspend fun fixture(body: suspend (Uri, Int, File) -> Unit) {
        check(isolatedContext==null)
        IsolatedStorageContext().use { isolated ->
            isolatedContext=isolated
            try {
                val fixture = requireNotNull(context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "fixture", null, null))
                val tree = Uri.parse(fixture.getString("tree")); val flags = fixture.getInt("flags")
                val access = AndroidSafAccess(context); access.selectDestination(tree, flags)
                val directory = File(context.filesDir, "queue-recovery-${StorageRules.token()}"); check(directory.mkdir())
                try { body(tree, flags, directory) }
                finally { access.clearDestination(); directory.deleteRecursively(); context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "dispose", fixture.getString("root"), null) }
            } finally {isolatedContext=null}
        }
    }
    private fun entry() = FileEntry.newBuilder().setId(StorageRules.token()).setGrantId(StorageRules.token()).setName("download.txt")
        .setVersion("a".repeat(64)).setSize(17).setPermissions(StorageRules.READ_ONLY).build()
    @Test fun damagedProgressLoadsBesideValidRecordAndArchivingPreservesStagingAndItsAndroidLeaseUntilReview() = runBlocking { fixture { tree, _, directory ->
        val id: String
        AndroidDownloadManager(context, directory).use { id = it.enqueue(peer, entry(), tree) }
        val bad = File(directory, "$id.json"); bad.writeText("{damaged"); val part = File(directory, "$id.part"); part.writeText("private retained bytes")
        AndroidSafAccess(context).clearDestination()
        AndroidDownloadManager(context, directory).use { loaded ->
            assertTrue(loaded.state.value.isEmpty()); val issue = loaded.recoveryIssues().single(); assertTrue(issue.canArchive)
            assertTrue(context.contentResolver.persistedUriPermissions.any { it.uri == tree && it.isWritePermission })
            var blocked = false; try { loaded.enqueue(peer, entry(), tree) } catch (_: StorageFailure) { blocked = true }; assertTrue(blocked)
            loaded.archiveRecovery(issue.id); assertTrue(loaded.recoveryIssues().isEmpty())
            assertEquals("private retained bytes", part.readText()); assertEquals("{damaged", File(directory, "recovery").walkTopDown().first { it.name == "$id.json" }.readText())
            val preserved = File(directory, "recovery").listFiles()!!.single(); val unknown = File(preserved, "unexpected.bin"); unknown.writeText("keep unknown content")
            var refused = false; try { loaded.clearPreservedRecords() } catch (_: IllegalStateException) { refused = true }
            assertTrue(refused); assertEquals("keep unknown content", unknown.readText()); unknown.delete()
            loaded.clearPreservedRecords(); assertEquals(0, loaded.preservedRecords()); assertEquals("private retained bytes", part.readText())
            assertFalse(context.contentResolver.persistedUriPermissions.any { it.uri == tree })
        }
    } }
    @Test fun backupAndUncommittedMetadataArePreservedAndChangedRecordsRequireAnotherReview() = runBlocking { fixture { _, _, directory ->
        val id = StorageRules.token(); val staged = File(directory, "$id.json.new"); staged.writeText("uncommitted")
        AndroidDownloadManager(context, directory).use { loaded ->
            val issue = loaded.recoveryIssues().single(); staged.writeText("changed after review")
            var rejected = false; try { loaded.archiveRecovery(issue.id) } catch (_: IllegalStateException) { rejected = true }
            assertTrue(rejected); assertTrue(staged.exists())
        }
        AndroidDownloadManager(context, directory).use { reviewed -> reviewed.archiveRecovery(reviewed.recoveryIssues().single().id); assertTrue(reviewed.recoveryIssues().isEmpty()) }
        val backup = File(directory, "${StorageRules.token()}.json.bak"); backup.writeText("{bad backup")
        AndroidDownloadManager(context, directory).use { restored -> assertEquals(1, restored.recoveryIssues().size); restored.archiveRecovery(restored.recoveryIssues().single().id) }
        assertEquals(2, File(directory, "recovery").listFiles()!!.size)
    } }
    @Test fun aChildCannotResumeOrPublishOutsideItsOriginalBatchEvenAfterParentMetadataIsArchived() = runBlocking { fixture { tree, _, parent ->
        val filesPath = File(parent, "files"); val batchPath = File(parent, "batches"); val batchId = StorageRules.token(); val child: String
        AndroidDownloadManager(context, filesPath).use { files -> child = files.enqueue(peer, entry(), tree, batchId = batchId) }
        check(batchPath.mkdir()); File(batchPath, "$batchId.json").writeText("null")
        AndroidDownloadManager(context, filesPath).use { files -> AndroidBatchDownloads(context, files, batchPath).use { batches ->
            assertTrue(batches.state.value.isEmpty()); assertEquals(batchId, files.state.value.single().batchId)
            var reads = 0; val request: suspend (FileRequest) -> FileResponse = { reads++; error("No source request is authorized") }
            var denied = false; try { files.resume(child, peer, request) } catch (_: IllegalStateException) { denied = true }; assertTrue(denied)
            denied = false; try { files.publish(child) } catch (_: IllegalStateException) { denied = true }; assertTrue(denied)
            batches.archiveRecovery(batches.recoveryIssues().single().id); assertTrue(batches.recoveryIssues().isEmpty()); assertEquals(0, reads)
            files.cancel(child); files.remove(child); assertTrue(files.state.value.isEmpty())
        } }
    } }
    @Test fun unreadableBatchDoesNotReleaseRecordedDestinationAccessBeforeItsOwnerArchivesIt() = runBlocking { fixture { tree, _, parent ->
        val filesPath = File(parent, "files"); val batchPath = File(parent, "batches"); val id: String
        val root = FileEntry.newBuilder().setId(StorageRules.token()).setGrantId(StorageRules.token()).setName("Empty").setIsDirectory(true)
            .setVersion("a".repeat(64)).setPermissions(StorageRules.READ_ONLY).build()
        val manifest = PhoneTransferManifest(listOf(PhoneTransferItem(-1, root.grantId, listOf("Empty"), "Empty", true, 0, root.version)))
        AndroidDownloadManager(context, filesPath).use { files -> AndroidBatchDownloads(context, files, batchPath).use { batches -> id = batches.enqueue(peer, StorageRules.token(), manifest, tree, PhoneCollision.KEEP_BOTH) } }
        File(batchPath, "$id.json").writeText("{damaged batch"); AndroidSafAccess(context).clearDestination()
        AndroidDownloadManager(context, filesPath).use { files -> AndroidBatchDownloads(context, files, batchPath).use { batches ->
            assertTrue(context.contentResolver.persistedUriPermissions.any { it.uri == tree && it.isWritePermission })
            batches.archiveRecovery(batches.recoveryIssues().single().id)
            assertFalse(context.contentResolver.persistedUriPermissions.any { it.uri == tree })
        } }
    } }
    @Test fun damagedSendingCacheDoesNotPreventBrowsingAndCannotCreateTicketsOrBeOverwrittenByRevocation() = runBlocking { fixture { tree, flags, _ ->
        val cache = File(context.filesDir, "download-sources.json"); val previous = if (cache.exists()) cache.readBytes() else null
        val grants = AndroidStorageGrants(context); val key = TestPeer.key(); val approval = TestPeer.approve(grants.owner, key)
        val grant = grants.add(approval, tree, flags, "Recovery source fixture")
        var archive = ""
        try {
            PrivateJsonFile(cache, 4 * 1024 * 1024).write("{bad source cache")
            val tickets = AndroidDownloadSources(context, grants)
            try { AndroidFileService(context, grants, tickets).use { host ->
                suspend fun rpc(request: FileRequest) = host.execute(approval.id, request.toBuilder().setRequestId(StorageRules.token()).build())
                val root = rpc(FileRequest.newBuilder().setOperation(FileOperation.LIST_ROOTS).build()).entriesList.single()
                val file = rpc(FileRequest.newBuilder().setOperation(FileOperation.LIST_DIRECTORY).setNodeId(root.id).build()).entriesList.single { !it.isDirectory }
                val prepare = FileRequest.newBuilder().setOperation(FileOperation.PREPARE_DOWNLOAD).setNodeId(file.id).setExpectedVersion(file.version).build()
                assertEquals(FileStatus.CONFLICT, rpc(prepare).status); assertEquals("{bad source cache", cache.readText())
                archive = tickets.recoveryIssues().single().id; tickets.archiveRecovery(archive); assertEquals(FileStatus.FILE_STATUS_OK, rpc(prepare).status)
            } } finally { tickets.shutdown() }
        } finally {
            grants.remove(grant.id); grants.revokePeer(approval.id)
            if (previous == null) cache.delete() else cache.writeBytes(previous)
            if (archive.isNotEmpty()) File(context.filesDir, "recovery/$archive").deleteRecursively()
        }
    } }
}
