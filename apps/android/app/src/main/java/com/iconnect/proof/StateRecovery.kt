package com.iconnect.proof

import android.system.Os
import android.system.OsConstants
import java.io.File
import java.nio.file.Files

data class StateRecoveryIssue(val id: String, val record: String, val message: String, val canArchive: Boolean)

/** Metadata-only recovery. Transfer staging and provider documents are never removed here. */
internal class StateRecovery(private val directory: File) {
    private data class Stamp(val name: String, val device: Long, val inode: Long, val size: Long, val written: Long)
    private data class Issue(val public: StateRecoveryIssue, val files: List<Stamp>)
    private val issues = linkedMapOf<String, Issue>()
    fun all() = issues.values.map { it.public }
    fun needed() = issues.isNotEmpty()
    fun block(message: String) { val id = StorageRules.token(); issues[id] = Issue(StateRecoveryIssue(id, "Queue storage", message, false), emptyList()) }
    fun archivedCount() = File(directory, "recovery").listFiles().orEmpty().take(257).size
    fun clearArchives() {
        val shelf = File(directory, "recovery"); if (!shelf.exists()) return
        check(shelf.canonicalFile.parentFile == directory.canonicalFile && OsConstants.S_ISDIR(Os.lstat(shelf.path).st_mode))
        for (archive in shelf.listFiles().orEmpty().take(257)) {
            if (archive.name in issues) continue
            check(Rules.tokenPattern.matches(archive.name) && archive.canonicalFile.parentFile == shelf.canonicalFile && OsConstants.S_ISDIR(Os.lstat(archive.path).st_mode))
            val files = archive.listFiles().orEmpty(); check(files.size <= 3 && files.all { file ->
                file.canonicalFile.parentFile == archive.canonicalFile && OsConstants.S_ISREG(Os.lstat(file.path).st_mode) &&
                    (file.name.endsWith(".json") || file.name.endsWith(".json.new") || file.name.endsWith(".json.bak"))
            }) { "Recovery storage contains an unexpected item. It was kept." }
            files.forEach { check(it.delete()) { "A metadata backup could not be removed." } }; check(archive.delete())
        }
    }
    fun requireHealthy() { if (needed()) throw StorageFailure(com.iconnect.protocol.FileStatus.CONFLICT,
        "Saved transfer progress needs review. Open Downloads on this phone before starting or resuming work.") }
    private fun stamp(file: File): Stamp {
        check(file.canonicalFile.parentFile == directory.canonicalFile)
        val stat = Os.lstat(file.path); check(OsConstants.S_ISREG(stat.st_mode))
        return Stamp(file.name, stat.st_dev, stat.st_ino, stat.st_size, file.lastModified())
    }
    fun add(file: File, message: String) {
        val family = listOf(file, File(file.path + ".bak"), File(file.path + ".new"))
        val stamps = runCatching { family.filter { it.exists() }.map(::stamp) }.getOrDefault(emptyList())
        val id = StorageRules.token()
        issues[id] = Issue(StateRecoveryIssue(id, file.name.filterNot(Character::isISOControl).take(100), message, stamps.isNotEmpty()), stamps)
    }
    fun archive(id: String, after: (String) -> Unit = {}) {
        val issue = issues[id] ?: return
        check(issue.public.canArchive) { "Restore access to this saved record and restart the app before reviewing it." }
        issue.files.forEach { saved -> val file = File(directory, saved.name)
            if (file.exists()) check(stamp(file) == saved) { "This saved record changed. Restart the app and review its current state." }
        }
        val shelf = File(directory, "recovery"); check(shelf.mkdir() || shelf.isDirectory)
        check(shelf.canonicalFile.parentFile == directory.canonicalFile && OsConstants.S_ISDIR(Os.lstat(shelf.path).st_mode))
        val target = File(shelf, id)
        check(target.isDirectory || shelf.list().orEmpty().size < 256) { "The recovery folder is full. Review preserved records there before archiving more." }
        check(target.mkdir() || target.isDirectory)
        check(target.canonicalFile.parentFile == shelf.canonicalFile && OsConstants.S_ISDIR(Os.lstat(target.path).st_mode))
        for (saved in issue.files) {
            val from = File(directory, saved.name); val to = File(target, saved.name)
            if (!from.exists()) {
                val old = Os.lstat(to.path); check(old.st_ino == saved.inode && old.st_dev == saved.device)
                continue
            }
            check(stamp(from) == saved) { "This saved record changed. Restart the app and review its current state." }
            check(!to.exists()); Files.move(from.toPath(), to.toPath())
        }
        after(issue.public.record.removeSuffix(".json")); issues.remove(id)
    }
    companion object {
        fun journals(directory: File, maximum:Int=1024): List<File> = Files.newDirectoryStream(directory.toPath()).use { stream -> stream.asSequence().map { it.toFile() }
            .filter { it.name.endsWith(".json") || it.name.endsWith(".json.bak") || it.name.endsWith(".json.new") }
            .map { if (it.name.endsWith(".json")) it else File(it.path.dropLast(4)) }.distinctBy { it.name }.take(maximum+1).toList().sortedBy { it.name } }
    }
}
