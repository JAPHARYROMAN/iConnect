package com.iconnect.proof

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.iconnect.protocol.DownloadState
import kotlinx.coroutines.*

@Composable
internal fun PreparedSourceControlsPanel(rows:List<PhonePreparedSource>,enabled:Boolean,changing:Set<String>,
    stop:suspend(String)->Unit,cleanup:suspend()->Int) {
    if(rows.isEmpty()) return
    var page by rememberSaveable {mutableIntStateOf(0)}
    var selected by remember {mutableStateOf<PhonePreparedSource?>(null)}
    var work by remember {mutableStateOf<Job?>(null)}
    var error by remember {mutableStateOf("")}
    var notice by remember {mutableStateOf("")}
    val scope=rememberCoroutineScope(); val pages=maxOf(1,(rows.size+31)/32); val current=page.coerceIn(0,pages-1)
    Card {Column(Modifier.padding(18.dp),verticalArrangement=Arrangement.spacedBy(10.dp)) {
        Text("Prepared reads from this phone",style=MaterialTheme.typography.titleLarge)
        Text("Retained approvals for every computer, including while disconnected. Stopping one keeps original files and received copies. Folder grants stay separate and can allow a new request. Use folder pause or revocation to end wider access.")
        Text("Preparation progress measures checking this phone's source file, not bytes saved on the computer.",style=MaterialTheme.typography.bodySmall)
        if(error.isNotEmpty()) Text(error,color=MaterialTheme.colorScheme.error)
        if(notice.isNotEmpty()) Text(notice)
        TextButton({work=scope.launch(start=CoroutineStart.LAZY) {
            try {error=""; notice="Removed ${cleanup()} expired source approvals."}
            catch(e:Exception) {if(e !is CancellationException) error=e.message?:"Expired approvals could not be removed."}
            finally {work=null}
        }; work?.start()},enabled=enabled && work==null && rows.any {it.expired}) {Text("Remove expired source approvals")}
        Row {
            TextButton({page=current-1},enabled=current>0) {Text("Previous prepared sources")}
            Text("${current+1} / $pages")
            TextButton({page=current+1},enabled=current+1<pages) {Text("Next prepared sources")}
        }
        rows.drop(current*32).take(32).forEach {row->
            HorizontalDivider(); Text(row.name,style=MaterialTheme.typography.titleMedium)
            Text(when {row.expired->"Expired"; row.paused->"Paused"; row.state==DownloadState.DOWNLOAD_FAILED->"Source unavailable";
                row.preparing->"Preparing · ${row.prepared} / ${row.size} bytes"; row.state==DownloadState.DOWNLOAD_READY->"Prepared · ${row.size} bytes"; else->"Preparation stopped"})
            Text("Receiving computer: ${row.peer}",style=MaterialTheme.typography.bodySmall)
            Text("Approval ends: ${java.text.DateFormat.getDateTimeInstance().format(java.util.Date(row.expiresAt))}")
            TextButton({selected=row},enabled=enabled && work==null && row.id !in changing) {Text("Stop this prepared read…")}
        }
        if(work!=null) Text("Updating source approvals…")
    }}
    selected?.let {row->AlertDialog(onDismissRequest={selected=null},title={Text("Stop this prepared read?")},text={
        Column(Modifier.heightIn(max=420.dp).verticalScroll(rememberScrollState()),verticalArrangement=Arrangement.spacedBy(8.dp)) {
            Text(row.name); Text("Receiving computer: ${row.peer}")
            Text("This ends only this prepared approval. Original files and copies already received are kept. The computer may need a freshly reviewed source to continue. Folder grants remain unchanged and can permit another request. This does not authorize background sending.")
        }
    },confirmButton={TextButton({work=scope.launch(start=CoroutineStart.LAZY) {
        try {error=""; stop(row.id); notice="Prepared read stopped. Originals and received copies were kept."}
        catch(e:Exception) {if(e !is CancellationException) error=e.message?:"The source approval could not be stopped."}
        finally {work=null; selected=null}
    }; work?.start()},enabled=enabled && work==null) {Text("Stop prepared read")}},dismissButton={TextButton({selected=null}) {Text("Keep approval")}})}
}
