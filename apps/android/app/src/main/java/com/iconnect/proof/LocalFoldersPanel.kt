package com.iconnect.proof

import android.app.Activity
import android.content.Intent
import android.provider.DocumentsContract as Docs
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import kotlinx.coroutines.launch
import kotlinx.coroutines.Job

@Composable
internal fun LocalFoldersPanel(browser:PhoneLocalBrowser,canCreate:Boolean) {
    val manager=browser.folders
    if(manager==null) {Text(browser.folderIssue); return}
    val state by manager.state.collectAsStateWithLifecycle()
    val local by browser.state.collectAsStateWithLifecycle()
    val scope=rememberCoroutineScope()
    var naming by remember {mutableStateOf(false)}
    var name by remember {mutableStateOf("")}
    var choosing by remember {mutableStateOf(false)}
    var target by remember {mutableStateOf<LocalFolderTarget?>(null)}
    var error by remember {mutableStateOf("")}
    fun ownerAction(action:()->Job) {runCatching(action).onFailure {error="Return to iConnect and review the current folder action."}}
    var removal by remember {mutableStateOf<PhoneLocalFolderReceipt?>(null)}
    var preserve by remember {mutableStateOf<StateRecoveryIssue?>(null)}
    var backups by remember {mutableStateOf(false)}
    var page by remember {mutableIntStateOf(0)}
    val picker=rememberLauncherForActivityResult(ActivityResultContracts.StartActivityForResult()) {result->
        choosing=false; val selected=target; target=null
        if(result.resultCode==Activity.RESULT_OK && selected!=null && result.data?.data!=null)
            runCatching {manager.approve(selected,requireNotNull(result.data?.data),requireNotNull(result.data).flags)}.onFailure {error="The folder approval could not be retained. Start a fresh review."}
    }
    if(naming) AlertDialog(onDismissRequest={if(!choosing) naming=false},title={Text("Create a folder here?")},text={Column(Modifier.heightIn(max=400.dp).verticalScroll(rememberScrollState()),verticalArrangement=Arrangement.spacedBy(8.dp)) {
        Text("Location: "+local.breadcrumbs.joinToString(" / ") {it.name})
        Text("Android will ask you to select the same local root folder again for this one creation. Browsing shortcuts and sharing approvals stay separate. Existing items are kept; provider failures may need inspection before another attempt.")
        OutlinedTextField(name,{name=it.take(255)},label={Text("New folder name")},singleLine=true,enabled=!choosing)
        if(error.isNotEmpty()) Text(error,color=MaterialTheme.colorScheme.error)
    }},confirmButton={TextButton({choosing=true; error=""; scope.launch {
        try {val selected=browser.captureFolderCreation(name); target=selected; naming=false
            picker.launch(Intent(Intent.ACTION_OPEN_DOCUMENT_TREE).putExtra(Docs.EXTRA_INITIAL_URI,Docs.buildDocumentUriUsingTree(selected.tree,Docs.getTreeDocumentId(selected.tree)))
                .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION or Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION or Intent.FLAG_GRANT_PREFIX_URI_PERMISSION))
        } catch(_:Exception) {target=null; choosing=false; error="The folder selection or name is unavailable. Refresh it and start again."}
    }},enabled=!choosing && name.isNotBlank()) {Text("Approve folder access…")}},dismissButton={TextButton({naming=false},enabled=!choosing) {Text("Cancel")}})
    state.review?.let {review->AlertDialog(onDismissRequest={if(!state.busy) ownerAction {manager.cancelReview()}},title={Text("Confirm local folder creation")},
        text={Text("Folder: ${review.folder}\nNew folder: ${review.name}\n\nThis creates one provider folder. It does not share this location or authorize background work. An uncertain result requires inspection and is never retried automatically.")},
        confirmButton={TextButton({ownerAction {manager.create(review.id)}},enabled=!state.busy) {Text("Create folder")}},
        dismissButton={TextButton({ownerAction {manager.cancelReview()}},enabled=!state.busy) {Text("Keep current folder")}})}
    removal?.let {row->AlertDialog(onDismissRequest={removal=null},title={Text("Remove local folder receipt?")},
        text={Text("${row.folder} / ${row.resultingName.ifEmpty {row.name}}\nOnly this local receipt is removed. Existing provider folders are kept. Inspect an uncertain result before deliberately creating another folder.")},
        confirmButton={TextButton({removal=null; ownerAction {manager.removeReceipt(row.id)}}) {Text("Remove receipt")}},dismissButton={TextButton({removal=null}) {Text("Keep receipt")}})}
    preserve?.let {issue->AlertDialog(onDismissRequest={preserve=null},title={Text("Preserve damaged folder history?")},text={Text("Unreadable metadata is kept separately. Provider folders are not changed. Earlier uncertain creations still need manual inspection.")},
        confirmButton={TextButton({preserve=null; ownerAction {manager.preserve(issue.id)}}) {Text("Preserve history")}},dismissButton={TextButton({preserve=null}) {Text("Keep current state")}})}
    if(backups) AlertDialog(onDismissRequest={backups=false},title={Text("Remove preserved folder metadata?")},text={Text("Only separately preserved metadata is removed. Current receipts and provider folders are kept.")},
        confirmButton={TextButton({backups=false; ownerAction {manager.clearPreserved()}}) {Text("Remove metadata")}},dismissButton={TextButton({backups=false}) {Text("Keep metadata")}})
    Card(Modifier.fillMaxWidth()) {Column(Modifier.padding(16.dp),verticalArrangement=Arrangement.spacedBy(8.dp)) {
        Text("Local folder creation",style=MaterialTheme.typography.titleMedium)
        Text("Each creation needs a separate Android picker approval and confirmation. Folder receipts stay on this phone; existing sharing approvals and queued copies remain separate.",style=MaterialTheme.typography.bodySmall)
        if(state.error.isNotEmpty()) Text(state.error,color=MaterialTheme.colorScheme.error)
        if(error.isNotEmpty() && !naming) Text(error,color=MaterialTheme.colorScheme.error)
        Text("${state.receipts.size} / 128 local folder receipts retained",style=MaterialTheme.typography.bodySmall)
        if(state.receipts.size>=128) Text("Remove a reviewed receipt before another creation. Provider folders are kept.")
        TextButton({name=""; error=""; naming=true},enabled=canCreate && state.receipts.size<128 && !state.busy && !choosing && state.review==null && state.issues.isEmpty()) {Text("Create folder here…")}
        if(state.busy) LinearProgressIndicator(Modifier.fillMaxWidth())
        state.issues.forEach {issue->Text(issue.message); TextButton({preserve=issue},enabled=!state.busy && issue.canArchive) {Text("Review damaged folder history…")}}
        if(state.preserved>0) TextButton({backups=true},enabled=!state.busy) {Text("Remove preserved folder metadata…")}
        val pages=maxOf(1,(state.receipts.size+15)/16); val currentPage=page.coerceIn(0,pages-1)
        state.receipts.drop(currentPage*16).take(16).forEach {row->
            Text("${row.folder} / ${row.resultingName.ifEmpty {row.name}} · ${row.phase.name.lowercase()}")
            Text(if(row.phase==PhoneLocalFolderPhase.CREATED) "The provider returned a new empty folder at the recorded location. Refresh the local view to browse it."
                else "The provider may have created a folder. Inspect the location before another creation; this receipt cannot retry it.",style=MaterialTheme.typography.bodySmall)
            TextButton({removal=row},enabled=!state.busy && state.review==null) {Text("Remove folder receipt…")}
        }
        if(pages>1) Row {TextButton({page=currentPage-1},enabled=currentPage>0) {Text("Previous folder receipts")}; Text("${currentPage+1} / $pages"); TextButton({page=currentPage+1},enabled=currentPage+1<pages) {Text("Next folder receipts")}}
    }}
}
