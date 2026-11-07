package com.iconnect.proof

import com.iconnect.protocol.*
import kotlinx.coroutines.*
import java.text.Normalizer
import java.util.Locale

enum class PhoneExplorerSort { NAME, SIZE, MODIFIED, TYPE }
enum class PhoneExplorerLayout { LIST, GRID }
data class PhoneExplorerRow(val entry: FileEntry, val route: List<FileEntry>) {
    val sourcePath get() = route.drop(1).map { it.name }
    val relativePath get() = sourcePath.joinToString(" / ")
    val type get() = if (entry.isDirectory) "Folder" else entry.name.substringAfterLast('.', "").uppercase(Locale.ROOT).ifEmpty { "File" }
}
data class PhoneExplorerSnapshot(val rows: List<PhoneExplorerRow>, val complete: Boolean, val visited: Int, val notice: String = "")

object PhoneExplorerCatalog {
    const val PAGE = 50
    private class Stop : RuntimeException()
    private class Budget {
        var requests = 0; var visited = 0; var bytes = 0; var stopped = false; var notice = ""
        fun stop(reason: String) { stopped = true; notice = reason }
    }
    suspend fun load(route: List<FileEntry>, request: suspend (FileRequest) -> FileResponse): PhoneExplorerSnapshot {
        val budget = Budget(); val rows = mutableListOf<PhoneExplorerRow>()
        try { withTimeout(20_000) {
            if (route.isEmpty()) {
                val roots = send(FileRequest.newBuilder().setOperation(FileOperation.LIST_ROOTS).build(), request, budget)
                val grants = mutableSetOf<String>()
                roots.entriesList.forEach { require(it.isRoot && grants.add(it.grantId)); rows.add(PhoneExplorerRow(it, listOf(it))); budget.visited++ }
                if (roots.continuation.isNotEmpty()) budget.stop("The source returned an incomplete approved-location list.")
            } else { validateRoute(route); readDirectory(route, request, budget) { rows.add(it) } }
        } } catch (_: TimeoutCancellationException) { if (!budget.stopped) budget.stop("Discovery timed out. Results are incomplete; refresh or choose a narrower scope.") }
        catch (_: Stop) { }
        return PhoneExplorerSnapshot(rows, !budget.stopped, budget.visited, budget.notice)
    }
    suspend fun search(scope: List<FileEntry>, query: String, request: suspend (FileRequest) -> FileResponse): PhoneExplorerSnapshot {
        validateRoute(scope); val needle = fold(query.trim())
        require(query.trim().length in 1..128 && query.none(Character::isISOControl)) { "Enter 1–128 visible characters to search filenames." }
        val budget = Budget(); val rows = mutableListOf<PhoneExplorerRow>(); val pending = ArrayDeque<List<FileEntry>>(); pending.add(scope)
        val directories = mutableSetOf<String>(); var unavailable = 0
        try { withTimeout(20_000) {
            while (pending.isNotEmpty() && !budget.stopped) {
                ensureActive(); val route = pending.removeFirst(); require(directories.add(route.last().id)) { "The source repeated a directory reference." }
                try { readDirectory(route, request, budget) { row ->
                    if (fold(row.entry.name).contains(needle)) { rows.add(row); if (rows.size >= 2048) budget.stop("The result limit was reached. Search is incomplete; use a narrower query.") }
                    if (row.entry.isDirectory) { if (row.route.size <= 33) pending.add(row.route) else unavailable++ }
                } } catch (e: StorageFailure) {
                    if (e.status in setOf(FileStatus.ACCESS_DENIED, FileStatus.NOT_FOUND, FileStatus.STALE_REFERENCE, FileStatus.UNSUPPORTED)) unavailable++ else throw e
                }
            }
        } } catch (_: TimeoutCancellationException) { if (!budget.stopped) budget.stop("Search timed out. Results are incomplete; use a narrower scope.") }
        catch (_: Stop) { }
        val notice = budget.notice + if (unavailable == 0) "" else (if (budget.notice.isEmpty()) "" else " ") + "$unavailable folders were unavailable or exceeded the depth limit; results are incomplete."
        return PhoneExplorerSnapshot(rows, !budget.stopped && unavailable == 0, budget.visited, notice)
    }
    private suspend fun readDirectory(original: List<FileEntry>, request: suspend (FileRequest) -> FileResponse, budget: Budget, accept: (PhoneExplorerRow) -> Unit) {
        val old = original.last()
        val directory = send(FileRequest.newBuilder().setOperation(FileOperation.GET_METADATA).setNodeId(old.id).build(), request, budget).entriesList.single()
        require(sameFolder(directory, old)) { "The selected folder changed. Refresh its parent." }
        val route = original.dropLast(1) + directory
        val names = mutableSetOf<String>(); val ids = mutableSetOf<String>(); var continuation = ""
        try {
            for (page in 0 until 256) {
                if (budget.stopped) break
                val response = send(FileRequest.newBuilder().setOperation(FileOperation.LIST_DIRECTORY).setNodeId(directory.id).setContinuation(continuation).build(), request, budget)
                continuation = response.continuation
                for (entry in response.entriesList) {
                    require(!entry.isRoot && entry.parentId == directory.id && entry.grantId == directory.grantId && names.add(entry.name.uppercase(Locale.ROOT)) && ids.add(entry.id)) {
                        "The source returned ambiguous or unrelated directory items." }
                    budget.visited++; if (budget.visited > 8192) { budget.stop("The discovery limit was reached. Results and sorting are limited to loaded items."); break }
                    budget.bytes += entry.serializedSize + route.sumOf { it.serializedSize }
                    if (budget.bytes > 16 * 1024 * 1024) { budget.stop("The metadata limit was reached. Results are incomplete."); break }
                    accept(PhoneExplorerRow(entry, route + entry)); if (budget.stopped) break
                }
                if (continuation.isEmpty()) break
                if (page == 255) budget.stop("This folder exceeded the page limit. Results are incomplete.")
            }
            if (!budget.stopped) {
                val after = send(FileRequest.newBuilder().setOperation(FileOperation.GET_METADATA).setNodeId(directory.id).build(), request, budget).entriesList.single()
                if (!sameFolder(after, directory) || after.version != directory.version)
                    throw StorageFailure(FileStatus.STALE_REFERENCE, "The directory changed during discovery. Refresh it.")
            }
        } finally { if (continuation.isNotEmpty()) withContext(NonCancellable) {
            runCatching { withTimeout(1_000) { request(FileRequest.newBuilder().setOperation(FileOperation.CLOSE_LISTING).setNodeId(directory.id).setContinuation(continuation).build()) } }
        } }
    }
    private suspend fun send(value: FileRequest, request: suspend (FileRequest) -> FileResponse, budget: Budget): FileResponse {
        if (++budget.requests > 2048) { budget.stop("The request limit was reached. Results are incomplete."); throw Stop() }
        return PhoneTransferManifests.checked(request, value)
    }
    fun sort(rows: List<PhoneExplorerRow>, sort: PhoneExplorerSort, descending: Boolean): List<PhoneExplorerRow> {
        val comparer = Comparator<PhoneExplorerRow> { left, right ->
            var order = when (sort) { PhoneExplorerSort.NAME -> fold(left.entry.name).compareTo(fold(right.entry.name)); PhoneExplorerSort.SIZE -> left.entry.size.compareTo(right.entry.size)
                PhoneExplorerSort.MODIFIED -> left.entry.modifiedAt.compareTo(right.entry.modifiedAt); PhoneExplorerSort.TYPE -> fold(left.type).compareTo(fold(right.type)) }
            if (order == 0) order = left.relativePath.compareTo(right.relativePath)
            if (order == 0) order = left.entry.id.compareTo(right.entry.id)
            if (descending) -order else order
        }
        return rows.sortedWith(compareBy<PhoneExplorerRow> { !it.entry.isDirectory }.then(comparer))
    }
    fun normalizeSelection(rows: List<PhoneExplorerRow>) = rows.filter { row -> rows.none { parent ->
        parent.entry.id != row.entry.id && parent.entry.isDirectory && parent.entry.grantId == row.entry.grantId && parent.sourcePath.size < row.sourcePath.size &&
            row.sourcePath.take(parent.sourcePath.size) == parent.sourcePath
    } }
    fun validateRoute(route: List<FileEntry>) {
        require(route.size in 1..33 && route.first().isRoot && route.all { it.isDirectory } && route.map { it.id }.distinct().size == route.size) { "Open an approved folder before searching." }
        route.drop(1).forEachIndexed { index, entry -> require(entry.grantId == route.first().grantId && entry.parentId == route[index].id) { "The folder scope escaped its approved grant." } }
    }
    private fun sameFolder(left: FileEntry, right: FileEntry) = left.id == right.id && left.grantId == right.grantId && left.isDirectory &&
        left.isRoot == right.isRoot && left.parentId == right.parentId && left.name == right.name
    private fun fold(value: String) = Normalizer.normalize(value, Normalizer.Form.NFC).uppercase(Locale.ROOT)
}
