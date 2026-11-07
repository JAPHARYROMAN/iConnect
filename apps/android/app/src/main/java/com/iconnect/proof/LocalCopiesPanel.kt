package com.iconnect.proof

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.launch

internal fun localCopyCollision(value: PhoneCollision) = when (value) {
    PhoneCollision.KEEP_BOTH -> "Keep both files"
    PhoneCollision.SKIP -> "Skip this copy"
    PhoneCollision.FAIL -> "Stop for review"
}

@Composable
internal fun LocalCopyReviewText(name: String, size: Long, collision: String, select: (String) -> Unit) {
    Column(Modifier.heightIn(max = 400.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Text("$name · $size bytes")
        Text("Choose a destination using Android's folder picker. The original stays intact. No pairing or coordinator is required. Offline access depends on your chosen providers; cloud-backed folders may use the internet. Copies pause when the app leaves the foreground.")
        Text("Retained copy records keep their source access. Removing a record releases unused access; canceling an unused draft also releases its access.")
        Text("If a destination name already exists:")
        PhoneCollision.entries.forEach { choice -> Row(Modifier.fillMaxWidth().selectable(collision == choice.name, role = Role.RadioButton,
            onClick = { select(choice.name) })) { RadioButton(collision == choice.name, null); Text(localCopyCollision(choice)) } }
    }
}

@Composable
internal fun LocalCopiesPanel(state: PhoneLocalCopyState, copies: PhoneLocalCopies, destination: (String) -> Unit) {
    val scope = rememberCoroutineScope()
    var review by remember { mutableStateOf<Pair<String, String>?>(null) }
    var busy by remember { mutableStateOf(false) }
    fun perform(action: suspend () -> Unit) { busy = true; scope.launch {
        try { action() } catch (_: Exception) { copies.report("The local copy action could not be confirmed. Review its current progress before retrying.") }
        finally { busy = false }
    } }
    review?.let { (action, id) -> AlertDialog(onDismissRequest = { review = null }, title = { Text(when(action) {
        "cancel" -> "Cancel this local copy?"; "remove" -> "Remove this copy record?"; "another" -> "Save another local copy?"
        "preserve" -> "Preserve damaged local copy progress?"; else -> "Remove preserved copy metadata?"
    }) }, text = { Text(when(action) {
        "cancel" -> "Only this job's private staging is removed. Originals and any provider copy are kept."
        "remove" -> "This removes the saved progress and its unused folder access. Originals and saved provider files are kept."
        "another" -> "Inspect the destination first. An earlier provider copy may exist. A new copy will use an available name and keep existing files."
        "preserve" -> "The damaged metadata is kept separately. Staging, originals and provider copies are preserved. Missing source records require a fresh source selection."
        else -> "This removes only separately preserved progress metadata. Files and active copy records are kept."
    }) }, confirmButton = { TextButton({ review = null; perform { when(action) {
        "cancel" -> copies.cancel(id); "remove" -> copies.remove(id); "another" -> copies.save(id, true)
        "preserve" -> copies.preserve(id); else -> copies.clearPreserved()
    } } }) { Text("Continue") } }, dismissButton = { TextButton({ review = null }) { Text("Keep current state") } }) }
    if (state.items.isEmpty() && state.issues.isEmpty() && state.error.isEmpty() && state.preserved == 0) return
    Card(Modifier.fillMaxWidth()) { Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Text("Copies on this phone", style = MaterialTheme.typography.titleLarge)
        Text("These copies use your chosen Android storage providers without a paired device or coordinator. Cloud-backed providers may use the internet. Private staging and the saved destination are checked before completion. Restarted jobs wait for your action.")
        if (state.error.isNotEmpty()) Text(state.error, color = MaterialTheme.colorScheme.error)
        state.issues.forEach { issue ->
            Text(issue.message)
            TextButton({ review = "preserve" to issue.id }, enabled = issue.canArchive && !busy && state.items.none { it.download?.active == true }) { Text("Review damaged copy progress…") }
        }
        if (state.preserved > 0) {
            Text("${state.preserved} preserved metadata records")
            TextButton({ review = "backups" to "" }, enabled = !busy && state.items.none { it.download?.active == true }) { Text("Remove preserved copy metadata…") }
        }
        state.items.forEach { item ->
            HorizontalDivider(); Text(item.name, style = MaterialTheme.typography.titleMedium)
            val row = item.download
            Text(if (row == null) "${item.size} bytes · choose a destination to start" else "${row.offset} / ${row.size} bytes · ${row.phase.name.lowercase().replace('_', ' ')}")
            row?.let {LocalPayloadStatus(it,state.liveBytes[it.id],state.payloadEstimates[it.id],state.finalizations[it.id])}
            if (row?.error?.isNotEmpty() == true) Text(row.error, color = MaterialTheme.colorScheme.error)
            val enabled = !busy && state.issues.isEmpty()
            when {
                row == null -> TextButton({ destination(item.id) }, enabled = enabled) { Text("Choose destination for: ${item.name}") }
                row.active -> TextButton({ copies.pause(item.id) }, enabled = !busy) { Text("Pause local copy") }
                row.phase in setOf(PhoneDownloadPhase.PAUSED, PhoneDownloadPhase.INTERRUPTED, PhoneDownloadPhase.PREPARING, PhoneDownloadPhase.READY_TO_SAVE) ->
                    TextButton({ perform { copies.resume(item.id) } }, enabled = enabled) { Text("Resume local copy") }
                row.phase == PhoneDownloadPhase.PUBLICATION_INTERRUPTED -> {
                    Text("Inspect the destination. A partial or complete provider file may remain; it is kept untouched.")
                    TextButton({ perform { copies.save(item.id) } }, enabled = enabled) { Text("Verify earlier local copy") }
                    TextButton({ review = "another" to item.id }, enabled = enabled) { Text("Save another local copy…") }
                }
            }
            if (row?.phase in setOf(PhoneDownloadPhase.COMPLETED, PhoneDownloadPhase.CANCELED, PhoneDownloadPhase.SKIPPED))
                TextButton({ review = "remove" to item.id }, enabled = !busy && row?.active != true) { Text("Remove local copy record…") }
            else TextButton({ review = "cancel" to item.id }, enabled = !busy) { Text("Cancel local copy…") }
        }
    } }
}
