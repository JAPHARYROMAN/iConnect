package com.iconnect.proof

import com.google.protobuf.ByteString
import com.google.protobuf.CodedOutputStream
import com.google.protobuf.InvalidProtocolBufferException
import com.iconnect.protocol.*
import java.nio.ByteBuffer
import java.security.MessageDigest

/** Portable plan validation. These names alone never authorize provider access. */
object UploadBatchRules {
    const val CAPABILITY="upload-batches-v1"
    const val MAX_PLANS=8
    const val MAX_PEER_PLANS=4
    const val PUBLISHED_CHECK_CAPABILITY="published-batch-check-v1"
    const val PUBLISHED_CHECK_CANCEL_CAPABILITY="published-batch-cancel-v1"
    fun isRpc(operation:FileOperation)=operation==FileOperation.VERIFY_PUBLISHED_BATCH || operation==FileOperation.CANCEL_PUBLISHED_BATCH_CHECK || operation!=FileOperation.UNRECOGNIZED && operation.number in 31..44
    fun hasManifest(phase:UploadBatchPlanPhase)=phase in setOf(UploadBatchPlanPhase.UPLOAD_PLAN_READY,UploadBatchPlanPhase.UPLOAD_PLAN_STOPPING,UploadBatchPlanPhase.UPLOAD_PLAN_STOPPED,UploadBatchPlanPhase.UPLOAD_PLAN_COMPLETED)
    fun terminal(phase:UploadBatchPlanPhase)=phase in setOf(UploadBatchPlanPhase.UPLOAD_PLAN_CANCELED,UploadBatchPlanPhase.UPLOAD_PLAN_STOPPED,UploadBatchPlanPhase.UPLOAD_PLAN_COMPLETED)
    fun validateReceipt(receipt:UploadBatchReceipt) {
        require(receipt.hasHeader()) {"A batch receipt needs its original intent."}; validateHeader(receipt.header)
        require(receipt.phase!=UploadBatchPlanPhase.UNRECOGNIZED && receipt.manifestOffset>=0 && receipt.manifestOffset<=receipt.header.encodedLength &&
            (!hasManifest(receipt.phase) || receipt.manifestOffset==receipt.header.encodedLength) &&
            (receipt.phase!=UploadBatchPlanPhase.UPLOAD_PLAN_CANCELED || receipt.manifestOffset==0) && (!receipt.effectsStarted || hasManifest(receipt.phase)) &&
            (receipt.phase !in setOf(UploadBatchPlanPhase.UPLOAD_PLAN_STOPPING,UploadBatchPlanPhase.UPLOAD_PLAN_STOPPED) || receipt.effectsStarted) &&
            receipt.message.length<=512 && receipt.message.none(Character::isISOControl)) {"Invalid batch admission receipt."}
        validateOutcomes(receipt.phase,receipt.header.itemCount,receipt.itemOutcomes.toByteArray())
    }
    fun validateOutcomes(phase:UploadBatchPlanPhase,count:Int,outcomes:ByteArray) {
        val finished=phase in setOf(UploadBatchPlanPhase.UPLOAD_PLAN_STOPPED,UploadBatchPlanPhase.UPLOAD_PLAN_COMPLETED)
        require(if(finished) outcomes.size==count else outcomes.isEmpty()) {"Invalid batch outcome count."}
        require(outcomes.all {(it.toInt() and 255) in 0..4 && (phase!=UploadBatchPlanPhase.UPLOAD_PLAN_COMPLETED || it.toInt() in 1..3)}) {"A completed batch contains an unresolved item."}
    }
    fun checkReceipt(original:UploadBatchHeader,response:FileResponse):UploadBatchReceipt {
        validateHeader(original); PhoneTransferManifests.validateResponse(response)
        if(response.status!=FileStatus.FILE_STATUS_OK) throw StorageFailure(response.status,response.message.ifEmpty {"The batch admission needs review."})
        require(response.hasBatch() && response.mutationEpoch==original.epoch && response.batch.header==original) {"The receiver changed the original batch intent or history namespace."}
        return response.batch
    }
    fun checkFileReceipt(peer:String,original:UploadBatchHeader,index:Int,descriptor:UploadInfo,response:FileResponse):UploadInfo {
        UploadRules.validate(descriptor); val item=checkItemReceipt(peer,original,index,false,response)
        require(response.hasUpload()); val file=response.upload
        val phase=when(file.phase) {UploadPhase.UPLOAD_COMPLETED->UploadBatchItemPhase.BATCH_ITEM_SUCCEEDED; UploadPhase.UPLOAD_SKIPPED->UploadBatchItemPhase.BATCH_ITEM_SKIPPED
            UploadPhase.UPLOAD_CANCELED->UploadBatchItemPhase.BATCH_ITEM_FAILED; UploadPhase.UPLOAD_REVIEW->UploadBatchItemPhase.BATCH_ITEM_REVIEW; else->UploadBatchItemPhase.BATCH_ITEM_PENDING}
        require(descriptor.id==childId(peer,original,index,false) && file.id==descriptor.id && file.name==descriptor.name && file.size==descriptor.size && file.sha256==descriptor.sha256 &&
            file.collision==descriptor.collision && item.phase==phase && item.resultingName==if(file.phase==UploadPhase.UPLOAD_COMPLETED) file.finalName else "") {"The receiver changed the batch file descriptor or outcome."}
        return file
    }
    fun validateItemReceipt(receipt:UploadBatchItemReceipt) {
        require(receipt.hasHeader()); validateHeader(receipt.header)
        require(receipt.index>=0 && receipt.index<receipt.header.itemCount && canonicalToken(receipt.id) && MutationRules.created(receipt.id)==0L &&
            receipt.phase!=UploadBatchItemPhase.UNRECOGNIZED && receipt.message.length<=512 && receipt.message.none(Character::isISOControl) && receipt.resultingName.length<=255 &&
            (receipt.phase!=UploadBatchItemPhase.BATCH_ITEM_SUCCEEDED || receipt.resultingName.isNotEmpty())) {"Invalid scoped batch child receipt."}
        if(receipt.resultingName.isNotEmpty()) StorageRules.name(receipt.resultingName)
    }
    fun checkItemReceipt(peer:String,original:UploadBatchHeader,index:Int,directory:Boolean,response:FileResponse):UploadBatchItemReceipt {
        validateHeader(original); PhoneTransferManifests.validateResponse(response)
        if(response.status!=FileStatus.FILE_STATUS_OK) throw StorageFailure(response.status,response.message.ifEmpty {"The batch item needs review."})
        require(response.hasBatchItem() && response.mutationEpoch==original.epoch && response.batchItem.header==original && response.batchItem.index==index &&
            response.batchItem.isDirectory==directory && response.batchItem.id==childId(peer,original,index,directory)) {"The receiver changed the scoped batch item binding."}
        return response.batchItem
    }
    const val MAX_ITEMS=4096
    const val MAX_ROOTS=128
    const val MAX_DEPTH=32
    const val MAX_PATH_CHARS=2048
    const val MAX_ENCODED_BYTES=4*1024*1024

    fun canonicalToken(value:String)=Rules.tokenPattern.matches(value) && Rules.b64(Rules.decode(value))==value
    fun validateHeader(header:UploadBatchHeader) {
        require(canonicalToken(header.id) && canonicalToken(header.epoch) && canonicalToken(header.grantId) &&
            MutationRules.created(header.id) in 0..StorageRules.MAX_TIMESTAMP && header.encodedLength in 1..MAX_ENCODED_BYTES &&
            header.itemCount in 1..MAX_ITEMS && header.totalBytes in 0..StorageRules.MAX_TRANSFER_SIZE && header.sha256.size()==32) {"Invalid outgoing-batch intent."}
        val known=UploadBatchHeader.newBuilder().setId(header.id).setEpoch(header.epoch).setGrantId(header.grantId).setEncodedLength(header.encodedLength)
            .setItemCount(header.itemCount).setTotalBytes(header.totalBytes).setSha256(header.sha256).build()
        require(header.toByteArray().contentEquals(known.toByteArray())) {"Unsupported outgoing-batch intent fields."}
    }
    fun validate(manifest:UploadBatchManifest):Long {
        require(manifest.collision!=UploadCollision.UNRECOGNIZED && manifest.itemsCount in 1..MAX_ITEMS) {"Invalid outgoing-batch plan."}
        val depth=IntArray(manifest.itemsCount); val pathChars=IntArray(manifest.itemsCount); val names=mutableSetOf<Pair<Int,String>>(); var roots=0; var bytes=0L
        manifest.itemsList.forEachIndexed {index,item->
            require(item.parent in -1 until index && item.name.length in 1..255 && validText(item.name) && item.name.none(Character::isISOControl) &&
                item.size in 0..StorageRules.MAX_TRANSFER_SIZE && item.error.length<=512 && validText(item.error) && item.error.none(Character::isISOControl) &&
                names.add(item.parent to collisionKey(item.name))) {"Invalid or ambiguous outgoing-batch item."}
            if(item.parent<0) {require(++roots<=MAX_ROOTS) {"Too many selected batch roots."}; depth[index]=1}
            else {
                val parent=manifest.itemsList[item.parent]
                require(parent.isDirectory && parent.error.isEmpty()) {"A batch item has no usable parent directory."}
                depth[index]=depth[item.parent]+1
            }
            pathChars[index]=item.name.length+if(item.parent<0) 0 else pathChars[item.parent]
            require(depth[index]<=MAX_DEPTH && pathChars[index]<=MAX_PATH_CHARS && (!item.isDirectory || item.size==0L && item.sha256.isEmpty) &&
                (item.error.isEmpty() || item.sha256.isEmpty) && (item.isDirectory || item.error.isNotEmpty() || item.sha256.size()==32)) {"Invalid outgoing-batch depth or content descriptor."}
            if(item.error.isEmpty()) {
                try {StorageRules.name(item.name)} catch(e:StorageFailure) {throw IllegalArgumentException("An outgoing-batch name cannot be represented safely.",e)}
                if(!item.isDirectory) {require(item.size<=StorageRules.MAX_TRANSFER_SIZE-bytes) {"The outgoing batch exceeds its aggregate byte limit."}; bytes+=item.size}
            }
        }
        require(manifest.serializedSize<=MAX_ENCODED_BYTES) {"The outgoing-batch manifest exceeds its encoding limit."}
        require(manifest.toByteArray().contentEquals(knownFields(manifest).toByteArray())) {"Unsupported outgoing-batch fields."}
        return bytes
    }
    fun encode(manifest:UploadBatchManifest):ByteArray {validate(manifest); return canonicalBytes(manifest)}
    fun decode(encoded:ByteArray):UploadBatchManifest {
        require(encoded.size in 1..MAX_ENCODED_BYTES) {"Invalid batch encoding length."}
        preflight(encoded)
        val manifest=try {UploadBatchManifest.parseFrom(encoded)} catch(e:InvalidProtocolBufferException) {throw IllegalArgumentException("Invalid batch encoding.",e)}
        validate(manifest)
        require(encoded.contentEquals(canonicalBytes(manifest))) {"Unsupported or noncanonical batch encoding."}
        return manifest
    }
    fun decode(header:UploadBatchHeader,encoded:ByteArray):UploadBatchManifest {
        validateHeader(header)
        require(encoded.size==header.encodedLength && MessageDigest.isEqual(Rules.digest(encoded),header.sha256.toByteArray())) {"The outgoing-batch manifest checksum or length changed."}
        val manifest=decode(encoded)
        require(manifest.itemsCount==header.itemCount && validate(manifest)==header.totalBytes) {"The outgoing-batch manifest changed its declared totals."}
        return manifest
    }
    private class Wire(val bytes:ByteArray,var at:Int=0,val end:Int=bytes.size) {
        fun integer():ULong {
            var value=0uL
            for(shift in 0..63 step 7) {
                require(at<end) {"The batch encoding ended unexpectedly."}; val next=bytes[at++].toInt() and 255
                require(shift!=63 || next and 254==0) {"The batch encoding contains an overflowing integer."}
                value=value or ((next and 127).toULong() shl shift)
                if(next and 128==0) return value
            }
            throw IllegalArgumentException("The batch encoding contains an invalid integer.")
        }
        fun field(limit:Int):Wire {
            val length=integer(); require(length<=limit.toULong() && length<=(end-at).toULong()) {"A batch field exceeds its bounds."}
            val field=Wire(bytes,at,at+length.toInt()); at+=length.toInt(); return field
        }
    }
    // No repeated protobuf objects or strings are allocated before these limits pass.
    private fun preflight(encoded:ByteArray) {
        val wire=Wire(encoded); var items=0; var collisionSeen=false
        while(wire.at<wire.end) {
            val tag=wire.integer()
            if(tag==16uL) {require(!collisionSeen && wire.integer()<=2uL) {"Unsupported batch collision choice."}; collisionSeen=true; continue}
            require(tag==10uL && !collisionSeen && ++items<=MAX_ITEMS) {"Unsupported batch fields or item count."}
            val item=wire.field(2500); var previous=0uL
            while(item.at<item.end) {
                val field=item.integer(); require(field>previous) {"Noncanonical batch item fields."}; previous=field
                when(field) {
                8uL->require(item.integer()<=UInt.MAX_VALUE.toULong()) {"Invalid batch parent."}
                18uL->item.field(255*3)
                24uL->require(item.integer()<=1uL) {"Invalid directory flag."}
                32uL->require(item.integer()<=StorageRules.MAX_TRANSFER_SIZE.toULong()) {"Invalid file size."}
                42uL->item.field(32)
                50uL->item.field(512*3)
                else->throw IllegalArgumentException("Unsupported batch item fields.")
                }
            }
        }
        require(items>0) {"The outgoing batch is empty."}
    }
    fun header(id:String,epoch:String,grant:String,manifest:UploadBatchManifest):UploadBatchHeader {
        val bytes=encode(manifest)
        return UploadBatchHeader.newBuilder().setId(id).setEpoch(epoch).setGrantId(grant).setEncodedLength(bytes.size).setItemCount(manifest.itemsCount)
            .setTotalBytes(validate(manifest)).setSha256(ByteString.copyFrom(Rules.digest(bytes))).build().also(::validateHeader)
    }
    fun childId(peer:String,header:UploadBatchHeader,index:Int,directory:Boolean):String {
        validateHeader(header); require(peer.matches(Regex("[a-f0-9]{64}")) && index in 0 until header.itemCount) {"Invalid batch child binding."}
        val hash=MessageDigest.getInstance("SHA-256")
        hash.update("iConnect/upload-batch-child/v1\u0000".toByteArray(Charsets.UTF_8))
        hash.update(ByteArray(32) {peer.substring(it*2,it*2+2).toInt(16).toByte()})
        listOf(header.id,header.epoch,header.grantId).forEach {hash.update(Rules.decode(it))}
        hash.update(header.sha256.toByteArray())
        hash.update(ByteBuffer.allocate(5).putInt(index).put(if(directory) 1.toByte() else 2.toByte()).array())
        val result=hash.digest(); result.fill(0,0,8)
        // Timestamp zero cannot enter ordinary new-operation freshness checks.
        return Rules.b64(result)
    }
    private fun knownFields(source:UploadBatchManifest):UploadBatchManifest {
        val result=UploadBatchManifest.newBuilder().setCollision(source.collision)
        source.itemsList.forEach {result.addItems(UploadBatchItem.newBuilder().setParent(it.parent).setName(it.name).setIsDirectory(it.isDirectory).setSize(it.size).setSha256(it.sha256).setError(it.error))}
        return result.build()
    }
    // A versioned explicit encoder, independent of generated message field order.
    private fun canonicalBytes(manifest:UploadBatchManifest):ByteArray {
        val buffer=java.io.ByteArrayOutputStream(); val output=CodedOutputStream.newInstance(buffer)
        manifest.itemsList.forEach {item->
            val length=(if(item.parent==0) 0 else CodedOutputStream.computeSInt32Size(1,item.parent))+
                (if(item.name.isEmpty()) 0 else CodedOutputStream.computeStringSize(2,item.name))+
                (if(item.isDirectory) CodedOutputStream.computeBoolSize(3,true) else 0)+
                (if(item.size==0L) 0 else CodedOutputStream.computeInt64Size(4,item.size))+
                (if(item.sha256.isEmpty) 0 else CodedOutputStream.computeBytesSize(5,item.sha256))+
                (if(item.error.isEmpty()) 0 else CodedOutputStream.computeStringSize(6,item.error))
            output.writeTag(1,2); output.writeUInt32NoTag(length)
            if(item.parent!=0) output.writeSInt32(1,item.parent)
            if(item.name.isNotEmpty()) output.writeString(2,item.name)
            if(item.isDirectory) output.writeBool(3,true)
            if(item.size!=0L) output.writeInt64(4,item.size)
            if(!item.sha256.isEmpty) output.writeBytes(5,item.sha256)
            if(item.error.isNotEmpty()) output.writeString(6,item.error)
        }
        if(manifest.collision!=UploadCollision.UPLOAD_FAIL) output.writeEnum(2,manifest.collisionValue)
        output.flush(); return buffer.toByteArray()
    }
    private fun collisionKey(name:String):String {
        val result=StringBuilder(); var index=0
        while(index<name.length) {val point=name.codePointAt(index); result.appendCodePoint(Character.toUpperCase(point)); index+=Character.charCount(point)}
        return result.toString()
    }
    private fun validText(value:String):Boolean {
        var index=0
        while(index<value.length) {
            val current=value[index++]
            if(current.isHighSurrogate()) {if(index>=value.length || !value[index++].isLowSurrogate()) return false}
            else if(current.isLowSurrogate()) return false
        }
        return true
    }
}

/** Bounded control-message assembly. Acknowledged offsets are deliberately not durable. */
class UploadBatchAssembly(header:UploadBatchHeader):AutoCloseable {
    private val header=header.also(UploadBatchRules::validateHeader)
    private val bytes=ByteArray(header.encodedLength)
    private var closed=false
    var offset=0
        private set
    fun add(at:Int,chunk:ByteArray):Int {
        check(!closed) {"Batch assembly ended."}
        require(at in bytes.indices && at%StorageRules.RANGE==0 && chunk.size==minOf(StorageRules.RANGE,bytes.size-at) && at<=offset) {"Invalid outgoing-batch chunk or sequence."}
        if(at<offset) {
            require(at+chunk.size<=offset && chunk.indices.all {chunk[it]==bytes[at+it]}) {"A repeated batch chunk changed."}
            return offset
        }
        chunk.copyInto(bytes,at); offset+=chunk.size; return offset
    }
    fun complete():UploadBatchManifest {
        check(!closed) {"Batch assembly ended."}; require(offset==bytes.size) {"The outgoing-batch manifest is incomplete."}
        try {
            return UploadBatchRules.decode(header,bytes)
        } catch(e:Exception) {close(); throw e}
    }
    override fun close() {if(closed) return; closed=true; bytes.fill(0)}
}
