package com.iconnect.proof

import androidx.test.ext.junit.runners.AndroidJUnit4
import com.iconnect.protocol.*
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

/** Real provider effects remain distinct from the caller's knowledge of those effects. */
@RunWith(AndroidJUnit4::class)
class MutationActivityTest {
    private suspend fun request(f:IncomingUploadsTest.Fixture,name:String):FileRequest {
        val context=f.call(FileRequest.newBuilder().setOperation(FileOperation.MUTATION_CONTEXT).build())
        return FileRequest.newBuilder().setOperation(FileOperation.CREATE_DIRECTORY).setDestinationId(f.target)
            .setMutationEpoch(context.mutationEpoch).setMutationId(MutationRules.newId()).setName(name).build()
    }
    @Test fun confirmedProviderChangeAndLostReplyPersistDifferentOutcomesWithoutPrivateMetadata()=runBlocking {
        val f=IncomingUploadsTest.Fixture(); val history=PhoneActivity(f.context)
        try {
            f.initialize(); val confirmed=request(f,"Confirmed private folder")
            val attempt=PhoneMutationActivity(confirmed.operation,f.approval.id,history::record)
            attempt.dispatched(confirmed.mutationId,confirmed.name)
            assertEquals(PhoneActivityOutcome.COMPLETED,attempt.replied(f.call(confirmed)))
            attempt.interrupted(true) // A later refresh failure must not change a confirmed result.
            val lost=request(f,"Folder with lost reply")
            val uncertain=PhoneMutationActivity(lost.operation,f.approval.id,history::record)
            uncertain.dispatched(lost.mutationId,lost.name)
            assertEquals(MutationPhase.CHANGE_SUCCEEDED,f.call(lost).mutation.phase)
            uncertain.interrupted(false); uncertain.interrupted(true)
            assertEquals(1,f.children().values.count {it==confirmed.name})
            assertEquals(1,f.children().values.count {it==lost.name})
            history.flush()
            val outcomes=history.state.value.entries.map {it.outcome}
            assertEquals(1,outcomes.count {it==PhoneActivityOutcome.COMPLETED})
            assertEquals(1,outcomes.count {it==PhoneActivityOutcome.REVIEW})
            assertEquals(4,outcomes.size)
            val text=File(f.root,"local-activity/activity.json").readText()
            for(secret in listOf(confirmed.name,lost.name,confirmed.mutationId,lost.mutationId,f.tree.toString())) assertFalse(text.contains(secret))
            history.close()
            PhoneActivityCore(f.context).use {core->assertEquals(outcomes,core.view().entries.map {it.outcome})}
        } finally {history.close(); f.close()}
    }
    @Test fun genuineReceiptCannotConfirmAnotherIntentNameOperationOrStatusAndMissingReceiptsNeedReview()=runBlocking {
        val f=IncomingUploadsTest.Fixture()
        try {
            f.initialize(); val original=request(f,"Receipt binding fixture"); val receipt=f.call(original)
            val other=MutationRules.newId()
            val variants=listOf(
                receipt.toBuilder().setMutation(receipt.mutation.toBuilder().setId(other).setCreatedAt(MutationRules.created(other))).build(),
                receipt.toBuilder().setMutation(receipt.mutation.toBuilder().setRequestedName("Different folder")).build(),
                receipt.toBuilder().setMutation(receipt.mutation.toBuilder().setOperation(FileOperation.RENAME)).build(),
                receipt.toBuilder().setStatus(FileStatus.ACCESS_DENIED).build(),
                receipt.toBuilder().setMutation(receipt.mutation.toBuilder().setPhase(MutationPhase.CHANGE_PENDING)).build(),
                receipt.toBuilder().clearMutation().build())
            for(reply in variants) {
                val events=mutableListOf<PhoneActivityEvent>()
                val attempt=PhoneMutationActivity(original.operation,f.approval.id,events::add)
                attempt.dispatched(original.mutationId,original.name)
                assertEquals(PhoneActivityOutcome.REVIEW,attempt.replied(reply))
                attempt.interrupted(false)
                assertEquals(listOf(PhoneActivityOutcome.REQUESTED,PhoneActivityOutcome.REVIEW),events.map {it.outcome})
            }
            assertEquals(1,f.children().values.count {it==original.name})
        } finally {f.close()}
    }
    @Test fun permissionRefusalAndThrowingObserverCannotChangeProviderAuthorityOrSuccessfulEffects()=runBlocking {
        val f=IncomingUploadsTest.Fixture(StorageRules.READ_ONLY)
        try {
            f.initialize(); val create=request(f,"Permission fixture"); val events=mutableListOf<PhoneActivityEvent>()
            val refused=PhoneMutationActivity(create.operation,"iconnect://private-invalid-peer",events::add)
            refused.dispatched(create.mutationId,create.name)
            assertEquals(PhoneActivityOutcome.REFUSED,refused.replied(f.raw(create)))
            assertFalse(f.children().values.contains(create.name)); assertTrue(events.all {it.peer.isEmpty()})
            f.grants.update(f.grant.id,StorageRules.READ_ONLY or StorageRules.CREATE)
            val allowed=PhoneMutationActivity(create.operation,f.approval.id) {throw IllegalStateException("Generated observer failure")}
            allowed.dispatched(create.mutationId,create.name)
            assertEquals(PhoneActivityOutcome.COMPLETED,allowed.replied(f.call(create)))
            assertEquals(1,f.children().values.count {it==create.name})
        } finally {f.close()}
    }
    @Test fun cancelBeforeDispatchIsCanceledButCancelAfterDispatchCannotClaimNoEffect()=runBlocking {
        val f=IncomingUploadsTest.Fixture()
        try {
            f.initialize(); val create=request(f,"Cancellation fixture"); val events=mutableListOf<PhoneActivityEvent>()
            PhoneMutationActivity(create.operation,f.approval.id,events::add).interrupted(true)
            assertFalse(f.children().values.contains(create.name))
            val sent=PhoneMutationActivity(create.operation,f.approval.id,events::add)
            sent.dispatched(create.mutationId,create.name); f.call(create); sent.interrupted(true)
            assertEquals(listOf(PhoneActivityOutcome.REQUESTED,PhoneActivityOutcome.CANCELED,PhoneActivityOutcome.REQUESTED,PhoneActivityOutcome.REVIEW),events.map {it.outcome})
            assertEquals(1,f.children().values.count {it==create.name})
        } finally {f.close()}
    }
}
