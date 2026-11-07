package com.iconnect.proof

import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import com.google.protobuf.ByteString
import com.iconnect.protocol.Identity
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.Signature
import java.security.spec.ECGenParameterSpec

interface SessionIdentity {
    val publicKey: ByteArray
    val id: String
    fun sign(transcript: ByteArray): ByteArray
}

class LocalIdentity : SessionIdentity {
    private val alias = "iconnect.proof.identity.v1"
    private val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
    init {
        if (!store.containsAlias(alias)) {
            KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, "AndroidKeyStore").apply {
                initialize(KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_SIGN or KeyProperties.PURPOSE_VERIFY)
                    .setAlgorithmParameterSpec(ECGenParameterSpec("secp256r1"))
                    .setDigests(KeyProperties.DIGEST_SHA256).build())
            }.generateKeyPair()
        }
    }
    override val publicKey: ByteArray get() = store.getCertificate(alias).publicKey.encoded
    override val id: String get() = Rules.id(publicKey)
    fun descriptor(name: String): Identity = Identity.newBuilder()
        .setPublicKey(ByteString.copyFrom(publicKey)).setName(name).build()
    override fun sign(transcript: ByteArray): ByteArray = Signature.getInstance("SHA256withECDSA").run {
        initSign(store.getKey(alias, null) as java.security.PrivateKey)
        update(transcript)
        sign()
    }
}
