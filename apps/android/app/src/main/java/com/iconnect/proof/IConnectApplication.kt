package com.iconnect.proof

import android.app.Application
import android.os.Looper

/** One process-local runtime survives activity recreation; it hosts no unattended availability. */
class IConnectApplication : Application() {
    private var active: PhoneController? = null
    fun controller(): PhoneController {
        check(Looper.myLooper() == Looper.getMainLooper())
        return active ?: PhoneController(this).also { active = it }
    }
    fun existingController(): PhoneController? = active
    internal fun releaseClosed(controller: PhoneController) {
        check(Looper.myLooper() == Looper.getMainLooper() && controller.closed)
        if (active === controller) active = null
    }
}
