package com.iconnect.proof

import android.content.Context
import com.iconnect.protocol.FileEntry
import com.iconnect.protocol.FileRequest
import com.iconnect.protocol.FileResponse
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow

data class PhonePreviewState(val name: String = "", val text: String = "", val frame: PreviewFrame? = null,
    val busy: Boolean = false, val error: String = "")

/** One foreground preview, pinned to the requester's original session or local browser revision. */
class PhonePreview(context: Context, private val scope: CoroutineScope, private val changed: (PhonePreviewState) -> Unit) {
    private val renderer=PreviewRenderer(context)
    private val mutable=MutableStateFlow(PhonePreviewState()); val state=mutable.asStateFlow()
    private var cached: PreviewInput? = null
    private var current: () -> Boolean = { false }
    private var job: Job? = null
    private var revision=0
    private fun publish(value: PhonePreviewState) { mutable.value=value; changed(value) }
    fun clear() { revision++; job?.cancel(); job=null; cached?.bytes?.fill(0); cached=null; current={false}; publish(PhonePreviewState()) }
    fun open(entry: FileEntry, available: () -> Boolean, request: suspend (FileRequest) -> FileResponse) {
        clear(); if(!available()) return
        current=available; val at=revision; publish(PhonePreviewState(name=entry.name,busy=true))
        job=scope.launch {
            var fetched: PreviewInput?=null
            try {
                fetched=withTimeout(30_000) { PreviewRules.fetch(entry,request) }
                ensureActive(); if(at!=revision || !current()) return@launch
                cached=fetched
                if(fetched.kind==PreviewKind.TEXT) publish(PhonePreviewState(name=entry.name,text=PreviewRules.text(fetched)))
                else render(fetched,0,at)
            } catch(e: Exception) { failure(at,e) }
            finally { if(fetched !== cached) fetched?.bytes?.fill(0); if(at==revision && current()) publish(mutable.value.copy(busy=false)) }
        }
    }
    fun page(index: Int) {
        val input=cached ?: return; val pages=mutable.value.frame?.pages ?: return
        if(!current() || input.kind!=PreviewKind.PDF || index !in 0 until pages || mutable.value.busy) return
        job?.cancel(); val at=++revision; publish(mutable.value.copy(busy=true,error=""))
        job=scope.launch { try { render(input,index,at) } catch(e: Exception) { failure(at,e) }
            finally { if(at==revision && current()) publish(mutable.value.copy(busy=false)) } }
    }
    private suspend fun render(input: PreviewInput,page: Int,at: Int) {
        val frame=renderer.render(input,page)
        if(at==revision && current()) publish(PhonePreviewState(name=input.name,frame=frame)) else frame.bitmap.recycle()
    }
    private fun failure(at: Int,error: Exception) {
        if(at!=revision || !current()) return
        if(error is CancellationException && error !is TimeoutCancellationException) return
        publish(mutable.value.copy(busy=false,error=if(error is TimeoutCancellationException) "Preview timed out. Save this file to open it separately." else error.message ?: "Preview is unavailable."))
    }
}
