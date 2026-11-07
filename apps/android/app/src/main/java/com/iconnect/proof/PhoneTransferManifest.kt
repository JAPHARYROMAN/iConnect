package com.iconnect.proof

import com.iconnect.protocol.*
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import org.json.JSONArray
import org.json.JSONObject

data class PhoneTransferItem(val parent: Int, val grant: String, val path: List<String>, val name: String,
    val directory: Boolean, val size: Long, val version: String, val error: String = "")
data class PhoneTransferManifest(val items: List<PhoneTransferItem>) {
    val bytes get() = items.filter { !it.directory && it.error.isEmpty() }.sumOf { it.size }
    val files get() = items.count { !it.directory }
}
data class PhoneTransferSelection(val entry: FileEntry, val path: List<String>)

object PhoneTransferManifests {
    const val MAX_ITEMS = 4096
    private val hash = Regex("[a-f0-9]{64}")
    suspend fun checked(request: suspend (FileRequest) -> FileResponse, value: FileRequest): FileResponse {
        currentCoroutineContext().ensureActive()
        val response = request(value.toBuilder().setRequestId(StorageRules.token()).build())
        validateResponse(response)
        if (response.status != FileStatus.FILE_STATUS_OK) throw StorageFailure(response.status, response.message.ifEmpty { "The source denied this operation." })
        return response
    }
    fun validateResponse(response: FileResponse) {
        if(response.hasBatch()) UploadBatchRules.validateReceipt(response.batch)
        if(response.hasBatchItem()) UploadBatchRules.validateItemReceipt(response.batchItem)
        if(response.hasUpload()) UploadRules.validate(response.upload)
        if(response.hasPublishedUploadCheck()) require(
            UploadBatchRules.canonicalToken(response.publishedUploadCheck.challenge) && response.hasUpload() &&
                response.upload.phase==UploadPhase.UPLOAD_COMPLETED)
        require(response.uploadsCount<=32); response.uploadsList.forEach(UploadRules::validate)
        require((response.mutationEpoch.isEmpty() || Rules.tokenPattern.matches(response.mutationEpoch)) && response.mutationsCount <= 32)
        if (response.hasMutation()) MutationRules.validate(response.mutation)
        response.mutationsList.forEach(MutationRules::validate)
        require(response.deletedItemsCount <= 32)
        response.deletedItemsList.forEach(RecoveryRules::validate)
        require(Rules.tokenPattern.matches(response.requestId) && response.entriesCount <= 32 && response.content.size() <= StorageRules.RANGE &&
            response.status != FileStatus.UNRECOGNIZED && response.message.length <= 512 && response.message.none(Character::isISOControl) &&
            (response.continuation.isEmpty() || Rules.tokenPattern.matches(response.continuation)))
        response.entriesList.forEach {
            require(Rules.tokenPattern.matches(it.id) && Rules.tokenPattern.matches(it.grantId) && it.name.length in 1..255 && it.name.none(Character::isISOControl) &&
                it.size >= 0 && it.modifiedAt in StorageRules.MIN_TIMESTAMP..StorageRules.MAX_TIMESTAMP && hash.matches(it.version) &&
                (it.parentId.isEmpty() || Rules.tokenPattern.matches(it.parentId)) && it.permissions and 255 == it.permissions &&
                (!it.isRoot || it.isDirectory && it.parentId.isEmpty()))
        }
        if (response.hasDownload()) {
            val info = response.download
            require(Rules.tokenPattern.matches(info.id) && info.size in 0..StorageRules.MAX_TRANSFER_SIZE && hash.matches(info.version) &&
                info.expiresAt in 0..StorageRules.MAX_TIMESTAMP && info.preparedBytes in 0..info.size && info.state != DownloadState.UNRECOGNIZED &&
                (if (info.state == DownloadState.DOWNLOAD_READY) info.sha256.size() == 32 else info.sha256.isEmpty))
            StorageRules.name(info.name)
        }
    }
    suspend fun list(request: suspend (FileRequest) -> FileResponse, directory: FileEntry): List<FileEntry> {
        val result = mutableListOf<FileEntry>(); val names = mutableSetOf<String>(); val ids = mutableSetOf<String>()
        var continuation = ""
        repeat(256) {
            val response = checked(request, FileRequest.newBuilder().setOperation(FileOperation.LIST_DIRECTORY).setNodeId(directory.id).setContinuation(continuation).build())
            response.entriesList.forEach { entry ->
                require(!entry.isRoot && entry.parentId == directory.id && entry.grantId == directory.grantId &&
                    names.add(entry.name.uppercase(java.util.Locale.ROOT)) && ids.add(entry.id)) { "The source returned ambiguous or unrelated items." }
                result.add(entry); check(result.size <= MAX_ITEMS) { "This directory exceeds the planning limit." }
            }
            continuation = response.continuation
            if (continuation.isEmpty()) return result
        }
        error("The source exceeded the directory page limit.")
    }
    suspend fun build(selection: List<PhoneTransferSelection>, request: suspend (FileRequest) -> FileResponse): PhoneTransferManifest {
        require(selection.size in 1..128)
        val result = mutableListOf<PhoneTransferItem>(); val ids = mutableSetOf<String>()
        suspend fun visit(entry: FileEntry, path: List<String>, parent: Int) {
            currentCoroutineContext().ensureActive()
            require(result.size < MAX_ITEMS && path.size <= 32 && path.sumOf { it.length } <= 2048 && ids.add(entry.id)) { "The source tree repeats items or exceeds the planning limits." }
            var error = ""
            try { StorageRules.name(entry.name); path.forEach(StorageRules::name) } catch (_: StorageFailure) { error = "This name cannot be safely represented on the destination." }
            if (entry.permissions and (if (entry.isDirectory) StorageRules.LIST else StorageRules.READ) == 0) error = "This item is not approved for copying."
            if (entry.size > StorageRules.MAX_TRANSFER_SIZE) error = "This item exceeds the supported transfer size."
            val index = result.size
            result.add(PhoneTransferItem(parent, entry.grantId, path.toList(), entry.name, entry.isDirectory, entry.size, entry.version, error))
            if (!entry.isDirectory || error.isNotEmpty()) return
            try {
                list(request, entry).sortedBy { it.name }.forEach { visit(it, path + it.name, index) }
                val after = checked(request, FileRequest.newBuilder().setOperation(FileOperation.GET_METADATA).setNodeId(entry.id).build()).entriesList.single()
                check(after.id == entry.id && after.grantId == entry.grantId && after.isDirectory && after.version == entry.version) { "The folder changed during discovery. Plan it again." }
            } catch (e: StorageFailure) { result[index] = result[index].copy(error = safe(e.message ?: "This folder is unavailable.")) }
        }
        selection.forEach { selected ->
            val entry = checked(request, FileRequest.newBuilder().setOperation(FileOperation.GET_METADATA).setNodeId(selected.entry.id).build()).entriesList.single()
            require(entry.id == selected.entry.id && entry.grantId == selected.entry.grantId && entry.name == selected.entry.name &&
                entry.isDirectory == selected.entry.isDirectory && entry.version == selected.entry.version) { "The selected source changed. Refresh it first." }
            visit(entry, selected.path, -1)
        }
        return PhoneTransferManifest(result.toList()).also(::validate)
    }
    fun validate(manifest: PhoneTransferManifest) {
        require(manifest.items.size in 1..MAX_ITEMS); val names = mutableSetOf<Pair<Int, String>>(); var bytes = 0L
        manifest.items.forEachIndexed { index, item ->
            require(item.parent in -1 until index && item.path.size <= 32 && item.path.sumOf { it.length } <= 2048 &&
                item.path.all { it.length in 1..255 && it.none(Character::isISOControl) } && Rules.tokenPattern.matches(item.grant) &&
                item.name.length in 1..255 && item.name.none(Character::isISOControl) && hash.matches(item.version) && item.size >= 0 &&
                item.error.length <= 512 && item.error.none(Character::isISOControl) && names.add(item.parent to item.name.uppercase(java.util.Locale.ROOT)))
            if (item.parent >= 0) {
                val parent = manifest.items[item.parent]
                require(parent.directory && parent.grant == item.grant && item.path == parent.path + item.name) { "A manifest item escaped its selected folder." }
            } else require(item.path.isEmpty() || item.path.last() == item.name)
            if (item.error.isEmpty()) {
                StorageRules.name(item.name); item.path.forEach(StorageRules::name); require(item.size <= StorageRules.MAX_TRANSFER_SIZE)
                if (!item.directory) { bytes += item.size; require(bytes <= StorageRules.MAX_TRANSFER_SIZE) }
            }
        }
        require(encode(manifest).toString().toByteArray(Charsets.UTF_8).size <= 4 * 1024 * 1024) { "The manifest exceeds its storage limit." }
    }
    fun encode(manifest: PhoneTransferManifest) = JSONArray().apply { manifest.items.forEach { item ->
        put(JSONObject().put("parent", item.parent).put("grant", item.grant).put("path", JSONArray(item.path)).put("name", item.name)
            .put("directory", item.directory).put("size", item.size).put("version", item.version).put("error", item.error))
    } }
    fun decode(rows: JSONArray): PhoneTransferManifest {
        require(rows.length() in 1..MAX_ITEMS)
        return PhoneTransferManifest((0 until rows.length()).map { index ->
            val row = rows.getJSONObject(index); val path = row.getJSONArray("path"); require(path.length() <= 32)
            require(row.get("directory") is Boolean && row.get("size").let { it is Int || it is Long })
            PhoneTransferItem(row.getInt("parent"), row.getString("grant"), (0 until path.length()).map { path.getString(it) }, row.getString("name"),
                row.getBoolean("directory"), row.getLong("size"), row.getString("version"), row.getString("error"))
        }).also(::validate)
    }
    fun safe(message: String) = message.filterNot(Character::isISOControl).take(512)
}

class PhoneManifestResolver(private val request: suspend (FileRequest) -> FileResponse) {
    private val cache = mutableMapOf<Pair<String, List<String>>, FileEntry>()
    suspend fun resolve(item: PhoneTransferItem): FileEntry {
        var path = emptyList<String>()
        var node = cache[item.grant to path] ?: run {
            var continuation = ""; var found: FileEntry? = null; val seen = mutableSetOf<String>()
            for (page in 0 until 256) {
                val roots = PhoneTransferManifests.checked(request, FileRequest.newBuilder().setOperation(FileOperation.LIST_ROOTS).setContinuation(continuation).build())
                require(roots.entriesList.all { it.isRoot && seen.add(it.grantId) }) { "The source returned ambiguous roots." }
                found = roots.entriesList.singleOrNull { it.grantId == item.grant }
                if (found != null || roots.continuation.isEmpty()) break
                continuation = roots.continuation
            }
            (found ?: error("The approved source folder is unavailable. Check permissions and reconnect.")).also { cache[item.grant to path] = it }
        }
        for (name in item.path) {
            if (cache[item.grant to (path + name)] == null) PhoneTransferManifests.list(request, node).forEach { cache[item.grant to (path + it.name)] = it }
            path = path + name; node = cache[item.grant to path] ?: error("A planned item moved or is unavailable.")
        }
        val info = PhoneTransferManifests.checked(request, FileRequest.newBuilder().setOperation(FileOperation.GET_METADATA).setNodeId(node.id).build()).entriesList.single()
        check(info.id == node.id && info.grantId == item.grant && info.name == item.name && info.isDirectory == item.directory &&
            (item.directory || info.size == item.size && info.version == item.version)) { "The planned source changed. Review a new transfer plan." }
        return info
    }
}
