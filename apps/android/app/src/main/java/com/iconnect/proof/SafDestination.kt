package com.iconnect.proof

import android.content.Context
import android.net.Uri
import android.os.CancellationSignal
import android.provider.DocumentsContract as Docs

data class SafFolderBinding(val id: String, val name: String)
enum class PhoneCollision { KEEP_BOTH, SKIP, FAIL }

/** A selected OS tree plus exact provider IDs. A child folder never becomes a new picker grant. */
class SafDestination(private val context: Context, val tree: Uri, val parents: List<SafFolderBinding> = emptyList(), private val signal:CancellationSignal?=null) {
    private val resolver = context.contentResolver
    private val rootId = Docs.getTreeDocumentId(tree)
    val parent get() = document(parents.lastOrNull()?.id ?: rootId)
    private fun document(id: String) = Docs.buildDocumentUriUsingTree(tree, id)
    init {
        AndroidSafAccess.validateTree(tree); validateBindings(parents)
        require(parents.none { it.id == rootId })
    }
    fun validate() {
        signal?.throwIfCanceled()
        check(resolver.persistedUriPermissions.any { it.uri == tree && it.isReadPermission && it.isWritePermission }) {
            "Choose this destination again to restore Android access." }
        directory(document(rootId), checkName = false)
        parents.forEachIndexed { index, binding ->
            val uri = document(binding.id)
            check(Docs.isChildDocument(resolver, document(rootId), uri) &&
                Docs.findDocumentPath(resolver, uri)?.path == listOf(rootId) + parents.take(index + 1).map { it.id }) {
                "A destination folder moved outside its recorded parent. Review the destination." }
            check(directory(uri) == binding.name) { "A destination folder was renamed. Review it before starting a new copy." }
        }
        signal?.throwIfCanceled()
    }
    fun child(uri: Uri) {
        validate()
        check(uri.scheme == "content" && uri.authority == tree.authority && uri.toString().length <= 16_384)
        val id = Docs.getDocumentId(uri)
        check(id != rootId && parents.none { it.id == id } && Docs.isChildDocument(resolver, document(rootId), uri) &&
            Docs.findDocumentPath(resolver, uri)?.path == listOf(rootId) + parents.map { it.id } + id) {
            "The provider document moved outside its recorded destination." }
    }
    fun children(maximumItems:Int=65_536, maximumChars:Int=4*1024*1024): Map<String, String> {
        require(maximumItems in 1..65_536 && maximumChars in 1..4*1024*1024)
        validate(); val result = linkedMapOf<String, String>(); var chars=0L
        resolver.query(Docs.buildChildDocumentsUriUsingTree(tree, parents.lastOrNull()?.id ?: rootId),
            arrayOf(Docs.Document.COLUMN_DOCUMENT_ID, Docs.Document.COLUMN_DISPLAY_NAME), null, null, null,signal)?.use {
            while (it.moveToNext()) {
                signal?.throwIfCanceled()
                check(result.size < maximumItems) { "Choose a destination with fewer items." }
                val id=it.getString(0); val name=it.getString(1)
                check(id.length in 1..4096 && id.none(Character::isISOControl) && !result.containsKey(id)) { "The provider returned ambiguous document identities." }
                StorageRules.name(name); chars+=id.length+name.length
                check(chars<=maximumChars) { "The destination listing exceeds its metadata limit." }
                result[id] = name
            }
        } ?: error("The destination could not be listed.")
        validate(); return result
    }
    fun created(uri: Uri, previous: Map<String, String>): String {
        child(uri); check(Docs.getDocumentId(uri) !in previous) { "The provider returned an existing document. It was left untouched." }
        return name(uri)
    }
    fun name(uri: Uri): String = resolver.query(uri, arrayOf(Docs.Document.COLUMN_DISPLAY_NAME), null, null, null,signal)?.use {
        check(it.moveToFirst()); it.getString(0).also(StorageRules::name)
    } ?: error("The provider document is unavailable.")
    fun createdFolder(uri: Uri, previous: Map<String, String>): SafFolderBinding {
        created(uri, previous); val name = directory(uri)
        val binding = SafFolderBinding(Docs.getDocumentId(uri), name)
        check(SafDestination(context, tree, parents + binding,signal).children(1,8192).isEmpty()) { "The provider returned a folder with existing content. It was left untouched." }
        return binding
    }
    private fun directory(uri: Uri, checkName: Boolean = true): String = resolver.query(uri, arrayOf(Docs.Document.COLUMN_MIME_TYPE, Docs.Document.COLUMN_FLAGS,
        Docs.Document.COLUMN_DISPLAY_NAME), null, null, null,signal)?.use {
        check(it.moveToFirst() && it.getString(0) == Docs.Document.MIME_TYPE_DIR && it.getInt(1) and Docs.Document.FLAG_DIR_SUPPORTS_CREATE != 0) {
            "The provider does not support a writable destination folder." }
        if (checkName) it.getString(2).also(StorageRules::name) else ""
    } ?: error("The destination folder is unavailable.")
    companion object {
        fun validateBindings(path: List<SafFolderBinding>) {
            require(path.size <= 32 && path.map { it.id }.distinct().size == path.size && path.sumOf { it.id.length } <= 16_384)
            path.forEach { require(it.id.length in 1..4096 && it.id.none(Character::isISOControl)); StorageRules.name(it.name) }
        }
        fun unusedName(name: String, existing: Collection<String>, folder: Boolean = false): String {
            StorageRules.name(name); val used = existing.map { it.lowercase(java.util.Locale.ROOT) }.toSet()
            if (name.lowercase(java.util.Locale.ROOT) !in used) return name
            val dot = if (folder) -1 else name.lastIndexOf('.')
            val stem = if (dot > 0) name.take(dot) else name; val extension = if (dot > 0) name.drop(dot) else ""
            for (n in 1..10_000) {
                val suffix = " ($n)$extension"; check(suffix.length < 255) { "Choose a shorter destination name." }
                var prefix = stem.take(255 - suffix.length).trimEnd(' ', '.')
                if (prefix.lastOrNull()?.isHighSurrogate() == true) prefix = prefix.dropLast(1)
                val candidate = prefix + suffix; StorageRules.name(candidate)
                if (candidate.lowercase(java.util.Locale.ROOT) !in used) return candidate
            }
            error("No unused destination name is available.")
        }
    }
}
