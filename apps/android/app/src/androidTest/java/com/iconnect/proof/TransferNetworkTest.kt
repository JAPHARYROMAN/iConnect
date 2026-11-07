package com.iconnect.proof

import android.net.Uri
import android.os.Bundle
import android.os.SystemClock
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.google.protobuf.ByteString
import com.iconnect.protocol.*
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.security.Signature

@RunWith(AndroidJUnit4::class)
class TransferNetworkTest {
    private val instrumentation get()=InstrumentationRegistry.getInstrumentation()
    @Test fun settingsPersistMissingAndCorruptRecordsNeedOwnerReviewAndNativeLeaseIsExclusive()=runBlocking {
        val f=ActivityControlsTest.Fixture(); val policy=PhoneFileTrafficPolicy(SystemClock::elapsedRealtime); val path=File(f.root,"transfer-network/settings.json")
        try {
            AndroidTransferNetworkSettings(f.application,policy).use {settings->
                assertEquals(PhoneNetworkPreferences(),settings.preferences); settings.set(PhoneNetworkPreferences(65536,true)); assertThrows(Exception::class.java) {AndroidTransferNetworkSettings(f.application,PhoneFileTrafficPolicy())}
            }
            AndroidTransferNetworkSettings(f.application,policy).use {assertEquals(PhoneNetworkPreferences(65536,true),it.preferences)}
            check(path.delete()); AndroidTransferNetworkSettings(f.application,policy).use {settings->
                assertTrue(settings.view().missing); assertThrows(StorageFailure::class.java) {policy.requireContent()}; path.writeText("reappeared"); assertThrows(Exception::class.java) {settings.reset()}; assertEquals("reappeared",path.readText()); check(path.delete()); settings.reset(); assertTrue(settings.issue.isEmpty())
            }
            path.writeText("{generated corruption"); AndroidTransferNetworkSettings(f.application,policy).use {settings->
                assertTrue(settings.view().canReset); assertThrows(StorageFailure::class.java) {policy.requireContent()}; settings.reset(); assertEquals(1,settings.view().preserved); settings.clearPreserved(); assertEquals(0,settings.view().preserved)
            }
        } finally {f.dispose()}
    }
    @Test fun changedSettingsAndSymlinkedRecordsAreRefusedWithoutOverwritingTheirTarget()=runBlocking {
        val f=ActivityControlsTest.Fixture(); val path=File(f.root,"transfer-network/settings.json"); val policy=PhoneFileTrafficPolicy()
        try {
            AndroidTransferNetworkSettings(f.application,policy).use {settings->settings.set(PhoneNetworkPreferences(65536)); path.appendText(" "); val current=path.readText(); assertThrows(Exception::class.java) {settings.set(PhoneNetworkPreferences())}; assertEquals(current,path.readText()); assertThrows(StorageFailure::class.java) {policy.requireContent()}}
            check(path.delete()); val target=File(f.root,"generated-target.txt").apply {writeText("kept original")}; android.system.Os.symlink(target.path,path.path)
            AndroidTransferNetworkSettings(f.application,policy).use {assertTrue(it.issue.isNotEmpty()); assertThrows(StorageFailure::class.java) {policy.requireContent()}}; assertEquals("kept original",target.readText())
        } finally {f.dispose()}
    }
    @Test fun controllerSavesOnIoAndRestartsWithoutCreatingStorageAuthority()=runBlocking {
        val f=ActivityControlsTest.Fixture()
        try {
            f.open(); f.main {f.model.setTransferNetwork(PhoneNetworkPreferences(131072,true))}
            withTimeout(15000) {while(f.model.state.value.transferNetwork.busy) delay(20)}; assertEquals(PhoneNetworkPreferences(131072,true),f.model.state.value.transferNetwork.preferences)
            f.closeModel(); f.open(); assertEquals(PhoneNetworkPreferences(131072,true),f.model.state.value.transferNetwork.preferences); assertTrue(f.model.state.value.phoneShares.isEmpty()); assertFalse(f.model.state.value.connected)
            f.main {f.model.setUiVisible(false)}; assertEquals(PhoneNetworkCost.UNKNOWN,f.model.state.value.transferNetwork.cost)
        } finally {f.dispose()}
    }
    @Test fun unavailableSettingsStoreDoesNotPreventLocalControllerBrowsing():Unit=runBlocking {
        val f=ActivityControlsTest.Fixture()
        try {AndroidTransferNetworkSettings(f.application,PhoneFileTrafficPolicy()).use {f.open(); assertTrue(f.model.state.value.transferNetwork.issue.isNotEmpty()); assertTrue(f.model.localFiles.state.value.healthy); assertThrows(StorageFailure::class.java) {f.model.networkTraffic.requireContent()}}} finally {f.dispose()}
    }
    @Test fun actualOfflineProviderCopyStillCompletesWhenRemoteNetworkSettingsAreCorrupt()=runBlocking {
        val f=ActivityControlsTest.Fixture(); val context=f.application
        val from=requireNotNull(context.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"fixture",null,null)); val to=requireNotNull(context.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"fixture",null,null))
        val locations=LocalDocumentLocations(context); val sourceTree=Uri.parse(from.getString("tree"))
        try {
            AndroidTransferNetworkSettings(context,PhoneFileTrafficPolicy()).use {it.set(PhoneNetworkPreferences(65536,true))}; File(f.root,"transfer-network/settings.json").writeText("{unreadable settings")
            f.open(); assertThrows(StorageFailure::class.java) {f.model.networkTraffic.requireContent()}; locations.add(sourceTree,from.getInt("flags"))
            AndroidFileService(context,locations as DocumentAccessPolicy).use {service->
                suspend fun rpc(request:FileRequest)=service.execute(locations.ownerId(),request.toBuilder().setRequestId(StorageRules.token()).build())
                val root=rpc(FileRequest.newBuilder().setOperation(FileOperation.LIST_ROOTS).build()).entriesList.single()
                val file=rpc(FileRequest.newBuilder().setOperation(FileOperation.LIST_DIRECTORY).setNodeId(root.id).build()).entriesList.single {it.name=="simu.txt"}
                val selected=service.captureLocalFile(locations.ownerId(),file); val copies=f.model.localFiles.copies; val id=copies.prepare(selected)
                copies.chooseDestination(id,Uri.parse(to.getString("tree")),to.getInt("flags"),PhoneCollision.KEEP_BOTH).join(); val row=copies.state.value.items.single().download!!
                assertEquals(row.error,PhoneDownloadPhase.COMPLETED,row.phase)
                val original=android.provider.DocumentsContract.buildDocumentUriUsingTree(sourceTree,from.getString("file"))
                val expected=context.contentResolver.openInputStream(original)!!.use {it.readBytes()}; val actual=context.contentResolver.openInputStream(Uri.parse(row.published))!!.use {it.readBytes()}; assertArrayEquals(expected,actual)
                copies.remove(id); assertThrows(StorageFailure::class.java) {f.model.networkTraffic.requireContent()}; assertTrue(f.model.state.value.phoneShares.isEmpty())
            }
        } finally {
            locations.all().filter {it.tree==sourceTree}.forEach {locations.remove(it.id)}; f.dispose()
            listOf(from,to).forEach {context.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"dispose",it.getString("root"),null)}
        }
    }
    @Test fun monitorReadsActualOsCostStopsWhenUnneededAndSuppressesLateResults()=runBlocking {
        val scope=CoroutineScope(SupervisorJob()+Dispatchers.Default); val cost=AndroidTransferCostMonitor.readCost(instrumentation.targetContext); assertNotNull(cost)
        val entered=CompletableDeferred<Unit>(); val release=java.util.concurrent.CountDownLatch(1); val values=java.util.concurrent.CopyOnWriteArrayList<PhoneNetworkCost>()
        val monitor=AndroidTransferCostMonitor(instrumentation.targetContext,scope,{values.add(it)},{entered.complete(Unit); release.await(); PhoneNetworkCost.UNRESTRICTED})
        try {monitor.setActive(true); withTimeout(5000) {entered.await()}; monitor.setActive(false); monitor.close(); release.countDown(); delay(100); assertFalse(values.contains(PhoneNetworkCost.UNRESTRICTED))} finally {release.countDown(); monitor.close(); scope.cancel()}
    }
    @Test fun closingRpcCancelsARequestBeforeTransmissionDuringItsBandwidthWait()=runBlocking {
        val entered=CompletableDeferred<Unit>(); val policy=PhoneFileTrafficPolicy(wait={entered.complete(Unit); awaitCancellation()}); policy.configure(PhoneNetworkPreferences(65536))
        val scope=CoroutineScope(SupervisorJob()+Dispatchers.Main.immediate); var sends=0
        val remote=withContext(Dispatchers.Main) {RemoteFiles({_,_->sends++},scope,"f".repeat(64),null,{error(it)},traffic=policy)}
        try {val work=async(Dispatchers.Main) {remote.request(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_WRITE).setContent(ByteString.copyFrom(ByteArray(32768))).build())}; entered.await(); withContext(Dispatchers.Main) {remote.close()}; withTimeout(2000) {work.join()}; assertTrue(work.isCancelled); assertEquals(0,sends)} finally {withContext(Dispatchers.Main) {remote.close()}; scope.cancel()}
    }
    @Test fun nativeAuthenticatedAndroidChannelsPaceProviderBytesAndRefuseMeteredWritesWithoutLosingStatus()=runBlocking {
        val f=ActivityControlsTest.Fixture(); val context=f.application; val bundle=requireNotNull(context.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"fixture",null,null))
        val original="Generated authenticated network payload.\n".repeat(2048).toByteArray(); context.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"change",bundle.getString("file"),Bundle().apply {putString("content",String(original))})
        val guestKey=TestPeer.key(); val identity=LocalIdentity(); val guestIdentity=object:SessionIdentity {override val publicKey=guestKey.public.encoded; override val id=Rules.id(publicKey); override fun sign(transcript:ByteArray)=Signature.getInstance("SHA256withECDSA").run {initSign(guestKey.private); update(transcript); sign()}}
        val store=AndroidStorageGrants(context); val approved=TestPeer.approve(store.owner,guestKey); val grant=store.add(approved,Uri.parse(bundle.getString("tree")),bundle.getInt("flags"),"Generated source"); store.update(grant.id,StorageRules.SUPPORTED)
        val incoming=AndroidIncomingUploads(context,store,identity.id); val scope=CoroutineScope(SupervisorJob()+Dispatchers.Main.immediate); val policy=PhoneFileTrafficPolicy(SystemClock::elapsedRealtime); policy.configure(PhoneNetworkPreferences(65536))
        val ready=CompletableDeferred<Unit>(); val logs=java.util.concurrent.CopyOnWriteArrayList<String>(); var host:RtcTestSession?=null; var guest:RtcTestSession?=null
        val session=StorageRules.token()
        try {
            withContext(Dispatchers.Main) {
                host=RtcTestSession(instrumentation.targetContext,scope,identity,session,guestIdentity.publicKey,{kind,body,mid->when(kind) {"offer"->guest!!.acceptOffer(body); "candidate"->guest!!.addCandidate(body,mid)}},{logs.add(it)},{},fileService=AndroidFileService(context,store,incoming=incoming),initiator=true,traffic=policy)
                guest=RtcTestSession(instrumentation.targetContext,scope,guestIdentity,session,identity.publicKey,{kind,body,mid->when(kind) {"answer"->host.acceptAnswer(body); "candidate"->host.addCandidate(body,mid)}},{logs.add(it)},{if(it) ready.complete(Unit)})
                host.offer()
            }
            withTimeout(35000) {ready.await()}
            suspend fun rpc(request:FileRequest)=withContext(Dispatchers.Main) {guest!!.requestFiles(request)}
            val root=rpc(FileRequest.newBuilder().setOperation(FileOperation.LIST_ROOTS).build()).entriesList.single()
            val file=rpc(FileRequest.newBuilder().setOperation(FileOperation.LIST_DIRECTORY).setNodeId(root.id).build()).entriesList.single {it.name=="simu.txt"}
            val bytes=java.io.ByteArrayOutputStream(); val start=SystemClock.elapsedRealtime()
            var offset=0; while(offset<file.size) {val response=rpc(FileRequest.newBuilder().setOperation(FileOperation.READ_RANGE).setNodeId(file.id).setExpectedVersion(file.version).setOffset(offset.toLong()).setLength(minOf(32768,(file.size-offset).toInt())).build()); assertEquals(FileStatus.FILE_STATUS_OK,response.status); bytes.write(response.content.toByteArray()); offset+=response.content.size()}
            assertArrayEquals(original,bytes.toByteArray()); assertTrue(SystemClock.elapsedRealtime()-start>=original.size*1000L/65536-30)
            val epoch=rpc(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_CONTEXT).build()).mutationEpoch
            val upload=UploadInfo.newBuilder().setId(MutationRules.newId()).setName("generated-incoming.bin").setSize(original.size.toLong()).setSha256(ByteString.copyFrom(Rules.digest(original))).setCollision(UploadCollision.UPLOAD_FAIL).build()
            policy.configure(PhoneNetworkPreferences(65536,true)); policy.observe(PhoneNetworkCost.METERED)
            val prepare=FileRequest.newBuilder().setOperation(FileOperation.PREPARE_UPLOAD).setDestinationId(root.id).setTransferId(upload.id).setMutationEpoch(epoch).setUpload(upload).build()
            assertEquals(FileStatus.ACCESS_DENIED,rpc(prepare).status); assertTrue(incoming.state.value.isEmpty())
            policy.observe(PhoneNetworkCost.UNRESTRICTED); assertEquals(FileStatus.FILE_STATUS_OK,rpc(prepare).status)
            val write=FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_WRITE).setTransferId(upload.id).setMutationEpoch(epoch).setContent(ByteString.copyFrom(original,0,32768)).build()
            assertEquals(32768L,rpc(write).upload.offset); policy.observe(PhoneNetworkCost.METERED)
            assertEquals(FileStatus.ACCESS_DENIED,rpc(write.toBuilder().setOffset(32768).build()).status)
            assertEquals(32768L,rpc(FileRequest.newBuilder().setOperation(FileOperation.UPLOAD_STATUS).setTransferId(upload.id).setMutationEpoch(epoch).build()).upload.offset)
            assertEquals(FileStatus.FILE_STATUS_OK,rpc(FileRequest.newBuilder().setOperation(FileOperation.CANCEL_UPLOAD).setTransferId(upload.id).setMutationEpoch(epoch).build()).status)
            assertEquals(FileStatus.FILE_STATUS_OK,rpc(FileRequest.newBuilder().setOperation(FileOperation.GET_METADATA).setNodeId(file.id).build()).status)
        } catch(e:Exception) {throw AssertionError("Generated native channel test failed: ${logs.takeLast(12)}",e)}
        finally {
            withContext(Dispatchers.Main) {host?.close(); guest?.close()}; scope.cancel(); incoming.shutdown(); store.revokePeer(approved.id)
            context.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"dispose",bundle.getString("root"),null); f.dispose()
        }
    }
}
