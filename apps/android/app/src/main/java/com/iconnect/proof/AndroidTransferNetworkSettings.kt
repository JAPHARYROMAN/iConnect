package com.iconnect.proof

import android.content.Context
import android.system.Os
import android.system.OsConstants
import org.json.JSONObject
import org.json.JSONTokener
import java.io.File
import java.io.RandomAccessFile

data class PhoneTransferNetworkView(val preferences:PhoneNetworkPreferences=PhoneNetworkPreferences(),val cost:PhoneNetworkCost=PhoneNetworkCost.UNKNOWN,
    val issue:String="",val missing:Boolean=false,val canReset:Boolean=false,val preserved:Int=0,val busy:Boolean=false)

internal class AndroidTransferNetworkSettings(context:Context,val policy:PhoneFileTrafficPolicy):AutoCloseable {
    private val gate=Any()
    private val directory=File(context.filesDir,"transfer-network")
    private val path=File(directory,"settings.json")
    private var recovery=StateRecovery(directory)
    private val rootIdentity:String
    private val owner:RandomAccessFile
    private val lease:java.nio.channels.FileLock
    private var version=""
    private var disposed=false
    var preferences=PhoneNetworkPreferences(); private set
    var issue=""; private set
    var missing=false; private set
    init {
        check(directory.mkdir() || directory.isDirectory)
        check(directory.canonicalFile.parentFile==context.filesDir.canonicalFile)
        val root=Os.lstat(directory.path); check(OsConstants.S_ISDIR(root.st_mode)); rootIdentity="${root.st_dev}:${root.st_ino}"
        val lock=File(directory,"owner.lock"); val first=!lock.exists(); if(!first) stamp(lock)
        owner=RandomAccessFile(lock,"rw")
        try {check(stamp(lock)==stamp(Os.fstat(owner.fd))); lease=requireNotNull(owner.channel.tryLock()) {"Transfer network settings are already open."}} catch(e:Exception) {owner.close(); throw e}
        try {
            check(listOf(".new",".bak").none {File(path.path+it).exists()})
            if(path.exists()) {
                val before=stamp(path); val parser=JSONTokener(PrivateJsonFile(path,8192).read()); val json=parser.nextValue() as? JSONObject?:error("Invalid settings")
                check(parser.nextClean()=='\u0000' && stamp(path)==before && json.keys().asSequence().toSet()==setOf("schema","bytesPerSecond","pauseOnMetered"))
                check(integer(json,"schema")==1L && json.get("pauseOnMetered") is Boolean)
                preferences=PhoneNetworkPreferences(integer(json,"bytesPerSecond"),json.getBoolean("pauseOnMetered")).also {it.validate()}; version=before
            } else if(first) save(PhoneNetworkPreferences()) else {missing=true; error("Saved transfer network settings are missing")}
            policy.configure(preferences)
        } catch(_:Exception) {block()}
    }
    private fun integer(json:JSONObject,key:String):Long=when(val value=json.get(key)) {is Int->value.toLong(); is Long->value; else->error("Invalid settings number")}
    private fun stamp(stat:android.system.StructStat):String {check(OsConstants.S_ISREG(stat.st_mode) && stat.st_nlink==1L); return "${stat.st_dev}:${stat.st_ino}:${stat.st_size}:${stat.st_mtim.tv_sec}:${stat.st_mtim.tv_nsec}:${stat.st_ctim.tv_sec}:${stat.st_ctim.tv_nsec}"}
    private fun stamp(file:File)=stamp(Os.lstat(file.path)).also {check(file.canonicalFile.parentFile==directory.canonicalFile)}
    private fun checkRoot() {check(!disposed); val current=Os.lstat(directory.path); check(OsConstants.S_ISDIR(current.st_mode) && "${current.st_dev}:${current.st_ino}"==rootIdentity)}
    private fun block() {issue="Transfer network settings need review. Remote content is paused; local files, receipts and approvals were kept."; if(!recovery.needed()) recovery.add(path,issue); policy.configure(preferences,false)}
    private fun save(value:PhoneNetworkPreferences) {
        try {
            checkRoot(); check(issue.isEmpty()) {issue}; check(listOf(".new",".bak").none {File(path.path+it).exists()})
            if(version.isEmpty()) check(!path.exists()) else check(stamp(path)==version)
            val text=JSONObject().put("schema",1).put("bytesPerSecond",value.bytesPerSecond).put("pauseOnMetered",value.pauseOnMetered).toString()
            PrivateJsonFile(path,8192).write(text); version=stamp(path); preferences=value; policy.configure(value)
        } catch(e:Exception) {block(); throw e}
    }
    fun view()=synchronized(gate) {PhoneTransferNetworkView(preferences,policy.networkCost,issue,missing,missing || recovery.all().let {it.isNotEmpty() && it.all {row->row.canArchive}},recovery.archivedCount())}
    fun set(value:PhoneNetworkPreferences)=synchronized(gate) {value.validate(); save(value)}
    fun reset()=synchronized(gate) {
        checkRoot(); check(view().canReset) {"Restore settings storage access and restart before resetting."}
        if(missing) {check(listOf(path,File(path.path+".new"),File(path.path+".bak")).none {it.exists()}); recovery=StateRecovery(directory); missing=false}
        else recovery.all().toList().forEach {recovery.archive(it.id)}
        version=""; issue=""; save(PhoneNetworkPreferences())
    }
    fun clearPreserved()=synchronized(gate) {checkRoot(); recovery.clearArchives()}
    override fun close()=synchronized(gate) {if(!disposed) {disposed=true; policy.configure(preferences,false); try {lease.release()} finally {owner.close()}}}
}
