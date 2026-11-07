package com.iconnect.proof

import com.iconnect.protocol.PeerAvailability
import com.iconnect.protocol.PresenceSnapshot
import com.iconnect.protocol.PresenceUpdate
import org.junit.Assert.*
import org.junit.Test

class PresenceTrackerTest {
    private val owner="a".repeat(64); private val peer="b".repeat(64); private val other="c".repeat(64)
    private fun report(update:PresenceUpdate,available:Boolean=true)=PresenceSnapshot.newBuilder().setRequestId(update.requestId)
        .addAllPeers(update.peerIdsList.map {PeerAvailability.newBuilder().setPeerId(it).setAvailable(available).build()}).build()
    @Test fun expiryUsesRequestTimeAndOldRepliesCannotReviveIt() {
        var now=0L; val tracker=PresenceTracker(owner) {now}; assertTrue(tracker.availablePeers().isEmpty())
        val first=requireNotNull(tracker.next(true,listOf(peer))); now=9999; tracker.accept(report(first)); assertEquals(setOf(peer),tracker.availablePeers())
        assertNull(tracker.next(true,listOf(peer))); now=10_000; val next=requireNotNull(tracker.next(true,listOf(peer)))
        tracker.accept(report(first,false)); assertEquals(setOf(peer),tracker.availablePeers())
        now=25_000; assertTrue(tracker.availablePeers().isEmpty()); now=35_000; tracker.accept(report(next)); assertTrue(tracker.availablePeers().isEmpty())
    }
    @Test fun disableRevokeClearAndReenableNeverReuseAnOldReport() {
        val tracker=PresenceTracker(owner) {0L}; val first=requireNotNull(tracker.next(true,listOf(peer)))
        tracker.accept(report(first)); assertEquals(setOf(peer),tracker.availablePeers())
        val disabled=requireNotNull(tracker.next(false,listOf(peer))); assertTrue(disabled.peerIdsList.isEmpty()); assertTrue(tracker.availablePeers().isEmpty())
        tracker.accept(report(first)); assertTrue(tracker.availablePeers().isEmpty()); assertNull(tracker.next(false,listOf(peer)))
        val changed=requireNotNull(tracker.next(true,listOf(other))); tracker.accept(report(first)); assertTrue(tracker.availablePeers().isEmpty())
        tracker.accept(report(changed)); tracker.clear(); tracker.accept(report(changed)); assertTrue(tracker.availablePeers().isEmpty())
    }
    @Test fun currentReportsCannotSubstituteDuplicateOrOmitPeers() {
        val tracker=PresenceTracker(owner) {0L}; val update=requireNotNull(tracker.next(true,listOf(peer,other)))
        val valid=report(update)
        listOf(valid.toBuilder().clearPeers().build(),valid.toBuilder().setPeers(0,valid.peersList[1]).build(),
            valid.toBuilder().setPeers(0,PeerAvailability.newBuilder().setPeerId("d".repeat(64)).setAvailable(true)).build()).forEach {
            assertThrows(IllegalArgumentException::class.java) {tracker.accept(it)}; assertTrue(tracker.availablePeers().isEmpty())
        }
        tracker.accept(valid); assertEquals(setOf(peer,other),tracker.availablePeers())
    }
    @Test fun idsMustBeBoundedUniqueCanonicalAndExcludeSelf() {
        val tracker=PresenceTracker(owner) {0L}
        listOf(listOf(owner),listOf(peer,peer),listOf("B".repeat(64)),listOf("unknown"),List(129) {peer}).forEach {
            assertThrows(IllegalArgumentException::class.java) {tracker.next(true,it)}
        }
        val sorted=requireNotNull(tracker.next(true,listOf(other,peer))); assertEquals(listOf(peer,other),sorted.peerIdsList)
    }
}
