package com.iconnect.proof

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.*

@Composable
internal fun DownloadSourceRecoveryPanel(row:PhoneDownload,estimate:PhoneTransferTimeEstimate,enabled:Boolean,
    prepare:suspend()->PhoneDownloadSourceReview,confirm:suspend(PhoneDownloadSourceReview)->Unit,cancel:(String)->Unit,batchPath:String?=null,
    observed:PhonePayloadRateEstimate?=null) {
    val scope=rememberCoroutineScope()
    var work by remember {mutableStateOf<Job?>(null)}
    var review by remember {mutableStateOf<PhoneDownloadSourceReview?>(null)}
    var error by remember {mutableStateOf("")}
    var notice by remember {mutableStateOf("")}
    val cancelCurrent by rememberUpdatedState(cancel)
    DisposableEffect(review?.id) {val id=review?.id; onDispose {id?.let(cancelCurrent)}}
    if(estimate.remainingBytes>0 && observed!=null) Text(observed.text,style=MaterialTheme.typography.bodySmall)
    Text(estimate.payloadText,style=MaterialTheme.typography.bodySmall)
    if(row.offset<row.size || row.sha256.isEmpty()) {
        Text(estimate.approvalText,style=MaterialTheme.typography.bodySmall)
        if(estimate.warning.isNotEmpty()) Text(estimate.warning,color=MaterialTheme.colorScheme.error)
        else observed?.approvalWarning(estimate.sourceSeconds)?.takeIf {it.isNotEmpty()}?.let {Text(it,color=MaterialTheme.colorScheme.error)}
    }
    if(error.isNotEmpty()) Text(error,color=MaterialTheme.colorScheme.error)
    if(notice.isNotEmpty()) Text(notice)
    val eligible=row.phase in setOf(PhoneDownloadPhase.PAUSED,PhoneDownloadPhase.INTERRUPTED) && !row.active && (row.batchId.isEmpty()==(batchPath==null)) &&
        row.attempts==0 && row.published.isEmpty() && !(row.offset==row.size && row.sha256.isNotEmpty())
    if(eligible && work==null) TextButton({
        work=scope.launch(start=CoroutineStart.LAZY) {
            try {error=""; notice=""; review=prepare()}
            catch(e:Exception) {if(e !is CancellationException) error=e.message ?: "The selected source could not be reviewed."}
            finally {work=null}
        }; work?.start()
    },enabled=enabled && review==null) {Text("Review selected fresh source…")}
    if(work!=null) {
        Text("Preparing or checking the selected source… Your saved bytes are kept.")
        TextButton({work?.cancel()}) {Text("Cancel source preparation")}
    }
    review?.let {offer-> AlertDialog(onDismissRequest={review=null},title={Text(if(batchPath==null) "Review fresh download source" else "Review fresh batch source")},text={
        Column(Modifier.heightIn(max=420.dp).verticalScroll(rememberScrollState()),verticalArrangement=Arrangement.spacedBy(8.dp)) {
            batchPath?.let {Text("Original batch item: $it")}
            Text("${offer.job.name} · ${offer.job.size} bytes · saved checkpoint ${offer.job.offset} bytes")
            Text(if(offer.job.sha256.isEmpty()) "No original checksum was saved. Only an empty checkpoint can adopt this selected file." else "The prepared source matches the complete original SHA-256 checksum.")
            Text("Original version: ${offer.job.version}\nSelected version: ${offer.newVersion}\nSHA-256: ${offer.hash}")
            Text("Peer-reported source expiry: ${java.text.DateFormat.getDateTimeInstance().format(java.util.Date(offer.sourceExpiresAt))}. Device clocks may differ.")
            Text(if(batchPath==null) "This review lasts five minutes on this connection while iConnect remains open. Approval updates only the source ticket and verified descriptor. It keeps the private partial file, checkpoint, destination folder and collision policy. Resume separately to recheck the saved bytes and continue. Provider saving remains a separate step."
                else "This review lasts five minutes on this connection while iConnect remains open. Only this file's source is approved. The original batch plan, destination folders, private partial file, saved progress and collision choice stay in place. Approval creates no provider copy. Resume the original batch separately to continue receiving and saving under its existing plan. Background permissions are not expanded.")
        }
    },confirmButton={TextButton({
        work=scope.launch(start=CoroutineStart.LAZY) {
            try {error=""; confirm(offer); notice=if(batchPath==null) "Fresh source approved. Resume explicitly to continue." else "Fresh source approved. Resume the original batch explicitly."}
            catch(e:Exception) {if(e !is CancellationException) error=e.message ?: "The source review ended. Review again."}
            finally {review=null; work=null}
        }; work?.start()
    },enabled=work==null && enabled) {Text(if(batchPath==null) "Approve source only" else "Approve source in original batch")}},dismissButton={TextButton({work?.cancel(); review=null}) {Text("Cancel")}}) }
}
