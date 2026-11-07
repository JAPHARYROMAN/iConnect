package com.iconnect.proof

import android.content.Context
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import kotlinx.coroutines.*

/** Cost monitoring follows visible UI or already-approved background work; it grants no work. */
internal class AndroidTransferCostMonitor(context:Context,private val scope:CoroutineScope,private val publish:(PhoneNetworkCost)->Unit,
    private val read:()->PhoneNetworkCost={readCost(context)}):AutoCloseable {
    private val gate=Any(); private var revision=0L; private var active=false; private var closed=false; private var job:Job?=null
    fun setActive(value:Boolean)=synchronized(gate) {
        if(closed || value==active) return@synchronized
        active=value; val ticket=++revision; job?.cancel(); job=null
        if(!value) {publish(PhoneNetworkCost.UNKNOWN); return@synchronized}
        job=scope.launch(Dispatchers.IO) {while(isActive) {
            val cost=runCatching {read()}.getOrDefault(PhoneNetworkCost.UNKNOWN)
            synchronized(gate) {if(!closed && active && ticket==revision) runCatching {publish(cost)}}
            delay(2000)
        }}
    }
    override fun close()=synchronized(gate) {if(!closed) {closed=true; active=false; ++revision; job?.cancel(); job=null; publish(PhoneNetworkCost.UNKNOWN)}}
    companion object {
        fun readCost(context:Context):PhoneNetworkCost {
            val manager=context.getSystemService(ConnectivityManager::class.java)?:return PhoneNetworkCost.UNKNOWN
            val network=manager.boundNetworkForProcess?:manager.activeNetwork?:return PhoneNetworkCost.UNKNOWN
            val capabilities=manager.getNetworkCapabilities(network)?:return PhoneNetworkCost.UNKNOWN
            if(!capabilities.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET) || !capabilities.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_RESTRICTED)) return PhoneNetworkCost.UNKNOWN
            val cost=if(!capabilities.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED) || !capabilities.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_ROAMING) ||
                manager.restrictBackgroundStatus==ConnectivityManager.RESTRICT_BACKGROUND_STATUS_ENABLED) PhoneNetworkCost.METERED else PhoneNetworkCost.UNRESTRICTED
            return if(network==(manager.boundNetworkForProcess?:manager.activeNetwork)) cost else PhoneNetworkCost.UNKNOWN
        }
    }
}
