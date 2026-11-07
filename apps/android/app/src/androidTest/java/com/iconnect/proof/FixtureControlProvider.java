package com.iconnect.proof;

import android.content.ContentProvider;
import android.content.ContentValues;
import android.database.Cursor;
import android.net.Uri;
import android.os.Binder;
import android.os.Bundle;

/** Test-APK owner issues grants for its disposable documents, as a picker would. */
public final class FixtureControlProvider extends ContentProvider {
    @Override public boolean onCreate() { return true; }
    @Override public Bundle call(String method, String arg, Bundle extras) {
        if (!"com.iconnect.proof".equals(getCallingPackage()) ||
            !(method.equals("fixture") || method.equals("change") || method.equals("creationMode") || method.equals("dispose") || method.equals("revoke") || method.equals("queryProbe") || method.equals("releaseQuery") || method.equals("openProbe")))
            throw new SecurityException();
        if (method.equals("queryProbe") || method.equals("releaseQuery"))
            return TestDocumentsProvider.queryProbe(arg, method.equals("releaseQuery"));
        if (method.equals("openProbe")) return TestDocumentsProvider.openProbe(arg, extras != null && extras.getBoolean("release"));
        long token = Binder.clearCallingIdentity();
        try { return getContext().getContentResolver().call(TestDocumentsProvider.URI, method, arg, extras); }
        finally { Binder.restoreCallingIdentity(token); }
    }
    @Override public Cursor query(Uri uri, String[] projection, String selection, String[] args, String order) { return null; }
    @Override public String getType(Uri uri) { return null; }
    @Override public Uri insert(Uri uri, ContentValues values) { return null; }
    @Override public int delete(Uri uri, String selection, String[] args) { return 0; }
    @Override public int update(Uri uri, ContentValues values, String selection, String[] args) { return 0; }
}
