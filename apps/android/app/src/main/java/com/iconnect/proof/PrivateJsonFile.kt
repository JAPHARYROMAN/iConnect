package com.iconnect.proof

import android.util.AtomicFile
import java.io.File

/** Bounded private state. Explicit sync and read-back catch AtomicFile's logged rename failures. */
internal class PrivateJsonFile(path: File, private val limit: Int) {
    private val file = AtomicFile(path)
    fun exists() = file.baseFile.exists() || File(file.baseFile.path + ".bak").exists()
    fun read(): String = file.openRead().use {
        check(it.channel.size() <= limit) { "Private state exceeds its size limit." }
        it.readBytes().toString(Charsets.UTF_8)
    }
    fun write(text: String) {
        val bytes = text.toByteArray(Charsets.UTF_8); check(bytes.size <= limit)
        val parent = requireNotNull(file.baseFile.parentFile); check(parent.mkdirs() || parent.isDirectory)
        val output = file.startWrite()
        try {
            output.write(bytes); output.fd.sync(); file.finishWrite(output)
            check(read() == text) { "Private state could not be committed." }
        } catch (e: Exception) { runCatching { file.failWrite(output) }; throw e }
    }
}
