package dev.pocketagent;

import android.content.ContentResolver;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.net.Uri;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;

record ComposerImage(byte[] png, Bitmap preview) {
  private static final int MAX_BYTES = 2 * 1024 * 1024;
  private static final int MAX_SOURCE_BYTES = 24 * 1024 * 1024;
  private static final int MAX_DIMENSION = 2048;

  static ComposerImage read(ContentResolver resolver, Uri uri) throws IOException {
    byte[] bytes;
    try (InputStream input = resolver.openInputStream(uri);
         ByteArrayOutputStream output = new ByteArrayOutputStream()) {
      if (input == null) throw new IOException("Image unavailable");
      byte[] buffer = new byte[8192];
      int count;
      while ((count = input.read(buffer)) != -1) {
        if (output.size() + count > MAX_SOURCE_BYTES) throw new IOException("Image too large");
        output.write(buffer, 0, count);
      }
      bytes = output.toByteArray();
    }
    BitmapFactory.Options options = new BitmapFactory.Options();
    options.inJustDecodeBounds = true;
    BitmapFactory.decodeByteArray(bytes, 0, bytes.length, options);
    int longest = Math.max(options.outWidth, options.outHeight);
    if (longest <= 0) throw new IOException("Unsupported image");
    options.inSampleSize = 1;
    while (longest / options.inSampleSize > MAX_DIMENSION * 2) options.inSampleSize *= 2;
    options.inJustDecodeBounds = false;
    Bitmap bitmap = BitmapFactory.decodeByteArray(bytes, 0, bytes.length, options);
    if (bitmap == null) throw new IOException("Unsupported image");
    longest = Math.max(bitmap.getWidth(), bitmap.getHeight());
    if (longest > MAX_DIMENSION) bitmap = resize(bitmap, (double) MAX_DIMENSION / longest);
    try (ByteArrayOutputStream output = new ByteArrayOutputStream()) {
      while (true) {
        if (!bitmap.compress(Bitmap.CompressFormat.PNG, 100, output)) throw new IOException("Invalid image");
        if (output.size() <= MAX_BYTES) {
          Bitmap preview = resize(bitmap, Math.min(1, 192.0 / Math.max(bitmap.getWidth(), bitmap.getHeight())));
          return new ComposerImage(output.toByteArray(), preview);
        }
        bitmap = resize(bitmap, 0.75);
        output.reset();
      }
    }
  }

  private static Bitmap resize(Bitmap bitmap, double scale) {
    Bitmap resized = Bitmap.createScaledBitmap(bitmap, Math.max(1, (int) (bitmap.getWidth() * scale)),
        Math.max(1, (int) (bitmap.getHeight() * scale)), true);
    if (resized != bitmap) bitmap.recycle();
    return resized;
  }
}
