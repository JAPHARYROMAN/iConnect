package com.iconnect.proof

import android.content.Context
import android.net.Uri
import android.provider.DocumentsContract as Docs
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.iconnect.protocol.*
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.IOException

@RunWith(AndroidJUnit4::class)
class DiagnosticsTest {
    companion object {const val SECRET="NEVER-EXPORT-PRIVATE-74bd5ef"}
    private fun fixture(body:(Context,Uri,Uri)->Unit) {
        val context=InstrumentationRegistry.getInstrumentation().targetContext
        val data=requireNotNull(context.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"fixture",null,null))
        val tree=Uri.parse(data.getString("tree")); val root=Docs.buildDocumentUriUsingTree(tree,Docs.getTreeDocumentId(tree))
        check(Docs.isDocumentUri(context,root)) {"The fixture must be a registered document provider."}
        try {body(context,root,Docs.buildDocumentUriUsingTree(tree,data.getString("file")))}
        finally {context.contentResolver.call(TestDocumentsProvider.CONTROL_URI,"dispose",data.getString("root"),null)}
    }
    private fun create(context:Context,parent:Uri)=requireNotNull(Docs.createDocument(context.contentResolver,parent,"application/json","generated-diagnostics-${StorageRules.token()}.json"))
    private fun read(context:Context,uri:Uri)=context.contentResolver.openInputStream(uri)!!.use {it.readBytes()}
    @Test fun allowlistedSnapshotDoesNotSerializeNamesPathsAddressesIdsTokensContentHashesOrRawErrors() {
        val privateId="ab".repeat(32); val privateHash="cd".repeat(32); val uri=Uri.parse("content://private.example/$SECRET")
        val entry=FileEntry.newBuilder().setId(SECRET).setName(SECRET).setVersion(privateHash).setGrantId(SECRET).build()
        val state=ProofState(server="https://private.example/$SECRET",stun="stun:private.example:3478",deviceName=SECRET,deviceId=privateId,peerName=SECRET,
            invitation="iconnect://pair?token=$SECRET",code="937264",status=SECRET,events=listOf(SECRET),remoteEntries=listOf(entry),breadcrumbs=listOf(entry),
            fileError=SECRET,textPreview=SECRET,downloadDestination=uri.toString(),trustIssue=SECRET,trustNotice=SECRET,reconnectIssue=SECRET,presenceIssue=SECRET,
            phoneShares=listOf(AndroidStorageGrant(SECRET,privateId,uri,SECRET,trustEpoch=SECRET)),
            downloads=listOf(PhoneDownload(SECRET,privateId,SECRET,SECRET,privateHash,27,uri.toString(),ticket=SECRET,sha256=privateHash,error=SECRET,published=uri.toString(),publishedName=SECRET,active=true)),
            uploads=listOf(PhoneUpload(SECRET,privateId,privateId,SECRET,SECRET,27,identity=SECRET,hash=privateHash,remote=SECRET,destination=SECRET,message=SECRET,active=true)),
            privateOutgoingBatches=listOf(PhoneBatchSource(SECRET,privateId,privateId,SECRET,directory=SECRET,manifest=SECRET,hash=privateHash,label=SECRET,message=SECRET)),
            outgoingBatchSends=listOf(PhoneBatchSend(SECRET,SECRET,privateId,privateId,SECRET,privateHash,1,27,30,outcomes="",header=SECRET,destination=SECRET,label=SECRET,message=SECRET)),
            incomingCopies=listOf(PhoneIncomingCopy(SECRET,privateId,SECRET,27,0,UploadPhase.UPLOAD_RECEIVING,SECRET,SECRET,false)),
            incomingBatchPlans=listOf(PhoneIncomingBatchPlan(SECRET,privateId,27,1,UploadBatchPlanPhase.UPLOAD_PLAN_READY)),
            sends=listOf(PhoneSend(SECRET,privateId,SECRET,SECRET,27,DownloadState.DOWNLOAD_READY,27,0,true,false)),
            transferRecovery=listOf(PhoneTransferRecovery("Files",StateRecoveryIssue(SECRET,SECRET,SECRET,true)),PhoneTransferRecovery(SECRET,StateRecoveryIssue(SECRET,SECRET,SECRET,false))))
        val json=PhoneDiagnosticSnapshot.create(state,false); val parsed=JSONObject(json)
        assertEquals(1,parsed.getInt("schema")); assertEquals("android",parsed.getString("platform")); assertFalse(parsed.getJSONObject("policy").getBoolean("ownerHealthy"))
        assertEquals(1,parsed.getJSONObject("work").getJSONObject("downloads").getInt("active")); assertEquals(1,parsed.getJSONObject("recovery").getInt("files")); assertEquals(1,parsed.getJSONObject("recovery").getInt("otherAreas"))
        listOf(SECRET,privateId,privateHash,uri.toString(),state.server,state.stun,state.invitation,state.code).forEach {assertFalse("Private input was exported",json.contains(it))}
        assertTrue(json.toByteArray().size<=PhoneDiagnosticSnapshot.MAX_BYTES)
    }
    @Test fun reviewedReportWritesExactBytesToANewProviderDocumentAndReplayCannotWriteAnotherCopy()=fixture {context,parent,original->
        val before=read(context,original); val destination=create(context,parent); val manager=PhoneDiagnostics(context,{"generated-owner"},{true})
        val review=manager.review(ProofState(),false); assertEquals(0,read(context,destination).size)
        manager.export(review,destination); assertArrayEquals(review.json.toByteArray(Charsets.UTF_8),read(context,destination)); assertArrayEquals(before,read(context,original))
        val second=create(context,parent); assertThrows(IllegalStateException::class.java) {manager.export(review,second)}; assertEquals(0,read(context,second).size)
    }
    @Test fun canceledForgedAndEarlierReviewsNeverWriteAnyProviderBytes()=fixture {context,parent,_->
        val destination=create(context,parent); val manager=PhoneDiagnostics(context,{"generated-owner"},{true})
        var review=manager.review(ProofState(),true); manager.cancel(review.id); assertThrows(IllegalStateException::class.java) {manager.export(review,destination)}
        review=manager.review(ProofState(),true); assertThrows(IllegalStateException::class.java) {manager.export(review.copy(json=SECRET),destination)}
        assertThrows(IllegalStateException::class.java) {manager.export(review,destination)}
        review=manager.review(ProofState(),true); manager.review(ProofState(),true); assertThrows(IllegalStateException::class.java) {manager.export(review,destination)}
        assertEquals(0,read(context,destination).size)
    }
    @Test fun monotonicExpiryOwnerChangeAndControllerClosureEndTheReviewBeforeOpeningADocument()=fixture {context,parent,_->
        var elapsed=10L; var owner="first-owner"; var open=true; val manager=PhoneDiagnostics(context,{owner},{open},{elapsed}); val destination=create(context,parent)
        var review=manager.review(ProofState(),true); elapsed+=120_001; assertThrows(IllegalStateException::class.java) {manager.export(review,destination)}
        review=manager.review(ProofState(),true); owner="changed-owner"; assertThrows(IllegalStateException::class.java) {manager.export(review,destination)}
        review=manager.review(ProofState(),true); open=false; assertThrows(IllegalStateException::class.java) {manager.export(review,destination)}
        assertThrows(IllegalStateException::class.java) {manager.review(ProofState(),true)}; assertEquals(0,read(context,destination).size)
    }
    @Test fun aReopenedManagerCannotUseEarlierReviewsAndExistingProviderDocumentsAreNeverTruncated()=fixture {context,parent,original->
        val manager=PhoneDiagnostics(context,{"generated-owner"},{true}); var review=manager.review(ProofState(),true); val destination=create(context,parent)
        assertThrows(IllegalStateException::class.java) {PhoneDiagnostics(context,{"generated-owner"},{true}).export(review,destination)}
        val before=read(context,original); review=manager.review(ProofState(),true); assertThrows(IOException::class.java) {manager.export(review,original)}
        assertArrayEquals(before,read(context,original)); assertEquals(0,read(context,destination).size)
    }
    @Test fun aFileUriIsRefusedBeforeAnyDestinationIsOpened()=fixture {context,_,original->
        val before=read(context,original); val manager=PhoneDiagnostics(context,{"generated-owner"},{true}); val review=manager.review(ProofState(),true)
        assertThrows(IllegalArgumentException::class.java) {manager.export(review,Uri.parse("file:///unselected-owner-file.json"))}; assertArrayEquals(before,read(context,original))
    }
}
