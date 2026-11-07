package com.iconnect.proof

import android.content.Context
import android.content.ContextWrapper
import android.net.Uri
import android.os.Bundle
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

@RunWith(AndroidJUnit4::class)
class AndroidOwnerPolicyTest {
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext
    private fun isolated(body: (Context, File) -> Unit) {
        val prefix = "owner-test-${StorageRules.token()}-"
        val directory = File(context.filesDir, prefix).apply { check(mkdir()) }
        val local = object : ContextWrapper(context) {
            override fun getFilesDir() = directory
            override fun getSharedPreferences(name: String, mode: Int) = context.getSharedPreferences(prefix + name, mode)
        }
        try { body(local, directory) }
        finally {
            listOf("iconnect.proof", "iconnect.storage", "iconnect.saf-access").forEach { context.deleteSharedPreferences(prefix + it) }
            directory.deleteRecursively()
        }
    }
    private fun fixture(body: (Uri, Int, String) -> Unit) {
        val row = requireNotNull(context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "fixture", null, null))
        try { body(Uri.parse(row.getString("tree")), row.getInt("flags"), requireNotNull(row.getString("root"))) }
        finally { context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "dispose", row.getString("root"), null) }
    }
    private fun denied(body: () -> Unit) {
        var failed = false
        try { body() } catch (_: Exception) { failed = true }
        assertTrue("Operation must reject changed or unavailable authority", failed)
    }
    private fun blocked(directory: File, filename: String, body: () -> Unit) {
        val staging = File(directory, "$filename.new").apply { check(mkdir()) }
        val held = File(staging, "fixture").apply { writeText("AtomicFile must not remove this injected write failure.") }
        try { body() } finally { check(held.delete()); check(staging.delete()) }
    }
    private fun copyPolicy(from: File, into: File) {
        listOf("owner-policy.json", "trust-revocations.json").forEach { name ->
            File(from, name).takeIf { it.exists() }?.copyTo(File(into, name))
        }
    }

    @Test fun onlyMutuallySignedApprovalCanBecomeDurableTrust() = isolated { local, directory ->
        val policy = AndroidOwnerPolicy(local); val key = TestPeer.key(); val attempt = policy.beginPair(key.public.encoded)
        val receipt = TestPeer.receipt(key)
        denied { policy.commitPair(attempt, receipt.toBuilder().clearHostApproval().build()) }
        denied { policy.commitPair(attempt, receipt.toBuilder().clearGuestApproval().build()) }
        denied { policy.commitPair(attempt, receipt.toBuilder().setExpiresAt(1).build()) }
        denied { policy.commitPair(attempt, TestPeer.receipt(TestPeer.key())) }
        assertNull(policy.selected())
        val peer = policy.commitPair(attempt, receipt)
        assertEquals(peer, AndroidOwnerPolicy(local).selected())
        val saved = JSONObject(File(directory, "owner-policy.json").readText())
        assertEquals(LocalIdentity().id, saved.getString("identity"))
        assertEquals(peer.epoch, saved.getJSONArray("peers").getJSONObject(0).getString("epoch"))
        isolated { cold, coldDirectory -> copyPolicy(directory, coldDirectory); assertEquals(peer, AndroidOwnerPolicy(cold).selected()) }
        denied { policy.beginPair(LocalIdentity().publicKey) }
    }

    @Test fun canceledAndFailedPairingWritesCannotAdmitAComputer() = isolated { local, directory ->
        val policy = AndroidOwnerPolicy(local); val key = TestPeer.key()
        val attempt = policy.beginPair(key.public.encoded); val receipt = TestPeer.receipt(key)
        blocked(directory, "owner-policy.json") { denied { policy.commitPair(attempt, receipt) } }
        assertNull(policy.selected())
        assertEquals(0, JSONObject(File(directory, "owner-policy.json").readText()).getJSONArray("peers").length())
        val handle = policy.cancelPair(attempt); assertTrue(policy.revoke(setOf(handle)))
        denied { policy.commitPair(attempt, receipt) }
        assertNull(AndroidOwnerPolicy(local).selected())
        assertTrue(policy.isCurrent(TestPeer.approve(policy, key)))
    }

    @Test fun revocationJournalDeniesColdLoadWhenPolicyCleanupFails() = fixture { tree, flags, _ -> isolated { local, directory ->
        val store = AndroidStorageGrants(local); val peer = TestPeer.approve(store.owner, TestPeer.key())
        val grant = store.add(peer, tree, flags, "Saved approval")
        val access = AndroidSafAccess(local); access.selectDestination(tree, flags)
        val handle = store.owner.blockPeer(peer)
        blocked(directory, "owner-policy.json") {
            assertFalse(store.owner.revoke(setOf(handle)))
            assertFalse(store.owner.isCurrent(peer)); denied { store.get(grant.id, peer.id) }
            isolated { cold, coldDirectory ->
                copyPolicy(directory, coldDirectory)
                val restored = AndroidOwnerPolicy(cold)
                assertNull(restored.selected()); assertTrue(restored.activeGrants(peer.id).isEmpty())
            }
        }
        assertTrue(store.owner.revoke(setOf(handle))); store.reconcileAccess()
        assertTrue(context.contentResolver.persistedUriPermissions.any { it.uri == tree && it.isWritePermission })
        assertEquals(tree, access.destination()); access.clearDestination()
    } }

    @Test fun failedJournalSaveRemainsBlockedUntilExplicitRetry() = isolated { local, directory ->
        val policy = AndroidOwnerPolicy(local); val peer = TestPeer.approve(policy, TestPeer.key())
        val handle = policy.blockPeer(peer)
        blocked(directory, "trust-revocations.json") {
            denied { policy.revoke(setOf(handle)) }; assertNull(policy.current(peer.id))
        }
        assertTrue(policy.revoke(setOf(handle)))
        isolated { cold, coldDirectory -> copyPolicy(directory, coldDirectory); assertNull(AndroidOwnerPolicy(cold).selected()) }
    }

    @Test fun lateProviderApprovalAndOldRevocationCannotAffectNewPairing() = fixture { tree, flags, root -> isolated { local, _ ->
        val store = AndroidStorageGrants(local); val key = TestPeer.key()
        val oldPeer = TestPeer.approve(store.owner, key)
        val oldGrant = store.add(oldPeer, tree, flags, "Earlier pairing")
        context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "creationMode", root, Bundle().apply { putString("mode", "delayQuery") })
        val worker = Executors.newSingleThreadExecutor()
        val late = worker.submit<AndroidStorageGrant> { store.add(oldPeer, tree, flags, "Late approval") }
        try {
            val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5)
            while (context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "queryProbe", root, null)?.getBoolean("entered") != true) {
                check(System.nanoTime() < deadline) { "Fixture never entered delayed provider query" }; Thread.sleep(10)
            }
            val revoked = store.owner.blockPeer(oldPeer)
            assertTrue(store.owner.revoke(setOf(revoked)))
            val fresh = TestPeer.approve(AndroidOwnerPolicy(local), key)
            assertNotEquals(oldPeer.epoch, fresh.epoch)
            context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "releaseQuery", root, null)
            denied { late.get(5, TimeUnit.SECONDS) }
            denied { store.get(oldGrant.id, fresh.id) }
            context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "creationMode", root, Bundle().apply { putString("mode", "") })
            val newGrant = store.add(fresh, tree, flags, "New explicit approval")
            assertTrue(store.owner.revoke(setOf(revoked)))
            assertTrue(store.owner.isCurrent(fresh)); assertEquals(newGrant, store.get(newGrant.id, fresh.id))
            assertEquals(listOf(newGrant), store.all())
            store.revokePeer(fresh.id)
        } finally {
            context.contentResolver.call(TestDocumentsProvider.CONTROL_URI, "releaseQuery", root, null)
            worker.shutdownNow(); worker.awaitTermination(5, TimeUnit.SECONDS)
            store.owner.current(oldPeer.id)?.let { store.revokePeer(it.id) }; store.reconcileAccess()
        }
    } }

    @Test fun changedLocalIdentityDisablesAllEarlierRemoteApprovals() = fixture { tree, flags, _ -> isolated { local, _ ->
        val store = AndroidStorageGrants(local); val peer = TestPeer.approve(store.owner, TestPeer.key())
        store.add(peer, tree, flags, "Previous identity")
        val nextIdentity = Rules.id(TestPeer.key().public.encoded)
        val changed = AndroidOwnerPolicy(local, nextIdentity)
        assertFalse(store.owner.isCurrent(peer)); assertNull(changed.selected())
        assertEquals("identity-changed", changed.notice)
        assertTrue(changed.allGrants().all { !it.enabled && it.trustEpoch.isEmpty() })
        assertTrue(changed.activeGrants(peer.id).isEmpty())
        changed.reset(); AndroidSafAccess(local).reconcile("share:", emptyMap())
    } }

    @Test fun corruptPolicyDoesNotFallBackToLegacyTrustAndResetKeepsLocalFiles() = isolated { local, directory ->
        File(directory, "owner-policy.json").writeText("broken")
        val marker = File(directory, "keep.txt").apply { writeText("owner file") }
        local.getSharedPreferences("iconnect.proof", 0).edit().putString("peer-key", Rules.b64(TestPeer.key().public.encoded)).commit()
        val policy = AndroidOwnerPolicy(local)
        assertFalse(policy.healthy); assertTrue(policy.issue.isNotEmpty()); assertNull(policy.selected())
        denied { policy.beginPair(TestPeer.key().public.encoded) }
        policy.reset(); assertTrue(policy.healthy); assertEquals("owner file", marker.readText())
        assertTrue(policy.isCurrent(TestPeer.approve(policy, TestPeer.key())))
    }

    @Test fun malformedSavedPermissionTypesCannotRestoreDisabledOrExpiringAccess() = fixture { tree, flags, _ -> isolated { local, directory ->
        val store = AndroidStorageGrants(local); val peer = TestPeer.approve(store.owner, TestPeer.key())
        val grant = store.add(peer, tree, flags, "Typed policy")
        try {
            for ((field, value) in listOf("enabled" to "invalid", "expires" to "invalid", "permissions" to 3.5, "epoch" to JSONObject.NULL)) {
                isolated { cold, coldDirectory ->
                    val saved = JSONObject(File(directory, "owner-policy.json").readText())
                    saved.getJSONArray("grants").getJSONObject(0).put(field, value)
                    File(coldDirectory, "owner-policy.json").writeText(saved.toString())
                    val policy = AndroidOwnerPolicy(cold)
                    assertFalse("Malformed $field must deny all remote authority", policy.healthy)
                    assertTrue(policy.activeGrants(peer.id).isEmpty()); assertNull(policy.selected())
                }
            }
        } finally { store.remove(grant.id); store.revokePeer(peer.id) }
    } }
}
