package com.iconnect.proof

import android.app.Application
import android.net.Uri
import android.os.Build
import com.google.protobuf.ByteString as ProtoBytes
import com.google.protobuf.MessageLite
import com.iconnect.protocol.*
import kotlinx.coroutines.*
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import okhttp3.*
import okio.ByteString
import okio.ByteString.Companion.toByteString
import java.net.URI
import java.security.MessageDigest
import java.security.SecureRandom

data class ProofState(
    val server: String = if (BuildConfig.DEBUG) "http://10.0.2.2:5078" else "",
    val stun: String = "",
    val deviceName: String = Build.MODEL.take(64),
    val deviceId: String = "",
    val connected: Boolean = false,
    val connecting: Boolean = false,
    val peerName: String = "",
    val invitation: String = "",
    val code: String = "",
    val authenticated: Boolean = false,
    val batchReceiverSupported:Boolean=false,
    val peerConnecting: Boolean = false,
    val sharePresence:Boolean=false,
    val presenceSaving:Boolean=false,
    val presenceSupported:Boolean=false,
    val peerAvailable:Boolean=false,
    val presenceIssue:String="",
    val transferNetwork:PhoneTransferNetworkView=PhoneTransferNetworkView(),
    val uploads:List<PhoneUpload> = emptyList(),
    val privateOutgoingBatches:List<PhoneBatchSource> = emptyList(),
    val outgoingBatchSends:List<PhoneBatchSend> = emptyList(),
    val outgoingBatchPayloads: Map<String, PhoneBatchPayload> = emptyMap(),
    val batchSenderRecoveryArchives:List<PhoneSenderRecoveryArchive> = emptyList(),
    val batchSourceRecoveryArchives:List<PhoneSourceRecoveryArchive> = emptyList(),
    val privateOutgoingBytes:Long=0,
    val orphanUploads:Int=0,
    val incomingCopies:List<PhoneIncomingCopy> = emptyList(),
    val incomingPayloadEstimates: Map<String, PhonePayloadRateEstimate> = emptyMap(),
    val incomingFinalizations: Map<String, PhoneIncomingFinalization> = emptyMap(),
    val incomingBatchFileProgress: List<PhoneIncomingBatchFileProgress> = emptyList(),
    val incomingBatchPlans:List<PhoneIncomingBatchPlan> = emptyList(),
    val orphanBatchParts:Int=0,
    val batchPartRemoval:PhoneReceivingPartRemovalProgress?=null,
    val orphanIncoming:Int=0,
    val incomingPartRemoval:PhoneReceivingPartRemovalProgress?=null,
    val uploadDestinations:List<FileEntry> = emptyList(),
    val uploadDestination:FileEntry?=null,
    val uploadDestinationsBusy:Boolean=false,
    val autoReconnect:Boolean=false,
    val reconnectPaused:Boolean=true,
    val reconnectSaving:Boolean=false,
    val reconnectIssue:String="",
    val reconnectNotice:String="",
    val status: String = "Ready to pair",
    val events: List<String> = emptyList(),
    val activity:PhoneActivityView=PhoneActivityView(),
    val remoteEntries: List<FileEntry> = emptyList(),
    val breadcrumbs: List<FileEntry> = emptyList(),
    val browsing: Boolean = false,
    val listingBusy: Boolean = false,
    val fileError: String = "",
    val textPreview: String = "",
    val directoryContinuation: String = "",
    val phoneShares: List<AndroidStorageGrant> = emptyList(),
    val savingShare: Boolean = false,
    val downloadDestination: String = "",
    val downloads: List<PhoneDownload> = emptyList(),
    val receivingBytes:Map<String,Long> = emptyMap(),
    val downloadFinalizations: Map<String, PhoneIncomingFinalization> = emptyMap(),
    val payloadEstimates:Map<String,PhonePayloadRateEstimate> = emptyMap(),
    val uploadPayloadEstimates:Map<String,PhonePayloadRateEstimate> = emptyMap(),
    val downloadBusy: Boolean = false,
    val backgroundDownloads: Set<String> = emptySet(),
    val sends: List<PhoneSend> = emptyList(),
    val preparedSources:List<PhonePreparedSource> = emptyList(),
    val backgroundSends: Set<String> = emptySet(),
    val changingSends: Set<String> = emptySet(),
    val backgroundReady: Boolean = false,
    val savingTrust: Boolean = false,
    val trustIssue: String = "",
    val trustNotice: String = "",
    val revocationPending: Boolean = false,
    val batches: List<PhoneBatch> = emptyList(),
    val planningBatch: Boolean = false,
    val batchPlan: PhoneBatchPlan? = null,
    val explorerRows: List<PhoneExplorerRow> = emptyList(),
    val selectedFiles: List<PhoneExplorerRow> = emptyList(),
    val explorerSort: PhoneExplorerSort = PhoneExplorerSort.NAME,
    val explorerDescending: Boolean = false,
    val explorerLayout: PhoneExplorerLayout = PhoneExplorerLayout.LIST,
    val explorerPage: Int = 0,
    val explorerPages: Int = 1,
    val explorerCount: Int = 0,
    val discoveryComplete: Boolean = true,
    val discoveryNotice: String = "",
    val searchQuery: String = "",
    val fileDetails: FileEntry? = null,
    val transferRecovery: List<PhoneTransferRecovery> = emptyList(),
    val preservedProgressRecords: Int = 0,
    val recentMutations: List<MutationInfo> = emptyList(),
    val deletedItems: List<DeletedInfo> = emptyList(),
    val deletedOffset: Int = 0,
    val moreDeleted: Boolean = false,
    val pendingMove: FileEntry? = null,
    val mutationBusy: Boolean = false,
    val mutationMessage: String = "",
)
data class PhoneTransferRecovery(val area: String, val issue: StateRecoveryIssue)
data class PhoneBatchPlan(val approval: PeerApproval, val sessionId: String, val tree: String, val manifest: PhoneTransferManifest)

class PhoneController internal constructor(private val application: Application,
    private val coordinatorSockets: WebSocket.Factory = OkHttpClient.Builder().pingInterval(20, java.util.concurrent.TimeUnit.SECONDS).build(),
    networkFactory:(android.content.Context,(Boolean,Long?)->Unit)->NetworkWatch={context,changed->ForegroundNetwork(context,changed)}) {
    private val controllerScope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private val payloadRates=PhonePayloadRateTracker()
    private val uploadPayloadRates=PhoneUploadPayloadRates()
    private var receivingPayload=emptyMap<String,PhonePayloadProgress>()
    private val sourcePersistence = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val trustPersistence = Mutex()
    private val mutable = MutableStateFlow(ProofState())
    val state = mutable.asStateFlow()
    val preview = PhonePreview(application,controllerScope) { value -> mutable.update { it.copy(textPreview=value.text, fileError=if(value.error.isEmpty()) it.fileError else value.error) } }
    val localFiles = PhoneLocalBrowser(application)
    private val identity = LocalIdentity()
    private val preferences = application.getSharedPreferences("iconnect.proof", 0)
    internal val networkTraffic=PhoneFileTrafficPolicy(android.os.SystemClock::elapsedRealtime)
    private val transferNetwork=runCatching {AndroidTransferNetworkSettings(application,networkTraffic)}.getOrElse {networkTraffic.configure(PhoneNetworkPreferences(),false); null}
    private val costMonitor=AndroidTransferCostMonitor(application,controllerScope,publish={cost->networkTraffic.observe(cost); mutable.update {it.copy(transferNetwork=it.transferNetwork.copy(cost=cost))}})
    private val savedPresence=runCatching {preferences.getString("presence.owner","")==identity.id && preferences.getBoolean("presence.enabled",false)}
    private val owner = AndroidOwnerPolicy(application, identity.id)
    private val history=PhoneActivity(application,backupContext={identity.id+"|"+runCatching {owner.recoveryStamp}.getOrElse {"blocked"}},backupAvailable={uiVisible && closing==null})
    private val reconnect=AndroidReconnect(application,identity.id)
    private val reconnectSchedule=ReconnectSchedule(android.os.SystemClock::elapsedRealtime)
    private val presence=PresenceTracker(identity.id,android.os.SystemClock::elapsedRealtime)
    private var presenceLoop:Job?=null
    private var reconnectLoop:Job?=null
    private var reconnectWrites=0
    private var reconnectEnabling=false
    private var networkReady=false
    private var networkId:Long?=null
    @Volatile internal var automaticAttempts=0L; private set
    private var peerPaused=reconnect.pauseRecorded
    private val network=networkFactory(application) {ready,id->
        val changed=networkId!=null && (networkId!=id || networkReady && !ready)
        networkReady=ready; networkId=id
        if(changed && uiVisible && reconnectAllowed() && (state.value.connected || state.value.connecting)) disconnect("Network changed. Active work was paused before reconnecting.",PhoneConnectionEnd.PAUSED)
    }
    private val storage = AndroidStorageGrants(application, owner)
    private val safAccess = AndroidSafAccess(application)
    private val downloadSources = AndroidDownloadSources(application, storage)
    private val downloads = AndroidDownloadManager(application)
    private val batches = AndroidBatchDownloads(application, downloads)
    private val outgoingResources=PhoneOutgoingResources()
    private val uploads=PhoneUploads(application,identity.id,outgoingResources)
    private val outgoingBatches=AndroidBatchSources(application,owner,outgoingResources)
    private val outgoingBatchSender=PhoneBatchSender(application,owner,outgoingBatches)
    private val incoming=AndroidIncomingUploads(application,storage,identity.id)
    private val diagnostics=PhoneDiagnostics(application,
        {runCatching {owner.recoveryStamp}.getOrElse {identity.id+".blocked"}+"|"+(peerApproval?.epoch?:"")}, {closing==null})
    private var socket: WebSocket? = null
    @Volatile private var generation = 0
    private var registrationSent = false
    private var lifetime: Job? = null
    private var scope: CoroutineScope? = null
    private var endpoint: URI? = null
    private var expectedToken: String? = null
    private var expectedHost: ByteArray? = null
    private var pending: Pairing? = null
    private var locallyApproved = false
    @Volatile private var peerKey: ByteArray? = null
    @Volatile private var peerApproval: PeerApproval? = null
    private var pairAttempt: PairAttempt? = null
    private var pendingRevocations = emptySet<RevokedApproval>()
    private var revocationWrites = 0
    @Volatile private var session: RtcTestSession? = null
    @Volatile private var sourceReviewEpoch = 0L
    private var catalog = emptyList<PhoneExplorerRow>()
    private val selectedExplorer = linkedMapOf<String, PhoneExplorerRow>()
    private var discovery: Job? = null
    private var discoveryRevision = 0
    private var closing: Job? = null
    val closed get() = closing?.isCompleted == true
    @Volatile private var uiVisible = false
    private var backgroundToken = ""
    private var pendingBackgroundToken = ""
    private var pendingBackgroundIds = emptySet<String>()
    private var pendingBackgroundSources = emptyMap<String, String>()
    @Volatile private var authorizedSources = emptyMap<String, String>()
    private val seen = mutableSetOf<String>()
    private val sessionIds = mutableSetOf<String>()
    private val discardedSessions = mutableSetOf<String>()
    init {
        storage.activity=history::record; downloads.activity=history::record; localFiles.copies.activity=history::record; localFiles.localBatches.activity=history::record; batches.activity=history::record; uploads.activity=history::record
        localFiles.folders?.activity=history::record
        outgoingBatches.activity=history::record; outgoingBatchSender.activity=history::record
        controllerScope.launch {history.state.collect {view->mutable.update {it.copy(activity=view)}}}
        peerApproval = owner.selected(); peerKey = peerApproval?.publicKey()
        mutable.update { it.copy(deviceId = identity.id, sharePresence=savedPresence.getOrDefault(false),
            presenceIssue=if(savedPresence.isFailure) "Availability preference could not be read. Reporting is off; save this option again." else "",
            peerName = peerApproval?.name ?: "", trustIssue = owner.issue, trustNotice = owner.notice,
            status = if (peerKey == null) "Ready to pair" else "Paired computer saved. Connect to coordinator.",
            phoneShares = storage.all(), server = preferences.getString("server", null) ?: it.server, stun=preferences.getString("stun", "")?:"") }
        mutable.update { it.copy(downloadDestination = safAccess.destination()?.toString() ?: "") }
        syncTransferRecovery()
        controllerScope.launch { downloads.state.collect { rows ->
            mutable.update { it.copy(downloads = rows) }; refreshPayloadEstimates(); checkBackground()
        } }
        controllerScope.launch {downloads.payloadProgress.collect {progress->
            receivingPayload=progress
            val rows=downloads.state.value.associateBy {it.id}; val limit=state.value.transferNetwork.preferences.bytesPerSecond
            progress.values.forEach {point->rows[point.id]?.takeIf {matchesPayload(it,point)}?.let {row->
                payloadRates.observe(row.id,payloadScope(row,limit),row.size,point.received,true,point.at)
            }}
            refreshPayloadEstimates()
        }}
        controllerScope.launch {while(isActive) {delay(1000); if(uiVisible || state.value.backgroundReady) {refreshPayloadEstimates(); refreshUploadPayloadEstimates()}}}
        controllerScope.launch { downloadSources.state.collect { rows -> syncSends(rows); checkBackground() } }
        controllerScope.launch {downloadSources.inventory.collect {rows->mutable.update {it.copy(preparedSources=rows)}}}
        controllerScope.launch { batches.state.collect { rows -> mutable.update { it.copy(batches = rows) } } }
        controllerScope.launch {uploads.state.collect {rows->mutable.update {it.copy(uploads=rows,orphanUploads=uploads.orphanCount(),privateOutgoingBytes=outgoingResources.reservedBytes)}; refreshUploadPayloadEstimates()}}
        controllerScope.launch {uploads.payloadProgress.collect {refreshUploadPayloadEstimates()}}
        controllerScope.launch {outgoingBatches.state.collect {rows->mutable.update {it.copy(privateOutgoingBatches=rows,privateOutgoingBytes=outgoingResources.reservedBytes)}; syncTransferRecovery()}}
        controllerScope.launch {outgoingBatchSender.state.collect {rows->
            mutable.update {it.copy(outgoingBatchSends=rows)}
            refreshUploadPayloadEstimates()
            syncTransferRecovery()
        }}
        controllerScope.launch {outgoingBatchSender.recoveryChanges.collect {mutable.update {it.copy(batchSenderRecoveryArchives=outgoingBatchSender.recoveryArchives())}; syncTransferRecovery()}}
        controllerScope.launch {outgoingBatches.recoveryChanges.collect {mutable.update {it.copy(batchSourceRecoveryArchives=outgoingBatches.recoveryArchives(),privateOutgoingBytes=outgoingResources.reservedBytes)}; syncTransferRecovery()}}
        controllerScope.launch { incoming.state.collect { rows ->
            val estimates = incomingPayloadEstimates(rows, state.value.transferNetwork.preferences.bytesPerSecond)
            mutable.update { it.copy(incomingCopies=rows, incomingPayloadEstimates=estimates, orphanIncoming=incoming.orphanCount()) }
        } }
        controllerScope.launch {incoming.partChanges.collect {mutable.update {it.copy(orphanIncoming=incoming.orphanCount(),incomingPartRemoval=incoming.orphanRemovalProgress())}; syncTransferRecovery()}}
        controllerScope.launch {storage.batchPlans.changes.collect {mutable.update {it.copy(incomingBatchPlans=storage.batchPlans.all(),orphanBatchParts=runCatching {storage.batchPlans.orphanParts()}.getOrDefault(0),batchPartRemoval=storage.batchPlans.orphanRemovalProgress())}; syncTransferRecovery()}}
        syncReconnect()
        mutable.update {it.copy(transferNetwork=networkView())}
    }
    private fun networkView()=transferNetwork?.view()?:PhoneTransferNetworkView(issue="Transfer network settings could not be opened. Remote content is paused; local browsing remains available. Restore private storage access and restart.")
    private fun changeTransferNetwork(action:(AndroidTransferNetworkSettings)->Unit)=guarded {
        check(uiVisible && closing==null && !state.value.transferNetwork.busy) {"Open iConnect and wait for the current settings change."}
        val settings=requireNotNull(transferNetwork) {state.value.transferNetwork.issue}; mutable.update {it.copy(transferNetwork=it.transferNetwork.copy(busy=true))}
        sourcePersistence.launch {
            val result=runCatching {action(settings)}; val snapshot=networkView()
            withContext(Dispatchers.Main.immediate) {if(closing==null) {incoming.clearPayloadRates(); storage.batchPlans.clearFilePayloadRates(); mutable.update {it.copy(transferNetwork=snapshot)}; refreshUploadPayloadEstimates(); if(result.isFailure) log("Transfer network settings could not be changed. Review their current state.")}}
        }
    }
    fun setTransferNetwork(value:PhoneNetworkPreferences) {value.validate(); changeTransferNetwork {it.set(value)}}
    fun resetTransferNetwork()=changeTransferNetwork {it.reset()}
    fun clearTransferNetworkBackups()=changeTransferNetwork {it.clearPreserved()}
    private fun syncCostMonitor()=costMonitor.setActive(uiVisible || state.value.backgroundReady)
    fun setServer(value: String) { if (!state.value.connected && !state.value.connecting) {pauseReconnectForEdit(); mutable.update { it.copy(server = value.take(2049)) }; syncReconnect()} }
    fun setStun(value: String) { if (!state.value.connected && !state.value.connecting) {pauseReconnectForEdit(); mutable.update { it.copy(stun = value.take(257)) }; syncReconnect()} }
    private fun reconnectTarget():ReconnectTarget?=runCatching {
        val approved=peerApproval?.takeIf(owner::isCurrent)?:return null
        ReconnectTarget(approved.id,approved.epoch,Rules.server(state.value.server,BuildConfig.DEBUG).toString(),Rules.stun(state.value.stun))
    }.getOrNull()
    private fun reconnectAllowed()=reconnectWrites==0 && closing==null && pendingRevocations.isEmpty() && !state.value.savingTrust && !state.value.presenceSaving && reconnect.allowed(reconnectTarget())
    fun setSharePresence(enabled:Boolean)=guarded {
        check(uiVisible && closing==null && !state.value.connected && !state.value.connecting && !state.value.presenceSaving)
        mutable.update {it.copy(presenceSaving=true,presenceIssue="")}
        sourcePersistence.launch {
            val saved=runCatching {auditLocal(PhoneActivityKind.PRESENCE_SETTINGS) {check(preferences.edit().putString("presence.owner",identity.id).putBoolean("presence.enabled",enabled).commit()) {"Availability preference could not be saved. Retry before connecting."}}}
            withContext(Dispatchers.Main) {mutable.update {it.copy(presenceSaving=false,sharePresence=if(saved.isSuccess) enabled else it.sharePresence,
                presenceIssue=saved.exceptionOrNull()?.message?:"")}}
        }
    }
    private fun syncPresence() {
        val approved=peerApproval?.takeIf(owner::isCurrent)
        mutable.update {it.copy(peerAvailable=it.connected && it.presenceSupported && it.sharePresence && uiVisible && !peerPaused && approved!=null && approved.id in presence.availablePeers())}
    }
    private fun updatePresence() {
        if(!state.value.connected || !state.value.presenceSupported) return
        val enabled=state.value.sharePresence && uiVisible && !peerPaused && owner.healthy && !state.value.savingTrust && pendingRevocations.isEmpty()
        val ids=if(enabled) peerApproval?.takeIf(owner::isCurrent)?.let {listOf(it.id)}?:emptyList() else emptyList()
        presence.next(enabled,ids)?.let {send("presence",it)}; syncPresence()
    }
    private fun startPresenceLoop() {
        if(!uiVisible || !state.value.connected || !state.value.presenceSupported || !state.value.sharePresence || presenceLoop?.isActive==true) return
        presenceLoop=scope?.launch {
            try {while(isActive) {updatePresence(); delay(1000)}}
            catch(e:Exception) {if(e !is CancellationException) disconnect("Coordinator availability reporting failed.",PhoneConnectionEnd.FAILED)}
        }
    }
    private fun withdrawPresence() {
        presenceLoop?.cancel(); presenceLoop=null
        if(state.value.connected && state.value.presenceSupported && state.value.sharePresence)
            runCatching {presence.next(false,emptyList())?.let {send("presence",it)}}
        presence.clear(); mutable.update {it.copy(peerAvailable=false)}
    }
    private fun syncReconnect(notice:String=state.value.reconnectNotice) {
        val target=reconnectTarget(); val enabled=reconnect.options.enabled && reconnect.options.target==target && target!=null
        mutable.update {it.copy(autoReconnect=enabled,reconnectPaused=reconnect.paused,reconnectSaving=reconnectWrites>0,reconnectIssue=reconnect.issue,
            reconnectNotice=if(reconnect.options.enabled && !enabled && reconnect.healthy) "Enable reconnect again to approve this pairing and address." else notice)}
    }
    private fun reconnectWrite(action:()->Unit) {
        reconnectWrites++; syncReconnect()
        sourcePersistence.launch {
            val result=runCatching {auditLocal(PhoneActivityKind.RECONNECT_SETTINGS) {action()}}
            withContext(Dispatchers.Main.immediate) {reconnectWrites--; if(reconnectWrites==0) reconnectEnabling=false; syncReconnect(); if(result.isFailure) log(reconnect.issue.ifEmpty {result.exceptionOrNull()?.message?:"Automatic connection settings could not be saved."})}
        }
    }
    fun setAutoReconnect(enabled:Boolean)=guarded {
        check(uiVisible && reconnectWrites==0 && closing==null)
        val target=if(enabled) requireNotNull(reconnectTarget()) {"Pair a computer and enter valid connection settings first."} else reconnect.options.target
        val ticket=reconnect.blockNow(); reconnectSchedule.reset()
        reconnectEnabling=enabled
        // Save the endpoint before optional intent, so a cold start uses the exact approved configuration.
        val server=state.value.server; val stun=state.value.stun
        reconnectWrite {
            if(!enabled) reconnect.persistPause()
            else check(preferences.edit().putString("server",server).putString("stun",stun).commit()) {"Connection settings could not be saved."}
            reconnect.configure(target,enabled,ticket)
        }
    }
    private fun pauseReconnectIntent() {
        reconnect.blockNow(); peerPaused=true
        withdrawPresence()
        reconnectWrite {reconnect.persistPause()}
    }
    private fun pauseReconnectForEdit() {if(reconnectEnabling || reconnect.options.enabled && !reconnect.paused) {reconnectEnabling=false; pauseReconnectIntent()}}
    fun pauseConnections() {pauseReconnectIntent(); disconnect("Connections paused. Automatic retries stay paused until resumed.",PhoneConnectionEnd.PAUSED)}
    fun resumeConnections()=setAutoReconnect(true)
    fun resetReconnect()=guarded {
        check(uiVisible && reconnectWrites==0); val ticket=reconnect.blockNow()
        reconnectWrite {reconnect.reset(ticket)}
    }
    private fun startReconnectLoop() {
        if(reconnectLoop?.isActive==true) return
        network.start()
        reconnectLoop=controllerScope.launch {
            while(isActive && uiVisible) {
                val value=state.value; val allowed=reconnectAllowed()
                val action=reconnectSchedule.next(ReconnectSnapshot(allowed,uiVisible,networkReady,value.connected,value.connecting,value.authenticated,value.peerConnecting,
                    pending!=null || pairAttempt!=null,sessionIds.size>=32 || seen.size>=1024))
                if(action!=null) automaticAttempts++
                when(action) {
                    ReconnectAction.COORDINATOR->connect()
                    ReconnectAction.PEER->connectPeer()
                    ReconnectAction.REFRESH->disconnect("Refreshing the idle coordinator context before reconnecting.",PhoneConnectionEnd.SUPERSEDED)
                    null->Unit
                }
                val after=state.value
                syncReconnect(when {
                    !after.autoReconnect->""
                    !allowed->"Automatic connections are paused."
                    !networkReady->"Waiting for an available network."
                    after.authenticated->"Automatic reconnect is ready while the app is open."
                    after.connecting || after.peerConnecting->"Connection attempt in progress."
                    else->"Retry in ${reconnectSchedule.remainingSeconds()} s. Saved transfers require deliberate resume."
                })
                delay(1000)
            }
        }
    }
    private fun stopReconnectLoop() {reconnectLoop?.cancel(); reconnectLoop=null; network.stop()}
    fun setInvitation(value: String) { mutable.update { it.copy(invitation = value) } }
    private fun log(message: String) { mutable.update { it.copy(status = message, events = (it.events + message).takeLast(8)) } }
    private fun recordActivity(kind:PhoneActivityKind,outcome:PhoneActivityOutcome,peer:String=peerApproval?.id?:"")=history.record(PhoneActivityEvent(kind,outcome,peer))
    private inline fun <T> auditLocal(kind:PhoneActivityKind,reviewFailure:Boolean=false,work:()->T):T {
        try {val result=work(); recordActivity(kind,if(result is Boolean && !result) PhoneActivityOutcome.REVIEW else PhoneActivityOutcome.COMPLETED); return result}
        catch(e:Exception) {recordActivity(kind,if(reviewFailure || e is CancellationException) PhoneActivityOutcome.REVIEW else PhoneActivityOutcome.FAILED); throw e}
    }
    private inline fun <T> auditRecovery(kind:PhoneActivityKind,work:()->T):T=auditLocal(kind,true,work)
    private fun syncTransferRecovery() {
        val issues = downloads.recoveryIssues().map { PhoneTransferRecovery("Files", it) } +
            batches.recoveryIssues().map { PhoneTransferRecovery("Batches", it) } + downloadSources.recoveryIssues().map { PhoneTransferRecovery("Sending", it) } +
            storage.mutations.recoveryIssues().map { PhoneTransferRecovery("File operations", it) } + uploads.recoveryIssues().map {PhoneTransferRecovery("Uploads",it)} + outgoingBatches.recoveryIssues().map {PhoneTransferRecovery("Private batches",it)} + outgoingBatchSender.recoveryIssues().map {PhoneTransferRecovery("Batch sending",it)} + incoming.recoveryIssues().map {PhoneTransferRecovery("Incoming copies",it)} + storage.batchPlans.recoveryIssues().map {PhoneTransferRecovery("Batch plans",it)}
        val preserved = downloads.preservedRecords() + batches.preservedRecords() + downloadSources.preservedRecords() + storage.mutations.preservedRecords()+uploads.preservedRecords()+incoming.preservedRecords()+storage.batchPlans.preservedRecords()
        mutable.update { it.copy(transferRecovery = issues, preservedProgressRecords = preserved,orphanUploads=uploads.orphanCount(),orphanIncoming=incoming.orphanCount()) }
    }
    fun clearPreservedProgress() = guarded { check(uiVisible); controllerScope.launch { try {
        withContext(Dispatchers.IO) {auditRecovery(PhoneActivityKind.PROGRESS_BACKUP_REMOVAL) { downloads.clearPreservedRecords(); batches.clearPreservedRecords(); downloadSources.clearPreservedRecords(); storage.mutations.clearPreservedRecords(); uploads.clearArchives(); incoming.clearArchives(); storage.batchPlans.clearPreservedRecords() }}
        syncTransferRecovery(); log("Preserved metadata backups removed. Partial and copied files were kept.")
    } catch (e: Exception) { log(e.message ?: "Some metadata backups could not be removed.") } finally { syncTransferRecovery() } } }
    fun archiveTransferRecovery(area: String, id: String) = guarded {
        check(uiVisible)
        controllerScope.launch { try {
            withContext(Dispatchers.IO) {auditRecovery(PhoneActivityKind.PROGRESS_PRESERVATION) { when (area) { "Files" -> downloads.archiveRecovery(id); "Batches" -> batches.archiveRecovery(id);
                "Sending" -> downloadSources.archiveRecovery(id); "File operations" -> storage.mutations.archiveRecovery(id); "Uploads"->uploads.archive(id); "Incoming copies"->incoming.archiveRecovery(id); else -> error("This recovery item is no longer available.") } }}
            syncTransferRecovery(); log("Progress record preserved in local recovery storage. Partial and saved files were kept.")
        } catch (e: Exception) { log(e.message ?: "The progress record could not be preserved.") } finally { syncTransferRecovery() } }
    }

    fun connect() = guarded {
        check(uiVisible && closing == null) { "Open iConnect to start a connection." }
        check(owner.healthy && pendingRevocations.isEmpty() && !state.value.savingTrust && !state.value.presenceSaving) { "Finish saving or repairing pairing data before connecting." }
        check(socket == null) { "Disconnect before changing the coordinator." }
        val origin = Rules.server(state.value.server, BuildConfig.DEBUG)
        Rules.stun(state.value.stun)
        peerPaused=false
        endpoint = origin
        val current = ++generation
        val job = SupervisorJob(controllerScope.coroutineContext[Job])
        val activeScope = CoroutineScope(Dispatchers.Main.immediate + job)
        lifetime = job; scope = activeScope
        val frames = Channel<Frame>(64)
        registrationSent = false
        seen.clear(); sessionIds.clear(); discardedSessions.clear()
        mutable.update { it.copy(connecting = true, status = "Connecting to coordinator...") }
        recordActivity(PhoneActivityKind.COORDINATOR_CONNECTION,PhoneActivityOutcome.REQUESTED,"")
        activeScope.launch {
            try {
                for (frame in frames) {
                    if (current != generation) break
                    handle(frame)
                }
            } catch (e: Exception) {
                if (e !is CancellationException && current == generation)
                    disconnect(e.message ?: "Protocol failed.", PhoneConnectionEnd.FAILED)
            }
        }
        val wsScheme = if (origin.scheme == "https") "wss" else "ws"
        socket = coordinatorSockets.newWebSocket(Request.Builder().url("$wsScheme://${origin.rawAuthority}/signal").build(), object : WebSocketListener() {
            override fun onMessage(webSocket: WebSocket, bytes: ByteString) {
                if (current != generation) return
                try {
                    require(bytes.size <= Rules.MAX_FRAME)
                    if (!frames.trySend(Rules.parse(bytes.toByteArray())).isSuccess) error("Coordinator queue exceeded.")
                } catch (e: Exception) {
                    val message = if (e is UnsupportedProtocolVersionException) e.message else "Invalid coordinator message."
                    controllerScope.launch { if (current == generation) disconnect(message, PhoneConnectionEnd.FAILED) }
                }
            }
            override fun onMessage(webSocket: WebSocket, text: String) {
                controllerScope.launch { if (current == generation) disconnect("Expected a binary protocol message.",PhoneConnectionEnd.FAILED) }
            }
            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                controllerScope.launch { if (current == generation) disconnect("Coordinator unavailable: ${t.message}",PhoneConnectionEnd.FAILED) }
            }
            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                controllerScope.launch { if (current == generation) disconnect("Coordinator disconnected.",PhoneConnectionEnd.ENDED) }
            }
        })
        activeScope.launch {
            delay(10_000)
            if (current == generation && !state.value.connected)
                disconnect("Coordinator authentication timed out.", PhoneConnectionEnd.FAILED)
        }
    }

    private suspend fun handle(frame: Frame) {
        if (state.value.connected) {
            require(frame.kind != "challenge" && frame.kind != "registered") { "Unexpected coordinator registration message." }
        } else {
            val expected = if (registrationSent) "registered" else "challenge"
            require(frame.kind == expected) { "Unexpected coordinator registration message." }
        }
        when (frame.kind) {
            "challenge" -> {
                val challenge = com.iconnect.protocol.Challenge.parseFrom(frame.payload)
                require(challenge.nonce.size() == 32)
                send("register", Registration.newBuilder().setIdentity(identity.descriptor(state.value.deviceName))
                    .setSignalStatus(true)
                    .setSignature(ProtoBytes.copyFrom(identity.sign(Rules.registration(challenge.nonce.toByteArray())))).build())
                registrationSent = true
            }
            "registered" -> {
                val notice=Notice.parseFrom(frame.payload)
                require(notice.message == identity.id && notice.capabilitiesCount<=16 && notice.capabilitiesList.all {it.length in 1..32})
                preferences.edit().putString("server", requireNotNull(endpoint).toString()).putString("stun",state.value.stun).apply()
                mutable.update { it.copy(connected = true, connecting = false, presenceSupported=PresenceTracker.CAPABILITY in notice.capabilitiesList,status = "Coordinator connected. Files are not exposed.") }
                recordActivity(PhoneActivityKind.COORDINATOR_CONNECTION,PhoneActivityOutcome.COMPLETED,"")
                startPresenceLoop()
            }
            "presence" -> {
                require(state.value.presenceSupported)
                presence.accept(PresenceSnapshot.parseFrom(frame.payload)); syncPresence()
            }
            "pairing" -> {
                check(pending == null)
                val p = Pairing.parseFrom(frame.payload)
                require(p.token == expectedToken && Rules.tokenPattern.matches(p.token) && Rules.tokenPattern.matches(p.transactionId) &&
                    p.host.publicKey.toByteArray().contentEquals(expectedHost) && p.guest.publicKey.toByteArray().contentEquals(identity.publicKey) &&
                    p.expiresAt > System.currentTimeMillis() && p.expiresAt <= System.currentTimeMillis() + 120_000)
                listOf(p.host, p.guest).forEach {
                    Rules.id(it.publicKey.toByteArray())
                    require(it.name.length in 1..64 && it.name.none { char -> Character.isISOControl(char) })
                }
                require(p.host.publicKey != p.guest.publicKey)
                pending = p
                mutable.update { it.copy(code = Rules.code(p), status = "Compare the code on Windows, then approve on both devices.") }
            }
            "paired" -> {
                val p = Pairing.parseFrom(frame.payload)
                val original = requireNotNull(pending)
                val transcript = Rules.pairing(original)
                require(locallyApproved && p.expiresAt > System.currentTimeMillis() &&
                    transcript.contentEquals(Rules.pairing(p)) &&
                    Rules.verify(original.host.publicKey.toByteArray(), transcript, p.hostApproval.toByteArray()) &&
                    Rules.verify(original.guest.publicKey.toByteArray(), transcript, p.guestApproval.toByteArray())) {
                    "Mutually approved pairing was not proven."
                }
                val attempt = requireNotNull(pairAttempt)
                mutable.update { it.copy(savingTrust = true) }
                val saved = try { withContext(Dispatchers.IO) { trustPersistence.withLock { owner.commitPair(attempt, p) } } }
                    finally { mutable.update { it.copy(savingTrust = revocationWrites > 0) } }
                check(pairAttempt === attempt && owner.isCurrent(saved)) { "Pairing changed before it was saved." }
                pairAttempt = null; peerApproval = saved; peerKey = saved.publicKey()
                syncSends(downloadSources.state.value)
                pending = null; expectedToken = null; expectedHost = null; locallyApproved = false
                mutable.update { it.copy(code = "", invitation = "", peerName = saved.name, trustNotice = owner.notice) }
                log("Pairing complete. Use connect <device id> on Windows to start a direct session.")
                recordActivity(PhoneActivityKind.PAIRING,PhoneActivityOutcome.COMPLETED,saved.id)
            }
            "rejected" -> {
                cancelPairAttempt()
                pending = null; expectedToken = null; expectedHost = null; locallyApproved = false
                mutable.update { it.copy(code = "") }; log("Pairing rejected.")
            }
            "signal-unavailable" -> {
                val unavailable = SignalUnavailable.parseFrom(frame.payload)
                require(Rules.tokenPattern.matches(unavailable.sessionId) && unavailable.peerId.matches(Regex("[a-f0-9]{64}")))
                if (session?.sessionId == unavailable.sessionId && peerApproval?.id == unavailable.peerId) {
                    pausePeerWork(PhoneConnectionEnd.UNAVAILABLE)
                    mutable.update { it.copy(authenticated = false, batchReceiverSupported=false, peerConnecting=false) }
                    log("The paired computer is unavailable. Its app must be open or enabled in the notification area.")
                }
            }
            "signal" -> {
                val s = Signal.parseFrom(frame.payload)
                val approved = requireNotNull(peerApproval?.takeIf(owner::isCurrent)) { "An unpaired device requested a connection." }
                val key = approved.publicKey()
                Rules.validateSignal(s, key, identity.id)
                require(seen.size < 2048 && seen.add(Rules.b64(s.nonce.toByteArray()))) { "Replayed connection message." }
                if(s.sessionId in discardedSessions) return
                if (s.kind == "offer") {
                    require(sessionIds.size < 64 && sessionIds.add(s.sessionId)) { "Replayed session offer." }
                    if (!uiVisible || peerPaused) { discardedSessions.add(s.sessionId); log("A new connection was refused while phone availability is paused."); return }
                    val active=session
                    if(active!=null && active.initiator && !active.isClosed && !active.isAuthenticated && identity.id<approved.id) {
                        discardedSessions.add(s.sessionId); log("Both devices connected at once. Keeping one authenticated connection attempt."); return
                    }
                    startSession(approved,s.sessionId,false).acceptOffer(s.body)
                } else {
                    require(session?.sessionId == s.sessionId) { "Unexpected connection session." }
                    if(session!!.isClosed) return
                    if(s.kind=="candidate") session!!.addCandidate(s.body,s.mid) else session!!.acceptAnswer(s.body)
                }
            }
            else -> error("Unsupported coordinator response.")
        }
    }

    fun requestPairing() = guarded {
        check(state.value.connected && pending == null && pairAttempt == null && !state.value.savingTrust && pendingRevocations.isEmpty() && peerApproval == null)
        val uri = Uri.parse(state.value.invitation.trim())
        require(uri.scheme == "iconnect" && uri.host == "pair" && uri.queryParameterNames == setOf("server", "token", "key")) {
            "Scan or paste an iConnect pairing invitation."
        }
        require(uri.queryParameterNames.all { uri.getQueryParameters(it).size == 1 }) { "Ambiguous pairing invitation." }
        val invitationServer = Rules.server(requireNotNull(uri.getQueryParameter("server")), BuildConfig.DEBUG)
        val configured = requireNotNull(endpoint)
        val emulatorAlias = BuildConfig.DEBUG && configured.scheme == "http" && configured.host == "10.0.2.2" &&
            invitationServer.scheme == "http" && invitationServer.host in listOf("localhost", "127.0.0.1") &&
            configured.port == invitationServer.port
        require(invitationServer == configured || emulatorAlias) { "Invitation does not match the configured coordinator." }
        val token = requireNotNull(uri.getQueryParameter("token"))
        require(Rules.tokenPattern.matches(token))
        val host = Rules.decode(requireNotNull(uri.getQueryParameter("key")))
        require(Rules.id(host) != identity.id)
        pairAttempt = owner.beginPair(host)
        expectedToken = token; expectedHost = host; locallyApproved = false
        try { send("claim", Claim.newBuilder().setToken(token).setHostPublicKey(ProtoBytes.copyFrom(host)).build()) }
        catch (e: Exception) { cancelPairAttempt(); throw e }
        log("Pairing requested. Check the verification code on both devices.")
    }
    fun approve(code: String) = guarded {
        val p = requireNotNull(pending)
        require(code == Rules.code(p) && p.expiresAt > System.currentTimeMillis()) { "Enter the current code shown on Windows." }
        locallyApproved = true
        send("approve", Approval.newBuilder().setTransactionId(p.transactionId).setApproved(true)
            .setSignature(ProtoBytes.copyFrom(identity.sign(Rules.pairing(p)))).build())
        log("Local approval sent. Waiting for Windows approval.")
    }
    fun reject() = guarded {
        pending?.let { send("approve", Approval.newBuilder().setTransactionId(it.transactionId).setApproved(false).build()) }
        pending = null; expectedToken = null; expectedHost = null; locallyApproved = false
        cancelPairAttempt()
        mutable.update { it.copy(code = "") }
    }
    private fun emitSignal(sessionId: String, kind: String, body: String, mid: String) {
        val signal = Signal.newBuilder().setSessionId(sessionId).setFromDevice(identity.id)
            .setToDevice(Rules.id(requireNotNull(peerKey))).setKind(kind).setBody(body).setMid(mid)
            .setCreatedAt(System.currentTimeMillis()).setNonce(ProtoBytes.copyFrom(ByteArray(32).also { SecureRandom().nextBytes(it) }))
            .build()
        send("signal", signal.toBuilder().setSignature(ProtoBytes.copyFrom(identity.sign(Rules.signal(signal)))).build())
    }
    fun connectPeer() = guarded {
        check(uiVisible && closing==null && state.value.connected && !state.value.savingTrust && pendingRevocations.isEmpty()) { "Open iConnect and connect to the coordinator first." }
        val approved=peerApproval?.takeIf(owner::isCurrent)?:error("Pair a computer first.")
        check(session?.isClosed!=false) { "A direct connection or attempt is already active. Disconnect it before starting another." }
        check(sessionIds.size<64 && seen.size<2048) { "Reconnect to the coordinator before starting more direct sessions." }
        peerPaused=false
        startPresenceLoop()
        val id=StorageRules.token(); sessionIds.add(id); startSession(approved,id,true).offer()
    }
    fun disconnectPeer() = guarded {
        pauseReconnectIntent()
        pausePeerWork()
        mutable.update { it.copy(authenticated=false,batchReceiverSupported=false,peerConnecting=false) }
        log(if(state.value.connected) "Direct connection ended. The coordinator is still connected." else "Direct connection ended. Connect to the coordinator to start again.")
    }
    private fun startSession(approved: PeerApproval, id: String, initiator: Boolean): RtcTestSession {
        pausePeerWork(PhoneConnectionEnd.SUPERSEDED)
        val epoch=generation; val fileAccess: (FileRequest)->Boolean={ request->owner.isCurrent(approved) && allowFileRequest(approved.id,request) }
        val active=RtcTestSession(application,requireNotNull(scope),identity,id,approved.publicKey(),
            { kind,body,mid->if(generation==epoch && session?.sessionId==id && owner.isCurrent(approved)) emitSignal(id,kind,body,mid) },::log,
            { authenticated ->
                if (generation == epoch && session?.sessionId == id && owner.isCurrent(approved)) {
                    mutable.update { it.copy(authenticated = authenticated, peerConnecting = false,
                        batchReceiverSupported = authenticated && session?.acceptsFolderBatches == true,
                        incomingPayloadEstimates = if (authenticated) it.incomingPayloadEstimates else emptyMap(),
                        incomingFinalizations = if (authenticated) it.incomingFinalizations else emptyMap(),
                        incomingBatchFileProgress = if (authenticated) it.incomingBatchFileProgress else emptyList()) }
                }
            },
            stun=state.value.stun.takeIf { it.isNotEmpty() },fileService=AndroidFileService(application,storage,downloadSources,requestAllowed=fileAccess,incoming=incoming).also {it.activity=history::record},
            available={uiVisible},fileRequestAllowed=fileAccess,initiator=initiator,activity=history::record,traffic=networkTraffic)
        session=active; mutable.update { it.copy(authenticated=false,batchReceiverSupported=false,peerConnecting=true) }; return active
    }
    fun sendTestFile() = guarded { requireNotNull(session).sendTestFile() }
    fun browseRoots() = browse(null)
    fun browseFolder(entry: FileEntry) = browse(entry)
    private fun browse(folder: FileEntry?) {
        if (state.value.listingBusy || state.value.planningBatch) return
        val route = if (folder == null) emptyList() else {
            val row = catalog.firstOrNull { it.entry.id == folder.id } ?: selectedExplorer[folder.id]
            val index = state.value.breadcrumbs.indexOfFirst { it.id == folder.id }
            if (row?.entry?.isDirectory == true) row.route else if (index >= 0) state.value.breadcrumbs.take(index + 1)
            else return log("Refresh this location before opening it.")
        }
        discover(route, "", false)
    }
    fun searchFiles(query: String) = guarded {
        check(state.value.breadcrumbs.isNotEmpty() && !state.value.listingBusy && !state.value.planningBatch) { "Open an approved folder before searching." }
        discover(state.value.breadcrumbs, query, true)
    }
    private fun discover(route: List<FileEntry>, query: String, search: Boolean) {
        val active = session ?: return log("Connect to the paired computer first.")
        if (!uiVisible || !state.value.authenticated) return
        preview.clear()
        val revision = ++discoveryRevision; discovery?.cancel()
        if (!search) selectedExplorer.clear()
        mutable.update { it.copy(listingBusy = true, fileError = "", textPreview = "", selectedFiles = selectedExplorer.values.toList()) }
        discovery = controllerScope.launch {
            try {
                val request: suspend (FileRequest) -> FileResponse = { value -> withContext(Dispatchers.Main.immediate) {
                    check(uiVisible && session === active); active.requestFiles(value)
                } }
                val result = withContext(Dispatchers.IO) { if (search) PhoneExplorerCatalog.search(route, query, request) else PhoneExplorerCatalog.load(route, request) }
                if (session !== active || discoveryRevision != revision || !uiVisible) return@launch
                catalog = PhoneExplorerCatalog.sort(result.rows, state.value.explorerSort, state.value.explorerDescending)
                catalog.filter { row -> selectedExplorer[row.entry.id]?.entry?.version?.let { it != row.entry.version } == true }.forEach { selectedExplorer.remove(it.entry.id) }
                mutable.update { it.copy(browsing = true, breadcrumbs = route, explorerPage = 0, discoveryComplete = result.complete, discoveryNotice = result.notice,
                    searchQuery = if (search) query.trim() else "", selectedFiles = selectedExplorer.values.toList()) }; publishExplorerPage(0)
            } catch (e: Exception) {
                if (session === active && discoveryRevision == revision) mutable.update { it.copy(fileError = if (e is CancellationException) "Discovery canceled. Previous results were kept." else e.message ?: "Discovery stopped.") }
            } finally { if (discoveryRevision == revision) mutable.update { it.copy(listingBusy = false) } }
        }
    }
    fun cancelDiscovery() { discovery?.cancel() }
    fun nextDirectoryPage() = publishExplorerPage(state.value.explorerPage + 1)
    fun previousDirectoryPage() = publishExplorerPage(state.value.explorerPage - 1)
    private fun publishExplorerPage(page: Int) {
        val pages = maxOf(1, (catalog.size + PhoneExplorerCatalog.PAGE - 1) / PhoneExplorerCatalog.PAGE); val selected = page.coerceIn(0, pages - 1)
        val rows = catalog.drop(selected * PhoneExplorerCatalog.PAGE).take(PhoneExplorerCatalog.PAGE)
        mutable.update { it.copy(explorerRows = rows, remoteEntries = rows.map { it.entry }, explorerPage = selected, explorerPages = pages, explorerCount = catalog.size, directoryContinuation = "") }
    }
    fun setExplorerSort(sort: PhoneExplorerSort, descending: Boolean) {
        mutable.update { it.copy(explorerSort = sort, explorerDescending = descending) }; catalog = PhoneExplorerCatalog.sort(catalog, sort, descending); publishExplorerPage(0)
    }
    fun setExplorerLayout(layout: PhoneExplorerLayout) { mutable.update { it.copy(explorerLayout = layout) } }
    fun selectExplorerItem(id: String, selected: Boolean) = guarded {
        if (!selected) selectedExplorer.remove(id) else {
            check(selectedExplorer.size < 128 || id in selectedExplorer) { "Select at most 128 items for one copy." }
            selectedExplorer[id] = catalog.single { it.entry.id == id }
        }
        mutable.update { it.copy(selectedFiles = selectedExplorer.values.toList()) }
    }
    fun clearExplorerSelection() { selectedExplorer.clear(); mutable.update { it.copy(selectedFiles = emptyList()) } }
    fun showFileDetails(entry: FileEntry) {
        val active = session ?: return
        controllerScope.launch { try {
            val response = active.requestFiles(FileRequest.newBuilder().setOperation(FileOperation.GET_METADATA).setNodeId(entry.id).build())
            val metadata = response.entriesList.single()
            check(response.status == FileStatus.FILE_STATUS_OK && metadata.id == entry.id && metadata.grantId == entry.grantId)
            if (session === active && uiVisible) mutable.update { it.copy(fileDetails = metadata) }
        } catch (_: Exception) { log("This item changed or is unavailable. Refresh it.") } }
    }
    fun dismissFileDetails() { mutable.update { it.copy(fileDetails = null) } }
    fun refreshMutations() = guarded {
        val active = session ?: error("Connect to the computer first."); check(uiVisible && state.value.authenticated)
        controllerScope.launch { try { loadMutations(active) } catch (e: Exception) { mutable.update { it.copy(mutationMessage = e.message ?: "Recent changes are unavailable.") } } }
    }
    private suspend fun loadMutations(active: RtcTestSession) {
        val context = active.requestFiles(FileRequest.newBuilder().setOperation(FileOperation.MUTATION_CONTEXT).build())
        check(context.status == FileStatus.FILE_STATUS_OK) { context.message }
        check(uiVisible && session === active)
        val reply = active.requestFiles(FileRequest.newBuilder().setOperation(FileOperation.LIST_MUTATIONS).setMutationEpoch(context.mutationEpoch).build())
        check(reply.status == FileStatus.FILE_STATUS_OK) { reply.message }
        if (session === active && uiVisible) mutable.update { it.copy(recentMutations = reply.mutationsList) }
    }
    private fun changeFile(value: FileRequest) {
        val active = session ?: error("Connect to the computer first."); check(uiVisible && state.value.authenticated && !state.value.mutationBusy)
        val folder = state.value.breadcrumbs.lastOrNull(); mutable.update { it.copy(mutationBusy = true, mutationMessage = "") }
        val peer=peerApproval?.id?:""
        controllerScope.launch { val activity=PhoneMutationActivity(value.operation,peer,history::record); try {
            val recovering = RecoveryRules.isChange(value.operation)
            val context = active.requestFiles(FileRequest.newBuilder().setOperation(if (recovering) FileOperation.RECOVERY_CONTEXT else FileOperation.MUTATION_CONTEXT).build())
            if(context.status!=FileStatus.FILE_STATUS_OK) activity.replied(context)
            check(context.status == FileStatus.FILE_STATUS_OK) { context.message }
            check(uiVisible && session === active)
            val request=value.toBuilder().setMutationEpoch(context.mutationEpoch).setMutationId(MutationRules.newId()).build()
            activity.dispatched(request.mutationId,request.name)
            val reply = active.requestFiles(request)
            val outcome=activity.replied(reply)
            if (session !== active || !uiVisible) return@launch
            mutable.update { it.copy(mutationMessage = reply.message, recentMutations =
                if (reply.hasMutation()) (listOf(reply.mutation) + it.recentMutations.filter { row -> row.id != reply.mutation.id }).take(32) else it.recentMutations) }
            try { if (recovering) loadDeleted(active) else loadMutations(active) } catch (_: Exception) {
                if (session === active && uiVisible) mutable.update { it.copy(mutationMessage = it.mutationMessage + " Recent history could not be refreshed.") }
            }
            if (session !== active || !uiVisible) return@launch
            check(reply.status == FileStatus.FILE_STATUS_OK && outcome==PhoneActivityOutcome.COMPLETED) { if(reply.status==FileStatus.FILE_STATUS_OK) "The change did not return a confirmed matching receipt." else reply.message }
            if (value.operation == FileOperation.MOVE) mutable.update { it.copy(pendingMove = null) }
            if (folder != null) browseFolder(folder)
        } catch (e: Exception) { activity.interrupted(e is CancellationException); if (session === active && uiVisible) mutable.update { it.copy(mutationMessage = (e.message ?: "The outcome is unavailable.") + " Reconnect and review recent changes before repeating this action.") } }
        finally { if (session === active) mutable.update { it.copy(mutationBusy = false) } } }
    }
    fun createFolder(name: String) = guarded {
        StorageRules.name(name); val parent = state.value.breadcrumbs.lastOrNull() ?: error("Open a destination folder first.")
        changeFile(FileRequest.newBuilder().setOperation(FileOperation.CREATE_DIRECTORY).setDestinationId(parent.id).setName(name).build())
    }
    fun refreshDeleted(next: Boolean = false) = guarded {
        val active = session ?: error("Connect to the computer first."); check(uiVisible && state.value.authenticated)
        controllerScope.launch { try { loadDeleted(active, next) } catch (e: Exception) {
            if (session === active && uiVisible) mutable.update { it.copy(mutationMessage = e.message ?: "Deleted items are unavailable.") }
        } }
    }
    private suspend fun loadDeleted(active: RtcTestSession, next: Boolean = false) {
        val context = active.requestFiles(FileRequest.newBuilder().setOperation(FileOperation.RECOVERY_CONTEXT).build())
        check(context.status == FileStatus.FILE_STATUS_OK) { context.message }; check(uiVisible && session === active)
        val offset = if (next) state.value.deletedOffset else 0
        val reply = active.requestFiles(FileRequest.newBuilder().setOperation(FileOperation.LIST_DELETED).setMutationEpoch(context.mutationEpoch).setOffset(offset.toLong()).build())
        check(reply.status == FileStatus.FILE_STATUS_OK) { reply.message }
        if (session === active && uiVisible) mutable.update { it.copy(deletedItems = if (next) (it.deletedItems + reply.deletedItemsList).distinctBy { row -> row.id }.take(512) else reply.deletedItemsList,
            deletedOffset = offset + 32, moreDeleted = !reply.endOfFile && offset + 32 < 512) }
    }
    fun deleteSelected(entry: FileEntry) = guarded {
        check(state.value.selectedFiles.singleOrNull()?.entry == entry && !entry.isRoot && entry.permissions and StorageRules.DELETE != 0) { "The selection changed. Review it again." }
        changeFile(FileRequest.newBuilder().setOperation(FileOperation.DELETE).setNodeId(entry.id).setExpectedVersion(entry.version).build())
    }
    fun restoreDeleted(item: DeletedInfo, name: String) = guarded {
        StorageRules.name(name); check(state.value.deletedItems.any { it == item }) { "The saved item changed. Refresh it." }
        changeFile(FileRequest.newBuilder().setOperation(FileOperation.RESTORE).setDeletedId(item.id).setName(name).build())
    }
    fun renameSelected(name: String) = guarded {
        StorageRules.name(name); val entry = state.value.selectedFiles.single().entry; check(!entry.isRoot)
        changeFile(FileRequest.newBuilder().setOperation(FileOperation.RENAME).setNodeId(entry.id).setExpectedVersion(entry.version).setName(name).build())
    }
    fun beginMove() = guarded {
        val entry = state.value.selectedFiles.single().entry; check(!entry.isRoot && entry.permissions and StorageRules.MOVE != 0)
        mutable.update { it.copy(pendingMove = entry, selectedFiles = emptyList()) }
    }
    fun cancelMove() { mutable.update { it.copy(pendingMove = null) } }
    fun moveHere() = guarded {
        val entry = state.value.pendingMove ?: error("Select an item to move first."); val parent = state.value.breadcrumbs.lastOrNull() ?: error("Open a destination folder.")
        changeFile(FileRequest.newBuilder().setOperation(FileOperation.MOVE).setNodeId(entry.id).setExpectedVersion(entry.version).setName(entry.name).setDestinationId(parent.id).build())
    }
    private fun clearExplorer() {
        preview.clear()
        discoveryRevision++; discovery?.cancel(); discovery = null; catalog = emptyList(); selectedExplorer.clear()
        mutable.update { it.copy(explorerRows = emptyList(), selectedFiles = emptyList(), explorerPage = 0, explorerPages = 1, explorerCount = 0, discoveryComplete = true,
            discoveryNotice = "", searchQuery = "", fileDetails = null, recentMutations = emptyList(), deletedItems = emptyList(), deletedOffset = 0, moreDeleted = false, pendingMove = null, mutationBusy = false, mutationMessage = "",
            uploadDestinations=emptyList(),uploadDestination=null,uploadDestinationsBusy=false) }
    }
    fun previewText(entry: FileEntry) {
        val active = session ?: return
        val revision=discoveryRevision
        mutable.update { it.copy(fileError="") }
        preview.open(entry,{ uiVisible && session===active && discoveryRevision==revision }) { request ->
            check(uiVisible && session===active && discoveryRevision==revision) { "The preview session ended." }; active.requestFiles(request)
        }
    }
    fun forgetPeer() {
        diagnostics.invalidate()
        costMonitor.close()
        val reconnectTicket=reconnect.blockNow(); peerPaused=true
        reconnectWrite {reconnect.persistPause(); if(reconnect.healthy) reconnect.configure(null,false,reconnectTicket)}
        val oldPeer = peerKey?.let(Rules::id)
        recordActivity(PhoneActivityKind.REVOCATION,PhoneActivityOutcome.REQUESTED,oldPeer?:"")
        val oldBatches = batches.state.value.filter { it.peer == oldPeer && it.phase !in setOf(PhoneBatchPhase.COMPLETED, PhoneBatchPhase.CANCELED) }.map { it.id }
        oldBatches.forEach(batches::pause)
        val revoked = peerApproval?.let { setOf(owner.blockPeer(it)) } ?: emptySet()
        val oldSources = oldPeer?.let(downloadSources::blockPeer) ?: emptySet()
        disconnect("Revoking computer locally...")
        peerKey = null; peerApproval = null
        mutable.update { it.copy(peerName = "", authenticated = false, batchReceiverSupported=false, sends = emptyList()) }
        persistRevocations(revoked)
        oldBatches.forEach { id -> sourcePersistence.launch { runCatching { batches.cancel(id) } } }
        if (oldPeer != null) sourcePersistence.launch { runCatching { downloadSources.releasePeer(oldPeer, oldSources) } }
    }
    fun addPhoneShare(tree: Uri, offeredFlags: Int, label: String) {
        val peer = peerKey?.let(Rules::id) ?: return log("Pair a computer before sharing phone storage.")
        val approval = runCatching { owner.capture(peer) }.getOrElse { return log("Pair this computer again before approving storage.") }
        if (state.value.savingShare) return
        mutable.update { it.copy(savingShare = true) }
        controllerScope.launch {
            try {
                withContext(Dispatchers.IO) { storage.add(approval, tree, offeredFlags, label) }
                mutable.update { it.copy(phoneShares = storage.all()) }
                log("Phone folder approved read-only for the paired computer. Keep the app open to make it available.")
                recordActivity(PhoneActivityKind.SHARING,PhoneActivityOutcome.COMPLETED,peer)
            } catch (_: Exception) { log("The folder could not be approved. Select an accessible folder in the system picker.") }
            finally { mutable.update { it.copy(savingShare = false) } }
        }
    }
    fun setPhoneCreate(id: String, enabled: Boolean) = changePhoneShare {
        val grant = storage.all().single { it.id == id }
        storage.update(id, if (enabled) grant.permissions or StorageRules.CREATE else grant.permissions and StorageRules.CREATE.inv(),
            grant.exclusions, grant.expiresAt, grant.enabled)
    }
    fun setPhoneReceive(id:String,enabled:Boolean) {
        if(!enabled) incoming.pauseAll()
        changePhoneShare {val grant=storage.all().single {it.id==id}; storage.update(id,if(enabled) grant.permissions or StorageRules.WRITE or StorageRules.CREATE else grant.permissions and StorageRules.WRITE.inv(),grant.exclusions,grant.expiresAt,grant.enabled)}
    }
    fun setPhoneReceiveOnly(id:String)=changePhoneShare {incoming.pauseAll(); val grant=storage.all().single {it.id==id}; storage.update(id,StorageRules.WRITE or StorageRules.CREATE,grant.exclusions,grant.expiresAt,grant.enabled)}
    fun setPhoneReadOnly(id:String) {incoming.pauseAll(); changePhoneShare {val grant=storage.all().single {it.id==id}; storage.update(id,StorageRules.READ_ONLY,grant.exclusions,grant.expiresAt,grant.enabled)}}
    fun discardIncomingPrivate(id:String) {check(uiVisible); controllerScope.launch {try {withContext(Dispatchers.IO) {auditRecovery(PhoneActivityKind.INCOMING_PRIVATE_CLEANUP) {incoming.discardPrivate(id)}}} catch(e:Exception) {log(e.message?:"Private incoming copy was preserved.")}; syncTransferRecovery()}}
    fun archiveIncomingHistory() {
        check(uiVisible); session?.close(); session=null
        controllerScope.launch {try {withContext(Dispatchers.IO) {auditRecovery(PhoneActivityKind.INCOMING_HISTORY_RESET) {incoming.archiveHistory()}}; log("Incoming history was preserved and reset. Reconnect only after reviewing any earlier provider copies.")}
            catch(e:Exception) {log(e.message?:"Incoming history was kept.")}; syncTransferRecovery()}
    }
    fun archiveIncomingBatchPlans() {
        check(uiVisible); pauseConnections()
        controllerScope.launch {try {withContext(Dispatchers.IO) {auditRecovery(PhoneActivityKind.BATCH_HISTORY_RESET) {storage.batchPlans.archiveHistory()}}; log("Batch-plan history was preserved and reset. Provider files were kept.")}
            catch(e:Exception) {log(e.message?:"Batch-plan history was kept for review.")}; syncTransferRecovery()}
    }
    suspend fun inspectOrphanIncomingParts(start:Int):List<PhoneUnreferencedReceivingPart> =withContext(Dispatchers.IO) {incoming.inspectOrphans(start) {uiVisible && closing==null}}
    suspend fun reviewOrphanIncomingParts():PhoneReceivingPartRemovalReview=withContext(Dispatchers.IO) {incoming.reviewOrphanRemoval {uiVisible && closing==null}}
    suspend fun confirmOrphanIncomingParts(review:PhoneReceivingPartRemovalReview):Boolean=withContext(Dispatchers.IO) {auditRecovery(PhoneActivityKind.RECEIVING_PART_CLEANUP) {incoming.confirmOrphanRemoval(review) {uiVisible && closing==null}}}
    suspend fun continueOrphanIncomingParts():Boolean=withContext(Dispatchers.IO) {auditRecovery(PhoneActivityKind.RECEIVING_PART_CLEANUP) {incoming.continueOrphanRemoval {uiVisible && closing==null}}}
    suspend fun inspectOrphanBatchParts(start:Int):List<PhoneUnreferencedReceivingPart> =withContext(Dispatchers.IO) {storage.batchPlans.inspectOrphanParts(start) {uiVisible && closing==null}}
    suspend fun reviewOrphanBatchParts():PhoneReceivingPartRemovalReview=withContext(Dispatchers.IO) {storage.batchPlans.reviewOrphanPartRemoval {uiVisible && closing==null}}
    suspend fun confirmOrphanBatchParts(review:PhoneReceivingPartRemovalReview):Boolean=withContext(Dispatchers.IO) {auditRecovery(PhoneActivityKind.RECEIVING_PART_CLEANUP) {storage.batchPlans.confirmOrphanPartRemoval(review) {uiVisible && closing==null}}}
    suspend fun continueOrphanBatchParts():Boolean=withContext(Dispatchers.IO) {auditRecovery(PhoneActivityKind.RECEIVING_PART_CLEANUP) {storage.batchPlans.continueOrphanPartRemoval {uiVisible && closing==null}}}
    fun phoneApprovalCurrent(grant: AndroidStorageGrant) = storage.approvalCurrent(grant)
    fun removePhoneShare(id: String) {
        // End the channel before removing policy, so no queued response can escape after revocation.
        session?.close(); session = null
        changePhoneShare { storage.remove(id) }
    }
    private fun changePhoneShare(action: () -> Unit) {
        if (state.value.savingShare) return
        mutable.update { it.copy(savingShare = true) }
        controllerScope.launch {
            try {
                withContext(Dispatchers.IO) { action() }
                mutable.update { it.copy(phoneShares = storage.all()) }
                log("Phone storage permissions updated.")
                recordActivity(PhoneActivityKind.SHARING,PhoneActivityOutcome.COMPLETED)
            } catch (e: Exception) { log(e.message ?: "Could not update folder permissions.") }
            finally { mutable.update { it.copy(savingShare = false) } }
        }
    }
    private fun send(kind: String, payload: MessageLite) {
        check(socket?.send(Rules.pack(kind, payload).toByteString()) == true) { "Coordinator connection unavailable." }
    }
    fun disconnect(message: String = "Disconnected. Phone files are unavailable remotely.",reason:PhoneConnectionEnd=PhoneConnectionEnd.OWNER) {
        val hadCoordinator=state.value.connected || state.value.connecting
        withdrawPresence()
        cancelPairAttempt()
        pausePeerWork(reason)
        generation++
        registrationSent = false
        // Cancel a pending connect as well as an open socket; backgrounding must not leave a queued handshake.
        socket?.cancel(); socket = null
        lifetime?.cancel(); lifetime = null; scope = null
        if(hadCoordinator) recordActivity(PhoneActivityKind.COORDINATOR_CONNECTION,reason.outcome,"")
        pending = null; expectedToken = null; expectedHost = null; locallyApproved = false
        mutable.update { it.copy(connected = false, connecting = false, authenticated = false, batchReceiverSupported=false, peerConnecting=false, presenceSupported=false, peerAvailable=false, code = "",
            browsing = false, remoteEntries = emptyList(), breadcrumbs = emptyList(), textPreview = "", listingBusy = false, outgoingBatchPayloads = emptyMap()) }
        log(message)
    }
    private fun pausePeerWork(reason:PhoneConnectionEnd=PhoneConnectionEnd.OWNER) {
        history.cancelBackupReview()
        sourceReviewEpoch++; downloads.invalidateSourceReviews(); incoming.invalidateWindowReviews()
        uploads.pauseSending()
        outgoingBatchSender.pauseAll()
        outgoingBatches.pauseAll()
        clearExplorer()
        batches.pauseAll(); mutable.update { it.copy(batchPlan = null, incomingPayloadEstimates = emptyMap(),
            incomingFinalizations = emptyMap(), incomingBatchFileProgress = emptyList()) }
        val sending = authorizedSources.keys.toList()
        authorizedSources = emptyMap(); backgroundToken = ""; pendingBackgroundToken = ""; pendingBackgroundIds = emptySet(); pendingBackgroundSources = emptyMap()
        mutable.update { it.copy(backgroundDownloads = emptySet(), backgroundSends = emptySet(), backgroundReady = false) }
        syncCostMonitor()
        sending.forEach { pauseSourceNow(it) }
        application.stopService(android.content.Intent(application, TransferService::class.java))
        downloads.pauseAll()
        session?.let {discardedSessions.add(it.sessionId); it.close(reason)}; session = null
    }
    private fun guarded(action: () -> Unit) {
        try { action() } catch (e: Exception) { log(e.message ?: "Operation failed.") }
    }
    fun chooseDownloadDestination(tree: Uri, flags: Int) {
        controllerScope.launch {
            try {
                withContext(Dispatchers.IO) {
                    auditLocal(PhoneActivityKind.DESTINATION_SETTINGS) {safAccess.selectDestination(tree, flags)}
                }
                mutable.update { it.copy(downloadDestination = tree.toString()) }
                log("Download folder selected. This does not share it with the computer.")
            } catch (_: Exception) { log("Choose a writable phone folder and allow persistent access.") }
        }
    }
    fun clearDownloadDestination() = controllerScope.launch {
        try { withContext(Dispatchers.IO) {auditLocal(PhoneActivityKind.DESTINATION_SETTINGS) { safAccess.clearDestination() }}; mutable.update { it.copy(downloadDestination = "") }
            log("Download destination cleared. Queued downloads keep their existing folder access until their records are removed.") }
        catch (_: Exception) { log("Could not clear the saved destination.") }
    }
    fun downloadFile(entry: FileEntry) {
        if (!uiVisible) return log("Open iConnect to start another transfer.")
        val active = session ?: return log("Connect to the paired computer first.")
        val peer = peerKey?.let(Rules::id) ?: return
        val destination = state.value.downloadDestination
        if (destination.isEmpty()) return log("Choose a download folder while disconnected first.")
        if (state.value.downloadBusy) return
        mutable.update { it.copy(downloadBusy = true) }
        controllerScope.launch {
            try {
                val id = withContext(Dispatchers.IO) { downloads.enqueue(peer, entry, Uri.parse(destination)) }
                check(session === active && peerKey?.let(Rules::id) == peer) { "Reconnect the source computer to resume the queued download." }
                downloads.resume(id, peer) { request -> withContext(Dispatchers.Main.immediate) { active.requestFiles(request) } }
            } catch (e: Exception) { if (e !is CancellationException) log(e.message ?: "The download could not be queued.") }
            finally { mutable.update { it.copy(downloadBusy = false) } }
        }
    }
    fun planBatch(entries: List<FileEntry>) {
        if (state.value.planningBatch) return
        val approval = peerApproval?.takeIf(owner::isCurrent) ?: return log("Pair and connect the source computer first.")
        val active = session ?: return log("Connect the source computer first.")
        if (!uiVisible || !state.value.authenticated || state.value.downloadDestination.isEmpty()) return log("Choose a destination and open an authenticated connection first.")
        val tree = state.value.downloadDestination
        val rows = entries.map { entry -> selectedExplorer[entry.id] ?: catalog.firstOrNull { it.entry.id == entry.id } ?: return log("A selected item is no longer loaded. Refresh it.") }
        if (rows.isEmpty()) return log("Select at least one item.")
        val selection = PhoneExplorerCatalog.normalizeSelection(rows)
        mutable.update { it.copy(planningBatch = true, batchPlan = null, directoryContinuation = "") }
        controllerScope.launch {
            try {
                val manifest = withContext(Dispatchers.IO) { PhoneTransferManifests.build(selection.map { PhoneTransferSelection(it.entry, it.sourcePath) }) { value ->
                    withContext(Dispatchers.Main.immediate) { check(uiVisible && session === active && owner.isCurrent(approval)); active.requestFiles(value) }
                } }
                check(uiVisible && session === active && owner.isCurrent(approval) && tree == state.value.downloadDestination) { "The source or destination changed. Review a new plan." }
                mutable.update { it.copy(batchPlan = PhoneBatchPlan(approval, active.sessionId, tree, manifest)) }
            } catch (e: Exception) { if (e !is CancellationException) log(e.message ?: "The folder plan could not be prepared.") }
            finally { mutable.update { it.copy(planningBatch = false) } }
        }
    }
    fun dismissBatchPlan() { mutable.update { it.copy(batchPlan = null) } }
    fun confirmBatch(collision: PhoneCollision) = guarded {
        val plan = requireNotNull(state.value.batchPlan); val active = requireNotNull(session)
        check(uiVisible && owner.isCurrent(plan.approval) && active.sessionId == plan.sessionId && plan.tree == state.value.downloadDestination)
        dismissBatchPlan()
        controllerScope.launch {
            try {
                val id = withContext(Dispatchers.IO) { batches.enqueue(plan.approval.id, plan.approval.epoch, plan.manifest, Uri.parse(plan.tree), collision) }
                check(uiVisible && owner.isCurrent(plan.approval) && session === active) { "The connection changed. Resume the queued batch deliberately after reconnecting." }
                startBatch(id, plan.approval, active)
            } catch (e: Exception) { if (e !is CancellationException) log(e.message ?: "The batch could not be queued.") }
        }
    }
    private fun startBatch(id: String, approval: PeerApproval, active: RtcTestSession?, anotherCopy: String = "") {
        batches.resume(id, approval.id, approval.epoch, { value -> withContext(Dispatchers.Main.immediate) {
            check(uiVisible && owner.isCurrent(approval) && active != null && session === active) { "Reconnect the same trusted computer to continue pending items." }
            active.requestFiles(value)
        } }, anotherCopy)
    }
    fun resumeBatch(id: String, anotherCopy: String = "") = guarded {
        check(uiVisible)
        val row = batches.state.value.single { it.id == id }
        val approval = requireNotNull(peerApproval?.takeIf { it.id == row.peer && it.epoch == row.epoch && owner.isCurrent(it) }) {
            "This batch belongs to an earlier pairing. Cancel it and review a new copy." }
        startBatch(id, approval, session, anotherCopy)
    }
    fun pauseBatch(id: String) = batches.pause(id)
    fun cancelBatch(id: String) = sourcePersistence.launch { runCatching { batches.cancel(id) }.onFailure { controllerScope.launch { log(it.message ?: "Batch cleanup could not finish.") } } }
    fun canRemoveBatch(id: String) = batches.canRemove(id)
    fun removeBatch(id: String) = controllerScope.launch { runCatching { withContext(Dispatchers.IO) { batches.remove(id) } }.onFailure { log(it.message ?: "The batch record could not be removed.") } }
    fun skipUncertainBatchFolder(id: String, index: Int) = controllerScope.launch {
        runCatching { withContext(Dispatchers.IO) { batches.skipUncertainFolder(id, index) } }.onFailure { log(it.message ?: "This folder cannot be skipped while active.") }
    }
    fun resumeDownload(id: String) = guarded {
        check(uiVisible) { "Open iConnect to resume a transfer." }
        val row = state.value.downloads.single { it.id == id }
        if (row.offset == row.size && row.sha256.isNotEmpty()) {
            downloads.resume(id, row.peer) { error("The source is offline.") }; return@guarded
        }
        val active = requireNotNull(session) { "Reconnect the source computer first." }
        downloads.resume(id, Rules.id(requireNotNull(peerKey))) { request -> withContext(Dispatchers.Main.immediate) { active.requestFiles(request) } }
    }
    fun pauseDownload(id: String) = downloads.pause(id)
    private fun payloadScope(row:PhoneDownload,limit:Long)="${row.peer}/${row.ticket}/${row.version}/${session?.sessionId ?: generation}/$limit"
    private fun matchesPayload(row:PhoneDownload,point:PhonePayloadProgress)=row.active && row.phase==PhoneDownloadPhase.DOWNLOADING &&
        row.id==point.id && row.peer==point.peer && row.ticket==point.ticket && row.version==point.version && row.size==point.size
    private fun refreshPayloadEstimates() {
        val rows=downloads.state.value; val limit=state.value.transferNetwork.preferences.bytesPerSecond; val now=android.os.SystemClock.elapsedRealtime()
        val live=rows.mapNotNull {row->receivingPayload[row.id]?.takeIf {matchesPayload(row,it)}?.let {row.id to maxOf(row.offset,it.received)}}.toMap()
        payloadRates.retain(rows.map {it.id}.toSet())
        val estimates=rows.associate {row->row.id to payloadRates.read(row.id,payloadScope(row,limit),row.size,live[row.id] ?: row.offset,
            row.active && row.phase==PhoneDownloadPhase.DOWNLOADING,now,limit)}
        val finalizations = if (uiVisible && closing == null) downloads.readFinalizations() else emptyMap()
        mutable.update {it.copy(receivingBytes=live,payloadEstimates=estimates,downloadFinalizations=finalizations)}
    }
    private fun refreshUploadPayloadEstimates() {
        val limit = state.value.transferNetwork.preferences.bytesPerSecond
        val estimates=if(uiVisible && closing==null) uploadPayloadRates.refresh(uploads.state.value,uploads.payloadProgress.value,
            session?.sessionId ?: generation.toString(),limit,android.os.SystemClock.elapsedRealtime())
            else {uploadPayloadRates.clear(); emptyMap()}
        val batchPayloads = if (uiVisible && closing == null && state.value.authenticated) outgoingBatchSender.readPayloads(limit) else emptyMap()
        val incomingPayloads = incomingPayloadEstimates(state.value.incomingCopies, limit)
        val incomingFinalizations = if (uiVisible && closing == null && state.value.authenticated) incoming.readFinalizations() else emptyMap()
        val incomingBatchFiles = if (uiVisible && closing == null && state.value.authenticated)
            peerApproval?.let { storage.batchPlans.readFileProgress(it.id, it.epoch, limit) }.orEmpty() else emptyList()
        mutable.update {it.copy(uploadPayloadEstimates=estimates, outgoingBatchPayloads=batchPayloads, incomingPayloadEstimates=incomingPayloads,
            incomingFinalizations=incomingFinalizations,incomingBatchFileProgress=incomingBatchFiles)}
    }
    private fun incomingPayloadEstimates(rows: List<PhoneIncomingCopy>, limit: Long) =
        if (uiVisible && closing == null && state.value.authenticated) incoming.readPayloads(limit, rows) else emptyMap()
    suspend fun reviewDownloadSource(id:String):PhoneDownloadSourceReview {
        check(uiVisible && closing==null && state.value.authenticated && !state.value.savingTrust)
        val active=requireNotNull(session) {"Reconnect the source computer first."}
        val approval=requireNotNull(peerApproval?.takeIf(owner::isCurrent))
        val row=state.value.downloads.single {it.id==id}
        check(row.peer==approval.id) {"Reconnect the computer that owns this retained download."}
        val selected=selectedExplorer.values.singleOrNull()?.entry ?: error("Select exactly one current file in the computer explorer first.")
        val epoch=sourceReviewEpoch
        val allowed={uiVisible && session===active && sourceReviewEpoch==epoch && owner.isCurrent(approval) && state.value.authenticated && !state.value.savingTrust}
        return withContext(Dispatchers.IO) { downloads.prepareSourceReview(id,approval.id,selected,{request->withContext(Dispatchers.Main.immediate) {
            // Lease cleanup may finish after hiding; it grants no access to content.
            check(session===active && (request.operation==FileOperation.RELEASE_DOWNLOAD || allowed()))
            active.requestFiles(request)
        }},allowed) }
    }
    suspend fun confirmDownloadSource(review:PhoneDownloadSourceReview)=withContext(Dispatchers.IO) {downloads.confirmSourceReview(review)}
    fun cancelDownloadSourceReview(id:String)=downloads.cancelSourceReview(id)
    suspend fun reviewIncomingWindow(id:String):PhoneIncomingWindowReview {
        check(uiVisible && closing==null && owner.healthy && !state.value.savingTrust)
        val current=sourceReviewEpoch
        return withContext(Dispatchers.IO) {incoming.reviewWindow(id) {uiVisible && closing==null && owner.healthy && !state.value.savingTrust && current==sourceReviewEpoch}}
    }
    suspend fun confirmIncomingWindow(review:PhoneIncomingWindowReview)=withContext(Dispatchers.IO) {
        check(uiVisible && closing==null && owner.healthy && !state.value.savingTrust); incoming.confirmWindow(review)
    }
    fun cancelIncomingWindowReview(id:String)=incoming.cancelWindowReview(id)
    suspend fun reviewBatchDownloadSource(batch:String,index:Int):PhoneBatchDownloadSourceReview {
        check(uiVisible && closing==null && state.value.authenticated && !state.value.savingTrust)
        val active=requireNotNull(session) {"Reconnect the source computer first."}
        val row=state.value.batches.single {it.id==batch}
        val approval=requireNotNull(peerApproval?.takeIf {it.id==row.peer && it.epoch==row.epoch && owner.isCurrent(it)}) {"Reconnect this batch's original paired computer."}
        val selected=selectedExplorer.values.singleOrNull()?.entry ?: error("Select exactly one current file in the computer explorer first.")
        val epoch=sourceReviewEpoch
        val allowed={uiVisible && session===active && sourceReviewEpoch==epoch && owner.isCurrent(approval) && state.value.authenticated && !state.value.savingTrust}
        return withContext(Dispatchers.IO) {batches.prepareSourceReview(batch,index,approval.id,approval.epoch,selected,{request->withContext(Dispatchers.Main.immediate) {
            check(session===active && (request.operation==FileOperation.RELEASE_DOWNLOAD || allowed())); active.requestFiles(request)
        }},allowed)}
    }
    suspend fun confirmBatchDownloadSource(review:PhoneBatchDownloadSourceReview)=withContext(Dispatchers.IO) {batches.confirmSourceReview(review)}
    fun cancelBatchDownloadSourceReview(id:String)=batches.cancelSourceReview(id)
    fun saveDownload(id: String, anotherCopy: Boolean = false) = guarded { check(uiVisible); downloads.publish(id, anotherCopy) }
    fun cancelDownload(id: String): Job {
        val active = session
        val peer = peerKey?.let(Rules::id)
        val samePeer = state.value.downloads.firstOrNull { it.id == id }?.peer == peer
        return controllerScope.launch { try { withContext(Dispatchers.IO) {
            downloads.cancel(id, if (active != null && samePeer) { request -> withContext(Dispatchers.Main.immediate) { active.requestFiles(request) } } else null)
        } }
        catch (e: Exception) { log(e.message ?: "The private partial file could not be removed.") } } }
    fun removeDownload(id: String) = controllerScope.launch { try { withContext(Dispatchers.IO) { downloads.remove(id) } }
        catch (_: Exception) { log("This download is still active or could not be removed.") } }
    fun close(): Job {
        closing?.let { return it }
        history.cancelBackupReview()
        diagnostics.invalidate()
        reconnect.blockNow(); stopReconnectLoop()
        disconnect(); val localClosing = localFiles.close(); downloadSources.close()
        if (coordinatorSockets is OkHttpClient) {
            coordinatorSockets.dispatcher.executorService.shutdown()
            coordinatorSockets.connectionPool.evictAll()
        }
        payloadRates.clear(); uploadPayloadRates.clear(); receivingPayload=emptyMap(); mutable.update {it.copy(receivingBytes=emptyMap(),payloadEstimates=emptyMap(),downloadFinalizations=emptyMap(),uploadPayloadEstimates=emptyMap(),outgoingBatchPayloads=emptyMap())}
        controllerScope.cancel()
        // Waiting here on Main would deadlock a canceled RPC dispatched to Main.
        return CoroutineScope(Dispatchers.IO).launch {
            localClosing.join()
            sourcePersistence.coroutineContext.job.children.toList().joinAll(); sourcePersistence.cancel()
            incoming.shutdown(); storage.batchPlans.shutdown(); outgoingBatchSender.shutdown(); outgoingBatches.shutdown(); uploads.shutdown(); batches.shutdown(); downloadSources.shutdown(); downloads.shutdown(); transferNetwork?.close(); history.close()
        }.also { closing = it }
    }
    fun setUiVisible(visible: Boolean) {
        if(closing!=null) return
        uiVisible = visible
        refreshUploadPayloadEstimates()
        if(!visible) {history.cancelBackupReview(); sourceReviewEpoch++; downloads.invalidateSourceReviews(); incoming.invalidateWindowReviews()}
        syncCostMonitor()
        localFiles.setVisible(visible)
        if (visible) {startReconnectLoop(); startPresenceLoop(); return}
        incoming.pauseAll()
        storage.batchPlans.pauseFiles()
        uploads.pauseAll()
        outgoingBatchSender.pauseAll()
        outgoingBatches.pauseAll()
        withdrawPresence()
        stopReconnectLoop()
        clearExplorer()
        batches.pauseAll(); mutable.update { it.copy(batchPlan = null) }
        checkBackground()
        session?.suspendStorageAvailability(if (state.value.backgroundReady) authorizedSources.keys else emptySet())
        val allowed = if (state.value.backgroundReady) state.value.backgroundDownloads else emptySet()
        if (allowed.isEmpty() && authorizedSources.isEmpty()) disconnect("App left the foreground. Remote availability ended.",PhoneConnectionEnd.PAUSED)
        else {
            downloads.state.value.filter { it.active && it.id !in allowed }.forEach { downloads.pause(it.id) }
            mutable.update { it.copy(browsing = false, remoteEntries = emptyList(), textPreview = "", breadcrumbs = emptyList()) }
            log("Only the selected transfers continue. Phone storage browsing is unavailable.")
        }
    }
    suspend fun reviewDiagnostics():PhoneDiagnosticReview=withContext(Dispatchers.IO) {
        check(uiVisible && closing==null) {"Open iConnect to review a diagnostic report."}
        diagnostics.review(state.value,owner.healthy)
    }
    fun cancelDiagnostics(id:String)=diagnostics.cancel(id)
    suspend fun exportDiagnostics(review:PhoneDiagnosticReview,uri:Uri)=withContext(Dispatchers.IO) {diagnostics.export(review,uri)}
    suspend fun configureActivity(days:Int) {check(uiVisible && closing==null); history.configure(days)}
    suspend fun clearActivity() {check(uiVisible && closing==null); history.clear()}
    suspend fun resetActivity() {check(uiVisible && closing==null); history.reset()}
    suspend fun inspectActivityBackups(start:Int=0,continuing:()->Boolean={true}):PhoneActivityBackupInventory {
        check(uiVisible && closing==null); return history.inspectBackups(start,continuing)
    }
    suspend fun reviewActivityBackup(id:String,continuing:()->Boolean={true}):PhoneActivityBackupReview {
        check(uiVisible && closing==null); return history.reviewBackup(id,continuing)
    }
    suspend fun confirmActivityBackup(review:PhoneActivityBackupReview,continuing:()->Boolean={true}) {
        check(uiVisible && closing==null); history.confirmBackup(review,continuing)
    }
    fun cancelActivityBackupReview()=history.cancelBackupReview()
    internal suspend fun flushActivity()=history.flush()
    fun stageUpload(entry:FileEntry)=guarded {
        check(uiVisible && !state.value.savingTrust && closing==null)
        val approval=requireNotNull(peerApproval?.takeIf(owner::isCurrent)) {"Pair a computer before preparing a send."}
        val selected=localFiles.captureUpload(entry)
        controllerScope.launch {try {withContext(Dispatchers.IO) {uploads.stage(approval,selected) {uiVisible && owner.isCurrent(approval)}}}
            catch(e:Exception) {log(e.message?:"The private source copy could not be prepared.")}; syncTransferRecovery()}
    }
    fun stageUploadBatch(entries:List<FileEntry>,collision:UploadCollision)=guarded {
        check(uiVisible && !state.value.savingTrust && closing==null)
        val approval=requireNotNull(peerApproval?.takeIf(owner::isCurrent)) {"Pair a computer before preparing a send."}
        val selected=localFiles.captureBatch(entries)
        controllerScope.launch {try {withContext(Dispatchers.IO) {outgoingBatches.stage(approval,selected,collision) {uiVisible && owner.isCurrent(approval)}}}
            catch(e:Exception) {log(e.message?:"The private folder copies could not be prepared.")}; syncTransferRecovery()}
    }
    fun sendUpload(id:String)=guarded {
        val active=requireNotNull(session); val approval=requireNotNull(peerApproval?.takeIf(owner::isCurrent))
        check(uiVisible && state.value.authenticated && !state.value.savingTrust)
        uploads.send(id,approval,state.value.uploadDestination?:state.value.breadcrumbs.lastOrNull(),{uiVisible && session===active && owner.isCurrent(approval) && state.value.authenticated}) {request->
            withContext(Dispatchers.Main.immediate) {check(uiVisible && session===active && owner.isCurrent(approval)); active.requestFiles(request)}
        }
    }
    fun supportsPublishedUploadCheck()=session?.acceptsPublishedUploadChecks==true
    fun supportsPublishedBatchCheck()=session?.acceptsPublishedBatchChecks==true
    fun verifyPublishedBatch(id:String)=guarded {
        val active=requireNotNull(session); val approval=requireNotNull(peerApproval?.takeIf(owner::isCurrent))
        check(active.acceptsPublishedBatchChecks) {"The connected receiver does not support fresh saved-batch checks."}
        val allowed={uiVisible && session===active && owner.isCurrent(approval) && state.value.authenticated && closing==null}
        check(allowed() && !state.value.savingTrust)
        controllerScope.launch {
            try {
                outgoingBatchSender.verifyPublished(id,approval,allowed) {request->
                    withContext(Dispatchers.Main.immediate) {check(allowed()); active.requestFiles(request)}
                }
                if(allowed()) log("The receiver verified the saved batch at the time of this check. Received files remain untrusted.")
            } catch(e:CancellationException) {throw e}
            catch(e:Exception) {log(e.message?:"The saved batch could not be verified. Its copies and receipts were kept.")}
        }
    }
    fun verifyPublishedUpload(id:String)=guarded {
        val active=requireNotNull(session)
        val approval=requireNotNull(peerApproval?.takeIf(owner::isCurrent))
        check(active.acceptsPublishedUploadChecks) {"The connected computer does not support fresh published-copy checks."}
        val allowed={uiVisible && session===active && owner.isCurrent(approval) && state.value.authenticated && closing==null}
        check(allowed() && !state.value.savingTrust)
        controllerScope.launch {
            try {
                uploads.verifyPublished(id,approval,allowed) {request->
                    withContext(Dispatchers.Main.immediate) {check(allowed()); active.requestFiles(request)}
                }
                if(allowed()) log("The computer reread the published file and verified its bytes.")
            } catch(e:CancellationException) {throw e}
            catch(e:Exception) {log(e.message?:"The published copy could not be verified. Its receipt was kept.")}
        }
    }
    fun queueOutgoingBatch(source:String,anotherCopy:Boolean=false)=guarded {
        check(uiVisible && !state.value.savingTrust && closing==null)
        val approval=requireNotNull(peerApproval?.takeIf(owner::isCurrent))
        check(outgoingBatches.state.value.single {it.id==source}.peer==approval.id)
        controllerScope.launch {try {withContext(Dispatchers.IO) {
            check(uiVisible && owner.isCurrent(approval) && closing==null)
            outgoingBatchSender.queue(source,anotherCopy)
        }} catch(e:Exception) {log(e.message?:"The private batch could not be queued.")}; syncTransferRecovery()}
    }
    suspend fun queueReviewedBatch(source:String,anotherCopy:Boolean=false):String=withContext(Dispatchers.IO) {
        check(uiVisible && !state.value.savingTrust && closing==null)
        val approval=requireNotNull(peerApproval?.takeIf(owner::isCurrent))
        check(outgoingBatches.state.value.single {it.id==source}.let {it.peer==approval.id && it.pairing==approval.epoch})
        outgoingBatchSender.queue(source,anotherCopy)
    }
    fun sendOutgoingBatch(id:String,destination:FileEntry?=state.value.uploadDestination?:state.value.breadcrumbs.lastOrNull())=controlOutgoingBatch(id,false,destination)
    fun stopOutgoingBatch(id:String)=guarded {
        check(uiVisible && closing==null)
        controllerScope.launch {try {
            outgoingBatchSender.pauseAndWait(id)
            val row=outgoingBatchSender.state.value.single {it.id==id}
            if(row.header.isEmpty()) outgoingBatchSender.stop(id,{uiVisible && closing==null}) {error("An unstarted job must not contact a receiver.")}.join()
            else controlOutgoingBatch(id,true)
        } catch(e:Exception) {log(e.message?:"The original batch stop needs review.")}}
    }
    private fun controlOutgoingBatch(id:String,stop:Boolean,destination:FileEntry?=null)=guarded {
        val active=requireNotNull(session); val approval=requireNotNull(peerApproval?.takeIf(owner::isCurrent))
        check(uiVisible && state.value.authenticated && !state.value.savingTrust && closing==null)
        check(active.acceptsFolderBatches) {"The connected device has not enabled compatible folder/file batch receiving."}
        check(outgoingBatchSender.state.value.single {it.id==id}.let {it.peer==approval.id && it.pairing==approval.epoch})
        val allowed={uiVisible && session===active && owner.isCurrent(approval) && state.value.authenticated && closing==null}
        val request:suspend (FileRequest)->FileResponse={value->withContext(Dispatchers.Main.immediate) {check(allowed()); active.requestFiles(value)}}
        if(stop) outgoingBatchSender.stop(id,allowed,request)
        else outgoingBatchSender.send(id,destination,allowed,request)
    }
    fun pauseOutgoingBatch(id:String) {
        outgoingBatchSender.pause(id)
        refreshUploadPayloadEstimates()
    }
    fun outgoingBatchResult(id:String,index:Int)=outgoingBatchSender.itemResult(id,index)
    fun privateBatchPeerName(peer:String,pairing:String):String=owner.current(peer)?.takeIf {it.epoch==pairing}?.name?:"Earlier pairing · ${peer.take(8)}"
    fun incomingBatchPeerName(peer:String):String=owner.current(peer)?.name?:"Earlier device · ${peer.take(8)}"
    fun privateBatchCurrent(peer:String,pairing:String)=peerApproval?.let {it.id==peer && it.epoch==pairing && owner.isCurrent(it)}==true
    fun pausePrivateBatchPreparation(id:String)=guarded {check(uiVisible); outgoingBatches.cancel(id)}
    suspend fun inspectPrivateBatch(id:String):UploadBatchManifest=withContext(Dispatchers.IO) {check(uiVisible && closing==null); outgoingBatches.inspectManifest(id)}
    suspend fun outgoingBatchResults(id:String,start:Int):List<PhoneBatchItemResult> =withContext(Dispatchers.IO) {
        check(uiVisible && closing==null); val row=outgoingBatchSender.state.value.single {it.id==id}; require(start>=0 && start<=row.next)
        (start until minOf(row.next,start+50)).map {outgoingBatchSender.itemResult(id,it)}
    }
    suspend fun batchResultPage(id:String,start:Int):List<PhoneBatchResultView> =withContext(Dispatchers.IO) {
        check(uiVisible && closing==null); val row=outgoingBatchSender.state.value.single {it.id==id}; require(!row.purging && start>=0 && start<row.items)
        val manifest=runCatching {outgoingBatches.inspectManifest(row.source)}.getOrNull(); val receiver=Rules.decode(row.receiverOutcomes)
        (start until minOf(row.items,start+50)).map {index->
            val result=if(index<row.next) outgoingBatchSender.itemResult(id,index) else null
            val name=manifest?.let {batchPath(it,index)}?:result?.name?.ifEmpty {null}?:"Item ${index+1}"
            val phase=result?.phase?.number?:receiver.getOrNull(index)?.toInt()?:if(row.attempted && index==row.next) 4 else 0
            PhoneBatchResultView(index,name,when(phase) {1->"Copied"; 2->"Skipped"; 3->"Issue"; 4->"Review receiver copy"; else->"Not processed"},result?.message?:"")
        }
    }
    suspend fun reviewOutgoingBatchHistory(id:String,action:PhoneBatchHistoryAction):PhoneBatchHistoryReview=withContext(Dispatchers.IO) {
        check(uiVisible && closing==null); outgoingBatchSender.reviewHistory(id,action)
    }
    suspend fun reviewBatchSenderReset():PhoneSenderResetReview=withContext(Dispatchers.IO) {
        check(uiVisible && closing==null); outgoingBatchSender.pauseAll(); outgoingBatchSender.whenIdle()
        outgoingBatchSender.reviewStoreReset {uiVisible && closing==null}
    }
    suspend fun confirmBatchSenderReset(review:PhoneSenderResetReview):String=withContext(Dispatchers.IO) {auditRecovery(PhoneActivityKind.SENDER_STORE_RESET) {outgoingBatchSender.confirmStoreReset(review) {uiVisible && closing==null}}}
    suspend fun reviewBatchSenderArchiveRemoval(id:String):PhoneSenderArchiveRemovalReview=withContext(Dispatchers.IO) {outgoingBatchSender.reviewArchiveRemoval(id) {uiVisible && closing==null}}
    suspend fun confirmBatchSenderArchiveRemoval(review:PhoneSenderArchiveRemovalReview):Boolean=withContext(Dispatchers.IO) {auditRecovery(PhoneActivityKind.SENDER_ARCHIVE_CLEANUP) {outgoingBatchSender.confirmArchiveRemoval(review) {uiVisible && closing==null}}}
    suspend fun continueBatchSenderArchiveRemoval(id:String):Boolean=withContext(Dispatchers.IO) {auditRecovery(PhoneActivityKind.SENDER_ARCHIVE_CLEANUP) {outgoingBatchSender.continueArchiveRemoval(id) {uiVisible && closing==null}}}
    suspend fun batchSenderArchiveFiles(id:String,start:Int):List<PhoneSenderArchiveFile> =withContext(Dispatchers.IO) {outgoingBatchSender.archiveFiles(id,start) {uiVisible && closing==null}}
    suspend fun reviewPrivateBatchStoreReset():PhoneSourceResetReview=withContext(Dispatchers.IO) {
        check(uiVisible && closing==null); outgoingBatchSender.pauseAll(); outgoingBatches.pauseAll(); outgoingBatchSender.whenIdle(); outgoingBatches.whenIdle()
        outgoingBatchSender.reviewSourceStoreReset {uiVisible && closing==null}
    }
    suspend fun confirmPrivateBatchStoreReset(review:PhoneSourceResetReview):String=withContext(Dispatchers.IO) {auditRecovery(PhoneActivityKind.SOURCE_STORE_RESET) {outgoingBatchSender.confirmSourceStoreReset(review) {uiVisible && closing==null}}}
    suspend fun reviewPrivateBatchArchiveRemoval(id:String):PhoneSourceArchiveRemovalReview=withContext(Dispatchers.IO) {outgoingBatches.reviewArchiveRemoval(id) {uiVisible && closing==null}}
    suspend fun confirmPrivateBatchArchiveRemoval(review:PhoneSourceArchiveRemovalReview):Boolean=withContext(Dispatchers.IO) {auditRecovery(PhoneActivityKind.SOURCE_ARCHIVE_CLEANUP) {outgoingBatches.confirmArchiveRemoval(review) {uiVisible && closing==null}}}
    suspend fun continuePrivateBatchArchiveRemoval(id:String):Boolean=withContext(Dispatchers.IO) {auditRecovery(PhoneActivityKind.SOURCE_ARCHIVE_CLEANUP) {outgoingBatches.continueArchiveRemoval(id) {uiVisible && closing==null}}}
    suspend fun privateBatchArchiveEntries(id:String,start:Int):List<PhoneSourceArchiveEntry> =withContext(Dispatchers.IO) {outgoingBatches.archiveEntries(id,start) {uiVisible && closing==null}}
    suspend fun reviewIncomingBatchRetirement(id:String):PhoneIncomingBatchRetirementReview=withContext(Dispatchers.IO) {check(uiVisible && closing==null); storage.batchPlans.reviewRetirement(id)}
    suspend fun confirmIncomingBatchRetirement(review:PhoneIncomingBatchRetirementReview):Boolean=withContext(Dispatchers.IO) {auditRecovery(PhoneActivityKind.RECEIVER_HISTORY_RETIREMENT) {check(uiVisible && closing==null); storage.batchPlans.confirmRetirement(review)}}
    suspend fun continueIncomingBatchRetirement(id:String):Boolean=withContext(Dispatchers.IO) {auditRecovery(PhoneActivityKind.RECEIVER_HISTORY_RETIREMENT) {check(uiVisible && closing==null); storage.batchPlans.continueRetirement(id)}}
    suspend fun resetClosedBatchHistory()=withContext(Dispatchers.IO) {auditRecovery(PhoneActivityKind.BATCH_HISTORY_RESET) {check(uiVisible && closing==null); storage.batchPlans.archiveHistory(closedOnly=true)}}
    suspend fun confirmOutgoingBatchHistory(review:PhoneBatchHistoryReview):Boolean=withContext(Dispatchers.IO) {
        val kind=when(review.action) {PhoneBatchHistoryAction.ARCHIVE->PhoneActivityKind.SENDER_HISTORY_ARCHIVE; PhoneBatchHistoryAction.RESTORE->PhoneActivityKind.SENDER_HISTORY_RESTORE; PhoneBatchHistoryAction.PURGE->PhoneActivityKind.SENDER_HISTORY_PURGE}
        auditRecovery(kind) {check(uiVisible && closing==null); outgoingBatchSender.confirmHistory(review)}
    }
    suspend fun continueOutgoingBatchPurge(id:String):Boolean=withContext(Dispatchers.IO) {
        auditRecovery(PhoneActivityKind.SENDER_HISTORY_PURGE) {check(uiVisible && closing==null); outgoingBatchSender.continuePurge(id)}
    }
    suspend fun reviewOutgoingBatchRemoval(id:String):PhoneBatchSourceRemoval=withContext(Dispatchers.IO) {synchronized(owner.gate) {
        check(uiVisible && closing==null && !outgoingBatchSender.sourceInUse(id)) {"Archive or finish the retained send before reviewing its private copies."}; outgoingBatches.reviewRemoval(id)
    }}
    suspend fun removeOutgoingBatchReviewed(review:PhoneBatchSourceRemoval)=withContext(Dispatchers.IO) {synchronized(owner.gate) {
        check(uiVisible && closing==null && !outgoingBatchSender.sourceInUse(review.plan)) {"The private source belongs to a current send. Pause and archive that job before removing its private copies."}; outgoingBatches.removeReviewed(review)
    }}
    fun loadUploadDestinations()=guarded {
        check(uiVisible && state.value.authenticated && !state.value.uploadDestinationsBusy && !state.value.savingTrust)
        val active=requireNotNull(session); val approved=requireNotNull(peerApproval?.takeIf(owner::isCurrent))
        mutable.update {it.copy(uploadDestinationsBusy=true,uploadDestination=null)}
        controllerScope.launch {try {
            val response=PhoneTransferManifests.checked(active::requestFiles,FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_CONTEXT).build())
            require(response.entriesList.all {it.isRoot && it.isDirectory && it.permissions and 12==12}) {"The receiver returned invalid incoming locations."}
            if(uiVisible && session===active && owner.isCurrent(approved)) mutable.update {it.copy(uploadDestinations=response.entriesList.toList())}
        } catch(e:Exception) {if(session===active) log(e.message?:"Incoming locations are unavailable.")}
        finally {if(session===active) mutable.update {it.copy(uploadDestinationsBusy=false)}} }
    }
    fun selectUploadDestination(id:String?)=guarded {check(uiVisible); val selected=id?.let {value->state.value.uploadDestinations.single {it.id==value}}; mutable.update {it.copy(uploadDestination=selected)} }
    fun useCreatedFolderForSending(id:String)=guarded {
        check(uiVisible && state.value.authenticated && !state.value.uploadDestinationsBusy && !state.value.savingTrust)
        val original=state.value.recentMutations.single {it.id==id && it.operation==FileOperation.CREATE_DIRECTORY && it.phase==MutationPhase.CHANGE_SUCCEEDED}
        val active=requireNotNull(session); val approved=requireNotNull(peerApproval?.takeIf(owner::isCurrent))
        mutable.update {it.copy(uploadDestinationsBusy=true)}
        controllerScope.launch {try {
            val context=PhoneTransferManifests.checked(active::requestFiles,FileRequest.newBuilder().setOperation(FileOperation.MUTATION_CONTEXT).build())
            val response=PhoneTransferManifests.checked(active::requestFiles,FileRequest.newBuilder().setOperation(FileOperation.RESOLVE_CREATED_DIRECTORY).setMutationId(id).setMutationEpoch(context.mutationEpoch).build())
            val roots=PhoneTransferManifests.checked(active::requestFiles,FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_CONTEXT).build())
            require(roots.entriesList.all {it.isRoot && it.isDirectory && it.permissions and 12==12}) {"The receiver returned invalid incoming locations."}
            val grant=roots.entriesList.singleOrNull {root->response.entriesCount==1 && root.grantId==response.entriesList[0].grantId && root.isRoot && root.permissions and 12==12}
                ?:error("This created folder is not in a currently approved receiving location.")
            val entry=CreatedDirectoryReferences.check(CreatedDirectoryReceipt(id,context.mutationEpoch,grant.grantId,original.requestedName),response)
            require(entry.permissions and 12==12) {"This folder no longer permits incoming copies."}
            if(uiVisible && session===active && owner.isCurrent(approved)) mutable.update {it.copy(uploadDestinations=roots.entriesList.toList()+entry,uploadDestination=entry,
                mutationMessage="Selected created folder for sending: ${grant.name} / ${entry.name}. Review the send queue before sending.")}
        } catch(e:Exception) {if(session===active) log(e.message?:"The created folder needs review.")}
        finally {if(session===active) mutable.update {it.copy(uploadDestinationsBusy=false)}} }
    }
    fun pauseUpload(id:String)=uploads.pause(id)
    fun renameUpload(id:String,name:String)=guarded {check(uiVisible); controllerScope.launch {try {withContext(Dispatchers.IO) {uploads.rename(id,name)}} catch(e:Exception) {log(e.message?:"The receiving filename could not be changed.")}}}
    fun anotherUpload(id:String)=guarded {check(uiVisible); controllerScope.launch {try {withContext(Dispatchers.IO) {uploads.anotherCopy(id)}} catch(e:Exception) {log(e.message?:"The copy could not be reset.")}}}
    fun removeUpload(id:String) {check(uiVisible); controllerScope.launch {try {withContext(Dispatchers.IO) {uploads.remove(id)}} catch(e:Exception) {log(e.message?:"The private copy was kept.")}; syncTransferRecovery()}}
    fun removeOrphanUploads() {check(uiVisible); controllerScope.launch {try {withContext(Dispatchers.IO) {uploads.removeOrphans()}} catch(e:Exception) {log(e.message?:"Some private copies were kept.")}; syncTransferRecovery()}}
    fun allowBackground(id: String) = guarded {
        authorizeBackground(download = id)
    }
    fun allowBackgroundSending(id: String) = guarded {
        authorizeBackground(source = id)
    }
    private fun authorizeBackground(download: String? = null, source: String? = null) {
        check(uiVisible && closing == null) { "Open iConnect to authorize background work." }
        if (download != null) check(state.value.downloads.any { it.id == download && it.active && it.batchId.isEmpty() }) { "Start or resume a single-file transfer first. Batches require the app to remain open." }
        val send = source?.let { id -> state.value.sends.single { it.id == id && it.active && !it.paused } }
        if (send != null) check(state.value.authenticated && downloadSources.isLive(Rules.id(requireNotNull(peerKey)), send.id, send.scope)) { "Ask the computer to resume this transfer while iConnect is open." }
        check(TransferService.notificationsAllowed(application)) { "Allow iConnect transfer notifications to continue in the background." }
        if (backgroundToken.isNotEmpty()) {
            if (send != null) authorizedSources = authorizedSources + (send.id to send.scope)
            mutable.update { it.copy(backgroundDownloads = if (download == null) it.backgroundDownloads else it.backgroundDownloads + download,
                backgroundSends = authorizedSources.keys) }; return
        }
        check(pendingBackgroundToken.isEmpty()) { "The transfer notification is starting. Try again shortly." }
        pendingBackgroundToken = StorageRules.token(); pendingBackgroundIds = if (download == null) emptySet() else setOf(download)
        pendingBackgroundSources = if (send == null) emptyMap() else mapOf(send.id to send.scope)
        val token = pendingBackgroundToken
        try { application.startForegroundService(android.content.Intent(application, TransferService::class.java).putExtra(TransferService.TOKEN, token)) }
        catch (e: Exception) { pendingBackgroundToken = ""; pendingBackgroundIds = emptySet(); pendingBackgroundSources = emptyMap(); throw e }
        controllerScope.launch { delay(10_000); if (pendingBackgroundToken == token) {
            pendingBackgroundToken = ""; pendingBackgroundIds = emptySet(); pendingBackgroundSources = emptyMap(); log("Background transfer could not start. Keep iConnect open and retry.")
        } }
    }
    internal fun acceptBackgroundStart(token: String): Boolean {
        if (!uiVisible || token.isEmpty() || token != pendingBackgroundToken) return false
        val selected = pendingBackgroundIds.intersect(state.value.downloads.filter { it.active }.map { it.id }.toSet())
        val peer = peerKey?.let(Rules::id) ?: ""
        val sending = pendingBackgroundSources.filter { (id, scope) -> downloadSources.isLive(peer, id, scope) }
        pendingBackgroundToken = ""; pendingBackgroundIds = emptySet(); pendingBackgroundSources = emptyMap()
        if (selected.isEmpty() && sending.isEmpty()) return false
        authorizedSources = sending; backgroundToken = token
        mutable.update { it.copy(backgroundDownloads = selected, backgroundSends = sending.keys, backgroundReady = true) }; syncCostMonitor(); return true
    }
    internal fun ownsBackground(token: String) = token.isNotEmpty() && token == backgroundToken
    internal fun backgroundStartFailed(token: String) {
        if (token != pendingBackgroundToken || token.isEmpty()) return
        pendingBackgroundToken = ""; pendingBackgroundIds = emptySet(); pendingBackgroundSources = emptyMap()
        log("Android declined background execution. Keep iConnect open and resume the transfer.")
    }
    internal fun backgroundStopped(token: String, reason: String) {
        if (ownsBackground(token)) stopBackground(reason)
    }
    fun stopBackground(reason: String = "Background transfers paused.") {
        val selected = state.value.backgroundDownloads
        val sending = authorizedSources.keys.toList()
        authorizedSources = emptyMap(); backgroundToken = ""; pendingBackgroundToken = ""; pendingBackgroundIds = emptySet(); pendingBackgroundSources = emptyMap()
        mutable.update { it.copy(backgroundReady = false, backgroundDownloads = emptySet(), backgroundSends = emptySet()) }
        syncCostMonitor()
        selected.forEach(downloads::pause)
        sending.forEach { pauseSourceNow(it) }
        application.stopService(android.content.Intent(application, TransferService::class.java))
        if (!uiVisible) disconnect(reason,PhoneConnectionEnd.PAUSED) else log(reason)
    }
    internal fun checkBackground() {
        val peer = peerKey?.let(Rules::id) ?: ""
        authorizedSources = authorizedSources.filter { (id, scope) -> downloadSources.isLive(peer, id, scope) }
        val receiving = state.value.backgroundDownloads.intersect(downloads.state.value.filter { it.active }.map { it.id }.toSet())
        mutable.update { it.copy(backgroundDownloads = receiving, backgroundSends = authorizedSources.keys) }
        if (backgroundToken.isNotEmpty() && receiving.isEmpty() && authorizedSources.isEmpty()) stopBackground("The selected transfers finished or stopped.")
    }
    private fun allowFileRequest(peer: String, request: FileRequest): Boolean {
        if (peerApproval?.let { it.id == peer && owner.isCurrent(it) } != true) return false
        if((UploadRules.isRpc(request.operation) || UploadBatchRules.isRpc(request.operation) || MutationRules.isChange(request.operation) || request.operation==FileOperation.RESOLVE_CREATED_DIRECTORY) && state.value.savingShare) return false
        if (request.operation == FileOperation.RELEASE_DOWNLOAD) return uiVisible || downloadSources.canUse(peer, request.transferId, includePaused = true)
        if (request.operation in setOf(FileOperation.DOWNLOAD_STATUS, FileOperation.DOWNLOAD_READ)) {
            if (!downloadSources.canUse(peer, request.transferId)) return false
            if (uiVisible) return true
            val scope = authorizedSources[request.transferId] ?: return false
            return state.value.backgroundReady && downloadSources.isLive(peer, request.transferId, scope)
        }
        return uiVisible
    }
    private fun syncSends(rows: List<PhoneSend>) {
        val peer = peerKey?.let(Rules::id)
        mutable.update { it.copy(sends = rows.filter { source -> source.peer == peer }) }
    }
    suspend fun stopPreparedSource(id:String) {
        check(uiVisible && closing==null && owner.healthy && id !in state.value.changingSends)
        val current=sourceReviewEpoch; downloadSources.block(id); authorizedSources=authorizedSources-id; session?.interruptSource(id)
        mutable.update {it.copy(backgroundSends=authorizedSources.keys,changingSends=it.changingSends+id)}
        try {downloadSources.stopOwned(id) {uiVisible && closing==null && owner.healthy && current==sourceReviewEpoch}}
        finally {mutable.update {it.copy(changingSends=it.changingSends-id)}}
    }
    suspend fun removeExpiredPreparedSources():Int {
        check(uiVisible && closing==null && owner.healthy)
        val current=sourceReviewEpoch; val ids=state.value.preparedSources.filter {it.expired}.map {it.id}.toSet()
        authorizedSources=authorizedSources.filterKeys {it !in ids}; ids.forEach {session?.interruptSource(it)}
        mutable.update {it.copy(backgroundSends=authorizedSources.keys)}
        return downloadSources.removeExpiredOwned {uiVisible && closing==null && owner.healthy && current==sourceReviewEpoch}
    }
    fun pauseSending(id: String) = guarded { check(uiVisible); check(id !in state.value.changingSends); pauseSourceNow(id) }
    private fun pauseSourceNow(id: String) {
        val revision = runCatching { downloadSources.block(id) }.getOrNull() ?: return
        authorizedSources = authorizedSources - id
        mutable.update { it.copy(backgroundSends = authorizedSources.keys) }
        session?.interruptSource(id)
        sourceChange(id) { downloadSources.persistPause(id, revision) }
    }
    fun resumeSending(id: String) = guarded {
        check(uiVisible && id !in state.value.changingSends)
        val peer = Rules.id(requireNotNull(peerKey)); val revision = downloadSources.reserveResume(id)
        sourceChange(id) { downloadSources.resume(id, peer, revision) }
    }
    fun removeSending(id: String) = guarded {
        check(uiVisible && id !in state.value.changingSends)
        val peer = Rules.id(requireNotNull(peerKey)); downloadSources.block(id)
        authorizedSources = authorizedSources - id; session?.interruptSource(id)
        sourceChange(id) { downloadSources.release(peer, id) }
    }
    private fun sourceChange(id: String, change: () -> Unit) {
        mutable.update { it.copy(changingSends = it.changingSends + id) }
        sourcePersistence.launch {
            try { change() } catch (e: Exception) { controllerScope.launch { log(when (e) {
                is StorageFailure -> "The phone folder is no longer approved for this computer. Review its sharing permission before resuming."
                is IllegalStateException, is IllegalArgumentException -> "This sending record changed or expired. Retry, or start the download again on the computer."
                else -> "The sending transfer change could not be saved. Keep iConnect open and retry."
            }) } }
            finally { controllerScope.launch { mutable.update { it.copy(changingSends = it.changingSends - id) } } }
        }
    }
    private fun cancelPairAttempt() {
        val attempt = pairAttempt ?: return
        pairAttempt = null; persistRevocations(setOf(owner.cancelPair(attempt)))
    }
    private fun persistRevocations(items: Set<RevokedApproval>) {
        pendingRevocations = pendingRevocations + items
        if (pendingRevocations.isEmpty()) return
        val captured = pendingRevocations; revocationWrites++
        mutable.update { it.copy(savingTrust = true, revocationPending = true) }
        sourcePersistence.launch {
            val result = runCatching { trustPersistence.withLock { owner.revoke(captured) } }
            val accessCleanup = runCatching { storage.reconcileAccess() }
            withContext(Dispatchers.Main.immediate) {
                revocationWrites--
                if (result.getOrDefault(false)) pendingRevocations = pendingRevocations - captured
                recordActivity(PhoneActivityKind.REVOCATION,if(result.getOrDefault(false)) PhoneActivityOutcome.COMPLETED else PhoneActivityOutcome.FAILED)
                mutable.update { it.copy(savingTrust = revocationWrites > 0, revocationPending = pendingRevocations.isNotEmpty(), phoneShares = storage.all()) }
                log(if (result.isFailure) "Revocation could not be saved. Access is blocked in this run. Free storage space and retry before closing iConnect."
                    else if (!result.getOrDefault(false)) "Pairing approval revoked durably. Retry to finish inactive policy cleanup."
                    else if (accessCleanup.isFailure) "Pairing approval revoked. Some unused system folder access could not be released."
                    else "Pairing approval revoked. Pair again before future access.")
            }
        }
    }
    fun retryRevocation() = guarded { check(uiVisible && !state.value.savingTrust); persistRevocations(emptySet()) }
    fun resetPairingData() {
        if (!uiVisible || state.value.savingTrust) return
        history.cancelBackupReview()
        forgetPeer(); revocationWrites++; mutable.update { it.copy(savingTrust = true) }
        sourcePersistence.launch {
            val reset = runCatching { trustPersistence.withLock { owner.reset() } }
            runCatching { storage.reconcileAccess() }
            withContext(Dispatchers.Main.immediate) {
                revocationWrites--
                if (reset.isSuccess) { pendingRevocations = emptySet(); peerApproval = null; peerKey = null }
                mutable.update { it.copy(savingTrust = revocationWrites > 0, trustIssue = owner.issue,
                    trustNotice = owner.notice, phoneShares = storage.all(), revocationPending = pendingRevocations.isNotEmpty(), peerName = "") }
                log(if (reset.isSuccess) "Pairing data reset. Phone files and local downloads were kept." else "Pairing data could not be reset. Free storage space and retry.")
            }
        }
    }
}
