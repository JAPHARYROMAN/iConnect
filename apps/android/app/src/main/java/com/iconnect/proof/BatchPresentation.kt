package com.iconnect.proof

import com.iconnect.protocol.*

internal fun batchPath(manifest:UploadBatchManifest,index:Int):String {
    val names=mutableListOf<String>(); var at=index
    while(at>=0) {val item=manifest.getItems(at); names.add(item.name); at=item.parent}
    return names.asReversed().joinToString(" / ")
}
internal fun batchCollision(collision:UploadCollision)=when(collision) {
    UploadCollision.UPLOAD_KEEP_BOTH->"Keep both copies"
    UploadCollision.UPLOAD_SKIP->"Skip existing names"
    else->"Report existing names as issues"
}
data class PhoneBatchResultView(val index:Int,val name:String,val phase:String,val message:String)
