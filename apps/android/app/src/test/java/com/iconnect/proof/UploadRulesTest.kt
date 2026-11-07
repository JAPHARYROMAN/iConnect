package com.iconnect.proof

import com.google.protobuf.ByteString
import com.iconnect.protocol.*
import org.junit.Assert.*
import org.junit.Test

class UploadRulesTest {
    private fun info()=UploadInfo.newBuilder().setId(MutationRules.newId()).setName("新しい.txt").setSize(10).setSha256(ByteString.copyFrom(ByteArray(32))).build()
    @Test fun descriptorsCannotClaimCompletionWithMissingBytesOrUnreviewedNames() {
        val value=info(); UploadRules.validate(value)
        listOf(value.toBuilder().setPhase(UploadPhase.UPLOAD_COMPLETED).setFinalName("done.txt").build(),value.toBuilder().setOffset(11).build(),
            value.toBuilder().setName("../escape").build(),value.toBuilder().setFinalName("unexpected.txt").build(),value.toBuilder().clearSha256().build()).forEach {
            assertThrows(IllegalArgumentException::class.java) {UploadRules.validate(it)}
        }
        UploadRules.validate(value.toBuilder().setOffset(10).setPhase(UploadPhase.UPLOAD_COMPLETED).setFinalName("done.txt").build())
    }
    @Test fun capabilityOnlyCoversTheDedicatedIncomingCopyOperations() {
        assertTrue(UploadRules.isRpc(FileOperation.PREPARE_UPLOAD)); assertTrue(UploadRules.isRpc(FileOperation.LIST_UPLOADS)); assertFalse(UploadRules.isRpc(FileOperation.READ_RANGE))
        assertFalse(UploadRules.isRpc(FileOperation.PREPARE_DOWNLOAD))
    }
}
