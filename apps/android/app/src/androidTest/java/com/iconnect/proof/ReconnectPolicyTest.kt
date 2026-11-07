package com.iconnect.proof

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

@RunWith(AndroidJUnit4::class)
class ReconnectPolicyTest {
    private val own="a".repeat(64)
    private fun target()=ReconnectTarget("b".repeat(64),StorageRules.token(),"http://127.0.0.1:5078/","")
    private fun isolated(body:(File,File)->Unit) {
        val context=InstrumentationRegistry.getInstrumentation().targetContext
        val root=File(context.filesDir,"reconnect-test-"+StorageRules.token()).apply {check(mkdir())}; val directory=File(root,"availability")
        try {body(root,directory)} finally {root.deleteRecursively()}
    }
    private fun store(directory:File,identity:String=own,hook:(File)->Unit={})=AndroidReconnect(directory,identity,hook,fixture=true)
    @Test fun intentIsOffByDefaultAndBindsIdentityPairingAndEndpoints()=isolated {_,directory->
        val target=target(); val policy=store(directory); assertFalse(policy.allowed(target)); assertFalse(directory.exists())
        policy.configure(target,true,policy.blockNow()); assertTrue(policy.allowed(target)); assertTrue(store(directory).allowed(target))
        listOf(target.copy(peer="c".repeat(64)),target.copy(epoch=StorageRules.token()),target.copy(server="http://127.0.0.1:5079/"),target.copy(stun="stun:localhost:3478")).forEach {assertFalse(policy.allowed(it))}
        assertFalse(policy.allowed(null)); val changed=store(directory,"d".repeat(64)); assertFalse(changed.healthy); assertFalse(changed.allowed(target))
    }
    @Test fun durablePauseWinsEvenWhenTheOptionsCommitFails()=isolated {_,directory->
        val target=target(); var fail=false; val policy=store(directory,hook={if(fail && it.name=="reconnect.json") error("injected options failure")})
        policy.configure(target,true,policy.blockNow()); fail=true; policy.blockNow()
        assertThrows(IllegalStateException::class.java) {policy.persistPause()}; assertFalse(policy.allowed(target))
        val cold=store(directory); assertTrue(cold.healthy); assertTrue(cold.paused); assertFalse(cold.allowed(target))
        cold.configure(target,true,cold.blockNow()); assertTrue(cold.allowed(target)); assertTrue(store(directory).allowed(target))
    }
    @Test fun pauseInvalidatesAnInFlightEnableBeforePublication()=isolated {_,directory->
        val entered=CountDownLatch(1); val release=CountDownLatch(1); var block=true
        val policy=store(directory,hook={if(block && it.name=="reconnect.json"){entered.countDown(); check(release.await(5,TimeUnit.SECONDS))}})
        val executor=Executors.newSingleThreadExecutor(); val target=target(); val ticket=policy.blockNow()
        try {
            val saving=executor.submit {policy.configure(target,true,ticket)}; assertTrue(entered.await(5,TimeUnit.SECONDS))
            policy.blockNow(); assertFalse(policy.allowed(target)); release.countDown(); saving.get(5,TimeUnit.SECONDS)
            assertFalse(policy.allowed(target)); block=false; policy.persistPause(); assertFalse(store(directory).allowed(target))
        } finally {release.countDown(); executor.shutdownNow()}
    }
    @Test fun unreadableMalformedAndUncommittedIntentNeverEnableConnections()=isolated {_,directory->
        directory.mkdirs(); val file=File(directory,"reconnect.json")
        listOf("{broken", "x".repeat(8193), "{\"enabled\":true}").forEach {value->file.writeText(value); val policy=store(directory); assertFalse(policy.healthy); assertFalse(policy.allowed(target()))}
        file.delete(); File(directory,"reconnect.json.new").writeText("uncommitted"); val pending=store(directory); assertFalse(pending.healthy); assertFalse(pending.options.enabled)
    }
    @Test fun resetPreservesUnexpectedPauseMetadataAndNeverTouchesOtherFiles()=isolated {root,directory->
        val target=target(); val policy=store(directory); policy.configure(target,true,policy.blockNow())
        val pause=File(directory,"reconnect-paused.json.new").apply {mkdir()}; File(pause,"keep.txt").writeText("preserved injected metadata")
        val copied=File(root,"copied-file.txt").apply {writeText("generated copy kept")}
        assertFalse(store(directory).allowed(target)); policy.reset(policy.blockNow())
        assertFalse(policy.options.enabled); assertTrue(policy.paused); assertFalse(store(directory).allowed(target))
        val archive=root.listFiles()!!.single {it.name.startsWith("availability-recovery-")}; assertEquals("preserved injected metadata",File(archive,"reconnect-paused.json.new/keep.txt").readText()); assertEquals("generated copy kept",copied.readText())
        policy.configure(target,true,policy.blockNow()); assertTrue(policy.allowed(target))
    }
    @Test fun failedMarkerRemovalCannotTurnOnAutomaticConnections()=isolated {_,directory->
        directory.mkdirs(); File(directory,"reconnect-paused.json").mkdir(); val policy=store(directory); val target=target()
        assertThrows(IllegalStateException::class.java) {policy.configure(target,true,policy.blockNow())}
        assertFalse(policy.allowed(target)); assertFalse(store(directory).allowed(target))
    }
}
