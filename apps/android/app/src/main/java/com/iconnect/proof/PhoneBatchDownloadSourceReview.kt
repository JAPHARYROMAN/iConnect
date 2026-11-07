package com.iconnect.proof

import org.json.JSONObject
import java.nio.ByteBuffer
import java.security.MessageDigest

/** Durable intent before child adoption; either descriptor may survive a journal interruption. */
data class PhoneBatchSourceApproval(val child:String,val originalVersion:String,val previousVersion:String,val approvedVersion:String,
    val previousHash:String,val hash:String,val destination:String,val partIdentity:String) {
    fun encode():JSONObject=JSONObject().put("child",child).put("originalVersion",originalVersion).put("previousVersion",previousVersion)
        .put("approvedVersion",approvedVersion).put("previousHash",previousHash).put("hash",hash).put("destination",destination).put("partIdentity",partIdentity)
    fun validate(item:PhoneBatchItem) {
        fun hash(value:String)=value.matches(Regex("[a-f0-9]{64}"))
        require(!item.source.directory && item.source.error.isEmpty() && child==item.child && Rules.tokenPattern.matches(child) && originalVersion==item.source.version &&
            hash(previousVersion) && hash(approvedVersion) && (previousHash.isEmpty() && previousVersion==originalVersion || hash(previousHash)) && hash(this.hash) && hash(destination) &&
            partIdentity.length in 7..256 && partIdentity.split(':').let {it.size==4 && it.all {part->part.toLongOrNull()!=null} && it[1].toLong()!=0L && (it[3].toLong()!=0L || it[2].toLong()==0L)})
    }
    companion object {
        fun decode(row:JSONObject)=PhoneBatchSourceApproval(row.getString("child"),row.getString("originalVersion"),row.getString("previousVersion"),row.getString("approvedVersion"),
            row.getString("previousHash"),row.getString("hash"),row.getString("destination"),row.getString("partIdentity"))
        fun binding(tree:String,parents:List<SafFolderBinding>,collision:PhoneCollision):String {
            val digest=MessageDigest.getInstance("SHA-256")
            fun add(value:String) {val bytes=value.toByteArray(Charsets.UTF_8); digest.update(ByteBuffer.allocate(4).putInt(bytes.size).array()); digest.update(bytes)}
            add("iconnect-receiving-batch-destination-v1"); add(tree); add(parents.size.toString()); parents.forEach {add(it.id); add(it.name)}; add(collision.name)
            return Rules.hex(digest.digest())
        }
        fun binding(file:PhoneDownload)=binding(file.tree,file.parents,file.collision)
        fun unallocated(value:String)=value.isEmpty() || value.split(':').let {it.size==4 && it[2]=="0" && it[3]=="0"}
        fun matchesPart(expected:String,actual:String):Boolean = if(unallocated(expected)) expected.isEmpty() || expected.split(':').take(2)==actual.split(':').take(2) else expected==actual
    }
}

data class PhoneBatchDownloadSourceReview(val id:String,val batch:String,val item:Int,val path:String,val source:PhoneDownloadSourceReview)
