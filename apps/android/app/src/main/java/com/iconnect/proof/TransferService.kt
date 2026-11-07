package com.iconnect.proof

import android.Manifest
import android.app.*
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.content.pm.ServiceInfo
import android.net.Uri
import android.os.Build
import android.os.IBinder
import android.os.PowerManager
import kotlinx.coroutines.*

/** A live, owner-selected transfer only. No boot receiver, sticky restart or unattended reconnect. */
class TransferService : Service() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private var token = ""
    private var wake: PowerManager.WakeLock? = null
    private var observing: Job? = null
    private var limit: Job? = null
    private var foreground = false
    private var lastNotification = 0L
    private val controller get() = (application as IConnectApplication).existingController()
    override fun onCreate() {
        super.onCreate()
        getSystemService(NotificationManager::class.java).createNotificationChannel(NotificationChannel(CHANNEL, "File transfers", NotificationManager.IMPORTANCE_LOW))
        // Fulfil the platform start deadline even when an obsolete intent is rejected below.
        try { startForeground(NOTIFICATION, notification(null), ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC); foreground = true }
        catch (_: Exception) { stopSelf() }
    }
    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val requested = intent?.getStringExtra(TOKEN) ?: ""
        if (!foreground) { controller?.backgroundStartFailed(requested); stopSelf(); return START_NOT_STICKY }
        if (intent?.action == PAUSE) {
            controller?.backgroundStopped(requested, "Transfers paused from notification.")
            if (requested == token) stopSelf()
            return START_NOT_STICKY
        }
        val active = controller
        if (active == null || !active.acceptBackgroundStart(requested)) { if (token.isEmpty()) stopSelf(); return START_NOT_STICKY }
        token = requested
        wake?.let { if (it.isHeld) it.release() }
        try { wake = getSystemService(PowerManager::class.java).newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "iConnect:transfer").apply {
            setReferenceCounted(false); acquire(MAX_RUN_MS)
        } } catch (_: Exception) { active.backgroundStopped(token, "Android could not keep this transfer active. Reopen iConnect to resume."); stopSelf(); return START_NOT_STICKY }
        observing?.cancel()
        getSystemService(NotificationManager::class.java).notify(NOTIFICATION, notification(active.state.value))
        lastNotification = android.os.SystemClock.elapsedRealtime()
        observing = scope.launch {
            active.state.collect { state ->
                if (!active.ownsBackground(token)) stopSelf()
                else if (android.os.SystemClock.elapsedRealtime() - lastNotification >= 500) {
                    getSystemService(NotificationManager::class.java).notify(NOTIFICATION, notification(state))
                    lastNotification = android.os.SystemClock.elapsedRealtime()
                }
            }
        }
        limit?.cancel()
        limit = scope.launch { delay(MAX_RUN_MS); active.backgroundStopped(requested, "Transfer time limit reached. Reopen iConnect to resume."); stopSelf() }
        scope.launch { while (isActive && active.ownsBackground(requested)) {
            delay(1_000)
            active.checkBackground()
            if (!notificationsAllowed(this@TransferService)) { active.backgroundStopped(requested, "Transfer notifications are unavailable. Reopen iConnect to resume."); stopSelf(); break }
        } }
        return START_NOT_STICKY
    }
    override fun onTimeout(startId: Int, fgsType: Int) {
        controller?.backgroundStopped(token, "Android stopped the background transfer. Reopen iConnect to resume.")
        stopForeground(STOP_FOREGROUND_REMOVE); stopSelf()
    }
    override fun onDestroy() {
        controller?.backgroundStopped(token, "Background transfer service stopped. Reopen iConnect to resume.")
        scope.cancel(); wake?.let { if (it.isHeld) it.release() }; wake = null
        stopForeground(STOP_FOREGROUND_REMOVE); super.onDestroy()
    }
    override fun onBind(intent: Intent?): IBinder? = null
    private fun notification(state: ProofState?): Notification {
        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val builder = Notification.Builder(this, CHANNEL).setSmallIcon(android.R.drawable.stat_sys_download)
            .setContentTitle("iConnect file transfer").setContentIntent(open).setOnlyAlertOnce(true).setOngoing(true)
            .setCategory(Notification.CATEGORY_PROGRESS).setVisibility(Notification.VISIBILITY_PRIVATE)
            .setPublicVersion(Notification.Builder(this, CHANNEL).setSmallIcon(android.R.drawable.stat_sys_download).setContentTitle("iConnect transfer active").build())
        val selected = state?.downloads?.filter { it.id in state.backgroundDownloads } ?: emptyList()
        val sending = state?.sends?.filter { it.id in state.backgroundSends } ?: emptyList()
        val total = selected.sumOf { it.size }; val received = selected.sumOf { it.offset }
        val progress = if (total == 0L) 0 else (received.toDouble() * 100 / total).toInt().coerceIn(0, 100)
        builder.setContentText(if (sending.isNotEmpty()) "${selected.size} receiving · ${sending.size} sending" else if (selected.isEmpty()) "Starting selected transfer…" else "${selected.size} selected transfer(s) · $progress%")
            .setProgress(100, progress, sending.isNotEmpty() || selected.isEmpty() || total == 0L)
        if (token.isNotEmpty()) {
            val stop = PendingIntent.getService(this, 1, Intent(this, TransferService::class.java).setAction(PAUSE)
                .setData(Uri.parse("iconnect-internal://transfer/$token")).putExtra(TOKEN, token), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
            builder.addAction(Notification.Action.Builder(null, "Pause transfers", stop).build())
        }
        return builder.build()
    }
    companion object {
        const val TOKEN = "transfer-authorization"
        private const val CHANNEL = "iconnect.transfers"
        private const val PAUSE = "com.iconnect.proof.PAUSE_TRANSFERS"
        private const val NOTIFICATION = 1001
        private const val MAX_RUN_MS = 6L * 60 * 60 * 1000
        fun notificationsAllowed(context: Context): Boolean {
            val manager = context.getSystemService(NotificationManager::class.java)
            return manager.areNotificationsEnabled() && (Build.VERSION.SDK_INT < 33 || context.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED) &&
                manager.getNotificationChannel(CHANNEL)?.importance != NotificationManager.IMPORTANCE_NONE
        }
    }
}
