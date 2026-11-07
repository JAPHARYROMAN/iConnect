@file:Suppress("DEPRECATION")
package com.iconnect.proof

import android.Manifest
import android.app.Service
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.Rect
import android.graphics.pdf.PdfRenderer
import android.os.*
import android.system.Os
import android.system.OsConstants
import java.io.File
import java.util.concurrent.Executors
import kotlin.math.max
import kotlin.math.roundToInt

/** Receives one read-only descriptor. This isolated process has no app or network permissions. */
class PreviewRenderService : Service() {
    private val executor = Executors.newSingleThreadExecutor()
    private var used = false
    private val messenger by lazy { Messenger(Handler(Looper.getMainLooper()) { message ->
        if (message.what == CANCEL) { Process.killProcess(Process.myPid()); true }
        else {
            if (!used && message.what == RENDER && Process.isIsolated() && message.sendingUid == applicationInfo.uid) {
                used = true; val values = message.data; val reply = message.replyTo
                executor.execute { render(values, reply) }
            }
            true
        }
    }) }
    override fun onBind(intent: Intent): IBinder = messenger.binder
    override fun onUnbind(intent: Intent): Boolean { Process.killProcess(Process.myPid()); return false }
    override fun onDestroy() { executor.shutdownNow(); Process.killProcess(Process.myPid()) }
    private fun render(values: Bundle, reply: Messenger?) {
        val id = values.getString("id").orEmpty(); var descriptor: ParcelFileDescriptor? = null
        try {
            require(Rules.tokenPattern.matches(id)); descriptor = values.getParcelable("input") ?: error("Missing preview input.")
            val length = descriptor.statSize; require(length in 1..PreviewRules.MAX_BYTES.toLong())
            val kind = PreviewKind.valueOf(values.getString("kind").orEmpty()); require(kind != PreviewKind.TEXT)
            val bytes = ParcelFileDescriptor.AutoCloseInputStream(ParcelFileDescriptor.dup(descriptor.fileDescriptor)).use { stream ->
                ByteArray(length.toInt()).also { output -> var at = 0; while(at < output.size) { val read = stream.read(output, at, output.size-at); check(read > 0); at += read }; check(stream.read() == -1) }
            }
            val expected = PreviewRules.probe(kind, bytes); val pageIndex = values.getInt("page", 0)
            var pageCount = 1
            val bitmap = if(kind == PreviewKind.PDF) {
                Os.lseek(descriptor.fileDescriptor, 0, OsConstants.SEEK_SET)
                val pdf = PdfRenderer(descriptor); descriptor = null
                pdf.use { document ->
                    pageCount = document.pageCount; require(pageCount in 1..PreviewRules.MAX_PAGES && pageIndex in 0 until pageCount)
                    document.openPage(pageIndex).use { page ->
                        require(page.width in 1..16384 && page.height in 1..16384)
                        val scale = PreviewRules.RENDER_SIDE.toDouble() / max(page.width,page.height)
                        Bitmap.createBitmap(max(1,(page.width*scale).roundToInt()),max(1,(page.height*scale).roundToInt()),Bitmap.Config.ARGB_8888).also { output ->
                            output.eraseColor(Color.WHITE); page.render(output,null,null,PdfRenderer.Page.RENDER_MODE_FOR_DISPLAY)
                        }
                    }
                }
            } else {
                require(pageIndex == 0)
                val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }; BitmapFactory.decodeByteArray(bytes,0,bytes.size,bounds)
                require(expected == PreviewRules.dimensions(bounds.outWidth,bounds.outHeight))
                var sample = 1; while(max(bounds.outWidth,bounds.outHeight)/sample > PreviewRules.RENDER_SIDE) sample *= 2
                val decoded = BitmapFactory.decodeByteArray(bytes,0,bytes.size,BitmapFactory.Options().apply { inSampleSize=sample; inPreferredConfig=Bitmap.Config.ARGB_8888; inScaled=false }) ?: error("Image decoding failed.")
                try {
                    val scale = minOf(1.0,PreviewRules.RENDER_SIDE.toDouble()/max(decoded.width,decoded.height))
                    val output = Bitmap.createBitmap(max(1,(decoded.width*scale).roundToInt()),max(1,(decoded.height*scale).roundToInt()),Bitmap.Config.ARGB_8888)
                    Canvas(output).drawBitmap(decoded,null,Rect(0,0,output.width,output.height),Paint(Paint.FILTER_BITMAP_FLAG)); output
                } finally { decoded.recycle() }
            }
            bitmap.useBitmap { output ->
                require(output.width in 1..PreviewRules.RENDER_SIDE && output.height in 1..PreviewRules.RENDER_SIDE && output.rowBytes == output.width*4)
                SharedMemory.create("iConnect preview pixels",output.byteCount).use { memory ->
                    val buffer = memory.mapReadWrite(); try { output.copyPixelsToBuffer(buffer) } finally { SharedMemory.unmap(buffer) }
                    check(memory.setProtect(OsConstants.PROT_READ))
                    reply?.send(Message.obtain(null,RESULT).apply { data=Bundle().apply { putString("id",id); putInt("width",output.width); putInt("height",output.height); putInt("pages",pageCount); putInt("page",pageIndex)
                        putBoolean("isolated",Process.isIsolated()); putBoolean("network",checkPermission(Manifest.permission.INTERNET,Process.myPid(),Process.myUid()) == PackageManager.PERMISSION_GRANTED)
                        putBoolean("appData",File(applicationInfo.dataDir).canRead()); putInt("pid",Process.myPid()); putParcelable("pixels",memory) } })
                }
            }
        } catch (_: Throwable) {
            // Decoder diagnostics and input content do not cross the sandbox boundary.
            runCatching { reply?.send(Message.obtain(null,RESULT).apply { data=Bundle().apply { putString("id",id); putString("error","Preview is unavailable. The file may be malformed, encrypted, or exceed supported limits.") } }) }
        } finally { descriptor?.close() }
    }
    private inline fun <T> Bitmap.useBitmap(block: (Bitmap) -> T): T = try { block(this) } finally { recycle() }
    companion object { const val RENDER=1; const val CANCEL=2; const val RESULT=3 }
}
