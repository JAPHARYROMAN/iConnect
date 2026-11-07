@file:Suppress("DEPRECATION")
package com.iconnect.proof

import android.content.*
import android.graphics.Bitmap
import android.os.*
import kotlinx.coroutines.*
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.io.File
import java.util.concurrent.atomic.AtomicReference

data class PreviewFrame(val bitmap: Bitmap, val page: Int, val pages: Int, val workerPid: Int)

class PreviewRenderer internal constructor(private val context: Context,
    private val component: ComponentName = ComponentName(context,PreviewRenderService::class.java),
    private val timeoutMillis: Long = 15_000, private val onBound: ((IBinder)->Unit)? = null) {
    suspend fun render(input: PreviewInput, page: Int = 0): PreviewFrame = gate.withLock {
        require(input.kind != PreviewKind.TEXT && page in 0 until PreviewRules.MAX_PAGES); PreviewRules.probe(input.kind,input.bytes)
        val delivery=AtomicReference<PreviewFrame?>(null)
        try { val frame=withContext(Dispatchers.IO) {
            val directory = File(context.cacheDir,"previews"); check(directory.mkdirs() || directory.isDirectory)
            val previous = directory.listFiles()?.take(9).orEmpty(); require(previous.size <= 8) { "Private preview cache needs local cleanup." }
            previous.forEach { file -> if(file.isFile && Regex("\\.iconnect-preview-[A-Za-z0-9_-]{43}\\.bin").matches(file.name)) check(file.delete()) }
            val id = StorageRules.token(); val file = File(directory,".iconnect-preview-$id.bin")
            try {
                file.outputStream().use { it.write(input.bytes) }
                require(timeoutMillis in 1..15_000)
                withTimeout(timeoutMillis) { renderDescriptor(file,id,input.kind,page) }.also { delivery.set(it) }
            } finally { if(file.exists()) check(file.delete()) { "Private preview cache could not be cleared." } }
        }; delivery.compareAndSet(frame,null); frame }
        finally { delivery.getAndSet(null)?.bitmap?.recycle() }
    }
    private suspend fun renderDescriptor(file: File,id: String,kind: PreviewKind,page: Int): PreviewFrame {
        val result = CompletableDeferred<PreviewFrame>(); var remote: Messenger? = null; var bound = false
        val produced = AtomicReference<PreviewFrame?>(null)
        val handler = Handler(Looper.getMainLooper()) { message ->
            var memory: SharedMemory? = null
            try {
                val data = message.data; memory = data.getParcelable("pixels")
                if(result.isCompleted) return@Handler true
                check(message.what == PreviewRenderService.RESULT && data.getString("id") == id)
                check(data.getString("error").isNullOrEmpty()) { data.getString("error").orEmpty() }
                val width=data.getInt("width"); val height=data.getInt("height"); val pages=data.getInt("pages"); val at=data.getInt("page"); val pid=data.getInt("pid")
                check(width in 1..PreviewRules.RENDER_SIDE && height in 1..PreviewRules.RENDER_SIDE && pages in 1..PreviewRules.MAX_PAGES && at == page && at < pages && pid > 0 && pid != Process.myPid())
                check(data.getBoolean("isolated") && !data.getBoolean("network") && !data.getBoolean("appData")) { "Preview isolation could not be verified." }
                val pixels = requireNotNull(memory); check(pixels.size.toLong() == width.toLong()*height*4)
                val buffer = pixels.mapReadOnly(); val bitmap=Bitmap.createBitmap(width,height,Bitmap.Config.ARGB_8888)
                try { bitmap.copyPixelsFromBuffer(buffer) } catch(e: Exception) { bitmap.recycle(); throw e } finally { SharedMemory.unmap(buffer) }
                val frame=PreviewFrame(bitmap,at,pages,pid); produced.set(frame)
                if(!result.complete(frame)) { produced.compareAndSet(frame,null); bitmap.recycle() }
            } catch(e: Exception) { result.completeExceptionally(e) } finally { memory?.close() }
            true
        }
        val receiver=Messenger(handler)
        val connection=object: ServiceConnection {
            override fun onServiceConnected(name: ComponentName,binder: IBinder) {
                if(!result.isActive) return
                try {
                    val messenger=Messenger(binder); remote=messenger
                    onBound?.invoke(binder)
                    ParcelFileDescriptor.open(file,ParcelFileDescriptor.MODE_READ_ONLY).use { descriptor -> messenger.send(Message.obtain(null,PreviewRenderService.RENDER).apply {
                        replyTo=receiver; data=Bundle().apply { putString("id",id); putString("kind",kind.name); putInt("page",page); putParcelable("input",descriptor) }
                    }) }
                } catch(e: Exception) { result.completeExceptionally(e) }
            }
            override fun onServiceDisconnected(name: ComponentName) { result.completeExceptionally(IllegalStateException("The isolated preview process ended.")) }
            override fun onBindingDied(name: ComponentName) { result.completeExceptionally(IllegalStateException("The isolated preview process ended.")) }
            override fun onNullBinding(name: ComponentName) { result.completeExceptionally(IllegalStateException("Preview isolation is unavailable.")) }
        }
        try {
            bound=context.bindIsolatedService(Intent().setComponent(component),Context.BIND_AUTO_CREATE,"preview"+Rules.hex(Rules.decode(id)),context.mainExecutor,connection)
            check(bound) { "Preview isolation is unavailable." }; val frame=result.await(); produced.compareAndSet(frame,null); return frame
        } finally {
            result.cancel()
            produced.getAndSet(null)?.bitmap?.recycle()
            runCatching { remote?.send(Message.obtain(null,PreviewRenderService.CANCEL)) }
            if(bound) runCatching { context.unbindService(connection) }
        }
    }
    companion object { private val gate=Mutex() }
}
