package com.iconnect.proof

internal data class PhoneLocalPayloadView(val bytes:Map<String,Long> = emptyMap(),val estimates:Map<String,PhonePayloadRateEstimate> = emptyMap())

/** Display-only observations. No durable offset, provider access or transfer authority is changed here. */
internal class PhoneLocalPayloadRates {
    private val rates=PhonePayloadRateTracker()
    fun refresh(rows:List<PhoneDownload>,points:Map<String,PhonePayloadProgress>,now:Long):PhoneLocalPayloadView {
        val active=rows.mapNotNull {row->points[row.id]?.takeIf {p->row.active && row.phase==PhoneDownloadPhase.DOWNLOADING &&
            row.peer.isEmpty() && p.peer.isEmpty() && p.id==row.id && p.ticket==row.ticket && p.version==row.version && p.size==row.size && p.received in 0..row.size
        }?.let {row to it}}
        rates.retain(active.map {it.first.id}.toSet())
        val estimates=active.associate {(row,p)->
            val scope="${p.ticket}/${p.version}/${p.run}"
            rates.observe(row.id,scope,row.size,p.received,true,p.at)
            row.id to rates.read(row.id,scope,row.size,maxOf(row.offset,p.received),true,now)
        }
        return PhoneLocalPayloadView(active.associate {it.first.id to maxOf(it.first.offset,it.second.received)},estimates)
    }
    fun clear()=rates.clear()
}

internal class PhoneUploadPayloadRates {
    private val rates=PhonePayloadRateTracker()
    fun refresh(rows:List<PhoneUpload>,points:Map<String,PhoneUploadPayloadProgress>,connection:String,limit:Long,now:Long):Map<String,PhonePayloadRateEstimate> {
        val active=rows.mapNotNull {row->points[row.id]?.takeIf {p->row.active && row.phase==PhoneUploadPhase.SENDING &&
            p.id==row.id && p.receiver==row.remote && p.epoch==row.remoteEpoch && p.size==row.size && p.offset in 0..row.size
        }?.let {row to it}}
        rates.retain(active.map {it.first.id}.toSet())
        return active.associate {(row,p)->
            val scope="${p.run}/$connection/$limit"
            rates.observe(row.id,scope,row.size,p.offset,true,p.at)
            row.id to rates.read(row.id,scope,row.size,maxOf(row.offset,p.offset),true,now,limit)
        }
    }
    fun clear()=rates.clear()
}

internal fun localPayloadText(row:PhoneDownload,estimate:PhonePayloadRateEstimate?)=when(row.phase) {
    PhoneDownloadPhase.PREPARING->"Checking the selected local source and destination. Payload time remaining is unavailable."
    PhoneDownloadPhase.VERIFYING->"Payload copied. Verifying private bytes; completion time is unavailable."
    PhoneDownloadPhase.READY_TO_SAVE->"Private payload verified. Saving to the chosen provider is separate; completion time is unavailable."
    PhoneDownloadPhase.PUBLISHING->"Saving and checking the provider copy. Completion time is unavailable."
    PhoneDownloadPhase.PUBLICATION_INTERRUPTED->"Provider saving needs review. Completion time is unavailable."
    PhoneDownloadPhase.COMPLETED->"The verified provider copy is saved."
    else->(estimate?:PhonePayloadRateEstimate(if(row.offset==row.size && row.sha256.isNotEmpty()) PhonePayloadRateState.RECEIVED else PhonePayloadRateState.STOPPED)).describe(PhonePayloadKind.LOCAL_COPY)
}

internal fun uploadPayloadText(row:PhoneUpload,estimate:PhonePayloadRateEstimate?,limited:Boolean)=when(row.phase) {
    PhoneUploadPhase.STAGING->"Preparing a private snapshot. Payload time remaining is available after sending starts."
    PhoneUploadPhase.READY->"Ready for explicit sending. No recent send rate is available."
    PhoneUploadPhase.COMPLETED->"The receiver confirmed a verified copy."
    PhoneUploadPhase.VERIFYING->"Payload acknowledged. The receiver is verifying or saving; completion time is unavailable."
    PhoneUploadPhase.SENDING->when {
        row.offset==row.size && row.remote.isNotEmpty()->"Payload acknowledged. Waiting for the receiver to verify and save; completion time is unavailable."
        estimate!=null->estimate.describe(PhonePayloadKind.SENDING,limited)
        else->"Checking the private source or receiver receipt. Payload time remaining is unavailable."
    }
    else->PhonePayloadRateEstimate(PhonePayloadRateState.STOPPED).describe(PhonePayloadKind.SENDING)
}
