package com.iconnect.proof

import android.net.Uri
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch

@Composable
internal fun DiagnosticsPanel(model:PhoneController,onChooseLocation:((PhoneDiagnosticReview)->Unit)?=null,onSaved:(PhoneDiagnosticReview,Uri)->Unit={_,_->}) {
    val scope=rememberCoroutineScope(); var busy by remember {mutableStateOf(false)}; var notice by remember {mutableStateOf("")}
    var review by remember {mutableStateOf<PhoneDiagnosticReview?>(null)}; var pending by remember {mutableStateOf<PhoneDiagnosticReview?>(null)}
    val create=rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("application/json")) {uri->
        val value=pending; pending=null
        if(value!=null) {
            if(uri==null) {model.cancelDiagnostics(value.id); notice="Report creation canceled."}
            else {busy=true; scope.launch {try {model.exportDiagnostics(value,uri); onSaved(value,uri); notice="Report created and verified. Share it only when you choose."}
                catch(e:CancellationException) {throw e} catch(_:Exception) {notice="The report could not be verified. A provider copy may remain; prepare a fresh report and choose a new empty document."} finally {busy=false}}}
        }
    }
    review?.let {value->DiagnosticReviewDialog(value,
        dismiss={model.cancelDiagnostics(value.id); review=null; notice="Diagnostic review canceled; no report was created."},
        save={review=null; if(onChooseLocation!=null) onChooseLocation(value) else {pending=value; create.launch(value.filename)}})}
    Card(Modifier.fillMaxWidth()) {Column(Modifier.padding(18.dp),verticalArrangement=Arrangement.spacedBy(10.dp)) {
        Text("Diagnostic report",style=MaterialTheme.typography.titleLarge)
        Text("Review a snapshot of app/OS versions, connection states, permission counts, queue phases and recovery counts. Reports omit device names and IDs, addresses, paths, filenames, content, pairing data, logs and raw errors. Saving writes a JSON document to your chosen provider, which may sync it.")
        if(busy) LinearProgressIndicator(Modifier.fillMaxWidth())
        if(notice.isNotEmpty()) Text(notice)
        TextButton({busy=true; notice=""; scope.launch {try {review=model.reviewDiagnostics()}
            catch(e:CancellationException) {throw e} catch(_:Exception) {notice="A diagnostic snapshot could not be prepared. Reopen iConnect and retry."} finally {busy=false}}},enabled=!busy && pending==null) {Text("Review diagnostic report…")}
    }}
}

@Composable
internal fun DiagnosticReviewDialog(review:PhoneDiagnosticReview,dismiss:()->Unit,save:()->Unit) {
    AlertDialog(onDismissRequest=dismiss,title={Text("Review redacted diagnostic report")},text={Column(Modifier.heightIn(max=420.dp).verticalScroll(rememberScrollState()),verticalArrangement=Arrangement.spacedBy(10.dp)) {
        Text("This is the exact report to save. Counts can reveal app usage and activity. It is a best-effort snapshot; states can change while it is open. The review expires after two minutes. Choose a new report document in the system picker. Your chosen provider may sync this document.")
        SelectionContainer {Text(review.json,fontFamily=FontFamily.Monospace,style=MaterialTheme.typography.bodySmall)}
    }},confirmButton={TextButton(save) {Text("Choose report location…")}},dismissButton={TextButton(dismiss) {Text("Keep report private")}})
}
