package com.iconnect.proof

import com.iconnect.protocol.*

object RecoveryRules {
    const val CAPABILITY = "recovery-v1"
    fun isChange(operation: FileOperation) = operation in setOf(FileOperation.DELETE, FileOperation.RESTORE)
    fun isRpc(operation: FileOperation) = isChange(operation) || operation in setOf(FileOperation.RECOVERY_CONTEXT, FileOperation.LIST_DELETED, FileOperation.RECOVERY_STATUS)
    fun validate(item: DeletedInfo) {
        require(Rules.tokenPattern.matches(item.id) && item.name.length in 1..255 && item.name.none(Character::isISOControl) &&
            item.size >= 0 && item.deletedAt in 0..StorageRules.MAX_TIMESTAMP && MutationRules.created(item.id) == item.deletedAt && item.state != DeletedState.UNRECOGNIZED &&
            item.message.length <= 512 && item.message.none(Character::isISOControl) && item.restoredName.length <= 255 && item.restoredName.none(Character::isISOControl) && (!item.canRestore || item.state == DeletedState.DELETED_STORED))
    }
}
