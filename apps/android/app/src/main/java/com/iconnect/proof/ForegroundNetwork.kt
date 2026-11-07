package com.iconnect.proof

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.os.Handler
import android.os.Looper

/** One callback only while the owning UI is visible. Callback arguments supply ordered network state. */
internal interface NetworkWatch {fun start(); fun stop()}
internal class ForegroundNetwork(context:Context,private val changed:(Boolean,Long?)->Unit):NetworkWatch {
    private val manager=context.getSystemService(ConnectivityManager::class.java)
    private var active:ConnectivityManager.NetworkCallback?=null
    override fun start() {
        if(active!=null) return
        val callback=object:ConnectivityManager.NetworkCallback() {
            var network:Network?=null; var capable=false; var blocked=true
            fun publish() {if(active===this) changed(capable && !blocked,network?.networkHandle)}
            override fun onAvailable(value:Network) {network=value; capable=false; blocked=true; publish()}
            override fun onCapabilitiesChanged(value:Network,capabilities:NetworkCapabilities) {
                if(network!=value) return
                capable=capabilities.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET); publish()
            }
            override fun onBlockedStatusChanged(value:Network,valueBlocked:Boolean) {if(network==value){blocked=valueBlocked; publish()}}
            override fun onLost(value:Network) {if(network==value){network=null; capable=false; blocked=true; publish()}}
        }
        active=callback
        try {manager.registerDefaultNetworkCallback(callback,Handler(Looper.getMainLooper()))}
        catch(_:RuntimeException) {active=null; changed(false,null)}
    }
    override fun stop() {val previous=active?:return; active=null; runCatching {manager.unregisterNetworkCallback(previous)}; changed(false,null)}
}
