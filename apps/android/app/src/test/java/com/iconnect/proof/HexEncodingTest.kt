package com.iconnect.proof

import org.junit.Assert.assertEquals
import org.junit.Test
import java.math.BigInteger
import java.util.Random

class HexEncodingTest {
    private fun expected(bytes:ByteArray)=if(bytes.isEmpty()) "" else BigInteger(1,bytes).toString(16).padStart(bytes.size*2,'0')

    @Test fun allByteValuesAndLeadingZerosHaveFixedWidthLowercaseEncoding() {
        for(bytes in listOf(byteArrayOf(),ByteArray(32),byteArrayOf(0,1,15,16,127,-128,-1),ByteArray(256) {it.toByte()}))
            assertEquals(expected(bytes),Rules.hex(bytes))
    }

    @Test fun deterministicBinaryInputsMatchTheIndependentIntegerRadixConversion() {
        val random=Random(20261109)
        for(size in listOf(1,2,7,16,32,64,127,255,1024)) repeat(32) {
            val bytes=ByteArray(size).also(random::nextBytes)
            assertEquals(expected(bytes),Rules.hex(bytes))
        }
    }
}
