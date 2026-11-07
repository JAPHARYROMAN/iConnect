package com.iconnect.proof

import android.content.Context
import android.system.Os
import android.system.OsConstants
import org.json.JSONObject
import java.io.File
import java.util.concurrent.atomic.AtomicLong

internal data class ReconnectTarget(val peer:String,val epoch:String,val server:String,val stun:String)
internal data class ReconnectOptions(val enabled:Boolean=false,val paused:Boolean=true,val target:ReconnectTarget?=null)

/** Reconnect is optional local intent, never trust or file authority. Any pause-family entry blocks it. */
internal class AndroidReconnect private constructor(private val directory:File,private val ownId:String,private val beforeWrite:(File)->Unit) {
    constructor(context:Context,ownId:String):this(File(context.filesDir,"availability"),ownId,{})
    internal constructor(directory:File,ownId:String,beforeWrite:(File)->Unit={},fixture:Boolean):this(directory,ownId,beforeWrite) {check(fixture)}
    private val gate=Any()
    private val revision=AtomicLong()
    private val path=File(directory,"reconnect.json")
    private val pause=File(directory,"reconnect-paused.json")
    @Volatile var options=ReconnectOptions(); private set
    @Volatile private var allowedRevision=-1L
    val blocked get()=allowedRevision!=revision.get()
    @Volatile var issue=""; private set
    val healthy get()=issue.isEmpty()
    val paused get()=blocked || options.paused || pauseExists()
    init {
        require(ownId.matches(Regex("[a-f0-9]{64}")))
        try {
            if(family(path).any {it.exists()}) {
                val json=JSONObject(PrivateJsonFile(path,8192).read())
                require(json.keys().asSequence().toSet()==setOf("version","identity","enabled","paused","peer","epoch","server","stun"))
                require(json.get("version")==1 && json.get("identity")==ownId && json.get("enabled") is Boolean && json.get("paused") is Boolean)
                val target=ReconnectTarget(text(json,"peer"),text(json,"epoch"),text(json,"server"),text(json,"stun"))
                val present=listOf(target.peer,target.epoch,target.server,target.stun).any {it.isNotEmpty()}
                if(present) validate(target) else require(!json.getBoolean("enabled"))
                options=ReconnectOptions(json.getBoolean("enabled"),json.getBoolean("paused"),target.takeIf {present})
                allowedRevision=if(options.paused || pauseExists()) -1L else revision.get()
            }
        } catch(_:Exception) {issue="Automatic connection settings need review. Manual connections and saved files are separate."}
    }
    val pauseRecorded get()=pauseExists()
    fun allowed(target:ReconnectTarget?)=healthy && !paused && options.enabled && target!=null && options.target==target
    fun blockNow():Long =revision.incrementAndGet()
    fun configure(target:ReconnectTarget?,enabled:Boolean,ticket:Long) = synchronized(gate) {
        check(healthy) {issue}; if(ticket!=revision.get()) return@synchronized
        require(!enabled || target!=null); target?.let(::validate)
        val next=ReconnectOptions(enabled,!enabled,target)
        try {
            write(next)
            if(ticket!=revision.get()) return@synchronized
            if(enabled) clearPause()
            if(ticket==revision.get()) {options=next; allowedRevision=if(enabled) ticket else -1L}
        } catch(error:Exception) {allowedRevision=-1; issue="Automatic connections are blocked because their settings could not be saved. Review or reset these settings."; throw error}
    }
    fun persistPause()=synchronized(gate) {
        allowedRevision=-1
        // The marker is authoritative even if the following options write fails.
        try {
            beforeWrite(pause); PrivateJsonFile(pause,128).write("{\"paused\":true}")
            if(healthy) {val next=options.copy(paused=true); write(next); options=next}
        } catch(error:Exception) {allowedRevision=-1; issue="Paused now, but the saved pause could not be committed. Review automatic connections before restarting."; throw error}
    }
    fun reset(ticket:Long)=synchronized(gate) {
        if(ticket!=revision.get()) return@synchronized
        // Preserve the complete old metadata directory, including unexpected pause entries, without deleting contents.
        if(directory.exists()) {
            val parent=requireNotNull(directory.parentFile)
            check(directory.canonicalFile.parentFile==parent.canonicalFile && OsConstants.S_ISDIR(Os.lstat(directory.path).st_mode))
            val prefix=directory.name+"-recovery-"
            check(parent.listFiles().orEmpty().count {it.name.startsWith(prefix)}<32) {"Review preserved automatic-connection settings before resetting again."}
            java.nio.file.Files.move(directory.toPath(),File(parent,prefix+StorageRules.token()).toPath())
        }
        write(ReconnectOptions()); options=ReconnectOptions(); issue=""; allowedRevision=-1
    }
    private fun write(value:ReconnectOptions) {
        val target=value.target
        val json=JSONObject().put("version",1).put("identity",ownId).put("enabled",value.enabled).put("paused",value.paused)
            .put("peer",target?.peer?:"").put("epoch",target?.epoch?:"").put("server",target?.server?:"").put("stun",target?.stun?:"")
        beforeWrite(path); PrivateJsonFile(path,8192).write(json.toString())
    }
    private fun clearPause() {
        family(pause).forEach {file-> if(file.exists() || java.nio.file.Files.isSymbolicLink(file.toPath())) {
            check(file.canonicalFile.parentFile==directory.canonicalFile && OsConstants.S_ISREG(Os.lstat(file.path).st_mode)) {"The saved pause needs local storage review."}
            check(file.delete()) {"The saved pause could not be cleared."}
        } }
    }
    private fun pauseExists()=family(pause).any {it.exists() || java.nio.file.Files.isSymbolicLink(it.toPath())}
    private fun family(file:File)=listOf(file,File(file.path+".bak"),File(file.path+".new"))
    private fun text(json:JSONObject,key:String)=json.get(key).also {require(it is String)} as String
    private fun validate(target:ReconnectTarget) {
        require(target.peer.matches(Regex("[a-f0-9]{64}")) && Rules.tokenPattern.matches(target.epoch))
        require(target.server.length<=2048 && Rules.server(target.server,BuildConfig.DEBUG).toString()==target.server)
        Rules.stun(target.stun)
    }
}
