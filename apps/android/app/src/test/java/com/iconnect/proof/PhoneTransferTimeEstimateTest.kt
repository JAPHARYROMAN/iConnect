package com.iconnect.proof

import org.junit.Assert.*
import org.junit.Test

class PhoneTransferTimeEstimateTest {
    private val now=1_800_000_000_000L
    @Test fun payloadLimitRoundsUpAndIncludesOnlyRemainingBytes() {
        val estimate=PhoneTransferTimeEstimate.create(131073,65536,65536,now+10000,now)
        assertEquals(2L,estimate.minimumPayloadSeconds); assertEquals(65537L,estimate.remainingBytes); assertFalse(estimate.outlastsApproval)
    }
    @Test fun fullPayloadStillNeedsVerificationAndNoSourceRenewal() {
        val estimate=PhoneTransferTimeEstimate.create(100,100,0,now-1000,now)
        assertEquals(0L,estimate.minimumPayloadSeconds); assertFalse(estimate.outlastsApproval); assertTrue(estimate.payloadText.contains("Verification"))
    }
    @Test fun unlimitedDoesNotInventAnEta() {assertNull(PhoneTransferTimeEstimate.create(1,0,0,0,now).minimumPayloadSeconds)}
    @Test fun unknownExpiryRemainsUnknown() {assertNull(PhoneTransferTimeEstimate.create(1,0,65536,0,now).sourceSeconds)}
    @Test fun deadlineEqualityWarns() {assertTrue(PhoneTransferTimeEstimate.create(65536,0,65536,now+1000,now).outlastsApproval)}
    @Test fun expiredSourceWarnsEvenWithUnlimitedRate() {assertTrue(PhoneTransferTimeEstimate.create(1,0,0,now-1,now).outlastsApproval)}
    @Test fun largestTransferIsOverflowSafe() {assertEquals(134217728L,PhoneTransferTimeEstimate.create(StorageRules.MAX_TRANSFER_SIZE,0,65536,0,now).minimumPayloadSeconds)}
    @Test fun invalidRangesRatesAndTimestampsAreRejected() {
        for(action in listOf<()->Unit>({PhoneTransferTimeEstimate.create(-1,0,0,0,now)},{PhoneTransferTimeEstimate.create(1,2,0,0,now)},
            {PhoneTransferTimeEstimate.create(1,0,1,0,now)},{PhoneTransferTimeEstimate.create(1,0,0,-1,now)},
            {PhoneTransferTimeEstimate.create(1,0,0,0,StorageRules.MAX_TIMESTAMP+1)})) assertThrows(IllegalArgumentException::class.java,action)
    }
}
