package com.iconnect.proof

import com.iconnect.protocol.*

data class CreatedDirectoryReceipt(val id:String,val epoch:String,val grant:String,val requestedName:String)

object CreatedDirectoryReferences {
    fun validate(receipt:CreatedDirectoryReceipt) {
        require(listOf(receipt.id,receipt.epoch,receipt.grant).all {Rules.tokenPattern.matches(it)})
        StorageRules.name(receipt.requestedName); require(MutationRules.created(receipt.id) in 0..StorageRules.MAX_TIMESTAMP)
    }
    fun check(receipt:CreatedDirectoryReceipt,response:FileResponse):FileEntry {
        validate(receipt); PhoneTransferManifests.validateResponse(response)
        if(response.status!=FileStatus.FILE_STATUS_OK) throw StorageFailure(response.status,response.message.ifEmpty {"The created folder needs review."})
        val mutation=response.mutation; val entry=response.entriesList.singleOrNull()
        require(response.mutationEpoch==receipt.epoch && response.hasMutation() && mutation.id==receipt.id && mutation.operation==FileOperation.CREATE_DIRECTORY &&
            mutation.phase==MutationPhase.CHANGE_SUCCEEDED && mutation.status==FileStatus.FILE_STATUS_OK && mutation.requestedName==receipt.requestedName &&
            entry!=null && entry.isDirectory && !entry.isRoot && entry.parentId.isNotEmpty() && entry.parentId!=entry.id && entry.grantId==receipt.grant && entry.name==mutation.resultingName && entry.permissions and StorageRules.CREATE!=0) {
            "The receiver changed the created-folder receipt or reference. Review the destination."
        }
        return entry
    }
    suspend fun resolve(receipt:CreatedDirectoryReceipt,request:suspend (FileRequest)->FileResponse):FileEntry {
        validate(receipt)
        return check(receipt,request(FileRequest.newBuilder().setOperation(FileOperation.RESOLVE_CREATED_DIRECTORY).setMutationId(receipt.id).setMutationEpoch(receipt.epoch).build()))
    }
}
