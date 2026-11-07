package com.iconnect.proof

import com.google.protobuf.ByteString
import com.iconnect.protocol.*
import org.junit.Assert.*
import org.junit.Test

class UploadBatchRulesTest {
    @Test fun publishedBatchProofRequiresACompletedSuccessfulParentEvenWhilePending() {
        val h=header(plan())
        for(verified in listOf(false,true)) {
            val response=FileResponse.newBuilder().setRequestId(StorageRules.token()).setMutationEpoch(h.epoch)
                .setBatch(UploadBatchReceipt.newBuilder().setHeader(h).setPhase(UploadBatchPlanPhase.UPLOAD_PLAN_COMPLETED).setEffectsStarted(true)
                    .setManifestOffset(h.encodedLength).setItemOutcomes(ByteString.copyFrom(byteArrayOf(1,1))))
                .setPublishedBatchCheck(PublishedBatchCheck.newBuilder().setChallenge(StorageRules.token()).setVerified(verified)).build()
            PhoneTransferManifests.validateResponse(FileResponse.parseFrom(response.toByteArray()))
            val invalid=listOf(response.toBuilder().setPublishedBatchCheck(response.publishedBatchCheck.toBuilder().clearChallenge()),
                response.toBuilder().clearBatch(),response.toBuilder().setMutationEpoch(StorageRules.token()),
                response.toBuilder().setBatch(response.batch.toBuilder().setPhase(UploadBatchPlanPhase.UPLOAD_PLAN_STOPPED)),
                response.toBuilder().setBatch(response.batch.toBuilder().setItemOutcomes(ByteString.copyFrom(byteArrayOf(1,2)))),
                response.toBuilder().setStatus(FileStatus.CONFLICT))
            invalid.forEach {assertThrows(IllegalArgumentException::class.java) {PhoneTransferManifests.validateResponse(it.build())}}
        }
        assertTrue(UploadBatchRules.isRpc(FileOperation.VERIFY_PUBLISHED_BATCH)); assertFalse(UploadRules.isRpc(FileOperation.VERIFY_PUBLISHED_BATCH))
    }
    @Test fun terminalReceiptsHaveExactOutcomesAndCompletedPlansCannotHideUnresolvedItems() {
        val h=UploadBatchHeader.newBuilder().setId(MutationRules.newId()).setEpoch(StorageRules.token()).setGrantId(StorageRules.token()).setEncodedLength(1).setItemCount(2).setTotalBytes(0).setSha256(ByteString.copyFrom(ByteArray(32))).build()
        val receipt=UploadBatchReceipt.newBuilder().setHeader(h).setPhase(UploadBatchPlanPhase.UPLOAD_PLAN_COMPLETED).setManifestOffset(1).setEffectsStarted(true).setItemOutcomes(ByteString.copyFrom(byteArrayOf(1,3))).build()
        UploadBatchRules.validateReceipt(receipt); assertTrue(UploadBatchRules.terminal(receipt.phase)); assertFalse(UploadBatchRules.terminal(UploadBatchPlanPhase.UPLOAD_PLAN_STOPPING))
        listOf(0,4,5,255).forEach {invalid->assertThrows(IllegalArgumentException::class.java) {UploadBatchRules.validateReceipt(receipt.toBuilder().setItemOutcomes(ByteString.copyFrom(byteArrayOf(invalid.toByte(),1))).build())}}
        assertThrows(IllegalArgumentException::class.java) {UploadBatchRules.validateReceipt(receipt.toBuilder().clearItemOutcomes().build())}
        val stopped=receipt.toBuilder().setPhase(UploadBatchPlanPhase.UPLOAD_PLAN_STOPPED).setItemOutcomes(ByteString.copyFrom(byteArrayOf(0,4))).build(); UploadBatchRules.validateReceipt(stopped)
        assertThrows(IllegalArgumentException::class.java) {UploadBatchRules.validateReceipt(stopped.toBuilder().setPhase(UploadBatchPlanPhase.UPLOAD_PLAN_STOPPING).build())}
    }
    private fun file(index:Int)=UploadBatchItem.newBuilder().setParent(0).setName("Habari-文書-$index.txt").setSize(6)
        .setSha256(ByteString.copyFrom(Rules.digest("salama".toByteArray()))).build()
    private fun plan(count:Int=2):UploadBatchManifest {
        val value=UploadBatchManifest.newBuilder().setCollision(UploadCollision.UPLOAD_KEEP_BOTH)
            .addItems(UploadBatchItem.newBuilder().setParent(-1).setName("folder").setIsDirectory(true))
        for(index in 1 until count) value.addItems(file(index))
        return value.build()
    }
    private fun header(value:UploadBatchManifest)=UploadBatchRules.header(MutationRules.newId(),StorageRules.token(),StorageRules.token(),value)
    @Test fun fileReceiptsBindBytesNamesKindAndPublicationOutcomeToTheOriginalChild() {
        val h=header(plan()); val peer="a".repeat(64)
        val descriptor=UploadInfo.newBuilder().setId(UploadBatchRules.childId(peer,h,1,false)).setName("file.bin").setSize(6).setSha256(ByteString.copyFrom(Rules.digest("salama".toByteArray()))).setCollision(UploadCollision.UPLOAD_KEEP_BOTH).build()
        val response=FileResponse.newBuilder().setRequestId(StorageRules.token()).setMutationEpoch(h.epoch).setUpload(descriptor)
            .setBatchItem(UploadBatchItemReceipt.newBuilder().setHeader(h).setIndex(1).setId(descriptor.id)).build()
        assertEquals(descriptor,UploadBatchRules.checkFileReceipt(peer,h,1,descriptor,response))
        val variants=listOf(response.toBuilder().setUpload(descriptor.toBuilder().setName("changed.bin")),response.toBuilder().setUpload(descriptor.toBuilder().setSize(7)),
            response.toBuilder().setUpload(descriptor.toBuilder().setSha256(ByteString.copyFrom(ByteArray(32)))),response.toBuilder().setUpload(descriptor.toBuilder().setCollision(UploadCollision.UPLOAD_SKIP)),
            response.toBuilder().setBatchItem(response.batchItem.toBuilder().setIsDirectory(true)),response.toBuilder().setBatchItem(response.batchItem.toBuilder().setPhase(UploadBatchItemPhase.BATCH_ITEM_FAILED)),response.toBuilder().clearUpload())
        variants.forEach {assertThrows(IllegalArgumentException::class.java) {UploadBatchRules.checkFileReceipt(peer,h,1,descriptor,it.build())}}
        val done=response.toBuilder().setUpload(descriptor.toBuilder().setOffset(6).setPhase(UploadPhase.UPLOAD_COMPLETED).setFinalName("file (2).bin"))
            .setBatchItem(response.batchItem.toBuilder().setPhase(UploadBatchItemPhase.BATCH_ITEM_SUCCEEDED).setResultingName("file (2).bin")).build()
        assertEquals(done.upload,UploadBatchRules.checkFileReceipt(peer,h,1,descriptor,done))
        assertThrows(IllegalArgumentException::class.java) {UploadBatchRules.checkFileReceipt(peer,h,1,descriptor,done.toBuilder().setBatchItem(done.batchItem.toBuilder().setResultingName("other.bin")).build())}
    }
    @Test fun reservedChildIdsCannotBecomeOrdinaryOperationsWhenTheClockIsReset() {
        val h=header(plan()); val child=UploadBatchRules.childId("a".repeat(64),h,0,true)
        listOf(0L,1L,3_600_000L,System.currentTimeMillis()).forEach {now->
            assertEquals(FileStatus.STALE_REFERENCE,assertThrows(StorageFailure::class.java) {MutationRules.current(child,now)}.status)
        }
    }
    private fun hex(value:String)=ByteArray(value.length/2) {value.substring(it*2,it*2+2).toInt(16).toByte()}
    @Test fun independentlyEncodedManifestAndChildIdMatchTheSharedVector() {
        val text=javaClass.classLoader!!.getResourceAsStream("upload-batches.json")!!.bufferedReader().use {it.readText()}
        val vector=Regex("\"([A-Za-z0-9]+)\": \"([A-Za-z0-9_-]+)\"").findAll(text).associate {it.groupValues[1] to it.groupValues[2]}
        assertEquals(8,vector.size); val encoded=hex(vector.getValue("encodedHex")); val manifest=UploadBatchRules.decode(encoded)
        assertEquals(2,manifest.itemsCount); assertEquals("folder",manifest.itemsList[0].name); assertEquals("Salama.txt",manifest.itemsList[1].name)
        assertEquals(vector.getValue("sourceHash"),Rules.hex(manifest.itemsList[1].sha256.toByteArray()))
        val header=UploadBatchRules.header(vector.getValue("id"),vector.getValue("epoch"),vector.getValue("grant"),manifest)
        assertEquals(vector.getValue("sha256"),Rules.hex(header.sha256.toByteArray())); assertArrayEquals(encoded,UploadBatchRules.encode(manifest))
        assertEquals(vector.getValue("fileChildId"),UploadBatchRules.childId(vector.getValue("peer"),header,1,false))
    }
    @Test fun full4096ItemPlanAssemblesWithIdempotentBoundedChunks() {
        val manifest=plan(4096); val encoded=UploadBatchRules.encode(manifest); val header=header(manifest)
        UploadBatchAssembly(header).use {assembly->
            assertThrows(IllegalArgumentException::class.java) {assembly.complete()}
            assertThrows(IllegalArgumentException::class.java) {assembly.add(StorageRules.RANGE,encoded.copyOfRange(StorageRules.RANGE,StorageRules.RANGE*2))}
            for(at in encoded.indices step StorageRules.RANGE) {
                val chunk=encoded.copyOfRange(at,minOf(at+StorageRules.RANGE,encoded.size))
                assertEquals(at+chunk.size,assembly.add(at,chunk)); assertEquals(assembly.offset,assembly.add(at,chunk))
                val altered=chunk.copyOf().also {it[0]=(it[0].toInt() xor 1).toByte()}
                assertThrows(IllegalArgumentException::class.java) {assembly.add(at,altered)}
            }
            assertEquals(4096,assembly.complete().itemsCount); assertEquals(4095*6L,header.totalBytes)
            assembly.close(); assertThrows(IllegalStateException::class.java) {assembly.complete()}
        }
    }
    @Test fun malformedOrAmbiguousPlansAreRejected() {
        val initial=plan(); val cases=mutableListOf<UploadBatchManifest>()
        cases+=plan(4097)
        cases+=UploadBatchManifest.newBuilder().apply {repeat(129) {addItems(UploadBatchItem.newBuilder().setParent(-1).setName("root$it").setIsDirectory(true))}}.build()
        cases+=UploadBatchManifest.newBuilder().apply {repeat(33) {addItems(UploadBatchItem.newBuilder().setParent(it-1).setName("level$it").setIsDirectory(true))}}.build()
        cases+=initial.toBuilder().setItems(1,file(1).toBuilder().setParent(1)).build()
        cases+=initial.toBuilder().addItems(UploadBatchItem.newBuilder().setParent(1).setName("child").setIsDirectory(true)).build()
        cases+=initial.toBuilder().addItems(file(1).toBuilder().setName(file(1).name.uppercase(java.util.Locale.ROOT))).build()
        cases+=initial.toBuilder().setItems(1,file(1).toBuilder().setName("\uD801\uDC28.txt")).addItems(file(1).toBuilder().setName("\uD801\uDC00.txt")).build()
        cases+=initial.toBuilder().setItems(1,file(1).toBuilder().clearSha256()).build()
        cases+=initial.toBuilder().setItems(0,initial.itemsList[0].toBuilder().setSize(1)).build()
        cases+=initial.toBuilder().setItems(1,file(1).toBuilder().setSize(StorageRules.MAX_TRANSFER_SIZE)).addItems(file(2).toBuilder().setSize(StorageRules.MAX_TRANSFER_SIZE)).build()
        cases+=initial.toBuilder().setItems(1,file(1).toBuilder().setName("bad\uD800.txt")).build()
        cases+=initial.toBuilder().setItems(0,initial.itemsList[0].toBuilder().setError("Source unavailable")).build()
        cases+=initial.toBuilder().setItems(1,file(1).toBuilder().setError("Source unavailable")).build()
        cases+=UploadBatchManifest.newBuilder().apply {repeat(9) {addItems(UploadBatchItem.newBuilder().setParent(it-1).setName("a".repeat(255)).setIsDirectory(true))}}.build()
        listOf("..","bad/name","bad\\name","NUL.txt","bad.").forEach {cases+=initial.toBuilder().setItems(1,file(1).toBuilder().setName(it)).build()}
        cases.forEach {assertThrows(IllegalArgumentException::class.java) {UploadBatchRules.validate(it)}}
    }
    @Test fun exactRootDepthAndLargeFileBoundsPreserveSourceIssues() {
        val roots=UploadBatchManifest.newBuilder().apply {repeat(128) {addItems(UploadBatchItem.newBuilder().setParent(-1).setName("root$it").setIsDirectory(true))}}.build()
        assertEquals(0L,UploadBatchRules.validate(roots))
        val deep=UploadBatchManifest.newBuilder().apply {repeat(32) {addItems(UploadBatchItem.newBuilder().setParent(it-1).setName("level$it").setIsDirectory(true))}}.build()
        assertEquals(0L,UploadBatchRules.validate(deep))
        val manifest=plan().toBuilder().setItems(1,file(1).toBuilder().setSize(StorageRules.MAX_TRANSFER_SIZE))
            .addItems(UploadBatchItem.newBuilder().setParent(0).setName("unavailable:name").setSize(99).setError("Source cannot be copied."))
            .addItems(UploadBatchItem.newBuilder().setParent(0).setName("straße.txt").setIsDirectory(true))
            .addItems(UploadBatchItem.newBuilder().setParent(0).setName("STRASSE.txt").setIsDirectory(true)).build()
        assertEquals(StorageRules.MAX_TRANSFER_SIZE,UploadBatchRules.validate(manifest))
        assertEquals(manifest.itemsCount,UploadBatchRules.decode(UploadBatchRules.encode(manifest)).itemsCount)
    }
    @Test fun encodingLimitsAndUnknownOrNoncanonicalFieldsAreRejected() {
        val oversized=plan(4096).toBuilder()
        for(index in 1 until oversized.itemsCount) oversized.setItems(index,file(index).toBuilder().setName("文".repeat(245)+index).clearSha256().setError("文".repeat(512)))
        assertThrows(IllegalArgumentException::class.java) {UploadBatchRules.validate(oversized.build())}
        val encoded=UploadBatchRules.encode(plan())
        for(bytes in listOf(encoded+byteArrayOf(0x78,1),encoded+byteArrayOf(0x10,1),encoded.copyOf(encoded.size-1),ByteArray(UploadBatchRules.MAX_ENCODED_BYTES+1)))
            assertThrows(IllegalArgumentException::class.java) {UploadBatchRules.decode(bytes)}
    }
    @Test fun inconsistentIntentCannotFinishAndEndsAssembly() {
        val manifest=plan(); val encoded=UploadBatchRules.encode(manifest); val header=header(manifest)
        val altered=listOf(header.toBuilder().setSha256(ByteString.copyFrom(ByteArray(32))),header.toBuilder().setItemCount(header.itemCount+1),header.toBuilder().setTotalBytes(header.totalBytes+1))
        altered.forEach {changed->UploadBatchAssembly(changed.build()).use {assembly->
            assembly.add(0,encoded); assertThrows(IllegalArgumentException::class.java) {assembly.complete()}; assertThrows(IllegalStateException::class.java) {assembly.complete()}
        }}
    }
    @Test fun childOperationsBindTheParentAndCannotEnterStandaloneCreation() {
        val header=header(plan()); val peer="a".repeat(64); val child=UploadBatchRules.childId(peer,header,1,false)
        assertEquals(0L,MutationRules.created(child)); assertThrows(StorageFailure::class.java) {MutationRules.current(child)}
        assertNotEquals(child,UploadBatchRules.childId(peer,header,0,false)); assertNotEquals(child,UploadBatchRules.childId(peer,header,1,true))
        assertNotEquals(child,UploadBatchRules.childId("b".repeat(64),header,1,false))
        for(changed in listOf(header.toBuilder().setId(MutationRules.newId()),header.toBuilder().setEpoch(StorageRules.token()),header.toBuilder().setGrantId(StorageRules.token()),header.toBuilder().setSha256(ByteString.copyFrom(ByteArray(32)))))
            assertNotEquals(child,UploadBatchRules.childId(peer,changed.build(),1,false))
        val alphabet="ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_"
        val alias=header.grantId.dropLast(1)+alphabet[alphabet.indexOf(header.grantId.last()) xor 1]
        assertFalse(UploadBatchRules.canonicalToken(alias)); assertThrows(IllegalArgumentException::class.java) {UploadBatchAssembly(header.toBuilder().setGrantId(alias).build())}
    }
    @Test fun invalidChunksCannotChangeTheCapturedBytes() {
        val manifest=plan(); val encoded=UploadBatchRules.encode(manifest)
        UploadBatchAssembly(header(manifest)).use {assembly->
            assertThrows(IllegalArgumentException::class.java) {assembly.add(0,byteArrayOf())}; assertThrows(IllegalArgumentException::class.java) {assembly.add(-1,encoded)}
            assertThrows(IllegalArgumentException::class.java) {assembly.add(1,encoded)}
            assembly.add(0,encoded); encoded[0]=(encoded[0].toInt() xor 1).toByte(); assertEquals(6L,UploadBatchRules.validate(assembly.complete()))
        }
    }
    @Test fun unknownIntentFieldsAreRejectedBeforeAllocatingAssembly() {
        val encoded=header(plan()).toByteArray()+byteArrayOf(0x78,1)
        assertThrows(IllegalArgumentException::class.java) {UploadBatchAssembly(UploadBatchHeader.parseFrom(encoded))}
    }
    @Test fun repeatedTinyMessagesAndOversizedFieldsAreRejectedBeforeProtobufParsing() {
        val attack=ByteArray(2_000_000) {if(it%2==0) 10 else 0}
        for(encoded in listOf(attack,byteArrayOf(10,-1,-1,-1,-1,127),byteArrayOf(10,2,18,127),byteArrayOf(10,2,120,1),ByteArray(11) {-1}))
            assertThrows(IllegalArgumentException::class.java) {UploadBatchRules.decode(encoded)}
    }
    @Test fun admissionReceiptsCannotChangeTheIntentOrReturnImpossibleProgress() {
        val header=header(plan()); val receipt=UploadBatchReceipt.newBuilder().setHeader(header).setPhase(UploadBatchPlanPhase.UPLOAD_PLAN_READY).setManifestOffset(header.encodedLength).build()
        val response=FileResponse.newBuilder().setRequestId(StorageRules.token()).setMutationEpoch(header.epoch).setBatch(receipt).build()
        assertEquals(receipt,UploadBatchRules.checkReceipt(header,response))
        val variants=mutableListOf<FileResponse>()
        val changed=listOf(header.toBuilder().setId(MutationRules.newId()),header.toBuilder().setEpoch(StorageRules.token()),header.toBuilder().setGrantId(StorageRules.token()),
            header.toBuilder().setSha256(ByteString.copyFrom(ByteArray(32))),header.toBuilder().setItemCount(header.itemCount+1),header.toBuilder().setTotalBytes(header.totalBytes+1))
        changed.forEach {variants+=response.toBuilder().setBatch(receipt.toBuilder().setHeader(it)).build()}
        variants+=response.toBuilder().setMutationEpoch(StorageRules.token()).build()
        variants+=response.toBuilder().setBatch(receipt.toBuilder().setManifestOffset(0)).build()
        variants+=response.toBuilder().setBatch(receipt.toBuilder().setPhase(UploadBatchPlanPhase.UPLOAD_PLAN_CANCELED)).build()
        variants+=response.toBuilder().setBatch(receipt.toBuilder().setPhaseValue(42)).build()
        variants.forEach {assertThrows(IllegalArgumentException::class.java) {UploadBatchRules.checkReceipt(header,it)}}
    }
    @Test fun batchAdmissionIsIndependentOfStandaloneUploadsAndMutations() {
        assertEquals("upload-batches-v1",UploadBatchRules.CAPABILITY)
        for(value in 31..44) {val operation=FileOperation.forNumber(value); assertTrue(UploadBatchRules.isRpc(operation)); assertFalse(UploadRules.isRpc(operation)); assertFalse(MutationRules.isRpc(operation))}
        assertFalse(UploadBatchRules.isRpc(FileOperation.PREPARE_UPLOAD)); assertFalse(UploadBatchRules.isRpc(FileOperation.UNRECOGNIZED))
    }
    @Test fun scopedDirectoryReceiptCannotChangeTheParentIndexKindOrIdentity() {
        val h=header(plan()); val peer="a".repeat(64)
        val receipt=UploadBatchItemReceipt.newBuilder().setHeader(h).setIndex(0).setId(UploadBatchRules.childId(peer,h,0,true)).setIsDirectory(true)
            .setPhase(UploadBatchItemPhase.BATCH_ITEM_SUCCEEDED).setResultingName("folder").build()
        val response=FileResponse.newBuilder().setRequestId(StorageRules.token()).setMutationEpoch(h.epoch).setBatchItem(receipt).build()
        assertEquals(receipt,UploadBatchRules.checkItemReceipt(peer,h,0,true,response))
        val changed=listOf(receipt.toBuilder().setIndex(1),receipt.toBuilder().setIsDirectory(false),receipt.toBuilder().setId(MutationRules.newId()),
            receipt.toBuilder().setHeader(h.toBuilder().setGrantId(StorageRules.token())),receipt.toBuilder().setPhaseValue(42))
        changed.forEach {assertThrows(IllegalArgumentException::class.java) {UploadBatchRules.checkItemReceipt(peer,h,0,true,response.toBuilder().setBatchItem(it).build())}}
        assertThrows(IllegalArgumentException::class.java) {UploadBatchRules.checkItemReceipt("b".repeat(64),h,0,true,response)}
        assertThrows(IllegalArgumentException::class.java) {UploadBatchRules.checkItemReceipt(peer,h,0,true,response.toBuilder().setMutationEpoch(StorageRules.token()).build())}
    }
}
