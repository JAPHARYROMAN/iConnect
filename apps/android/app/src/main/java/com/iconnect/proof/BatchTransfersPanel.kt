package com.iconnect.proof

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.iconnect.protocol.*
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch
import kotlinx.coroutines.yield

private data class BatchPrompt(val title:String,val message:String,val confirm:String,val action:suspend ()->Unit)
private data class BatchPlanView(val source:PhoneBatchSource,val manifest:UploadBatchManifest)

/** Local review never grants remote access. Every mutation is checked again by the owner stores. */
@Composable
internal fun BatchTransfersPanel(model:PhoneController,state:ProofState,chooseLocal:()->Unit) {
    val scope=rememberCoroutineScope()
    var busy by remember {mutableStateOf(false)}
    var error by remember {mutableStateOf("")}
    var prompt by remember {mutableStateOf<BatchPrompt?>(null)}
    var plan by remember {mutableStateOf<BatchPlanView?>(null)}
    var results by remember {mutableStateOf<String?>(null)}
    var queuePage by rememberSaveable {mutableIntStateOf(0)}
    var incomingPage by rememberSaveable {mutableIntStateOf(0)}
    val enabled=!busy && !state.savingTrust && !state.revocationPending
    fun run(action:suspend ()->Unit) {if(busy) return; busy=true; error=""; scope.launch {try {action()} catch(e:CancellationException) {throw e}
        catch(e:Exception) {error=e.message?:"This batch needs local review."} finally {busy=false}}}
    suspend fun purge(id:String) {while(!model.continueOutgoingBatchPurge(id)) yield()}
    suspend fun retire(id:String) {while(!model.continueIncomingBatchRetirement(id)) yield()}
    fun history(row:PhoneBatchSend,action:PhoneBatchHistoryAction) =run {
        val review=model.reviewOutgoingBatchHistory(row.id,action)
        val (title,body,button)=when(action) {
            PhoneBatchHistoryAction.ARCHIVE->Triple("Archive this batch send?","This frees a queue slot and keeps its receipts. Private snapshots and receiver files stay intact. Restore this record before checking or stopping its original receiver plan.","Archive send")
            PhoneBatchHistoryAction.RESTORE->Triple("Restore this batch send?","This restores the same request and receiver receipt. It does not send anything. A removed private snapshot prevents further sending; the original receiver plan can still be stopped.","Restore send")
            PhoneBatchHistoryAction.PURGE->Triple("Purge saved batch history?","Remove only this job's local metadata and ${review.results} item receipts. This loses its resume and stop controls. Private snapshots, originals and receiver copies stay intact. Cleanup can be continued if interrupted.","Purge history")
        }
        prompt=BatchPrompt(title,body,button) {if(!model.confirmOutgoingBatchHistory(review)) purge(row.id)}
    }
    fun queue(source:PhoneBatchSource) {
        val another=source.mayHaveReceiverCopies || state.outgoingBatchSends.any {it.source==source.id && it.header.isNotEmpty()}
        prompt=BatchPrompt(if(another) "Queue another batch copy?" else "Queue this private batch?",
            "${source.label.ifEmpty {"Private batch"}} · ${source.items} items · ${source.bytes} bytes. "+
                (if(another) "Earlier receiver copies may already exist. This deliberately creates a new request. " else "")+
                "Nothing is sent until you review a receiving destination. Originals remain intact.",if(another) "Queue another copy" else "Queue batch") {model.queueReviewedBatch(source.id,another)}
    }
    prompt?.let {value->AlertDialog(onDismissRequest={if(!busy) prompt=null},title={Text(value.title)},
        text={Text(value.message)},confirmButton={TextButton({prompt=null; run(value.action)},enabled=!busy) {Text(value.confirm)}},
        dismissButton={TextButton({prompt=null},enabled=!busy) {Text("Cancel")}})}
    plan?.let {value->BatchManifestDialog(value.source,value.manifest,{plan=null},
        queue=if(enabled && value.source.owner==state.deviceId && model.privateBatchCurrent(value.source.peer,value.source.pairing) && state.outgoingBatchSends.none {it.source==value.source.id && !it.archived && it.phase !in setOf(PhoneBatchSendingPhase.STOPPED,PhoneBatchSendingPhase.COMPLETED)})
            {{plan=null; queue(value.source)}} else null)}
    results?.let {id->BatchResultsDialog(model,id,state.outgoingBatchSends.firstOrNull {it.id==id},{results=null})}
    Card(Modifier.fillMaxWidth()) {Column(Modifier.padding(18.dp),verticalArrangement=Arrangement.spacedBy(10.dp)) {
        Text("Send files and folders",style=MaterialTheme.typography.titleLarge)
        Text("Select up to 128 roots in On this phone. Review the private snapshot, then queue it for the paired device. Accessible nested and empty folders are included; unavailable items appear as issues. Keep iConnect open while preparing and sending.")
        Text("Private copies share the outgoing storage quota. They do not create sharing approvals.")
        TextButton(chooseLocal,enabled=enabled) {Text("Choose local files and folders…")}
        if(!state.authenticated) Text("Connect to the paired device to send or check receiver receipts.")
        else if(!state.batchReceiverSupported) Text("The connected device has not enabled compatible folder/file batch receiving.")
        if(busy) LinearProgressIndicator(Modifier.fillMaxWidth())
        if(error.isNotEmpty()) Text(error,color=MaterialTheme.colorScheme.error)
        if(state.privateOutgoingBatches.isEmpty()) Text("No private folder/file batches prepared.")
        state.privateOutgoingBatches.forEach {source->
            HorizontalDivider(); Text(source.label.ifEmpty {"Private batch · ${source.id.take(8)}"},style=MaterialTheme.typography.titleMedium)
            Text(model.privateBatchPeerName(source.peer,source.pairing)); Text("${source.phase.name.lowercase()} · ${source.preparedFiles} files / ${source.preparedBytes} bytes prepared")
            Text(source.message)
            if(source.owner!=state.deviceId) Text("Prepared under an earlier phone identity. Sending and plan inspection are unavailable; private-copy removal still requires owner review.")
            if(source.mayHaveReceiverCopies) Text("Receiver copies may already exist. Queueing this snapshot again requires an explicit additional-copy decision, even if its send history was removed.")
            if(source.active) TextButton({model.pausePrivateBatchPreparation(source.id)},enabled=enabled) {Text("Stop preparing this batch")}
            else {
                if(source.phase==PhoneBatchSourcePhase.READY) TextButton({run {plan=BatchPlanView(source,model.inspectPrivateBatch(source.id))}},enabled=enabled && source.owner==state.deviceId) {Text("Review batch plan…")}
                TextButton({run {val review=model.reviewOutgoingBatchRemoval(source.id)
                    prompt=BatchPrompt("Remove private batch copies?","Remove ${review.files} private files (${review.bytes} bytes) and this snapshot's manifest. Originals and receiver copies stay intact. Saved history loses its source for resuming. This cannot be undone.","Remove private copies") {model.removeOutgoingBatchReviewed(review)}
                }},enabled=enabled) {Text("Review private-copy removal…")}
            }
        }
        Text("Batch queue and saved history",style=MaterialTheme.typography.titleMedium)
        val pages=maxOf(1,(state.outgoingBatchSends.size+9)/10); val page=queuePage.coerceIn(0,pages-1)
        BatchPager(page,pages,"batch sends",{queuePage=it})
        if(state.outgoingBatchSends.isEmpty()) Text("No queued or archived batch sends.")
        state.outgoingBatchSends.drop(page*10).take(10).forEach {row->
            HorizontalDivider(); Text(row.label.ifEmpty {"Saved batch · ${row.id.take(8)}"},style=MaterialTheme.typography.titleMedium)
            Text(model.privateBatchPeerName(row.peer,row.pairing)); Text("${if(row.archived) "Archived · " else ""}${row.phase.name.lowercase()} · ${row.next} / ${row.items} items · ${row.bytes} bytes")
            Text(row.message); if(row.destination.isNotEmpty()) Text("Original receiving location: ${row.destination}")
            val current = row.owner == state.deviceId && model.privateBatchCurrent(row.peer, row.pairing)
            val online = state.authenticated && state.batchReceiverSupported && current
            val payload = state.outgoingBatchPayloads[row.id]?.takeIf { online && it.index == row.next && it.offset == row.offset }
            if (payload != null) {
                Text("Current file (item ${payload.index + 1} of ${row.items}) · ${payload.name} · ${payload.offset} / ${payload.size} bytes acknowledged")
                Text(payload.estimate.describe(PhonePayloadKind.SENDING, state.transferNetwork.preferences.bytesPerSecond > 0))
                if (payload.batchRemaining > payload.size - payload.offset) {
                    Text("Batch payload still planned · up to ${payload.batchRemaining} bytes")
                    Text(payload.batchEstimate.describe(PhonePayloadKind.BATCH_SENDING, state.transferNetwork.preferences.bytesPerSecond > 0))
                }
            } else if (row.offset > 0) Text("Saved item checkpoint · ${row.offset} bytes acknowledged")
            if(row.purging) {
                Text("History cleanup: ${row.purgeCursor} / ${row.purgeCount} receipts")
                TextButton({run {purge(row.id)}},enabled=enabled) {Text("Continue history cleanup")}
            } else {
                TextButton({results=row.id},enabled=enabled && !row.active) {Text("Review item results…")}
                if(row.active) TextButton({model.pauseOutgoingBatch(row.id)},enabled=enabled) {Text("Pause this batch send")}
                if(row.archived) {
                    TextButton({history(row,PhoneBatchHistoryAction.RESTORE)},enabled=enabled) {Text("Review restoring send…")}
                    TextButton({history(row,PhoneBatchHistoryAction.PURGE)},enabled=enabled) {Text("Review history purge…")}
                } else {
                    if(!row.active && row.phase !in setOf(PhoneBatchSendingPhase.COMPLETED,PhoneBatchSendingPhase.STOPPED,PhoneBatchSendingPhase.STOPPING)) {
                        val target=state.uploadDestination?:state.breadcrumbs.lastOrNull()
                        TextButton({prompt=BatchPrompt(if(row.header.isEmpty()) "Send this batch?" else "Check the original receiver plan?",
                            if(row.header.isEmpty()) "Send ${row.items} items (${row.bytes} bytes) into ${target?.name.orEmpty()}. Originals remain intact. Existing names use the collision choice in the reviewed plan."
                            else "Resume only the original request in ${row.destination}. Its saved receiver receipts determine what still needs copying. Uncertain copies require review; this will not start a new receiver plan.",
                            if(row.header.isEmpty()) "Send batch" else "Check / resume") {model.sendOutgoingBatch(row.id,target)}},
                            enabled=enabled && online && (row.header.isNotEmpty() || target?.permissions?.let {it and 12==12}==true)) {Text(if(row.header.isEmpty()) "Review sending batch…" else "Review original receipt / resume…")}
                    }
                    if(row.phase !in setOf(PhoneBatchSendingPhase.COMPLETED,PhoneBatchSendingPhase.STOPPED)) TextButton({
                        prompt=BatchPrompt("Stop this batch?","Pause local work and stop its original receiver plan. Published or uncertain receiver files are kept. Pending private receiver staging is cleaned before the receiver confirms the stop. Reconnect to that device if cleanup is interrupted.","Stop batch") {model.stopOutgoingBatch(row.id)}
                    },enabled=enabled && current && (row.header.isEmpty() || online)) {Text(if(row.phase==PhoneBatchSendingPhase.STOPPING) "Continue original batch stop…" else "Review stopping batch…")}
                    TextButton({history(row,PhoneBatchHistoryAction.ARCHIVE)},enabled=enabled && !row.active) {Text("Review archiving send…")}
                }
            }
        }
    }}
    BatchSenderRecoveryPanel(model,state)
    BatchSourceRecoveryPanel(model,state)
    if(state.incomingBatchPlans.isNotEmpty()) Card(Modifier.fillMaxWidth()) {Column(Modifier.padding(18.dp),verticalArrangement=Arrangement.spacedBy(10.dp)) {
        Text("Received batch history",style=MaterialTheme.typography.titleLarge)
        Text("Retiring a closed plan removes detailed local receipts, keeping its compact outcome and received files. A closed-history reset also ends old receipt lookup; preserved metadata remains in this phone's private backup.")
        val pages=maxOf(1,(state.incomingBatchPlans.size+9)/10); val page=incomingPage.coerceIn(0,pages-1)
        BatchPager(page,pages,"received batches",{incomingPage=it})
        state.incomingBatchPlans.drop(page*10).take(10).forEach {row->
            HorizontalDivider(); Text(model.incomingBatchPeerName(row.peer),style=MaterialTheme.typography.titleMedium)
            Text("${row.items} items · ${row.bytes} bytes · ${row.phase.name.lowercase().removePrefix("upload_plan_")}")
            Text("Created ${java.time.Instant.ofEpochMilli(MutationRules.created(row.id))} · ${row.id.take(8)}",style=MaterialTheme.typography.bodySmall)
            if (state.authenticated && row.phase == UploadBatchPlanPhase.UPLOAD_PLAN_READY) {
                state.incomingBatchFileProgress.filter { it.batch == row.id }.forEach { progress ->
                    val file = progress.file
                    Text("Item ${progress.index + 1} / ${row.items} · ${file.name} · ${file.offset} / ${file.size} bytes saved")
                    val status = progress.finalization?.describe() ?: when {
                        file.phase == UploadPhase.UPLOAD_VERIFYING -> "Verifying the saved file."
                        file.phase == UploadPhase.UPLOAD_FINALIZING -> "Finishing saving and checking the file."
                        file.offset == file.size -> PhonePayloadRateEstimate(PhonePayloadRateState.RECEIVED).text
                        else -> (progress.estimate ?: PhonePayloadRateEstimate(PhonePayloadRateState.WAITING))
                            .receivingText(state.transferNetwork.preferences.bytesPerSecond != 0L)
                    }
                    Text(status)
                }
            }
            when {
                row.retired->Text("Compact receipt retained. Detailed item lookup is unavailable.")
                row.retiring->{Text("Retiring detailed receipts · checkpoint ${row.retirementCursor} / ${row.items*2}")
                    TextButton({run {retire(row.id)}},enabled=enabled) {Text("Continue receipt retirement")}}
                UploadBatchRules.terminal(row.phase)->TextButton({run {val review=model.reviewIncomingBatchRetirement(row.id)
                    prompt=BatchPrompt("Retire detailed received-batch receipts?","Remove ${review.metadataFiles} metadata files (${review.metadataBytes} bytes). Received folders, files and the original terminal outcome stay intact. This loses detailed item receipt lookup. Cleanup can be continued if interrupted.","Retire detailed receipts") {if(!model.confirmIncomingBatchRetirement(review)) retire(row.id)}
                }},enabled=enabled) {Text("Review receipt retirement…")}
            }
        }
        TextButton({prompt=BatchPrompt("Reset closed batch history?","Preserve the closed receipt store in a private backup and start a new receiver context. Existing files stay intact. Earlier senders lose receipt lookup and must review existing copies before deliberately sending another batch.","Preserve and reset") {model.resetClosedBatchHistory()}},
            enabled=enabled && state.incomingBatchPlans.all {UploadBatchRules.terminal(it.phase) && !it.retiring}) {Text("Review closed-history reset…")}
    }}
}

@Composable
private fun BatchPager(page:Int,pages:Int,label:String,change:(Int)->Unit) {
    Text("Page ${page+1} / $pages · $label")
    Row(Modifier.fillMaxWidth(),horizontalArrangement=Arrangement.spacedBy(8.dp)) {
        TextButton({change(page-1)},enabled=page>0,modifier=Modifier.weight(1f)) {Text("Previous $label")}
        TextButton({change(page+1)},enabled=page+1<pages,modifier=Modifier.weight(1f)) {Text("Next $label")}
    }
}

@Composable
private fun BatchManifestDialog(source:PhoneBatchSource,manifest:UploadBatchManifest,dismiss:()->Unit,queue:(()->Unit)?) {
    var page by remember(source.id) {mutableIntStateOf(0)}
    AlertDialog(onDismissRequest=dismiss,title={Text("Private batch plan")},text={Column(Modifier.heightIn(max=400.dp).verticalScroll(rememberScrollState()),verticalArrangement=Arrangement.spacedBy(8.dp)) {
        Text(source.label.ifEmpty {"Private batch"}); Text("${manifest.itemsCount} items · ${source.bytes} bytes · ${batchCollision(manifest.collision)}")
        Text("${manifest.itemsList.count {it.error.isNotEmpty()}} discovery issues. Review unavailable items before queueing.")
        BatchPager(page,maxOf(1,(manifest.itemsCount+49)/50),"plan items",{page=it})
        manifest.itemsList.drop(page*50).take(50).forEachIndexed {at,item->Text("${page*50+at+1}. ${batchPath(manifest,page*50+at)} · "+when {
            item.error.isNotEmpty()->"Issue: ${item.error}"; item.isDirectory->"Folder"; else->"${item.size} bytes"})}
    }},confirmButton={if(queue!=null) TextButton(queue) {Text("Review queueing batch…")} else TextButton(dismiss) {Text("Done")}},
        dismissButton={if(queue!=null) TextButton(dismiss) {Text("Close")}})
}

@Composable
private fun BatchResultsDialog(model:PhoneController,id:String,row:PhoneBatchSend?,dismiss:()->Unit) {
    var page by remember(id) {mutableIntStateOf(0)}
    var items by remember(id) {mutableStateOf<List<PhoneBatchResultView>>(emptyList())}
    var error by remember(id) {mutableStateOf("")}
    var loading by remember(id) {mutableStateOf(true)}
    LaunchedEffect(id,page,row?.next,row?.receiverOutcomes) {loading=true; items=emptyList(); error=""; try {items=model.batchResultPage(id,page*50)}
        catch(e:CancellationException) {throw e} catch(e:Exception) {error=e.message?:"Saved results are unavailable."} finally {loading=false}}
    AlertDialog(onDismissRequest=dismiss,title={Text("Batch item results")},text={Column(Modifier.heightIn(max=400.dp).verticalScroll(rememberScrollState()),verticalArrangement=Arrangement.spacedBy(8.dp)) {
        Text(row?.label?.ifEmpty {"Saved batch"}?:"Saved batch unavailable")
        Text("An issue or uncertain copy is kept for review. Copied means the receiver verified the saved bytes; received files still need your trust decision.")
        BatchPager(page,maxOf(1,((row?.items?:0)+49)/50),"result items",{page=it})
        if(loading) LinearProgressIndicator(Modifier.fillMaxWidth())
        if(error.isNotEmpty()) Text(error,color=MaterialTheme.colorScheme.error)
        items.forEach {Text("${it.index+1}. ${it.name} · ${it.phase}"); if(it.message.isNotEmpty()) Text(it.message,style=MaterialTheme.typography.bodySmall)}
    }},confirmButton={TextButton(dismiss) {Text("Done")}})
}
