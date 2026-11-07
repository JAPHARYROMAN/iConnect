package com.iconnect.proof

import com.google.protobuf.ByteString
import com.iconnect.protocol.FileOperation
import com.iconnect.protocol.FileRequest
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test

class PhoneFileTrafficPolicyTest {
    private fun request(operation:FileOperation,length:Int=0)=FileRequest.newBuilder().setOperation(operation).setLength(length).build()
    @Test fun contentGatesCoverEveryCurrentBulkOperationAndCleanupRemainsUsable()=runBlocking {
        val policy=PhoneFileTrafficPolicy(); policy.configure(PhoneNetworkPreferences(pauseOnMetered=true)); policy.observe(PhoneNetworkCost.METERED)
        for(operation in listOf(FileOperation.READ_RANGE,FileOperation.PREPARE_DOWNLOAD,FileOperation.DOWNLOAD_READ,FileOperation.PREPARE_UPLOAD,FileOperation.UPLOAD_WRITE,
            FileOperation.BEGIN_UPLOAD_BATCH,FileOperation.WRITE_UPLOAD_MANIFEST,FileOperation.SEAL_UPLOAD_BATCH,FileOperation.CREATE_BATCH_DIRECTORY,FileOperation.PREPARE_BATCH_FILE,FileOperation.WRITE_BATCH_FILE)) {
            try {policy.beforeRequest(request(operation),true); fail("Content operation bypassed cost policy: $operation")} catch(_:StorageFailure) {}
        }
        policy.configure(PhoneNetworkPreferences(),false)
        for(operation in listOf(FileOperation.LIST_ROOTS,FileOperation.GET_METADATA,FileOperation.DOWNLOAD_STATUS,FileOperation.RELEASE_DOWNLOAD,FileOperation.CANCEL_UPLOAD,
            FileOperation.FINISH_UPLOAD,FileOperation.UPLOAD_BATCH_STATUS,FileOperation.STOP_UPLOAD_BATCH,FileOperation.CANCEL_UPLOAD_BATCH,FileOperation.CANCEL_BATCH_FILE,FileOperation.FINISH_BATCH_FILE,FileOperation.FINISH_UPLOAD_BATCH)) policy.beforeRequest(request(operation),false)
    }
    @Test fun unknownAndExpiredReadingsRequireExplicitSuitableNetworkWhenEnabled() {
        var now=0L; val policy=PhoneFileTrafficPolicy({now}); policy.requireContent(); policy.configure(PhoneNetworkPreferences(pauseOnMetered=true))
        assertThrows(StorageFailure::class.java) {policy.requireContent()}; policy.observe(PhoneNetworkCost.UNRESTRICTED); policy.requireContent()
        now=5001; assertEquals(PhoneNetworkCost.UNKNOWN,policy.networkCost); assertThrows(StorageFailure::class.java) {policy.requireContent()}
        policy.configure(PhoneNetworkPreferences()); policy.requireContent()
    }
    @Test fun invalidLimitsAndOutOfBoundsPayloadsCannotReachAWait()=runBlocking {
        for(rate in listOf(-1L,1L,65535L,67108865L)) assertThrows(IllegalArgumentException::class.java) {PhoneNetworkPreferences(rate).validate()}
        val policy=PhoneFileTrafficPolicy()
        try {policy.beforeRequest(request(FileOperation.READ_RANGE,-1),true); fail()} catch(_:StorageFailure) {}
        try {policy.beforeRequest(request(FileOperation.READ_RANGE,32769),true); fail()} catch(_:StorageFailure) {}
        try {policy.beforeRequest(FileRequest.newBuilder().setContent(ByteString.copyFrom(ByteArray(32769))).build(),false); fail()} catch(_:StorageFailure) {}
        policy.configure(PhoneNetworkPreferences(pauseOnMetered=true))
        try {policy.beforeRequest(FileRequest.newBuilder().setOperation(FileOperation.LIST_ROOTS).setContent(ByteString.copyFromUtf8("unexpected content")).build(),true); fail()} catch(_:StorageFailure) {}
    }
    @Test fun unlimitedDoesNotWaitAndLoweringRateDuringWaitIsConservative()=runBlocking {
        var now=0L; var waits=0; lateinit var policy:PhoneFileTrafficPolicy
        policy=PhoneFileTrafficPolicy({now},{duration->now+=duration; waits++; policy.configure(PhoneNetworkPreferences(65536))})
        policy.pace(32768,true); assertEquals(0,waits); policy.configure(PhoneNetworkPreferences(524288)); policy.pace(32768,true); assertTrue(now>=500)
    }
    @Test fun costChangesAndCancellationAreObservedWhilePacingAndNoDebtRemains()=runBlocking {
        var now=0L; lateinit var policy:PhoneFileTrafficPolicy
        policy=PhoneFileTrafficPolicy({now},{now+=it; policy.observe(PhoneNetworkCost.METERED)})
        policy.configure(PhoneNetworkPreferences(65536,true)); policy.observe(PhoneNetworkCost.UNRESTRICTED)
        try {policy.pace(32768,false); fail()} catch(_:StorageFailure) {}
        val entered=CompletableDeferred<Unit>(); policy=PhoneFileTrafficPolicy(wait={entered.complete(Unit); awaitCancellation()}); policy.configure(PhoneNetworkPreferences(65536))
        val job=launch {policy.pace(32768,true)}; entered.await(); job.cancelAndJoin(); policy.configure(PhoneNetworkPreferences()); policy.pace(32768,true)
    }
    @Test fun sendingAndReceivingBudgetsRemainIndependent()=runBlocking {
        val both=CompletableDeferred<Unit>(); val release=CompletableDeferred<Unit>(); var entered=0
        val policy=PhoneFileTrafficPolicy(wait={if(++entered==2) both.complete(Unit); release.await()}); policy.configure(PhoneNetworkPreferences(65536))
        val send=launch {policy.pace(32768,true)}; val receive=launch {policy.pace(32768,false)}
        withTimeout(2000) {both.await()}; send.cancel(); receive.cancel(); release.complete(Unit); joinAll(send,receive)
    }
    @Test fun concurrentPayloadsShareOneRealElapsedBudget()=runBlocking {
        val policy=PhoneFileTrafficPolicy(); policy.configure(PhoneNetworkPreferences(65536)); val start=System.nanoTime()
        (1..4).map {async {policy.pace(32768,true)}}.awaitAll(); assertTrue((System.nanoTime()-start)/1_000_000>=1950)
    }
}
