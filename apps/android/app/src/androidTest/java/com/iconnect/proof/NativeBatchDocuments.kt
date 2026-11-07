package com.iconnect.proof

import android.content.ContentResolver
import android.net.Uri
import android.provider.DocumentsContract as Docs
import java.security.MessageDigest
import java.security.SecureRandom

/** Generated, separately owned provider trees. Payloads never use the control channel. */
internal class NativeBatchDocuments(private val resolver:ContentResolver,val sourceTree:Uri,val incomingTree:Uri) {
    data class Summary(val hash:String,val items:Int,val bytes:Long)
    companion object {
        const val FOLDER="Native folder"
        const val TEXT="Generated nested text.\nSalama! 文書\n"
        const val UNICODE="Habari-文書.txt"
        const val PAYLOAD=2*1024*1024+17
        val BYTES=PAYLOAD+TEXT.toByteArray(Charsets.UTF_8).size
        val NAMES=listOf(FOLDER,"Empty","Reports",UNICODE,"payload.bin","zero.bin")
        val PARENTS=listOf(-1,0,0,2,0,0)
    }
    private val existing="Generated existing owner file; keep these bytes."
    private fun root(tree:Uri)=Docs.getTreeDocumentId(tree)
    private fun uri(tree:Uri,id:String)=Docs.buildDocumentUriUsingTree(tree,id)
    fun children(tree:Uri,id:String=root(tree)):Map<String,String> {
        val rows=linkedMapOf<String,String>()
        requireNotNull(resolver.query(Docs.buildChildDocumentsUriUsingTree(tree,id),arrayOf(Docs.Document.COLUMN_DOCUMENT_ID,Docs.Document.COLUMN_DISPLAY_NAME),null,null,null)).use {
            while(it.moveToNext()) {check(rows.size<32); check(rows.put(it.getString(0),it.getString(1))==null)}
        }; return rows
    }
    private fun create(tree:Uri,parent:String,name:String,bytes:ByteArray?=null):String {
        val document=requireNotNull(Docs.createDocument(resolver,uri(tree,parent),if(bytes==null) Docs.Document.MIME_TYPE_DIR else "application/octet-stream",name))
        if(bytes!=null) requireNotNull(resolver.openOutputStream(document,"wt")).use {it.write(bytes)}
        return Docs.getDocumentId(document)
    }
    private fun bytes(tree:Uri,id:String)=requireNotNull(resolver.openInputStream(uri(tree,id))).use {input->
        val output=java.io.ByteArrayOutputStream(); val buffer=ByteArray(32*1024)
        while(true) {val count=input.read(buffer); if(count<0) break; check(output.size()+count<=PAYLOAD); output.write(buffer,0,count)}
        output.toByteArray()
    }
    fun tree(tree:Uri,id:String,names:Map<String,String>?=null):Summary {
        val lines=mutableListOf<Pair<String,String>>(); val seen=mutableSetOf<String>(); var total=0L
        fun walk(document:String,relative:String,depth:Int) {
            check(depth<=8 && seen.add(document) && seen.size<=16)
            val logical=if(names==null) relative else requireNotNull(names[relative]) {"Unexpected saved batch item."}
            val directory=requireNotNull(resolver.query(uri(tree,document),arrayOf(Docs.Document.COLUMN_MIME_TYPE),null,null,null)).use {
                check(it.moveToFirst()); val value=it.getString(0)==Docs.Document.MIME_TYPE_DIR; check(!it.moveToNext()); value
            }
            if(directory) {
                lines.add(logical to "D\t$logical\n")
                children(tree,document).forEach {(child,name)->StorageRules.name(name); walk(child,if(relative.isEmpty()) name else "$relative/$name",depth+1)}
            } else {
                val content=bytes(tree,document); total+=content.size
                lines.add(logical to "F\t$logical\t${content.size}\t${Rules.hex(MessageDigest.getInstance("SHA-256").digest(content))}\n")
            }
        }
        walk(id,"",0)
        val canonical=lines.sortedBy {it.first}.joinToString("") {it.second}.toByteArray(Charsets.UTF_8)
        return Summary(Rules.hex(MessageDigest.getInstance("SHA-256").digest(canonical)),seen.size,total)
    }
    val sourceRoot:String
    val original:Summary
    private val initial:Map<String,String>
    private val initialHashes:Map<String,String>
    init {
        sourceRoot=create(sourceTree,root(sourceTree),FOLDER)
        create(sourceTree,sourceRoot,"Empty"); val reports=create(sourceTree,sourceRoot,"Reports")
        create(sourceTree,reports,UNICODE,TEXT.toByteArray(Charsets.UTF_8))
        create(sourceTree,sourceRoot,"payload.bin",ByteArray(PAYLOAD).also {SecureRandom().nextBytes(it)})
        create(sourceTree,sourceRoot,"zero.bin",ByteArray(0))
        val seed=create(incomingTree,root(incomingTree),FOLDER)
        create(incomingTree,seed,"owner.txt",existing.toByteArray(Charsets.UTF_8))
        original=tree(sourceTree,sourceRoot); check(original.items==6 && original.bytes==BYTES.toLong())
        initial=children(incomingTree); initialHashes=initial.mapValues {tree(incomingTree,it.key).hash}
    }
    fun verifyOriginal() {check(tree(sourceTree,sourceRoot)==original)}
    fun verifyIncoming(receivedNames:List<String>,listing:Boolean):Summary {
        check(receivedNames.size==6)
        val after=children(incomingTree)
        check(after.size==initial.size+1 && initial.all {after[it.key]==it.value && tree(incomingTree,it.key).hash==initialHashes[it.key]})
        val incoming=after.entries.single {it.key !in initial}; check(incoming.value==receivedNames[0])
        val physical=Array(6) {""}; val logical=Array(6) {""}; val ids=Array(6) {""}; val mapping=linkedMapOf<String,String>()
        repeat(6) {index->
            val name=receivedNames[index]; val expected=NAMES[index]; val parent=PARENTS[index]; StorageRules.name(name)
            if(listing && index>0) check(name==expected)
            if(!listing) {
                check(name!=expected)
                val stem=if(index<3) expected else expected.substringBeforeLast('.')
                val extension=if(index<3) "" else "."+expected.substringAfterLast('.')
                check(name.startsWith("$stem (") && name.endsWith(")$extension"))
            }
            ids[index]=if(index==0) incoming.key else children(incomingTree,ids[parent]).entries.single {it.value==name}.key
            physical[index]=if(parent<0) "" else if(physical[parent].isEmpty()) name else "${physical[parent]}/$name"
            logical[index]=if(parent<0) "" else if(logical[parent].isEmpty()) expected else "${logical[parent]}/$expected"
            check(mapping.put(physical[index],logical[index])==null)
        }
        val result=tree(incomingTree,incoming.key,mapping)
        check(result.items==6 && result.bytes==BYTES.toLong() && children(incomingTree,ids[1]).isEmpty())
        check(bytes(incomingTree,ids[3]).toString(Charsets.UTF_8)==TEXT && bytes(incomingTree,ids[5]).isEmpty())
        verifyOriginal(); return result
    }
}
