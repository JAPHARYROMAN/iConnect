package com.iconnect.proof

import android.content.Context
import android.content.ContentResolver
import android.database.Cursor
import android.net.Uri
import android.os.CancellationSignal
import android.provider.DocumentsContract as Documents
import com.google.protobuf.ByteString
import com.iconnect.protocol.*
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.runInterruptible
import java.io.FileInputStream
import java.io.FileNotFoundException
import java.io.IOException
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.coroutines.coroutineContext

/** Session-scoped opaque references. Provider IDs, tree URIs and local paths never cross the channel. */
class AndroidFileService internal constructor(context: Context, private val grants: DocumentAccessPolicy,
    private val downloads: AndroidDownloadSources? = null,
    private val available: () -> Boolean = { true },
    private val requestAllowed: (FileRequest) -> Boolean = { true }, private val incoming:AndroidIncomingUploads?=null) : AutoCloseable {
    constructor(context: Context, grants: AndroidStorageGrants, downloads: AndroidDownloadSources? = null,
        available: () -> Boolean = { true }, requestAllowed: (FileRequest) -> Boolean = { true },incoming:AndroidIncomingUploads?=null) :
        this(context, grants as DocumentAccessPolicy, downloads, available, requestAllowed,incoming)
    val supportsIncoming get()=incoming!=null && grants is AndroidStorageGrants
    val supportsCreatedDirectoryReferences get()=grants is AndroidStorageGrants
    private val downloadScope = StorageRules.token()
    @Volatile private var batchPlanHost:AndroidBatchPlans?=null
    private val resolver = context.contentResolver
    private data class Node(val grant: String, val document: String, val parent: String?, val names: List<String>)
    private data class Resolved(val node: Node, val grant: AndroidStorageGrant, val info: Metadata)
    private data class Ancestry(val path: List<String>, val leaf: Metadata)
    internal data class Metadata(val id: String, val name: String, val directory: Boolean,
        val size: Long, val modified: Long, val flags: Int, val knownSize: Boolean) {
        val version get() = StorageRules.version(id, size, modified)
    }
    private data class Page(val token: String, val node: String, val cursor: Cursor, val created: Long, val peer: String)
    private val nodes = LinkedHashMap<String, Node>(32, .75f, true)
    private val ids = mutableMapOf<Pair<String, String>, String>()
    private var page: Page? = null
    private val lock = Any()
    private val closed = AtomicBoolean(false)
    @Volatile private var activeSignal: CancellationSignal? = null
    @Volatile private var activeRequest: FileRequest? = null
    internal var activity:(PhoneActivityEvent)->Unit={}

    suspend fun execute(peer: String, request: FileRequest): FileResponse {
        coroutineContext.ensureActive()
        return runInterruptible(Dispatchers.IO) {
            // Cancel only an already-bound worker before either service/owner lock.
            // Durable stop and all filesystem actions still require authorization below.
            if(!closed.get() && Rules.tokenPattern.matches(request.requestId)) {
                if(request.operation==FileOperation.CANCEL_PUBLISHED_BATCH_CHECK) {
                    val host=batchPlanHost?:(grants as? AndroidStorageGrants)?.existingBatchPlans()
                    host?.publishedCancellationRefusal(peer,downloadScope,request)?.let {refusal->
                        return@runInterruptible error(request,refusal.status,refusal.message?:"This check cannot be stopped by this request.")
                    }
                }
                batchPlanHost?.pauseForStop(peer,request)
                interruptPublishedBatchCheck(peer,request)
            }
            synchronized(lock) {
                check(!closed.get())
                val signal = CancellationSignal()
                activeRequest = request; activeSignal = signal
                if (closed.get()) signal.cancel()
                try {
                    grants.authorized {
                        if (!available() || !requestAllowed(request)) throw StorageFailure(FileStatus.ACCESS_DENIED, "Phone storage is paused or unavailable for this request.")
                        if (!Rules.tokenPattern.matches(request.requestId))
                            throw StorageFailure(FileStatus.INVALID_REQUEST, "Invalid request identifier.")
                        val response = FileResponse.newBuilder().setRequestId(request.requestId)
                        if(UploadBatchRules.isRpc(request.operation)) {
                            val approvals=grants as? AndroidStorageGrants?:throw StorageFailure(FileStatus.UNSUPPORTED,"Local browser shortcuts cannot admit remote batches.")
                            val files=incoming?:throw StorageFailure(FileStatus.UNSUPPORTED,"This host does not accept incoming copies.")
                            val host=batchPlanHost?:approvals.batchPlans.also {batchPlanHost=it}
                            return@authorized host.execute(peer,downloadScope,request,{
                                val (node,grant)=resolve(peer,request.destinationId,StorageRules.WRITE or StorageRules.CREATE,signal)
                                val info=metadata(grant,node.document,signal)
                                if(!info.directory || info.flags and Documents.Document.FLAG_DIR_SUPPORTS_CREATE==0) throw StorageFailure(FileStatus.UNSUPPORTED,"Choose a receiving folder that supports new items.")
                                val path=validateAncestry(grant,node.document,signal).drop(1).map {id->SafFolderBinding(id,metadata(grant,id,signal).name)}
                                IncomingTarget(grant.id,grant.tree.toString(),grant.trustEpoch,path)
                            },files::reservedBytes,{!closed.get() && available() && requestAllowed(request)},signal)
                        }
                        if(UploadRules.isRpc(request.operation)) {
                            val host=incoming?.takeIf {grants is AndroidStorageGrants}?:throw StorageFailure(FileStatus.UNSUPPORTED,"This location does not accept remote incoming copies.")
                            val reply=host.execute(peer,request,{
                                val (node,grant)=resolve(peer,request.destinationId,StorageRules.WRITE or StorageRules.CREATE,signal)
                                val info=metadata(grant,node.document,signal)
                                if(!info.directory) throw StorageFailure(FileStatus.INVALID_REQUEST,"Choose an approved incoming folder.")
                                val path=validateAncestry(grant,node.document,signal).drop(1).map {id->SafFolderBinding(id,metadata(grant,id,signal).name)}
                                IncomingTarget(grant.id,grant.tree.toString(),grant.trustEpoch,path)
                            },downloadScope,{!closed.get() && available() && requestAllowed(request)},signal)
                            if(request.operation!=FileOperation.UPLOAD_CONTEXT) return@authorized reply
                            val result=reply.toBuilder()
                            grants.forPeer(peer).take(32).forEach {grant->
                                if(grant.permissions and 12!=12) return@forEach
                                try {
                                    val meta=metadata(grant,rootId(grant),signal)
                                    if(!meta.directory || meta.flags and Documents.Document.FLAG_DIR_SUPPORTS_CREATE==0 || !grants.visible(grant,meta.name)) return@forEach
                                    val entry=issue(Node(grant.id,meta.id,null,emptyList()),grant,meta).toBuilder()
                                    if(grant.permissions and StorageRules.LIST==0) entry.setModifiedAt(0).setSize(0).setVersion(StorageRules.version("incoming:${grant.id}:${meta.id}",0,0))
                                    result.addEntries(entry)
                                } catch(_:SecurityException) {} catch(_:FileNotFoundException) {}
                            }
                            return@authorized result.build()
                        }
                        when (request.operation) {
                            FileOperation.RESOLVE_CREATED_DIRECTORY -> {
                                response.addEntries(resolveCreatedDirectory(peer,request,response,signal))
                                return@authorized response.build()
                            }
                            FileOperation.MUTATION_CONTEXT, FileOperation.MUTATION_STATUS, FileOperation.LIST_MUTATIONS ->
                                return@authorized mutationHost().query(peer, request)
                            FileOperation.PREPARE_DOWNLOAD -> {
                                val (node, grant) = resolve(peer, request.nodeId, StorageRules.READ, signal)
                                val info = sourceSnapshot(peer, grant.id, node.document, request.expectedVersion, signal)
                                response.download = requireDownloads().prepare(peer, grant.id, node.document, info.name,
                                    info.version, info.size, downloadScope)
                            }
                            FileOperation.DOWNLOAD_STATUS -> response.download = requireDownloads().status(peer, request.transferId, downloadScope, this, signal)
                            FileOperation.DOWNLOAD_READ -> return@authorized requireDownloads().read(peer, request, this, signal, downloadScope)
                            FileOperation.RELEASE_DOWNLOAD -> requireDownloads().release(peer, request.transferId)
                            FileOperation.LIST_ROOTS -> roots(peer, response, signal)
                            FileOperation.LIST_DIRECTORY -> list(peer, request, response, signal)
                            FileOperation.CLOSE_LISTING -> if (page?.let { it.peer == peer && it.node == request.nodeId && it.token == request.continuation } == true) clearPage()
                            FileOperation.GET_METADATA -> {
                                val (node, grant) = resolve(peer, request.nodeId, StorageRules.LIST, signal)
                                response.addEntries(describe(request.nodeId, node, grant, metadata(grant, node.document, signal)))
                            }
                            FileOperation.READ_RANGE -> read(peer, request, response, signal)
                            FileOperation.CREATE_DIRECTORY -> create(peer, request, response, signal)
                            FileOperation.RENAME -> rename(peer, request, response, signal)
                            else -> throw StorageFailure(FileStatus.UNSUPPORTED, "This provider operation is not available yet.")
                        }
                        response.build()
                    }
                } catch (e: StorageFailure) { error(request, e.status, e.message ?: "Storage is unavailable.") }
                catch (_: android.os.OperationCanceledException) { clearPage(); error(request, FileStatus.ACCESS_DENIED, "Phone storage availability ended.") }
                catch (_: SecurityException) { clearPage(); error(request, FileStatus.ACCESS_DENIED, "Android or the document provider denied access. Select the folder again if needed.") }
                catch (_: FileNotFoundException) { clearPage(); error(request, FileStatus.NOT_FOUND, "The document is no longer available.") }
                catch (_: UnsupportedOperationException) { clearPage(); error(request, FileStatus.UNSUPPORTED, "The document provider does not support this safe operation.") }
                catch (_: IOException) { clearPage(); error(request, FileStatus.IO_ERROR, "The document provider could not finish this operation.") }
                catch (_: IllegalArgumentException) { clearPage(); error(request, FileStatus.INVALID_REQUEST, "The document request is invalid.") }
                catch(e:IllegalStateException) {if(UploadRules.isRpc(request.operation) || request.operation in setOf(FileOperation.VERIFY_PUBLISHED_BATCH,FileOperation.CANCEL_PUBLISHED_BATCH_CHECK)) error(request,FileStatus.CONFLICT,"Incoming copy could not be confirmed. Review its receipt and the preserved files.") else throw e}
                finally { activeSignal = null; activeRequest = null; if (closed.get()) clear() }
            }
        }.also {reply->
            // Successful range/status polling would overwhelm history and does
            // not itself establish file completion. Refusals remain observable.
            val quiet=request.operation in quietOperations
            if(Rules.tokenPattern.matches(request.requestId) && (!quiet || reply.status!=FileStatus.FILE_STATUS_OK)) try {
                activity(PhoneActivityEvent(PhoneActivityKind.FILE_REQUEST,when(reply.status) {FileStatus.FILE_STATUS_OK->PhoneActivityOutcome.ACCEPTED; FileStatus.ACCESS_DENIED,FileStatus.UNSUPPORTED->PhoneActivityOutcome.REFUSED; else->PhoneActivityOutcome.FAILED},
                    peer,operation=if(request.operation==FileOperation.UNRECOGNIZED) -1 else request.operation.number))
            } catch(_:Exception) {}
        }
    }
    fun suspendAvailability(preserve: Set<String> = emptySet()) {
        batchPlanHost?.cancelFileScopeNow(downloadScope)
        val current = activeRequest
        if (current == null || current.transferId !in preserve || current.operation !in setOf(FileOperation.DOWNLOAD_STATUS, FileOperation.DOWNLOAD_READ)) activeSignal?.cancel()
        downloads?.cancelScope(downloadScope, preserve)
        incoming?.cancelScope(downloadScope)
    }
    fun interruptSource(id: String) { if (activeRequest?.transferId == id) activeSignal?.cancel() }
    private fun requireDownloads() = downloads ?: throw StorageFailure(FileStatus.UNSUPPORTED, "Real-file downloads are unavailable in this host.")
    private fun mutationHost() = (grants as? AndroidStorageGrants)?.mutations ?: throw StorageFailure(FileStatus.UNSUPPORTED, "File changes are unavailable in this local browser.")
    internal fun sourceSnapshot(peer: String, grantId: String, documentId: String, version: String, signal: CancellationSignal): Metadata {
        val grant = grants.get(grantId, peer); StorageRules.requirePermission(grant.permissions, StorageRules.READ)
        checkChild(grant, documentId)
        val info = inspectAncestry(grant, documentId, signal).leaf
        requireVersion(version, info.version)
        if (info.directory || !info.knownSize || info.size > StorageRules.MAX_TRANSFER_SIZE ||
            info.flags and Documents.Document.FLAG_VIRTUAL_DOCUMENT != 0 || !grants.visible(grant, info.name))
            throw StorageFailure(FileStatus.UNSUPPORTED, "Choose a normal file with a known size.")
        return info
    }
    /** Internal local selection only. This is never an RPC or a paired-device grant. */
    internal fun captureLocalFile(peer: String, entry: FileEntry): LocalCopySelection = synchronized(lock) {
        require(!entry.isDirectory)
        captureLocalItem(peer, entry)
    }
    internal fun captureLocalFolder(peer:String,entry:FileEntry,name:String):LocalFolderTarget=synchronized(lock) {
        check(grants is LocalDocumentLocations && !closed.get() && available()); StorageRules.name(name)
        val signal=CancellationSignal(); val (node,grant)=resolve(peer,entry.id,StorageRules.LIST,signal)
        val info=metadata(grant,node.document,signal)
        check(entry.grantId==grant.id && entry.isDirectory && info.directory && entry.name==info.name && entry.version==info.version) {"Refresh the chosen local folder."}
        val path=validateAncestry(grant,node.document,signal)
        val parents=path.drop(1).map {id->SafFolderBinding(id,metadata(grant,id,signal).name)}; SafDestination.validateBindings(parents)
        LocalFolderTarget(grant.id,grant.tree,metadata(grant,rootId(grant),signal).name,parents,name,android.os.SystemClock.elapsedRealtime())
    }
    internal fun captureLocalItem(peer: String, entry: FileEntry): LocalCopySelection = synchronized(lock) {
        check(grants is LocalDocumentLocations && !closed.get() && available())
        val node = nodes[entry.id] ?: error("Refresh the local folder before copying this file.")
        val grant = grants.get(node.grant, peer)
        require(entry.grantId == grant.id && entry.permissions and (if (entry.isDirectory) StorageRules.LIST else StorageRules.READ) != 0)
        LocalCopySelection(grant.tree, node.document, entry)
    }
    internal fun localSourceItem(grantId: String, documentId: String, signal: CancellationSignal): Metadata {
        val grant = grants.get(grantId, ""); checkChild(grant, documentId); validateAncestry(grant, documentId, signal)
        val info = metadata(grant, documentId, signal)
        StorageRules.requirePermission(grant.permissions, if (info.directory) StorageRules.LIST else StorageRules.READ)
        check(grants.visible(grant, info.name)); return info
    }
    internal fun localSourceChildren(grantId: String, documentId: String, signal: CancellationSignal): List<Metadata> {
        check(localSourceItem(grantId, documentId, signal).directory)
        val grant = grants.get(grantId, ""); val result = mutableListOf<Metadata>()
        resolver.query(Documents.buildChildDocumentsUriUsingTree(grant.tree, documentId), projection, null, null, null, signal)?.use { cursor ->
            while (cursor.moveToNext()) { signal.throwIfCanceled(); check(result.size < PhoneTransferManifests.MAX_ITEMS); result.add(decode(cursor)) }
        } ?: throw IOException()
        return result
    }
    internal fun sourceRange(peer: String, grantId: String, documentId: String, version: String,
        request: FileRequest, signal: CancellationSignal): FileResponse {
        val id = StorageRules.token(); nodes[id] = Node(grantId, documentId, null, emptyList())
        try {
            val result = FileResponse.newBuilder().setRequestId(request.requestId)
            read(peer, request.toBuilder().setNodeId(id).setExpectedVersion(version).build(), result, signal, StorageRules.MAX_TRANSFER_SIZE)
            // Internal references must not escape or be usable as session browse nodes.
            result.clearEntries(); return result.build()
        } finally { nodes.remove(id) }
    }
    internal fun hashSource(peer: String, grantId: String, id: String, version: String, size: Long,
        signal: CancellationSignal, progress: (Long) -> Unit): ByteArray {
        sourceSnapshot(peer, grantId, id, version, signal)
        val grant = grants.get(grantId, peer)
        val hash = java.security.MessageDigest.getInstance("SHA-256")
        resolver.openFileDescriptor(document(grant, id), "r", signal)?.use { descriptor ->
            FileInputStream(descriptor.fileDescriptor).use { input ->
                val buffer = ByteArray(StorageRules.RANGE); var offset = 0L
                while (offset < size) {
                    signal.throwIfCanceled()
                    StorageRules.requirePermission(grants.get(grantId, peer).permissions, StorageRules.READ)
                    if (Thread.currentThread().isInterrupted) throw java.io.InterruptedIOException()
                    val count = input.read(buffer, 0, minOf(buffer.size.toLong(), size - offset).toInt())
                    if (count <= 0) throw IOException()
                    hash.update(buffer, 0, count); offset += count; progress(offset)
                }
                if (input.read() != -1) throw StorageFailure(FileStatus.CONFLICT, "The source size changed.")
            }
        } ?: throw FileNotFoundException()
        sourceSnapshot(peer, grantId, id, version, signal)
        return hash.digest()
    }
    private fun roots(peer: String, response: FileResponse.Builder, signal: CancellationSignal) {
        grants.forPeer(peer).forEach { grant ->
            if (grant.permissions and StorageRules.LIST != 0) try {
                val root = metadata(grant, rootId(grant), signal)
                if (!root.directory || !grants.visible(grant, root.name)) return@forEach
                response.addEntries(issue(Node(grant.id, root.id, null, emptyList()), grant, root))
            } catch (_: SecurityException) { /* Revoked/missing providers expose no root. */ }
            catch (_: FileNotFoundException) { }
        }
    }
    private fun list(peer: String, request: FileRequest, response: FileResponse.Builder, signal: CancellationSignal) {
        val (node, grant) = resolve(peer, request.nodeId, StorageRules.LIST, signal)
        if (!metadata(grant, node.document, signal).directory) throw StorageFailure(FileStatus.INVALID_REQUEST, "Choose a folder.")
        if (request.continuation.isEmpty()) {
            clearPage()
            val cursor = resolver.query(Documents.buildChildDocumentsUriUsingTree(grant.tree, node.document), projection,
                null, null, null, signal) ?: throw IOException()
            page = Page(StorageRules.token(), request.nodeId, cursor, System.currentTimeMillis(), peer)
        }
        val current = page ?: throw StorageFailure(FileStatus.STALE_REFERENCE, "Refresh the folder to obtain a new page.")
        if (current.peer != peer || current.node != request.nodeId || (request.continuation.isNotEmpty() && request.continuation != current.token) ||
            System.currentTimeMillis() - current.created > 120_000) {
            clearPage(); throw StorageFailure(FileStatus.STALE_REFERENCE, "The directory page expired. Refresh the folder.")
        }
        try {
            var scanned = 0
            while (response.entriesCount < StorageRules.PAGE && scanned++ < 512) {
                signal.throwIfCanceled()
                if (Thread.currentThread().isInterrupted) throw IOException()
                if (!current.cursor.moveToNext()) { clearPage(); return }
                val child = decode(current.cursor)
                if (!grants.visible(grant, child.name) || child.id == node.document ||
                    child.flags and Documents.Document.FLAG_VIRTUAL_DOCUMENT != 0) continue
                // A provider must prove membership; unsupported ancestry fails closed.
                checkChild(grant, child.id)
                response.addEntries(issue(Node(grant.id, child.id, request.nodeId, node.names + child.name), grant, child))
            }
            response.continuation = current.token
        } catch (e: Exception) { clearPage(); throw e }
    }
    private fun read(peer: String, request: FileRequest, response: FileResponse.Builder, signal: CancellationSignal,
        maximumSize: Long = Long.MAX_VALUE) {
        val selected = nodes[request.nodeId] ?: throw StorageFailure(FileStatus.STALE_REFERENCE, "Refresh this folder to obtain a current document reference.")
        val selectedGrant = grants.get(selected.grant, peer)
        StorageRules.requirePermission(selectedGrant.permissions, StorageRules.READ)
        signal.throwIfCanceled()
        // A connection belongs to this synchronous read only. Every range rechecks policy
        // and inspects ancestry/metadata before and after reading; no provider data is cached.
        val client = resolver.acquireUnstableContentProviderClient(selectedGrant.tree) ?: throw FileNotFoundException()
        client.use {
            val provider = ContentResolver.wrap(it)
            val (node, grant, before) = resolve(peer, request.nodeId, StorageRules.READ, signal, provider)
            requireVersion(request.expectedVersion, before.version)
            if (before.directory || !before.knownSize || before.size > maximumSize || before.flags and Documents.Document.FLAG_VIRTUAL_DOCUMENT != 0)
                throw StorageFailure(FileStatus.UNSUPPORTED, "This provider cannot supply a normal file with a known size.")
            if (request.offset < 0 || request.length !in 1..StorageRules.RANGE || request.offset > before.size)
                throw StorageFailure(FileStatus.INVALID_REQUEST, "Request a bounded file range.")
            val data = ByteArray(minOf(request.length.toLong(), before.size - request.offset).toInt())
            val descriptor = provider.openFileDescriptor(document(grant, node.document), "r", signal) ?: throw IOException()
            descriptor.use {
                FileInputStream(it.fileDescriptor).use { input ->
                    // Range reads require a seekable descriptor. Never skip unbounded remote content.
                    try { input.channel.position(request.offset) }
                    catch (_: IOException) { throw StorageFailure(FileStatus.UNSUPPORTED, "The provider does not support seekable range reads.") }
                    var offset = 0
                    while (offset < data.size) {
                        signal.throwIfCanceled()
                        val count = input.read(data, offset, data.size - offset)
                        if (count < 0) throw StorageFailure(FileStatus.CONFLICT, "The source changed while it was being read.")
                        offset += count
                    }
                }
            }
            checkChild(grant, node.document, provider)
            val after = inspectAncestry(grant, node.document, signal, provider).leaf
            requireVersion(before.version, after.version)
            response.content = ByteString.copyFrom(data)
            response.endOfFile = request.offset + data.size >= after.size
            response.addEntries(describe(request.nodeId, node, grant, after))
        }
    }
    private fun create(peer: String, request: FileRequest, response: FileResponse.Builder, signal: CancellationSignal) {
        if (request.mutationId.isEmpty()) { resolve(peer, request.destinationId, StorageRules.CREATE, signal); throw StorageFailure(FileStatus.INVALID_REQUEST, "Update the client to supply a durable operation identifier.") }
        var selected: Pair<Node, AndroidStorageGrant>? = null
        var creationName=""
        val previous = mutableSetOf<String>()
        val receipt = mutationHost().execute(peer, request, {
        val (parent, grant) = resolve(peer, request.destinationId, StorageRules.CREATE, signal)
        selected = parent to grant
        StorageRules.name(request.name)
        if (!StorageRules.visible(request.name, grant.exclusions)) throw StorageFailure(FileStatus.ACCESS_DENIED, "This name is excluded from sharing.")
        val info = metadata(grant, parent.document, signal)
        if (!info.directory || info.flags and Documents.Document.FLAG_DIR_SUPPORTS_CREATE == 0)
            throw StorageFailure(FileStatus.UNSUPPORTED, "The provider does not allow creating items in this folder.")
        creationName=if(grant.permissions and StorageRules.LIST==0) incomingFolderName(request.name) else request.name
        if(!grants.visible(grant,creationName)) throw StorageFailure(FileStatus.ACCESS_DENIED,"This name is excluded from sharing.")
        var bytes = 0
        resolver.query(Documents.buildChildDocumentsUriUsingTree(grant.tree, parent.document), projection, null, null, null, signal)?.use { cursor ->
            while (cursor.moveToNext()) {
                signal.throwIfCanceled(); val child = decode(cursor); bytes += child.id.length + child.name.length
                if (previous.size >= 4096 || bytes > 1024 * 1024) throw StorageFailure(FileStatus.UNSUPPORTED, "Choose a smaller folder for a safe creation check.")
                if (!previous.add(child.id)) throw StorageFailure(FileStatus.CONFLICT, "The provider returned ambiguous folder contents.")
                if (child.name.equals(creationName, true)) throw StorageFailure(FileStatus.CONFLICT, "An existing destination was kept. Choose another name.")
            }
        } ?: throw IOException()
        val ancestors=validateAncestry(grant,parent.document,signal)
        if(ancestors.size>=64 || ancestors.sumOf {it.length}>64*1024 || ancestors.any {it.length !in 1..4096 || it.any(Character::isISOControl)})
            throw StorageFailure(FileStatus.UNSUPPORTED,"The provider's folder identities exceed safe receipt limits. No folder was created.")
        AndroidMutation(request.mutationId, peer, MutationRules.fingerprint(request), grant.id, grant.trustEpoch, grant.tree.toString(), parent.document, parent.names, creationName,
            ancestors=ancestors,requested=request.name)
        }, {
        val (parent, grant) = requireNotNull(selected)
        resolve(peer, request.destinationId, StorageRules.CREATE, signal)
        val uri = Documents.createDocument(resolver, document(grant, parent.document), Documents.Document.MIME_TYPE_DIR, creationName)
            ?: throw IOException()
        val id = returnedId(grant, uri)
        if (id in previous) throw StorageFailure(FileStatus.CONFLICT, "The provider returned an existing folder. Review its contents; it was not adopted as a new folder.")
        val created = metadata(grant, id, signal)
        if (!created.directory || validateAncestry(grant, id, signal).dropLast(1).lastOrNull() != parent.document)
            throw StorageFailure(FileStatus.CONFLICT, "The provider returned a different item or destination. Review the folder before another change.")
        if (!StorageRules.visible(created.name, grant.exclusions)) throw StorageFailure(FileStatus.ACCESS_DENIED, "The provider returned an excluded name.")
        resolver.query(Documents.buildChildDocumentsUriUsingTree(grant.tree,id),arrayOf(Documents.Document.COLUMN_DOCUMENT_ID),null,null,null,signal)?.use {
            if(it.moveToFirst()) throw StorageFailure(FileStatus.CONFLICT,"The provider returned a folder with existing content. It was kept for review.")
        }?:throw IOException()
        clearPage()
        AndroidMutationResult(created.name,id)
        })
        response.mutation = receipt; response.status = receipt.status; response.message = receipt.message
        if(receipt.phase==MutationPhase.CHANGE_SUCCEEDED) response.addEntries(resolveCreatedDirectory(peer,request,response,signal))
    }
    private fun resolveCreatedDirectory(peer:String,request:FileRequest,response:FileResponse.Builder,signal:CancellationSignal):FileEntry {
        val row=mutationHost().createdDirectory(peer,request); val grant=grants.get(row.grant,peer)
        val path=validateAncestry(grant,row.parent,signal)
        if(path!=row.ancestors) throw StorageFailure(FileStatus.STALE_REFERENCE,"The created folder's recorded ancestors changed.")
        val names=path.drop(1).map {metadata(grant,it,signal).name}
        if(names!=row.names) throw StorageFailure(FileStatus.STALE_REFERENCE,"A recorded parent folder was renamed.")
        val created=metadata(grant,row.created,signal)
        if(!created.directory || created.name!=row.result || validateAncestry(grant,row.created,signal)!=path+row.created)
            throw StorageFailure(FileStatus.STALE_REFERENCE,"The created folder moved, was renamed or was replaced. The current item was kept.")
        var parent=issue(Node(grant.id,path.first(),null,emptyList()),grant,metadata(grant,path.first(),signal))
        path.drop(1).forEachIndexed {index,id->parent=issue(Node(grant.id,id,parent.id,names.take(index+1)),grant,metadata(grant,id,signal))}
        response.mutation=mutationHost().info(row); response.mutationEpoch=request.mutationEpoch
        return issue(Node(grant.id,row.created,parent.id,names+row.result),grant,created)
    }
    private fun incomingFolderName(name:String):String {
        val suffix=" ("+StorageRules.token().take(16)+")"; var stem=name.take(255-suffix.length).trimEnd(' ','.')
        if(stem.lastOrNull()?.isHighSurrogate()==true) stem=stem.dropLast(1)
        return (stem+suffix).also(StorageRules::name)
    }
    private fun rename(peer: String, request: FileRequest, response: FileResponse.Builder, signal: CancellationSignal) {
        val (node, grant) = resolve(peer, request.nodeId, StorageRules.RENAME, signal)
        if (node.parent == null) throw StorageFailure(FileStatus.ACCESS_DENIED, "Approved roots cannot be renamed.")
        StorageRules.name(request.name)
        if (!StorageRules.visible(request.name, grant.exclusions)) throw StorageFailure(FileStatus.ACCESS_DENIED, "This name is excluded from sharing.")
        val info = metadata(grant, node.document, signal)
        requireVersion(request.expectedVersion, info.version)
        if (info.flags and Documents.Document.FLAG_SUPPORTS_RENAME == 0)
            throw StorageFailure(FileStatus.UNSUPPORTED, "The provider does not support rename.")
        // SAF has no portable no-replace rename primitive. A provider may merge or
        // replace names under concurrency; require the conservative provider opt-in.
        throw StorageFailure(FileStatus.UNSUPPORTED, "Safe rename requires a provider with no-replace guarantees.")
    }
    private fun resolve(peer: String, id: String, permission: Int, signal: CancellationSignal,
        provider: ContentResolver = resolver): Resolved {
        val node = nodes[id] ?: throw StorageFailure(FileStatus.STALE_REFERENCE, "Refresh this folder to obtain a current document reference.")
        val grant = grants.get(node.grant, peer)
        StorageRules.requirePermission(grant.permissions, permission)
        if (node.names.any { !grants.visible(grant, it) }) throw StorageFailure(FileStatus.ACCESS_DENIED, "This item is excluded from sharing.")
        checkChild(grant, node.document, provider)
        val info = inspectAncestry(grant, node.document, signal, provider).leaf
        return Resolved(node, grant, info)
    }
    private fun checkChild(grant: AndroidStorageGrant, id: String, provider: ContentResolver = resolver) {
        if (id != rootId(grant) && !Documents.isChildDocument(provider, document(grant, rootId(grant)), document(grant, id)))
            throw StorageFailure(FileStatus.ACCESS_DENIED, "The document is outside the approved folder.")
    }
    private fun returnedId(grant: AndroidStorageGrant, uri: Uri): String {
        if (uri.scheme != "content" || uri.authority != grant.tree.authority)
            throw StorageFailure(FileStatus.ACCESS_DENIED, "The provider returned an invalid document.")
        val id = Documents.getDocumentId(uri)
        if(id.length !in 1..4096 || id.any(Character::isISOControl))
            throw StorageFailure(FileStatus.CONFLICT,"The provider returned an invalid folder identity. Review the destination before another change.")
        checkChild(grant, id)
        return id
    }
    private fun validateAncestry(grant: AndroidStorageGrant, id: String, signal: CancellationSignal): List<String> =
        inspectAncestry(grant, id, signal).path
    private fun inspectAncestry(grant: AndroidStorageGrant, id: String, signal: CancellationSignal,
        provider: ContentResolver = resolver): Ancestry {
        val root = rootId(grant)
        val path = if (id == root) listOf(id) else Documents.findDocumentPath(provider, document(grant, id))?.path
            ?: throw StorageFailure(FileStatus.UNSUPPORTED, "The provider cannot verify this document's folder ancestry.")
        if (path.size !in (if (id == root) 1..1 else 2..64) || path.first() != root || path.last() != id || path.distinct().size != path.size)
            throw StorageFailure(FileStatus.ACCESS_DENIED, "The provider returned an invalid document ancestry.")
        val inspected = path.map { ancestor ->
            signal.throwIfCanceled()
            val info = metadata(grant, ancestor, signal, provider)
            if (!grants.visible(grant, info.name))
                throw StorageFailure(FileStatus.ACCESS_DENIED, "An ancestor folder is excluded from sharing.")
            info
        }
        return Ancestry(path, inspected.last())
    }
    private fun metadata(grant: AndroidStorageGrant, id: String, signal: CancellationSignal,
        provider: ContentResolver = resolver): Metadata =
        provider.query(document(grant, id), projection, null, null, null, signal)?.use {
            if (!it.moveToFirst()) throw FileNotFoundException()
            decode(it).also { data -> if (data.id != id) throw IOException() }
        } ?: throw FileNotFoundException()
    private fun decode(cursor: Cursor): Metadata {
        fun text(column: String) = cursor.getString(cursor.getColumnIndexOrThrow(column)) ?: ""
        fun number(column: String): Long = cursor.getColumnIndex(column).let { if (it < 0 || cursor.isNull(it)) 0 else cursor.getLong(it) }
        val sizeIndex = cursor.getColumnIndex(Documents.Document.COLUMN_SIZE)
        val knownSize = sizeIndex >= 0 && !cursor.isNull(sizeIndex)
        val result = Metadata(text(Documents.Document.COLUMN_DOCUMENT_ID), text(Documents.Document.COLUMN_DISPLAY_NAME),
            text(Documents.Document.COLUMN_MIME_TYPE) == Documents.Document.MIME_TYPE_DIR,
            number(Documents.Document.COLUMN_SIZE), number(Documents.Document.COLUMN_LAST_MODIFIED),
            number(Documents.Document.COLUMN_FLAGS).toInt(), knownSize)
        if (result.id.length !in 1..4096 || result.name.length !in 1..255 || result.size < 0 ||
            result.modified !in StorageRules.MIN_TIMESTAMP..StorageRules.MAX_TIMESTAMP) throw IOException()
        return result
    }
    private fun issue(node: Node, grant: AndroidStorageGrant, info: Metadata): FileEntry {
        val key = node.grant to node.document
        val existing = ids[key]
        if (existing != null) { nodes[existing] = node; return describe(existing, node, grant, info) }
        if (nodes.size >= 16_384) {
            val protected = mutableSetOf<String>()
            var parent = node.parent
            while (parent != null && protected.add(parent)) parent = nodes[parent]?.parent
            val evicted = nodes.entries.firstOrNull { it.key !in protected && it.value.parent != null && page?.node != it.key }
                ?: throw StorageFailure(FileStatus.RESOURCE_EXHAUSTED, "Too many active document references.")
            nodes.remove(evicted.key); ids.remove(evicted.value.grant to evicted.value.document)
        }
        val id = StorageRules.token()
        nodes[id] = node; ids[key] = id
        return describe(id, node, grant, info)
    }
    private fun describe(id: String, node: Node, grant: AndroidStorageGrant, info: Metadata): FileEntry {
        var allowed = grant.permissions and StorageRules.READ_ONLY
        if (info.directory && info.flags and Documents.Document.FLAG_DIR_SUPPORTS_CREATE != 0) allowed = allowed or (grant.permissions and (StorageRules.CREATE or StorageRules.WRITE))
        // Rename is deliberately not advertised until provider collision guarantees exist.
        if (info.flags and Documents.Document.FLAG_VIRTUAL_DOCUMENT != 0 || !info.knownSize && !info.directory) allowed = allowed and StorageRules.READ.inv()
        return FileEntry.newBuilder().setId(id).setGrantId(grant.id).setParentId(node.parent ?: "")
            .setName(if (node.parent == null) grant.label else info.name).setIsDirectory(info.directory).setIsRoot(node.parent == null)
            .setSize(if (info.directory) 0 else info.size).setModifiedAt(if(info.directory && grant.permissions and StorageRules.LIST==0) 0 else info.modified).setPermissions(allowed)
            .setVersion(if(info.directory && grant.permissions and StorageRules.LIST==0) StorageRules.version("incoming:${grant.id}:${info.id}",0,0) else info.version).build()
    }
    private fun requireVersion(expected: String, actual: String) {
        if (expected.length != 64 || expected != actual) throw StorageFailure(FileStatus.CONFLICT, "This file changed. Refresh it before continuing.")
    }
    private fun rootId(grant: AndroidStorageGrant) = Documents.getTreeDocumentId(grant.tree)
    private fun document(grant: AndroidStorageGrant, id: String) = Documents.buildDocumentUriUsingTree(grant.tree, id)
    private fun error(request: FileRequest, status: FileStatus, message: String) = FileResponse.newBuilder()
        .setRequestId(request.requestId).setStatus(status).setMessage(message).build()
    private fun clearPage() { page?.cursor?.close(); page = null }
    private fun clear() { batchPlanHost?.endScope(downloadScope); clearPage(); nodes.clear(); ids.clear() }
    override fun close() {
        if (!closed.compareAndSet(false, true)) return
        downloads?.cancelScope(downloadScope)
        incoming?.cancelScope(downloadScope)
        batchPlanHost?.cancelFileScopeNow(downloadScope)
        activeSignal?.cancel()
        // Do not block the UI behind a slow provider call; the worker owns final cleanup.
        if (Thread.holdsLock(lock)) clear()
        else CoroutineScope(Dispatchers.IO).launch { synchronized(lock) { clear() } }
    }
    internal fun interruptPublishedBatchCheck(peer:String,request:FileRequest) {
        if(!closed.get() && Rules.tokenPattern.matches(request.requestId)) batchPlanHost?.cancelPublishedCheckNow(peer,downloadScope,request)
    }
    companion object {
        private val quietOperations=setOf(FileOperation.READ_RANGE,FileOperation.DOWNLOAD_READ,FileOperation.DOWNLOAD_STATUS,FileOperation.UPLOAD_WRITE,FileOperation.WRITE_BATCH_FILE,FileOperation.UPLOAD_STATUS,FileOperation.UPLOAD_BATCH_ITEM_STATUS,FileOperation.VERIFY_PUBLISHED_UPLOAD,FileOperation.VERIFY_PUBLISHED_BATCH,FileOperation.CANCEL_PUBLISHED_BATCH_CHECK)
        private val projection = arrayOf(Documents.Document.COLUMN_DOCUMENT_ID, Documents.Document.COLUMN_DISPLAY_NAME,
            Documents.Document.COLUMN_MIME_TYPE, Documents.Document.COLUMN_SIZE, Documents.Document.COLUMN_LAST_MODIFIED, Documents.Document.COLUMN_FLAGS)
    }
}
