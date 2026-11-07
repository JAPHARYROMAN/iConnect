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

private data class SenderRecoveryPrompt(val title:String,val message:String,val button:String,val action:suspend ()->Unit)

@Composable
internal fun BatchSenderRecoveryPanel(model:PhoneController,state:ProofState) {
    val blocked=state.transferRecovery.any {it.area=="Batch sending"}
    if(!blocked && state.batchSenderRecoveryArchives.isEmpty()) return
    val scope=rememberCoroutineScope(); var busy by remember {mutableStateOf(false)}; var error by remember {mutableStateOf("")}
    var prompt by remember {mutableStateOf<SenderRecoveryPrompt?>(null)}; var viewing by remember {mutableStateOf<String?>(null)}
    fun run(action:suspend ()->Unit) {if(busy) return; busy=true; error=""; scope.launch {
        try {action()} catch(e:CancellationException) {throw e} catch(e:Exception) {error=e.message?:"The sender store was kept for local review."} finally {busy=false}
    }}
    suspend fun remove(id:String) {while(!model.continueBatchSenderArchiveRemoval(id)) yield()}
    prompt?.let {value->AlertDialog(onDismissRequest={prompt=null},title={Text(value.title)},text={Text(value.message)},
        confirmButton={TextButton({prompt=null; run(value.action)},enabled=!busy) {Text(value.button)}},dismissButton={TextButton({prompt=null}) {Text("Cancel")}})}
    viewing?.let {id->SenderArchiveInventory(model,id) {viewing=null}}
    Card(Modifier.fillMaxWidth()) {Column(Modifier.padding(18.dp),verticalArrangement=Arrangement.spacedBy(10.dp)) {
        Text("Batch sender recovery",style=MaterialTheme.typography.titleLarge)
        Text("Preserve an unreadable sender store before opening a fresh queue. Private snapshots, originals and receiver files stay intact. Lost intents cannot be reconstructed by this action.")
        if(busy) LinearProgressIndicator(Modifier.fillMaxWidth())
        if(error.isNotEmpty()) Text(error,color=MaterialTheme.colorScheme.error)
        if(blocked) TextButton({run {
            val review=model.reviewBatchSenderReset()
            prompt=SenderRecoveryPrompt("Preserve the unreadable sender store?",
                "Preserve ${review.files} files (${review.bytes} bytes), including ${review.unrecognizedFiles} unrecognized files, in a private archive and open an empty sender queue. This loses its resume and stop controls. Receiver copies may already exist. Retained snapshots require explicit additional-copy review. Their files and all receiver files stay intact.",
                "Preserve and reset queue") {model.confirmBatchSenderReset(review)}
        }},enabled=!busy && !state.savingTrust && !state.revocationPending) {Text("Pause batch sends and review sender recovery…")}
        state.batchSenderRecoveryArchives.forEach {archive->
            HorizontalDivider(); Text("Preserved sender store · ${archive.id.take(8)}",style=MaterialTheme.typography.titleMedium)
            Text(if(archive.removing) "Reviewed removal: ${archive.removed} / ${archive.reviewedFiles} files" else "Preserved files remain available for local inspection.")
            TextButton({viewing=archive.id},enabled=!busy && !archive.removing) {Text("Inspect preserved store files…")}
            TextButton({run {
                if(archive.removing) remove(archive.id) else {
                    val review=model.reviewBatchSenderArchiveRemoval(archive.id)
                    prompt=SenderRecoveryPrompt("Permanently remove this preserved sender store?",
                        "Delete the ${review.files} reviewed files (${review.bytes} bytes) in this archive. ${review.unrecognizedFiles} files have unrecognized names; inspect them before deleting. This cannot be undone. The active queue, private snapshots, originals and receiver files stay intact. After restarting iConnect, review the remaining files again.",
                        "Remove reviewed archive files") {if(!model.confirmBatchSenderArchiveRemoval(review)) remove(archive.id)}
                }
            }},enabled=!busy && !state.savingTrust && !state.revocationPending) {Text(if(archive.removing) "Continue reviewed archive removal" else "Review preserved store removal…")}
        }
    }}
}

@Composable
private fun SenderArchiveInventory(model:PhoneController,id:String,dismiss:()->Unit) {
    var page by remember(id) {mutableIntStateOf(0)}; var files by remember(id) {mutableStateOf<List<PhoneSenderArchiveFile>>(emptyList())}
    var loading by remember(id) {mutableStateOf(false)}; var error by remember(id) {mutableStateOf("")}
    LaunchedEffect(id,page) {loading=true; error=""; files=emptyList()
        try {files=model.batchSenderArchiveFiles(id,page*50)} catch(e:CancellationException) {throw e} catch(e:Exception) {error=e.message?:"The archive needs local review."} finally {loading=false}}
    AlertDialog(onDismissRequest=dismiss,title={Text("Preserved sender store files")},text={Column(Modifier.heightIn(max=400.dp).verticalScroll(rememberScrollState()),verticalArrangement=Arrangement.spacedBy(8.dp)) {
        Text("Local inventory only. Archive records are not restored as sending instructions.")
        Text("Page ${page+1}")
        Row(Modifier.fillMaxWidth(),horizontalArrangement=Arrangement.spacedBy(8.dp)) {
            TextButton({page--},enabled=page>0 && !loading,modifier=Modifier.weight(1f)) {Text("Previous archive files")}
            TextButton({page++},enabled=files.size==50 && !loading,modifier=Modifier.weight(1f)) {Text("Next archive files")}
        }
        if(loading) LinearProgressIndicator(Modifier.fillMaxWidth())
        if(error.isNotEmpty()) Text(error,color=MaterialTheme.colorScheme.error)
        if(!loading && files.isEmpty() && error.isEmpty()) Text("No more archive files.")
        files.forEach {Text("${it.name} · ${it.bytes} bytes${if(!it.recognized) " · Unrecognized name" else ""}")}
    }},confirmButton={TextButton(dismiss) {Text("Done")}})
}
