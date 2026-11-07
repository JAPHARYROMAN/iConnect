package com.iconnect.proof

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.iconnect.protocol.UploadPhase
import kotlinx.coroutines.*

@Composable
internal fun IncomingWindowRecoveryPanel(row:PhoneIncomingCopy,enabled:Boolean,prepare:suspend()->PhoneIncomingWindowReview,
    confirm:suspend(PhoneIncomingWindowReview)->Unit,cancel:(String)->Unit) {
    var work by remember {mutableStateOf<Job?>(null)}
    var review by remember {mutableStateOf<PhoneIncomingWindowReview?>(null)}
    var error by remember {mutableStateOf("")}
    var notice by remember {mutableStateOf("")}
    val scope=rememberCoroutineScope(); val cancelCurrent by rememberUpdatedState(cancel)
    DisposableEffect(review?.id) {val id=review?.id; onDispose {id?.let(cancelCurrent)}}
    fun date(value:Long)=java.text.DateFormat.getDateTimeInstance().format(java.util.Date(value))
    Text("Write window ends: ${date(row.operationExpiresAt)}. Current folder approval and its expiry still apply.",style=MaterialTheme.typography.bodySmall)
    if(error.isNotEmpty()) Text(error,color=MaterialTheme.colorScheme.error)
    if(notice.isNotEmpty()) Text(notice)
    val eligible=!row.active && row.phase in setOf(UploadPhase.UPLOAD_RECEIVING,UploadPhase.UPLOAD_REVIEW,UploadPhase.UPLOAD_FINALIZING)
    if(eligible && work==null) TextButton({work=scope.launch(start=CoroutineStart.LAZY) {
        try {error=""; notice=""; review=prepare()}
        catch(e:Exception) {if(e !is CancellationException) error=e.message?:"The existing receipt could not be reviewed."}
        finally {work=null}
    }; work?.start()},enabled=enabled && review==null) {Text("Review this write window…")}
    if(work!=null) {Text("Checking retained incoming bytes…"); TextButton({work?.cancel()}) {Text("Cancel incoming-window check")}}
    review?.let {offer->AlertDialog(onDismissRequest={review=null},title={Text("Review expired incoming write window")},text={
        Column(Modifier.heightIn(max=420.dp).verticalScroll(rememberScrollState()),verticalArrangement=Arrangement.spacedBy(8.dp)) {
            Text("${offer.name} · ${offer.checkpoint} / ${offer.size} bytes at the retained checkpoint")
            Text("Paired computer: ${offer.peerName}\nDevice ID: ${offer.peer}")
            Text("Receiving location: ${offer.destination}")
            Text(if(offer.publishedName.isEmpty()) "The identified private stage and checkpoint are retained." else "Recorded provider copy: ${offer.publishedName}. Finalization still verifies that same copy.")
            Text("Current window ended: ${date(offer.currentExpiresAt)}\nProposed window ends: ${date(offer.proposedExpiresAt)}")
            Text("This allows the same paired computer to continue this receipt for up to 24 hours from review. Current permissions, exclusions and grant expiry still apply. Confirmation rechecks the retained bytes. It does not resume receiving, create a provider document or change an uncertain publication decision. Resume the existing send on the computer separately. This review expires in five minutes and ends when iConnect leaves the foreground or owner context changes.")
        }
    },confirmButton={TextButton({work=scope.launch(start=CoroutineStart.LAZY) {
        try {confirm(offer); notice="Write window approved. Resume the existing send on the computer separately."}
        catch(e:Exception) {if(e !is CancellationException) error=e.message?:"The incoming window was not approved."}
        finally {review=null; work=null}
    }; work?.start()},enabled=enabled && work==null) {Text("Allow reviewed write window")}},dismissButton={TextButton({work?.cancel(); review=null}) {Text("Cancel")}})}
}
