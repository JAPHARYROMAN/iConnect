package com.iconnect.proof

import com.iconnect.protocol.*
import java.nio.ByteBuffer
import java.security.SecureRandom

object MutationRules {
    const val CAPABILITY = "mutations-v1"
    const val CREATED_FOLDERS_CAPABILITY = "created-folders-v1"
    const val LIFETIME = 24 * 60 * 60 * 1000L
    fun isChange(operation: FileOperation) = operation in setOf(FileOperation.CREATE_DIRECTORY, FileOperation.RENAME, FileOperation.MOVE)
    fun isRpc(operation: FileOperation) = isChange(operation) || operation in setOf(FileOperation.MUTATION_CONTEXT, FileOperation.MUTATION_STATUS, FileOperation.LIST_MUTATIONS,FileOperation.RESOLVE_CREATED_DIRECTORY)
    fun newId(): String = ByteArray(32).also { SecureRandom().nextBytes(it); ByteBuffer.wrap(it).putLong(System.currentTimeMillis()) }.let(Rules::b64)
    fun created(id: String): Long { require(Rules.tokenPattern.matches(id)); val bytes = Rules.decode(id); require(bytes.size == 32); return ByteBuffer.wrap(bytes).long }
    fun current(id: String, now:Long=System.currentTimeMillis()) { val at = created(id)
        if (at <= 0 || at < now - LIFETIME || at > now + 300_000) throw StorageFailure(FileStatus.STALE_REFERENCE, "This operation identifier expired or the clocks disagree. Refresh and review a new change.") }
    fun fingerprint(request: FileRequest) = Rules.hex(Rules.digest(request.toBuilder().clearRequestId().clearMutationId().clearMutationEpoch().build().toByteArray()))
    fun validate(info: MutationInfo) {
        require((isChange(info.operation) || RecoveryRules.isChange(info.operation)) && info.phase != MutationPhase.UNRECOGNIZED && info.status != FileStatus.UNRECOGNIZED &&
            info.message.length <= 512 && info.message.none(Character::isISOControl) && info.resultingName.length <= 255 && info.resultingName.none(Character::isISOControl) && info.requestedName.length in 1..255 && info.requestedName.none(Character::isISOControl) &&
            info.createdAt in 0..StorageRules.MAX_TIMESTAMP && created(info.id) == info.createdAt && (info.phase != MutationPhase.CHANGE_SUCCEEDED || info.status == FileStatus.FILE_STATUS_OK))
    }
}
