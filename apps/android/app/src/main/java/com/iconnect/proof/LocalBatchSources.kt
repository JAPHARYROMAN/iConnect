package com.iconnect.proof

import android.content.Context
import androidx.core.net.toUri
import android.os.CancellationSignal
import android.provider.DocumentsContract as Docs
import com.google.protobuf.ByteString
import com.iconnect.protocol.*
import kotlinx.coroutines.*
import org.json.JSONArray
import org.json.JSONObject

internal data class LocalBatchRoot(val id: String, val tree: String, val document: String, val name: String, val directory: Boolean)
internal data class LocalBatchNode(val id: String, val document: String, val source: PhoneTransferItem)
internal data class LocalBatchPlan(val id: String, val roots: List<LocalBatchRoot>, val nodes: List<LocalBatchNode>, val started: Boolean = false) {
    val manifest get() = PhoneTransferManifest(nodes.map { it.source })
}

internal object LocalBatchSources {
    private val hash = Regex("[a-f0-9]{64}")
    fun encode(plan: LocalBatchPlan) = JSONObject().put("schema", 1).put("id", plan.id).put("started", plan.started)
        .put("roots", JSONArray().apply { plan.roots.forEach { put(JSONObject().put("id", it.id).put("tree", it.tree).put("document", it.document).put("name", it.name).put("directory", it.directory)) } })
        .put("nodes", JSONArray().apply { plan.nodes.forEach { put(JSONObject().put("id", it.id).put("document", it.document)) } })
        .put("manifest", PhoneTransferManifests.encode(plan.manifest))
    fun decode(row: JSONObject): LocalBatchPlan {
        require(row.getInt("schema") == 1 && row.get("started") is Boolean)
        val roots = row.getJSONArray("roots"); val nodes = row.getJSONArray("nodes"); val manifest = PhoneTransferManifests.decode(row.getJSONArray("manifest"))
        require(roots.length() in 1..128 && nodes.length() == manifest.items.size)
        return LocalBatchPlan(row.getString("id"), (0 until roots.length()).map { i -> roots.getJSONObject(i).let {
            require(it.get("directory") is Boolean); LocalBatchRoot(it.getString("id"), it.getString("tree"), it.getString("document"), it.getString("name"), it.getBoolean("directory"))
        } }, (0 until nodes.length()).map { i -> nodes.getJSONObject(i).let { LocalBatchNode(it.getString("id"), it.getString("document"), manifest.items[i]) } }, row.getBoolean("started")).also(::validate)
    }
    fun validate(plan: LocalBatchPlan) {
        require(Rules.tokenPattern.matches(plan.id) && plan.roots.size in 1..128 && plan.roots.map { it.id }.distinct().size == plan.roots.size)
        require(plan.roots.map { it.tree }.distinct().size <= 32 && plan.nodes.map { it.id }.distinct().size == plan.nodes.size)
        plan.roots.forEach { require(Rules.tokenPattern.matches(it.id)); AndroidSafAccess.validateTree(it.tree.toUri()); document(it.document); StorageRules.name(it.name) }
        PhoneTransferManifests.validate(plan.manifest); require(plan.manifest.bytes <= 16L * 1024 * 1024 * 1024)
        val roots = plan.roots.associateBy { it.id }; val tops = mutableSetOf<String>(); val documents = mutableSetOf<Pair<String, String>>()
        plan.nodes.forEach { node ->
            require(Rules.tokenPattern.matches(node.id)); document(node.document); val root = roots.getValue(node.source.grant)
            require(documents.add(requireNotNull(root.tree.toUri().authority) to node.document)); require(hash.matches(node.source.version))
            if (node.source.parent < 0) {
                require(tops.add(root.id) && node.document == root.document && node.source.name == root.name && node.source.directory == root.directory)
                require(if (root.directory) node.id == root.id && node.source.path.isEmpty() else node.id != root.id && node.source.path == listOf(root.name))
            } else require(node.id !in roots)
        }
        require(tops == roots.keys)
    }
    private fun document(value: String) { require(value.length in 1..4096 && value.none(Character::isISOControl)) }
    private fun policy(roots: List<LocalBatchRoot>, available: () -> Boolean) = object : DocumentAccessPolicy {
        override fun <T> authorized(action: () -> T): T { check(available()); return action() }
        override fun forPeer(peer: String) = if (peer.isEmpty()) roots.map { AndroidStorageGrant(it.id, "", it.tree.toUri(), it.name) } else emptyList()
        override fun get(id: String, peer: String) = forPeer(peer).singleOrNull { it.id == id } ?: error("This local source is unavailable.")
        override fun visible(grant: AndroidStorageGrant, name: String) = runCatching { StorageRules.name(name); true }.getOrDefault(false)
    }
    suspend fun scan(context: Context, selected: List<LocalCopySelection>, available: () -> Boolean): LocalBatchPlan = withContext(Dispatchers.IO) {
        require(selected.size in 1..128 && selected.map { it.entry.name.uppercase(java.util.Locale.ROOT) }.distinct().size == selected.size)
        val roots = selected.map { LocalBatchRoot(StorageRules.token(), it.tree.toString(), it.document, it.entry.name, it.entry.isDirectory) }
        val signal = CancellationSignal(); val reader = AndroidFileService(context, policy(roots, available)); val nodes = mutableListOf<LocalBatchNode>()
        val seen = mutableSetOf<Pair<String, String>>(); var bytes = 0L
        val watcher = launch(start = CoroutineStart.UNDISPATCHED) { try { awaitCancellation() } finally { signal.cancel(); reader.close() } }
        try {
            suspend fun visit(root: LocalBatchRoot, info: AndroidFileService.Metadata, parent: Int, path: List<String>, id: String) {
                currentCoroutineContext().ensureActive(); check(available()); StorageRules.name(info.name); path.forEach(StorageRules::name)
                check(nodes.size < PhoneTransferManifests.MAX_ITEMS && path.size <= 32 && path.sumOf { it.length } <= 2048 && seen.add(requireNotNull(root.tree.toUri().authority) to info.id)) { "The selection overlaps or exceeds planning limits." }
                val error = if (info.flags and Docs.Document.FLAG_VIRTUAL_DOCUMENT != 0 || !info.directory && !info.knownSize) "This provider item cannot be copied as ordinary bytes." else ""
                if (!info.directory && error.isEmpty()) { bytes += info.size; check(info.size <= StorageRules.MAX_TRANSFER_SIZE && bytes <= 16L * 1024 * 1024 * 1024) }
                val index = nodes.size; nodes.add(LocalBatchNode(id, info.id, PhoneTransferItem(parent, root.id, path, info.name, info.directory, if (info.directory || error.isNotEmpty()) 0 else info.size, info.version, error)))
                if (info.directory && error.isEmpty()) {
                    val children = runInterruptible { reader.localSourceChildren(root.id, info.id, signal) }
                    for (child in children.sortedBy { it.name }) visit(root, child, index, path + child.name, StorageRules.token())
                    val after = runInterruptible { reader.localSourceItem(root.id, info.id, signal) }; check(after.version == info.version && after.name == info.name) { "A folder changed during planning. Refresh and plan again." }
                }
            }
            roots.forEachIndexed { i, root ->
                val info = runInterruptible { reader.localSourceItem(root.id, root.document, signal) }; val entry = selected[i].entry
                check(info.name == entry.name && info.directory == entry.isDirectory && info.version == entry.version && (info.directory || info.size == entry.size)) { "A selected source changed." }
                visit(root, info, -1, if (root.directory) emptyList() else listOf(root.name), if (root.directory) root.id else StorageRules.token())
            }
            LocalBatchPlan(StorageRules.token(), roots, nodes.toList()).also(::validate)
        } finally { watcher.cancel(); signal.cancel(); reader.close() }
    }
    suspend fun <T> read(context: Context, plan: LocalBatchPlan, available: () -> Boolean, action: suspend (suspend (FileRequest) -> FileResponse) -> T): T = coroutineScope {
        val signal = CancellationSignal(); val host = AndroidFileService(context, policy(plan.roots, available)); val nodes = plan.nodes.associateBy { it.id }; val roots = plan.roots.associateBy { it.id }
        val hashes = mutableMapOf<String, ByteArray>(); val cursors = mutableMapOf<String, Pair<String, Int>>()
        val rootCursors = (32 until plan.roots.size step 32).associateBy { StorageRules.token() }
        val worker = currentCoroutineContext()
        val watcher = launch(start = CoroutineStart.UNDISPATCHED) { try { awaitCancellation() } finally { signal.cancel(); host.close() } }
        fun entry(node: LocalBatchNode): FileEntry {
            val root = roots.getValue(node.source.grant); val source = node.source
            return FileEntry.newBuilder().setId(node.id).setGrantId(root.id).setName(source.name).setVersion(source.version).setIsDirectory(source.directory).setSize(source.size)
                .setIsRoot(source.parent < 0 && root.directory).setParentId(if (source.parent < 0) if (root.directory) "" else root.id else plan.nodes[source.parent].id)
                .setPermissions(if (source.error.isEmpty()) StorageRules.READ_ONLY else 0).build()
        }
        fun rootEntry(root: LocalBatchRoot) = if (root.directory) entry(nodes.getValue(root.id)) else FileEntry.newBuilder().setId(root.id).setGrantId(root.id).setName("Local selection")
            .setIsRoot(true).setIsDirectory(true).setVersion(StorageRules.version(root.id, 0, 0)).setPermissions(StorageRules.READ_ONLY).build()
        fun checkNode(node: LocalBatchNode) {
            check(node.source.error.isEmpty()); val info = host.localSourceItem(node.source.grant, node.document, signal)
            check(info.name == node.source.name && info.directory == node.source.directory && (info.directory || info.version == node.source.version && info.size == node.source.size)) { "A planned source changed." }
        }
        try { action { value ->
            worker.ensureActive(); check(available()); val request = value.toBuilder().setRequestId(StorageRules.token()).build()
            runInterruptible(Dispatchers.IO) {
                worker.ensureActive(); check(available()); val result = FileResponse.newBuilder().setRequestId(request.requestId)
                when (request.operation) {
                    FileOperation.LIST_ROOTS -> {
                        val start = if (request.continuation.isEmpty()) 0 else rootCursors.getValue(request.continuation)
                        result.addAllEntries(plan.roots.drop(start).take(32).map(::rootEntry))
                        if (start + result.entriesCount < plan.roots.size) result.continuation = rootCursors.entries.single { it.value == start + result.entriesCount }.key
                    }
                    FileOperation.LIST_DIRECTORY -> {
                        val root = roots[request.nodeId]
                        if (root?.directory == true || root == null) checkNode(nodes.getValue(request.nodeId))
                        val children = plan.nodes.filter { if (it.source.parent >= 0) plan.nodes[it.source.parent].id == request.nodeId else roots.getValue(it.source.grant).let { source -> !source.directory && source.id == request.nodeId } }
                        val start = if (request.continuation.isEmpty()) 0 else cursors.remove(request.continuation)!!.also { require(it.first == request.nodeId) }.second
                        result.addAllEntries(children.drop(start).take(32).map(::entry))
                        if (start + result.entriesCount < children.size) { check(cursors.size < 128); result.continuation = StorageRules.token(); cursors[result.continuation] = request.nodeId to start + result.entriesCount }
                    }
                    FileOperation.CLOSE_LISTING -> if (cursors[request.continuation]?.first == request.nodeId) cursors.remove(request.continuation)
                    FileOperation.GET_METADATA -> { val node = nodes.getValue(request.nodeId); checkNode(node); result.addEntries(entry(node)) }
                    FileOperation.PREPARE_DOWNLOAD, FileOperation.DOWNLOAD_STATUS -> {
                        val node = nodes.getValue(if (request.operation == FileOperation.PREPARE_DOWNLOAD) request.nodeId else request.transferId)
                        check(!node.source.directory && (request.operation != FileOperation.PREPARE_DOWNLOAD || request.expectedVersion == node.source.version)); checkNode(node)
                        val hash = hashes.getOrPut(node.id) { host.hashSource("", node.source.grant, node.document, node.source.version, node.source.size, signal) { worker.ensureActive(); check(available()) } }
                        result.download = DownloadInfo.newBuilder().setId(node.id).setName(node.source.name).setVersion(node.source.version).setSize(node.source.size).setPreparedBytes(node.source.size)
                            .setState(DownloadState.DOWNLOAD_READY).setExpiresAt(StorageRules.MAX_TIMESTAMP).setSha256(ByteString.copyFrom(hash)).build()
                    }
                    FileOperation.DOWNLOAD_READ -> { val node = nodes.getValue(request.transferId); check(hashes.containsKey(node.id)); checkNode(node); return@runInterruptible host.sourceRange("", node.source.grant, node.document, node.source.version, request, signal) }
                    FileOperation.RELEASE_DOWNLOAD -> check(nodes[request.transferId]?.source?.directory == false)
                    else -> error("This local batch operation is unavailable.")
                }; result.build()
            }
        } } finally { watcher.cancel(); signal.cancel(); host.close() }
    }
}
