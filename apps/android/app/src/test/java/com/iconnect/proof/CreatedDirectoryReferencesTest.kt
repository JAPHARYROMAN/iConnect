package com.iconnect.proof

import com.iconnect.protocol.*
import org.junit.Assert.*
import org.junit.Test

class CreatedDirectoryReferencesTest {
    private fun fixture():Pair<CreatedDirectoryReceipt,FileResponse> {
        val id=MutationRules.newId(); val receipt=CreatedDirectoryReceipt(id,StorageRules.token(),StorageRules.token(),"folder")
        val entry=FileEntry.newBuilder().setId(StorageRules.token()).setGrantId(receipt.grant).setParentId(StorageRules.token()).setName("folder").setIsDirectory(true).setPermissions(12).setVersion("a".repeat(64)).build()
        val mutation=MutationInfo.newBuilder().setId(id).setOperation(FileOperation.CREATE_DIRECTORY).setPhase(MutationPhase.CHANGE_SUCCEEDED).setRequestedName("folder").setResultingName("folder").setCreatedAt(MutationRules.created(id)).build()
        return receipt to FileResponse.newBuilder().setRequestId(StorageRules.token()).setMutationEpoch(receipt.epoch).setMutation(mutation).addEntries(entry).build()
    }
    @Test fun confirmedDirectoryIsBoundToTheOriginalReceiptAndGrant() {
        val (receipt,response)=fixture(); assertEquals("folder",CreatedDirectoryReferences.check(receipt,response).name)
    }
    @Test fun changedOrAmbiguousDescriptorsAreRejected() {
        val (receipt,response)=fixture(); val entry=response.entriesList.single()
        val changes=listOf(response.toBuilder().setMutationEpoch(StorageRules.token()),
            response.toBuilder().setMutation(response.mutation.toBuilder().setRequestedName("different")),
            response.toBuilder().setMutation(response.mutation.toBuilder().setPhase(MutationPhase.CHANGE_REVIEW)),
            response.toBuilder().setEntries(0,entry.toBuilder().setGrantId(StorageRules.token())),
            response.toBuilder().setEntries(0,entry.toBuilder().setName("different")),
            response.toBuilder().setEntries(0,entry.toBuilder().setIsDirectory(false)),
            response.toBuilder().setEntries(0,entry.toBuilder().setParentId(entry.id)),response.toBuilder().addEntries(entry))
        changes.forEach {assertThrows(IllegalArgumentException::class.java) {CreatedDirectoryReferences.check(receipt,it.build())}}
    }
    @Test fun recoveryIsAnIndependentlyNegotiatedReceiptOperation() {
        assertTrue(MutationRules.isRpc(FileOperation.RESOLVE_CREATED_DIRECTORY)); assertFalse(MutationRules.isChange(FileOperation.RESOLVE_CREATED_DIRECTORY))
        assertFalse(UploadRules.isRpc(FileOperation.RESOLVE_CREATED_DIRECTORY)); assertEquals("created-folders-v1",MutationRules.CREATED_FOLDERS_CAPABILITY)
    }
}
