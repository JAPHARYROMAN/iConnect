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

@Composable
internal fun BatchPartRecoveryPanel(model:PhoneController,state:ProofState) {
    ReceivingPartRecoveryPanel(model,state,true)
}

@Composable
internal fun IncomingPartRecoveryPanel(model:PhoneController,state:ProofState) {
    ReceivingPartRecoveryPanel(model,state,false)
}

@Composable
private fun ReceivingPartRecoveryPanel(model:PhoneController,state:ProofState,batch:Boolean) {
    val count=if(batch) state.orphanBatchParts else state.orphanIncoming
    val progress=if(batch) state.batchPartRemoval else state.incomingPartRemoval
    val area=if(batch) "Batch plans" else "Incoming copies"
    val label=if(batch) "receiving" else "incoming"
    if(count==0 && progress==null) return
    val scope=rememberCoroutineScope(); var busy by remember {mutableStateOf(false)}; var error by remember {mutableStateOf("")}
    var review by remember {mutableStateOf<PhoneReceivingPartRemovalReview?>(null)}; var inspect by remember {mutableStateOf(false)}
    val enabled=!busy && !state.savingTrust && !state.revocationPending && state.transferRecovery.none {it.area==area}
    fun run(action:suspend ()->Unit) {if(busy) return; busy=true; error=""; scope.launch {
        try {action()} catch(e:CancellationException) {throw e} catch(e:Exception) {error=e.message?:"Private receiving parts were kept for review."} finally {busy=false}
    }}
    suspend fun continueRemoval() {while(!(if(batch) model.continueOrphanBatchParts() else model.continueOrphanIncomingParts())) yield()}
    review?.let {value->AlertDialog(onDismissRequest={review=null},title={Text("Remove reviewed private receiving parts?")},
        text={Text("Permanently remove the reviewed ${value.files} private files (${value.bytes} bytes) that have no current receiving receipt. Archived transfers cannot resume from these parts afterward. Current receiving parts and every provider document, including uncertain or incomplete copies, stay intact. New or changed files require a fresh review. After restarting iConnect, review the remaining files again.")},
        confirmButton={TextButton({review=null; run {if(!(if(batch) model.confirmOrphanBatchParts(value) else model.confirmOrphanIncomingParts(value))) continueRemoval()}},enabled=!busy) {Text("Remove reviewed $label parts")}},
        dismissButton={TextButton({review=null}) {Text("Keep private parts")}})}
    if(inspect) OrphanPartInventory(model,batch) {inspect=false}
    Card(Modifier.fillMaxWidth()) {Column(Modifier.padding(18.dp),verticalArrangement=Arrangement.spacedBy(10.dp)) {
        Text("Private $label-part recovery",style=MaterialTheme.typography.titleLarge)
        Text("$count private files have no current receipt. A filename does not prove that a part belongs to an old transfer. Inspect and review their removal separately; these files are never adopted as receiving instructions.")
        if(state.transferRecovery.any {it.area==area}) Text("Preserve and reset the unreadable receiver history before reviewing unreferenced parts.")
        progress?.let {Text("Reviewed cleanup: ${it.removed} / ${it.reviewedFiles} files")}
        if(busy) LinearProgressIndicator(Modifier.fillMaxWidth())
        if(error.isNotEmpty()) Text(error,color=MaterialTheme.colorScheme.error)
        TextButton({inspect=true},enabled=enabled && progress==null) {Text("Inspect unreferenced $label parts…")}
        TextButton({run {if(progress!=null) continueRemoval() else review=if(batch) model.reviewOrphanBatchParts() else model.reviewOrphanIncomingParts()}},enabled=enabled) {
            Text(if(progress!=null) "Continue reviewed $label-part cleanup" else "Review unreferenced $label-part removal…")
        }
    }}
}

@Composable
private fun OrphanPartInventory(model:PhoneController,batch:Boolean,dismiss:()->Unit) {
    val label=if(batch) "receiving" else "incoming"
    var page by remember {mutableIntStateOf(0)}; var parts by remember {mutableStateOf<List<PhoneUnreferencedReceivingPart>>(emptyList())}
    var loading by remember {mutableStateOf(false)}; var error by remember {mutableStateOf("")}
    LaunchedEffect(page) {loading=true; parts=emptyList(); error=""
        try {parts=if(batch) model.inspectOrphanBatchParts(page*50) else model.inspectOrphanIncomingParts(page*50)} catch(e:CancellationException) {throw e} catch(e:Exception) {error=e.message?:"Private receiving parts need local review."} finally {loading=false}}
    AlertDialog(onDismissRequest=dismiss,title={Text("Unreferenced private $label parts")},text={Column(Modifier.heightIn(max=400.dp).verticalScroll(rememberScrollState()),verticalArrangement=Arrangement.spacedBy(8.dp)) {
        Text("Local inventory only. Current receipts protect their referenced parts from this cleanup."); Text("Page ${page+1}")
        Row(Modifier.fillMaxWidth(),horizontalArrangement=Arrangement.spacedBy(8.dp)) {
            TextButton({page--},enabled=page>0 && !loading,modifier=Modifier.weight(1f)) {Text("Previous $label parts")}
            TextButton({page++},enabled=parts.size==50 && !loading,modifier=Modifier.weight(1f)) {Text("Next $label parts")}
        }
        if(loading) LinearProgressIndicator(Modifier.fillMaxWidth())
        if(error.isNotEmpty()) Text(error,color=MaterialTheme.colorScheme.error)
        if(!loading && parts.isEmpty() && error.isEmpty()) Text("No more unreferenced parts.")
        parts.forEach {Text("${it.name} · ${it.bytes} bytes")}
    }},confirmButton={TextButton(dismiss) {Text("Done")}})
}
