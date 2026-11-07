package com.iconnect.proof

import org.junit.Assert.*
import org.junit.Test

class CopyPayloadEstimatesTest {
    private val m=1048576L
    private fun local()=PhoneDownload("local","","file.bin","node","version",10*m,"tree",
        ticket="ticket",phase=PhoneDownloadPhase.DOWNLOADING,active=true)
    private fun point(row:PhoneDownload,offset:Long,at:Long,run:String="run")=PhonePayloadProgress(row.id,row.peer,row.ticket,row.version,row.size,offset,at,run)
    private fun upload()=PhoneUpload("send","owner","peer","pairing","file.bin",10*m,remote="receiver",remoteEpoch="epoch",phase=PhoneUploadPhase.SENDING,active=true)
    private fun send(row:PhoneUpload,offset:Long,at:Long,run:String="run")=PhoneUploadPayloadProgress(row.id,run,row.remote,row.remoteEpoch,row.size,offset,at)
    @Test fun localLiveBytesRemainSeparateFromTheCheckpointAndHaveNoNetworkCap() {
        val row=local(); val rates=PhoneLocalPayloadRates()
        rates.refresh(listOf(row),mapOf(row.id to point(row,0,0)),0)
        val view=rates.refresh(listOf(row),mapOf(row.id to point(row,2*m,2000)),2000)
        assertEquals(2*m,view.bytes[row.id]); assertEquals(0L,row.offset); val estimate=view.estimates.getValue(row.id)
        assertEquals(m,estimate.bytesPerSecond); assertEquals(8L,estimate.remainingSeconds)
        assertFalse(estimate.describe(PhonePayloadKind.LOCAL_COPY,true).contains("limit"))
    }
    @Test fun rapidLocalResumeUsesANewRunEvenIfTheStoppedStateWasConflated() {
        val row=local(); val rates=PhoneLocalPayloadRates(); rates.refresh(listOf(row),mapOf(row.id to point(row,0,0)),0)
        assertNotNull(rates.refresh(listOf(row),mapOf(row.id to point(row,2*m,2000)),2000).estimates[row.id]?.remainingSeconds)
        assertNull(rates.refresh(listOf(row.copy(offset=2*m)),mapOf(row.id to point(row,2*m,2100,"new-run")),2100).estimates[row.id]?.remainingSeconds)
    }
    @Test fun localWrongOwnerSourceVersionSizeAndTicketNeverSupplyLiveBytes() {
        val row=local(); val rates=PhoneLocalPayloadRates(); val good=point(row,2*m,2000)
        listOf(good.copy(peer="remote"),good.copy(version="other"),good.copy(ticket="other"),good.copy(size=row.size+1),good.copy(id="other")).forEach {p->
            assertTrue(rates.refresh(listOf(row),mapOf(row.id to p),2000).bytes.isEmpty())
        }
    }
    @Test fun localStallPauseAndVerificationClearRateWithoutChangingTheRow() {
        val row=local(); val rates=PhoneLocalPayloadRates(); rates.refresh(listOf(row),mapOf(row.id to point(row,0,0)),0)
        val p=mapOf(row.id to point(row,2*m,2000)); rates.refresh(listOf(row),p,2000)
        assertEquals(PhonePayloadRateState.WAITING,rates.refresh(listOf(row),p,12001).estimates[row.id]?.state)
        assertTrue(rates.refresh(listOf(row.copy(phase=PhoneDownloadPhase.PAUSED)),p,13000).estimates.isEmpty())
        assertTrue(rates.refresh(listOf(row.copy(phase=PhoneDownloadPhase.VERIFYING)),p,14000).bytes.isEmpty()); assertEquals(0L,row.offset)
    }
    @Test fun recordPublicationAheadOfTheLiveFlowDoesNotEraseLocalSamples() {
        val row=local(); val rates=PhoneLocalPayloadRates(); rates.refresh(listOf(row),mapOf(row.id to point(row,0,0)),0)
        val p=mapOf(row.id to point(row,2*m,2000)); rates.refresh(listOf(row),p,2000)
        val value=rates.refresh(listOf(row.copy(offset=3*m)),p,2100)
        assertEquals(3*m,value.bytes[row.id]); assertEquals(m,value.estimates[row.id]?.bytesPerSecond)
    }
    @Test fun sendUsesTheAcknowledgedBaselineAndConfiguredLimit() {
        val row=upload().copy(offset=4*m); val rates=PhoneUploadPayloadRates()
        rates.refresh(listOf(row),mapOf(row.id to send(row,4*m,0)),"connection",65536,0)
        val value=rates.refresh(listOf(row.copy(offset=6*m)),mapOf(row.id to send(row,6*m,2000)),"connection",65536,2000).getValue(row.id)
        assertEquals(m,value.bytesPerSecond); assertEquals(64L,value.remainingSeconds); assertTrue(value.describe(PhonePayloadKind.SENDING,true).contains("configured limit"))
    }
    @Test fun changedSendRunConnectionOrLimitDiscardsOldRates() {
        for(change in 0..2) {
            val row=upload(); val rates=PhoneUploadPayloadRates(); rates.refresh(listOf(row),mapOf(row.id to send(row,0,0)),"connection",0,0)
            val p=send(row,2*m,2000); rates.refresh(listOf(row),mapOf(row.id to p),"connection",0,2000)
            val next=rates.refresh(listOf(row),mapOf(row.id to if(change==0) p.copy(run="new") else p),if(change==1) "new" else "connection",if(change==2) 65536 else 0,2000)
            assertNull(next.getValue(row.id).remainingSeconds)
        }
    }
    @Test fun sendReceiptSubstitutionAndInactiveRowsCannotUseObservations() {
        val row=upload(); val rates=PhoneUploadPayloadRates(); val p=send(row,2*m,2000)
        listOf(p.copy(receiver="other"),p.copy(epoch="other"),p.copy(size=row.size+1)).forEach {
            assertTrue(rates.refresh(listOf(row),mapOf(row.id to it),"connection",0,2000).isEmpty())
        }
        assertTrue(rates.refresh(listOf(row.copy(active=false)),mapOf(row.id to p),"connection",0,2000).isEmpty())
        assertTrue(rates.refresh(listOf(row.copy(phase=PhoneUploadPhase.VERIFYING)),mapOf(row.id to p),"connection",0,2000).isEmpty())
    }
    @Test fun payloadCompletionDoesNotClaimProviderOrReceiverCompletion() {
        val row=local().copy(offset=10*m,sha256="a".repeat(64),phase=PhoneDownloadPhase.READY_TO_SAVE)
        assertTrue(localPayloadText(row,null).contains("Saving to the chosen provider is separate"))
        assertTrue(localPayloadText(row.copy(phase=PhoneDownloadPhase.PUBLISHING),null).contains("Completion time is unavailable"))
        assertTrue(uploadPayloadText(upload().copy(offset=10*m),null,false).contains("Waiting for the receiver"))
    }
}
