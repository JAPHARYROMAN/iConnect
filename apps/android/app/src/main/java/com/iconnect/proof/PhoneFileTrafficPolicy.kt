package com.iconnect.proof

import com.iconnect.protocol.FileOperation
import com.iconnect.protocol.FileRequest
import com.iconnect.protocol.FileStatus
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlin.math.ceil
import kotlin.math.min

enum class PhoneNetworkCost { UNKNOWN, UNRESTRICTED, METERED }
data class PhoneNetworkPreferences(val bytesPerSecond:Long=0,val pauseOnMetered:Boolean=false) {
    fun validate() {require(bytesPerSecond==0L || bytesPerSecond in 65536L..67108864L) {"Choose unlimited or a limit between 64 KiB/s and 64 MiB/s."}}
}

/** Owner-wide payload pacing. It does not measure OS traffic or extend a storage approval. */
class PhoneFileTrafficPolicy(private val elapsed:()->Long={System.nanoTime()/1_000_000},private val wait:suspend(Long)->Unit={delay(it)}) {
    private val gate=Any()
    private val sending=Mutex(); private val receiving=Mutex()
    private var preferences=PhoneNetworkPreferences()
    private var healthy=true
    private var cost=PhoneNetworkCost.UNKNOWN
    private var observed:Long?=null
    fun configure(value:PhoneNetworkPreferences,healthy:Boolean=true) {value.validate(); synchronized(gate) {preferences=value; this.healthy=healthy}}
    fun observe(value:PhoneNetworkCost)=synchronized(gate) {cost=value; observed=elapsed()}
    val networkCost get()=synchronized(gate) {currentCost()}
    private fun currentCost()=if(observed==null || elapsed()-observed!!>5000) PhoneNetworkCost.UNKNOWN else cost
    private fun limit():Long=synchronized(gate) {
        if(!healthy) throw StorageFailure(FileStatus.ACCESS_DENIED,"Transfer network settings need review on this phone. Remote content is paused; local files were kept.")
        if(preferences.pauseOnMetered && currentCost()!=PhoneNetworkCost.UNRESTRICTED) throw StorageFailure(FileStatus.ACCESS_DENIED,
            "Remote content is paused on a metered or unknown network. Review Transfer network controls, then explicitly resume interrupted work.")
        preferences.bytesPerSecond
    }
    fun requireContent() {limit()}
    fun requireRequest(request:FileRequest) {if(contentOperation(request.operation) || !request.content.isEmpty) requireContent()}
    suspend fun beforeRequest(request:FileRequest,outgoing:Boolean) {
        currentCoroutineContext().ensureActive()
        if(request.content.size()>StorageRules.RANGE) throw StorageFailure(FileStatus.INVALID_REQUEST,"The content payload exceeds its range limit.")
        val reading=request.operation==FileOperation.READ_RANGE || request.operation==FileOperation.DOWNLOAD_READ
        if(reading && request.length !in 0..StorageRules.RANGE) throw StorageFailure(FileStatus.INVALID_REQUEST,"Choose a bounded content range.")
        requireRequest(request)
        if(!request.content.isEmpty) pace(request.content.size(),outgoing)
        if(reading && request.length!=0) pace(request.length,!outgoing)
        requireRequest(request)
    }
    suspend fun pace(bytes:Int,outgoing:Boolean) {
        require(bytes in 0..StorageRules.RANGE)
        if(bytes==0) {requireContent(); return}
        (if(outgoing) sending else receiving).withLock {
            var remaining=bytes.toDouble()
            while(remaining>0) {
                currentCoroutineContext().ensureActive(); val rate=limit(); if(rate==0L) return@withLock
                val duration=maxOf(1L,ceil(min(100.0,remaining*1000/rate)).toLong()); val start=elapsed()
                wait(duration)
                val current=limit(); if(current==0L) return@withLock
                remaining-=maxOf(0,elapsed()-start)*min(rate,current)/1000.0
            }
            requireContent()
        }
    }
    companion object {
        fun contentOperation(operation:FileOperation)=when(operation) {FileOperation.READ_RANGE,FileOperation.PREPARE_DOWNLOAD,FileOperation.DOWNLOAD_READ,
            FileOperation.PREPARE_UPLOAD,FileOperation.UPLOAD_WRITE,FileOperation.BEGIN_UPLOAD_BATCH,FileOperation.WRITE_UPLOAD_MANIFEST,FileOperation.SEAL_UPLOAD_BATCH,
            FileOperation.CREATE_BATCH_DIRECTORY,FileOperation.PREPARE_BATCH_FILE,FileOperation.WRITE_BATCH_FILE->true; else->false}
    }
}
