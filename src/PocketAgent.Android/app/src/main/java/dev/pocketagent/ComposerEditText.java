package dev.pocketagent;

import android.content.ClipData;
import android.content.ClipDescription;
import android.content.ClipboardManager;
import android.content.Context;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.view.ContentInfo;
import android.view.inputmethod.EditorInfo;
import android.view.inputmethod.InputConnection;
import android.view.inputmethod.InputConnectionWrapper;
import android.view.inputmethod.InputContentInfo;
import android.widget.EditText;

final class ComposerEditText extends EditText {
  interface ImageReceiver {
    void receive(Uri uri, Runnable release);
  }

  private static final String IMAGE_MIME = "image/*";
  private ImageReceiver receiver;

  ComposerEditText(Context context) {
    super(context);
  }

  void receiveImages(ImageReceiver receiver) {
    this.receiver = receiver;
    if (Build.VERSION.SDK_INT >= 31) {
      setOnReceiveContentListener(new String[] {IMAGE_MIME}, (view, content) -> {
        if (!content.getClip().getDescription().hasMimeType(IMAGE_MIME)) return content;
        ClipData clip = content.getClip();
        ClipData remaining = null;
        Uri image = null;
        for (int i = 0; i < clip.getItemCount(); i++) {
          ClipData.Item item = clip.getItemAt(i);
          if (isContentUri(item.getUri())) {
            if (image == null) image = item.getUri();
          } else if (remaining == null) {
            remaining = new ClipData(clip.getDescription(), item);
          } else {
            remaining.addItem(item);
          }
        }
        if (image == null) return content;
        receiver.receive(image, () -> java.lang.ref.Reference.reachabilityFence(content));
        return remaining == null ? null : new ContentInfo.Builder(content).setClip(remaining).build();
      });
    }
  }

  @Override
  public boolean onTextContextMenuItem(int id) {
    if (Build.VERSION.SDK_INT < 31 && receiver != null && id == android.R.id.paste) {
      ClipboardManager clipboard = getContext().getSystemService(ClipboardManager.class);
      ClipDescription description = clipboard.getPrimaryClipDescription();
      if (description != null && description.hasMimeType(IMAGE_MIME)) {
        ClipData clip = clipboard.getPrimaryClip();
        if (clip != null && clip.getItemCount() > 0 && isContentUri(clip.getItemAt(0).getUri())) {
          receiver.receive(clip.getItemAt(0).getUri(), () -> {});
          return true;
        }
      }
    }
    return super.onTextContextMenuItem(id);
  }

  @Override
  public InputConnection onCreateInputConnection(EditorInfo outAttrs) {
    InputConnection connection = super.onCreateInputConnection(outAttrs);
    if (Build.VERSION.SDK_INT >= 31 || connection == null || receiver == null) return connection;
    outAttrs.contentMimeTypes = new String[] {IMAGE_MIME};
    return new InputConnectionWrapper(connection, false) {
      @Override
      public boolean commitContent(InputContentInfo info, int flags, Bundle opts) {
        Uri uri = info.getContentUri();
        if (!isContentUri(uri) || !info.getDescription().hasMimeType(IMAGE_MIME))
          return super.commitContent(info, flags, opts);
        if ((flags & INPUT_CONTENT_GRANT_READ_URI_PERMISSION) == 0) {
          post(() -> receiver.receive(uri, () -> {}));
          return true;
        }
        try {
          info.requestPermission();
        } catch (SecurityException exception) {
          return false;
        }
        if (!post(() -> receiver.receive(uri, info::releasePermission))) {
          info.releasePermission();
          return false;
        }
        return true;
      }
    };
  }

  private static boolean isContentUri(Uri uri) {
    return uri != null && "content".equals(uri.getScheme());
  }
}
