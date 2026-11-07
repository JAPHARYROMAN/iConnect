package com.iconnect.proof

import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.security.SecureRandom

@RunWith(AndroidJUnit4::class)
class IdentityTest {
    @Test fun keystoreIdentityIsStableAndSignsDerEncodedChallenges() {
        val first = LocalIdentity()
        val second = LocalIdentity()
        assertEquals(first.id, second.id)
        assertArrayEquals(first.publicKey, second.publicKey)
        val transcript = Rules.registration(ByteArray(32).also { SecureRandom().nextBytes(it) })
        val signature = first.sign(transcript)
        assertTrue(Rules.verify(first.publicKey, transcript, signature))
        transcript[transcript.lastIndex] = (transcript.last().toInt() xor 1).toByte()
        assertFalse(Rules.verify(first.publicKey, transcript, signature))
    }
}
