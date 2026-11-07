package com.iconnect.proof

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import java.text.DateFormat
import java.util.Date
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLong

@Composable
internal fun ActivityBackupControls(model:PhoneController,enabled:Boolean) {
    val scope=rememberCoroutineScope(); val lifecycle=LocalLifecycleOwner.current
    val visible=remember {AtomicBoolean(false)}
    val epoch=remember {AtomicLong()}
    var open by remember {mutableStateOf(false)}; var busy by remember {mutableStateOf(false)}
    var inventory by remember {mutableStateOf<PhoneActivityBackupInventory?>(null)}
    var review by remember {mutableStateOf<PhoneActivityBackupReview?>(null)}
    var page by remember {mutableIntStateOf(0)}; var typed by remember {mutableStateOf("")}
    var error by remember {mutableStateOf("")}; var notice by remember {mutableStateOf("")}
    var operation by remember {mutableStateOf<Job?>(null)}
    fun close() {
        epoch.incrementAndGet(); visible.set(false); model.cancelActivityBackupReview(); operation?.cancel()
        open=false; review=null; typed=""; inventory=null; busy=false
    }
    fun run(work:suspend ()->Unit) {
        val current=epoch.get()
        busy=true; error=""
        operation=scope.launch {
            try {work()}
            catch(e:CancellationException) {throw e}
            catch(_:Exception) {if(visible.get() && epoch.get()==current) error="The backup operation did not finish. Already removed metadata stays removed; review remaining contents again."}
            finally {if(epoch.get()==current) busy=false}
        }
    }
    suspend fun load(start:Int) {val current=model.inspectActivityBackups(start) {visible.get()}; if(visible.get()) inventory=current}
    DisposableEffect(model,lifecycle) {
        val observer=LifecycleEventObserver {_,event->if(event==Lifecycle.Event.ON_STOP) close()}
        lifecycle.lifecycle.addObserver(observer)
        onDispose {lifecycle.lifecycle.removeObserver(observer); close()}
    }
    TextButton({visible.set(true); open=true; page=0; review=null; typed=""; notice=""; run {load(0)}},enabled=enabled && !open) {
        Text("Manage preserved activity backups…")
    }
    if(open && review==null) AlertDialog(onDismissRequest=::close,title={Text("Preserved activity backups")},
        text={Column(verticalArrangement=Arrangement.spacedBy(10.dp)) {
            Text("Clearing current activity or turning recording off keeps these earlier metadata copies. Review one activity-only backup at a time. Transfer records, trust, original files and other recovery stores are kept.")
            if(busy) LinearProgressIndicator(Modifier.fillMaxWidth())
            if(error.isNotEmpty()) Text(error,color=MaterialTheme.colorScheme.error)
            if(notice.isNotEmpty()) Text(notice)
            inventory?.let {view->
                Text("${view.total} activity backups · page ${page+1}")
                if(view.keptFolders>0) Text("${view.keptFolders} active, unrecognized or unsupported recovery folders are kept.")
                Row {TextButton({page--; run {load(page*50)}},enabled=!busy && page>0) {Text("Previous activity backups")}
                    TextButton({page++; run {load(page*50)}},enabled=!busy && (page+1)*50<view.total) {Text("Next activity backups")}}
                Column(Modifier.heightIn(max=280.dp).verticalScroll(rememberScrollState()),verticalArrangement=Arrangement.spacedBy(8.dp)) {
                    view.entries.forEach {entry->
                        Text(DateFormat.getDateTimeInstance(DateFormat.SHORT,DateFormat.SHORT).format(Date(entry.modifiedAt))+" · ${entry.files} files · ${entry.bytes} bytes")
                        TextButton({run {val captured=model.reviewActivityBackup(entry.id) {visible.get()}; if(visible.get()) {review=captured; typed=""}}},enabled=!busy) {Text("Review backup ${entry.id.take(8)}")}
                        HorizontalDivider()
                    }
                    if(view.entries.isEmpty()) Text("No removable activity backups on this page.")
                }
            }
            TextButton({page=0; run {load(0)}},enabled=!busy) {Text("Refresh activity backups")}
        }},confirmButton={TextButton(::close) {Text("Done")}})
    review?.takeIf {open}?.let {captured->AlertDialog(onDismissRequest=::close,title={Text("Remove this activity backup?")},
        text={Column(verticalArrangement=Arrangement.spacedBy(10.dp)) {
            Text("Permanently remove the exact reviewed ${captured.files} metadata files (${captured.bytes} bytes) in backup ${captured.backup.take(8)}. Current activity, transfers and original files are kept. This cannot be undone. Changed or expired reviews require another decision. Interrupted cleanup can leave remaining files after some metadata was removed.")
            OutlinedTextField(typed,{typed=it.take(6)},label={Text("Type REMOVE to confirm")},singleLine=true,enabled=!busy,modifier=Modifier.fillMaxWidth())
            if(busy) LinearProgressIndicator(Modifier.fillMaxWidth())
            if(error.isNotEmpty()) Text(error,color=MaterialTheme.colorScheme.error)
        }},confirmButton={TextButton({run {
            model.confirmActivityBackup(captured) {visible.get()}
            if(visible.get()) {review=null; typed=""; page=0; notice="The reviewed activity backup was removed. Current history and other stores were kept."; load(0)}
        }},enabled=!busy && typed=="REMOVE") {Text("Remove activity backup")}},
        dismissButton={TextButton({model.cancelActivityBackupReview(); review=null; typed=""; page=0; run {load(0)}},enabled=!busy) {Text("Keep backup")}})}
}
