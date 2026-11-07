package com.iconnect.proof

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.provider.DocumentsContract
import org.json.JSONArray
import org.json.JSONObject

/** App-local ownership of OS permissions. These leases never authorize a remote peer. */
class AndroidSafAccess(private val context: Context) {
    private data class Lease(val tree: Uri, val flags: Int)
    private val preferences = context.getSharedPreferences("iconnect.saf-access", Context.MODE_PRIVATE)
    private val file = PrivateJsonFile(java.io.File(context.filesDir, "saf-access.json"), 2 * 1024 * 1024)
    internal val gate: Any = synchronized(gates) { gates.getOrPut(context.filesDir.canonicalPath) { Any() } }
    init { synchronized(gate) { if (!file.exists()) migrate() } }
    fun destination(): Uri? = synchronized(gate) { read()[DESTINATION]?.tree }
    fun selectDestination(tree: Uri, offeredFlags: Int) = acquire(DESTINATION, tree, offeredFlags, ALL)
    fun clearDestination() = release(DESTINATION)

    fun acquire(owner: String, tree: Uri, offeredFlags: Int, required: Int = Intent.FLAG_GRANT_READ_URI_PERMISSION) = synchronized(gate) {
        validateTree(tree); require(owner.length in 1..160 && required in 1..ALL)
        val flags = offeredFlags and ALL
        require(flags and required == required) { "The provider did not grant the required folder access." }
        val before = read()
        context.contentResolver.takePersistableUriPermission(tree, flags)
        try { replace(before, owner, Lease(tree, flags)) }
        catch (e: Exception) { trim(tree, before); throw e }
    }
    /** Journal recovery retains ownership without manufacturing a missing OS permission. */
    fun retainRecorded(owner: String, tree: Uri, flags: Int = ALL) = synchronized(gate) {
        validateTree(tree); require(owner.length in 1..160 && flags in 1..ALL)
        replace(read(), owner, Lease(tree, flags))
    }
    fun release(owner: String) = synchronized(gate) {
        val before = read(); val old = before[owner] ?: return@synchronized
        val after = before - owner; save(after); trim(old.tree, after)
    }
    fun reconcile(prefix: String, expected: Map<String, Uri>, defaultFlags: Int = ALL) = synchronized(gate) {
        require(defaultFlags in 1..ALL && expected.keys.all { it.startsWith(prefix) })
        val before = read(); val after = before.filterKeys { !it.startsWith(prefix) }.toMutableMap()
        expected.forEach { (owner, tree) -> validateTree(tree); after[owner] = before[owner]?.takeIf { it.tree == tree } ?: Lease(tree, defaultFlags) }
        save(after)
        before.filterKeys { it.startsWith(prefix) }.values.map { it.tree }.distinct().forEach { trim(it, after) }
    }
    private fun replace(before: Map<String, Lease>, owner: String, lease: Lease) {
        val after = before + (owner to lease); save(after)
        before[owner]?.tree?.let { trim(it, after) }
    }
    private fun read(): Map<String, Lease> {
        val text = file.read()
        check(text.length <= 2 * 1024 * 1024) { "Folder-access records need repair." }
        val rows = JSONArray(text); check(rows.length() <= 1024)
        val result = mutableMapOf<String, Lease>()
        repeat(rows.length()) {
            val row = rows.getJSONObject(it); val owner = row.getString("owner"); val tree = Uri.parse(row.getString("tree")); val flags = row.getInt("flags")
            require(owner.length in 1..160 && owner !in result && flags in 1..ALL); validateTree(tree)
            result[owner] = Lease(tree, flags)
        }
        return result
    }
    private fun save(leases: Map<String, Lease>) {
        check(leases.size <= 1024) { "Remove an old folder approval or download record first." }
        val rows = JSONArray(); leases.forEach { (owner, lease) -> rows.put(JSONObject().put("owner", owner).put("tree", lease.tree.toString()).put("flags", lease.flags)) }
        val text = rows.toString(); check(text.length <= 2 * 1024 * 1024)
        file.write(text)
    }
    private fun trim(tree: Uri, leases: Map<String, Lease>) {
        val retained = leases.values.filter { it.tree == tree }.fold(0) { flags, lease -> flags or lease.flags }
        val permission = context.contentResolver.persistedUriPermissions.firstOrNull { it.uri == tree } ?: return
        val actual = (if (permission.isReadPermission) Intent.FLAG_GRANT_READ_URI_PERMISSION else 0) or
            (if (permission.isWritePermission) Intent.FLAG_GRANT_WRITE_URI_PERMISSION else 0)
        val unused = actual and retained.inv()
        if (unused != 0) runCatching { context.contentResolver.releasePersistableUriPermission(tree, unused) }
    }
    private fun migrate() {
        if (preferences.getBoolean("migrated", false)) {
            file.write(preferences.getString("leases", "[]") ?: "[]")
            read() // Reject a damaged legacy registry before any permission is released.
            preferences.edit().clear().commit()
            context.getSharedPreferences("iconnect.proof", 0).edit().remove("download-tree").commit()
            return
        }
        val leases = mutableMapOf<String, Lease>()
        val saved = context.getSharedPreferences("iconnect.storage", 0).getString("grants", "[]") ?: "[]"
        // Legacy policy remains authoritative for peer access; migration only retains OS ownership.
        if (saved.length <= 1024 * 1024) runCatching {
            val rows = JSONArray(saved); require(rows.length() <= 128)
            repeat(rows.length()) {
                val row = rows.getJSONObject(it); val id = row.getString("id"); require(Rules.tokenPattern.matches(id))
                val tree = Uri.parse(row.getString("tree")); validateTree(tree)
                val permission = context.contentResolver.persistedUriPermissions.firstOrNull { it.uri == tree }
                val flags = Intent.FLAG_GRANT_READ_URI_PERMISSION or (if (permission?.isWritePermission == true) Intent.FLAG_GRANT_WRITE_URI_PERMISSION else 0)
                leases["share:$id"] = Lease(tree, flags)
            }
        }.onFailure { leases.clear() }
        context.getSharedPreferences("iconnect.proof", 0).getString("download-tree", null)?.let { value ->
            runCatching { val tree = Uri.parse(value); validateTree(tree); leases[DESTINATION] = Lease(tree, ALL) }
        }
        save(leases)
        context.getSharedPreferences("iconnect.proof", 0).edit().remove("download-tree").commit()
    }
    companion object {
        private val gates = mutableMapOf<String, Any>()
        private const val ALL = Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION
        private const val DESTINATION = "destination"
        fun validateTree(tree: Uri) {
            require(tree.scheme == "content" && !tree.authority.isNullOrEmpty() && tree.userInfo == null && tree.query == null && tree.fragment == null &&
                tree.toString().length <= 16_384 && DocumentsContract.isTreeUri(tree) && tree.pathSegments.size == 2 && tree.pathSegments[0] == "tree" &&
                DocumentsContract.getTreeDocumentId(tree).isNotEmpty()) { "Select a folder in Android's system picker." }
        }
    }
}
