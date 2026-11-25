package dev.pocketagent;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Intent;
import android.content.pm.PackageInfo;
import android.net.Uri;
import android.provider.Settings;
import android.widget.LinearLayout;
import android.widget.ProgressBar;
import android.widget.TextView;
import android.widget.Toast;
import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.text.NumberFormat;
import java.util.Locale;
import java.util.UUID;
import java.util.concurrent.Executor;
import okhttp3.Call;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.Response;
import okhttp3.ResponseBody;
import org.json.JSONArray;
import org.json.JSONObject;

final class AndroidUpdater {
  private static final int INSTALL_PERMISSION = 42;
  private static final int INSTALL_APK = 43;
  private static final String REPOSITORY = "HoveringGoat/PocketAgent";
  private static final String RELEASES = "https://github.com/" + REPOSITORY + "/releases";
  private static final String CHANNEL = "android-v";
  private static final String APK_MIME = "application/vnd.android.package-archive";

  private final Activity activity;
  private final Executor executor;
  private final OkHttpClient client;
  private Transfer active;
  private AlertDialog dialog;
  private Release pendingPermission;
  private File installing;

  record Release(String version, String notes, String apk, String checksum, String digest) {
    String page() {
      return RELEASES + "/tag/" + CHANNEL + version;
    }
  }

  private static final class Transfer {
    private boolean cancelled;
    private Call call;

    synchronized void start(Call next) throws IOException {
      if (cancelled) throw new IOException("Update cancelled");
      call = next;
    }

    synchronized void cancel() {
      cancelled = true;
      if (call != null) call.cancel();
    }
  }

  AndroidUpdater(Activity activity, Executor executor) {
    this.activity = activity;
    this.executor = executor;
    this.client = new OkHttpClient.Builder().followSslRedirects(false).build();
  }

  void check(boolean manual) {
    if (active != null || dialog != null || pendingPermission != null || installing != null) {
      if (manual) toast("An update check or installation is already in progress.");
      return;
    }
    Transfer check = new Transfer();
    active = check;
    executor.execute(() -> {
      Release release = null;
      boolean failed = false;
      try {
        String endpoint = "https://api.github.com/repos/" + REPOSITORY + "/releases?per_page=30";
        release = selectRelease(new JSONArray(readText(check, endpoint, 1_048_576)), BuildConfig.VERSION_NAME);
      } catch (Exception exception) {
        failed = true;
      }
      Release result = release;
      boolean checkFailed = failed;
      activity.runOnUiThread(() -> {
        if (active != check || !isAvailable()) return;
        active = null;
        if (result != null) offer(result);
        else if (manual) toast(checkFailed ? "Could not check for updates. Try again." : "PocketAgent is up to date.");
      });
    });
  }

  static Release selectRelease(JSONArray releases, String installed) {
    Release selected = null;
    for (int i = 0; i < releases.length(); i++) {
      JSONObject release = releases.optJSONObject(i);
      if (release == null || release.optBoolean("draft") || release.optBoolean("prerelease")) continue;
      String tag = release.optString("tag_name");
      if (!tag.matches("android-v[0-9]{1,9}\\.[0-9]{1,9}\\.[0-9]{1,9}")) continue;
      String version = tag.substring(CHANNEL.length());
      if (compareVersions(version, selected == null ? installed : selected.version()) <= 0) continue;
      JSONArray assets = release.optJSONArray("assets");
      String base = RELEASES + "/download/" + tag + "/PocketAgent-" + version + "-android.apk";
      JSONObject apk = findAsset(assets, base);
      if (apk == null) continue;
      JSONObject checksum = findAsset(assets, base + ".sha256");
      String digest = apk.optString("digest");
      String sha256 = digest.matches("sha256:[0-9a-fA-F]{64}") ? digest.substring(7) : "";
      selected = new Release(version, androidNotes(release.optString("body")), base,
          checksum == null ? "" : base + ".sha256", sha256);
    }
    return selected;
  }

  private static JSONObject findAsset(JSONArray assets, String url) {
    if (assets == null) return null;
    String name = url.substring(url.lastIndexOf('/') + 1);
    for (int i = 0; i < assets.length(); i++) {
      JSONObject asset = assets.optJSONObject(i);
      if (asset != null && name.equals(asset.optString("name"))
          && url.equals(asset.optString("browser_download_url"))) return asset;
    }
    return null;
  }

  private static int compareVersions(String left, String right) {
    String[] leftParts = left.split("\\.");
    String[] rightParts = right.split("\\.");
    for (int i = 0; i < 3; i++) {
      int comparison = Integer.compare(Integer.parseInt(leftParts[i]), Integer.parseInt(rightParts[i]));
      if (comparison != 0) return comparison;
    }
    return 0;
  }

  private static String androidNotes(String notes) {
    String marker = "## Android";
    int start = notes.indexOf(marker);
    if (start >= 0) {
      int end = notes.indexOf("\n## ", start + marker.length());
      notes = notes.substring(start + marker.length(), end < 0 ? notes.length() : end);
    }
    notes = notes.trim();
    return notes.length() <= 1200 ? notes : notes.substring(0, 1200) + "\u2026";
  }

  private void offer(Release release) {
    String message = "PocketAgent " + release.version() + " is available.";
    if (!release.notes().isEmpty()) message += "\n\n" + release.notes();
    show(new AlertDialog.Builder(activity)
        .setTitle("Update available")
        .setMessage(message)
        .setNegativeButton("Later", null)
        .setPositiveButton("Download and install", (ignored, which) -> requestInstall(release)));
  }

  private void requestInstall(Release release) {
    if (activity.getPackageManager().canRequestPackageInstalls()) {
      download(release);
      return;
    }
    show(new AlertDialog.Builder(activity)
        .setTitle("Allow app updates")
        .setMessage("In Android Settings, turn on Allow from this source for PocketAgent, then return to continue.")
        .setNegativeButton("Cancel", null)
        .setPositiveButton("Open Settings", (ignored, which) -> {
          try {
            pendingPermission = release;
            Intent settings = new Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,
                Uri.fromParts("package", activity.getPackageName(), null));
            activity.startActivityForResult(settings, INSTALL_PERMISSION);
          } catch (RuntimeException exception) {
            pendingPermission = null;
            failure(release, "Could not open Android’s install permission settings.");
          }
        }));
  }

  boolean activityResult(int request, int result) {
    if (request == INSTALL_APK) {
      if (installing != null) installing.delete();
      installing = null;
      if (result != Activity.RESULT_OK) toast("Update wasn’t installed. Check for updates to try again.");
      return true;
    }
    if (request != INSTALL_PERMISSION) return false;
    Release release = pendingPermission;
    pendingPermission = null;
    if (release != null) {
      if (activity.getPackageManager().canRequestPackageInstalls()) download(release);
      else failure(release, "Android needs permission for PocketAgent to install the update.");
    }
    return true;
  }

  private void download(Release release) {
    Transfer transfer = new Transfer();
    active = transfer;
    ProgressBar bar = new ProgressBar(activity, null, android.R.attr.progressBarStyleHorizontal);
    bar.setIndeterminate(true);
    bar.setMax(100);
    TextView status = new TextView(activity);
    status.setText(R.string.update_connecting);
    LinearLayout content = new LinearLayout(activity);
    content.setOrientation(LinearLayout.VERTICAL);
    int padding = Math.round(24 * activity.getResources().getDisplayMetrics().density);
    content.setPadding(padding, padding / 2, padding, 0);
    content.addView(bar);
    content.addView(status);
    AlertDialog progress = show(new AlertDialog.Builder(activity)
        .setTitle("Downloading update")
        .setView(content)
        .setNegativeButton("Cancel", (ignored, which) -> cancel(transfer)));
    progress.setCanceledOnTouchOutside(false);
    progress.setOnCancelListener(ignored -> cancel(transfer));

    File file = new File(UpdateApkProvider.directory(activity), UUID.randomUUID() + ".apk");
    executor.execute(() -> {
      String error = null;
      try {
        downloadVerified(transfer, release, file, (received, total) -> activity.runOnUiThread(() -> {
          if (active != transfer) return;
          if (total <= 0) {
            status.setText(R.string.update_downloading);
          } else {
            bar.setIndeterminate(false);
            bar.setProgress(received);
            if (received == 100) status.setText(R.string.update_verifying);
            else status.setText(NumberFormat.getPercentInstance().format(received / 100.0));
          }
        }));
        PackageInfo archive = activity.getPackageManager().getPackageArchiveInfo(file.getPath(), 0);
        if (archive == null || !activity.getPackageName().equals(archive.packageName)
            || !release.version().equals(archive.versionName)
            || archive.versionCode <= BuildConfig.VERSION_CODE) {
          throw new IOException("The APK does not match this PocketAgent update.");
        }
      } catch (Exception exception) {
        error = exception instanceof VerificationException
            ? "Could not verify the downloaded update." : "Could not download a valid PocketAgent update.";
      }
      String failureMessage = error;
      activity.runOnUiThread(() -> {
        if (active != transfer || !isAvailable()) {
          file.delete();
          return;
        }
        active = null;
        progress.dismiss();
        if (failureMessage == null) {
          install(release, file);
        } else {
          file.delete();
          failure(release, failureMessage);
        }
      });
    });
  }

  @FunctionalInterface
  private interface Progress {
    void report(int percent, long total);
  }

  private static final class VerificationException extends IOException {}

  private void downloadVerified(Transfer transfer, Release release, File file, Progress progress) throws Exception {
    String expected = release.digest();
    if (expected.isEmpty() && !release.checksum().isEmpty()) {
      expected = readText(transfer, release.checksum(), 1024).trim().split("\\s+", 2)[0];
    }
    if (!expected.matches("[0-9a-fA-F]{64}")) throw new VerificationException();
    File directory = file.getParentFile();
    if (!directory.isDirectory() && !directory.mkdirs() && !directory.isDirectory()) {
      throw new IOException("Could not create update cache");
    }
    Call call = call(transfer, release.apk());
    MessageDigest digest = MessageDigest.getInstance("SHA-256");
    try (Response response = call.execute()) {
      ResponseBody body = requireBody(response);
      long total = body.contentLength();
      long received = 0;
      int lastPercent = -1;
      try (InputStream input = body.byteStream(); FileOutputStream output = new FileOutputStream(file)) {
        byte[] bytes = new byte[64 * 1024];
        for (int count; (count = input.read(bytes)) != -1; ) {
          output.write(bytes, 0, count);
          digest.update(bytes, 0, count);
          received += count;
          int percent = total > 0 ? (int) Math.min(100, received * 100.0 / total) : 0;
          if (percent != lastPercent) {
            progress.report(percent, total);
            lastPercent = percent;
          }
        }
      }
    }
    StringBuilder actual = new StringBuilder(64);
    for (byte value : digest.digest()) actual.append(String.format(Locale.ROOT, "%02x", value));
    if (!expected.equalsIgnoreCase(actual.toString())) throw new VerificationException();
  }

  private void install(Release release, File file) {
    Uri uri = new Uri.Builder().scheme("content").authority(activity.getPackageName() + ".update")
        .appendPath(file.getName()).build();
    try {
      installing = file;
      Intent install = new Intent(Intent.ACTION_INSTALL_PACKAGE)
          .setDataAndType(uri, APK_MIME)
          .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
          .putExtra(Intent.EXTRA_RETURN_RESULT, true);
      activity.startActivityForResult(install, INSTALL_APK);
    } catch (RuntimeException exception) {
      installing = null;
      file.delete();
      failure(release, "Could not open Android’s package installer.");
    }
  }

  private void failure(Release release, String message) {
    show(new AlertDialog.Builder(activity)
        .setTitle("Update couldn’t finish")
        .setMessage(message)
        .setNegativeButton("Close", null)
        .setNeutralButton("Open release", (ignored, which) -> {
          try {
            activity.startActivity(new Intent(Intent.ACTION_VIEW, Uri.parse(release.page())));
          } catch (RuntimeException exception) {
            toast("Could not open the release page.");
          }
        })
        .setPositiveButton("Retry", (ignored, which) -> requestInstall(release)));
  }

  private Call call(Transfer transfer, String url) throws IOException {
    Call call = client.newCall(new Request.Builder().url(url)
        .header("User-Agent", "PocketAgent/" + BuildConfig.VERSION_NAME).build());
    transfer.start(call);
    return call;
  }

  private String readText(Transfer transfer, String url, int limit) throws IOException {
    try (Response response = call(transfer, url).execute()) {
      okio.BufferedSource source = requireBody(response).source();
      if (source.request(limit + 1L)) throw new IOException("Update metadata was too large");
      return source.readString(StandardCharsets.UTF_8);
    }
  }

  private static ResponseBody requireBody(Response response) throws IOException {
    ResponseBody body = response.body();
    if (!response.isSuccessful() || body == null) throw new IOException("Update request failed");
    return body;
  }

  private AlertDialog show(AlertDialog.Builder builder) {
    AlertDialog shown = builder.create();
    dialog = shown;
    shown.setOnDismissListener(ignored -> {
      if (dialog == shown) dialog = null;
    });
    shown.show();
    return shown;
  }

  private boolean isAvailable() {
    return !activity.isDestroyed() && !activity.isFinishing();
  }

  private void toast(String message) {
    Toast.makeText(activity, message, Toast.LENGTH_SHORT).show();
  }

  private void cancel(Transfer transfer) {
    transfer.cancel();
    if (active == transfer) active = null;
  }

  void close() {
    if (active != null) cancel(active);
    if (dialog != null) dialog.dismiss();
  }
}
