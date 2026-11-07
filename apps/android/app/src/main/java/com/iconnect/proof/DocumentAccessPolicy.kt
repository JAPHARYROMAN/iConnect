package com.iconnect.proof

/** A file host's authority; local shortcuts and paired-device approvals use separate stores. */
interface DocumentAccessPolicy {
    fun <T> authorized(action: () -> T): T
    fun forPeer(peer: String): List<AndroidStorageGrant>
    fun get(id: String, peer: String): AndroidStorageGrant
    fun visible(grant: AndroidStorageGrant, name: String): Boolean = StorageRules.visible(name, grant.exclusions)
}
