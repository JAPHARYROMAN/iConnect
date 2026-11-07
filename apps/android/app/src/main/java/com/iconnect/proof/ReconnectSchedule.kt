package com.iconnect.proof

import kotlin.math.min
import kotlin.random.Random

internal enum class ReconnectAction { COORDINATOR, PEER, REFRESH }
internal data class ReconnectSnapshot(val allowed:Boolean, val visible:Boolean, val network:Boolean,
    val connected:Boolean=false,val connecting:Boolean=false,val authenticated:Boolean=false,val peerConnecting:Boolean=false,
    val pairing:Boolean=false,val refresh:Boolean=false)

/** Monotonic retry budget. Temporary network/foreground loss never resets accumulated backoff. */
internal class ReconnectSchedule(private val clock:()->Long,private val jitter:()->Double={Random.nextDouble(.8,1.2)}) {
    private var failures=0
    private var due=0L
    private var last:ReconnectAction?=null
    private var stableSince:Long?=null
    fun reset() {failures=0; due=0; last=null; stableSince=null}
    fun remainingSeconds()=((due-clock()).coerceAtLeast(0)+999)/1000
    fun next(value:ReconnectSnapshot):ReconnectAction? {
        if(!value.allowed || !value.visible || !value.network) return null
        if(value.authenticated) {
            val now=clock(); val since=stableSince?:now.also {stableSince=it}
            if(now-since>=30_000) {failures=0; due=0; last=null}
            return null
        }
        stableSince=null
        if(value.connecting || value.peerConnecting || value.pairing) return null
        val now=clock()
        // A successful coordinator connection may hand off once immediately to its pinned peer.
        if(now<due && !(value.connected && last==ReconnectAction.COORDINATOR)) return null
        val action=if(!value.connected) ReconnectAction.COORDINATOR else if(value.refresh) ReconnectAction.REFRESH else ReconnectAction.PEER
        val nominal=min(60_000L,1000L shl min(failures,6)); failures=min(failures+1,7)
        due=now+(nominal*jitter()).toLong().coerceIn(800,60_000); last=action
        return action
    }
}
