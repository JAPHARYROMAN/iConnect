package com.iconnect.proof;

import android.content.Intent;
import android.database.Cursor;
import android.database.MatrixCursor;
import android.net.Uri;
import android.os.Binder;
import android.os.Bundle;
import android.os.CancellationSignal;
import android.os.ParcelFileDescriptor;
import android.provider.DocumentsContract;
import android.provider.DocumentsContract.Document;
import android.provider.DocumentsProvider;
import java.io.File;
import java.io.FileNotFoundException;
import java.io.IOException;
import java.io.FileOutputStream;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

/** Framework-only Java so the separate test-provider process needs no app runtime. */
public final class TestDocumentsProvider extends DocumentsProvider {
    public static final String AUTHORITY = "com.iconnect.proof.test.documents";
    public static final Uri URI = Uri.parse("content://" + AUTHORITY);
    public static final Uri CONTROL_URI = Uri.parse("content://com.iconnect.proof.test.fixtures");
    private static final String[] COLUMNS = { Document.COLUMN_DOCUMENT_ID, Document.COLUMN_DISPLAY_NAME,
        Document.COLUMN_MIME_TYPE, Document.COLUMN_SIZE, Document.COLUMN_LAST_MODIFIED, Document.COLUMN_FLAGS };
    private static final class Item {
        String id, parent, name;
        boolean directory;
        File file;
        int flags;
        long modified = 1;
        Item(String id, String parent, String name, File file, int flags) {
            this.id = id; this.parent = parent; this.name = name; this.file = file; this.flags = flags; directory = file == null;
        }
    }
    private final Map<String, Item> items = new LinkedHashMap<>();
    private final Map<String, String> creationModes = new LinkedHashMap<>();
    private final java.util.Set<String> pickerRoots = new java.util.HashSet<>();
    private static final Map<String, CountDownLatch> queries = new ConcurrentHashMap<>();
    private static final Map<String, CountDownLatch> blockedOpens = new ConcurrentHashMap<>();
    public static Bundle openProbe(String id, boolean release) {
        CountDownLatch latch = blockedOpens.get(id);
        Bundle result = new Bundle(); result.putBoolean("entered", latch != null);
        if (release && latch != null) latch.countDown();
        return result;
    }
    public static Bundle queryProbe(String id, boolean release) {
        CountDownLatch latch = queries.get(id);
        Bundle result = new Bundle(); result.putBoolean("entered", latch != null);
        if (release && latch != null) latch.countDown();
        return result;
    }
    @Override public boolean onCreate() { return true; }
    @Override public synchronized Bundle call(String method, String arg, Bundle extras) {
        if (method.startsWith("android:")) return super.call(method, arg, extras);
        if (!("com.iconnect.proof".equals(getCallingPackage()) || "com.iconnect.proof.test".equals(getCallingPackage())))
            throw new SecurityException();
        long token = Binder.clearCallingIdentity();
        try {
            switch (method) {
                case "fixture": {
                    String root = "root-" + UUID.randomUUID();
                    File directory = new File(getContext().getFilesDir(), root);
                    if (!directory.mkdirs()) throw new IllegalStateException();
                    items.put(root, new Item(root, null, "Phone fixture", null, Document.FLAG_DIR_SUPPORTS_CREATE));
                    String file = child(root, directory, "simu.txt", "iConnect approved phone document.\nSalama!\n");
                    String folder = child(root, directory, "Reports", null);
                    String nested = child(folder, directory, "notes.txt", "nested fixture");
                    if (extras != null && extras.getBoolean("media")) {
                        String image = child(root, directory, "fixture.png", "");
                        android.graphics.Bitmap bitmap = android.graphics.Bitmap.createBitmap(32,16,android.graphics.Bitmap.Config.ARGB_8888);
                        bitmap.eraseColor(android.graphics.Color.RED);
                        try (FileOutputStream output = new FileOutputStream(items.get(image).file)) { bitmap.compress(android.graphics.Bitmap.CompressFormat.PNG,100,output); }
                        catch (IOException ex) { throw new IllegalStateException(ex); } finally { bitmap.recycle(); }
                        String document = child(root,directory,"fixture.pdf","");
                        android.graphics.pdf.PdfDocument pdf = new android.graphics.pdf.PdfDocument();
                        try {
                            for(int index=0;index<2;index++) { android.graphics.pdf.PdfDocument.Page page=pdf.startPage(new android.graphics.pdf.PdfDocument.PageInfo.Builder(200,300,index).create());
                                page.getCanvas().drawColor(index==0?android.graphics.Color.GREEN:android.graphics.Color.BLUE); pdf.finishPage(page); }
                            try (FileOutputStream output = new FileOutputStream(items.get(document).file)) { pdf.writeTo(output); }
                        } catch (IOException ex) { throw new IllegalStateException(ex); } finally { pdf.close(); }
                    }
                    if (extras != null && extras.getBoolean("emptyFolder")) child(root, directory, "Empty", null);
                    child(root, directory, ".ssh", null);
                    if (extras != null && extras.getBoolean("largeFile")) {
                        String binary = child(root, directory, "kutuma.bin", "");
                        byte[] bytes = new byte[2 * 1024 * 1024 + 17];
                        for (int i = 0; i < bytes.length; i++) bytes[i] = (byte)(i * 31 % 251);
                        try (FileOutputStream output = new FileOutputStream(items.get(binary).file)) { output.write(bytes); }
                        catch (IOException ex) { throw new IllegalStateException(ex); }
                    }
                    for (int i = 0; i < (extras == null ? 0 : extras.getInt("extraFiles")); i++) child(root, directory, "item-" + i + ".txt", Integer.toString(i));
                    Uri tree = DocumentsContract.buildTreeDocumentUri(AUTHORITY, root);
                    if (extras != null && extras.getBoolean("pickerRoot")) {
                        pickerRoots.add(root);
                        getContext().getContentResolver().notifyChange(DocumentsContract.buildRootsUri(AUTHORITY), null);
                    }
                    int flags = Intent.FLAG_GRANT_READ_URI_PERMISSION | Intent.FLAG_GRANT_WRITE_URI_PERMISSION |
                        Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION | Intent.FLAG_GRANT_PREFIX_URI_PERMISSION;
                    getContext().grantUriPermission("com.iconnect.proof", tree, flags);
                    Bundle result = new Bundle();
                    result.putString("tree", tree.toString()); result.putInt("flags", flags); result.putString("root", root);
                    result.putString("file", file); result.putString("folder", folder); result.putString("nested", nested);
                    return result;
                }
                case "change": {
                    Item item = items.get(arg);
                    if (item == null) throw new IllegalArgumentException();
                    if (extras.containsKey("name")) item.name = extras.getString("name");
                    if (extras.containsKey("parent")) item.parent = extras.getString("parent");
                    if (extras.containsKey("content")) { write(item.file, extras.getString("content")); item.modified++; }
                    return Bundle.EMPTY;
                }
                case "creationMode": { creationModes.put(arg, extras.getString("mode")); return Bundle.EMPTY; }
                case "dispose": {
                    creationModes.remove(arg);
                    if (pickerRoots.remove(arg)) getContext().getContentResolver().notifyChange(DocumentsContract.buildRootsUri(AUTHORITY), null);
                    List<String> remove = new ArrayList<>();
                    for (String id : items.keySet()) if (id.equals(arg) || isChildDocument(arg, id) ||
                        (items.get(id).file != null && items.get(id).file.getParentFile().getName().equals(arg))) remove.add(id);
                    for (String id : remove) { Item item = items.remove(id); if (item.file != null) item.file.delete(); }
                    new File(getContext().getFilesDir(), arg).delete();
                    return Bundle.EMPTY;
                }
                case "revoke": {
                    getContext().revokeUriPermission(Uri.parse(arg), Intent.FLAG_GRANT_READ_URI_PERMISSION | Intent.FLAG_GRANT_WRITE_URI_PERMISSION);
                    return Bundle.EMPTY;
                }
                default: return super.call(method, arg, extras);
            }
        } finally { Binder.restoreCallingIdentity(token); }
    }
    private String child(String parent, File directory, String name, String content) {
        String id = UUID.randomUUID().toString();
        File file = content == null ? null : new File(directory, id);
        if (file != null) write(file, content);
        items.put(id, new Item(id, parent, name, file, file == null ? Document.FLAG_DIR_SUPPORTS_CREATE : Document.FLAG_SUPPORTS_RENAME));
        return id;
    }
    private static void write(File file, String content) {
        try (FileOutputStream output = new FileOutputStream(file)) { output.write(content.getBytes(StandardCharsets.UTF_8)); }
        catch (IOException ex) { throw new IllegalStateException(ex); }
    }
    @Override public synchronized Cursor queryRoots(String[] projection) {
        MatrixCursor result = new MatrixCursor(projection == null ? new String[] { DocumentsContract.Root.COLUMN_ROOT_ID,
            DocumentsContract.Root.COLUMN_DOCUMENT_ID, DocumentsContract.Root.COLUMN_TITLE, DocumentsContract.Root.COLUMN_FLAGS } : projection);
        for (String id : pickerRoots) {
            MatrixCursor.RowBuilder row = result.newRow();
            for (String column : result.getColumnNames()) {
                Object value = null;
                if (DocumentsContract.Root.COLUMN_ROOT_ID.equals(column) || DocumentsContract.Root.COLUMN_DOCUMENT_ID.equals(column)) value = id;
                else if (DocumentsContract.Root.COLUMN_TITLE.equals(column)) value = "iConnect test copies";
                else if (DocumentsContract.Root.COLUMN_FLAGS.equals(column)) value = DocumentsContract.Root.FLAG_SUPPORTS_CREATE | DocumentsContract.Root.FLAG_SUPPORTS_IS_CHILD | DocumentsContract.Root.FLAG_LOCAL_ONLY;
                else if (DocumentsContract.Root.COLUMN_MIME_TYPES.equals(column)) value = "*/*";
                else if (DocumentsContract.Root.COLUMN_ICON.equals(column)) value = android.R.drawable.ic_menu_save;
                else if (DocumentsContract.Root.COLUMN_AVAILABLE_BYTES.equals(column)) value = getContext().getFilesDir().getUsableSpace();
                row.add(value);
            }
        }
        return result;
    }
    @Override public synchronized Cursor queryDocument(String id, String[] projection) throws FileNotFoundException {
        Item item = items.get(id);
        if (item == null) throw new FileNotFoundException();
        if ("delayQuery".equals(creationModes.get(id))) {
            CountDownLatch latch = new CountDownLatch(1); queries.put(id, latch);
            try { if (!latch.await(10, TimeUnit.SECONDS)) throw new FileNotFoundException("Fixture query timed out"); }
            catch (InterruptedException ex) { Thread.currentThread().interrupt(); throw new FileNotFoundException(); }
            finally { queries.remove(id); }
        }
        MatrixCursor result = new MatrixCursor(projection == null ? COLUMNS : projection); add(result, item); return result;
    }
    @Override public synchronized Cursor queryChildDocuments(String parent, String[] projection, String sortOrder) {
        MatrixCursor result = new MatrixCursor(projection == null ? COLUMNS : projection);
        for (Item item : items.values()) if (parent.equals(item.parent)) add(result, item);
        return result;
    }
    @Override public synchronized boolean isChildDocument(String parentId, String id) {
        String parent = items.containsKey(id) ? items.get(id).parent : null;
        for (int depth = 0; depth < 64 && parent != null; depth++) {
            if (parent.equals(parentId)) return true;
            parent = items.containsKey(parent) ? items.get(parent).parent : null;
        }
        return false;
    }
    @Override public synchronized DocumentsContract.Path findDocumentPath(String parentId, String id) throws FileNotFoundException {
        List<String> path = new ArrayList<>(); path.add(id);
        String parent = items.containsKey(id) ? items.get(id).parent : null;
        while (parent != null && path.size() <= 64) { path.add(0, parent); parent = items.containsKey(parent) ? items.get(parent).parent : null; }
        int index = parentId == null ? 0 : path.indexOf(parentId);
        if (index < 0) throw new FileNotFoundException();
        return new DocumentsContract.Path(null, path.subList(index, path.size()));
    }
    @Override public synchronized ParcelFileDescriptor openDocument(String id, String mode, CancellationSignal signal) throws FileNotFoundException {
        Item item = items.get(id);
        if (item == null || item.file == null) throw new FileNotFoundException();
        String ancestor = id;
        for (int i = 0; i < 64 && items.containsKey(ancestor) && items.get(ancestor).parent != null; i++) ancestor = items.get(ancestor).parent;
        if ("r".equals(mode) && "slowRead".equals(creationModes.get(ancestor))) {
            android.os.SystemClock.sleep(20);
            if (signal != null) signal.throwIfCanceled();
        }
        if (mode.contains("w") && "blockWrite".equals(creationModes.get(ancestor))) {
            CountDownLatch release = new CountDownLatch(1);
            blockedOpens.put(id, release);
            try {
            long deadline = android.os.SystemClock.elapsedRealtime() + 10000;
            while (android.os.SystemClock.elapsedRealtime() < deadline) {
                if (signal != null) signal.throwIfCanceled();
                try {
                    if (release.await(20, java.util.concurrent.TimeUnit.MILLISECONDS)) {
                        if (signal != null) signal.throwIfCanceled();
                        return ParcelFileDescriptor.open(item.file, ParcelFileDescriptor.parseMode(mode));
                    }
                } catch (InterruptedException ex) { Thread.currentThread().interrupt(); throw new FileNotFoundException(); }
            }
            throw new FileNotFoundException("Simulated provider open timeout");
            } finally { blockedOpens.remove(id); }
        }
        return ParcelFileDescriptor.open(item.file, ParcelFileDescriptor.parseMode(mode));
    }
    @Override public synchronized String createDocument(String parent, String mimeType, String name) throws FileNotFoundException {
        if ("existing".equals(creationModes.get(parent))) {
            for (Item item : items.values()) if (parent.equals(item.parent) && item.file != null) return item.id;
        }
        for (Item item : items.values()) if (parent.equals(item.parent) && name.equals(item.name)) throw new FileNotFoundException();
        String ancestor = parent;
        for (int i = 0; i < 64 && items.containsKey(ancestor) && items.get(ancestor).parent != null; i++) ancestor = items.get(ancestor).parent;
        File directory = new File(getContext().getFilesDir(), ancestor);
        if (!directory.isDirectory()) throw new FileNotFoundException();
        if (Document.MIME_TYPE_DIR.equals(mimeType)) {
            String id = child(parent, directory, name, "fileForFolder".equals(creationModes.get(parent)) ? "" : null);
            if ("nonemptyFolder".equals(creationModes.get(parent))) child(id, directory, "owner-content.txt", "kept owner content");
            if ("misplacedFolder".equals(creationModes.get(parent))) {
                for (Item item : items.values()) if (parent.equals(item.parent) && "Reports".equals(item.name)) { items.get(id).parent = item.id; break; }
            }
            if ("loseFolderReceipt".equals(creationModes.get(parent))) throw new FileNotFoundException("Simulated folder receipt loss");
            if ("blockFolderReceipt".equals(creationModes.get(parent))) {
                CountDownLatch latch = new CountDownLatch(1); queries.put(parent + "/create", latch);
                try { if (!latch.await(15, TimeUnit.SECONDS)) throw new FileNotFoundException("Folder reply timed out"); }
                catch (InterruptedException ex) { throw new FileNotFoundException("Folder reply interrupted"); }
                finally { queries.remove(parent + "/create"); }
            }
            return id;
        }
        String id = child(parent, directory, name, "");
        items.get(id).flags = Document.FLAG_SUPPORTS_WRITE | Document.FLAG_SUPPORTS_DELETE;
        if ("loseReceipt".equals(creationModes.get(parent))) throw new FileNotFoundException("Simulated provider receipt loss");
        return id;
    }
    private static void add(MatrixCursor cursor, Item item) {
        MatrixCursor.RowBuilder row = cursor.newRow();
        for (String column : cursor.getColumnNames()) switch (column) {
            case Document.COLUMN_DOCUMENT_ID: row.add(item.id); break;
            case Document.COLUMN_DISPLAY_NAME: row.add(item.name); break;
            case Document.COLUMN_MIME_TYPE: row.add(item.directory ? Document.MIME_TYPE_DIR : "text/plain"); break;
            case Document.COLUMN_SIZE: row.add(item.file == null ? 0L : item.file.length()); break;
            case Document.COLUMN_LAST_MODIFIED: row.add(item.modified); break;
            case Document.COLUMN_FLAGS: row.add(item.flags); break;
            default: row.add(null); break;
        }
    }
}
