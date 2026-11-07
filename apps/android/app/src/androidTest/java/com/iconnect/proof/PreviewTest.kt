package com.iconnect.proof

import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.pdf.PdfDocument
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.ByteArrayOutputStream

@RunWith(AndroidJUnit4::class)
class PreviewTest {
    private val context = InstrumentationRegistry.getInstrumentation().targetContext
    @Test fun largeAllowedImageIsDownsampledBeforeReturningPixels()=runBlocking {
        val source=Bitmap.createBitmap(3000,2000,Bitmap.Config.ARGB_8888); source.eraseColor(Color.RED)
        val bytes=try { ByteArrayOutputStream().also { source.compress(Bitmap.CompressFormat.PNG,100,it) }.toByteArray() } finally { source.recycle() }
        val frame=PreviewRenderer(context).render(PreviewInput(PreviewKind.PNG,"large.png",bytes,true))
        try { assertTrue(frame.bitmap.width<=PreviewRules.RENDER_SIDE); assertTrue(frame.bitmap.height<=PreviewRules.RENDER_SIDE); assertEquals(Color.RED,frame.bitmap.getPixel(0,0)) }
        finally { frame.bitmap.recycle() }
    }
    @Test fun excessivePdfPageCountIsRefusedBeforeRendering()=runBlocking {
        val pdf=PdfDocument(); val bytes=try {
            for(index in 0..PreviewRules.MAX_PAGES) { val page=pdf.startPage(PdfDocument.PageInfo.Builder(10,10,index).create()); pdf.finishPage(page) }
            ByteArrayOutputStream().also(pdf::writeTo).toByteArray()
        } finally { pdf.close() }
        assertTrue(runCatching { PreviewRenderer(context).render(PreviewInput(PreviewKind.PDF,"many.pdf",bytes,true)) }.isFailure)
    }
    @Test fun closingPreviewDropsDelayedSourceWorkWithoutPublishingOrStaging()=runBlocking {
        val scope=CoroutineScope(SupervisorJob()+Dispatchers.Main.immediate); val reached=CompletableDeferred<Unit>(); val release=CompletableDeferred<Unit>()
        val bytes="Habari!".toByteArray(); val entry=com.iconnect.protocol.FileEntry.newBuilder().setId(StorageRules.token()).setGrantId(StorageRules.token()).setName("late.txt").setSize(bytes.size.toLong()).setPermissions(3).setVersion("a".repeat(64)).build()
        val preview=PhonePreview(context,scope) {}
        try {
            withContext(Dispatchers.Main) { preview.open(entry,{true}) { request ->
                val reply=com.iconnect.protocol.FileResponse.newBuilder().setRequestId(StorageRules.token()).addEntries(entry)
                if(request.operation==com.iconnect.protocol.FileOperation.READ_RANGE) { reached.complete(Unit); release.await(); reply.setContent(com.google.protobuf.ByteString.copyFrom(bytes)).setEndOfFile(true) }
                reply.build()
            } }
            withTimeout(5000){ reached.await() }; withContext(Dispatchers.Main){ preview.clear() }; release.complete(Unit); delay(100)
            assertEquals("",preview.state.value.name); assertNull(preview.state.value.frame); assertEquals("",preview.state.value.text)
        } finally { scope.cancel() }
    }
    @Test fun cancelledOrTimedOutDecoderIsUnboundAndItsProcessEnds() = runBlocking {
        val source=Bitmap.createBitmap(8,8,Bitmap.Config.ARGB_8888)
        val input=try { PreviewInput(PreviewKind.PNG,"cancel.png",ByteArrayOutputStream().also { source.compress(Bitmap.CompressFormat.PNG,100,it) }.toByteArray(),true) } finally { source.recycle() }
        for(cancel in listOf(false,true)) {
            val started=CompletableDeferred<android.os.IBinder>()
            val renderer=PreviewRenderer(context,android.content.ComponentName(context,BlockedPreviewService::class.java),4000) { started.complete(it) }
            val job=async { renderer.render(input) }; val binder=withTimeoutOrNull(5000) { started.await() }; assertNotNull("Isolated decoder did not bind",binder)
            if(cancel) { job.cancelAndJoin() } else { assertTrue(runCatching { job.await() }.exceptionOrNull() is TimeoutCancellationException) }
            val stopped=withTimeoutOrNull(5000) { while(binder!!.pingBinder()) delay(20); true }; assertEquals("Decoder endpoint remained alive after cancellation/timeout",true,stopped)
            assertTrue(java.io.File(context.cacheDir,"previews").listFiles().orEmpty().isEmpty())
        }
    }
    @Test fun pngAndJpegDecodeOnlyInAnIsolatedProcessAndLeaveNoInputFile() = runBlocking {
        val source=Bitmap.createBitmap(32,16,Bitmap.Config.ARGB_8888); source.eraseColor(Color.RED)
        try {
            for((kind,format) in listOf(PreviewKind.PNG to Bitmap.CompressFormat.PNG,PreviewKind.JPEG to Bitmap.CompressFormat.JPEG)) {
                val bytes=ByteArrayOutputStream().also { assertTrue(source.compress(format,95,it)) }.toByteArray()
                val frame=PreviewRenderer(context).render(PreviewInput(kind,"fixture",bytes,true))
                try { assertEquals(32,frame.bitmap.width); assertEquals(16,frame.bitmap.height); assertEquals(1,frame.pages); assertTrue(frame.workerPid != android.os.Process.myPid())
                    val pixel=frame.bitmap.getPixel(0,0); assertTrue(Color.red(pixel)>240); assertTrue(Color.green(pixel)<20); assertTrue(Color.blue(pixel)<20)
                } finally { frame.bitmap.recycle() }
            }
        } finally { source.recycle() }
        assertTrue(java.io.File(context.cacheDir,"previews").listFiles().orEmpty().isEmpty())
    }
    @Test fun pdfPagesRenderTheirOwnContentAndRejectUnavailablePages() = runBlocking {
        val pdf=PdfDocument()
        try {
            for(index in 0..1) { val page=pdf.startPage(PdfDocument.PageInfo.Builder(200,300,index).create()); page.canvas.drawColor(if(index==0) Color.GREEN else Color.BLUE); pdf.finishPage(page) }
            val bytes=ByteArrayOutputStream().also(pdf::writeTo).toByteArray(); val input=PreviewInput(PreviewKind.PDF,"fixture.pdf",bytes,true)
            for(index in 0..1) { val frame=PreviewRenderer(context).render(input,index)
                try { assertEquals(2,frame.pages); assertEquals(index,frame.page); assertEquals(if(index==0) Color.GREEN else Color.BLUE,frame.bitmap.getPixel(10,10)) }
                finally { frame.bitmap.recycle() }
            }
            assertTrue(runCatching { PreviewRenderer(context).render(input,2) }.isFailure)
        } finally { pdf.close() }
    }
    @Test fun corruptPayloadAndOversizedDimensionsDoNotProduceAnImage() = runBlocking {
        assertTrue(runCatching { PreviewRenderer(context).render(PreviewInput(PreviewKind.PDF,"bad.pdf","%PDF-1.7\nnot a document".toByteArray(),true)) }.isFailure)
        val header=ByteArray(33); byteArrayOf(-119,80,78,71,13,10,26,10).copyInto(header); java.nio.ByteBuffer.wrap(header,8,4).putInt(13); "IHDR".toByteArray().copyInto(header,12)
        java.nio.ByteBuffer.wrap(header,16,8).putInt(Int.MAX_VALUE).putInt(Int.MAX_VALUE)
        assertTrue(runCatching { PreviewRenderer(context).render(PreviewInput(PreviewKind.PNG,"bomb.png",header,true)) }.isFailure)
        assertTrue(java.io.File(context.cacheDir,"previews").listFiles().orEmpty().isEmpty())
    }
}
