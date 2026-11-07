package com.iconnect.proof

import com.google.protobuf.ByteString
import com.iconnect.protocol.*
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import java.nio.ByteBuffer

class PreviewRulesTest {
    private fun png(width:Int,height:Int)=ByteArray(33).also { byteArrayOf(-119,80,78,71,13,10,26,10).copyInto(it); ByteBuffer.wrap(it,8,4).putInt(13); "IHDR".toByteArray().copyInto(it,12); ByteBuffer.wrap(it,16,8).putInt(width).putInt(height) }
    @Test fun imageBoundsAndSignatureAreValidatedBeforeDecoding() {
        assertEquals(32 to 16,PreviewRules.probe(PreviewKind.PNG,png(32,16)))
        for((width,height) in listOf(0 to 1,8193 to 1,8192 to 8192,Int.MAX_VALUE to Int.MAX_VALUE)) assertTrue(runCatching { PreviewRules.probe(PreviewKind.PNG,png(width,height)) }.isFailure)
        assertTrue(runCatching { PreviewRules.probe(PreviewKind.JPEG,ByteArray(300_000){-1}) }.isFailure)
        assertTrue(runCatching { PreviewRules.probe(PreviewKind.PDF,"<html>not PDF</html>".toByteArray()) }.isFailure)
    }
    @Test fun utf8PrefixAllowsOnlyAnIncompleteFinalCharacterAndStripsDisplayControls() {
        val bytes="Habari\u001b!\n".toByteArray()+byteArrayOf(-30,-126)
        assertEquals("Habari!\n",PreviewRules.text(PreviewInput(PreviewKind.TEXT,"file",bytes,false)))
        assertTrue(runCatching { PreviewRules.text(PreviewInput(PreviewKind.TEXT,"file",bytes,true)) }.isFailure)
        assertTrue(runCatching { PreviewRules.text(PreviewInput(PreviewKind.TEXT,"file",byteArrayOf(0),true)) }.isFailure)
    }
    @Test fun sourceRangeEofVersionAndLateMetadataMustAgree()=runBlocking {
        val bytes="Habari!".toByteArray(); val entry=FileEntry.newBuilder().setId(StorageRules.token()).setGrantId(StorageRules.token()).setName("file.txt").setSize(bytes.size.toLong()).setVersion("a".repeat(64)).setPermissions(3).build()
        for(attack in listOf("none","eof","version","late","empty")) {
            var calls=0
            val result=runCatching { PreviewRules.fetch(entry) { request ->
                calls++; val reply=FileResponse.newBuilder().setRequestId(StorageRules.token()).addEntries(entry)
                if(request.operation==FileOperation.READ_RANGE) { reply.setContent(ByteString.copyFrom(bytes)).setEndOfFile(true)
                    when(attack) { "eof" -> reply.setEndOfFile(false); "version" -> reply.setEntries(0,entry.toBuilder().setVersion("b".repeat(64))); "empty" -> reply.clearContent() }
                }
                if(attack=="late" && calls==3) reply.setEntries(0,entry.toBuilder().setName("other.txt"))
                reply.build()
            } }
            if(attack=="none") { assertEquals("Habari!",PreviewRules.text(result.getOrThrow())); assertEquals(3,calls) } else assertTrue(result.isFailure)
        }
    }
    @Test fun largeMediaIsRejectedBeforeAnySourceRequest()=runBlocking {
        val entry=FileEntry.newBuilder().setName("large.pdf").setPermissions(3).setSize(PreviewRules.MAX_BYTES+1L).build(); var requested=false
        assertTrue(runCatching { PreviewRules.fetch(entry) { requested=true; error("Unexpected read") } }.isFailure); assertFalse(requested)
    }
}
