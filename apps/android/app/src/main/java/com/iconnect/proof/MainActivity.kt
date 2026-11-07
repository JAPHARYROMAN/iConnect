package com.iconnect.proof

import android.os.Bundle
import android.app.Activity
import android.content.Intent
import android.net.Uri
import android.Manifest
import android.os.Build
import android.content.pm.PackageManager
import androidx.compose.ui.platform.LocalContext
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.iconnect.protocol.UploadPhase
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions

class MainActivity : ComponentActivity() {
    private lateinit var model: PhoneController
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        model = (application as IConnectApplication).controller()
        setContent {
            MaterialTheme(colorScheme = lightColorScheme(primary = Color(0xFF007E88),
                background = Color(0xFFF1F6F8), surface = Color.White, onSurface = Color(0xFF102C3D))) {
                ProofScreen(model)
            }
        }
    }
    override fun onStart() { super.onStart(); model.setUiVisible(true) }
    override fun onStop() {
        model.setUiVisible(false)
        super.onStop()
    }
}

@Composable
private fun IncomingBatchPlanHistoryCard(state:ProofState,onReview:()->Unit) {
    Card {Column(Modifier.padding(18.dp),verticalArrangement=Arrangement.spacedBy(10.dp)) {
        Text("Incoming batch plans",style=MaterialTheme.typography.titleLarge)
        val reserved=state.incomingBatchPlans.filter {!UploadBatchRules.terminal(it.phase)}.sumOf {it.bytes}
        Text(if(state.transferRecovery.any {it.area=="Batch plans"}) "Saved batch-plan metadata needs local review. Incoming reservations cannot be verified." else
            "Retained plans: ${state.incomingBatchPlans.size}. Incoming-copy quota reserved: $reserved bytes. "+
                if(state.incomingBatchPlans.any {it.effectsStarted}) "Destination items may already exist. Archiving metadata keeps those items." else "Provider documents have not been created by these plans.")
        TextButton(onReview) {Text("Archive / reset batch-plan history…")}
    }}
}

@Composable
internal fun ProofScreen(model: PhoneController) {
    val state by model.state.collectAsStateWithLifecycle()
    val filePreview by model.preview.state.collectAsStateWithLifecycle()
    var onPhone by rememberSaveable { mutableStateOf(false) }
    LaunchedEffect(onPhone) { if(onPhone) model.preview.clear() }
    if (onPhone) { LocalFilesScreen(model.localFiles,stageUpload=if(state.peerName.isNotEmpty() && !state.savingTrust && !state.revocationPending) model::stageUpload else null,
        uploadNotice=listOfNotNull(state.uploads.firstOrNull()?.let {"${it.name} · ${it.phase.name.lowercase()} · ${it.message}"},
            state.privateOutgoingBatches.firstOrNull()?.let {"${it.label.ifEmpty {"Private batch"}} · ${it.phase.name.lowercase()} · ${it.preparedFiles} files / ${it.preparedBytes} bytes prepared · ${it.message}"}).joinToString("\n"),
        stageBatch=if(state.peerName.isNotEmpty() && !state.savingTrust && !state.revocationPending) model::stageUploadBatch else null,
        preparationBusy=state.privateOutgoingBatches.any {it.active}) { onPhone = false }; return }
    var verification by rememberSaveable { mutableStateOf("") }
    var selectedTree by rememberSaveable { mutableStateOf("") }
    var selectedFlags by rememberSaveable { mutableIntStateOf(0) }
    var shareLabel by rememberSaveable { mutableStateOf("Phone documents") }
    var fileQuery by rememberSaveable { mutableStateOf("") }
    var batchCollision by remember { mutableStateOf(PhoneCollision.KEEP_BOTH) }
    var anotherBatchCopy by remember { mutableStateOf<Pair<String, String>?>(null) }
    var skipBatchFolder by remember { mutableStateOf<Pair<String, Int>?>(null) }
    var resetPairing by rememberSaveable { mutableStateOf(false) }
    var recoveryItem by remember { mutableStateOf<PhoneTransferRecovery?>(null) }
    var recoveryPage by rememberSaveable { mutableIntStateOf(0) }
    var clearRecoveryBackups by remember { mutableStateOf(false) }
    var changeName by remember { mutableStateOf<String?>(null) }
    var renameChange by remember { mutableStateOf(false) }
    var confirmMove by remember { mutableStateOf(false) }
    var showChangeHistory by remember { mutableStateOf(false) }
    var resetAutomaticConnections by remember {mutableStateOf(false)}
    var removeUpload by remember {mutableStateOf<PhoneUpload?>(null)}
    var anotherUpload by remember {mutableStateOf<PhoneUpload?>(null)}
    var clearOrphanUploads by remember {mutableStateOf(false)}
    var renameUpload by remember {mutableStateOf<PhoneUpload?>(null)}
    var uploadName by remember {mutableStateOf("")}
    var discardIncoming by remember {mutableStateOf<PhoneIncomingCopy?>(null)}
    var incomingPage by rememberSaveable {mutableIntStateOf(0)}
    var archiveIncoming by remember {mutableStateOf(false)}
    var archiveBatchPlans by remember {mutableStateOf(false)}
    if(archiveBatchPlans) AlertDialog(onDismissRequest={archiveBatchPlans=false},title={Text("Archive batch-plan history?")},
        text={Text("Connections will pause and batch verification will stop. Metadata is preserved in private storage. Provider documents and unfinished private parts are kept; reservations are released and earlier requests lose receipt lookup. Keep the archived receipts to review existing copies before deliberately starting another batch.")},
        confirmButton={TextButton({model.archiveIncomingBatchPlans(); archiveBatchPlans=false}) {Text("Preserve and reset")}},dismissButton={TextButton({archiveBatchPlans=false}) {Text("Keep history")}})
    if(archiveIncoming) AlertDialog(onDismissRequest={archiveIncoming=false},title={Text("Archive incoming-copy history?")},
        text={Text("This disconnects, stops incoming verification and preserves the current receipt metadata before starting a fresh history. Private staging and every provider document are kept. Earlier sends cannot resume automatically. Review existing copies before deliberately starting new ones.")},
        confirmButton={TextButton({model.archiveIncomingHistory(); archiveIncoming=false}) {Text("Preserve and reset")}},dismissButton={TextButton({archiveIncoming=false}) {Text("Keep history")}})
    discardIncoming?.let {item->AlertDialog(onDismissRequest={discardIncoming=null},title={Text("Discard private incoming staging?")},
        text={Text("This stops incoming work for ${item.name} and removes only the identified private staged file. Provider copies, including incomplete or uncertain ones, are kept. You may need a deliberately new copy from the sender afterward.")},
        confirmButton={TextButton({model.discardIncomingPrivate(item.id); discardIncoming=null}) {Text("Discard private staging")}},dismissButton={TextButton({discardIncoming=null}) {Text("Keep")}})}
    renameUpload?.let {item->AlertDialog(onDismissRequest={renameUpload=null},title={Text("Receiving filename")},
        text={OutlinedTextField(uploadName,{uploadName=it.take(255)},label={Text("New copy name")})},
        confirmButton={TextButton({model.renameUpload(item.id,uploadName); renameUpload=null}) {Text("Use name")}},dismissButton={TextButton({renameUpload=null}) {Text("Cancel")}})}
    removeUpload?.let {item->AlertDialog(onDismissRequest={removeUpload=null},title={Text("Remove private send copy?")},
        text={Text("This stops this send and removes only iConnect's private snapshot and local record for ${item.name}. The original source file and any receiver copies are kept. An interrupted receiver copy may still need review on the computer.")},
        confirmButton={TextButton({model.removeUpload(item.id); removeUpload=null}) {Text("Remove private copy")}},dismissButton={TextButton({removeUpload=null}) {Text("Keep")}})}
    anotherUpload?.let {item->AlertDialog(onDismissRequest={anotherUpload=null},title={Text("Prepare another receiver copy?")},
        text={Text("An earlier receiver copy may already exist or still need review. This deliberately starts a new request from the retained private snapshot of ${item.name}. Existing files are kept; choose the destination again.")},
        confirmButton={TextButton({model.anotherUpload(item.id); anotherUpload=null}) {Text("Prepare another copy")}},dismissButton={TextButton({anotherUpload=null}) {Text("Cancel")}})}
    if(clearOrphanUploads) AlertDialog(onDismissRequest={clearOrphanUploads=false},title={Text("Remove orphaned private send copies?")},
        text={Text("This permanently removes private upload snapshots that have no local queue record. Original source files and computer copies are kept. These snapshots cannot be resumed afterward.")},
        confirmButton={TextButton({model.removeOrphanUploads(); clearOrphanUploads=false}) {Text("Remove orphaned copies")}},dismissButton={TextButton({clearOrphanUploads=false}) {Text("Keep")}})
    var deleteItem by remember { mutableStateOf<com.iconnect.protocol.FileEntry?>(null) }
    var restoreItem by remember { mutableStateOf<com.iconnect.protocol.DeletedInfo?>(null) }
    var restoreName by remember { mutableStateOf("") }
    val pairingReady = !state.savingTrust && !state.revocationPending && state.trustIssue.isEmpty()
    if(resetAutomaticConnections) AlertDialog(onDismissRequest={resetAutomaticConnections=false},title={Text("Reset automatic connection settings?")},
        text={Text("Earlier reconnect settings are preserved in a local metadata archive. Automatic connections will be off and paused. Pairing, sharing approvals, copied files and download progress are kept.")},
        confirmButton={TextButton({model.resetReconnect(); resetAutomaticConnections=false}) {Text("Preserve and reset")}},
        dismissButton={TextButton({resetAutomaticConnections=false}) {Text("Cancel")}})
    val picker = rememberLauncherForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
        if (result.resultCode == Activity.RESULT_OK) result.data?.let { data ->
            data.data?.let { selectedTree = it.toString(); selectedFlags = data.flags }
        }
    }
    val scan = rememberLauncherForActivityResult(ScanContract()) { result ->
        result.contents?.let { model.setInvitation(it) }
    }
    val destinationPicker = rememberLauncherForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
        if (result.resultCode == Activity.RESULT_OK) result.data?.let { data -> data.data?.let { model.chooseDownloadDestination(it, data.flags) } }
    }
    var repeatSave by remember { mutableStateOf<String?>(null) }
    var pendingBackground by remember { mutableStateOf<String?>(null) }
    var pendingSending by remember { mutableStateOf(false) }
    val context = LocalContext.current
    val notificationPermission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        val id = pendingBackground; pendingBackground = null
        if (granted && id != null) { if (pendingSending) model.allowBackgroundSending(id) else model.allowBackground(id) }
    }
    recoveryItem?.let { entry -> AlertDialog(onDismissRequest = { recoveryItem = null }, title = { Text("Archive unreadable progress?") },
        text = { Text("The damaged metadata is preserved in local recovery storage. Private staging, copied files and folders are kept. This record will no longer occupy its queue; you may need to start a new copy. Sending-ticket recovery requires the receiving device to start a new transfer.") },
        confirmButton = { TextButton({ model.archiveTransferRecovery(entry.area, entry.issue.id); recoveryItem = null }) { Text("Archive progress") } },
        dismissButton = { TextButton({ recoveryItem = null }) { Text("Cancel") } }) }
    changeName?.let { current -> AlertDialog(onDismissRequest = { changeName = null }, title = { Text(if (renameChange) "Rename selected item?" else "Create computer folder?") },
        text = { Column { Text("The change happens on the connected computer. Existing destinations are kept. Review recent changes if the response is interrupted.")
            OutlinedTextField(current, { changeName = it.take(255) }, label = { Text(if (renameChange) "New name" else "Folder name") }) } },
        confirmButton = { TextButton({ if (renameChange) model.renameSelected(current) else model.createFolder(current); changeName = null }) { Text(if (renameChange) "Rename" else "Create folder") } },
        dismissButton = { TextButton({ changeName = null }) { Text("Cancel") } }) }
    if (confirmMove) AlertDialog(onDismissRequest = { confirmMove = false }, title = { Text("Move ${state.pendingMove?.name.orEmpty()} here?") },
        text = { Text("The item leaves its current folder only if the same-volume move succeeds. An existing destination is kept. Review recent changes after an interrupted response.") },
        confirmButton = { TextButton({ model.moveHere(); confirmMove = false }) { Text("Move here") } }, dismissButton = { TextButton({ confirmMove = false }) { Text("Cancel") } })
    deleteItem?.let { item -> AlertDialog(onDismissRequest = { deleteItem = null }, title = { Text("Delete ${item.name} to recovery?") },
        text = { Text("The item leaves its current folder on the computer and is kept in iConnect recovery on the same volume. Review deleted computer items to restore it. No permanent deletion is performed.") },
        confirmButton = { TextButton({ model.deleteSelected(item); deleteItem = null }) { Text("Delete to recovery") } },
        dismissButton = { TextButton({ deleteItem = null }) { Text("Cancel") } }) }
    restoreItem?.let { item -> AlertDialog(onDismissRequest = { restoreItem = null }, title = { Text("Restore ${item.name}?") },
        text = { Column { Text("Restore to the original parent folder on the computer. An existing item is kept; choose another name if needed.")
            OutlinedTextField(restoreName, { restoreName = it.take(255) }, label = { Text("Restore name") }) } },
        confirmButton = { TextButton({ model.restoreDeleted(item, restoreName); restoreItem = null }) { Text("Restore") } },
        dismissButton = { TextButton({ restoreItem = null }) { Text("Cancel") } }) }
    if (clearRecoveryBackups) AlertDialog(onDismissRequest = { clearRecoveryBackups = false }, title = { Text("Permanently remove preserved metadata?") },
        text = { Text("This removes only archived progress metadata and cannot be undone. Private partial files, copied files, folders and live queue records are kept. You will lose these archived details for future review.") },
        confirmButton = { TextButton({ model.clearPreservedProgress(); clearRecoveryBackups = false }) { Text("Remove metadata backups") } },
        dismissButton = { TextButton({ clearRecoveryBackups = false }) { Text("Cancel") } })
    state.batchPlan?.let { plan -> AlertDialog(onDismissRequest = model::dismissBatchPlan, title = { Text("Copy selected files and folders?") },
        text = { Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text("${plan.manifest.files} files · ${plan.manifest.items.count { it.directory }} folders · ${plan.manifest.bytes} bytes")
            Text("Copy to your selected download folder. Empty folders are included. Existing folders are kept separate. Keep iConnect open; leaving the app pauses this batch.")
            val errors = plan.manifest.items.count { it.error.isNotEmpty() }
            if (errors > 0) Text("$errors items cannot be copied and will have explicit failed results.")
            PhoneCollision.entries.forEach { choice -> Row {
                RadioButton(batchCollision == choice, { batchCollision = choice })
                Text(when (choice) { PhoneCollision.KEEP_BOTH -> "Keep both using a new name"; PhoneCollision.SKIP -> "Skip existing names"; PhoneCollision.FAIL -> "Fail colliding items" }, Modifier.padding(top = 12.dp))
            } }
        } }, confirmButton = { TextButton({ model.confirmBatch(batchCollision); model.clearExplorerSelection() }) { Text("Start copy") } },
        dismissButton = { TextButton(model::dismissBatchPlan) { Text("Cancel") } }) }
    anotherBatchCopy?.let { (id, child) -> AlertDialog(onDismissRequest = { anotherBatchCopy = null }, title = { Text("Save another batch copy?") },
        text = { Text("The previous provider copy may be partial or complete. It will be kept untouched. This explicitly creates another copy of this item with an unused name, then continues the batch.") },
        confirmButton = { TextButton({ model.resumeBatch(id, child); anotherBatchCopy = null }) { Text("Save another copy") } },
        dismissButton = { TextButton({ anotherBatchCopy = null }) { Text("Cancel") } }) }
    skipBatchFolder?.let { (id, index) -> AlertDialog(onDismissRequest = { skipBatchFolder = null }, title = { Text("Skip uncertain folder?") },
        text = { Text("A folder may have been created before its ID was saved. Review the destination first. This keeps that folder and any contents, skips this branch, and lets you resume other selected items.") },
        confirmButton = { TextButton({ model.skipUncertainBatchFolder(id, index); skipBatchFolder = null }) { Text("Skip this branch") } },
        dismissButton = { TextButton({ skipBatchFolder = null }) { Text("Cancel") } }) }
    state.fileDetails?.let { entry -> AlertDialog(onDismissRequest = model::dismissFileDetails, title = { Text("Item details") },
        text = { Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text(entry.name); Text(if (entry.isDirectory) "Folder" else "File · ${entry.size} bytes")
            Text("Modified: ${java.time.Instant.ofEpochMilli(entry.modifiedAt)}")
            Text("Location permissions: " + listOf(StorageRules.LIST to "list folders", StorageRules.READ to "read files", StorageRules.CREATE to "create folders",
                StorageRules.WRITE to "write", StorageRules.RENAME to "rename", StorageRules.MOVE to "move", StorageRules.DELETE to "delete", StorageRules.REPLACE to "replace")
                .filter { entry.permissions and it.first != 0 }.joinToString { it.second })
        } }, confirmButton = { TextButton(model::dismissFileDetails) { Text("Done") } }) }
    if (resetPairing) AlertDialog(onDismissRequest = { resetPairing = false }, title = { Text("Reset pairing data?") },
        text = { Text("This forgets saved computers and removes their phone folder approvals. Phone files, local downloads and the selected download folder are kept. Pair again and approve folders before allowing future access.") },
        confirmButton = { TextButton({ model.resetPairingData(); resetPairing = false }) { Text("Reset pairing data") } },
        dismissButton = { TextButton({ resetPairing = false }) { Text("Cancel") } })
    if (repeatSave != null) AlertDialog(onDismissRequest = { repeatSave = null }, title = { Text("Save another copy?") },
        text = { Text("A previous save may have left a partial or complete file in the destination folder. It will be left untouched. This creates another file with an unused name.") },
        confirmButton = { TextButton({ model.saveDownload(requireNotNull(repeatSave), true); repeatSave = null }) { Text("Save another copy") } },
        dismissButton = { TextButton({ repeatSave = null }) { Text("Cancel") } })
    if (selectedTree.isNotEmpty() && state.peerName.isNotEmpty()) {
        AlertDialog(onDismissRequest = { selectedTree = "" }, title = { Text("Share selected phone folder?") },
            text = {
                Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
                    Text("${state.peerName} will be able to list and read files in the selected folder while iConnect is open. Sensitive folders are excluded.")
                    OutlinedTextField(shareLabel, { shareLabel = it.take(255) }, label = { Text("Shared location name") })
                }
            }, confirmButton = {
                TextButton({ model.addPhoneShare(Uri.parse(selectedTree), selectedFlags, shareLabel); selectedTree = "" },
                    enabled = shareLabel.isNotBlank() && !state.savingShare && pairingReady) { Text("Approve read-only sharing") }
            }, dismissButton = { TextButton({ selectedTree = "" }) { Text("Cancel") } })
    }
    Surface(Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
        Column(Modifier.fillMaxSize().windowInsetsPadding(WindowInsets.safeDrawing)
            .verticalScroll(rememberScrollState()).padding(24.dp), verticalArrangement = Arrangement.spacedBy(16.dp)) {
            Text("iConnect", style = MaterialTheme.typography.headlineLarge, fontWeight = FontWeight.Bold)
            Text("Devices and approved files", style = MaterialTheme.typography.titleMedium, color = MaterialTheme.colorScheme.primary)
            if (state.preservedProgressRecords != 0) {
                Text("${state.preservedProgressRecords} preserved progress backups. Partial files and saved copies are stored separately.")
                TextButton({ clearRecoveryBackups = true }) { Text("Remove preserved metadata backups…") }
            }
            OutlinedButton({ onPhone = true }, modifier = Modifier.fillMaxWidth()) { Text("On this phone · local folders") }
            Text("Pair trusted devices, browse approved computer folders, and verify the connection.",
                style = MaterialTheme.typography.bodyMedium)
            if(state.transferRecovery.any {it.area=="Batch plans"}) IncomingBatchPlanHistoryCard(state) {archiveBatchPlans=true}
            if (state.transferRecovery.isNotEmpty()) Card { Column(Modifier.padding(18.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                Text("Review saved progress", style = MaterialTheme.typography.titleLarge)
                Text("Some saved progress could not be validated. Affected queues are paused. Archiving keeps partial files and saved copies; unreadable progress cannot resume automatically.")
                val page = recoveryPage.coerceIn(0, (state.transferRecovery.size - 1) / 20)
                state.transferRecovery.drop(page * 20).take(20).forEach { entry ->
                    Text("${entry.area} progress needs review", fontWeight = FontWeight.SemiBold); Text(entry.issue.message)
                    TextButton({ if(entry.area=="Batch plans") archiveBatchPlans=true else recoveryItem = entry }, enabled = entry.area=="Batch plans" || entry.issue.canArchive) {
                        Text(if(entry.area=="Batch plans") "Review batch-plan history…" else "Archive progress record…") }
                }
                Text("Recovery page ${page+1} / ${maxOf(1,(state.transferRecovery.size+19)/20)}")
                Row { TextButton({ recoveryPage = page - 1 }, enabled = page > 0) { Text("Previous recovery items") }
                    TextButton({ recoveryPage = page + 1 }, enabled = (page + 1) * 20 < state.transferRecovery.size) { Text("Next recovery items") } }
            } }
            Card {
                Column(Modifier.padding(18.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                    Text(state.status, style = MaterialTheme.typography.titleSmall)
                    if (state.savingTrust) { LinearProgressIndicator(Modifier.fillMaxWidth()); Text("Saving pairing changes…") }
                    if (state.trustIssue.isNotEmpty()) Text(state.trustIssue, color = MaterialTheme.colorScheme.error)
                    if (state.trustNotice.isNotEmpty()) Text(if (state.trustNotice == "identity-changed")
                        "This phone's identity changed. Pair your computer again and approve folders for the new pairing."
                        else "Earlier pairing data needs fresh approval. Pair your computer again; saved folder selections remain inactive.")
                    if (state.revocationPending && !state.savingTrust) TextButton(model::retryRevocation) { Text("Retry saving revocation") }
                    if (state.trustIssue.isNotEmpty() || state.revocationPending) TextButton({ resetPairing = true },
                        enabled = !state.savingTrust) { Text("Reset pairing data…") }
                    TransferNetworkPanel(state.transferNetwork,model::setTransferNetwork,model::resetTransferNetwork,model::clearTransferNetworkBackups)
                    OutlinedTextField(state.server, model::setServer, label = { Text("Coordinator address") },
                        placeholder = { Text("https://your-coordinator.example") }, singleLine = true,
                        enabled = !state.connected && !state.connecting && !state.reconnectSaving, modifier = Modifier.fillMaxWidth())
                    OutlinedTextField(state.stun,model::setStun,label={Text("STUN address (optional)")},placeholder={Text("stun:host:3478")},singleLine=true,
                        enabled=!state.connected && !state.connecting && !state.reconnectSaving,modifier=Modifier.fillMaxWidth())
                    Text("STUN discovers direct network endpoints. Its operator sees network metadata. No relay is used. Settings are saved after connecting to the coordinator.",style=MaterialTheme.typography.bodySmall)
                    if (!state.connected && !state.connecting) {
                        Button(model::connect, enabled = pairingReady && !state.reconnectSaving && !state.presenceSaving, modifier = Modifier.fillMaxWidth()) { Text("Connect") }
                    } else {
                        OutlinedButton(model::pauseConnections, modifier = Modifier.fillMaxWidth()) { Text("Disconnect and pause") }
                    }
                    Row(verticalAlignment=androidx.compose.ui.Alignment.CenterVertically) {
                        Switch(state.autoReconnect,model::setAutoReconnect,modifier=Modifier.semantics {contentDescription="Reconnect to this computer while the app is open"},
                            enabled=pairingReady && state.peerName.isNotEmpty() && !state.reconnectSaving && state.reconnectIssue.isEmpty())
                        Text("Reconnect to this computer while the app is open",Modifier.padding(start=10.dp))
                    }
                    Text("Reconnect uses the saved pairing and address. Saved transfers stay paused until you resume them. It does not start connections in the background.",style=MaterialTheme.typography.bodySmall)
                    Row(verticalAlignment=androidx.compose.ui.Alignment.CenterVertically) {
                        Switch(state.sharePresence,model::setSharePresence,enabled=!state.connected && !state.connecting && !state.presenceSaving,
                            modifier=Modifier.semantics {contentDescription="Share availability with paired devices"})
                        Text("Share availability with paired devices",Modifier.padding(start=10.dp))
                    }
                    Text("Off initially. Sends your paired device ID to the coordinator only while this app is open. Both devices must opt in. Reports expire and do not authenticate a device or approve file access. Disconnect before changing this option.",style=MaterialTheme.typography.bodySmall)
                    if(state.presenceSaving) Text("Saving availability preference…")
                    if(state.presenceIssue.isNotEmpty()) Text(state.presenceIssue,color=MaterialTheme.colorScheme.error)
                    if(state.reconnectSaving) {LinearProgressIndicator(Modifier.fillMaxWidth()); Text("Saving automatic connection settings…",style=MaterialTheme.typography.bodySmall)}
                    if(state.reconnectNotice.isNotEmpty()) Text(state.reconnectNotice,style=MaterialTheme.typography.bodySmall)
                    if(state.reconnectIssue.isNotEmpty()) {
                        Text(state.reconnectIssue,color=MaterialTheme.colorScheme.error)
                        TextButton({resetAutomaticConnections=true},enabled=!state.reconnectSaving) {Text("Reset automatic connection settings…")}
                    }
                    if(state.autoReconnect && state.reconnectPaused && state.reconnectIssue.isEmpty()) TextButton(model::resumeConnections,enabled=!state.reconnectSaving && pairingReady) {Text("Resume automatic connections")}
                    if(state.autoReconnect && !state.reconnectPaused) TextButton(model::pauseConnections) {Text("Pause connections")}
                }
            }
            if (state.peerName.isEmpty()) {
                Card {
                    Column(Modifier.padding(18.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                        Text("Pair a computer", style = MaterialTheme.typography.titleMedium)
                        Text("Create an invitation on Windows. Scan it before connecting, or paste the invitation below.")
                        OutlinedButton({ scan.launch(ScanOptions().setPrompt("Scan the iConnect invitation")
                            .setBeepEnabled(false).setOrientationLocked(false)) }, enabled = !state.connected && !state.connecting && pairingReady,
                            modifier = Modifier.fillMaxWidth()) { Text("Scan QR invitation") }
                        OutlinedTextField(state.invitation, model::setInvitation, label = { Text("Pairing invitation") },
                            minLines = 2, maxLines = 4, modifier = Modifier.fillMaxWidth())
                        Button(model::requestPairing, enabled = state.connected && state.invitation.isNotBlank() && state.code.isEmpty() && pairingReady,
                            modifier = Modifier.fillMaxWidth()) { Text("Request pairing") }
                        if (state.code.isNotEmpty()) {
                            Text(state.code, style = MaterialTheme.typography.headlineLarge, fontFamily = FontFamily.Monospace)
                            Text("Codes must match. Approve only the computer you intended to pair. Storage sharing is approved separately.")
                            OutlinedTextField(verification, { verification = it.filter(Char::isDigit).take(6) },
                                label = { Text("Code shown on Windows") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                            Button({ model.approve(verification) }, enabled = verification.length == 6 && pairingReady,
                                modifier = Modifier.fillMaxWidth()) { Text("Approve computer") }
                            TextButton(model::reject) { Text("Reject") }
                        }
                    }
                }
            } else {
                Card {
                    Column(Modifier.padding(18.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                        Text(state.peerName, style = MaterialTheme.typography.titleLarge)
                        Text(if (state.authenticated) "Authenticated direct connection" else if(state.peerConnecting) "Connecting directly…" else if(!state.connected) "Coordinator disconnected · availability unknown."
                            else if(!state.sharePresence) "Availability reports are off. Connect to authenticate."
                            else if(!state.presenceSupported) "Coordinator does not support availability reports. Connect to authenticate."
                            else if(state.peerAvailable) "Coordinator reports available · connect to authenticate."
                            else "Availability unknown · both devices must opt in and be connected. You can still try connecting.")
                        if(!state.authenticated && !state.peerConnecting) Button(model::connectPeer,enabled=state.connected && pairingReady,modifier=Modifier.fillMaxWidth()) {Text("Connect to computer")}
                        else OutlinedButton(model::disconnectPeer,modifier=Modifier.fillMaxWidth()) {Text(if(state.peerConnecting) "Cancel direct connection" else "Disconnect from computer")}
                        Button(model::browseRoots, enabled = state.authenticated, modifier = Modifier.fillMaxWidth()) {
                            Text("Browse computer files")
                        }
                        Button(model::sendTestFile, enabled = state.authenticated, modifier = Modifier.fillMaxWidth()) {
                            Text("Send 256 KiB test file")
                        }
                        TextButton(model::forgetPeer, enabled = !state.savingTrust) { Text("Forget computer") }
                    }
                }
            }
            if (state.peerName.isNotEmpty() || state.phoneShares.isNotEmpty()) {
                Card {
                    Column(Modifier.padding(18.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                        Text("Shared phone folders", style = MaterialTheme.typography.titleLarge)
                        Text(if (state.peerName.isNotEmpty()) "Choose a folder while disconnected, then approve it for ${state.peerName}. Android limits which folders can be selected."
                            else "Review or remove saved folder approvals. Pair a computer to approve a new folder.")
                        OutlinedButton({ picker.launch(Intent(Intent.ACTION_OPEN_DOCUMENT_TREE).apply {
                            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION or
                                Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION or Intent.FLAG_GRANT_PREFIX_URI_PERMISSION)
                        }) }, enabled = state.peerName.isNotEmpty() && !state.connected && !state.connecting && !state.savingShare && pairingReady,
                            modifier = Modifier.fillMaxWidth()) { Text("Choose phone folder") }
                        if (state.phoneShares.isEmpty()) Text("No phone folders are shared.")
                        state.phoneShares.forEach { grant ->
                            Text(grant.label, fontWeight = FontWeight.SemiBold)
                            if (!model.phoneApprovalCurrent(grant)) Text("Inactive. Choose this folder again to approve it for the current pairing.")
                            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                                Text("Allow creating folders", modifier = Modifier.weight(1f))
                                Switch(checked = grant.permissions and StorageRules.CREATE != 0,
                                    onCheckedChange = { model.setPhoneCreate(grant.id, it) },
                                    enabled = !state.savingShare && pairingReady && model.phoneApprovalCurrent(grant))
                            }
                            Row(Modifier.fillMaxWidth(),horizontalArrangement=Arrangement.SpaceBetween) {
                                Text("Receive new file copies",Modifier.weight(1f))
                                Switch(checked=grant.permissions and StorageRules.WRITE!=0,onCheckedChange={model.setPhoneReceive(grant.id,it)},
                                    enabled=!state.savingShare && pairingReady && model.phoneApprovalCurrent(grant))
                            }
                            TextButton({model.setPhoneReceiveOnly(grant.id)},enabled=!state.savingShare && pairingReady && model.phoneApprovalCurrent(grant)) {Text("Use as receive-only inbox")}
                            TextButton({model.setPhoneReadOnly(grant.id)},enabled=!state.savingShare && pairingReady && model.phoneApprovalCurrent(grant)) {Text("Use read-only sharing")}
                            Text("Incoming files require Write and Create approval and stay private until verified. Keep iConnect open. Provider publication is checked separately; uncertain copies are preserved.",style=MaterialTheme.typography.bodySmall)
                            TextButton({ model.removePhoneShare(grant.id) }, enabled = !state.savingShare) { Text("Stop sharing") }
                        }
                    }
                }
            }
            if (state.browsing && state.authenticated) {
                Card {
                    Column(Modifier.padding(18.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                        Text("Computer storage", style = MaterialTheme.typography.titleLarge)
                        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                            TextButton(model::browseRoots, enabled = !state.listingBusy) { Text("Locations") }
                            if (state.breadcrumbs.size > 1) TextButton({ model.browseFolder(state.breadcrumbs[state.breadcrumbs.lastIndex - 1]) },
                                enabled = !state.listingBusy) { Text("Up") }
                        }
                        if (state.breadcrumbs.isNotEmpty()) Text(state.breadcrumbs.joinToString(" / ") { it.name },
                            style = MaterialTheme.typography.labelLarge)
                        OutlinedTextField(fileQuery, { fileQuery = it.take(128) }, label = { Text("Search filenames in this folder and descendants") },
                            enabled = state.breadcrumbs.isNotEmpty() && !state.listingBusy && !state.planningBatch, modifier = Modifier.fillMaxWidth())
                        Row { TextButton({ model.searchFiles(fileQuery) }, enabled = fileQuery.isNotBlank() && state.breadcrumbs.isNotEmpty() && !state.listingBusy && !state.planningBatch) { Text("Search scope") }
                            TextButton({ state.breadcrumbs.lastOrNull()?.let(model::browseFolder) ?: model.browseRoots() }, enabled = !state.listingBusy && !state.planningBatch) { Text("Refresh / clear search") } }
                        if (state.listingBusy) TextButton(model::cancelDiscovery) { Text("Cancel discovery") }
                        Row { TextButton({ model.setExplorerSort(PhoneExplorerSort.entries[(state.explorerSort.ordinal + 1) % PhoneExplorerSort.entries.size], state.explorerDescending) }) { Text("Sort: ${state.explorerSort.name.lowercase()}") }
                            TextButton({ model.setExplorerSort(state.explorerSort, !state.explorerDescending) }) { Text(if (state.explorerDescending) "Descending" else "Ascending") }
                            TextButton({ model.setExplorerLayout(if (state.explorerLayout == PhoneExplorerLayout.LIST) PhoneExplorerLayout.GRID else PhoneExplorerLayout.LIST) }) { Text(if (state.explorerLayout == PhoneExplorerLayout.LIST) "Grid" else "List") } }
                        Text("${state.explorerCount} loaded items · page ${state.explorerPage + 1} / ${state.explorerPages}" + if (state.searchQuery.isEmpty()) "" else " · Search: ${state.searchQuery}")
                        if (state.discoveryNotice.isNotEmpty()) Text(state.discoveryNotice, color = MaterialTheme.colorScheme.error)
                        if (state.listingBusy) LinearProgressIndicator(Modifier.fillMaxWidth())
                        if (state.fileError.isNotEmpty()) Text(state.fileError, color = MaterialTheme.colorScheme.error)
                        if (state.remoteEntries.isEmpty() && !state.listingBusy) Text(
                            if (!state.discoveryComplete) "Discovery was incomplete. Review the notice above and retry."
                            else if (state.searchQuery.isNotEmpty()) "No loaded filenames match this search."
                            else if (state.breadcrumbs.isEmpty()) "No storage is shared. Approve a drive or folder on the computer."
                            else "No accessible items in this folder.")
                        Text("${state.selectedFiles.size} selected across result pages. Sorting and search retain selection; navigation or refresh clears it.")
                        if (state.mutationMessage.isNotEmpty()) Text(state.mutationMessage)
                        Row {
                            TextButton({ renameChange = false; changeName = "" }, enabled = !state.mutationBusy && state.breadcrumbs.lastOrNull()?.let { it.permissions and StorageRules.CREATE != 0 } == true) { Text("New computer folder…") }
                            TextButton({ renameChange = true; changeName = state.selectedFiles.single().entry.name }, enabled = !state.mutationBusy && state.selectedFiles.singleOrNull()?.entry?.let { !it.isRoot && it.permissions and StorageRules.RENAME != 0 } == true) { Text("Rename selected…") }
                        }
                        TextButton(model::beginMove, enabled = !state.mutationBusy && state.selectedFiles.singleOrNull()?.entry?.let { !it.isRoot && it.permissions and StorageRules.MOVE != 0 } == true) { Text("Move selected…") }
                        TextButton({ deleteItem = state.selectedFiles.single().entry }, enabled = !state.mutationBusy && state.selectedFiles.singleOrNull()?.entry?.let { !it.isRoot && it.permissions and StorageRules.DELETE != 0 } == true) { Text("Delete selected to recovery…") }
                        state.pendingMove?.let { moving ->
                            Text("Choose a destination for ${moving.name}. Open an approved folder, then confirm Move here.")
                            Row { TextButton({ confirmMove = true }, enabled = !state.mutationBusy && state.breadcrumbs.isNotEmpty()) { Text("Move here…") }; TextButton(model::cancelMove) { Text("Cancel move") } }
                        }
                        TextButton(model::refreshMutations, enabled = !state.mutationBusy) { Text("Review recent computer changes") }
                        TextButton({ model.refreshDeleted() }, enabled = !state.mutationBusy) { Text("Review deleted computer items") }
                        state.deletedItems.forEach { item ->
                            val outcome = when (item.state) { com.iconnect.protocol.DeletedState.DELETED_STORED -> "Saved for recovery"; com.iconnect.protocol.DeletedState.DELETED_RESTORED -> "Restored"; else -> "Needs review" }
                            Text("${item.name} · $outcome\n${item.message}", style = MaterialTheme.typography.bodySmall)
                            TextButton({ restoreName = item.name; restoreItem = item }, enabled = !state.mutationBusy && item.canRestore) { Text("Restore ${item.name}…") }
                        }
                        if (state.moreDeleted) TextButton({ model.refreshDeleted(true) }, enabled = !state.mutationBusy) { Text("More deleted items") }
                        if (state.recentMutations.isNotEmpty()) TextButton({ showChangeHistory = !showChangeHistory }) { Text(if (showChangeHistory) "Hide recent changes" else "Show recent changes (${state.recentMutations.size})") }
                        if (showChangeHistory) state.recentMutations.forEach { change ->
                            val operation = when (change.operation) { com.iconnect.protocol.FileOperation.CREATE_DIRECTORY -> "Create folder"; com.iconnect.protocol.FileOperation.RENAME -> "Rename"; com.iconnect.protocol.FileOperation.DELETE -> "Delete to recovery"; com.iconnect.protocol.FileOperation.RESTORE -> "Restore"; else -> "Move" }
                            val outcome = when (change.phase) { com.iconnect.protocol.MutationPhase.CHANGE_SUCCEEDED -> "Completed"; com.iconnect.protocol.MutationPhase.CHANGE_FAILED -> "Kept existing item"; else -> "Needs review" }
                            Text("$operation · ${change.requestedName} · $outcome\n${change.message}", style = MaterialTheme.typography.bodySmall)
                            if(change.operation==com.iconnect.protocol.FileOperation.CREATE_DIRECTORY && change.phase==com.iconnect.protocol.MutationPhase.CHANGE_SUCCEEDED)
                                TextButton({model.useCreatedFolderForSending(change.id)},enabled=!state.uploadDestinationsBusy && state.authenticated) {Text("Use this created folder for sending")}
                        }
                        TextButton(model::clearExplorerSelection, enabled = state.selectedFiles.isNotEmpty()) { Text("Clear selection") }
                        if (state.planningBatch) { LinearProgressIndicator(Modifier.fillMaxWidth()); Text("Preparing folder plan…") }
                        Button({ model.planBatch(state.selectedFiles.map { it.entry }) },
                            enabled = state.selectedFiles.isNotEmpty() && !state.planningBatch && !state.listingBusy && state.downloadDestination.isNotEmpty()) { Text("Copy selected files and folders…") }
                        state.explorerRows.chunked(if (state.explorerLayout == PhoneExplorerLayout.GRID) 2 else 1).forEach { items -> Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        items.forEach { row -> Column(Modifier.weight(1f)) { val entry = row.entry
                            Row {
                            Checkbox(state.selectedFiles.any { it.entry.id == entry.id }, { checked -> model.selectExplorerItem(entry.id, checked) },
                                enabled = !state.planningBatch && (state.selectedFiles.any { it.entry.id == entry.id } || state.selectedFiles.size < 128),
                                modifier = Modifier.semantics { contentDescription = "Select ${entry.name}" })
                            OutlinedButton({ if (entry.isDirectory) model.browseFolder(entry) else model.previewText(entry) },
                                enabled = !state.listingBusy && !state.planningBatch, modifier = Modifier.weight(1f)) {
                                Column(Modifier.fillMaxWidth()) {
                                    Text((if (entry.isDirectory) "Folder: " else "File: ") + entry.name)
                                    if (!entry.isDirectory) Text("${entry.size} bytes", style = MaterialTheme.typography.bodySmall)
                                    Text(if (state.searchQuery.isEmpty()) row.type else row.relativePath, style = MaterialTheme.typography.bodySmall)
                                }
                            }
                            }
                            TextButton({ model.showFileDetails(entry) }, enabled = !state.listingBusy && !state.planningBatch) { Text("Details") }
                            if (!entry.isDirectory && entry.permissions and StorageRules.READ != 0) TextButton({ model.downloadFile(entry) },
                                enabled = state.downloadDestination.isNotEmpty() && !state.downloadBusy) { Text("Download ${entry.name}") }
                        } }
                        if (items.size == 1 && state.explorerLayout == PhoneExplorerLayout.GRID) Spacer(Modifier.weight(1f))
                        } }
                        Row { TextButton(model::previousDirectoryPage, enabled = state.explorerPage > 0 && !state.listingBusy) { Text("Previous page") }
                            TextButton(model::nextDirectoryPage, enabled = state.explorerPage + 1 < state.explorerPages && !state.listingBusy) { Text("Next page") } }
                        FilePreviewPanel(filePreview,model.preview::page,model.preview::clear)
                        if (state.textPreview.isNotEmpty()) {
                            Text("Text preview (up to 32 KiB)", style = MaterialTheme.typography.labelLarge)
                            Text(state.textPreview, style = MaterialTheme.typography.bodySmall, fontFamily = FontFamily.Monospace)
                        }
                    }
                }
            }
            state.downloads.filter { child -> child.batchId.isNotEmpty() && state.batches.none { it.id == child.batchId } }.forEach { child -> Card {
                Column(Modifier.padding(18.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    Text("File belongs to a missing batch", style = MaterialTheme.typography.titleLarge); Text(child.name)
                    Text("The original batch is unavailable. This file cannot become a standalone or background transfer. Saved provider files are kept.")
                    if (child.phase != PhoneDownloadPhase.COMPLETED) TextButton({ model.cancelDownload(child.id) }) { Text("Cancel / clean private partial") }
                    if (child.phase in setOf(PhoneDownloadPhase.COMPLETED, PhoneDownloadPhase.CANCELED, PhoneDownloadPhase.SKIPPED)) TextButton({ model.removeDownload(child.id) }) { Text("Remove file record · keep saved files") }
                }
            } }
            state.batches.forEach { batch -> Card {
                var page by remember(batch.id) { mutableIntStateOf(0) }
                val pages = (batch.items.size + 49) / 50
                Column(Modifier.padding(18.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    Text("Folder / file batch", style = MaterialTheme.typography.titleLarge)
                    Text("${batch.phase.name.lowercase().replace('_', ' ')} · ${batch.completed} / ${batch.items.size} items · ${batch.issues} issues · ${batch.verifiedBytes} verified bytes")
                    if (batch.error.isNotEmpty()) Text(batch.error)
                    state.downloads.firstOrNull { it.batchId == batch.id }?.let {file->
                        val received=state.receivingBytes[file.id] ?: file.offset
                        Text("Current file: ${file.name} · $received / ${file.size} bytes · ${file.phase.name.lowercase().replace('_', ' ')}")
                        if(file.id in state.receivingBytes) Text("Saved resume checkpoint: ${file.offset} bytes",style=MaterialTheme.typography.bodySmall)
                        if (file.active) state.downloadFinalizations[file.id]?.let { Text(it.describe(), style = MaterialTheme.typography.bodySmall) }
                        if(file.phase==PhoneDownloadPhase.DOWNLOADING) state.payloadEstimates[file.id]?.let {observed->
                            val lifetime=PhoneTransferTimeEstimate.create(file.size,received,state.transferNetwork.preferences.bytesPerSecond,file.sourceExpiresAt,System.currentTimeMillis())
                            Text(observed.text,style=MaterialTheme.typography.bodySmall); Text(lifetime.payloadText,style=MaterialTheme.typography.bodySmall); Text(lifetime.approvalText,style=MaterialTheme.typography.bodySmall)
                            (lifetime.warning.ifEmpty {observed.approvalWarning(lifetime.sourceSeconds)}).takeIf {it.isNotEmpty()}?.let {Text(it,color=MaterialTheme.colorScheme.error)}
                        }
                    }
                    state.downloads.filter {it.batchId==batch.id && it.phase in setOf(PhoneDownloadPhase.PAUSED,PhoneDownloadPhase.INTERRUPTED) && it.attempts==0 && it.published.isEmpty() && !(it.offset==it.size && it.sha256.isNotEmpty())}.forEach {file->key(batch.id,file.id) {
                        val index=batch.items.indexOfFirst {it.child==file.id}
                        if(index>=0) BatchDownloadSourceRecoveryPanel(file,batch.items[index].source.path.joinToString(" / "),
                            PhoneTransferTimeEstimate.create(file.size,file.offset,state.transferNetwork.preferences.bytesPerSecond,file.sourceExpiresAt,System.currentTimeMillis()),
                            batch.phase in setOf(PhoneBatchPhase.PAUSED,PhoneBatchPhase.INTERRUPTED) && !batch.active && state.authenticated && state.selectedFiles.size==1,
                            {model.reviewBatchDownloadSource(batch.id,index)},model::confirmBatchDownloadSource,model::cancelBatchDownloadSourceReview,state.payloadEstimates[file.id])
                    }}
                    if (batch.active) TextButton({ model.pauseBatch(batch.id) }) { Text("Pause batch") }
                    else if (batch.phase in setOf(PhoneBatchPhase.PAUSED, PhoneBatchPhase.INTERRUPTED, PhoneBatchPhase.REVIEW)) TextButton({ model.resumeBatch(batch.id) }) { Text("Check saved copies / resume batch") }
                    state.downloads.firstOrNull { it.batchId == batch.id && it.phase == PhoneDownloadPhase.PUBLICATION_INTERRUPTED }?.let { child ->
                        TextButton({ anotherBatchCopy = batch.id to child.id }, enabled = !batch.active) { Text("Save another copy of ${child.name}…") }
                    }
                    batch.items.indexOfFirst { it.phase == PhoneBatchItemPhase.CREATING && it.document.isEmpty() }.takeIf { it >= 0 }?.let { index ->
                        TextButton({ skipBatchFolder = batch.id to index }, enabled = !batch.active) { Text("Review / skip uncertain folder…") }
                    }
                    if (batch.phase != PhoneBatchPhase.COMPLETED || !model.canRemoveBatch(batch.id)) TextButton({ model.cancelBatch(batch.id) }) { Text("Cancel / clean private partials") }
                    if (model.canRemoveBatch(batch.id)) TextButton({ model.removeBatch(batch.id) }) { Text("Remove batch record · keep saved files") }
                    Text("Item results · page ${page + 1} / $pages", fontWeight = FontWeight.SemiBold)
                    Row { TextButton({ page-- }, enabled = page > 0) { Text("Previous results") }; TextButton({ page++ }, enabled = page + 1 < pages) { Text("Next results") } }
                    batch.items.drop(page * 50).take(50).forEach { item ->
                        Text("${item.source.path.ifEmpty { listOf(item.source.name) }.joinToString(" / ")} · ${item.phase.name.lowercase()}" +
                            (if (item.localName.isNotEmpty()) " · Destination name: ${item.localName}" else "") + (if (item.error.isNotEmpty()) " · ${item.error}" else ""), style = MaterialTheme.typography.bodySmall)
                    }
                }
            } }
            Card {
                Column(Modifier.padding(18.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    Text("Downloads", style = MaterialTheme.typography.titleLarge)
                    Text("Choose a destination while disconnected. Downloads are verified in private storage first. Keep iConnect open, or explicitly allow a running transfer to continue with a notification. Then save the verified file to your chosen folder.")
                    Text("Byte verification checks integrity. Received files remain untrusted; use your device's security protections before opening them.",style=MaterialTheme.typography.bodySmall)
                    if (state.backgroundReady) TextButton({ model.stopBackground() }) { Text("Pause background transfers") }
                    Text(if (state.downloadDestination.isEmpty()) "No destination selected" else "A phone destination is selected. It is not shared with the computer.")
                    OutlinedButton({ destinationPicker.launch(Intent(Intent.ACTION_OPEN_DOCUMENT_TREE).apply {
                        addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION or
                            Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION or Intent.FLAG_GRANT_PREFIX_URI_PERMISSION)
                    }) }, enabled = !state.connected && !state.connecting) { Text("Choose download folder") }
                    if (state.downloadDestination.isNotEmpty()) TextButton({ model.clearDownloadDestination() }) { Text("Clear selected destination") }
                    state.downloads.filter { it.batchId.isEmpty() }.forEach { download ->
                        HorizontalDivider()
                        Text(download.name, fontWeight = FontWeight.SemiBold)
                        val received=state.receivingBytes[download.id] ?: download.offset
                        Text("${download.phase.name.lowercase().replace('_', ' ')} · $received / ${download.size} bytes")
                        if(download.id in state.receivingBytes) Text("Saved resume checkpoint: ${download.offset} bytes",style=MaterialTheme.typography.bodySmall)
                        key(download.id) {DownloadSourceRecoveryPanel(download,PhoneTransferTimeEstimate.create(download.size,received,
                            state.transferNetwork.preferences.bytesPerSecond,download.sourceExpiresAt,System.currentTimeMillis()),
                            state.authenticated && state.selectedFiles.size==1,{model.reviewDownloadSource(download.id)},model::confirmDownloadSource,model::cancelDownloadSourceReview,observed=state.payloadEstimates[download.id])}
                        if (download.error.isNotEmpty()) Text(download.error, color = MaterialTheme.colorScheme.error)
                        if (download.publishedName.isNotEmpty()) Text("Provider file: ${download.publishedName}")
                        if (download.active) state.downloadFinalizations[download.id]?.let { Text(it.describe(), style = MaterialTheme.typography.bodySmall) }
                        if (download.active && download.id !in state.backgroundDownloads) TextButton({
                            if (Build.VERSION.SDK_INT >= 33 && context.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
                                pendingSending = false; pendingBackground = download.id; notificationPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
                            } else model.allowBackground(download.id)
                        }) { Text("Continue this transfer in background") }
                        if (download.id in state.backgroundDownloads) Text("May continue in background with a visible notification.")
                        when (download.phase) {
                            PhoneDownloadPhase.PREPARING, PhoneDownloadPhase.DOWNLOADING, PhoneDownloadPhase.VERIFYING,
                            PhoneDownloadPhase.PUBLISHING -> TextButton({ model.pauseDownload(download.id) }) { Text("Pause") }
                            PhoneDownloadPhase.PAUSED, PhoneDownloadPhase.INTERRUPTED -> TextButton({ model.resumeDownload(download.id) },
                                enabled = (state.authenticated || download.offset == download.size && download.sha256.isNotEmpty()) && !download.active) {
                                Text(if (download.offset == download.size && download.sha256.isNotEmpty()) "Verify received file" else "Resume from this computer") }
                            PhoneDownloadPhase.READY_TO_SAVE -> TextButton({ model.saveDownload(download.id) }, enabled = !download.active) { Text("Save verified file") }
                            PhoneDownloadPhase.PUBLICATION_INTERRUPTED -> {
                                TextButton({ model.saveDownload(download.id) }, enabled = !download.active) { Text("Check saved copy") }
                                TextButton({ repeatSave = download.id }, enabled = !download.active) { Text("Save another copy…") }
                            }
                            else -> Unit
                        }
                        if (download.phase in setOf(PhoneDownloadPhase.COMPLETED, PhoneDownloadPhase.CANCELED)) {
                            TextButton({ model.removeDownload(download.id) }, enabled = !download.active) { Text("Remove record") }
                        } else TextButton({ model.cancelDownload(download.id) }) { Text("Cancel and remove private partial") }
                    }
                }
            }
            PreparedSourceControlsPanel(state.preparedSources,!state.savingTrust && state.trustIssue.isEmpty(),state.changingSends,model::stopPreparedSource,model::removeExpiredPreparedSources)
            if (state.sends.isNotEmpty()) Card {
                Column(Modifier.padding(18.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    Text("Sending from this phone", style = MaterialTheme.typography.titleLarge)
                    Text("Your computer can read approved files while this app is open. You can let one active sending transfer continue with a notification. Other phone browsing remains unavailable in the background.")
                    if (state.backgroundReady) TextButton({ model.stopBackground() }) { Text("Pause background transfers") }
                    state.sends.forEach { send ->
                        HorizontalDivider(); Text(send.name, fontWeight = FontWeight.SemiBold)
                        Text(when {
                            send.paused -> "Paused"
                            send.state == com.iconnect.protocol.DownloadState.DOWNLOAD_FAILED -> "File unavailable. Start a new download on the computer."
                            !send.active -> "Waiting for the computer to resume while iConnect is open"
                            send.state == com.iconnect.protocol.DownloadState.DOWNLOAD_PREPARING -> "Preparing file · ${send.prepared} / ${send.size} bytes"
                            else -> "Ready or sending · ${send.bytesRead} bytes read for this transfer"
                        })
                        Text("The computer verifies its saved copy before reporting completion.", style = MaterialTheme.typography.bodySmall)
                        val changing = send.id in state.changingSends
                        if (send.active && !send.paused && send.id !in state.backgroundSends) TextButton({
                            if (Build.VERSION.SDK_INT >= 33 && context.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
                                pendingSending = true; pendingBackground = send.id; notificationPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
                            } else model.allowBackgroundSending(send.id)
                        }, enabled = !changing && state.authenticated) { Text("Continue this sending transfer in background") }
                        if (send.id in state.backgroundSends) Text("May continue in background with a visible notification.")
                        if (send.paused) TextButton({ model.resumeSending(send.id) }, enabled = !changing) { Text("Allow this transfer to resume") }
                        else TextButton({ model.pauseSending(send.id) }, enabled = !changing) { Text("Pause sending") }
                        TextButton({ model.removeSending(send.id) }, enabled = !changing) { Text("Cancel sending record") }
                    }
                }
            }
            Card {Column(Modifier.padding(18.dp),verticalArrangement=Arrangement.spacedBy(10.dp)) {
                Text("Send into a computer folder",style=MaterialTheme.typography.titleLarge)
                Text("In On this phone, prepare a private file copy. Connect and choose an approved receiving location, or browse a folder with Write and Create permission. Keep this app open. Receiving inboxes use unique names. Original files are kept. Up to eight private copies share a 16 GiB limit.")
                TextButton({onPhone=true}) {Text("Choose a local file to prepare")}
                TextButton(model::loadUploadDestinations,enabled=state.authenticated && pairingReady && !state.uploadDestinationsBusy) {Text("Load approved receiving locations")}
                if(state.uploadDestinationsBusy) LinearProgressIndicator(Modifier.fillMaxWidth())
                state.uploadDestinations.forEach {entry->TextButton({model.selectUploadDestination(entry.id)}) {Text("Use receiving location: ${entry.name}")}}
                Text(state.uploadDestination?.let {"Selected receiving location: ${it.name}"}?:state.breadcrumbs.lastOrNull()?.let {"Open browser folder: ${it.name}"}?:"No receiving destination selected.")
                if(state.uploadDestination!=null) TextButton({model.selectUploadDestination(null)}) {Text("Use the open browser folder instead")}
                if(state.orphanUploads>0) TextButton({clearOrphanUploads=true}) {Text("Review ${state.orphanUploads} orphaned private send copies…")}
                if(state.uploads.isEmpty()) Text("No private send copies prepared.")
                state.uploads.forEach {item->
                    HorizontalDivider(); Text(item.name,style=MaterialTheme.typography.titleMedium)
                    Text("${item.phase.name.lowercase()} · ${item.offset} / ${item.size} bytes"); Text(item.message)
                    UploadPayloadStatus(item,state.uploadPayloadEstimates[item.id],state.transferNetwork.preferences.bytesPerSecond!=0L)
                    if(item.finalName.isNotEmpty()) Text("Receiver name: ${item.finalName}")
                    if(item.destination.isNotEmpty()) Text("Receiver folder: ${item.destination}")
                    if(item.active) TextButton({model.pauseUpload(item.id)}) {Text(if(item.phase==PhoneUploadPhase.COMPLETED) "Cancel receiver check" else "Pause send")}
                    else {
                        if(item.remote.isEmpty() && item.hash.isNotEmpty()) TextButton({uploadName=item.name; renameUpload=item}) {Text("Change receiving filename…")}
                        if(item.hash.isNotEmpty() && item.phase!=PhoneUploadPhase.COMPLETED) Button({model.sendUpload(item.id)},enabled=state.authenticated && pairingReady &&
                            (item.remote.isNotEmpty() || (state.uploadDestination?:state.breadcrumbs.lastOrNull())?.permissions?.let {it and 12==12}==true)) {Text(if(item.remote.isEmpty()) "Send to selected computer destination" else "Resume / check receiver receipt")}
                        if(item.phase==PhoneUploadPhase.COMPLETED) TextButton({model.verifyPublishedUpload(item.id)},
                            enabled=state.authenticated && pairingReady && model.supportsPublishedUploadCheck() && model.privateBatchCurrent(item.peer,item.pairing)) {Text("Verify published copy…")}
                        if(item.remote.isNotEmpty()) TextButton({anotherUpload=item}) {Text("Prepare another receiver copy…")}
                        TextButton({removeUpload=item}) {Text("Remove private send copy…")}
                    }
                }
            } }
            BatchTransfersPanel(model,state) {onPhone=true}
            BatchPartRecoveryPanel(model,state)
            IncomingPartRecoveryPanel(model,state)
            if(state.transferRecovery.none {it.area=="Batch plans"} && (state.incomingBatchPlans.isNotEmpty() || state.orphanBatchParts>0))
                IncomingBatchPlanHistoryCard(state) {archiveBatchPlans=true}
            if(state.incomingCopies.isNotEmpty() || state.orphanIncoming>0) Card {Column(Modifier.padding(18.dp),verticalArrangement=Arrangement.spacedBy(10.dp)) {
                Text("Incoming copies from computer",style=MaterialTheme.typography.titleLarge)
                Text("Private staging and provider publication are separate. A completed receipt requires read-back verification. Discarding private staging keeps every provider document.")
                Text("Verified bytes do not establish file safety. Treat every received file as untrusted.",style=MaterialTheme.typography.bodySmall)
                TextButton({archiveIncoming=true}) {Text("Archive / reset incoming history…")}
                val pages=maxOf(1,(state.incomingCopies.size+31)/32); val page=incomingPage.coerceIn(0,pages-1)
                Row {TextButton({incomingPage=page-1},enabled=page>0) {Text("Previous incoming copies")}; Text("${page+1} / $pages"); TextButton({incomingPage=page+1},enabled=page+1<pages) {Text("Next incoming copies")}}
                state.incomingCopies.drop(page*32).take(32).forEach {item->
                    HorizontalDivider(); Text(item.name,style=MaterialTheme.typography.titleMedium); Text("${item.phase.name.lowercase()} · ${item.offset} / ${item.size} bytes")
                    if (item.phase == UploadPhase.UPLOAD_RECEIVING) {
                        val estimate = if (item.offset == item.size) PhonePayloadRateEstimate(PhonePayloadRateState.RECEIVED)
                            else state.incomingPayloadEstimates[item.id]
                        estimate?.let { Text(it.receivingText(state.transferNetwork.preferences.bytesPerSecond != 0L)) }
                    } else if (state.authenticated && (item.phase == UploadPhase.UPLOAD_VERIFYING || item.phase == UploadPhase.UPLOAD_FINALIZING)) {
                        state.incomingFinalizations[item.id]?.let { Text(it.describe()) }
                    }
                    Text(item.message); if(item.finalName.isNotEmpty()) Text("Provider name: ${item.finalName}")
                    key(item.id) {IncomingWindowRecoveryPanel(item,!state.savingTrust && state.trustIssue.isEmpty(),{model.reviewIncomingWindow(item.id)},model::confirmIncomingWindow,model::cancelIncomingWindowReview)}
                    TextButton({discardIncoming=item}) {Text("Discard private staging…")}
                }
            }}
            DiagnosticsPanel(model)
            ActivityPanel(model,state)
            Text("This phone's device ID", style = MaterialTheme.typography.labelLarge)
            Text(state.deviceId, style = MaterialTheme.typography.bodySmall, fontFamily = FontFamily.Monospace)
            if (state.events.isNotEmpty()) {
                HorizontalDivider()
                Text("Recent status", style = MaterialTheme.typography.titleMedium)
                state.events.forEach { Text(it, style = MaterialTheme.typography.bodySmall) }
            }
            Text("No account. No file relay. Phone browsing ends when you leave the app; only explicitly selected transfers may continue.",
                style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.primary)
        }
    }
}
