package com.iconnect.proof

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable

@Composable
internal fun LocalPayloadStatus(row:PhoneDownload,live:Long?,estimate:PhonePayloadRateEstimate?,finalization:PhoneIncomingFinalization?) {
    if(row.phase==PhoneDownloadPhase.DOWNLOADING && live!=null)
        Text("Live local payload: $live / ${row.size} bytes. Saved resume checkpoint: ${row.offset} bytes.",style=MaterialTheme.typography.bodySmall)
    Text(finalization?.describe() ?: localPayloadText(row,estimate),style=MaterialTheme.typography.bodySmall)
}

@Composable
internal fun UploadPayloadStatus(row:PhoneUpload,estimate:PhonePayloadRateEstimate?,limited:Boolean) {
    Text(uploadPayloadText(row,estimate,limited),style=MaterialTheme.typography.bodySmall)
}
