package com.iconnect.proof

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp

@Composable
internal fun TransferNetworkPanel(view:PhoneTransferNetworkView,save:(PhoneNetworkPreferences)->Unit,reset:()->Unit,clear:()->Unit) {
    var editing by remember {mutableStateOf(false)}; var resetting by remember {mutableStateOf(false)}; var clearing by remember {mutableStateOf(false)}
    var draft by remember {mutableStateOf(view.preferences)}
    val choices=listOf(0L to "Unlimited",65536L to "64 KiB/s",131072L to "128 KiB/s",524288L to "512 KiB/s",1048576L to "1 MiB/s",5242880L to "5 MiB/s",10485760L to "10 MiB/s",67108864L to "64 MiB/s")
        .let {rows->if(rows.any {it.first==view.preferences.bytesPerSecond}) rows else rows+(view.preferences.bytesPerSecond to "${view.preferences.bytesPerSecond} bytes/s (saved)")}
    Card {Column(Modifier.padding(18.dp),verticalArrangement=Arrangement.spacedBy(10.dp)) {
        Text("Transfer network controls",style=MaterialTheme.typography.titleMedium)
        Text("This phone's observed network cost: ${view.cost.name.lowercase()}")
        Text("Remote content limit per direction: ${choices.first {it.first==view.preferences.bytesPerSecond}.second}. Metered-network pausing: ${if(view.preferences.pauseOnMetered) "on" else "off"}.")
        Text("Transfers and previews share this phone's limits. Offline copies, status and cleanup remain independent. This is payload pacing, not a billing cap; overhead and buffered traffic can still use data.",style=MaterialTheme.typography.bodySmall)
        if(view.issue.isNotEmpty()) Text(view.issue,color=MaterialTheme.colorScheme.error)
        if(view.busy) LinearProgressIndicator(Modifier.fillMaxWidth())
        OutlinedButton({draft=view.preferences; editing=true},enabled=!view.busy && view.issue.isEmpty()) {Text("Review transfer network settings…")}
        if(view.canReset) TextButton({resetting=true},enabled=!view.busy) {Text(if(view.missing) "Review missing network settings…" else "Preserve and reset network settings…")}
        if(view.preserved>0) TextButton({clearing=true},enabled=!view.busy) {Text("Remove preserved network-settings metadata…")}
    }}
    if(editing) AlertDialog(onDismissRequest={editing=false},title={Text("Review transfer network settings")},text={Column(Modifier.heightIn(max=420.dp).verticalScroll(rememberScrollState())) {
        Text("Shared file-content limit in each direction on this phone:")
        choices.forEach {(value,label)->Row {RadioButton(draft.bytesPerSecond==value,{draft=draft.copy(bytesPerSecond=value)},modifier=Modifier.semantics {contentDescription=label}); Text(label,Modifier.padding(top=12.dp))}}
        Row {Switch(draft.pauseOnMetered,{draft=draft.copy(pauseOnMetered=it)},modifier=Modifier.semantics {contentDescription="Pause remote content on metered or unknown networks"}); Text("Pause on metered or unknown networks",Modifier.padding(8.dp))}
        Text("Initially unlimited, with metered pausing off. Allowing metered or unknown networks can incur charges. Roaming or Android data restrictions are treated conservatively. These controls cannot assess the computer's network plan.")
        Text("Saving affects active and future requests; already transmitted data cannot be recalled. It does not resume stopped jobs or change sharing approvals. Source approvals still expire; very slow transfers can outlast them.")
    }},confirmButton={TextButton({save(draft); editing=false}) {Text("Save reviewed controls")}},dismissButton={TextButton({editing=false}) {Text("Cancel")}})
    if(resetting) AlertDialog(onDismissRequest={resetting=false},title={Text("Reset transfer network settings?")},text={Text((if(view.missing) "Earlier settings are missing and cannot be preserved. " else "Preserve damaged settings metadata. ")+"Create defaults: unlimited content bandwidth and metered pausing off. Files, receipts and approvals are kept; interrupted transfers require explicit resume.")},
        confirmButton={TextButton({reset(); resetting=false}) {Text("Reset reviewed settings")}},dismissButton={TextButton({resetting=false}) {Text("Cancel")}})
    if(clearing) AlertDialog(onDismissRequest={clearing=false},title={Text("Remove network-settings backups?")},text={Text("Remove only preserved settings metadata. Current controls, files, receipts and approvals are kept.")},
        confirmButton={TextButton({clear(); clearing=false}) {Text("Remove metadata backups")}},dismissButton={TextButton({clearing=false}) {Text("Cancel")}})
}
