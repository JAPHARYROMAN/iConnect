package com.iconnect.proof

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.provider.DocumentsContract as Docs
import androidx.core.net.toUri
import com.iconnect.protocol.FileStatus
import org.json.JSONArray
import org.json.JSONObject
import java.io.File

data class LocalDocumentLocation(val id: String, val tree: Uri, val label: String)

/** Independent local shortcuts. No pairing, remote grants, or transfer authority are created. */
internal class LocalDocumentLocations(private val context: Context) : DocumentAccessPolicy {
    private val access = AndroidSafAccess(context)
    private val gate = Any()
    private val file = PrivateJsonFile(File(context.filesDir, "local-locations.json"), 256 * 1024)
    private val owner = Rules.hex(java.security.SecureRandom().generateSeed(32))
    private var locations = emptyList<LocalDocumentLocation>()
    var issue = ""; private set
    init { synchronized(gate) { try {
        if (file.exists()) {
            val rows = JSONArray(file.read()); require(rows.length() <= 32)
            locations = (0 until rows.length()).map { index -> rows.getJSONObject(index).let {
                val id = it.getString("id"); val tree = it.getString("tree").toUri(); val label = it.getString("label")
                require(Rules.tokenPattern.matches(id)); AndroidSafAccess.validateTree(tree); StorageRules.name(label)
                LocalDocumentLocation(id, tree, label)
            } }
            require(locations.map { it.id }.distinct().size == locations.size && locations.map { it.tree }.distinct().size == locations.size)
        }
        access.reconcile("browser:", locations.associate { "browser:${it.id}" to it.tree }, Intent.FLAG_GRANT_READ_URI_PERMISSION)
    } catch (_: Exception) { locations = emptyList(); issue = "Local folder shortcuts need repair. Reset these shortcuts to start fresh; files and sharing approvals are separate." } } }
    fun all() = synchronized(gate) { locations.toList() }
    fun add(tree: Uri, flags: Int): LocalDocumentLocation {
        AndroidSafAccess.validateTree(tree)
        val root = Docs.buildDocumentUriUsingTree(tree, Docs.getTreeDocumentId(tree))
        val label = context.contentResolver.query(root, arrayOf(Docs.Document.COLUMN_DISPLAY_NAME, Docs.Document.COLUMN_MIME_TYPE), null, null, null)?.use {
            check(it.moveToFirst() && it.getString(1) == Docs.Document.MIME_TYPE_DIR) { "Choose a folder to browse locally." }
            it.getString(0).also(StorageRules::name)
        } ?: error("The local folder is unavailable.")
        return synchronized(gate) {
            check(issue.isEmpty()) { issue }; val old = locations.firstOrNull { it.tree == tree }
            check(old != null || locations.size < 32) { "Remove an old local shortcut first. At most 32 are saved." }
            val row = LocalDocumentLocation(old?.id ?: StorageRules.token(), tree, label)
            access.acquire("browser:${row.id}", tree, flags and Intent.FLAG_GRANT_READ_URI_PERMISSION)
            try { save(locations.filter { it.id != row.id } + row) }
            catch (e: Exception) { if (old == null) runCatching { access.release("browser:${row.id}") }; throw e }
            row
        }
    }
    fun remove(id: String) = synchronized(gate) {
        check(issue.isEmpty()) { issue }; save(locations.filter { it.id != id }); access.release("browser:$id")
    }
    fun reset() = synchronized(gate) {
        check(issue.isNotEmpty()) { "Remove individual local shortcuts instead." }
        save(emptyList()); access.reconcile("browser:", emptyMap(), Intent.FLAG_GRANT_READ_URI_PERMISSION); issue = ""
    }
    private fun save(updated: List<LocalDocumentLocation>) {
        val rows = JSONArray(); updated.forEach { rows.put(JSONObject().put("id", it.id).put("tree", it.tree.toString()).put("label", it.label)) }
        file.write(rows.toString()); locations = updated
    }
    override fun <T> authorized(action: () -> T): T = synchronized(gate) { check(issue.isEmpty()) { issue }; action() }
    override fun forPeer(peer: String) = if (peer == owner) all().map { AndroidStorageGrant(it.id, owner, it.tree, it.label) } else emptyList()
    override fun get(id: String, peer: String) = forPeer(peer).singleOrNull { it.id == id }
        ?: throw StorageFailure(FileStatus.ACCESS_DENIED, "This local shortcut was removed.")
    override fun visible(grant: AndroidStorageGrant, name: String) = runCatching { StorageRules.name(name); true }.getOrDefault(false)
    fun ownerId() = owner
}
