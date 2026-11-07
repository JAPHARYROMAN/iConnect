package com.iconnect.proof

import android.content.Context
import android.net.Uri
import android.os.CancellationSignal
import android.provider.DocumentsContract as Docs
import android.system.Os
import android.system.OsConstants
import com.iconnect.protocol.*
import org.json.JSONObject
import java.io.File

/** Called under the owner gate. A pending provider effect is reviewed, never replayed. */
internal class AndroidBatchDirectories(
    private val context:Context,private val grants:AndroidStorageGrants,private val directory:File,
    private val encoded:(AndroidBatchPlans.Row)->ByteArray,
    private val start:(AndroidBatchPlans.Row)->AndroidBatchPlans.Row,
    private val block:(String)->Unit,private val changed:()->Unit
) {
    internal data class Child(val index:Int,val id:String,val name:String,val parent:String,val document:String,val phase:UploadBatchItemPhase,val message:String,val directory:Boolean=true)
    internal data class Attempts(val bits:ByteArray)
    private data class Cached(val stamp:String,val row:AndroidBatchPlans.Row,val manifest:UploadBatchManifest)
    private val cache=linkedMapOf<String,Cached>()
    private val bytes=mutableMapOf<String,Long>()
    fun clear() {cache.clear(); bytes.clear()}
    fun forget(id:String) {cache.remove(id); bytes.remove(id)}
    private fun childFile(id:String,index:Int)=File(directory,"$id.$index.json")
    private fun attemptsFile(id:String)=File(directory,"$id.children.json")
    private fun checksum(row:AndroidBatchPlans.Row,bits:ByteArray)=Rules.hex(Rules.digest(row.header.sha256.toByteArray()+row.header.id.toByteArray(Charsets.US_ASCII)+bits))
    private fun fail(message:String):Nothing {block(message); throw StorageFailure(FileStatus.CONFLICT,message)}
    private fun stamp(file:File):String {
        val stat=Os.lstat(file.path); check(OsConstants.S_ISREG(stat.st_mode) && stat.st_nlink==1L)
        return "${stat.st_dev}:${stat.st_ino}:${stat.st_size}:${stat.st_mtim.tv_sec}:${stat.st_mtim.tv_nsec}:${stat.st_ctim.tv_sec}:${stat.st_ctim.tv_nsec}"
    }
    internal fun manifest(row:AndroidBatchPlans.Row):UploadBatchManifest {
        try {
        val file=File(directory,row.header.id+".json"); val version=stamp(file)
        cache[row.header.id]?.takeIf {it.stamp==version && it.row==row}?.let {return it.manifest}
        val manifest=UploadBatchRules.decode(row.header,encoded(row)); check(stamp(file)==version) {"Batch metadata changed while loading."}
        if(cache.size>=2) cache.remove(cache.keys.first())
        cache[row.header.id]=Cached(version,row,manifest); return manifest
        } catch(e:Exception) {fail("The accepted batch metadata changed or is damaged. Existing provider documents were kept; incoming writes require local review.")}
    }
    internal fun attempted(attempts:Attempts,index:Int)=attempts.bits[index/8].toInt() and (1 shl (index%8))!=0
    internal fun readAttempts(row:AndroidBatchPlans.Row):Attempts? {
        val file=PrivateJsonFile(attemptsFile(row.header.id),4096); if(!file.exists()) return null
        try {
            val value=JSONObject(file.read()); val bits=Rules.decode(value.getString("bits"))
            require(value.getInt("schema")==1 && value.getString("batch")==row.header.id && value.getString("hash")==Rules.hex(row.header.sha256.toByteArray()) &&
                bits.size==(row.header.itemCount+7)/8 && (row.header.itemCount%8==0 || (bits.last().toInt() and 255) ushr (row.header.itemCount%8)==0) && value.getString("checksum")==checksum(row,bits))
            return Attempts(bits)
        } catch(e:Exception) {fail("Batch attempt ownership needs local review. Possible provider documents were kept.")}
    }
    private fun write(file:File,text:String,limit:Int) {
        try {PrivateJsonFile(file,limit).write(text); changed()}
        catch(e:Exception) {block("Batch child progress could not be committed. Existing provider documents were kept."); throw e}
    }
    internal fun saveAttempts(row:AndroidBatchPlans.Row,attempts:Attempts)=write(attemptsFile(row.header.id),JSONObject().put("schema",1).put("batch",row.header.id)
        .put("hash",Rules.hex(row.header.sha256.toByteArray())).put("bits",Rules.b64(attempts.bits)).put("checksum",checksum(row,attempts.bits)).toString(),4096)
    internal fun readChild(row:AndroidBatchPlans.Row,manifest:UploadBatchManifest,index:Int):Child? {
        val file=PrivateJsonFile(childFile(row.header.id,index),CHILD_LIMIT); if(!file.exists()) return null
        try {
        val value=JSONObject(file.read()); val child=Child(value.getInt("index"),value.getString("id"),value.getString("name"),value.getString("parent"),value.getString("document"),
            UploadBatchItemPhase.valueOf(value.getString("phase")),value.getString("message"),value.optBoolean("directory",true))
        require(value.getInt("schema")==1 && child.index==index && index in 0 until manifest.itemsCount && manifest.getItems(index).isDirectory==child.directory && manifest.getItems(index).error.isEmpty() &&
            child.id==UploadBatchRules.childId(row.peer,row.header,index,child.directory) && child.parent.length in 1..4096 && child.parent.none(Character::isISOControl) &&
            child.document.length<=4096 && child.document.none(Character::isISOControl) && child.phase!=UploadBatchItemPhase.UNRECOGNIZED &&
            (child.phase==UploadBatchItemPhase.BATCH_ITEM_SUCCEEDED)==child.document.isNotEmpty() && (child.directory || child.phase==UploadBatchItemPhase.BATCH_ITEM_PENDING && child.name==manifest.getItems(index).name) && child.message.length<=512 && child.message.none(Character::isISOControl))
        StorageRules.name(child.name); return child
        } catch(e:Exception) {fail("A retained batch child receipt is damaged. Provider documents were kept; incoming writes require local review.")}
    }
    internal fun saveChild(row:AndroidBatchPlans.Row,child:Child) {
        val file=childFile(row.header.id,child.index); val text=JSONObject().put("schema",1).put("index",child.index).put("id",child.id).put("name",child.name)
            .put("parent",child.parent).put("document",child.document).put("phase",child.phase.name).put("message",child.message).put("directory",child.directory).toString()
        val size=text.toByteArray(Charsets.UTF_8).size; val total=(bytes[row.header.id]?:0)-file.length()+size
        if(size>CHILD_LIMIT || total>METADATA_LIMIT) throw StorageFailure(FileStatus.RESOURCE_EXHAUSTED,"Batch child metadata storage is full.")
        write(file,text,CHILD_LIMIT); bytes[row.header.id]=total
    }
    fun load(rows:Map<String,AndroidBatchPlans.Row>,paths:List<File>):Map<String,Map<Int,Child>> {
        val retained=linkedMapOf<String,Map<Int,Child>>()
        val groups=linkedMapOf<String,MutableList<Int>>()
        val indexes=rows.mapValues {(_,row)->readAttempts(row).also {index->require(if(row.effectsStarted) index!=null else index==null || index.bits.all {it==0.toByte()})}}
        paths.forEach {file->
            val name=file.name
            if(name=="context.json" || name.endsWith(".json") && UploadBatchRules.canonicalToken(name.removeSuffix(".json"))) return@forEach
            if(name.endsWith(".children.json")) {require(indexes[name.removeSuffix(".children.json")]!=null); return@forEach}
            if(AndroidBatchFileStore.parse(name)!=null) return@forEach
            val parsed=childName(name)?:error("Unexpected batch metadata filename.")
            groups.getOrPut(parsed.first) {mutableListOf()}.add(parsed.second)
        }
        groups.forEach {(id,items)->
            val row=requireNotNull(rows[id]); require(row.effectsStarted && items.size<=row.header.itemCount)
            val manifest=manifest(row); val index=requireNotNull(indexes[id]); val children=items.associateWith {requireNotNull(readChild(row,manifest,it))}
            var size=0L
            children.values.forEach {child->
                val parent=manifest.getItems(child.index).parent
                val expected=if(parent<0) row.target.parents.lastOrNull()?.id?:Docs.getTreeDocumentId(Uri.parse(row.target.tree)) else children[parent]?.takeIf {it.phase==UploadBatchItemPhase.BATCH_ITEM_SUCCEEDED}?.document
                require(expected==child.parent && (child.directory && child.phase!=UploadBatchItemPhase.BATCH_ITEM_SUCCEEDED || attempted(index,child.index)))
                size+=childFile(id,child.index).length(); require(size<=METADATA_LIMIT)
            }
            repeat(row.header.itemCount) {require(!attempted(index,it) || children.containsKey(it))}
            bytes[id]=size
            retained[id]=children
        }
        indexes.forEach {(id,index)->require(index==null || index.bits.all {it==0.toByte()} || groups.containsKey(id))}
        return retained
    }
    internal fun destination(row:AndroidBatchPlans.Row,manifest:UploadBatchManifest,index:Int,signal:CancellationSignal):SafDestination {
        val chain=mutableListOf<Child>(); var parent=manifest.getItems(index).parent
        while(parent>=0) {
            val child=readChild(row,manifest,parent)?.takeIf {it.phase==UploadBatchItemPhase.BATCH_ITEM_SUCCEEDED}
                ?:throw StorageFailure(FileStatus.CONFLICT,"A batch parent has no confirmed creation receipt.")
            chain.add(child); parent=manifest.getItems(parent).parent
        }
        val bindings=row.target.parents.toMutableList(); var parentId=bindings.lastOrNull()?.id?:Docs.getTreeDocumentId(Uri.parse(row.target.tree))
        val grant=grants.get(row.target.grant,row.peer)
        chain.asReversed().forEach {child->
            if(child.parent!=parentId) throw StorageFailure(FileStatus.STALE_REFERENCE,"A batch parent identity changed.")
            if(!grants.visible(grant,child.name)) throw StorageFailure(FileStatus.ACCESS_DENIED,"A created batch parent is now excluded.")
            bindings.add(SafFolderBinding(child.document,child.name)); parentId=child.document
        }
        return SafDestination(context,Uri.parse(row.target.tree),bindings,signal).also(::validateDestination)
    }
    private fun validateDestination(destination:SafDestination) {
        try {destination.validate()}
        catch(e:IllegalStateException) {throw StorageFailure(FileStatus.STALE_REFERENCE,"A recorded batch folder moved, was renamed or is no longer a writable directory. Existing documents were kept.")}
    }
    private fun receipt(row:AndroidBatchPlans.Row,child:Child)=UploadBatchItemReceipt.newBuilder().setHeader(row.header).setIndex(child.index).setId(child.id).setIsDirectory(true)
        .setPhase(child.phase).setResultingName(if(child.phase==UploadBatchItemPhase.BATCH_ITEM_SUCCEEDED) child.name else "").setMessage(child.message).build()
    fun execute(original:AndroidBatchPlans.Row,request:FileRequest,authorize:(AndroidBatchPlans.Row)->Unit,signal:CancellationSignal):UploadBatchItemReceipt {
        require(request.hasBatchItemIndex() && request.batchItemIndex in 0 until original.header.itemCount)
        if(original.phase!=UploadBatchPlanPhase.UPLOAD_PLAN_READY) throw StorageFailure(FileStatus.CONFLICT,"Only an accepted uncanceled batch can create or recover children.")
        var row=original; val manifest=manifest(row); val index=request.batchItemIndex; val item=manifest.getItems(index)
        if(!item.isDirectory || item.error.isNotEmpty()) throw StorageFailure(FileStatus.UNSUPPORTED,"This planned item is not a usable directory.")
        val destination=destination(row,manifest,index,signal); val parent=Docs.getDocumentId(destination.parent); var attempts=readAttempts(row)
        if(row.effectsStarted && attempts==null) fail("The batch attempt index is missing. Provider documents were kept; creation is blocked.")
        var previous=readChild(row,manifest,index)
        if(previous==null && attempts!=null && attempted(attempts,index)) fail("A started batch child lost its receipt. Creation is blocked for local review.")
        if(previous?.phase==UploadBatchItemPhase.BATCH_ITEM_SUCCEEDED && (attempts==null || !attempted(attempts,index))) fail("A successful batch child lost its attempt marker.")
        if(previous!=null) {
            if(previous.parent!=parent) throw StorageFailure(FileStatus.STALE_REFERENCE,"The recorded provider parent changed.")
            if(previous.phase==UploadBatchItemPhase.BATCH_ITEM_PENDING) {previous=previous.copy(phase=UploadBatchItemPhase.BATCH_ITEM_REVIEW,message="This folder may already exist. Review it locally; uncertain creation is never replayed."); saveChild(row,previous)}
            if(previous.phase==UploadBatchItemPhase.BATCH_ITEM_SUCCEEDED) {
                val grant=grants.get(row.target.grant,row.peer); if(!grants.visible(grant,previous.name)) throw StorageFailure(FileStatus.ACCESS_DENIED,"The created folder is now excluded.")
                validateDestination(SafDestination(context,destination.tree,destination.parents+SafFolderBinding(previous.document,previous.name),signal))
            }
            return receipt(row,previous)
        }
        if(request.operation==FileOperation.UPLOAD_BATCH_ITEM_STATUS) throw StorageFailure(FileStatus.NOT_FOUND,"No child receipt exists. Status never creates a folder.")
        if((bytes[row.header.id]?:0)>METADATA_LIMIT-CHILD_LIMIT) throw StorageFailure(FileStatus.RESOURCE_EXHAUSTED,"Batch child receipt capacity is full.")
        val grant=grants.get(row.target.grant,row.peer); if(!grants.visible(grant,item.name)) throw StorageFailure(FileStatus.ACCESS_DENIED,"The planned name is excluded.")
        // Validate the complete potential path before any provider effect.
        SafDestination.validateBindings(destination.parents+SafFolderBinding("pending:"+row.header.id,item.name))
        authorize(row)
        if(!row.effectsStarted) {attempts=attempts?:Attempts(ByteArray((row.header.itemCount+7)/8)); require(attempts.bits.all {it==0.toByte()}); saveAttempts(row,attempts); row=start(row)}
        val existing=destination.children(8192,1024*1024); val blind=grant.permissions and StorageRules.LIST==0
        val collision=existing.values.any {it.equals(item.name,true)}
        val name=if(blind) randomName(item.name) else if(manifest.collision==UploadCollision.UPLOAD_KEEP_BOTH) SafDestination.unusedName(item.name,existing.values,true) else item.name
        var child=Child(index,UploadBatchRules.childId(row.peer,row.header,index,true),name,parent,"",UploadBatchItemPhase.BATCH_ITEM_PENDING,"The provider directory intent is durable.")
        saveChild(row,child)
        attempts!!.bits[index/8]=(attempts.bits[index/8].toInt() or (1 shl (index%8))).toByte(); saveAttempts(row,attempts)
        if(collision && !blind && manifest.collision!=UploadCollision.UPLOAD_KEEP_BOTH) {
            child=child.copy(phase=if(manifest.collision==UploadCollision.UPLOAD_SKIP) UploadBatchItemPhase.BATCH_ITEM_SKIPPED else UploadBatchItemPhase.BATCH_ITEM_FAILED,message="An existing destination was kept. Batch folders are never merged.")
        } else {
            try {
                authorize(row); destination.validate(); signal.throwIfCanceled()
                val uri=requireNotNull(Docs.createDocument(context.contentResolver,destination.parent,Docs.Document.MIME_TYPE_DIR,name))
                signal.throwIfCanceled(); authorize(row)
                val binding=destination.createdFolder(uri,existing)
                require(grants.visible(grant,binding.name) && (manifest.collision==UploadCollision.UPLOAD_KEEP_BOTH || blind || binding.name==name))
                child=child.copy(document=binding.id,name=binding.name,phase=UploadBatchItemPhase.BATCH_ITEM_SUCCEEDED,message="A new provider folder was created and its exact identity retained.")
            } catch(e:Exception) {child=child.copy(phase=UploadBatchItemPhase.BATCH_ITEM_REVIEW,message="Provider creation needs review. A possible folder was kept and will not be created again automatically.")}
        }
        saveChild(row,child); return receipt(row,child)
    }
    internal fun reserveBinding(row:AndroidBatchPlans.Row) {if((bytes[row.header.id]?:0)>METADATA_LIMIT-CHILD_LIMIT) throw StorageFailure(FileStatus.RESOURCE_EXHAUSTED,"Batch child binding storage is full.")}
    internal fun beginFile(row:AndroidBatchPlans.Row):AndroidBatchPlans.Row {
        var attempts=readAttempts(row)
        if(row.effectsStarted) {if(attempts==null) fail("The batch attempt index is missing. Possible copies were kept."); return row}
        attempts=attempts?:Attempts(ByteArray((row.header.itemCount+7)/8)); require(attempts.bits.all {it==0.toByte()}); saveAttempts(row,attempts); return start(row)
    }
    private fun randomName(name:String):String {
        val suffix=" (${StorageRules.token().take(16)})"; var prefix=name.take(255-suffix.length).trimEnd(' ','.')
        if(prefix.lastOrNull()?.isHighSurrogate()==true) prefix=prefix.dropLast(1)
        return prefix+suffix
    }
    companion object {
        const val MAX_FILES=1+AndroidBatchPlans.MAX_DETAILED_PLANS*(UploadBatchRules.MAX_ITEMS*2+2)+AndroidBatchPlans.MAX_RETIRED_PLANS
        private const val CHILD_LIMIT=32*1024
        private const val METADATA_LIMIT=4*1024*1024
        private fun childName(name:String):Pair<String,Int>? {
            if(!name.endsWith(".json")) return null
            val stem=name.removeSuffix(".json"); val split=stem.indexOf('.'); if(split<0) return null
            val id=stem.take(split); val text=stem.drop(split+1); val index=text.toIntOrNull()?:return null
            return if(UploadBatchRules.canonicalToken(id) && index in 0 until UploadBatchRules.MAX_ITEMS && text==index.toString()) id to index else null
        }
        fun metadataName(name:String)=name=="context.json" || name.endsWith(".json") && UploadBatchRules.canonicalToken(name.removeSuffix(".json")) ||
            name.endsWith(".children.json") && UploadBatchRules.canonicalToken(name.removeSuffix(".children.json")) || childName(name)!=null || AndroidBatchFileStore.parse(name)!=null
    }
}
