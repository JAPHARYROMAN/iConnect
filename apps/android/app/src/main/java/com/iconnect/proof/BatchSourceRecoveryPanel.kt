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
import kotlinx.coroutines.yield

private data class SourceRecoveryPrompt(val title:String,val message:String,val button:String,val action:suspend ()->Unit)

@Composable
internal fun BatchSourceRecoveryPanel(model:PhoneController,state:ProofState) {
    val blocked=state.transferRecovery.any {it.area=="Private batches"}
    if(!blocked && state.batchSourceRecoveryArchives.isEmpty()) return
    val scope=rememberCoroutineScope(); var busy by remember {mutableStateOf(false)}; var error by remember {mutableStateOf("")}
    var prompt by remember {mutableStateOf<SourceRecoveryPrompt?>(null)}; var viewing by remember {mutableStateOf<String?>(null)}
    fun run(action:suspend ()->Unit) {if(busy) return; busy=true; error=""; scope.launch {
        try {action()} catch(e:CancellationException) {throw e} catch(e:Exception) {error=e.message?:"Private snapshots were kept for local review."} finally {busy=false}
    }}
    suspend fun remove(id:String) {while(!model.continuePrivateBatchArchiveRemoval(id)) yield()}
    prompt?.let {value->AlertDialog(onDismissRequest={prompt=null},title={Text(value.title)},text={Text(value.message)},
        confirmButton={TextButton({prompt=null; run(value.action)},enabled=!busy) {Text(value.button)}},dismissButton={TextButton({prompt=null}) {Text("Cancel")}})}
    viewing?.let {id->SourceArchiveInventory(model,id) {viewing=null}}
    Card(Modifier.fillMaxWidth()) {Column(Modifier.padding(18.dp),verticalArrangement=Arrangement.spacedBy(10.dp)) {
        Text("Private batch snapshot recovery",style=MaterialTheme.typography.titleLarge)
        Text("Preserve unreadable private snapshots before opening an empty preparation store. Preserved bytes remain charged to outgoing storage limits. Originals and receiver files stay intact.")
        if(busy) LinearProgressIndicator(Modifier.fillMaxWidth())
        if(error.isNotEmpty()) Text(error,color=MaterialTheme.colorScheme.error)
        if(blocked) TextButton({run {
            val review=model.reviewPrivateBatchStoreReset()
            prompt=SourceRecoveryPrompt("Preserve the unreadable private snapshots?",
                "Preserve ${review.files} files in ${review.directories} folders (${review.bytes} bytes), including ${review.unrecognizedEntries} unrecognized entries. These snapshots will no longer be available for sending or resuming. Retained sender controls, originals and receiver files stay intact. Future private batches will require explicit additional-copy review, including after archive removal. Unreadable ownership cannot assign bytes to a peer, so preserved bytes count against every peer's outgoing quota until separate reviewed cleanup.",
                "Preserve snapshots and reset") {model.confirmPrivateBatchStoreReset(review)}
        }},enabled=!busy && !state.savingTrust && !state.revocationPending) {Text("Pause batch work and review snapshot recovery…")}
        state.batchSourceRecoveryArchives.forEach {archive->
            HorizontalDivider(); Text("Preserved private snapshots · ${archive.id.take(8)}",style=MaterialTheme.typography.titleMedium)
            Text(if(archive.removing) "Reviewed removal: ${archive.removed} / ${archive.reviewedEntries} entries" else "Preserved files remain charged to outgoing storage limits.")
            TextButton({viewing=archive.id},enabled=!busy && !archive.removing) {Text("Inspect preserved snapshot entries…")}
            TextButton({run {
                if(archive.removing) remove(archive.id) else {
                    val review=model.reviewPrivateBatchArchiveRemoval(archive.id)
                    prompt=SourceRecoveryPrompt("Permanently remove these preserved snapshots?",
                        "Delete the reviewed ${review.files} files and ${review.directories} folders (${review.bytes} bytes), including ${review.unrecognizedEntries} unrecognized entries. Inspect them before deleting. This cannot be undone. Current private snapshots, sender controls, originals and receiver files stay intact. After restarting iConnect, review the remaining entries again.",
                        "Remove reviewed snapshots") {if(!model.confirmPrivateBatchArchiveRemoval(review)) remove(archive.id)}
                }
            }},enabled=!busy && !state.savingTrust && !state.revocationPending) {Text(if(archive.removing) "Continue reviewed snapshot removal" else "Review preserved snapshot removal…")}
        }
    }}
}

@Composable
private fun SourceArchiveInventory(model:PhoneController,id:String,dismiss:()->Unit) {
    var page by remember(id) {mutableIntStateOf(0)}; var entries by remember(id) {mutableStateOf<List<PhoneSourceArchiveEntry>>(emptyList())}
    var loading by remember(id) {mutableStateOf(false)}; var error by remember(id) {mutableStateOf("")}
    LaunchedEffect(id,page) {loading=true; error=""; entries=emptyList()
        try {entries=model.privateBatchArchiveEntries(id,page*50)} catch(e:CancellationException) {throw e} catch(e:Exception) {error=e.message?:"The private snapshots need local review."} finally {loading=false}}
    AlertDialog(onDismissRequest=dismiss,title={Text("Preserved private snapshot entries")},text={Column(Modifier.heightIn(max=400.dp).verticalScroll(rememberScrollState()),verticalArrangement=Arrangement.spacedBy(8.dp)) {
        Text("Local inventory only. Preserved records are not restored as sending instructions."); Text("Page ${page+1}")
        Row(Modifier.fillMaxWidth(),horizontalArrangement=Arrangement.spacedBy(8.dp)) {
            TextButton({page--},enabled=page>0 && !loading,modifier=Modifier.weight(1f)) {Text("Previous snapshot entries")}
            TextButton({page++},enabled=entries.size==50 && !loading,modifier=Modifier.weight(1f)) {Text("Next snapshot entries")}
        }
        if(loading) LinearProgressIndicator(Modifier.fillMaxWidth())
        if(error.isNotEmpty()) Text(error,color=MaterialTheme.colorScheme.error)
        if(!loading && entries.isEmpty() && error.isEmpty()) Text("No more archive entries.")
        entries.forEach {Text("${it.path} · ${if(it.directory) "Folder" else "${it.bytes} bytes"}${if(!it.recognized) " · Unrecognized name" else ""}")}
    }},confirmButton={TextButton(dismiss) {Text("Done")}})
}
