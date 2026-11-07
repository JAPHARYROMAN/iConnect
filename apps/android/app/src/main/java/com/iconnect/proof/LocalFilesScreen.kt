package com.iconnect.proof

import android.app.Activity
import android.content.Intent
import android.provider.DocumentsContract
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.selection.selectable
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.iconnect.protocol.FileEntry
import com.iconnect.protocol.UploadCollision
import kotlinx.coroutines.launch

@Composable
fun LocalFilesScreen(browser: PhoneLocalBrowser, stageUpload:((FileEntry)->Unit)?=null, uploadNotice:String="", stageBatch:((List<FileEntry>,UploadCollision)->Unit)?=null, preparationBusy:Boolean=false, back: () -> Unit) {
    val filePreview by browser.preview.state.collectAsStateWithLifecycle()
    val state by browser.state.collectAsStateWithLifecycle()
    val copies by browser.copies.state.collectAsStateWithLifecycle()
    val localBatches by browser.localBatches.state.collectAsStateWithLifecycle()
    val busyPreparing = preparationBusy || localBatches.planning
    val localScope = rememberCoroutineScope()
    var copySelection by remember { mutableStateOf<FileEntry?>(null) }
    var draftDestination by remember { mutableStateOf<PhoneLocalCopy?>(null) }
    var copyId by rememberSaveable { mutableStateOf("") }
    var copyCollision by rememberSaveable { mutableStateOf(PhoneCollision.KEEP_BOTH.name) }
    var choosingCopy by remember { mutableStateOf(false) }
    var localBatchId by rememberSaveable { mutableStateOf("") }
    var localBatchCollision by rememberSaveable { mutableStateOf(PhoneCollision.KEEP_BOTH.name) }
    var localBatchDestination by remember { mutableStateOf<PhoneLocalBatch?>(null) }
    val localBatchPicker = rememberLauncherForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
        val id = localBatchId; localBatchId = ""
        if (result.resultCode == Activity.RESULT_OK && result.data?.data != null && id.isNotEmpty())
            browser.localBatches.acceptDestination(id, requireNotNull(result.data?.data), requireNotNull(result.data).flags, PhoneCollision.valueOf(localBatchCollision))
    }
    val copyPicker = rememberLauncherForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
        val id = copyId; copyId = ""
        val data = result.data
        if (result.resultCode == Activity.RESULT_OK && data?.data != null && id.isNotEmpty())
            browser.copies.acceptDestination(id, requireNotNull(data.data), data.flags, PhoneCollision.valueOf(copyCollision))
    }
    fun chooseCopyDestination(id: String) {
        copyId = id
        try { copyPicker.launch(Intent(Intent.ACTION_OPEN_DOCUMENT_TREE).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or
            Intent.FLAG_GRANT_WRITE_URI_PERMISSION or Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION or Intent.FLAG_GRANT_PREFIX_URI_PERMISSION)) }
        catch (_: Exception) { copyId = ""; browser.copies.report("The system folder picker is unavailable. The selected copy was kept for review.") }
    }
    val scroll=rememberScrollState()
    LaunchedEffect(state.breadcrumbs.map { it.id },state.query) { scroll.scrollTo(0) }
    var query by rememberSaveable { mutableStateOf("") }
    var removing by remember { mutableStateOf<LocalDocumentLocation?>(null) }
    var reset by remember { mutableStateOf(false) }
    val selected=remember {mutableStateMapOf<String,FileEntry>()}
    var reviewBatch by remember {mutableStateOf<List<FileEntry>?>(null)}
    var collision by remember {mutableStateOf(UploadCollision.UPLOAD_KEEP_BOTH)}
    var leavePreparation by remember {mutableStateOf(false)}
    LaunchedEffect(state.busy) {if(state.busy) selected.clear()}
    fun select(entry:FileEntry) {if(entry.id in selected) selected.remove(entry.id) else if(selected.size<UploadBatchRules.MAX_ROOTS) selected[entry.id]=entry}
    val picker = rememberLauncherForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
        if (result.resultCode == Activity.RESULT_OK) result.data?.let { data -> data.data?.let { browser.add(it, data.flags) } }
    }
    LaunchedEffect(browser) { if (state.healthy) browser.browse() }
    DisposableEffect(browser) { onDispose { browser.cancel() } }
    BackHandler { if(busyPreparing) leavePreparation=true else if (state.breadcrumbs.isEmpty()) back() else browser.browse(state.breadcrumbs.dropLast(1).lastOrNull()) }
    if(leavePreparation) AlertDialog(onDismissRequest={leavePreparation=false},title={Text("Leave private-copy preparation?")},
        text={Text("Leaving this view stops its preparation. Originals stay intact; any private partial copy remains available for review.")},
        confirmButton={TextButton({browser.localBatches.cancelPlanning(); browser.cancel(); leavePreparation=false; back()}) {Text("Stop and leave")}},dismissButton={TextButton({leavePreparation=false}) {Text("Keep preparing")}})
    localBatchDestination?.let { item -> AlertDialog(onDismissRequest = { localBatchDestination = null }, title = { Text("Copy this local file and folder plan?") },
        text = { Column(Modifier.heightIn(max = 400.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text("${item.items} items · ${item.bytes} bytes · ${item.issues} source issues")
            Text("Choose a separate destination with Android's folder picker. Originals stay intact. This frozen selection excludes later-added source items. Each file is staged and verified before its provider save. Existing folders are never merged or replaced.")
            Text("No pairing or sharing approval is created. Cloud-backed providers may use the internet. Copies pause outside the foreground; restarted jobs wait for your action. Retained records keep required provider access.")
            PhoneCollision.entries.forEach { choice -> Row(Modifier.fillMaxWidth().selectable(localBatchCollision == choice.name, role = Role.RadioButton, onClick = { localBatchCollision = choice.name })) {
                RadioButton(localBatchCollision == choice.name, null); Text(when(choice) { PhoneCollision.KEEP_BOTH -> "Keep both using a new name"; PhoneCollision.SKIP -> "Skip a conflicting file or folder branch"; PhoneCollision.FAIL -> "Fail a conflicting file or folder branch" })
            } }
        } }, confirmButton = { TextButton({
            localBatchId = item.id; localBatchDestination = null
            try { localBatchPicker.launch(Intent(Intent.ACTION_OPEN_DOCUMENT_TREE).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION or Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION or Intent.FLAG_GRANT_PREFIX_URI_PERMISSION)) }
            catch (_: Exception) { localBatchId = ""; browser.localBatches.report("The system folder picker is unavailable. The draft was kept.") }
        }) { Text("Choose batch destination…") } }, dismissButton = { TextButton({ localBatchDestination = null }) { Text("Keep batch draft") } }) }
    copySelection?.let { entry -> AlertDialog(onDismissRequest = { if (!choosingCopy) copySelection = null },
        title = { Text("Copy this file on this phone?") }, text = { LocalCopyReviewText(entry.name, entry.size, copyCollision) { copyCollision = it } },
        confirmButton = { TextButton({ choosingCopy = true; localScope.launch {
            try { val id = browser.prepareLocalCopy(entry); copySelection = null; chooseCopyDestination(id) }
            catch (_: Exception) { browser.copies.report("The selected file could not be retained. Refresh the local folder and review copy storage."); copySelection = null }
            finally { choosingCopy = false }
        } }, enabled = !choosingCopy) { Text("Choose destination…") } }, dismissButton = { TextButton({ copySelection = null }, enabled = !choosingCopy) { Text("Cancel") } }) }
    draftDestination?.let { item -> AlertDialog(onDismissRequest = { draftDestination = null },
        title = { Text("Choose a destination for this copy?") },
        text = { LocalCopyReviewText(item.name, item.size, copyCollision) { copyCollision = it } },
        confirmButton = { TextButton({ draftDestination = null; chooseCopyDestination(item.id) }) { Text("Choose destination…") } },
        dismissButton = { TextButton({ draftDestination = null }) { Text("Keep draft") } }) }
    reviewBatch?.let {entries->AlertDialog(onDismissRequest={reviewBatch=null},title={Text("Prepare selected files and folders?")},
        text={Column(Modifier.heightIn(max=400.dp).verticalScroll(rememberScrollState()),verticalArrangement=Arrangement.spacedBy(8.dp)) {
            Text("${entries.size} selected roots. Private copies include accessible nested and empty folders. Originals remain unshared. Keep this view open until preparation finishes.")
            entries.forEach {Text(it.name)}
            Text("If a receiving name already exists:")
            listOf(UploadCollision.UPLOAD_KEEP_BOTH,UploadCollision.UPLOAD_SKIP,UploadCollision.UPLOAD_FAIL).forEach {choice->Row(Modifier.fillMaxWidth().selectable(collision==choice,role=Role.RadioButton,onClick={collision=choice})) {RadioButton(collision==choice,null); Text(batchCollision(choice))}}
        }},confirmButton={TextButton({stageBatch?.invoke(entries,collision); selected.clear(); reviewBatch=null}) {Text("Prepare private batch")}},dismissButton={TextButton({reviewBatch=null}) {Text("Cancel")}})}
    removing?.let { location -> AlertDialog(onDismissRequest = { removing = null }, title = { Text("Remove local shortcut?") },
        text = { Text("This removes only the local shortcut. Files, sharing approvals and access still needed by retained copies or downloads are kept.") },
        confirmButton = { TextButton({ browser.remove(location.id); removing = null }) { Text("Remove shortcut") } },
        dismissButton = { TextButton({ removing = null }) { Text("Cancel") } }) }
    if (reset) AlertDialog(onDismissRequest = { reset = false }, title = { Text("Reset local shortcuts?") },
        text = { Text("This resets only local folder shortcuts and their unused Android access. Files, sharing approvals and retained copy/download records are kept.") },
        confirmButton = { TextButton({ browser.reset(); reset = false }) { Text("Reset shortcuts") } },
        dismissButton = { TextButton({ reset = false }) { Text("Cancel") } })
    state.details?.let { entry -> AlertDialog(onDismissRequest = browser::dismissDetails, title = { Text("Local item details") },
        text = { Column(verticalArrangement = Arrangement.spacedBy(8.dp)) { Text(entry.name); Text(if (entry.isDirectory) "Folder" else "${entry.size} bytes")
            Text("Modified: ${java.time.Instant.ofEpochMilli(entry.modifiedAt)}"); Text("This local view does not change device sharing approvals.") } },
        confirmButton = { TextButton(browser::dismissDetails) { Text("Done") } }) }
    Surface(Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
        Column(Modifier.fillMaxSize().windowInsetsPadding(WindowInsets.safeDrawing).verticalScroll(scroll).padding(20.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp)) {
            TextButton({if(busyPreparing) leavePreparation=true else back()}) { Text("Back to devices") }
            Text("On this phone", style = MaterialTheme.typography.headlineMedium)
            Text("Browse chosen storage providers without a computer connection. Offline access depends on the provider. Local shortcuts do not create sharing approvals.")
            if(stageUpload!=null) Text("Prepare a private copy of a selected file to send. Keep this view open until preparation finishes, then return to Devices and choose an approved computer destination.")
            if(uploadNotice.isNotEmpty()) Text(uploadNotice)
            LocalCopiesPanel(copies, browser.copies) { id -> draftDestination = copies.items.single { it.id == id } }
            LocalBatchesPanel(localBatches, browser.localBatches) { localBatchDestination = it }
            LocalFoldersPanel(browser,state.breadcrumbs.isNotEmpty() && !state.busy && !busyPreparing)
            if (state.error.isNotEmpty()) Text(state.error, color = MaterialTheme.colorScheme.error)
            if (!state.healthy) { Button({ reset = true }, enabled = !state.busy) { Text("Reset local shortcuts…") }; return@Column }
            Button({ picker.launch(Intent(Intent.ACTION_OPEN_DOCUMENT_TREE).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or
                Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION or Intent.FLAG_GRANT_PREFIX_URI_PERMISSION)) }, enabled = !state.busy && !busyPreparing) { Text("Choose local folder…") }
            run {
                Text("${selected.size} selected for a folder/file batch · maximum 128 roots")
                TextButton({ val captured = selected.values.toList(); localScope.launch {
                    try { browser.prepareLocalBatch(captured); selected.clear() }
                    catch (_: Exception) { browser.localBatches.report("The selected local batch could not be planned. Refresh sources and review private storage.") }
                } }, enabled = selected.isNotEmpty() && !state.busy && !busyPreparing) { Text("Plan local file and folder copy…") }
                if(stageBatch!=null) TextButton({reviewBatch=selected.values.toList()},enabled=selected.isNotEmpty() && !state.busy && !busyPreparing) {Text("Review selected batch…")}
                if(selected.isNotEmpty()) TextButton({selected.clear()},enabled=!busyPreparing) {Text("Clear batch selection")}
            }
            Row { TextButton({ browser.browse() }, enabled = !state.busy && !busyPreparing) { Text("Local folders") }
                TextButton({ browser.browse(state.breadcrumbs.lastOrNull()) }, enabled = !state.busy && !busyPreparing) { Text("Refresh") } }
            state.breadcrumbs.forEach { entry -> TextButton({ browser.browse(entry) }, enabled = !state.busy && !busyPreparing) { Text(entry.name) } }
            if (state.busy) { LinearProgressIndicator(Modifier.fillMaxWidth()); TextButton(browser::cancel) { Text("Cancel local discovery") } }
            if (state.notice.isNotEmpty()) Text(state.notice, color = MaterialTheme.colorScheme.error)
            if (state.breadcrumbs.isEmpty()) {
                if (state.locations.isEmpty() && !state.busy) Text("Choose a folder to inspect its files. No local folders are saved yet.")
                state.locations.forEach { location ->
                    val root = state.rows.firstOrNull { it.entry.grantId == location.id }?.entry
                    Card(Modifier.fillMaxWidth()) { Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                        Text(location.label, style = MaterialTheme.typography.titleMedium)
                        Text(if (root == null) "Unavailable. Refresh or choose this folder again to restore Android access." else "Local shortcut · not a sharing approval")
                        if(root!=null) TextButton({select(root)},enabled=!state.busy && !busyPreparing && (root.id in selected || selected.size<128)) {Text(if(root.id in selected) "Deselect folder for batch: ${root.name}" else "Select folder for batch: ${root.name}")}
                        Row { TextButton({ root?.let(browser::browse) }, enabled = root != null && !state.busy && !busyPreparing) { Text("Open local folder") }
                            TextButton({ removing = location }, enabled = !state.busy && !busyPreparing) { Text("Remove shortcut…") } }
                    } }
                }; return@Column
            }
            OutlinedTextField(query, { query = it.take(128) }, label = { Text("Search local filenames in this folder") }, enabled = !state.busy && !busyPreparing, modifier = Modifier.fillMaxWidth())
            Row { TextButton({ browser.search(query) }, enabled = query.isNotBlank() && !state.busy && !busyPreparing) { Text("Search local scope") }
                TextButton({ browser.browse(state.breadcrumbs.last()) }, enabled = !state.busy && !busyPreparing) { Text("Clear search") } }
            TextButton({ browser.sort(PhoneExplorerSort.entries[(state.sort.ordinal + 1) % PhoneExplorerSort.entries.size], state.descending) }) { Text("Sort: ${state.sort.name.lowercase()}") }
            Row { TextButton({ browser.sort(state.sort, !state.descending) }) { Text(if (state.descending) "Descending" else "Ascending") }
                TextButton({ browser.layout(if (state.layout == PhoneExplorerLayout.LIST) PhoneExplorerLayout.GRID else PhoneExplorerLayout.LIST) }) { Text(if (state.layout == PhoneExplorerLayout.LIST) "Grid view" else "List view") } }
            Text("${state.count} loaded local items · page ${state.page + 1} / ${state.pages}")
            if (state.count == 0 && !state.busy) Text(if (state.notice.isNotEmpty()) "Discovery was incomplete. Review the notice above." else if (state.query.isEmpty()) "No accessible items in this local folder." else "No loaded filenames match this query.")
            state.rows.chunked(if (state.layout == PhoneExplorerLayout.GRID) 2 else 1).forEach { group -> Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                group.forEach { row -> Card(Modifier.weight(1f)) { Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                    Text(row.entry.name); Text(if (row.entry.isDirectory) "Folder" else "${row.entry.size} bytes · ${row.type}", style = MaterialTheme.typography.bodySmall)
                    TextButton({select(row.entry)},enabled=!state.busy && !busyPreparing && (row.entry.id in selected || selected.size<128)) {Text(if(row.entry.id in selected) "Deselect for batch: ${row.entry.name}" else "Select for batch: ${row.entry.name}")}
                    if (state.query.isNotEmpty()) Text(row.relativePath, style = MaterialTheme.typography.bodySmall)
                    TextButton({ if (row.entry.isDirectory) browser.browse(row.entry) else browser.inspect(row.entry, true) }, enabled = !state.busy && !busyPreparing) { Text(if (row.entry.isDirectory) "Open folder" else "Preview") }
                    TextButton({ browser.inspect(row.entry, false) }, enabled = !state.busy && !busyPreparing) { Text("Details") }
                    if (!row.entry.isDirectory && row.entry.permissions and StorageRules.READ != 0) TextButton({ copySelection = row.entry },
                        enabled = !state.busy && !busyPreparing && !choosingCopy && copies.issues.isEmpty()) { Text("Copy on this phone…") }
                    if(!row.entry.isDirectory && stageUpload!=null) TextButton({stageUpload(row.entry)},enabled=!state.busy && !busyPreparing) {Text("Prepare to send to computer")}
                } } }
                if (group.size == 1 && state.layout == PhoneExplorerLayout.GRID) Spacer(Modifier.weight(1f))
            } }
            Row { TextButton({ browser.publish(state.page - 1) }, enabled = state.page > 0 && !state.busy) { Text("Previous page") }
                TextButton({ browser.publish(state.page + 1) }, enabled = state.page + 1 < state.pages && !state.busy) { Text("Next page") } }
            FilePreviewPanel(filePreview,browser.preview::page,browser.preview::clear)
            if (state.preview.isNotEmpty()) { Text("Local text preview · up to 32 KiB", style = MaterialTheme.typography.titleSmall); Text(state.preview) }
        }
    }
}
