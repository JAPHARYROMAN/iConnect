package com.iconnect.proof

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.google.protobuf.ByteString
import com.iconnect.protocol.*
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class UploadBatchContractTest {
    private fun plan(count:Int=2)=UploadBatchManifest.newBuilder().setCollision(UploadCollision.UPLOAD_KEEP_BOTH).apply {
        addItems(UploadBatchItem.newBuilder().setParent(-1).setName("folder").setIsDirectory(true))
        repeat(count-1) {index->addItems(UploadBatchItem.newBuilder().setParent(0).setName("Habari-文書-😀-$index.txt").setSize(6).setSha256(ByteString.copyFrom(Rules.digest("salama".toByteArray()))))}
    }.build()
    private fun header(value:UploadBatchManifest)=UploadBatchRules.header(MutationRules.newId(),StorageRules.token(),StorageRules.token(),value)
    @Test fun scopedFileRepliesCannotSubstituteBytesOrClaimAnInconsistentPublication() {
        val h=header(plan()); val peer="a".repeat(64); val id=UploadBatchRules.childId(peer,h,1,false)
        val descriptor=UploadInfo.newBuilder().setId(id).setName("file.bin").setSize(6).setSha256(ByteString.copyFrom(Rules.digest("salama".toByteArray()))).build()
        val response=FileResponse.newBuilder().setRequestId(StorageRules.token()).setMutationEpoch(h.epoch).setUpload(descriptor)
            .setBatchItem(UploadBatchItemReceipt.newBuilder().setHeader(h).setIndex(1).setId(id)).build()
        assertEquals(descriptor,UploadBatchRules.checkFileReceipt(peer,h,1,descriptor,response))
        assertThrows(IllegalArgumentException::class.java) {UploadBatchRules.checkFileReceipt(peer,h,1,descriptor,response.toBuilder().setUpload(descriptor.toBuilder().setSha256(ByteString.copyFrom(ByteArray(32)))).build())}
        val done=response.toBuilder().setUpload(descriptor.toBuilder().setOffset(6).setPhase(UploadPhase.UPLOAD_COMPLETED).setFinalName("file (2).bin"))
            .setBatchItem(response.batchItem.toBuilder().setPhase(UploadBatchItemPhase.BATCH_ITEM_SUCCEEDED).setResultingName("file (2).bin")).build()
        assertEquals(done.upload,UploadBatchRules.checkFileReceipt(peer,h,1,descriptor,done))
        assertThrows(IllegalArgumentException::class.java) {UploadBatchRules.checkFileReceipt(peer,h,1,descriptor,done.toBuilder().setBatchItem(done.batchItem.toBuilder().setResultingName("other.bin")).build())}
    }
    @Test fun androidRuntimeMatchesIndependentWireHashAndChildIdentityVector() {
        val vector=JSONObject(InstrumentationRegistry.getInstrumentation().context.assets.open("upload-batches.json").bufferedReader().use {it.readText()})
        val hex=vector.getString("encodedHex"); val bytes=ByteArray(hex.length/2) {hex.substring(it*2,it*2+2).toInt(16).toByte()}
        val manifest=UploadBatchRules.decode(bytes); val header=UploadBatchRules.header(vector.getString("id"),vector.getString("epoch"),vector.getString("grant"),manifest)
        assertEquals(vector.getString("sha256"),Rules.hex(header.sha256.toByteArray())); assertEquals(vector.getString("fileChildId"),UploadBatchRules.childId(vector.getString("peer"),header,1,false))
        assertArrayEquals(bytes,UploadBatchRules.encode(manifest)); assertEquals(6L,header.totalBytes)
    }
    @Test fun androidRuntimeAssemblesAll4096ItemsWithoutAllocatingDeclaredFileBytes() {
        val manifest=plan(4096); val bytes=UploadBatchRules.encode(manifest)
        UploadBatchAssembly(header(manifest)).use {assembly->
            for(at in bytes.indices step StorageRules.RANGE) {val chunk=bytes.copyOfRange(at,minOf(bytes.size,at+StorageRules.RANGE)); assembly.add(at,chunk); assertEquals(assembly.offset,assembly.add(at,chunk))}
            assertEquals(4096,assembly.complete().itemsCount); assertEquals(4095*6L,UploadBatchRules.validate(assembly.complete()))
        }
    }
    @Test fun androidUnicodeCasingAndMalformedPlanRulesMatchWindows() {
        val directory=UploadBatchItem.newBuilder().setParent(0).setIsDirectory(true)
        val duplicate=plan().toBuilder().addItems(directory.setName("\uD801\uDC28.txt")).addItems(directory.setName("\uD801\uDC00.txt")).build()
        assertThrows(IllegalArgumentException::class.java) {UploadBatchRules.validate(duplicate)}
        val distinct=plan().toBuilder().addItems(directory.setName("straße.txt")).addItems(directory.setName("STRASSE.txt")).build()
        assertEquals(6L,UploadBatchRules.validate(distinct))
        val withIssue=distinct.toBuilder().addItems(UploadBatchItem.newBuilder().setParent(0).setName("bad:name").setSize(123).setError("Source cannot be copied.")).build()
        assertEquals(withIssue.itemsCount,UploadBatchRules.decode(UploadBatchRules.encode(withIssue)).itemsCount)
        val malformed=plan().toBuilder().setItems(1,plan().itemsList[1].toBuilder().setName("bad\uD800.txt")).build()
        assertThrows(IllegalArgumentException::class.java) {UploadBatchRules.validate(malformed)}
        val extended=UploadBatchManifest.parseFrom(UploadBatchRules.encode(plan())+byteArrayOf(0x78,1))
        assertThrows(IllegalArgumentException::class.java) {UploadBatchRules.validate(extended)}
    }
    @Test fun scopedChildCannotBecomeAStandaloneOperationAndTamperingEndsAssembly() {
        val manifest=plan(); val header=header(manifest); val child=UploadBatchRules.childId("a".repeat(64),header,1,false)
        assertEquals(0L,MutationRules.created(child)); assertThrows(StorageFailure::class.java) {MutationRules.current(child)}
        val changed=header.toBuilder().setTotalBytes(7).build()
        UploadBatchAssembly(changed).use {assembly->
            assembly.add(0,UploadBatchRules.encode(manifest)); assertThrows(IllegalArgumentException::class.java) {assembly.complete()}
            assertThrows(IllegalStateException::class.java) {assembly.complete()}
        }
    }
    @Test fun malformedWireCannotAllocateMillionsOfRepeatedMessagesOnThePhone() {
        val attack=ByteArray(2_000_000) {if(it%2==0) 10 else 0}
        assertThrows(IllegalArgumentException::class.java) {UploadBatchRules.decode(attack)}
        for(bytes in listOf(byteArrayOf(10,-1,-1,-1,-1,127),byteArrayOf(10,2,18,127),byteArrayOf(10,2,120,1),ByteArray(11) {-1}))
            assertThrows(IllegalArgumentException::class.java) {UploadBatchRules.decode(bytes)}
    }
}
