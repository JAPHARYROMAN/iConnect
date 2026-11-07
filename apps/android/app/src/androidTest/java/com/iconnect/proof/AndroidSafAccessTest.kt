package com.iconnect.proof

import android.content.Intent
import android.net.Uri
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.iconnect.protocol.FileEntry
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

@RunWith(AndroidJUnit4::class)
class AndroidSafAccessTest {
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext
    private val flags = Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION
    private val peerKey = TestPeer.key()
    private val peer = Rules.id(peerKey.public.encoded)
    private fun persisted(tree: Uri) = context.contentResolver.persistedUriPermissions.firstOrNull { it.uri == tree }
    private fun fixture(body: (Uri, Int) -> Unit) {
        val fixture = requireNotNull(context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "fixture", null, null))
        val tree = Uri.parse(fixture.getString("tree"))
        try { body(tree, fixture.getInt("flags")) }
        finally { context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "dispose", fixture.getString("root"), null) }
    }
    private fun entry() = FileEntry.newBuilder().setId(StorageRules.token()).setGrantId(StorageRules.token()).setVersion("b".repeat(64))
        .setName("download.txt").setSize(17).setPermissions(StorageRules.READ_ONLY).build()

    @Test fun revokingPeerAndClearingSelectionPreserveQueuedDownloadAcrossRestart() = fixture { tree, offered -> runBlocking {
        val access = AndroidSafAccess(context); val grants = AndroidStorageGrants(context)
        val approval = TestPeer.approve(grants.owner, peerKey)
        val grant = grants.add(approval, tree, offered, "Lease fixture"); access.selectDestination(tree, offered)
        val directory = File(context.filesDir, "access-test-${StorageRules.token()}")
        val id: String
        try {
            AndroidDownloadManager(context, directory).use { manager ->
                id = manager.enqueue(peer, entry(), tree)
                grants.revokePeer(peer)
                assertTrue(grants.forPeer(peer).isEmpty())
                assertTrue(persisted(tree)?.isWritePermission == true)
                access.clearDestination()
                assertTrue(persisted(tree)?.isReadPermission == true)
            }
            AndroidDownloadManager(context, directory).use { manager ->
                assertTrue(persisted(tree)?.isWritePermission == true)
                manager.cancel(id); manager.remove(id)
                assertNull(persisted(tree))
            }
        } finally { grants.remove(grant.id); access.clearDestination(); directory.deleteRecursively() }
    } }

    @Test fun replacingDestinationRetainsOldQueuedFolderOnlyUntilItsRecordIsRemoved() = fixture { first, offered -> fixture { second, secondFlags -> runBlocking {
        val access = AndroidSafAccess(context); access.selectDestination(first, offered)
        val directory = File(context.filesDir, "access-test-${StorageRules.token()}")
        try {
            AndroidDownloadManager(context, directory).use { manager ->
                val id = manager.enqueue(peer, entry(), first)
                access.selectDestination(second, secondFlags)
                assertEquals(second, AndroidSafAccess(context).destination())
                assertTrue(persisted(first)?.isWritePermission == true)
                access.clearDestination(); assertNull(persisted(second))
                manager.cancel(id); manager.remove(id); assertNull(persisted(first))
            }
        } finally { access.clearDestination(); directory.deleteRecursively() }
    } } }

    @Test fun independentPolicyInstancesCannotLoseOrResurrectAnotherInstancesChanges() = fixture { tree, offered ->
        val first = AndroidStorageGrants(context); val second = AndroidStorageGrants(context)
        val approval = TestPeer.approve(first.owner, peerKey)
        val other = TestPeer.approve(second.owner, TestPeer.key())
        val a = first.add(approval, tree, offered, "First approval")
        val b = second.add(other, tree, offered, "Second approval")
        try {
            assertTrue(first.all().any { it.id == b.id })
            first.update(a.id, StorageRules.LIST)
            assertEquals(StorageRules.LIST, second.get(a.id, peer).permissions)
            second.remove(a.id)
            assertFalse(first.all().any { it.id == a.id })
            first.update(b.id, StorageRules.READ_ONLY)
            assertFalse(AndroidStorageGrants(context).all().any { it.id == a.id })
            assertTrue(persisted(tree)?.isReadPermission == true)
        } finally { first.remove(a.id); first.remove(b.id); first.revokePeer(peer); first.revokePeer(other.id) }
        assertNull(persisted(tree))
    }

    @Test fun droppingWriteOwnerReleasesWriteWhileReadOwnerRemains() = fixture { tree, offered ->
        val access = AndroidSafAccess(context); val owner = "test:${StorageRules.token()}"
        try {
            access.acquire(owner, tree, Intent.FLAG_GRANT_READ_URI_PERMISSION)
            access.selectDestination(tree, offered); assertTrue(persisted(tree)?.isWritePermission == true)
            access.clearDestination()
            assertTrue(persisted(tree)?.isReadPermission == true); assertFalse(persisted(tree)?.isWritePermission == true)
            access.release(owner); assertNull(persisted(tree))
        } finally { access.clearDestination(); access.release(owner) }
    }
    @Test fun legacyApprovalsAndDestinationMigrateOnceWithoutResurrectingClearedAccess() = fixture { first, _ -> fixture { second, _ ->
        val prefix = "migration-${StorageRules.token()}-"
        val directory = File(context.filesDir, prefix)
        val isolated = object : android.content.ContextWrapper(context) {
            override fun getFilesDir() = directory
            override fun getSharedPreferences(name: String, mode: Int) = context.getSharedPreferences(prefix + name, mode)
        }
        context.contentResolver.takePersistableUriPermission(first, flags); context.contentResolver.takePersistableUriPermission(second, flags)
        val id = StorageRules.token()
        val old = org.json.JSONArray().put(org.json.JSONObject().put("id", id).put("peer", peer).put("tree", first.toString()).put("label", "Legacy folder")
            .put("permissions", StorageRules.READ_ONLY).put("exclusions", org.json.JSONArray()).put("expires", 0).put("enabled", true))
        isolated.getSharedPreferences("iconnect.storage", 0).edit().putString("grants", old.toString()).commit()
        isolated.getSharedPreferences("iconnect.proof", 0).edit().putString("download-tree", second.toString()).commit()
        try {
            val access = AndroidSafAccess(isolated); assertEquals(second, access.destination())
            val grants = AndroidStorageGrants(isolated)
            assertEquals(first, grants.all().single().tree)
            assertFalse(grants.all().single().enabled); assertEquals("", grants.all().single().trustEpoch)
            assertTrue(grants.forPeer(peer).isEmpty()); assertNull(grants.owner.selected())
            assertEquals("legacy", grants.owner.notice)
            grants.remove(id); assertNull(persisted(first)); assertNotNull(persisted(second))
            access.clearDestination(); assertNull(persisted(second))
            assertNull(AndroidSafAccess(isolated).destination())
        } finally {
            listOf("iconnect.saf-access", "iconnect.storage", "iconnect.proof").forEach { context.deleteSharedPreferences(prefix + it) }
            directory.deleteRecursively()
        }
    } }
    @Test fun failedPolicyOrAccessCommitCannotCreateALivePeerApproval() = fixture { tree, offered ->
        val prefix = "fault-${StorageRules.token()}-"; val directory = File(context.filesDir, prefix)
        val isolated = object : android.content.ContextWrapper(context) {
            override fun getFilesDir() = directory
            override fun getSharedPreferences(name: String, mode: Int) = context.getSharedPreferences(prefix + name, mode)
        }
        val store = AndroidStorageGrants(isolated)
        val approval = TestPeer.approve(store.owner, peerKey)
        val other = TestPeer.approve(store.owner, TestPeer.key())
        val old = store.add(approval, tree, offered, "Existing approval")
        try {
            for (name in listOf("owner-policy.json.new", "saf-access.json.new")) {
                val blocker = File(directory, name); check(blocker.mkdir())
                val held = File(blocker, "fixture"); held.writeText("Keep this injected failure present during AtomicFile recovery.")
                try {
                    var failed = false
                    try { store.add(other, tree, offered, "Must not be approved") } catch (_: Exception) { failed = true }
                    assertTrue(failed); assertTrue(store.forPeer(other.id).isEmpty())
                    assertEquals(old.id, store.get(old.id, peer).id)
                    val saved = org.json.JSONObject(File(directory, "owner-policy.json").readText()).getJSONArray("grants")
                    assertEquals(1, saved.length()); assertEquals(old.id, saved.getJSONObject(0).getString("id"))
                } finally { check(!held.exists() || held.delete()); check(!blocker.exists() || blocker.delete()) }
                assertTrue(AndroidStorageGrants(isolated).forPeer(other.id).isEmpty())
            }
        } finally {
            store.remove(old.id)
            listOf("iconnect.saf-access", "iconnect.storage", "iconnect.proof").forEach { context.deleteSharedPreferences(prefix + it) }
            directory.deleteRecursively()
        }
    }
}
