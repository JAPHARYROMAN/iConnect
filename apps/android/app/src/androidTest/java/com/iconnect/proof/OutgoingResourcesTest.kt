package com.iconnect.proof

import androidx.test.ext.junit.runners.AndroidJUnit4
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.IOException

@RunWith(AndroidJUnit4::class)
class OutgoingResourcesTest {
    private val peer="b".repeat(64)
    private fun row(bytes:Long,pending:Long=0,who:String=peer)=PhoneOutgoingResources.Reservation(StorageRules.token(),who,bytes,pending)
    @Test fun preservedUnknownOwnershipChargesEveryPeerOnceGloballyAndSurvivesParticipantClose() {
        val pool=PhoneOutgoingResources(); val single=pool.open(PhoneOutgoingResources.SINGLE,"test-profile"); val batch=pool.open(PhoneOutgoingResources.BATCH,"test-profile")
        val first=row(UploadRules.QUOTA-10); single.recover(listOf(first),false); batch.recover(emptyList(),false,10)
        assertTrue(pool.inventoryReady); assertEquals(UploadRules.QUOTA,pool.reservedBytes); assertEquals(UploadRules.QUOTA,pool.reservedForPeer(peer)); assertEquals(10L,pool.reservedForPeer("c".repeat(64)))
        assertEquals(0L,single.otherPending()); assertThrows(IOException::class.java) {single.replace(listOf(first.copy(bytes=first.bytes+1)))}
        batch.close(); assertEquals(UploadRules.QUOTA,pool.reservedBytes); assertFalse(pool.inventoryReady)
        pool.open(PhoneOutgoingResources.BATCH,"test-profile").use {reopened->
            reopened.recover(emptyList(),false,10); assertTrue(pool.inventoryReady); single.replace(listOf(first.copy(bytes=first.bytes-1))); assertEquals(UploadRules.QUOTA-1,pool.reservedBytes)
            reopened.recover(emptyList(),false); single.replace(listOf(first.copy(bytes=UploadRules.QUOTA))); assertEquals(UploadRules.QUOTA,pool.reservedBytes)
        }; single.close()
    }
    @Test fun preservedBytesAlsoApplyToTheProfileLimitAcrossDistinctPeerGroups() {
        val pool=PhoneOutgoingResources(); val single=pool.open(PhoneOutgoingResources.SINGLE,"test-profile"); val batch=pool.open(PhoneOutgoingResources.BATCH,"test-profile")
        val rows=(0..3).map {row(UploadRules.QUOTA-10,who=('a'+it).toString().repeat(64))}; single.recover(rows,false); batch.recover(emptyList(),false,10)
        val last=row(30,who="e".repeat(64)); batch.replace(listOf(last)); assertEquals(UploadRules.QUOTA*4,pool.reservedBytes)
        assertThrows(IOException::class.java) {batch.replace(listOf(last.copy(bytes=31)))}; assertEquals(UploadRules.QUOTA*4,pool.reservedBytes); single.close(); batch.close()
    }
    @Test fun sharedByteBoundariesPendingAndUnknownInventoriesAreAtomic() {
        val pool=PhoneOutgoingResources(); val single=pool.open(PhoneOutgoingResources.SINGLE,"test-profile"); val first=row(UploadRules.QUOTA-3,7)
        single.recover(listOf(first),false); assertFalse(pool.inventoryReady)
        assertThrows(IOException::class.java) {single.replace(listOf(first,row(0)))}
        val batch=pool.open(PhoneOutgoingResources.BATCH,"test-profile"); batch.recover(emptyList(),false); assertTrue(pool.inventoryReady)
        val last=row(3,2); batch.replace(listOf(last)); assertEquals(UploadRules.QUOTA,pool.reservedForPeer(peer)); assertEquals(7L,batch.otherPending(last.id))
        assertThrows(IOException::class.java) {batch.replace(listOf(last.copy(bytes=4)))}; assertEquals(UploadRules.QUOTA,pool.reservedBytes)
        batch.uncertain(); single.replace(listOf(first.copy(pending=0))); assertThrows(IOException::class.java) {single.replace(listOf(first,row(0)))}
        batch.recover(listOf(last),false); batch.close(); assertFalse(pool.inventoryReady); assertEquals(UploadRules.QUOTA,pool.reservedBytes)
        pool.open(PhoneOutgoingResources.BATCH,"test-profile").use {it.recover(listOf(last),false); assertTrue(pool.inventoryReady)}; single.close()
    }
    @Test fun globalQuotaAndIdempotentWorkerLeasesAreShared() {
        val pool=PhoneOutgoingResources(); val single=pool.open(PhoneOutgoingResources.SINGLE,"test-profile"); val batch=pool.open(PhoneOutgoingResources.BATCH,"test-profile")
        val rows=(0..3).map {row(UploadRules.QUOTA,who=('a'+it).toString().repeat(64))}; single.recover(rows.take(2),false); batch.recover(rows.drop(2),false)
        assertEquals(UploadRules.QUOTA*4,pool.reservedBytes); assertThrows(IOException::class.java) {batch.replace(rows.drop(2)+row(1,who="f".repeat(64)))}
        val a=requireNotNull(pool.tryWorker()); val b=requireNotNull(pool.tryWorker()); assertNull(pool.tryWorker()); a.close(); a.close(); assertEquals(1,pool.activeWorkers)
        requireNotNull(pool.tryWorker()).use {assertEquals(2,pool.activeWorkers); assertNull(pool.tryWorker())}; b.close(); assertEquals(0,pool.activeWorkers); single.close(); batch.close()
    }
    @Test fun competingStoresReservePendingDiskBytesAtomically()=runBlocking {
        val pool=PhoneOutgoingResources(); val single=pool.open(PhoneOutgoingResources.SINGLE,"test-profile"); val batch=pool.open(PhoneOutgoingResources.BATCH,"test-profile")
        single.recover(emptyList(),false); batch.recover(emptyList(),false); val start=CompletableDeferred<Unit>()
        val attempts=listOf(single,batch).map {store->async(Dispatchers.Default) {start.await(); try {store.replace(listOf(row(60,60))) {64L*1024*1024+100}; true} catch(_:IOException) {false}}}
        start.complete(Unit); assertEquals(1,attempts.awaitAll().count {it}); assertEquals(60L,pool.reservedBytes); assertEquals(60L,single.otherPending())
        single.replace(emptyList()) {0}; batch.replace(emptyList()) {0}; assertEquals(0L,pool.reservedBytes); single.close(); batch.close()
    }
}
