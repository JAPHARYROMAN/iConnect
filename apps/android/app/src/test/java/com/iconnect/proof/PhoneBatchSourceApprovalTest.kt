package com.iconnect.proof

import org.junit.Assert.*
import org.junit.Test

class PhoneBatchSourceApprovalTest {
    @Test fun destinationBindingCoversTreeParentIdentityNameOrderAndCollision() {
        val parents=listOf(SafFolderBinding("first","One"),SafFolderBinding("second","Two"))
        val original=PhoneBatchSourceApproval.binding("content://generated/tree",parents,PhoneCollision.KEEP_BOTH)
        for(changed in listOf(PhoneBatchSourceApproval.binding("content://other/tree",parents,PhoneCollision.KEEP_BOTH),
            PhoneBatchSourceApproval.binding("content://generated/tree",parents.reversed(),PhoneCollision.KEEP_BOTH),
            PhoneBatchSourceApproval.binding("content://generated/tree",listOf(parents[0].copy(id="different"),parents[1]),PhoneCollision.KEEP_BOTH),
            PhoneBatchSourceApproval.binding("content://generated/tree",listOf(parents[0].copy(name="Different"),parents[1]),PhoneCollision.KEEP_BOTH),
            PhoneBatchSourceApproval.binding("content://generated/tree",parents,PhoneCollision.SKIP))) assertNotEquals(original,changed)
    }
    @Test fun lengthDelimitedDestinationFieldsCannotBeRepartitioned() {
        assertNotEquals(PhoneBatchSourceApproval.binding("ab",listOf(SafFolderBinding("c","d")),PhoneCollision.KEEP_BOTH),
            PhoneBatchSourceApproval.binding("a",listOf(SafFolderBinding("bc","d")),PhoneCollision.KEEP_BOTH))
    }
    @Test fun unallocatedPartCanAllocateOnlyWithinTheOriginalPrivateFolder() {
        assertTrue(PhoneBatchSourceApproval.unallocated("1:2:0:0")); assertTrue(PhoneBatchSourceApproval.matchesPart("1:2:0:0","1:2:3:4"))
        assertFalse(PhoneBatchSourceApproval.matchesPart("1:2:0:0","1:9:3:4")); assertFalse(PhoneBatchSourceApproval.unallocated("1:2:3:4"))
    }
    @Test fun recordedNativePartIdentityCannotBeReplacedOrLost() {
        assertTrue(PhoneBatchSourceApproval.matchesPart("1:2:3:4","1:2:3:4")); assertFalse(PhoneBatchSourceApproval.matchesPart("1:2:3:4","1:2:3:5"))
        assertFalse(PhoneBatchSourceApproval.matchesPart("1:2:3:4","1:2:0:0"))
    }
}
