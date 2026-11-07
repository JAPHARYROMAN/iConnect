package com.iconnect.proof

import com.iconnect.protocol.PresenceUpdate
import com.iconnect.protocol.PresenceSnapshot
import java.security.SecureRandom

/** Coordinator observations only; never used by owner policy or direct authentication. Caller serializes access. */
internal class PresenceTracker(private val ownId:String,private val now:()->Long) {
    companion object {const val CAPABILITY="presence-v1"; const val MAX_PEERS=128; const val POLL_MS=10_000L; const val REPORT_MS=25_000L}
    private var pending:PresenceUpdate?=null
    private var sentAt=0L
    private var reportedAt:Long?=null
    private var available=emptySet<String>()
    fun next(enabled:Boolean,peerIds:List<String>):PresenceUpdate? {
        val ids=if(enabled) peerIds.sorted() else emptyList()
        require(ids.size<=MAX_PEERS && ids.distinct().size==ids.size && ids.all {it!=ownId && it.matches(Regex("[a-f0-9]{64}"))})
        val changed=pending?.let {it.enabled!=enabled || it.peerIdsList!=ids}?:true
        if(!changed && (!enabled || now()-sentAt<POLL_MS)) return null
        val update=PresenceUpdate.newBuilder().setRequestId(Rules.b64(ByteArray(32).also {SecureRandom().nextBytes(it)}))
            .setEnabled(enabled).addAllPeerIds(ids).build()
        if(changed) available=emptySet()
        pending=update; sentAt=now(); return update
    }
    fun accept(snapshot:PresenceSnapshot) {
        require(Rules.tokenPattern.matches(snapshot.requestId) && snapshot.peersCount<=MAX_PEERS)
        val request=pending?:return
        if(snapshot.requestId!=request.requestId) return
        require(snapshot.peersList.map {it.peerId}==request.peerIdsList) {"Availability report changed its scope."}
        if(now()-sentAt>=REPORT_MS) {available=emptySet(); return}
        available=if(request.enabled) snapshot.peersList.filter {it.available}.map {it.peerId}.toSet() else emptySet()
        reportedAt=sentAt
    }
    fun availablePeers():Set<String> = if(reportedAt?.let {now()-it<REPORT_MS}==true) available.toSet() else emptySet()
    fun clear() {pending=null; reportedAt=null; available=emptySet()}
}
