package com.iconnect.proof

import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch
import java.text.DateFormat
import java.util.Date
import java.util.Locale

@Composable
internal fun ActivityPanel(model:PhoneController,state:ProofState) {
    val view=state.activity; val scope=rememberCoroutineScope(); var page by remember {mutableIntStateOf(0)}; var busy by remember {mutableStateOf(false)}
    var action by remember {mutableStateOf("")}; var days by remember {mutableIntStateOf(30)}; var error by remember {mutableStateOf("")}
    fun run(work:suspend ()->Unit) {busy=true; error=""; scope.launch {try {work(); page=0} catch(e:CancellationException) {throw e} catch(_:Exception) {error="Activity history could not be changed. Its files were kept for review."} finally {busy=false}}}
    if(action.isNotEmpty()) AlertDialog(onDismissRequest={action=""},title={Text(when(action) {"retention"->"Change local activity retention?"; "reset"->"Preserve unreadable activity?"; else->"Clear local activity?"})},
        text={Text(when(action) {"retention"->if(days==0) "Remove retained activity and stop recording new entries. Files, transfer receipts and sharing settings stay intact." else "Keep at most 500 entries for $days days. Older activity is removed when history is loaded or updated. Files and transfer receipts stay intact."
            "reset"->"Preserve this journal and uncommitted sidecars in private recovery storage, then open empty 30-day history. Trust, sharing, transfers and original files stay intact."
            else->"Remove retained activity from this phone. New activity can still appear. Transfer receipts and original files stay intact."})},
        confirmButton={TextButton({val requested=action; action=""; run {when(requested) {"retention"->model.configureActivity(days); "reset"->model.resetActivity(); else->model.clearActivity()}}}) {Text(if(action=="reset") "Preserve and reset" else "Apply activity change")}},
        dismissButton={TextButton({action=""}) {Text("Keep current history")}})
    Card(Modifier.fillMaxWidth()) {Column(Modifier.padding(18.dp),verticalArrangement=Arrangement.spacedBy(10.dp)) {
        Text("Local activity history",style=MaterialTheme.typography.titleLarge)
        Text("Stored only in this app's private phone storage. Entries use fixed action/outcome labels and optional device identities and byte counts. Filenames, paths, pairing secrets, content and raw errors are excluded. Consecutive matching events within one second can be grouped. Activity is not an authorization record or a complete forensic log.")
        Text(if(view.days==0) "Recording is off." else "Retention: ${view.days} days · ${view.entries.size} / 500 entries")
        if(!view.ready || busy) LinearProgressIndicator(Modifier.fillMaxWidth())
        if(view.issue.isNotEmpty()) Text(view.issue,color=MaterialTheme.colorScheme.error)
        if(view.dropped>0) Text("${view.dropped} events were not recorded in this run. History may be incomplete.",color=MaterialTheme.colorScheme.error)
        if(error.isNotEmpty()) Text(error,color=MaterialTheme.colorScheme.error)
        Row(Modifier.fillMaxWidth()) {listOf(0,1,7,30,90).forEach {value->TextButton({days=value; action="retention"},enabled=view.ready && !busy && view.issue.isEmpty()) {Text(if(value==0) "Off" else "${value}d")}}}
        TextButton({action="clear"},enabled=view.ready && !busy && view.issue.isEmpty() && view.entries.isNotEmpty()) {Text("Clear local activity…")}
        if(view.issue.isNotEmpty()) TextButton({action="reset"},enabled=view.canReset && !busy) {Text("Preserve and reset activity…")}
        val pages=maxOf(1,(view.entries.size+49)/50); val selected=page.coerceIn(0,pages-1)
        Row {TextButton({page=selected-1},enabled=selected>0) {Text("Previous activity")}; Text("${selected+1} / $pages"); TextButton({page=selected+1},enabled=selected+1<pages) {Text("Next activity")}}
        if(view.ready && view.entries.isEmpty()) Text("No retained activity.")
        view.entries.drop(selected*50).take(50).forEach {entry->
            HorizontalDivider(); Text(entry.kind.name.lowercase(Locale.ROOT).replace('_',' ')+" · "+entry.outcome.name.lowercase(Locale.ROOT).replace('_',' '))
            Text(DateFormat.getDateTimeInstance(DateFormat.SHORT,DateFormat.SHORT).format(Date(entry.at))+(if(entry.peer.isEmpty()) "" else " · device "+entry.peer.take(8))+(if(entry.count>1) " · ${entry.count} events" else ""))
            if(entry.operation>=0) Text(com.iconnect.protocol.FileOperation.forNumber(entry.operation)?.name?.lowercase(Locale.ROOT)?.replace('_',' ')?:"Unknown operation")
            if(entry.bytes>=0) Text("${entry.bytes} bytes recorded")
        }
    }}
}
