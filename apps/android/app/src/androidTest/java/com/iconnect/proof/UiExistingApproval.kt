package com.iconnect.proof

import android.content.Context
import android.net.Uri
import kotlinx.coroutines.runBlocking

/** A generated read-only approval must survive an unrelated local UI workflow. */
internal class UiExistingApproval(context:Context,tree:Uri,flags:Int) : AutoCloseable {
    private val grants=AndroidStorageGrants(context)
    private val approval=TestPeer.approve(grants.owner,TestPeer.key())
    val grantId=try {grants.add(approval,tree,flags,"Generated existing UI approval").id}
        catch(error:Throwable) {grants.revokePeer(approval.id); runBlocking {grants.batchPlans.shutdown()}; throw error}
    override fun close() {
        try {grants.revokePeer(approval.id)} finally {runBlocking {grants.batchPlans.shutdown()}}
    }
}
