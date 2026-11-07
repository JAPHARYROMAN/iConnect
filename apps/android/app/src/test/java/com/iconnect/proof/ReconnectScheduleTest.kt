package com.iconnect.proof

import org.junit.Assert.*
import org.junit.Test

class ReconnectScheduleTest {
    private val ready=ReconnectSnapshot(true,true,true)
    @Test fun failedAttemptsUseMonotonicBoundedBackoffAndJitter() {
        var now=0L; val retry=ReconnectSchedule({now},{1.0})
        listOf(1L,2L,4L,8L,16L,32L,60L,60L).forEach {seconds->
            assertEquals(ReconnectAction.COORDINATOR,retry.next(ready)); assertNull(retry.next(ready)); assertEquals(seconds,retry.remainingSeconds())
            now+=seconds*1000-1; assertNull(retry.next(ready)); now++
        }
        val minimum=ReconnectSchedule({0L},{.8}); minimum.next(ready); assertEquals(1L,minimum.remainingSeconds())
        val maximum=ReconnectSchedule({now},{1.2}); repeat(9) {maximum.next(ready); assertTrue(maximum.remainingSeconds() in 1..60); now+=60_000}
    }
    @Test fun foregroundNetworkAndConsentFlappingNeverResetFailureBudget() {
        var now=0L; val retry=ReconnectSchedule({now},{1.0}); retry.next(ready)
        repeat(30) {assertNull(retry.next(ready.copy(network=false))); assertNull(retry.next(ready.copy(visible=false))); assertNull(retry.next(ready.copy(allowed=false)))}
        now=999; assertNull(retry.next(ready)); now=1000; assertNotNull(retry.next(ready)); assertEquals(2L,retry.remainingSeconds())
        now=2000; retry.next(ready.copy(authenticated=true)); now=2100; assertNull(retry.next(ready)); now=3000; retry.next(ready); assertEquals(4L,retry.remainingSeconds())
        retry.next(ready.copy(authenticated=true)); now+=30_000; retry.next(ready.copy(authenticated=true)); assertNotNull(retry.next(ready)); assertEquals(1L,retry.remainingSeconds())
    }
    @Test fun pendingConnectionsAndPairingNeverCreateOverlappingAttempts() {
        val retry=ReconnectSchedule({0L},{1.0})
        listOf(ready.copy(connecting=true),ready.copy(peerConnecting=true),ready.copy(pairing=true),ready.copy(authenticated=true)).forEach {value->repeat(20){assertNull(retry.next(value))}}
        assertEquals(ReconnectAction.COORDINATOR,retry.next(ready))
    }
    @Test fun coordinatorHandsOffOnceAndContextRefreshNeverInterruptsAuthentication() {
        var now=0L; val retry=ReconnectSchedule({now},{1.0})
        assertEquals(ReconnectAction.COORDINATOR,retry.next(ready))
        val online=ready.copy(connected=true)
        assertEquals(ReconnectAction.PEER,retry.next(online)); assertNull(retry.next(online))
        now=2000; assertNull(retry.next(online.copy(refresh=true,peerConnecting=true)))
        assertEquals(ReconnectAction.REFRESH,retry.next(online.copy(refresh=true)))
        assertNull(retry.next(ready)); assertNull(retry.next(online.copy(refresh=true,authenticated=true)))
    }
}
