package com.iconnect.proof

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.provider.DocumentsContract
import com.iconnect.protocol.FileStatus

data class AndroidStorageGrant(val id: String, val peerId: String, val tree: Uri, val label: String,
    val permissions: Int = StorageRules.READ_ONLY, val exclusions: Set<String> = emptySet(),
    val expiresAt: Long = 0, val enabled: Boolean = true, val trustEpoch: String = "")

/** Provider work precedes the atomic, pairing-generation-bound approval commit. */
class AndroidStorageGrants(private val context: Context, val owner: AndroidOwnerPolicy = AndroidOwnerPolicy(context)) : DocumentAccessPolicy {
    internal var activity:(PhoneActivityEvent)->Unit={}
    internal fun recordActivity(event:PhoneActivityEvent) {try {activity(event)} catch(_:Exception) {}}
    private val access = AndroidSafAccess(context)
    private val lock = owner.gate
    internal val mutations by lazy { AndroidMutations(context, this) }
    private val batchPlansStore = lazy { AndroidBatchPlans(context,this) }
    internal val batchPlans by batchPlansStore
    internal var ordinaryIncoming:AndroidIncomingUploads?=null
    private val incomingVerification=java.util.concurrent.Semaphore(2)
    internal fun reserveIncomingVerification():AutoCloseable? {
        if(!incomingVerification.tryAcquire()) return null
        return object:AutoCloseable {private val released=java.util.concurrent.atomic.AtomicBoolean(false)
            override fun close() {if(released.compareAndSet(false,true)) incomingVerification.release()}}
    }
    init { reconcileAccess() }
    fun all() = owner.allGrants()
    fun approvalCurrent(grant: AndroidStorageGrant) = owner.approvalCurrent(grant)
    fun capturePeer(peer: String) = owner.capture(peer)
    override fun <T> authorized(action: () -> T): T = synchronized(lock) {
        if (!owner.healthy) throw StorageFailure(FileStatus.ACCESS_DENIED, "Phone pairing policy needs repair.")
        action()
    }
    override fun forPeer(peer: String) = owner.activeGrants(peer)
    override fun get(id: String, peer: String) = forPeer(peer).firstOrNull { it.id == id }
        ?: throw StorageFailure(FileStatus.ACCESS_DENIED, "This folder is not approved for the current pairing.")
    fun add(peer: PeerApproval, tree: Uri, offeredFlags: Int, label: String): AndroidStorageGrant {
        check(owner.isCurrent(peer)) { "Pair this computer again before sharing a folder." }
        AndroidSafAccess.validateTree(tree); StorageRules.name(label)
        require(offeredFlags and Intent.FLAG_GRANT_READ_URI_PERMISSION != 0) { "The provider did not grant read access." }
        val document = DocumentsContract.buildDocumentUriUsingTree(tree, DocumentsContract.getTreeDocumentId(tree))
        context.contentResolver.query(document, arrayOf(DocumentsContract.Document.COLUMN_DISPLAY_NAME,
            DocumentsContract.Document.COLUMN_MIME_TYPE), null, null, null)?.use {
            require(it.moveToFirst() && it.getString(1) == DocumentsContract.Document.MIME_TYPE_DIR && StorageRules.visible(it.getString(0), emptySet())) {
                "Choose a normal folder that is safe to share." }
        } ?: error("The selected folder is unavailable.")
        return synchronized(lock) {
            check(owner.isCurrent(peer)) { "This pairing changed while the folder was being selected." }
            val grant = AndroidStorageGrant(StorageRules.token(), peer.id, tree, label, trustEpoch = peer.epoch)
            access.acquire("share:${grant.id}", tree, offeredFlags)
            try { owner.addGrant(peer, grant) }
            catch (e: Exception) { runCatching { access.release("share:${grant.id}") }; throw e }
            grant
        }
    }
    fun update(id: String, permissions: Int, exclusions: Set<String> = emptySet(), expiresAt: Long = 0, enabled: Boolean = true) {
      if(batchPlansStore.isInitialized()) batchPlans.cancelPublishedChecksNow()
      synchronized(lock) {
        require(permissions and StorageRules.SUPPORTED == permissions && exclusions.size <= 128 && expiresAt in 0..StorageRules.MAX_TIMESTAMP)
        exclusions.forEach(StorageRules::name)
        owner.updateGrant(id) { old ->
            if (permissions and (StorageRules.CREATE or StorageRules.WRITE) != 0) require(context.contentResolver.persistedUriPermissions.any { it.uri == old.tree && it.isReadPermission && it.isWritePermission }) {
                "Select this folder again and allow write access in the system picker." }
            old.copy(permissions = permissions, exclusions = exclusions.map { it.lowercase(java.util.Locale.ROOT) }.toSet(), expiresAt = expiresAt, enabled = enabled)
        }
        ordinaryIncoming?.clearPayloadRates()
        if (batchPlansStore.isInitialized()) batchPlans.clearFilePayloadRates()
        Unit
      }
    }
    fun remove(id: String) {
      if(batchPlansStore.isInitialized()) batchPlans.cancelPublishedChecksNow()
      synchronized(lock) {
        owner.removeGrant(id)
        ordinaryIncoming?.clearPayloadRates()
        if (batchPlansStore.isInitialized()) batchPlans.clearFilePayloadRates()
        runCatching { access.release("share:$id") }
        Unit
      }
    }
    fun revokePeer(peer: String) {
        if(batchPlansStore.isInitialized()) batchPlans.cancelPublishedChecksNow()
        val trusted = owner.current(peer)
        if (trusted != null) owner.revoke(setOf(owner.blockPeer(trusted)))
        reconcileAccess()
    }
    fun reconcileAccess() = synchronized(lock) {
        access.reconcile("share:", owner.allGrants().associate { "share:${it.id}" to it.tree }, Intent.FLAG_GRANT_READ_URI_PERMISSION)
    }
}
