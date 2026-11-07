package com.iconnect.proof

import androidx.compose.foundation.Image
import androidx.compose.foundation.gestures.detectTransformGestures
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.foundation.relocation.BringIntoViewRequester
import androidx.compose.foundation.relocation.bringIntoViewRequester
import androidx.compose.ui.draw.clipToBounds
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.unit.dp

@Composable
fun FilePreviewPanel(state: PhonePreviewState,page: (Int)->Unit,dismiss: ()->Unit) {
    if(state.name.isEmpty()) return
    var rotation by remember(state.name,state.frame) { mutableFloatStateOf(0f) }
    var zoom by remember(state.name,state.frame) { mutableFloatStateOf(1f) }
    var offset by remember(state.name,state.frame) { mutableStateOf(Offset.Zero) }
    val bring=remember { BringIntoViewRequester() }
    LaunchedEffect(state.name,state.frame) { bring.bringIntoView() }
    Card(Modifier.bringIntoViewRequester(bring)) { Column(Modifier.padding(14.dp),verticalArrangement=Arrangement.spacedBy(8.dp)) {
        Text("Preview · ${state.name}",style=MaterialTheme.typography.titleMedium)
        Text("PNG, JPEG and PDF up to 8 MiB. UTF-8 text: first 32 KiB.",style=MaterialTheme.typography.bodySmall)
        if(state.busy) { LinearProgressIndicator(Modifier.fillMaxWidth()); Text("Preparing preview…") }
        if(state.error.isNotEmpty()) Text(state.error,color=MaterialTheme.colorScheme.error)
        state.frame?.let { frame ->
            Box(Modifier.fillMaxWidth().height(360.dp).clipToBounds().pointerInput(frame) { detectTransformGestures { _,pan,scale,_ ->
                zoom=(zoom*scale).coerceIn(1f,4f)
                val next=offset+pan; offset=Offset(next.x.coerceIn(-size.width*(zoom-1)/2,size.width*(zoom-1)/2),next.y.coerceIn(-size.height*(zoom-1)/2,size.height*(zoom-1)/2))
            } }) {
                Image(frame.bitmap.asImageBitmap(),contentDescription="${state.name}, page ${frame.page+1} of ${frame.pages}",
                    modifier=Modifier.fillMaxSize().graphicsLayer(rotationZ=rotation,scaleX=zoom,scaleY=zoom,translationX=offset.x,translationY=offset.y),contentScale=ContentScale.Fit)
            }
            Text("Page ${frame.page+1} of ${frame.pages}")
            Row { if(frame.pages>1) {
                TextButton({ page(frame.page-1) },enabled=!state.busy && frame.page>0) { Text("Previous preview page") }
                TextButton({ page(frame.page+1) },enabled=!state.busy && frame.page+1<frame.pages) { Text("Next preview page") }
            } }
            TextButton({ rotation=(rotation+90f)%360f },enabled=!state.busy) { Text("Rotate preview") }
            Row { TextButton({ zoom=(zoom/1.5f).coerceAtLeast(1f); if(zoom==1f)offset=Offset.Zero }) { Text("Zoom out") }
                TextButton({ zoom=(zoom*1.5f).coerceAtMost(4f) }) { Text("Zoom in") }
                TextButton({ zoom=1f; rotation=0f; offset=Offset.Zero }) { Text("Reset view") } }
        }
        TextButton(dismiss) { Text("Close preview") }
    } }
}
