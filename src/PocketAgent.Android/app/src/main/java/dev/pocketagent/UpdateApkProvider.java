package dev.pocketagent;

import android.content.ContentProvider;
import android.content.ContentValues;
import android.content.Context;
import android.database.Cursor;
import android.database.MatrixCursor;
import android.net.Uri;
import android.os.ParcelFileDescriptor;
import android.provider.OpenableColumns;
import java.io.File;
import java.io.FileNotFoundException;

/** Grants the system installer read access to one cached APK. */
public final class UpdateApkProvider extends ContentProvider {
  static File directory(Context context) {
    return new File(context.getCacheDir(), "updates");
  }

  @Override public boolean onCreate() {
    File[] stale = directory(getContext()).listFiles();
    if (stale != null) {
      long cutoff = System.currentTimeMillis() - 24 * 60 * 60 * 1000L;
      for (File file : stale) {
        if (file.lastModified() < cutoff) file.delete();
      }
    }
    return true;
  }

  private File apk(Uri uri) {
    String name = uri.getLastPathSegment();
    if (uri.getPathSegments().size() != 1 || name == null
        || !name.matches("[0-9a-f-]{36}\\.apk")) {
      throw new IllegalArgumentException("Invalid update URI");
    }
    return new File(directory(getContext()), name);
  }

  @Override public ParcelFileDescriptor openFile(Uri uri, String mode) throws FileNotFoundException {
    if (!"r".equals(mode)) throw new FileNotFoundException("Read only");
    return ParcelFileDescriptor.open(apk(uri), ParcelFileDescriptor.MODE_READ_ONLY);
  }

  @Override public String getType(Uri uri) {
    return "application/vnd.android.package-archive";
  }

  @Override public Cursor query(Uri uri, String[] projection, String selection, String[] arguments, String order) {
    File file = apk(uri);
    String[] columns = projection == null
        ? new String[] {OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE} : projection;
    MatrixCursor cursor = new MatrixCursor(columns, 1);
    MatrixCursor.RowBuilder row = cursor.newRow();
    for (String column : columns) {
      if (OpenableColumns.DISPLAY_NAME.equals(column)) row.add("PocketAgent.apk");
      else if (OpenableColumns.SIZE.equals(column)) row.add(file.length());
      else row.add(null);
    }
    return cursor;
  }

  @Override public Uri insert(Uri uri, ContentValues values) { throw new UnsupportedOperationException(); }
  @Override public int update(Uri uri, ContentValues values, String selection, String[] args) { throw new UnsupportedOperationException(); }
  @Override public int delete(Uri uri, String selection, String[] args) { throw new UnsupportedOperationException(); }
}
