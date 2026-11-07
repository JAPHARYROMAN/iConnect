package com.iconnect.proof

import android.os.SystemClock
import android.system.Os
import android.system.OsConstants
import android.system.StructStat
import kotlinx.coroutines.CancellationException
import java.io.File

data class PhoneReceivingPartRemovalReview(val id:String,val files:Int,val bytes:Long)
data class PhoneReceivingPartRemovalProgress(val removed:Int,val reviewedFiles:Int)
data class PhoneUnreferencedReceivingPart(val name:String,val bytes:Long)

/** Owner-local removal of an exact reviewed list. Never adopts a part as live receiving state. */
internal class AndroidReceivingPartRecovery(private val owner:AndroidOwnerPolicy,private val binding:()->String,
    private val inventory:()->List<File>,private val unreferenced:(String)->Boolean,private val removed:(String)->Unit) {
    private data class Entry(val name:String,val path:File,val version:String,val bytes:Long)
    private data class Review(val value:PhoneReceivingPartRemovalReview,val owner:String,val binding:String,val files:List<Entry>,val expires:Long)
    private class Deletion(val review:Review,var cursor:Int=0)
    private var review:Review?=null
    private var deletion:Deletion?=null
    private fun stamp(s:StructStat):String {
        check(OsConstants.S_ISREG(s.st_mode) && s.st_nlink==1L) {"Redirected or linked private parts were kept."}
        return "${s.st_dev}:${s.st_ino}:${s.st_size}:${s.st_mtim.tv_sec}:${s.st_mtim.tv_nsec}:${s.st_ctim.tv_sec}:${s.st_ctim.tv_nsec}"
    }
    private fun actor(allowed:()->Boolean):String {
        check(owner.healthy) {"Review the current phone identity before private-part cleanup."}
        if(!allowed()) throw CancellationException("Open iConnect to continue private-part cleanup.")
        return owner.recoveryStamp
    }
    private fun snapshot():List<Entry> =inventory().sortedBy {it.name}.map {file->
        val stat=Os.lstat(file.path); Entry(file.name,file,stamp(stat),stat.st_size)
    }
    fun progress():PhoneReceivingPartRemovalProgress?=deletion?.let {PhoneReceivingPartRemovalProgress(it.cursor,it.review.files.size)}
    fun inspect(start:Int,allowed:()->Boolean):List<PhoneUnreferencedReceivingPart> {
        actor(allowed); binding(); require(start>=0); val files=snapshot(); require(start<=files.size)
        return files.drop(start).take(50).map {PhoneUnreferencedReceivingPart(it.name,it.bytes)}
    }
    fun review(allowed:()->Boolean):PhoneReceivingPartRemovalReview {
        val actor=actor(allowed); val bound=binding()
        deletion?.let {check(it.review.owner!=actor || it.review.binding!=bound) {"Continue the already reviewed private-part cleanup first."}; deletion=null}
        val files=snapshot(); check(files.isNotEmpty()) {"No unreferenced private receiving parts need removal."}
        val value=PhoneReceivingPartRemovalReview(StorageRules.token(),files.size,files.sumOf {it.bytes})
        review=Review(value,actor,bound,files,SystemClock.elapsedRealtime()+120_000); return value
    }
    fun confirm(value:PhoneReceivingPartRemovalReview,allowed:()->Boolean):Boolean {
        val actor=actor(allowed); val bound=binding(); val pending=review; review=null
        check(pending!=null && pending.value==value && pending.owner==actor && pending.binding==bound && pending.expires>=SystemClock.elapsedRealtime()) {"The private-part review changed or expired. Review the remaining files again."}
        check(snapshot()==pending.files) {"Private batch parts changed after review. Their files were kept."}
        deletion=Deletion(pending); return continueRemoval(allowed)
    }
    fun continueRemoval(allowed:()->Boolean):Boolean {
        val generation=actor(allowed); val bound=binding(); val active=deletion
        if(active==null || active.review.owner!=generation || active.review.binding!=bound) {deletion=null; error("Review the remaining private parts under the current phone identity and receiver context.")}
        try {
            val end=minOf(active.review.files.size,active.cursor+64)
            while(active.cursor<end) {
                check(actor(allowed)==generation)
                val expected=active.review.files[active.cursor]
                check(unreferenced(expected.name)) {"A current receipt now owns this part. It was kept."}
                Os.open(expected.path.path,OsConstants.O_RDONLY or OsConstants.O_NOFOLLOW,0).useOsDescriptor {fd->
                    check(stamp(Os.fstat(fd))==expected.version && stamp(Os.lstat(expected.path.path))==expected.version) {"A reviewed private part changed. Its file was kept."}
                    check(unreferenced(expected.name) && expected.path.delete()) {"The reviewed part could not be removed. Its remaining files were kept."}
                }
                removed(expected.name); active.cursor++
            }
            if(active.cursor<active.review.files.size) return false
            // New parts are never added to this authorization. They remain for a fresh review.
            deletion=null; return true
        } catch(e:CancellationException) {throw e}
        catch(e:Exception) {deletion=null; throw e}
    }
    fun invalidate() {review=null; deletion=null}
}
