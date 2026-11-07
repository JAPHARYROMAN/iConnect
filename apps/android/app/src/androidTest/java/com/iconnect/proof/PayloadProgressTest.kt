package com.iconnect.proof

import android.net.Uri
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.iconnect.protocol.FileOperation
import kotlinx.coroutines.*
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.util.concurrent.atomic.AtomicLong

@RunWith(AndroidJUnit4::class)
class PayloadProgressTest {
    @Test fun actualRangeProgressIsObservableBeforeCheckpointAndNeverChangesAuthority()=runBlocking {
        val f=DownloadSourceRecoveryTest.Fixture(); val tracker=PhonePayloadRateTracker(); val scope=CoroutineScope(SupervisorJob()+Dispatchers.Default)
        val observed=AtomicLong(-1); val reached=CompletableDeferred<Unit>()
        val collection=scope.launch(start=CoroutineStart.UNDISPATCHED) {f.manager.payloadProgress.collect {points->
            tracker.retain(points.keys)
            points.values.forEach {point->tracker.observe(point.id,"${point.peer}/${point.ticket}/${point.version}",point.size,point.received,true,point.at); observed.set(point.received)}
        }}
        try {
            f.source.before={request->if(request.operation==FileOperation.DOWNLOAD_READ) {
                withTimeout(5000) {while(observed.get()<request.offset) delay(5)}
                if(request.offset>=262144) {reached.complete(Unit); awaitCancellation()}
                f.elapsed+=500
            }}
            f.id=f.manager.enqueue(f.peer,f.source.selected,f.tree); f.manager.resume(f.id,f.peer,f.source::request)
            withTimeout(10000) {reached.await()}; val job=f.row; val point=f.manager.payloadProgress.value.getValue(f.id); val reads=f.source.reads
            assertEquals(262144L,point.received); assertEquals(0L,job.offset); assertEquals(0L,JSONObject(File(f.directory,"${f.id}.json").readText()).getLong("offset"))
            val key="${job.peer}/${job.ticket}/${job.version}"; val value=tracker.read(job.id,key,job.size,point.received,true,f.elapsed)
            assertEquals(65536L,value.bytesPerSecond); assertEquals(29L,value.remainingSeconds)
            assertEquals(PhonePayloadRateState.WAITING,tracker.read(job.id,key,job.size,point.received,true,f.elapsed+10001).state)
            assertEquals(reads,f.source.reads); assertEquals(job,f.row); assertTrue(f.row.published.isEmpty())
            f.manager.pause(f.id); f.manager.awaitIdle(f.id); assertEquals(262144L,f.row.offset); assertTrue(f.manager.payloadProgress.value.isEmpty()); assertEquals(job.sourceExpiresAt,f.row.sourceExpiresAt)
            f.source.before={}; f.manager.resume(f.id,f.peer,f.source::request).join(); f.manager.publish(f.id).join()
            assertEquals(f.row.error,PhoneDownloadPhase.COMPLETED,f.row.phase); assertArrayEquals(f.source.bytes,f.context.contentResolver.openInputStream(Uri.parse(f.row.published))!!.use {it.readBytes()})
        } finally {collection.cancelAndJoin(); scope.cancel(); f.source.before={}; f.dispose()}
    }
}
