package com.iconnect.proof

import android.content.Context
import android.content.ContextWrapper
import android.system.Os
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.io.IOException
import java.nio.file.Files
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicLong

@RunWith(AndroidJUnit4::class)
class ActivityBackupTest {
    private val instrumentation get()=InstrumentationRegistry.getInstrumentation()
    private fun fixture(body:(Context,File)->Unit) {
        val base=instrumentation.targetContext; val prefix="activity-backup-test-"+StorageRules.token()
        val root=File(base.filesDir,prefix).apply {check(mkdir())}
        val context=object:ContextWrapper(base) {
            override fun getFilesDir()=root
            override fun getApplicationContext():Context=this
            override fun getSharedPreferences(name:String,mode:Int)=base.getSharedPreferences(prefix+name,mode)
        }
        try {body(context,root)} finally {
            listOf("iconnect.proof","iconnect.storage","iconnect.saf-access").forEach {base.deleteSharedPreferences(prefix+it)}
            check(root.canonicalFile.parentFile==base.filesDir.canonicalFile && root.name==prefix); root.deleteRecursively()
        }
    }
    private fun preserved(context:Context):Pair<PhoneActivityCore,File> {
        PhoneActivityCore(context).close(); val area=File(context.filesDir,"local-activity")
        File(area,"activity.json").writeText("generated damaged activity")
        File(area,"activity.json.new").writeText("generated uncommitted activity")
        val core=PhoneActivityCore(context); assertTrue(core.view().canReset); core.reset()
        return core to File(area,"recovery").listFiles()!!.single()
    }
    @Test fun reviewedActivityOnlyBackupKeepsLiveHistoryOriginalsMixedFoldersAndOtherRecovery()=fixture {context,root->
        val (core,archive)=preserved(context)
        core.use {
            core.append(PhoneActivityEvent(PhoneActivityKind.SETTINGS,PhoneActivityOutcome.COMPLETED)); core.flush()
            val live=File(root,"local-activity/activity.json").readBytes()
            val keep=File(root,"owner.txt").apply {writeText("generated original bytes")}
            val other=File(archive.parentFile,StorageRules.token()).apply {check(mkdir())}; File(other,"journal.json").writeText("generated transfer receipt")
            val mixed=File(archive.parentFile,StorageRules.token()).apply {check(mkdir())}
            File(mixed,"activity.json").writeText("generated mixed activity"); File(mixed,"keep.txt").writeText("generated unknown bytes")
            val revision=AtomicLong(); val manager=core.backupManager(revision,{"owner-a"},{true})
            val inventory=manager.inspect(0) {true}; assertEquals(1,inventory.total); assertEquals(1,inventory.keptFolders)
            val row=inventory.entries.single(); assertEquals(archive.name,row.id)
            var review=manager.review(row.id) {true}; assertEquals(2,review.files)
            assertThrows(IllegalStateException::class.java) {manager.confirm(review.copy(bytes=review.bytes+1)) {true}}
            assertThrows(IllegalStateException::class.java) {manager.confirm(review) {true}}
            review=manager.review(row.id) {true}; revision.incrementAndGet()
            assertThrows(IllegalStateException::class.java) {manager.confirm(review) {true}}
            manager.confirm(manager.review(row.id) {true}) {true}; assertFalse(archive.exists())
            assertArrayEquals(live,File(root,"local-activity/activity.json").readBytes())
            assertEquals("generated original bytes",keep.readText()); assertEquals("generated transfer receipt",File(other,"journal.json").readText())
            assertEquals("generated mixed activity",File(mixed,"activity.json").readText()); assertEquals("generated unknown bytes",File(mixed,"keep.txt").readText())
            assertThrows(IllegalArgumentException::class.java) {manager.review("../activity.json") {true}}
        }
    }
    @Test fun changedMetadataWithRestoredSizeAndTimestampRequiresFreshReview()=fixture {context,_->
        val (core,archive)=preserved(context)
        core.use {
            val manager=core.backupManager(AtomicLong(),{"owner-a"},{true}); val review=manager.review(archive.name) {true}
            val path=File(archive,"activity.json"); val time=path.lastModified(); val bytes=path.readBytes().also {it[0]=(it[0].toInt() xor 1).toByte()}
            path.writeBytes(bytes); assertTrue(path.setLastModified(time))
            assertThrows(IllegalStateException::class.java) {manager.confirm(review) {true}}
            assertArrayEquals(bytes,path.readBytes()); assertTrue(File(archive,"activity.json.new").exists())
            manager.confirm(manager.review(archive.name) {true}) {true}; assertFalse(archive.exists())
        }
    }
    @Test fun ownerExpiryForegroundAndManagerRecreationEndEarlierRemovalPermission()=fixture {context,_->
        val (core,archive)=preserved(context); var owner="owner-a"; var available=true; var time=1000L
        core.use {
            val manager=core.backupManager(AtomicLong(),{owner},{available},{time})
            var review=manager.review(archive.name) {true}; time+=120_001
            assertThrows(IllegalStateException::class.java) {manager.confirm(review) {true}}
            review=manager.review(archive.name) {true}; owner="owner-b"
            assertThrows(IllegalStateException::class.java) {manager.confirm(review) {true}}; owner="owner-a"
            review=manager.review(archive.name) {true}; available=false
            assertThrows(CancellationException::class.java) {manager.confirm(review) {true}}; available=true
            val fresh=core.backupManager(AtomicLong(),{owner},{true})
            assertThrows(IllegalStateException::class.java) {fresh.confirm(review) {true}}
            assertTrue(archive.exists())
        }
    }
    @Test fun symlinksAndHardLinksCannotTurnOwnerFilesIntoRemovableActivityMetadata()=fixture {context,root->
        val (core,archive)=preserved(context)
        core.use {
            val manager=core.backupManager(AtomicLong(),{"owner-a"},{true}); val target=File(root,"owner.txt").apply {writeText("generated owner bytes")}
            val path=File(archive,"activity.json"); check(path.delete()); Os.symlink(target.path,path.path)
            assertTrue(manager.inspect(0) {true}.entries.isEmpty()); assertTrue(runCatching {manager.review(archive.name) {true}}.isFailure)
            assertEquals("generated owner bytes",target.readText()); Os.remove(path.path); Os.link(target.path,path.path)
            assertTrue(manager.inspect(0) {true}.entries.isEmpty()); assertThrows(IllegalStateException::class.java) {manager.review(archive.name) {true}}
            assertEquals("generated owner bytes",target.readText()); assertTrue(path.exists())
        }
    }
    @Test fun replacedRecoveryShelfWithMatchingNamesAndBytesIsNeverAdoptedByOldReview()=fixture {context,root->
        val (core,archive)=preserved(context)
        core.use {
            val manager=core.backupManager(AtomicLong(),{"owner-a"},{true}); val review=manager.review(archive.name) {true}
            val area=File(root,"local-activity"); val shelf=File(area,"recovery"); val moved=File(area,"recovery-held")
            check(shelf.canonicalFile.parentFile==area.canonicalFile && moved.canonicalFile.parentFile==area.canonicalFile)
            Files.move(shelf.toPath(),moved.toPath()); check(archive.mkdirs())
            File(moved,archive.name).listFiles()!!.forEach {it.copyTo(File(archive,it.name))}
            assertThrows(IllegalStateException::class.java) {manager.confirm(review) {true}}
            assertEquals(2,archive.listFiles()!!.size); assertEquals(2,File(moved,archive.name).listFiles()!!.size)
        }
    }
    @Test fun stillReferencedArchiveIsProtectedWhenItsArchivalCompletionIsInterrupted()=fixture {_,root->
        val area=File(root,"local-activity").apply {check(mkdir())}; val path=File(area,"activity.json").apply {writeText("generated archived activity")}
        val recovery=StateRecovery(area); recovery.add(path,"Generated recoverable metadata."); val id=recovery.all().single().id
        assertThrows(IOException::class.java) {recovery.archive(id) {throw IOException("Generated interruption before archival completion.")}}
        val stat=Os.lstat(area.path); val manager=AndroidActivityBackups(area,"${stat.st_dev}:${stat.st_ino}",{recovery.all().map {it.id}.toSet()},AtomicLong(),{"owner-a"},{true})
        assertEquals(1,manager.inspect(0) {true}.keptFolders); assertTrue(manager.inspect(0) {true}.entries.isEmpty())
        assertThrows(IllegalStateException::class.java) {manager.review(id) {true}}
        recovery.archive(id); manager.confirm(manager.review(id) {true}) {true}; assertFalse(File(area,"recovery/$id").exists())
    }
    @Test fun withdrawalAfterFirstRemovalKeepsRemainingMetadataForANewManagerReview()=fixture {context,root->
        val (core,archive)=preserved(context)
        core.use {
            val manager=core.backupManager(AtomicLong(),{"owner-a"},{true}); val first=File(archive,"activity.json"); val remaining=File(archive,"activity.json.new")
            val review=manager.review(archive.name) {true}
            assertThrows(CancellationException::class.java) {manager.confirm(review) {first.exists()}}
            assertFalse(first.exists()); assertEquals("generated uncommitted activity",remaining.readText())
            assertTrue(File(root,"local-activity/activity.json").exists())
            assertThrows(IllegalStateException::class.java) {manager.confirm(review) {true}}
        }
        PhoneActivityCore(context).use {reopened->val manager=reopened.backupManager(AtomicLong(),{"owner-a"},{true})
            manager.confirm(manager.review(archive.name) {true}) {true}; assertFalse(archive.exists())}
    }
    @Test fun pagedWriterQueriesAndRejectedReviewsDoNotBlockNormalActivityRecording()=fixture {context,_->runBlocking {
        val (core,archive)=preserved(context); core.close()
        repeat(50) {val folder=File(archive.parentFile,StorageRules.token()).apply {check(mkdir())}; File(folder,"activity.json").writeText("generated activity backup")}
        val history=PhoneActivity(context)
        try {
            history.flush(); val page=history.inspectBackups(0); assertEquals(51,page.total); assertEquals(50,page.entries.size)
            val last=history.inspectBackups(50).entries.single(); val review=history.reviewBackup(last.id)
            assertTrue(runCatching {history.confirmBackup(review.copy(files=review.files+1))}.isFailure)
            assertEquals("",history.state.value.issue)
            history.record(PhoneActivityEvent(PhoneActivityKind.SETTINGS,PhoneActivityOutcome.COMPLETED)); history.flush()
            assertEquals(PhoneActivityKind.SETTINGS,history.state.value.entries.single().kind)
            history.confirmBackup(history.reviewBackup(last.id)); assertEquals(50,history.inspectBackups(0).total)
            assertEquals(PhoneActivityKind.SETTINGS,history.state.value.entries.single().kind)
        } finally {history.close()}
    }}
    @Test fun cancelingTheCallerBeforeDeletionDoesNotExecuteQueuedRemovalOrBlockHistory()=fixture {context,_->runBlocking {
        val (core,archive)=preserved(context); core.close(); val history=PhoneActivity(context)
        val entered=CompletableDeferred<Unit>(); val release=CountDownLatch(1)
        try {
            history.flush(); val review=history.reviewBackup(archive.name)
            val operation=async(Dispatchers.Default) {history.confirmBackup(review) {
                entered.complete(Unit); check(release.await(10,TimeUnit.SECONDS)); true
            }}
            try {withTimeout(5_000) {entered.await()}; operation.cancel()} finally {release.countDown()}
            operation.join(); history.flush()
            assertEquals(2,archive.listFiles()!!.size); assertEquals("",history.state.value.issue)
            history.record(PhoneActivityEvent(PhoneActivityKind.CONNECTION,PhoneActivityOutcome.COMPLETED)); history.flush()
            assertEquals(PhoneActivityKind.CONNECTION,history.state.value.entries.single().kind)
        } finally {release.countDown(); history.close()}
    }}
}
