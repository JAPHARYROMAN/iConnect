package com.iconnect.proof

import android.app.Service
import android.content.Intent
import android.os.*

/** Debug APK only: simulates a blocked decoder while the service's control thread remains responsive. */
class BlockedPreviewService : Service() {
    private val messenger=Messenger(Handler(Looper.getMainLooper()) { message ->
        if(message.what == PreviewRenderService.CANCEL) Process.killProcess(Process.myPid())
        true
    })
    override fun onBind(intent: Intent): IBinder=messenger.binder
    override fun onUnbind(intent: Intent): Boolean { Process.killProcess(Process.myPid()); return false }
}
