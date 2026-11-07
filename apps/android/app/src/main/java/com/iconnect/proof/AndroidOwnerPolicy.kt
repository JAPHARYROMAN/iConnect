package com.iconnect.proof

import android.content.Context
import com.iconnect.protocol.Pairing
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicReference

data class PeerApproval(val id: String, val key: String, val name: String, val epoch: String) {
    fun publicKey() = Rules.decode(key)
}
class PairAttempt internal constructor(val id: String, val key: String, val epoch: String)
data class RevokedApproval(val id: String, val epoch: String)

/** Trust and grants share one atomic authority record. Revocation intent bypasses slow provider I/O. */
class AndroidOwnerPolicy(private val context: Context, private val ownId: String = LocalIdentity().id) {
    private data class State(val peers: Map<String, PeerApproval> = emptyMap(), val selected: String? = null,
        val grants: List<AndroidStorageGrant> = emptyList(), val notice: String = "")
    private class Shared(val ownId: String) {
        @Volatile var state = State()
        @Volatile var issue = ""
        @Volatile var loaded = false
        @Volatile var invalidated = false
        var recoveryGeneration = StorageRules.token()
        var approvalRevision = 0L
        val blocked = ConcurrentHashMap.newKeySet<String>()
        val pending = AtomicReference<PairAttempt?>()
        val intentGate = Any()
        var intents = emptySet<RevokedApproval>()
    }
    private val path = context.filesDir.canonicalPath
    internal val gate = AndroidSafAccess(context).gate
    private val file = PrivateJsonFile(File(context.filesDir, "owner-policy.json"), 4 * 1024 * 1024)
    private val journal = PrivateJsonFile(File(context.filesDir, "trust-revocations.json"), 256 * 1024)
    private val shared = synchronized(stores) {
        val old = stores[path]
        if (old != null && old.ownId == ownId) old else {
            old?.invalidated = true
            Shared(ownId).also { stores[path] = it }
        }
    }
    init { require(ownId.matches(Regex("[a-f0-9]{64}"))); synchronized(gate) { if (!shared.loaded) load() } }
    val healthy get() = shared.issue.isEmpty() && !shared.invalidated
    internal val deviceId get() = ownId
    internal val recoveryStamp get()=synchronized(gate) {check(healthy) {issue}; ownId+"."+shared.recoveryGeneration}
    internal val approvalStamp get()=synchronized(gate) {check(healthy) {issue}; "$ownId.${shared.recoveryGeneration}.${shared.approvalRevision}"}
    val issue get() = if (shared.invalidated) "Device identity changed. Reopen iConnect." else shared.issue
    val notice get() = shared.state.notice
    fun selected(): PeerApproval? = shared.state.selected?.let { shared.state.peers[it] }?.takeIf(::isCurrent)
    fun current(id: String): PeerApproval? = shared.state.peers[id]?.takeIf(::isCurrent)
    fun isCurrent(peer: PeerApproval): Boolean = healthy && peer.epoch !in shared.blocked && shared.state.peers[peer.id] == peer
    fun capture(id: String) = current(id) ?: error("Pair this computer again before approving storage.")
    fun allGrants(): List<AndroidStorageGrant> = if (healthy) shared.state.grants.toList() else emptyList()
    fun activeGrants(id: String): List<AndroidStorageGrant> {
        val peer = current(id) ?: return emptyList()
        return shared.state.grants.filter { it.peerId == id && it.trustEpoch == peer.epoch && it.enabled &&
            (it.expiresAt == 0L || it.expiresAt > System.currentTimeMillis()) }
    }
    fun approvalCurrent(grant: AndroidStorageGrant) = current(grant.peerId)?.epoch == grant.trustEpoch && grant.trustEpoch.isNotEmpty()
    fun beginPair(key: ByteArray): PairAttempt {
        check(healthy) { issue }; val id = Rules.id(key); require(id != ownId)
        val attempt = PairAttempt(id, Rules.b64(key), StorageRules.token())
        check(shared.pending.compareAndSet(null, attempt)) { "Finish or cancel the current pairing first." }
        return attempt
    }
    fun commitPair(attempt: PairAttempt, receipt: Pairing): PeerApproval {
        val now = System.currentTimeMillis()
        require(Rules.tokenPattern.matches(receipt.token) && Rules.tokenPattern.matches(receipt.transactionId) &&
            receipt.expiresAt in (now + 1)..(now + 120_000) && Rules.id(receipt.guest.publicKey.toByteArray()) == ownId &&
            Rules.b64(receipt.host.publicKey.toByteArray()) == attempt.key && Rules.id(receipt.host.publicKey.toByteArray()) == attempt.id)
        validateName(receipt.host.name); validateName(receipt.guest.name)
        val transcript = Rules.pairing(receipt)
        require(Rules.verify(receipt.host.publicKey.toByteArray(), transcript, receipt.hostApproval.toByteArray()) &&
            Rules.verify(receipt.guest.publicKey.toByteArray(), transcript, receipt.guestApproval.toByteArray())) { "Mutual pairing approval is required." }
        return synchronized(gate) {
            check(healthy && shared.pending.get() === attempt && attempt.epoch !in shared.blocked) { "Pairing was canceled or changed." }
            val peer = PeerApproval(attempt.id, attempt.key, receipt.host.name, attempt.epoch)
            check(shared.state.peers.size < 128 || peer.id in shared.state.peers)
            publish(clean(shared.state).copy(peers = clean(shared.state).peers + (peer.id to peer), selected = peer.id, notice = ""))
            shared.pending.compareAndSet(attempt, null)
            check(isCurrent(peer)) { "Pairing was canceled while it was being saved." }
            peer
        }
    }
    /** Lock-free admission barrier; caller then persists these exact epochs on IO. */
    fun blockPeer(peer: PeerApproval): RevokedApproval {
        shared.blocked.add(peer.epoch)
        return RevokedApproval(peer.id, peer.epoch)
    }
    fun cancelPair(attempt: PairAttempt): RevokedApproval {
        shared.blocked.add(attempt.epoch); shared.pending.compareAndSet(attempt, null)
        return RevokedApproval(attempt.id, attempt.epoch)
    }
    /** Returns false only when physical policy cleanup needs retry; intent already denies the epoch durably. */
    fun revoke(handles: Set<RevokedApproval>): Boolean {
        if (handles.isEmpty()) return true
        check(!shared.invalidated)
        handles.forEach { shared.blocked.add(it.epoch) }
        synchronized(shared.intentGate) {
            val after = shared.intents + handles; writeIntents(after); shared.intents = after
        }
        return synchronized(gate) {
            runCatching {
                publish(clean(shared.state))
                synchronized(shared.intentGate) {
                    val retained = shared.intents.filter { item -> shared.state.peers[item.id]?.epoch == item.epoch || shared.pending.get()?.epoch == item.epoch }.toSet()
                    writeIntents(retained); shared.intents = retained
                }
            }.isSuccess
        }
    }
    internal fun addGrant(peer: PeerApproval, grant: AndroidStorageGrant) = synchronized(gate) {
        check(isCurrent(peer) && grant.peerId == peer.id && grant.trustEpoch == peer.epoch) { "This pairing changed before the folder was approved." }
        val state = clean(shared.state)
        check(state.grants.size < 128 && state.grants.count { it.peerId == peer.id } < 32)
        publish(state.copy(grants = state.grants + grant))
        // Even if revocation raced the disk write, this epoch is never admitted again.
        check(isCurrent(peer)) { "Pairing was revoked while the approval was being saved." }
    }
    internal fun updateGrant(id: String, change: (AndroidStorageGrant) -> AndroidStorageGrant) = synchronized(gate) {
        check(healthy) { issue }
        val state = clean(shared.state); val old = state.grants.single { it.id == id }
        check(approvalCurrent(old)) { "Approve this folder again for the current pairing." }
        val updated = change(old)
        require(updated.id == old.id && updated.peerId == old.peerId && updated.trustEpoch == old.trustEpoch && updated.tree == old.tree)
        publish(state.copy(grants = state.grants.map { if (it.id == id) updated else it }))
    }
    internal fun removeGrant(id: String) = synchronized(gate) { check(healthy) { issue }; publish(clean(shared.state).let { it.copy(grants = it.grants.filterNot { g -> g.id == id }) }) }
    fun reset() = synchronized(gate) {
        check(!shared.invalidated)
        shared.recoveryGeneration=StorageRules.token()
        shared.state.peers.values.forEach { shared.blocked.add(it.epoch) }; shared.pending.getAndSet(null)?.let { shared.blocked.add(it.epoch) }
        file.write(encode(State()).toString()); shared.approvalRevision++; shared.state = State()
        synchronized(shared.intentGate) { writeIntents(emptySet()); shared.intents = emptySet() }
        shared.issue = ""
    }
    private fun clean(state: State): State {
        val peers = state.peers.filterValues { it.epoch !in shared.blocked }
        val grants = state.grants.filter { it.trustEpoch.isEmpty() || it.trustEpoch !in shared.blocked }
        return state.copy(peers = peers, selected = state.selected?.takeIf { it in peers }, grants = grants)
    }
    private fun publish(state: State) {
        check(healthy) { issue }; file.write(encode(state).toString()); shared.approvalRevision++; shared.state = state
    }
    private fun load() {
        shared.loaded = true
        try {
            if (journal.exists()) {
                val root = JSONObject(journal.read()); require(root.getInt("schema") == 1)
                if (root.getString("identity") == ownId) {
                    val rows = root.getJSONArray("epochs"); require(rows.length() <= 512)
                    shared.intents = (0 until rows.length()).map {
                        val row = rows.getJSONObject(it); RevokedApproval(row.getString("peer"), row.getString("epoch")).also { item ->
                            require(item.id.matches(Regex("[a-f0-9]{64}")) && Rules.tokenPattern.matches(item.epoch))
                        }
                    }.toSet()
                    shared.intents.forEach { shared.blocked.add(it.epoch) }
                }
            }
            if (file.exists()) {
                val root = JSONObject(file.read()); require(root.getInt("schema") == 1)
                val grants = readGrants(root.getJSONArray("grants"))
                if (root.getString("identity") != ownId) {
                    val reset = State(grants = grants.map { it.copy(enabled = false, trustEpoch = "") }, notice = "identity-changed")
                    file.write(encode(reset).toString()); shared.state = reset; return
                }
                val rows = root.getJSONArray("peers"); require(rows.length() <= 128)
                val peers = (0 until rows.length()).map { index ->
                    val row = rows.getJSONObject(index)
                    PeerApproval(row.getString("id"), row.getString("key"), row.getString("name"), row.getString("epoch")).also {
                        require(it.key.length == 122 && Rules.b64(it.publicKey()) == it.key && Rules.id(it.publicKey()) == it.id &&
                            it.id != ownId && Rules.tokenPattern.matches(it.epoch)); validateName(it.name)
                    }
                }
                require(peers.map { it.id }.distinct().size == peers.size)
                val selected = root.optString("selected").takeIf { it.isNotEmpty() }; require(selected == null || peers.any { it.id == selected })
                shared.state = clean(State(peers.associateBy { it.id }, selected, grants, root.optString("notice").also { require(it in setOf("", "legacy", "identity-changed")) }))
            } else {
                val legacy = PrivateJsonFile(File(context.filesDir, "storage-grants.json"), 1024 * 1024)
                val text = if (legacy.exists()) legacy.read() else context.getSharedPreferences("iconnect.storage", 0).getString("grants", "[]") ?: "[]"
                val old = readGrants(JSONArray(text), legacy = true).map { it.copy(enabled = false, trustEpoch = "") }
                val hadPeer = context.getSharedPreferences("iconnect.proof", 0).contains("peer-key")
                val state = State(grants = old, notice = if (hadPeer || old.isNotEmpty()) "legacy" else "")
                file.write(encode(state).toString()); shared.state = state
                context.getSharedPreferences("iconnect.proof", 0).edit().remove("peer-key").remove("peer-name").commit()
            }
        } catch (_: Exception) { shared.issue = "Pairing data could not be loaded. Reset pairing data before connecting."; shared.state = State() }
    }
    private fun writeIntents(items: Set<RevokedApproval>) {
        check(items.size <= 512) { "Revocation cleanup is full. Retry after freeing storage space." }
        val rows = JSONArray(); items.forEach { rows.put(JSONObject().put("peer", it.id).put("epoch", it.epoch)) }
        journal.write(JSONObject().put("schema", 1).put("identity", ownId).put("epochs", rows).toString())
    }
    private fun encode(state: State): JSONObject {
        val peers = JSONArray(); state.peers.values.forEach { peers.put(JSONObject().put("id", it.id).put("key", it.key).put("name", it.name).put("epoch", it.epoch)) }
        val grants = JSONArray(); state.grants.forEach { grants.put(grantJson(it)) }
        return JSONObject().put("schema", 1).put("identity", ownId).put("selected", state.selected ?: "").put("peers", peers).put("grants", grants).put("notice", state.notice)
    }
    companion object {
        private val stores = mutableMapOf<String, Shared>()
        private fun validateName(name: String) { require(name.length in 1..64 && name.none(Character::isISOControl)) }
        private fun readGrants(rows: JSONArray, legacy: Boolean = false): List<AndroidStorageGrant> {
            require(rows.length() <= 128)
            val result = (0 until rows.length()).map { index ->
                val row = rows.getJSONObject(index); val id = row.getString("id"); val peer = row.getString("peer")
                val tree = android.net.Uri.parse(row.getString("tree")); val label = row.getString("label")
                val permissions = integer(row, "permissions").also { require(it in 0..StorageRules.SUPPORTED.toLong()) }.toInt()
                val epoch = if (legacy) "" else row.getString("epoch")
                val expires = integer(row, "expires")
                val enabled = row.get("enabled").also { require(it is Boolean) } as Boolean
                require(Rules.tokenPattern.matches(id) && peer.matches(Regex("[a-f0-9]{64}")) && (epoch.isEmpty() || Rules.tokenPattern.matches(epoch)) &&
                    permissions and StorageRules.SUPPORTED == permissions && expires in 0..StorageRules.MAX_TIMESTAMP)
                AndroidSafAccess.validateTree(tree); StorageRules.name(label)
                val excluded = row.getJSONArray("exclusions"); require(excluded.length() <= 128)
                val names = (0 until excluded.length()).map { excluded.getString(it).also(StorageRules::name).lowercase(java.util.Locale.ROOT) }.toSet()
                AndroidStorageGrant(id, peer, tree, label, permissions, names, expires, enabled, epoch)
            }
            require(result.map { it.id }.distinct().size == result.size)
            return result
        }
        private fun integer(row: JSONObject, key: String): Long {
            val value = row.get(key); require(value is Int || value is Long) { "Invalid stored policy number." }
            return (value as Number).toLong()
        }
        private fun grantJson(g: AndroidStorageGrant) = JSONObject().put("id", g.id).put("peer", g.peerId).put("tree", g.tree.toString()).put("label", g.label)
            .put("permissions", g.permissions).put("exclusions", JSONArray(g.exclusions.toList())).put("expires", g.expiresAt).put("enabled", g.enabled).put("epoch", g.trustEpoch)
    }
}
