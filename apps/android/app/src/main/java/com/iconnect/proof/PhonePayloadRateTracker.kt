package com.iconnect.proof

enum class PhonePayloadRateState { MEASURING, AVAILABLE, WAITING, STOPPED, RECEIVED }
internal data class PhonePayloadProgress(val id:String,val peer:String,val ticket:String,val version:String,val size:Long,val received:Long,val at:Long,val run:String="")
enum class PhonePayloadKind { SENDING, LOCAL_COPY, BATCH_SENDING }
data class PhonePayloadRateEstimate(val state:PhonePayloadRateState,val bytesPerSecond:Long?=null,val remainingSeconds:Long?=null) {
    fun describe(kind:PhonePayloadKind,configuredLimit:Boolean=false):String {
        val sending = kind != PhonePayloadKind.LOCAL_COPY
        val label = when (kind) {
            PhonePayloadKind.SENDING -> "acknowledged send"
            PhonePayloadKind.LOCAL_COPY -> "local copy"
            PhonePayloadKind.BATCH_SENDING -> "batch acknowledgement"
        }
        val later=if(sending) "Receiver verification and saving can take longer." else "Verification and saving can take longer."
        return when(state) {
            PhonePayloadRateState.RECEIVED->(if(sending) "Payload acknowledged. " else "Payload copied. ")+later
            PhonePayloadRateState.STOPPED->"Recent $label rate is unavailable while payload copying is stopped."
            PhonePayloadRateState.WAITING->"Waiting for fresh $label progress. Time remaining is unavailable."
            else->if(bytesPerSecond!=null && remainingSeconds!=null)
                "Recent $label progress: ${rate(bytesPerSecond)}. About ${duration(remainingSeconds)} of payload remains at this rate${if(configuredLimit && sending) " and the configured limit" else ""}. $later"
            else "Measuring recent $label progress…"
        }
    }
    val text get() = receivingText(true)
    fun receivingText(configuredLimit: Boolean) = when(state) {
        PhonePayloadRateState.RECEIVED->"Payload received. Verification and saving may still take time."
        PhonePayloadRateState.STOPPED->"Recent payload rate is unavailable while this file is stopped."
        PhonePayloadRateState.WAITING->"Waiting for fresh payload progress. Time remaining is unavailable."
        else->if(bytesPerSecond!=null && remainingSeconds!=null) "Recent payload progress: ${rate(bytesPerSecond)}. About ${duration(remainingSeconds)} of payload remains at this rate${if(configuredLimit) " and the configured limit" else ""}. Verification and saving can take longer."
            else "Measuring recent payload progress…"
    }
    fun approvalWarning(sourceSeconds:Long?)=if(remainingSeconds!=null && sourceSeconds!=null && remainingSeconds>=sourceSeconds)
        "The recent payload estimate reaches source expiry. Review the limit or a fresh source; approval is not extended automatically." else ""
    fun describeStage() = when {
        state == PhonePayloadRateState.RECEIVED -> "All bytes processed. Checking the result."
        state == PhonePayloadRateState.WAITING -> "Waiting for fresh progress. Time remaining is unavailable."
        bytesPerSecond != null && remainingSeconds != null ->
            "Recent rate ${rate(bytesPerSecond)}. About ${duration(remainingSeconds)} for the remaining bytes in this step."
        else -> "Measuring recent progress…"
    }
    private fun rate(bytes:Long)=when {
        bytes>=1L.shl(30)->"%.1f GiB/s".format(bytes/1L.shl(30).toDouble())
        bytes>=1.shl(20)->"%.1f MiB/s".format(bytes/1.shl(20).toDouble())
        bytes>=1024->"%.1f KiB/s".format(bytes/1024.0)
        else->"$bytes B/s"
    }
    private fun duration(seconds:Long)=when {
        seconds>=86400->"${seconds/86400} d ${seconds/3600%24} h"
        seconds>=3600->"${seconds/3600} h ${seconds/60%60} min"
        seconds>=60->"${seconds/60} min ${seconds%60} s"
        else->"$seconds s"
    }
}

/** Memory-only payload observations, independent of durable checkpoints and transfer authority. */
class PhonePayloadRateTracker {
    private data class Point(val time:Long,val offset:Long)
    private class Row(val scope:String,val size:Long,var offset:Long,var lastClock:Long) {
        var lastProgress=lastClock; var waiting=false
        val points=mutableListOf(Point(lastClock,offset))
    }
    private val gate=Any(); private val rows=mutableMapOf<String,Row>()
    val count get()=synchronized(gate) {rows.size}
    private fun validate(id:String,scope:String,size:Long,offset:Long,now:Long) {
        require(id.length in 1..128 && scope.length in 1..512 && size in 0..StorageRules.MAX_TRANSFER_SIZE && offset in 0..size && now>=0)
    }
    private fun reset(id:String,scope:String,size:Long,offset:Long,now:Long):Row {
        if(id !in rows && rows.size>=128) rows.remove(rows.minBy {it.value.lastClock}.key)
        return Row(scope,size,offset,now).also {rows[id]=it}
    }
    fun observe(id:String,scope:String,size:Long,offset:Long,receiving:Boolean,now:Long) {
        validate(id,scope,size,offset,now)
        synchronized(gate) {
            if(!receiving || offset==size) {rows.remove(id); return}
            val row=rows[id]
            if(row==null || row.scope!=scope || row.size!=size || offset<row.offset || now<row.lastClock) {reset(id,scope,size,offset,now); return}
            row.lastClock=now
            if(offset==row.offset) return
            if(row.waiting || now-row.lastProgress>10000) {reset(id,scope,size,offset,now); return}
            row.offset=offset; row.lastProgress=now
            if(now-row.points.last().time>=250) row.points.add(Point(now,offset))
            while(row.points.size>2 && row.points[1].time<=now-20000) row.points.removeAt(0)
            while(row.points.size>96) row.points.removeAt(0)
        }
    }
    fun read(id:String,scope:String,size:Long,offset:Long,receiving:Boolean,now:Long,limit:Long=0,remainingPayload:Long?=null):PhonePayloadRateEstimate {
        validate(id,scope,size,offset,now); PhoneNetworkPreferences(limit).validate()
        require(remainingPayload == null || remainingPayload in 0..size)
        synchronized(gate) {
            if(offset==size) {rows.remove(id); return PhonePayloadRateEstimate(PhonePayloadRateState.RECEIVED)}
            if(!receiving) {rows.remove(id); return PhonePayloadRateEstimate(PhonePayloadRateState.STOPPED)}
            val row=rows[id]
            if(row==null || row.scope!=scope || row.size!=size || offset<row.offset || now<row.lastClock) {reset(id,scope,size,offset,now); return PhonePayloadRateEstimate(PhonePayloadRateState.MEASURING)}
            if(now-row.lastProgress>10000) {row.waiting=true; row.points.clear(); return PhonePayloadRateEstimate(PhonePayloadRateState.WAITING)}
            if(row.waiting || row.points.size<2) return PhonePayloadRateEstimate(PhonePayloadRateState.MEASURING)
            val first=row.points.first(); val last=row.points.last(); val elapsed=last.time-first.time; val bytes=last.offset-first.offset
            if(elapsed<2000 || bytes<=0) return PhonePayloadRateEstimate(PhonePayloadRateState.MEASURING)
            val rate=bytes*1000/elapsed
            if(rate==0L) return PhonePayloadRateEstimate(PhonePayloadRateState.MEASURING)
            val expected=if(limit==0L) rate else minOf(rate,limit); val remaining=remainingPayload ?: (size-offset)
            return PhonePayloadRateEstimate(PhonePayloadRateState.AVAILABLE,rate,(remaining+expected-1)/expected)
        }
    }
    fun retain(ids:Set<String>)=synchronized(gate) {rows.keys.retainAll(ids); Unit}
    fun clear()=synchronized(gate) {rows.clear()}
}
