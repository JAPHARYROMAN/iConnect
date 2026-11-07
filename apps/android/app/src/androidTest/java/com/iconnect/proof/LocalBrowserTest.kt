package com.iconnect.proof

import android.content.Intent
import android.net.Uri
import android.os.Bundle
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.iconnect.protocol.*
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

@RunWith(AndroidJUnit4::class)
class LocalBrowserTest {
    private val instrumentation get() = InstrumentationRegistry.getInstrumentation()
    private val context get() = instrumentation.targetContext
    private fun onMain(action: () -> Unit) = instrumentation.runOnMainSync(action)
    private fun waitFor(label: String, condition: () -> Boolean) {
        val end = System.currentTimeMillis() + 10_000
        while (System.currentTimeMillis() < end) { if (condition()) return; Thread.sleep(20) }
        fail("Timed out: $label")
    }
    private fun persisted(tree: Uri) = context.contentResolver.persistedUriPermissions.firstOrNull { it.uri == tree }
    private suspend fun fixture(body: suspend (Bundle, Uri, LocalDocumentLocations) -> Unit) {
        val fixture = requireNotNull(context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "fixture", null, Bundle().apply { putInt("extraFiles", 110) }))
        val tree = Uri.parse(fixture.getString("tree")); val locations = LocalDocumentLocations(context)
        assertTrue(locations.issue, locations.issue.isEmpty()); assertTrue(locations.all().isEmpty())
        try { body(fixture, tree, locations) }
        finally {
            val clean = LocalDocumentLocations(context)
            if (clean.issue.isNotEmpty()) clean.reset() else clean.all().forEach { clean.remove(it.id) }
            context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "dispose", fixture.getString("root"), null)
        }
    }
    private suspend fun rpc(service: AndroidFileService, peer: String, request: FileRequest) =
        service.execute(peer, request.toBuilder().setRequestId(StorageRules.token()).build())

    @Test fun localReferencesAndSensitiveNamesNeverBecomeRemoteAuthority() = runBlocking { fixture { fixture, tree, locations ->
        val remote = AndroidStorageGrants(context); val before = remote.all()
        locations.add(tree, fixture.getInt("flags")); assertEquals(before, remote.all())
        AndroidFileService(context, locations as DocumentAccessPolicy).use { service ->
            val root = rpc(service, locations.ownerId(), FileRequest.newBuilder().setOperation(FileOperation.LIST_ROOTS).build()).entriesList.single()
            val found = PhoneExplorerCatalog.search(listOf(root), ".ssh") { rpc(service, locations.ownerId(), it) }
            assertEquals(".ssh", found.rows.single().entry.name)
            assertTrue(rpc(service, "f".repeat(64), FileRequest.newBuilder().setOperation(FileOperation.LIST_ROOTS).build()).entriesList.isEmpty())
            assertEquals(FileStatus.ACCESS_DENIED, rpc(service, locations.ownerId(), FileRequest.newBuilder().setOperation(FileOperation.CREATE_DIRECTORY)
                .setDestinationId(root.id).setName("unexpected").build()).status)
            AndroidFileService(context, remote).use { other ->
                assertEquals(FileStatus.STALE_REFERENCE, rpc(other, "f".repeat(64), FileRequest.newBuilder().setOperation(FileOperation.GET_METADATA).setNodeId(root.id).build()).status)
            }
            val id = locations.all().single().id; locations.remove(id)
            assertEquals(FileStatus.ACCESS_DENIED, rpc(service, locations.ownerId(), FileRequest.newBuilder().setOperation(FileOperation.GET_METADATA).setNodeId(root.id).build()).status)
        }
    } }
    @Test fun localShortcutLeaseSurvivesRestartAndReleasesOnlyItsOwnAndroidAccess() = runBlocking { fixture { fixture, tree, locations ->
        val access = AndroidSafAccess(context); val row = locations.add(tree, fixture.getInt("flags"))
        assertTrue(persisted(tree)?.isReadPermission == true); assertFalse(persisted(tree)?.isWritePermission == true)
        val restarted = LocalDocumentLocations(context); assertEquals(row, restarted.all().single())
        access.selectDestination(tree, fixture.getInt("flags"))
        restarted.remove(row.id); assertTrue(persisted(tree)?.isWritePermission == true)
        val again = restarted.add(tree, fixture.getInt("flags")); access.clearDestination()
        assertTrue(persisted(tree)?.isReadPermission == true); assertFalse(persisted(tree)?.isWritePermission == true)
        restarted.remove(again.id); assertNull(persisted(tree))
    } }
    @Test fun failedPrivateCommitCannotPublishShortcutOrLeaveUnownedReadPermission() = runBlocking { fixture { fixture, tree, locations ->
        val blocker = File(context.filesDir, "local-locations.json.new"); assertTrue(blocker.mkdir())
        try {
            var rejected = false; try { locations.add(tree, fixture.getInt("flags")) } catch (_: Exception) { rejected = true }
            assertTrue(rejected); assertTrue(locations.all().isEmpty()); assertNull(persisted(tree))
        } finally { assertTrue(blocker.delete()) }
    } }
    @Test fun damagedLocalRecordsHaveRecoveryWithoutRemovingDestinationOrRemoteApprovals() = runBlocking { fixture { fixture, tree, locations ->
        locations.add(tree, fixture.getInt("flags")); val remote = AndroidStorageGrants(context); val before = remote.all()
        val access = AndroidSafAccess(context); access.selectDestination(tree, fixture.getInt("flags"))
        try {
            PrivateJsonFile(File(context.filesDir, "local-locations.json"), 256 * 1024).write("{damaged")
            val damaged = LocalDocumentLocations(context); assertTrue(damaged.issue.isNotEmpty()); assertTrue(damaged.all().isEmpty())
            damaged.reset(); assertTrue(damaged.issue.isEmpty()); assertTrue(damaged.all().isEmpty())
            assertEquals(tree, access.destination()); assertTrue(persisted(tree)?.isWritePermission == true); assertEquals(before, remote.all())
        } finally { access.clearDestination() }
    } }
    @Test fun offlineBrowserPagesSearchesPreviewsAndClearsMetadataWhenActivityStops() = runBlocking { fixture { fixture, tree, _ ->
        lateinit var browser: PhoneLocalBrowser
        onMain { browser = PhoneLocalBrowser(context); browser.add(tree, fixture.getInt("flags")) }
        assertTrue(browser.state.value.locations.isEmpty()) // Picker results may arrive before Activity.onStart.
        onMain { browser.setVisible(true) }
        try {
            waitFor("saved local shortcut") { !browser.state.value.busy && browser.state.value.rows.size == 1 }
            onMain { browser.browse(browser.state.value.rows.single().entry) }
            waitFor("multipage local catalog") { !browser.state.value.busy && browser.state.value.count == 113 }
            assertEquals(3, browser.state.value.pages)
            onMain { browser.publish(2); browser.layout(PhoneExplorerLayout.GRID); browser.sort(PhoneExplorerSort.SIZE, true) }
            assertEquals(0, browser.state.value.page); assertEquals(PhoneExplorerLayout.GRID, browser.state.value.layout)
            onMain { browser.search("NOTES") }
            waitFor("scoped local filename search") { !browser.state.value.busy && browser.state.value.query == "NOTES" }
            assertEquals("Reports / notes.txt", browser.state.value.rows.single().relativePath)
            onMain { browser.inspect(browser.state.value.rows.single().entry, true) }
            waitFor("real provider text preview") { browser.state.value.preview == "nested fixture" }
            onMain { browser.inspect(browser.state.value.rows.single().entry, false) }
            waitFor("live local details") { browser.state.value.details?.name == "notes.txt" }
            onMain { browser.setVisible(false) }
            assertTrue(browser.state.value.rows.isEmpty()); assertTrue(browser.state.value.breadcrumbs.isEmpty()); assertTrue(browser.state.value.preview.isEmpty()); assertNull(browser.state.value.details)
            onMain { browser.setVisible(true); browser.browse() }
            waitFor("offline shortcut after activity return") { !browser.state.value.busy && browser.state.value.rows.size == 1 }
        } finally { lateinit var closing: Job; onMain { closing = browser.close() }; closing.join() }
    } }
}
