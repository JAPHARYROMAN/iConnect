package com.iconnect.proof

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch

private data class LocalBatchUiReview(val action: String, val id: String = "", val index: Int = -1)

@Composable
internal fun LocalBatchesPanel(state: PhoneLocalBatchState, copies: PhoneLocalBatches, destination: (PhoneLocalBatch) -> Unit) {
    if (state.items.isEmpty() && state.orphans.isEmpty() && state.issues.isEmpty() && state.preserved == 0 && !state.planning && state.error.isEmpty()) return
    val scope = rememberCoroutineScope(); val pages = remember { mutableStateMapOf<String, Int>() }
    var orphanPage by remember { mutableIntStateOf(0) }; var busy by remember { mutableStateOf(false) }
    var review by remember { mutableStateOf<LocalBatchUiReview?>(null) }
    fun perform(action: suspend () -> Unit) { busy = true; scope.launch {
        try { action() } catch (failure: Exception) { if (failure !is CancellationException) copies.report("The local batch action could not be confirmed. Review current progress before retrying.") }
        finally { busy = false }
    } }
    val idle = !busy && !state.planning && state.items.none { it.batch?.active == true } && state.orphans.none { it.active }
    review?.let { value -> AlertDialog(onDismissRequest = { review = null }, title = { Text(when(value.action) {
        "cancel" -> "Cancel this local batch?"; "remove" -> "Remove this local batch record?"; "missing" -> "Remove references to missing batch progress?"
        "skip" -> "Skip an uncertain provider folder?"; "another" -> "Save another copy of this item?"; "preserve" -> "Preserve damaged local batch metadata?"
        "stop-orphan" -> "Stop this unattached file receipt?"; "remove-orphan" -> "Remove this unattached receipt?"; else -> "Remove preserved batch metadata?"
    }) }, text = { Text(when(value.action) {
        "cancel" -> "This stops unfinished work and removes its identified private staging. Originals, saved files, created folders and uncertain provider copies stay intact."
        "remove" -> "Only progress and its unused provider access are removed. Originals, saved files and created folders remain."
        "missing" -> "Receiving history is missing; earlier copies may exist. This removes only retained source/progress references and their unused provider access. Inspect earlier destinations before selecting sources for another copy."
        "skip" -> "The provider may already have created a folder. It and its contents are kept. This branch will be skipped when you resume."
        "another" -> "Inspect the destination first. An earlier provider copy may exist. Saving another copy uses a new available name and keeps earlier provider objects."
        "preserve" -> "Only identified damaged metadata is moved aside. Originals, private staging and provider copies are kept. Missing source access requires a fresh selection."
        "stop-orphan" -> "This cannot fetch source bytes or resume the missing batch. It removes only this receipt's identified private staging; provider copies remain."
        "remove-orphan" -> "Only this finished or stopped receipt and its unused provider access are removed. Originals and provider copies remain."
        else -> "Only separately preserved metadata is removed. Originals, current progress, private staging and provider copies stay intact."
    }) }, confirmButton = { TextButton({ review = null; perform { when(value.action) {
        "cancel" -> copies.cancel(value.id); "remove" -> copies.remove(value.id); "missing" -> copies.removeMissing(value.id)
        "skip" -> copies.skipUncertainFolder(value.id, value.index); "another" -> copies.resume(value.id, copies.state.value.items.single { it.id == value.id }.batch!!.items[value.index].child)
        "preserve" -> copies.preserve(value.id); "stop-orphan" -> copies.stopOrphan(value.id); "remove-orphan" -> copies.removeOrphan(value.id); else -> copies.clearPreserved()
    } } }) { Text("Confirm local batch action") } }, dismissButton = { TextButton({ review = null }) { Text("Keep current progress") } }) }
    Text("File and folder copies on this phone", style = MaterialTheme.typography.titleLarge)
    Text("Review up to 4,096 frozen items before choosing a destination. No pairing is needed. Copies pause outside the foreground and never restart automatically.")
    if (state.error.isNotEmpty()) Text(state.error, color = MaterialTheme.colorScheme.error)
    if (state.planning) { LinearProgressIndicator(Modifier.fillMaxWidth()); TextButton(copies::cancelPlanning) { Text("Cancel local batch planning") } }
    state.issues.forEach { issue ->
        Text(issue.message, color = MaterialTheme.colorScheme.error)
        TextButton({ review = LocalBatchUiReview("preserve", issue.id) }, enabled = idle && issue.canArchive) { Text("Preserve damaged local batch metadata…") }
    }
    if (state.preserved > 0) TextButton({ review = LocalBatchUiReview("clear") }, enabled = idle) { Text("Remove ${state.preserved} preserved local batch metadata records…") }
    state.items.forEach { item ->
        Card(Modifier.fillMaxWidth()) { Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text(item.label, style = MaterialTheme.typography.titleMedium)
            Text("${item.items} retained items · ${item.bytes} planned bytes · ${item.issues} source issues")
            val batch = item.batch; val pageCount = maxOf(1, (item.items + 19) / 20); val page = (pages[item.id] ?: 0).coerceIn(0, pageCount - 1)
            if (batch == null) {
                copies.page(item.id, page * 20).forEach { source -> Text("${source.name} · ${if (source.directory) "Folder" else "${source.size} bytes"}${source.error.takeIf { it.isNotEmpty() }?.let { " · $it" } ?: ""}") }
                if (!item.started) TextButton({ destination(item) }, enabled = idle && state.issues.isEmpty()) { Text("Review destination for local batch…") }
                else {
                    Text("Receiving progress is missing. Copies may already exist. Review any unattached receipts below before removing these references.")
                    TextButton({ review = LocalBatchUiReview("missing", item.id) }, enabled = idle && state.orphans.none { it.batchId == item.id }) { Text("Review missing local batch progress…") }
                }
            } else {
                Text("${batch.completed} / ${item.items} completed · ${batch.verifiedBytes} verified bytes · ${batch.phase.name.lowercase().replace('_', ' ')}")
                if (batch.error.isNotEmpty()) Text(batch.error, color = MaterialTheme.colorScheme.error)
                val activeFile = if (batch.active) copies.activeChild(item.id) else null
                activeFile?.let { file ->
                    Text("Current file · ${file.name}")
                    LocalPayloadStatus(file,state.liveBytes[file.id],state.payloadEstimates[file.id],state.finalizations[file.id])
                }
                batch.items.drop(page * 20).take(20).forEachIndexed { offset, entry ->
                    val index = page * 20 + offset
                    Text("${entry.source.name} · ${entry.phase.name.lowercase()}${entry.localName.takeIf { it.isNotEmpty() }?.let { " · $it" } ?: ""}")
                    copies.child(entry.child)?.takeIf { it.id != activeFile?.id }?.let {
                        LocalPayloadStatus(it,state.liveBytes[it.id],state.payloadEstimates[it.id],state.finalizations[it.id])
                    }
                    if (entry.error.isNotEmpty()) Text(entry.error)
                    if (entry.source.directory && entry.phase == PhoneBatchItemPhase.CREATING)
                        TextButton({ review = LocalBatchUiReview("skip", item.id, index) }, enabled = idle) { Text("Review uncertain local folder…") }
                    if (copies.child(entry.child)?.phase == PhoneDownloadPhase.PUBLICATION_INTERRUPTED)
                        TextButton({ review = LocalBatchUiReview("another", item.id, index) }, enabled = idle && state.issues.isEmpty()) { Text("Review another copy of this item…") }
                }
                if (batch.active) TextButton({ copies.pause(item.id) }, enabled = !busy) { Text("Pause local folder batch") }
                else if (batch.phase in setOf(PhoneBatchPhase.PAUSED, PhoneBatchPhase.INTERRUPTED, PhoneBatchPhase.REVIEW))
                    TextButton({ perform { copies.resume(item.id) } }, enabled = !busy && state.issues.isEmpty()) { Text("Resume or check local folder batch") }
            }
            Row { TextButton({ pages[item.id] = page - 1 }, enabled = page > 0 && !busy) { Text("Previous local batch items") }
                Text("${page + 1} / $pageCount"); TextButton({ pages[item.id] = page + 1 }, enabled = page + 1 < pageCount && !busy) { Text("Next local batch items") } }
            if (batch == null && !item.started || batch != null && !copies.canRemove(item.id))
                TextButton({ review = LocalBatchUiReview("cancel", item.id) }, enabled = !busy) { Text("Cancel local file/folder batch…") }
            else if (batch != null && copies.canRemove(item.id)) TextButton({ review = LocalBatchUiReview("remove", item.id) }, enabled = idle) { Text("Remove local batch record…") }
        } }
    }
    if (state.orphans.isNotEmpty()) {
        Text("Unattached local batch receipts", style = MaterialTheme.typography.titleMedium)
        Text("A matching parent item is unavailable. These receipts cannot fetch source bytes or start a new provider copy. Review private staging and any recorded provider copy separately.")
        val pageCount = maxOf(1, (state.orphans.size + 19) / 20); val page = orphanPage.coerceIn(0, pageCount - 1)
        state.orphans.drop(page * 20).take(20).forEach { row -> Card(Modifier.fillMaxWidth()) { Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text(row.name); Text("${row.offset} / ${row.size} bytes · ${row.phase.name.lowercase()}"); if (row.error.isNotEmpty()) Text(row.error)
            LocalPayloadStatus(row,state.liveBytes[row.id],state.payloadEstimates[row.id],state.finalizations[row.id])
            if (row.active) TextButton({ copies.pauseOrphan(row.id) }) { Text("Pause local receipt check") }
            else {
                if (row.phase == PhoneDownloadPhase.PUBLICATION_INTERRUPTED && row.published.isNotEmpty())
                    TextButton({ perform { copies.checkOrphan(row.id) } }, enabled = idle) { Text("Check recorded provider copy only") }
                if (row.phase in setOf(PhoneDownloadPhase.COMPLETED, PhoneDownloadPhase.CANCELED, PhoneDownloadPhase.SKIPPED))
                    TextButton({ review = LocalBatchUiReview("remove-orphan", row.id) }, enabled = idle) { Text("Remove unattached local receipt…") }
                else TextButton({ review = LocalBatchUiReview("stop-orphan", row.id) }, enabled = idle) { Text("Stop unattached local receipt…") }
            }
        } } }
        Row { TextButton({ orphanPage = page - 1 }, enabled = page > 0) { Text("Previous local receipts") }; Text("${page + 1} / $pageCount")
            TextButton({ orphanPage = page + 1 }, enabled = page + 1 < pageCount) { Text("Next local receipts") } }
    }
}
