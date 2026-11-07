package com.iconnect.proof

import com.iconnect.protocol.*

internal object UploadRules {
    const val CAPABILITY="uploads-v1"
    const val PUBLISHED_CHECK_CAPABILITY="published-upload-check-v1"
    const val QUOTA=16L*1024*1024*1024
    fun isRpc(operation:FileOperation)=operation==FileOperation.VERIFY_PUBLISHED_UPLOAD || operation.number in 23..29
    fun validate(info:UploadInfo) {
        try {StorageRules.name(info.name)} catch(e:StorageFailure) {throw IllegalArgumentException("Unsupported incoming-copy name.",e)}
        require(Rules.tokenPattern.matches(info.id) && MutationRules.created(info.id) in 0..StorageRules.MAX_TIMESTAMP &&
            info.size in 0..StorageRules.MAX_TRANSFER_SIZE && info.offset in 0..info.size && info.sha256.size()==32 &&
            info.phase!=UploadPhase.UNRECOGNIZED && info.collision!=UploadCollision.UNRECOGNIZED && info.message.length<=512 && info.message.none(Character::isISOControl))
        if(info.finalName.isNotEmpty()) {try {StorageRules.name(info.finalName)} catch(e:StorageFailure) {throw IllegalArgumentException("Unsupported incoming-copy destination name.",e)}; require(info.phase in setOf(UploadPhase.UPLOAD_FINALIZING,UploadPhase.UPLOAD_COMPLETED,UploadPhase.UPLOAD_REVIEW))}
        if(info.phase==UploadPhase.UPLOAD_COMPLETED) require(info.offset==info.size && info.finalName.isNotEmpty())
    }
}
