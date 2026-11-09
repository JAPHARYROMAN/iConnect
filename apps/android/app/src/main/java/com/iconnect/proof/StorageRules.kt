package com.iconnect.proof

import com.iconnect.protocol.FileStatus
import java.security.MessageDigest
import java.security.SecureRandom

class StorageFailure(val status: FileStatus, message: String) : Exception(message)

object StorageRules {
    private val reservedDeviceNames=setOf("CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$")
    private val reservedDevicePort=Regex("(?:COM|LPT)[1-9¹²³]")
    private val protectedFolders=setOf(".ssh", ".aws", ".azure", ".gnupg", ".kube", ".codex", ".git", "android")
    const val LIST = 1
    const val READ = 2
    const val WRITE = 4
    const val CREATE = 8
    const val RENAME = 16
    const val MOVE = 32
    const val DELETE = 64
    const val REPLACE = 128
    const val READ_ONLY = LIST or READ
    const val SUPPORTED = READ_ONLY or CREATE or WRITE
    const val PAGE = 32
    const val RANGE = 32 * 1024
    const val MIN_TIMESTAMP = -62_135_596_800_000L
    const val MAX_TIMESTAMP = 253_402_300_799_999L
    const val MAX_TRANSFER_SIZE = 8L * 1024 * 1024 * 1024 * 1024
    const val DOWNLOAD_CAPABILITY = "downloads-v1"
    fun token() = Rules.b64(ByteArray(32).also { SecureRandom().nextBytes(it) })
    fun requirePermission(granted: Int, required: Int) {
        if (granted and required != required) throw StorageFailure(FileStatus.ACCESS_DENIED,
            "This device does not have permission for this action.")
    }
    fun name(value: String) {
        val stem = value.substringBefore('.').uppercase(java.util.Locale.ROOT)
        if (value.length !in 1..255 || value == "." || value == ".." || value.endsWith('.') || value.endsWith(' ') ||
            value.any { Character.isISOControl(it) || it in "<>:\"/\\|?*" } ||
            value.startsWith(".iconnect-", true) || stem in reservedDeviceNames ||
            reservedDevicePort.matches(stem) ||
            !Charsets.UTF_8.newEncoder().canEncode(value))
            throw StorageFailure(FileStatus.INVALID_REQUEST, "Choose a supported file or folder name.")
    }
    fun visible(value: String, exclusions: Set<String>): Boolean = try {
        name(value)
        val folded=value.lowercase(java.util.Locale.ROOT)
        folded !in exclusions && folded !in protectedFolders
    } catch (_: StorageFailure) { false }
    fun version(id: String, size: Long, modified: Long): String = Rules.hex(MessageDigest.getInstance("SHA-256")
        .digest("$id:$size:$modified".toByteArray(Charsets.UTF_8)))
}
