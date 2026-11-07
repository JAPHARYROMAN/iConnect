package com.iconnect.proof

import com.google.protobuf.ByteString
import com.iconnect.protocol.Identity
import com.iconnect.protocol.Pairing
import java.security.KeyPair
import java.security.KeyPairGenerator
import java.security.Signature
import java.security.spec.ECGenParameterSpec

/** Test-APK identities issue both real signatures; production has no approval bypass. */
object TestPeer {
    fun key(): KeyPair = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()
    fun receipt(key: KeyPair, name: String = "Fixture computer"): Pairing {
        val own = LocalIdentity()
        val unsigned = Pairing.newBuilder().setTransactionId(StorageRules.token()).setToken(StorageRules.token())
            .setHost(Identity.newBuilder().setName(name).setPublicKey(ByteString.copyFrom(key.public.encoded)))
            .setGuest(own.descriptor("Fixture phone")).setExpiresAt(System.currentTimeMillis() + 120_000).build()
        val transcript = Rules.pairing(unsigned)
        val signature = Signature.getInstance("SHA256withECDSA").run { initSign(key.private); update(transcript); sign() }
        return unsigned.toBuilder().setHostApproval(ByteString.copyFrom(signature)).setGuestApproval(ByteString.copyFrom(own.sign(transcript))).build()
    }
    fun approve(policy: AndroidOwnerPolicy, key: KeyPair): PeerApproval {
        val attempt = policy.beginPair(key.public.encoded)
        return policy.commitPair(attempt, receipt(key))
    }
}
