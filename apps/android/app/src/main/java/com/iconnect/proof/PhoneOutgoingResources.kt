package com.iconnect.proof

import java.io.IOException
import java.util.concurrent.Semaphore
import java.util.concurrent.atomic.AtomicBoolean

/** Shared private outgoing inventory and payload workers for one app profile. */
internal class PhoneOutgoingResources(private val kinds:Set<String> = setOf(SINGLE,BATCH)) {
    internal data class Reservation(val id:String,val peer:String,val bytes:Long,val pending:Long)
    private class Store(var owner:Any?=null,var loaded:Boolean=false,var uncertain:Boolean=false,var rows:Map<String,Reservation> = emptyMap(),var retainedBytes:Long=0)
    private val gate=Any()
    private val stores=kinds.associateWith {Store()}
    private val workers=Semaphore(2)
    private var profile:String?=null
    val reservedBytes get()=synchronized(gate) {stores.values.sumOf {it.rows.values.sumOf {row->row.bytes}+it.retainedBytes}}
    // Unreadable preserved ownership cannot safely attribute its bytes to a peer.
    fun reservedForPeer(peer:String)=synchronized(gate) {stores.values.sumOf {it.rows.values.filter {row->row.peer==peer}.sumOf {row->row.bytes}+it.retainedBytes}}
    val activeWorkers get()=2-workers.availablePermits()
    val inventoryReady get()=synchronized(gate) {stores.values.all {it.loaded && !it.uncertain}}
    fun open(kind:String,location:String):Participant=synchronized(gate) {
        require(kind in stores && (profile==null || profile==location)) {"Outgoing resource accounting belongs to another profile."}
        val store=stores.getValue(kind); check(store.owner==null) {"This outgoing inventory is already open."}
        profile=location; val token=Any(); store.owner=token; store.loaded=false; Participant(kind,token)
    }
    inner class Participant internal constructor(private val kind:String,private val token:Any):AutoCloseable {
        private fun owned()=stores.getValue(kind).also {check(it.owner===token) {"Outgoing inventory is closed."}}
        private fun validate(values:Collection<Reservation>):Map<String,Reservation> {
            require(values.size<=UploadBatchRules.MAX_PLANS)
            val result=linkedMapOf<String,Reservation>()
            values.forEach {row->require(UploadBatchRules.canonicalToken(row.id) && row.peer.matches(Regex("[a-f0-9]{64}")) && row.bytes in 0..UploadRules.QUOTA && row.pending in 0..row.bytes && result.put(row.id,row)==null)}
            return result
        }
        fun recover(values:Collection<Reservation>,uncertain:Boolean,retainedBytes:Long=0) {
            require(retainedBytes>=0)
            val rows=validate(values); synchronized(gate) {owned().also {it.rows=rows; it.loaded=true; it.uncertain=uncertain; it.retainedBytes=retainedBytes}}
        }
        fun replace(values:Collection<Reservation>,availableSpace:(()->Long)?=null) {
            val rows=validate(values)
            synchronized(gate) {
                val store=owned()
                val growth=rows.values.any {row->store.rows[row.id].let {old->old==null || row.peer!=old.peer || row.bytes>old.bytes || row.pending>old.pending}}
                if(growth) {
                    if(stores.values.any {!it.loaded || it.uncertain}) throw IOException("Review private outgoing inventories before preparing another copy.")
                    val all=stores.filterKeys {it!=kind}.values.flatMap {it.rows.values}+rows.values
                    val retained=stores.values.sumOf {it.retainedBytes}
                    if(all.sumOf {it.bytes}+retained>UploadRules.QUOTA*4 || all.groupBy {it.peer}.values.any {group->group.sumOf {it.bytes}+retained>UploadRules.QUOTA})
                        throw IOException("Shared private outgoing byte reservations are full. Review retained file or folder copies.")
                    if(availableSpace!=null && rows.values.any {it.pending>(store.rows[it.id]?.pending?:0L)} &&
                        all.sumOf {it.pending}>(availableSpace()-64L*1024*1024).coerceAtLeast(0))
                        throw IOException("Not enough private storage for the shared outgoing reservations.")
                }
                store.rows=rows
            }
        }
        fun uncertain()=synchronized(gate) {owned().uncertain=true}
        fun otherPending(id:String?=null)=synchronized(gate) {owned(); stores.entries.sumOf {(name,store)->store.rows.values.filter {name!=kind || it.id!=id}.sumOf {it.pending}}}
        fun tryWorker():AutoCloseable?=synchronized(gate) {owned(); this@PhoneOutgoingResources.tryWorker()}
        override fun close()=synchronized(gate) {
            val store=stores.getValue(kind); if(store.owner===token) {store.owner=null; store.loaded=false}
        }
    }
    fun tryWorker():AutoCloseable? {
        if(!workers.tryAcquire()) return null
        val released=AtomicBoolean(false)
        return AutoCloseable {if(released.compareAndSet(false,true)) workers.release()}
    }
    companion object {const val SINGLE="single"; const val BATCH="batch"; fun isolated(kind:String)=PhoneOutgoingResources(setOf(kind))}
}
