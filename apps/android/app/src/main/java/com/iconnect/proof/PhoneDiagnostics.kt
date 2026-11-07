package com.iconnect.proof

import android.content.Context
import android.net.Uri
import android.os.Build
import android.os.ParcelFileDescriptor
import android.os.SystemClock
import android.provider.DocumentsContract
import android.system.Os
import android.system.OsConstants
import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.io.IOException
import java.util.Locale

data class PhoneDiagnosticReview(val id:String,val json:String) {
    val filename get()="iConnect-diagnostics-$id.json"
}

/** Freshly constructed fields only. Never serializes a state object or raw log. */
internal object PhoneDiagnosticSnapshot {
    const val MAX_BYTES=32*1024
    private fun <T> phases(rows:List<T>,phase:(T)->Enum<*>,active:((T)->Boolean)?=null):JSONObject {
        val counts=JSONObject()
        rows.groupingBy {phase(it).name.lowercase(Locale.ROOT)}.eachCount().toSortedMap().forEach {(name,count)->counts.put(name,count)}
        return JSONObject().put("total",rows.size).put("active",active?.let {rows.count(it)}?:JSONObject.NULL).put("phases",counts)
    }
    fun create(state:ProofState,ownerHealthy:Boolean,now:Long=System.currentTimeMillis()):String {
        require(now>=0)
        val recovery=JSONObject()
        val areas=mapOf("Files" to "files","Batches" to "folderDownloads","Sending" to "sourceTickets","File operations" to "fileOperations",
            "Incoming copies" to "incoming","Batch plans" to "incomingBatches","Uploads" to "privateSends","Private batches" to "privateBatches","Batch sending" to "batchSends")
        areas.forEach {(area,key)->recovery.put(key,state.transferRecovery.count {it.area==area})}
        recovery.put("otherAreas",state.transferRecovery.count {it.area !in areas})
        val work=JSONObject()
            .put("downloads",phases(state.downloads,{it.phase},{it.active}))
            .put("folderDownloads",phases(state.batches,{it.phase}))
            .put("privateSends",phases(state.uploads,{it.phase},{it.active}))
            .put("privateBatches",phases(state.privateOutgoingBatches,{it.phase},{it.active}))
            .put("batchSends",phases(state.outgoingBatchSends,{it.phase},{it.active}))
            .put("incoming",phases(state.incomingCopies,{it.phase},{it.active}))
            .put("incomingBatches",phases(state.incomingBatchPlans,{it.phase}))
            .put("sourceTickets",phases(state.sends,{it.state},{it.active}))
            .put("backgroundReceives",state.backgroundDownloads.size).put("backgroundSends",state.backgroundSends.size)
        return JSONObject().put("schema",1).put("product","iConnect").put("platform","android")
            .put("appVersion",BuildConfig.VERSION_NAME).put("androidApi",Build.VERSION.SDK_INT)
            .put("generatedAtUnixMs",now).put("bestEffortSnapshot",true)
            .put("connection",JSONObject().put("coordinator",when {state.connected->"connected"; state.connecting->"connecting"; else->"disconnected"})
                .put("direct",when {state.authenticated->"authenticated"; state.peerConnecting->"connecting"; else->"disconnected"})
                .put("coordinatorConfigured",state.server.isNotBlank()).put("stunConfigured",state.stun.isNotBlank()))
            .put("policy",JSONObject().put("ownerHealthy",ownerHealthy).put("pairingSaved",state.peerName.isNotEmpty())
                .put("approvedLocations",state.phoneShares.size).put("revocationPending",state.revocationPending))
            .put("availability",JSONObject().put("reconnectEnabled",state.autoReconnect).put("reconnectPaused",state.reconnectPaused)
                .put("mutualPresenceEnabled",state.sharePresence).put("backgroundTransferSelected",state.backgroundReady))
            .put("work",work).put("recovery",recovery)
            .put("localActivity",JSONObject().put("retainedEntries",state.activity.entries.size).put("retentionDays",state.activity.days).put("needsReview",state.activity.issue.isNotEmpty()).put("droppedThisRun",state.activity.dropped).put("entriesIncluded",false))
            .toString(2).also {check(it.toByteArray(Charsets.UTF_8).size<=MAX_BYTES)}
    }
}

/** A current-process, single-use review; a system document picker may background the app. */
internal class PhoneDiagnostics(private val context:Context,private val scope:()->String,private val open:()->Boolean,
    private val elapsed:()->Long=SystemClock::elapsedRealtime) {
    private data class Pending(val value:PhoneDiagnosticReview,val scope:String,val expires:Long)
    private val gate=Any()
    private var pending:Pending?=null
    fun review(state:ProofState,ownerHealthy:Boolean):PhoneDiagnosticReview=synchronized(gate) {
        check(open()) {"Reopen iConnect before preparing diagnostics."}
        val value=PhoneDiagnosticReview(StorageRules.token(),PhoneDiagnosticSnapshot.create(state,ownerHealthy))
        pending=Pending(value,scope(),elapsed()+120_000); value
    }
    fun cancel(id:String) {synchronized(gate) {if(pending?.value?.id==id) pending=null}}
    fun invalidate() {synchronized(gate) {pending=null}}
    fun export(review:PhoneDiagnosticReview,uri:Uri) {
        val bytes=synchronized(gate) {
            val captured=pending; pending=null
            check(open() && captured!=null && captured.value==review && captured.scope==scope() && captured.expires>=elapsed()) {
                "This diagnostic review changed, expired or was already used. Prepare a fresh report."
            }
            review.json.toByteArray(Charsets.UTF_8).also {check(it.size<=PhoneDiagnosticSnapshot.MAX_BYTES)}
        }
        require(uri.scheme=="content" && DocumentsContract.isDocumentUri(context,uri)) {"Choose a new document using the system picker."}
        try {
            val descriptor=requireNotNull(context.contentResolver.openFileDescriptor(uri,"rw"))
            try {
                val stat=Os.fstat(descriptor.fileDescriptor)
                check(OsConstants.S_ISREG(stat.st_mode) && stat.st_nlink==1L && stat.st_size==0L) {"Choose a new empty report document. Existing bytes were kept."}
                ParcelFileDescriptor.AutoCloseOutputStream(descriptor).use {output->
                    output.write(bytes); output.flush(); output.fd.sync()
                }
            } finally {descriptor.close()}
            val actual=context.contentResolver.openInputStream(uri)?.use {input->
                val result=ByteArrayOutputStream(); val buffer=ByteArray(4096)
                while(true) {val read=input.read(buffer,0,minOf(buffer.size,PhoneDiagnosticSnapshot.MAX_BYTES+1-result.size())); if(read<0) break
                    check(read>0 && result.size()+read<=PhoneDiagnosticSnapshot.MAX_BYTES); result.write(buffer,0,read)}
                result.toByteArray()
            }
            check(actual!=null && bytes.contentEquals(actual))
        } catch(_:Exception) {
            throw IOException("The diagnostic report could not be verified. A provider copy may remain; choose a new empty report document to retry.")
        }
    }
}
