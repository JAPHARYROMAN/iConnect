package com.iconnect.proof

import android.content.Context
import android.content.ContextWrapper
import androidx.test.platform.app.InstrumentationRegistry
import java.io.File
import java.util.concurrent.ConcurrentHashMap

/** One test owns its metadata and preferences; unrelated app recovery is preserved. */
internal class IsolatedStorageContext : ContextWrapper(InstrumentationRegistry.getInstrumentation().targetContext), AutoCloseable {
    private val base=baseContext
    private val prefix="isolated-storage-test-"+StorageRules.token()
    private val root=File(base.filesDir,prefix).apply {check(mkdir())}
    private val preferences=ConcurrentHashMap.newKeySet<String>()
    override fun getFilesDir()=root
    override fun getCacheDir()=File(root,"cache").apply {check(isDirectory || mkdirs())}
    override fun getApplicationContext():Context=this
    override fun getSharedPreferences(name:String,mode:Int)=base.getSharedPreferences(prefix+name,mode).also {preferences.add(prefix+name)}
    override fun close() {
        check(root.canonicalFile.parentFile==base.filesDir.canonicalFile && root.name==prefix)
        preferences.forEach {base.deleteSharedPreferences(it)}
        check(root.deleteRecursively()) {"Generated isolated storage could not be cleaned up."}
    }
}
