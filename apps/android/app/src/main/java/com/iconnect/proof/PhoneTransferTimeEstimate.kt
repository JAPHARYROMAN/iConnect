package com.iconnect.proof

data class PhoneTransferTimeEstimate(val remainingBytes:Long,val minimumPayloadSeconds:Long?,val sourceSeconds:Long?,val outlastsApproval:Boolean) {
    val payloadText get()=if(remainingBytes==0L) "Payload received. Verification and provider saving may still take time."
        else minimumPayloadSeconds?.let {"At the configured limit, remaining payload needs at least ${duration(it)} when this file has the whole directional budget. Other work, network and verification can take longer."}
            ?: "No configured limit. A reliable completion-time estimate is unavailable."
    val approvalText get()=when(sourceSeconds) {
        null->"Source expiry is not yet known."
        0L->"The peer-reported source approval has expired. Review a fresh source before requesting more bytes."
        else->"Peer-reported source approval remaining: about ${duration(sourceSeconds)}. Device clocks may differ."
    }
    val warning get()=if(outlastsApproval) "The payload estimate reaches or exceeds source expiry. Review the limit or a fresh source; approval is not extended automatically." else ""
    companion object {
        fun create(size:Long,offset:Long,rate:Long,expires:Long,now:Long):PhoneTransferTimeEstimate {
            require(size in 0..StorageRules.MAX_TRANSFER_SIZE && offset in 0..size && expires in 0..StorageRules.MAX_TIMESTAMP && now in 0..StorageRules.MAX_TIMESTAMP)
            PhoneNetworkPreferences(rate).validate()
            val remaining=size-offset
            val minimum=if(remaining==0L) 0L else if(rate==0L) null else (remaining+rate-1)/rate
            val source=if(expires==0L) null else ((expires-now)/1000).coerceAtLeast(0)
            return PhoneTransferTimeEstimate(remaining,minimum,source,remaining!=0L && source!=null && (source==0L || minimum!=null && minimum>=source))
        }
        private fun duration(seconds:Long)=when {
            seconds>=86400->"${seconds/86400} d ${seconds/3600%24} h"
            seconds>=3600->"${seconds/3600} h ${seconds/60%60} min"
            seconds>=60->"${seconds/60} min ${seconds%60} s"
            else->"$seconds s"
        }
    }
}

/** A memory-only offer. Approving it changes source authority, never starts receiving or saving. */
data class PhoneDownloadSourceReview(val id:String,val job:PhoneDownload,val newVersion:String,val hash:String,
    val sourceExpiresAt:Long,val reviewExpiresAt:Long)
