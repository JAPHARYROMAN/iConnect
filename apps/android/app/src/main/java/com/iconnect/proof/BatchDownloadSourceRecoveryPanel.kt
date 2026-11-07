package com.iconnect.proof

import androidx.compose.material3.Text
import androidx.compose.runtime.*

@Composable
internal fun BatchDownloadSourceRecoveryPanel(row:PhoneDownload,path:String,estimate:PhoneTransferTimeEstimate,enabled:Boolean,
    prepare:suspend()->PhoneBatchDownloadSourceReview,confirm:suspend(PhoneBatchDownloadSourceReview)->Unit,cancel:(String)->Unit,
    observed:PhonePayloadRateEstimate?=null) {
    var envelope by remember(row.id) {mutableStateOf<PhoneBatchDownloadSourceReview?>(null)}
    Text("Retained batch file: $path")
    DownloadSourceRecoveryPanel(row,estimate,enabled,{prepare().also {envelope=it}.source},{source->
        val review=requireNotNull(envelope); check(review.source==source); confirm(review)
    },{id->cancel(id); if(envelope?.id==id) envelope=null},batchPath=path,observed=observed)
}
