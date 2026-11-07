package com.iconnect.proof

import org.junit.Assert.*
import org.junit.Test

class PhonePayloadRateTrackerTest {
    private val m=1048576L; private val size=10*m
    @Test fun incomingFinalizationMeasuresEachBytePassIndependently() {
        var now = 0L
        val tracker = PhoneIncomingFinalizationTracker("receipt", size) { now }
        tracker.begin(PhoneIncomingFinalizationStage.CHECKING_PRIVATE_BYTES)
        now = 2000
        tracker.advance(2 * m)
        assertEquals(PhonePayloadRateEstimate(PhonePayloadRateState.AVAILABLE, m, 8), tracker.read().estimate)
        now = 12001
        assertEquals(PhonePayloadRateState.WAITING, tracker.read().estimate?.state)
        assertEquals(2 * m, tracker.read().offset)
        tracker.begin(PhoneIncomingFinalizationStage.WRITING_PROVIDER_DOCUMENT)
        assertEquals(0L, tracker.read().offset)
        assertEquals(PhonePayloadRateState.MEASURING, tracker.read().estimate?.state)
        tracker.advance(size)
        assertEquals(PhonePayloadRateState.RECEIVED, tracker.read().estimate?.state)
        assertNull(tracker.read().estimate?.remainingSeconds)
        tracker.begin(PhoneIncomingFinalizationStage.SYNCING_PROVIDER_DOCUMENT)
        assertNull(tracker.read().estimate)
        tracker.begin(PhoneIncomingFinalizationStage.CHECKING_PROVIDER_BYTES)
        assertEquals(PhonePayloadRateState.MEASURING, tracker.read().estimate?.state)
    }
    private fun ready()=PhonePayloadRateTracker().also {it.observe("job","source",size,0,true,0); it.observe("job","source",size,m,true,1000); it.observe("job","source",size,2*m,true,2000)}
    @Test fun recentProgressUsesRemainingPayloadAndRoundsUp() {val tracker=ready(); val value=tracker.read("job","source",size,2*m+1,true,2000); assertEquals(m,value.bytesPerSecond); assertEquals(8L,value.remainingSeconds); assertEquals(PhonePayloadRateState.AVAILABLE,value.state)}
    @Test fun aDifferentSizeResetsOldSamples() {assertNull(ready().read("job","source",size+1,2*m,true,2000).remainingSeconds)}
    @Test fun configuredCapBoundsEtaWithoutRelabellingTheObservedRate() {val value=ready().read("job","source",size,2*m,true,2000,65536); assertEquals(m,value.bytesPerSecond); assertEquals(128L,value.remainingSeconds)}
    @Test fun fewerThanTwoSecondsNeverInventsARate() {val tracker=PhonePayloadRateTracker(); tracker.observe("job","source",size,0,true,0); tracker.observe("job","source",size,m,true,1999); assertNull(tracker.read("job","source",size,m,true,1999).bytesPerSecond)}
    @Test fun readsAndEqualOffsetsDoNotCountAsProgress() {val tracker=PhonePayloadRateTracker(); tracker.observe("job","source",size,0,true,0); tracker.observe("job","source",size,0,true,2000); assertNull(tracker.read("job","source",size,0,true,2000).remainingSeconds); assertEquals(PhonePayloadRateState.WAITING,tracker.read("job","source",size,0,true,10001).state)}
    @Test fun stalledRateDisappearsAndDoesNotSpanIdleTime() {val tracker=ready(); assertEquals(PhonePayloadRateState.WAITING,tracker.read("job","source",size,2*m,true,12001).state); tracker.observe("job","source",size,3*m,true,13000); assertNull(tracker.read("job","source",size,3*m,true,13000).remainingSeconds); tracker.observe("job","source",size,4*m,true,14000); tracker.observe("job","source",size,5*m,true,15000); assertEquals(5L,tracker.read("job","source",size,5*m,true,15000).remainingSeconds)}
    @Test fun pauseClearsSamplesAndResumeUsesRetainedOffset() {val tracker=ready(); tracker.observe("job","source",size,2*m,false,2100); assertEquals(0,tracker.count); tracker.observe("job","source",size,2*m,true,60000); assertNull(tracker.read("job","source",size,2*m,true,60000).remainingSeconds)}
    @Test fun newPeerTicketLimitOrSessionScopeForgetsOldActivity() {for(scope in listOf("other-peer","other-ticket","new-limit","new-session")) {val tracker=ready(); assertNull(tracker.read("job",scope,size,2*m,true,2000).remainingSeconds); tracker.observe("job",scope,size,3*m,true,3000); assertNull(tracker.read("job",scope,size,3*m,true,3000).remainingSeconds)}}
    @Test fun rollbackOfOffsetOrMonotonicClockResetsTheWindow() {assertNull(ready().read("job","source",size,m,true,2100).remainingSeconds); assertNull(ready().read("job","source",size,2*m,true,1900).remainingSeconds)}
    @Test fun uiReadDoesNotMakeDelayedProgressLookLikeClockRollback() {val tracker=ready(); assertNotNull(tracker.read("job","source",size,2*m,true,2500).remainingSeconds); tracker.observe("job","source",size,3*m,true,2400); assertNotNull(tracker.read("job","source",size,3*m,true,2500).remainingSeconds)}
    @Test fun recentWindowForgetsEarlierSlowProgress() {val tracker=PhonePayloadRateTracker(); val total=100*m; for(second in 0..40) tracker.observe("job","source",total,(if(second<=20) second else 20+2*(second-20))*m,true,second*1000L); val value=tracker.read("job","source",total,60*m,true,40000); assertEquals(2*m,value.bytesPerSecond); assertEquals(20L,value.remainingSeconds)}
    @Test fun jobsNeverShareSamples() {val tracker=ready(); tracker.observe("other","source",size,m,true,2000); assertNull(tracker.read("other","source",size,m,true,2000).remainingSeconds); assertEquals(m,tracker.read("job","source",size,2*m,true,2000).bytesPerSecond)}
    @Test fun receivedAndZeroPayloadClearRateWithoutClaimingVerification() {val tracker=ready(); val value=tracker.read("job","source",size,size,false,2000); assertEquals(PhonePayloadRateState.RECEIVED,value.state); assertEquals(0,tracker.count); assertTrue(value.text.contains("Verification")); assertNull(value.remainingSeconds); assertEquals(PhonePayloadRateState.RECEIVED,tracker.read("zero","source",0,0,true,0).state)}
    @Test fun peerDeadlineWarningIsAdvisoryAndIncludesEquality() {val value=ready().read("job","source",size,2*m,true,2000); assertTrue(value.approvalWarning(8).isNotEmpty()); assertTrue(value.approvalWarning(9).isEmpty()); assertTrue(value.approvalWarning(null).isEmpty())}
    @Test fun largestPayloadIsOverflowSafe() {val tracker=PhonePayloadRateTracker(); val total=StorageRules.MAX_TRANSFER_SIZE; tracker.observe("large","source",total,0,true,0); tracker.observe("large","source",total,total/2,true,2000); assertEquals(2L,tracker.read("large","source",total,total/2,true,2000).remainingSeconds)}
    @Test fun storageIsBoundedAndRemovedRecordsCanBePruned() {val tracker=PhonePayloadRateTracker(); for(i in 0..199) tracker.observe(i.toString(),"source",size,0,true,i.toLong()); assertEquals(128,tracker.count); tracker.retain(setOf("199")); assertEquals(1,tracker.count); tracker.clear(); assertEquals(0,tracker.count)}
    @Test fun invalidObservationsAndLimitsAreRejected():Unit {val tracker=PhonePayloadRateTracker(); for(action in listOf<()->Unit>({tracker.observe("","source",size,0,true,0)},{tracker.observe("job","source",-1,0,true,0)},{tracker.observe("job","source",size,size+1,true,0)},{tracker.observe("job","source",size,0,true,-1)},{tracker.read("job","source",size,0,true,0,1)})) assertThrows(IllegalArgumentException::class.java,action)}
}
