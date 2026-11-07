package com.iconnect.proof

import com.iconnect.protocol.*
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import java.nio.ByteBuffer
import java.nio.CharBuffer
import java.nio.charset.CodingErrorAction
import java.util.Locale

enum class PreviewKind { TEXT, PNG, JPEG, PDF }
data class PreviewInput(val kind: PreviewKind, val name: String, val bytes: ByteArray, val eof: Boolean)
object PreviewRules {
    const val MAX_BYTES = 8 * 1024 * 1024
    const val MAX_PIXELS = 16 * 1024 * 1024
    const val MAX_SIDE = 8192
    const val RENDER_SIDE = 1280
    const val MAX_PAGES = 128
    fun kind(name: String) = when(name.substringAfterLast('.', "").lowercase(Locale.ROOT)) { "png" -> PreviewKind.PNG; "jpg", "jpeg" -> PreviewKind.JPEG; "pdf" -> PreviewKind.PDF; else -> PreviewKind.TEXT }
    fun dimensions(width: Int, height: Int): Pair<Int, Int> {
        require(width in 1..MAX_SIDE && height in 1..MAX_SIDE && width.toLong() * height <= MAX_PIXELS) { "This image exceeds the preview dimension limit. Save it to open it separately." }
        return width to height
    }
    fun probe(kind: PreviewKind, bytes: ByteArray): Pair<Int, Int>? {
        require(bytes.size in 1..MAX_BYTES) { "The preview file is empty or exceeds 8 MiB." }
        fun u(at: Int) = bytes[at].toInt() and 255
        fun word(at: Int) = (u(at) shl 8) or u(at + 1)
        when(kind) {
            PreviewKind.PDF -> { val header = bytes.take(8).toByteArray().toString(Charsets.US_ASCII)
                require(bytes.size >= 9 && (Regex("%PDF-1\\.[0-7]").matches(header) || header == "%PDF-2.0") && u(8) in listOf(10,13,32)) { "This file does not have a supported PDF header." }; return null }
            PreviewKind.PNG -> {
                require(bytes.size >= 33 && bytes.copyOfRange(0,8).contentEquals(byteArrayOf(-119,80,78,71,13,10,26,10)) && ByteBuffer.wrap(bytes,8,4).int == 13 && bytes.copyOfRange(12,16).toString(Charsets.US_ASCII) == "IHDR") { "This file does not have a supported PNG header." }
                return dimensions(ByteBuffer.wrap(bytes,16,4).int, ByteBuffer.wrap(bytes,20,4).int)
            }
            PreviewKind.JPEG -> {
                require(bytes.size >= 4 && u(0) == 255 && u(1) == 216) { "This file does not have a supported JPEG header." }
                var at = 2; var markers = 0
                while(at < minOf(bytes.size, 256 * 1024) && ++markers <= 4096) {
                    if(u(at++) != 255) break
                    var fill = 0; while(at < bytes.size && u(at) == 255 && ++fill <= 32) at++
                    if(at >= bytes.size || fill > 32) break
                    val marker = u(at++); if(marker in listOf(0,218,217,216)) break
                    if(marker == 1 || marker in 208..215) continue
                    if(at + 2 > bytes.size) break
                    val length = word(at); if(length < 2 || at + length > bytes.size) break
                    if(marker == 192 || marker == 194) { if(length < 8 || u(at+2) != 8 || u(at+7) !in listOf(1,3) || length != 8 + 3 * u(at+7)) break
                        return dimensions(word(at+5), word(at+3)) }
                    if(marker in 192..207 && marker !in listOf(196,200,204)) break
                    at += length
                }
                error("This JPEG structure is unsupported or exceeds the metadata limit.")
            }
            PreviewKind.TEXT -> return null
        }
    }
    fun text(input: PreviewInput): String {
        require(input.bytes.none { it == 0.toByte() }) { "Preview supports PNG, JPEG, PDF and UTF-8 plain text. Save this file to open it separately." }
        val chars = CharBuffer.allocate(input.bytes.size)
        val result = Charsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT).onUnmappableCharacter(CodingErrorAction.REPORT).decode(ByteBuffer.wrap(input.bytes), chars, input.eof)
        if(result.isError) result.throwException(); chars.flip()
        return chars.toString().filter { !Character.isISOControl(it) || it in "\n\r\t" }
    }
    suspend fun fetch(entry: FileEntry, request: suspend (FileRequest) -> FileResponse): PreviewInput {
        require(!entry.isDirectory && entry.size >= 0 && entry.permissions and StorageRules.READ != 0) { "Choose an approved readable file." }
        val kind = kind(entry.name); require(kind == PreviewKind.TEXT || entry.size in 1..MAX_BYTES.toLong()) { "Image and PDF previews support files up to 8 MiB. Save this file to open it separately." }
        fun metadata(reply: FileResponse) {
            PhoneTransferManifests.validateResponse(reply); check(reply.status == FileStatus.FILE_STATUS_OK) { reply.message }
            val current = reply.entriesList.singleOrNull()
            check(current != null && current.id == entry.id && current.grantId == entry.grantId && current.version == entry.version && current.size == entry.size && current.name == entry.name && !current.isDirectory && current.permissions and StorageRules.READ != 0) { "This file changed or is no longer approved. Refresh it before previewing." }
        }
        metadata(request(FileRequest.newBuilder().setOperation(FileOperation.GET_METADATA).setNodeId(entry.id).build()))
        val length = minOf(entry.size, (if(kind == PreviewKind.TEXT) StorageRules.RANGE else MAX_BYTES).toLong()).toInt(); val bytes = ByteArray(length)
        var offset = 0; var eof = entry.size == 0L; var requests = 0
        while(offset < length) { currentCoroutineContext().ensureActive(); check(++requests <= 512) { "The preview source returned too many short ranges." }
            val reply = request(FileRequest.newBuilder().setOperation(FileOperation.READ_RANGE).setNodeId(entry.id).setExpectedVersion(entry.version).setOffset(offset.toLong()).setLength(minOf(StorageRules.RANGE, length-offset)).build())
            metadata(reply); val count = reply.content.size()
            check(count in 1..minOf(StorageRules.RANGE, length-offset) && reply.endOfFile == (offset.toLong() + count == entry.size)) { "The preview source returned inconsistent file ranges." }
            reply.content.asReadOnlyByteBuffer().get(bytes,offset,count); offset += count; eof = reply.endOfFile
        }
        metadata(request(FileRequest.newBuilder().setOperation(FileOperation.GET_METADATA).setNodeId(entry.id).build()))
        if(kind != PreviewKind.TEXT) probe(kind,bytes)
        return PreviewInput(kind,entry.name,bytes,eof)
    }
}
