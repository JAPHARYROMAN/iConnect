package com.iconnect.proof

import android.content.Context
import com.iconnect.protocol.*
import org.json.JSONArray
import org.json.JSONObject
import java.io.File

internal data class AndroidMutation(val id: String, val peer: String, val fingerprint: String, val grant: String, val epoch: String,
    val tree: String, val parent: String, val names: List<String>, val name: String, val phase: MutationPhase = MutationPhase.CHANGE_PENDING,
    val status: FileStatus = FileStatus.FILE_STATUS_OK, val message: String = "The operation is awaiting reconciliation.", val result: String = "",
    val created:String="",val ancestors:List<String> = emptyList(),val requested:String="")
internal data class AndroidMutationResult(val name:String,val id:String)

/** SAF creation cannot be replayed after an uncertain provider call. Durable pending state becomes review. */
internal class AndroidMutations(context: Context, private val grants: AndroidStorageGrants) {
    private val directory = File(context.filesDir, "file-operations").also { check(it.mkdirs() || it.isDirectory) }
    private val file = PrivateJsonFile(File(directory, "journal.json"), 8 * 1024 * 1024)
    private val gate = synchronized(gates) { gates.getOrPut(directory.canonicalPath) { Any() } }
    private val recovery = StateRecovery(directory)
    private var epoch = ""
    private var records = emptyList<AndroidMutation>()
    init { synchronized(gate) { try { load() } catch (_: Exception) { recovery.add(File(directory, "journal.json"), "File-operation receipts need review. Writes are blocked; existing files are kept.") } } }
    fun recoveryIssues() = synchronized(gate) { recovery.all() }
    fun preservedRecords() = synchronized(gate) { recovery.archivedCount() }
    fun clearPreservedRecords() = synchronized(gate) { recovery.clearArchives() }
    fun archiveRecovery(id: String) = synchronized(gate) { if (recovery.all().none { it.id == id }) return@synchronized; recovery.archive(id); if (!recovery.needed()) { epoch = StorageRules.token(); save(emptyList()) } }
    private fun load() {
        if (!file.exists()) {
            check(!File(directory, "journal.json.new").exists()); epoch = StorageRules.token(); save(emptyList()); return
        }
        val root = JSONObject(file.read()); require(root.getInt("schema") == 1); val loadedEpoch = root.getString("epoch"); require(Rules.tokenPattern.matches(loadedEpoch))
        val rows = root.getJSONArray("records"); require(rows.length() <= 512)
        val loaded = (0 until rows.length()).map { i -> rows.getJSONObject(i).let { row ->
            val names = row.getJSONArray("names"); require(names.length() <= 128)
            AndroidMutation(row.getString("id"), row.getString("peer"), row.getString("fingerprint"), row.getString("grant"), row.getString("grantEpoch"),
                row.getString("tree"), row.getString("parent"), (0 until names.length()).map { names.getString(it) }, row.getString("name"),
                MutationPhase.forNumber(row.getInt("phase")) ?: error("Invalid phase"), FileStatus.forNumber(row.getInt("status")) ?: error("Invalid status"), row.getString("message"), row.getString("result"),
                row.optString("created",""),row.optJSONArray("ancestors")?.let {a->require(a.length()<=64);(0 until a.length()).map {a.getString(it)}}?:emptyList(),row.optString("requested",""))
        } }
        require(loaded.map { it.id }.distinct().size == loaded.size)
        loaded.forEach { row -> MutationRules.validate(info(row)); require(row.peer.matches(Regex("[a-f0-9]{64}")) && Rules.tokenPattern.matches(row.grant) &&
            Rules.tokenPattern.matches(row.epoch) && row.fingerprint.matches(Regex("[a-f0-9]{64}")) && row.parent.length in 1..4096)
            AndroidSafAccess.validateTree(android.net.Uri.parse(row.tree)); row.names.forEach(StorageRules::name); StorageRules.name(row.name)
            require(row.created.length<=4096 && row.created.none(Character::isISOControl) && row.ancestors.size<=64 && row.ancestors.distinct().size==row.ancestors.size &&
                row.ancestors.sumOf {it.length}<=64*1024 && row.ancestors.all {it.length in 1..4096 && it.none(Character::isISOControl)})
            if(row.requested.isNotEmpty()) StorageRules.name(row.requested)
            if(row.result.isNotEmpty()) StorageRules.name(row.result)
            if(row.created.isNotEmpty()) require(row.phase==MutationPhase.CHANGE_SUCCEEDED && row.ancestors.isNotEmpty() && row.ancestors.last()==row.parent && row.created !in row.ancestors)
        }
        epoch = loadedEpoch; records = loaded
    }
    private fun ready() {
        recovery.requireHealthy()
        try { load() } catch (_: Exception) { recovery.add(File(directory, "journal.json"), "File-operation receipts changed or could not be read. Review them on this phone before writing."); recovery.requireHealthy() }
    }
    private fun save(updated: List<AndroidMutation>) {
        val rows = JSONArray(); updated.forEach { row -> rows.put(JSONObject().put("id", row.id).put("peer", row.peer).put("fingerprint", row.fingerprint)
            .put("grant", row.grant).put("grantEpoch", row.epoch).put("tree", row.tree).put("parent", row.parent).put("names", JSONArray(row.names)).put("name", row.name)
            .put("phase", row.phase.number).put("status", row.status.number).put("message", row.message).put("result", row.result)
            .put("created",row.created).put("ancestors",JSONArray(row.ancestors)).put("requested",row.requested)) }
        try { file.write(JSONObject().put("schema", 1).put("epoch", epoch).put("records", rows).toString()) }
        catch (_: Exception) { throw StorageFailure(FileStatus.IO_ERROR, "File-operation progress could not be saved. Review recent changes before trying again.") }
        records = updated
    }
    private fun put(row: AndroidMutation): AndroidMutation { save(records.filter { it.id != row.id } + row); return row }
    private fun authorize(row: AndroidMutation) {
        val grant = grants.get(row.grant, row.peer); StorageRules.requirePermission(grant.permissions, StorageRules.CREATE)
        if (grant.trustEpoch != row.epoch || grant.tree.toString() != row.tree || (row.names + row.name + listOf(row.requested,row.result).filter {it.isNotEmpty()}).any { !grants.visible(grant, it) })
            throw StorageFailure(FileStatus.ACCESS_DENIED, "This operation no longer belongs to a current folder approval.")
    }
    private fun reconcile(row: AndroidMutation) = if (row.phase != MutationPhase.CHANGE_PENDING) row else put(row.copy(phase = MutationPhase.CHANGE_REVIEW, status = FileStatus.CONFLICT,
        message = "The provider may have created this folder. Refresh the destination before deciding on another operation."))
    fun query(peer: String, request: FileRequest): FileResponse = synchronized(gate) {
        ready(); val response = FileResponse.newBuilder().setRequestId(request.requestId).setMutationEpoch(epoch)
        if (request.operation == FileOperation.MUTATION_CONTEXT) return@synchronized response.build()
        if (request.mutationEpoch != epoch) throw StorageFailure(FileStatus.STALE_REFERENCE, "File-operation history was reset. Refresh its context.")
        if (request.operation == FileOperation.MUTATION_STATUS) {
            val row = records.singleOrNull { it.id == request.mutationId && it.peer == peer } ?: throw StorageFailure(FileStatus.NOT_FOUND, "No current receipt exists. Refresh files before another change.")
            authorize(row); response.mutation = info(reconcile(row)); return@synchronized response.build()
        }
        require(request.offset in 0..512)
        val rows = records.filter { it.peer == peer }.sortedWith(compareByDescending<AndroidMutation> { MutationRules.created(it.id) }.thenBy { it.id })
        rows.drop(request.offset.toInt()).take(32).forEach { row -> try { authorize(row); response.addMutations(info(reconcile(row))) } catch (_: StorageFailure) { } }
        response.endOfFile = request.offset + 32 >= rows.size; response.build()
    }
    fun createdDirectory(peer:String,request:FileRequest):AndroidMutation=synchronized(gate) {
        ready(); if(request.mutationEpoch!=epoch) throw StorageFailure(FileStatus.STALE_REFERENCE,"File-operation history changed. Review a current context.")
        var row=records.singleOrNull {it.id==request.mutationId && it.peer==peer}?:throw StorageFailure(FileStatus.NOT_FOUND,"No matching created-folder receipt exists for this device.")
        authorize(row); row=reconcile(row)
        if(row.phase!=MutationPhase.CHANGE_SUCCEEDED || row.created.isEmpty() || row.ancestors.isEmpty()) throw StorageFailure(FileStatus.CONFLICT,"This receipt has no confirmed created-folder identity. Review the destination before a deliberately new operation.")
        row
    }
    fun execute(peer: String, request: FileRequest, prepare: () -> AndroidMutation, apply: () -> AndroidMutationResult): MutationInfo = synchronized(gate) {
        ready(); if (request.mutationEpoch != epoch) throw StorageFailure(FileStatus.STALE_REFERENCE, "Refresh the operation context before making a change.")
        MutationRules.current(request.mutationId)
        records.firstOrNull { it.id == request.mutationId }?.let { old ->
            if (old.peer != peer || old.fingerprint != MutationRules.fingerprint(request)) throw StorageFailure(FileStatus.CONFLICT, "This operation identifier belongs to a different request.")
            authorize(old); return@synchronized info(reconcile(old))
        }
        val kept = records.filter { MutationRules.created(it.id) >= System.currentTimeMillis() - MutationRules.LIFETIME }
        if (kept.size >= 512 || kept.count { it.peer == peer } >= 128) throw StorageFailure(FileStatus.RESOURCE_EXHAUSTED, "The receipt limit was reached. Wait for earlier changes to expire.")
        val row = prepare(); authorize(row); save(kept + row)
        try { val applied=apply(); info(put(row.copy(phase = MutationPhase.CHANGE_SUCCEEDED, result = applied.name, created=applied.id,message = "The change completed."))) }
        catch (e: Exception) { try { info(reconcile(row)) } catch (_: Exception) { throw StorageFailure(FileStatus.IO_ERROR, "Progress could not be committed. Refresh the operation history before another change.") } }
    }
    internal fun info(row: AndroidMutation) = MutationInfo.newBuilder().setId(row.id).setOperation(FileOperation.CREATE_DIRECTORY).setPhase(row.phase)
        .setStatus(row.status).setMessage(row.message).setResultingName(row.result).setRequestedName(row.requested.ifEmpty {row.name}).setCreatedAt(MutationRules.created(row.id)).build()
    companion object { private val gates = mutableMapOf<String, Any>() }
}
