package com.iconnect.proof

import android.content.Context
import android.net.Uri
import com.iconnect.protocol.*
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update

data class PhoneLocalState(val locations: List<LocalDocumentLocation> = emptyList(), val rows: List<PhoneExplorerRow> = emptyList(),
    val breadcrumbs: List<FileEntry> = emptyList(), val count: Int = 0, val page: Int = 0, val pages: Int = 1,
    val sort: PhoneExplorerSort = PhoneExplorerSort.NAME, val descending: Boolean = false, val layout: PhoneExplorerLayout = PhoneExplorerLayout.LIST,
    val query: String = "", val busy: Boolean = false, val healthy: Boolean = true, val error: String = "", val notice: String = "",
    val preview: String = "", val details: FileEntry? = null)

/** UI-local browsing cannot authorize remote storage or survive leaving the activity. */
class PhoneLocalBrowser(private val context: Context) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private val locations = LocalDocumentLocations(context)
    private val mutable = MutableStateFlow(PhoneLocalState(locations = locations.all(), healthy = locations.issue.isEmpty(), error = locations.issue))
    val state = mutable.asStateFlow()
    val copies = PhoneLocalCopies(context)
    val localBatches = PhoneLocalBatches(context)
    private val openedFolders=runCatching {PhoneLocalFolders(context,{target->locations.issue.isEmpty() && locations.all().any {it.id==target.location && it.tree==target.tree}})}
    val folders=openedFolders.getOrNull()
    val folderIssue=if(openedFolders.isFailure) "Local folder history could not be opened. Existing folders were kept; browsing and copying remain separate. Restore private storage access and restart." else ""
    val preview = PhonePreview(context,scope) { value -> mutable.update { it.copy(preview=value.text,error=if(value.error.isEmpty()) it.error else value.error) } }
    @Volatile private var visible = false
    @Volatile private var closed = false
    @Volatile private var host: AndroidFileService? = null
    private var work: Job? = null
    private var mutation: Job? = null
    private var inspection: Job? = null
    private var pendingFolder: Pair<Uri, Int>? = null
    private var deadline: Job? = null
    @Volatile private var revision = 0
    private var catalog = emptyList<PhoneExplorerRow>()
    fun setVisible(value: Boolean) {
        visible = value
        copies.setVisible(value)
        localBatches.setVisible(value)
        folders?.setVisible(value)
        if (value) {
            mutable.update { it.copy(locations = locations.all(), healthy = locations.issue.isEmpty(), error = locations.issue, busy = mutation?.isActive == true) }
            pendingFolder?.let { pendingFolder = null; add(it.first, it.second) }
        }
        if (!value) {
            preview.clear()
            revision++; work?.cancel(); inspection?.cancel(); deadline?.cancel(); host?.close(); host = null; catalog = emptyList()
            mutable.update { it.copy(rows = emptyList(), breadcrumbs = emptyList(), count = 0, page = 0, pages = 1, query = "", preview = "", details = null, busy = false) }
        }
    }
    private fun service(): AndroidFileService {
        check(visible && !closed) { "Open iConnect to browse local folders." }
        return host ?: AndroidFileService(context, locations as DocumentAccessPolicy, available = { visible && !closed }).also { host = it }
    }
    fun add(tree: Uri, flags: Int) {
        check(!closed); AndroidSafAccess.validateTree(tree)
        if (!visible) { pendingFolder = tree to flags; return }
        mutate { locations.add(tree, flags) }
    }
    fun remove(id: String) = mutate { locations.remove(id) }
    fun reset() = mutate { locations.reset() }
    private fun mutate(action: () -> Unit) {
        check(visible && !closed && mutation?.isActive != true); cancel(); ++revision
        mutable.update { it.copy(busy = true, error = "") }
        mutation = scope.launch {
            try {
                withContext(Dispatchers.IO) { action() }
                mutable.update { it.copy(locations = locations.all(), healthy = true, busy = false) }
                if (visible && !closed) discover(emptyList(), "", false)
            } catch (e: Exception) { if (!closed) mutable.update { it.copy(busy = false, error = e.message ?: "Local folder shortcuts could not be saved.") } }
        }
    }
    fun browse(entry: FileEntry? = null) {
        if (mutation?.isActive == true) return
        val route = if (entry == null) emptyList() else state.value.breadcrumbs.indexOfFirst { it.id == entry.id }.let { index ->
            if (index >= 0) state.value.breadcrumbs.take(index + 1) else catalog.single { it.entry.id == entry.id && it.entry.isDirectory }.route }
        discover(route, "", false)
    }
    fun search(query: String) { check(state.value.breadcrumbs.isNotEmpty()); discover(state.value.breadcrumbs, query, true) }
    private fun discover(route: List<FileEntry>, query: String, search: Boolean) {
        val active = service(); cancel(); val current = ++revision
        mutable.update { it.copy(busy = true, error = "", preview = "", details = null) }
        work = scope.launch {
            try {
                val result = withContext(Dispatchers.IO) {
                    if (search) PhoneExplorerCatalog.search(route, query) { request(active, it) } else PhoneExplorerCatalog.load(route) { request(active, it) }
                }
                if (!visible || current != revision) return@launch
                catalog = PhoneExplorerCatalog.sort(result.rows, state.value.sort, state.value.descending)
                val unavailable = if (route.isEmpty() && catalog.size < locations.all().size) " Some saved folders are unavailable. Choose the folder again to restore Android access." else ""
                mutable.update { it.copy(locations = locations.all(), breadcrumbs = route, query = if (search) query.trim() else "", notice = result.notice + unavailable) }; publish(0)
            } catch (_: CancellationException) { if (visible && current == revision) mutable.update { it.copy(error = "Local discovery canceled. Previous results were kept.") } }
            catch (e: Exception) { if (visible && current == revision) mutable.update { it.copy(error = e.message ?: "The local folder could not be inspected.") } }
            finally { if (current == revision) { deadline?.cancel(); mutable.update { it.copy(busy = false) } } }
        }
        val thisWork = work
        deadline = scope.launch { delay(21_000); if (current == revision && thisWork?.isActive == true) { active.suspendAvailability(); thisWork.cancel() } }
    }
    private suspend fun request(active: AndroidFileService, value: FileRequest): FileResponse {
        check(visible && !closed && host === active) { "Local browsing ended." }
        require(value.operation in setOf(FileOperation.LIST_ROOTS, FileOperation.LIST_DIRECTORY, FileOperation.CLOSE_LISTING, FileOperation.GET_METADATA, FileOperation.READ_RANGE))
        return active.execute(locations.ownerId(), value.toBuilder().setRequestId(StorageRules.token()).build())
    }
    fun cancel() { revision++; preview.clear(); deadline?.cancel(); work?.cancel(); inspection?.cancel(); host?.suspendAvailability(); mutable.update {it.copy(busy=mutation?.isActive==true)} }
    internal fun captureUpload(entry:FileEntry):LocalUploadSource {
        return captureBatch(listOf(entry)).single().also {check(!it.entry.isDirectory)}
    }
    internal suspend fun prepareLocalCopy(entry: FileEntry): String {
        check(visible && !closed && !state.value.busy)
        val selected = catalog.single { it.entry == entry }.entry
        val active = service()
        val selection = withContext(Dispatchers.IO) { active.captureLocalFile(locations.ownerId(), selected) }
        return copies.prepare(selection)
    }
    internal suspend fun captureFolderCreation(name:String):LocalFolderTarget {
        check(visible && !closed && !state.value.busy && folders!=null); StorageRules.name(name)
        val entry=state.value.breadcrumbs.lastOrNull()?:error("Open the folder where the new folder belongs.")
        val active=service(); val current=revision
        val target=withContext(Dispatchers.IO) {active.captureLocalFolder(locations.ownerId(),entry,name)}
        check(visible && !closed && current==revision && host===active); return target
    }
    internal suspend fun prepareLocalBatch(entries: List<FileEntry>): String {
        check(visible && !closed && !state.value.busy); require(entries.size in 1..128 && entries.map { it.id }.distinct().size == entries.size)
        val selected = entries.map { entry -> catalog.single { it.entry == entry }.entry }; val active = service()
        val captured = withContext(Dispatchers.IO) { selected.map { active.captureLocalItem(locations.ownerId(), it) } }
        return localBatches.prepare(captured)
    }
    internal fun captureBatch(entries:List<FileEntry>):List<LocalUploadSource> {
        check(visible && !closed && !state.value.busy)
        require(entries.size in 1..UploadBatchRules.MAX_ROOTS && entries.map {it.id}.distinct().size==entries.size)
        val selected=entries.map {entry->catalog.single {it.entry.id==entry.id}.entry}
        val active=service(); val current=revision
        val allowed={visible && !closed && revision==current && host===active}
        return selected.map {entry->LocalUploadSource(entry,allowed) {value->check(allowed()); request(active,value)}}
    }
    fun sort(sort: PhoneExplorerSort, descending: Boolean) {
        mutable.update { it.copy(sort = sort, descending = descending) }; catalog = PhoneExplorerCatalog.sort(catalog, sort, descending); publish(0)
    }
    fun layout(layout: PhoneExplorerLayout) { mutable.update { it.copy(layout = layout) } }
    fun publish(page: Int) {
        val pages = maxOf(1, (catalog.size + PhoneExplorerCatalog.PAGE - 1) / PhoneExplorerCatalog.PAGE); val selected = page.coerceIn(0, pages - 1)
        mutable.update { it.copy(rows = catalog.drop(selected * PhoneExplorerCatalog.PAGE).take(PhoneExplorerCatalog.PAGE), count = catalog.size, page = selected, pages = pages) }
    }
    fun inspect(entry: FileEntry, text: Boolean) {
        if (state.value.busy || inspection?.isActive == true) return
        val active = service(); val current = revision
        if(text) {
            mutable.update { it.copy(error="") }
            preview.open(entry,{ visible && !closed && revision==current && host===active }) { value -> withContext(Dispatchers.IO) { request(active,value) } }
            return
        }
        mutable.update { it.copy(busy = true, error = "") }
        inspection = scope.launch { try {
            val result = withTimeout(10_000) { request(active, FileRequest.newBuilder().setOperation(FileOperation.GET_METADATA).setNodeId(entry.id).build()) }
            PhoneTransferManifests.validateResponse(result); check(result.status == FileStatus.FILE_STATUS_OK) { result.message }
            if (visible && current == revision && host === active) mutable.update { it.copy(details = result.entriesList.single()) }
        } catch (_: CancellationException) { if (visible && current == revision) mutable.update { it.copy(error = "Local inspection canceled.") } }
        catch (e: Exception) { if (visible && current == revision) mutable.update { it.copy(error = e.message ?: "This local item changed or is unavailable.") } }
        finally { if (visible && current == revision) { deadline?.cancel(); mutable.update { it.copy(busy = false) } } } }
        val thisInspection = inspection
        deadline = scope.launch { delay(10_000); if (current == revision && thisInspection?.isActive == true) { active.suspendAvailability(); thisInspection.cancel() } }
    }
    fun dismissDetails() { mutable.update { it.copy(details = null) } }
    fun close(): Job {
        closed = true; pendingFolder = null; setVisible(false); scope.cancel()
        val saving = mutation
        return CoroutineScope(Dispatchers.IO).launch { saving?.join(); copies.shutdown(); localBatches.shutdown(); folders?.shutdown() }
    }
}
