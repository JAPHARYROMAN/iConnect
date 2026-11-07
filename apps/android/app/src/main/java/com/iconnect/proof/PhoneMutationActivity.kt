package com.iconnect.proof

import com.iconnect.protocol.*

/** Observes one existing file-change intent. It never sends or retries an operation. */
internal class PhoneMutationActivity(private val operation:FileOperation,peer:String,private val sink:(PhoneActivityEvent)->Unit) {
    private val peer=peer.takeIf {it.matches(Regex("[a-f0-9]{64}"))}?:""
    private var sent=false
    private var finished=false
    private var id=""
    private var requestedName=""
    init {emit(PhoneActivityOutcome.REQUESTED)}
    private fun emit(outcome:PhoneActivityOutcome)=recordPhoneActivity(sink,PhoneActivityEvent(PhoneActivityKind.FILE_CHANGE,outcome,peer,
        operation=if(operation==FileOperation.UNRECOGNIZED) -1 else operation.number))
    fun dispatched(id:String,name:String="") {check(!finished && !sent); sent=true; this.id=id; requestedName=name}
    fun replied(reply:FileResponse):PhoneActivityOutcome {
        check(!finished)
        val hasMutation=reply.hasMutation(); val mutation=reply.mutation
        val valid=hasMutation && sent && mutation.id==id && mutation.operation==operation && mutation.status==reply.status &&
            (requestedName.isEmpty() || mutation.requestedName==requestedName) && runCatching {MutationRules.validate(mutation)}.isSuccess
        val outcome=when {
            hasMutation && !valid->PhoneActivityOutcome.REVIEW
            valid && mutation.phase==MutationPhase.CHANGE_SUCCEEDED && reply.status==FileStatus.FILE_STATUS_OK->PhoneActivityOutcome.COMPLETED
            hasMutation && mutation.phase in setOf(MutationPhase.CHANGE_PENDING,MutationPhase.CHANGE_REVIEW)->PhoneActivityOutcome.REVIEW
            reply.status in setOf(FileStatus.ACCESS_DENIED,FileStatus.UNSUPPORTED)->PhoneActivityOutcome.REFUSED
            reply.status!=FileStatus.FILE_STATUS_OK || hasMutation && mutation.phase==MutationPhase.CHANGE_FAILED->PhoneActivityOutcome.FAILED
            else->PhoneActivityOutcome.REVIEW
        }
        finish(outcome); return outcome
    }
    fun interrupted(canceled:Boolean) {if(!finished) finish(if(sent) PhoneActivityOutcome.REVIEW else if(canceled) PhoneActivityOutcome.CANCELED else PhoneActivityOutcome.FAILED)}
    private fun finish(outcome:PhoneActivityOutcome) {finished=true; id=""; requestedName=""; emit(outcome)}
}
