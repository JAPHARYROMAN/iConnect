package com.iconnect.proof

import android.app.Application
import android.content.Context
import android.os.Bundle
import android.view.accessibility.AccessibilityNodeInfo
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.test.core.app.ActivityScenario
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.first
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

@RunWith(AndroidJUnit4::class)
class ActivityBackupJourneyTest {
    private val instrumentation get()=InstrumentationRegistry.getInstrumentation()
    private fun main(action:()->Unit)=instrumentation.runOnMainSync(action)
    private fun create(app:Application):PhoneController {
        lateinit var model:PhoneController
        main {model=PhoneController(app) {_,_->object:NetworkWatch {override fun start() {}; override fun stop() {}}}; model.setUiVisible(true)}
        return model
    }
    private suspend fun ready(model:PhoneController) {withTimeout(30_000) {model.state.first {it.activity.ready && it.activity.issue.isEmpty()}}}
    private suspend fun close(model:PhoneController) {lateinit var work:Job; main {work=model.close()}; work.join()}
    private class Fixture(val app:Application,val root:File,val archive:File,var model:PhoneController) {var defaultModel:PhoneController?=null}
    private suspend fun fixture(body:suspend (Fixture)->Unit) {
        val base=instrumentation.targetContext; val prefix="activity-backup-journey-"+StorageRules.token()
        val root=File(base.filesDir,prefix).apply {check(mkdir())}
        val app=object:Application() {
            init {attachBaseContext(base)}
            override fun getFilesDir()=root
            override fun getCacheDir()=File(root,"cache").apply {mkdirs()}
            override fun getApplicationContext():Context=this
            override fun getSharedPreferences(name:String,mode:Int)=base.getSharedPreferences(prefix+name,mode)
        }
        var fixture:Fixture?=null
        try {
            PhoneActivityCore(app).close()
            File(root,"local-activity/activity.json").writeText("generated damaged activity")
            File(root,"local-activity/activity.json.new").writeText("generated uncommitted activity")
            PhoneActivityCore(app).use {core->core.reset(); core.append(PhoneActivityEvent(PhoneActivityKind.SHARING,PhoneActivityOutcome.COMPLETED)); core.flush()}
            File(root,"generated-owner.txt").writeText("generated original bytes")
            val archive=File(root,"local-activity/recovery").listFiles()!!.single()
            fixture=Fixture(app,root,archive,create(app)); ready(fixture.model); body(fixture)
        } finally {withContext(NonCancellable) {
            fixture?.let {current->close(current.model); current.defaultModel?.let {close(it); main {(base.applicationContext as IConnectApplication).releaseClosed(it)}}}
            listOf("iconnect.proof","iconnect.storage","iconnect.saf-access").forEach {base.deleteSharedPreferences(prefix+it)}
            check(root.canonicalFile.parentFile==base.filesDir.canonicalFile && root.name==prefix); root.deleteRecursively()
        }}
    }
    private fun nodes(root:AccessibilityNodeInfo?):List<AccessibilityNodeInfo> =if(root==null) emptyList() else listOf(root)+(0 until root.childCount).flatMap {nodes(root.getChild(it))}
    private fun all()=nodes(instrumentation.uiAutomation.rootInActiveWindow)
    private fun control(text:String):AccessibilityNodeInfo? {
        var node=all().firstOrNull {it.text?.toString()==text && it.isVisibleToUser}
        while(node!=null && !node.isClickable) node=node.parent
        return node
    }
    private fun waitFor(label:String,condition:()->Boolean) {val end=System.currentTimeMillis()+30_000
        while(System.currentTimeMillis()<end) {if(condition()) return; Thread.sleep(25)}; error("Timed out: $label")}
    private fun click(text:String) {
        val end=System.currentTimeMillis()+15_000; var direction=AccessibilityNodeInfo.ACTION_SCROLL_FORWARD
        while(System.currentTimeMillis()<end) {
            val node=control(text)
            if(node?.isEnabled==true && node.performAction(AccessibilityNodeInfo.ACTION_CLICK)) {instrumentation.waitForIdleSync(); return}
            if(all().firstOrNull {it.isScrollable}?.performAction(direction)!=true) direction=if(direction==AccessibilityNodeInfo.ACTION_SCROLL_FORWARD) AccessibilityNodeInfo.ACTION_SCROLL_BACKWARD else AccessibilityNodeInfo.ACTION_SCROLL_FORWARD
            Thread.sleep(70)
        }; error("Activity backup control unavailable: $text")
    }
    private fun type(value:String) {
        waitFor("removal confirmation field") {all().any {it.isEditable && it.isVisibleToUser}}
        val field=all().first {it.isEditable && it.isVisibleToUser}
        assertTrue(field.performAction(AccessibilityNodeInfo.ACTION_SET_TEXT,Bundle().apply {putCharSequence(AccessibilityNodeInfo.ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE,value)}))
        instrumentation.waitForIdleSync()
    }
    @Test fun controllerForegroundOwnerResetAndRecreationRequireFreshActivityBackupReviews():Unit=runBlocking {fixture {f->
        val seed=f.model.state.value.activity.entries.single().id
        val other=File(f.archive.parentFile,StorageRules.token()).apply {check(mkdir())}; File(other,"journal.json").writeText("generated unrelated transfer metadata")
        var review=f.model.reviewActivityBackup(f.archive.name)
        main {f.model.setUiVisible(false)}
        assertTrue(runCatching {f.model.confirmActivityBackup(review)}.isFailure)
        main {f.model.setUiVisible(true)}
        assertTrue(runCatching {f.model.confirmActivityBackup(review)}.isFailure); assertTrue(f.archive.exists())
        review=f.model.reviewActivityBackup(f.archive.name)
        val owner=AndroidOwnerPolicy(f.app).recoveryStamp
        main {f.model.resetPairingData()}; withTimeout(30_000) {f.model.state.first {!it.savingTrust}}
        assertNotEquals(owner,AndroidOwnerPolicy(f.app).recoveryStamp)
        assertTrue(runCatching {f.model.confirmActivityBackup(review)}.isFailure); assertTrue(f.archive.exists())
        review=f.model.reviewActivityBackup(f.archive.name)
        close(f.model); f.model=create(f.app); ready(f.model)
        assertTrue(runCatching {f.model.confirmActivityBackup(review)}.isFailure)
        f.model.confirmActivityBackup(f.model.reviewActivityBackup(f.archive.name))
        assertFalse(f.archive.exists()); assertEquals(0,f.model.inspectActivityBackups().total)
        assertTrue(f.model.state.value.activity.entries.any {it.id==seed})
        assertEquals("generated original bytes",File(f.root,"generated-owner.txt").readText())
        assertEquals("generated unrelated transfer metadata",File(other,"journal.json").readText())
        assertTrue(f.model.state.value.phoneShares.isEmpty()); assertFalse(f.model.state.value.authenticated); assertFalse(f.model.state.value.connected)
    }}
    @Test fun nativeActivityBackupReviewKeepsCanceledBackgroundedAndDisposedSelectionsUntilFreshTypedRemoval():Unit=runBlocking {fixture {f->
        val live=File(f.root,"local-activity/activity.json").readBytes()
        ActivityScenario.launch(MainActivity::class.java).use {scenario->
            fun show() {scenario.onActivity {activity->
                f.defaultModel=(activity.application as IConnectApplication).controller()
                activity.setContent {MaterialTheme {val state by f.model.state.collectAsState()
                    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp)) {ActivityPanel(f.model,state)}}}
            }}
            fun review() {click("Manage preserved activity backups…"); click("Review backup ${f.archive.name.take(8)}")}
            show(); review(); type("REMOV"); waitFor("disabled incomplete confirmation") {control("Remove activity backup")?.isEnabled==false}
            click("Keep backup"); assertEquals(2,f.archive.listFiles()!!.size); click("Done")
            review(); type("REMOVE"); scenario.moveToState(Lifecycle.State.CREATED); scenario.moveToState(Lifecycle.State.RESUMED)
            waitFor("closed background review") {all().none {it.text?.toString()=="Remove this activity backup?"}}
            assertEquals(2,f.archive.listFiles()!!.size)
            review(); type("REMOVE"); scenario.onActivity {it.setContent {Text("Another local view")}}
            waitFor("replacement view after review disposal") {all().any {it.text?.toString()=="Another local view"}}
            assertEquals(2,f.archive.listFiles()!!.size)
            show(); review(); type("REMOVE"); click("Remove activity backup")
            waitFor("selected backup removal") {!f.archive.exists()}
            waitFor("removal result") {all().any {it.text?.toString()?.startsWith("The reviewed activity backup was removed.")==true}}
            click("Done")
            assertArrayEquals(live,File(f.root,"local-activity/activity.json").readBytes())
            assertEquals("generated original bytes",File(f.root,"generated-owner.txt").readText())
            assertEquals(1,f.model.state.value.activity.entries.size)
            assertTrue(f.model.state.value.phoneShares.isEmpty()); assertFalse(f.model.state.value.authenticated); assertFalse(f.model.state.value.connected)
        }
    }}
}
