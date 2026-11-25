package dev.pocketagent;

import android.app.Activity;
import android.app.Instrumentation;
import android.content.Intent;
import android.graphics.Bitmap;
import android.graphics.Rect;
import android.os.Bundle;
import android.os.SystemClock;
import android.view.MotionEvent;
import android.view.accessibility.AccessibilityNodeInfo;
import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.nio.file.Files;
import java.security.MessageDigest;
import java.util.Collections;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;
import okhttp3.MediaType;
import okhttp3.OkHttpClient;
import okhttp3.Protocol;
import okhttp3.Response;
import okhttp3.ResponseBody;
import okio.Buffer;
import okio.Okio;
import okio.Source;
import okio.Timeout;
import org.json.JSONArray;
import org.json.JSONObject;

/** Debug-only check of the update dialogs, checksum gate and Android installer handoff. */
public final class UpdateInstrumentation extends Instrumentation {
  private static final String VERSION = "9.0.0";
  private static final String APK = "PocketAgent-" + VERSION + "-android.apk";
  private static final String URL = "https://github.com/HoveringGoat/PocketAgent/releases/download/android-v"
      + VERSION + "/" + APK;

  @Override public void onCreate(Bundle arguments) {
    super.onCreate(arguments);
    start();
  }

  @Override public void onStart() {
    Bundle result = new Bundle();
    SessionStore store = new SessionStore(getTargetContext());
    boolean startupChecks = store.checkUpdatesOnStartup();
    try {
      store.checkUpdatesOnStartup(false);
      verify();
      result.putString("checks", "Release selection, download cancellation, checksum failure, retry, and system installer passed");
    } catch (Throwable error) {
      result.putString("error", error.toString());
    } finally {
      store.checkUpdatesOnStartup(startupChecks);
    }
    finish(result.containsKey("error") ? Activity.RESULT_CANCELED : Activity.RESULT_OK, result);
  }

  private void verify() throws Exception {
    JSONObject apkAsset = new JSONObject().put("name", APK).put("browser_download_url", URL);
    JSONObject checksumAsset = new JSONObject().put("name", APK + ".sha256").put("browser_download_url", URL + ".sha256");
    JSONObject stable = new JSONObject().put("tag_name", "android-v" + VERSION)
        .put("body", "## Android\n" + String.join("", Collections.nCopies(35, "Long release notes for scrollable update content.\n"))
            + "\n## Desktop\nDesktop-only notes")
        .put("assets", new JSONArray().put(apkAsset).put(checksumAsset));
    JSONArray releases = new JSONArray().put(new JSONObject(stable.toString()).put("prerelease", true))
        .put(new JSONObject().put("tag_name", "android-v99.0.0")).put(stable);
    AndroidUpdater.Release selected = AndroidUpdater.selectRelease(releases, BuildConfig.VERSION_NAME);
    if (selected == null || !VERSION.equals(selected.version()) || selected.notes().contains("Desktop-only")
        || AndroidUpdater.selectRelease(releases, VERSION) != null) {
      throw new AssertionError("Android release selection failed");
    }

    byte[] apk = Files.readAllBytes(new File(getTargetContext().getCacheDir(), "update-fixture.apk").toPath());
    String checksum = new Buffer().write(MessageDigest.getInstance("SHA-256").digest(apk)).readByteString().hex();
    AtomicInteger downloads = new AtomicInteger();
    CountDownLatch firstDownload = new CountDownLatch(1);
    CountDownLatch cancelled = new CountDownLatch(1);
    OkHttpClient client = new OkHttpClient.Builder().addInterceptor(chain -> {
      String url = chain.request().url().toString();
      ResponseBody body;
      if (url.startsWith("https://api.github.com/")) {
        body = ResponseBody.create(MediaType.parse("application/json"), releases.toString());
      } else if (url.equals(URL + ".sha256")) {
        body = ResponseBody.create(null, checksum + "  " + APK);
      } else if (url.equals(URL)) {
        int attempt = downloads.incrementAndGet();
        body = attempt == 1
            ? stalledDownload(apk, chain.call(), firstDownload, cancelled)
            : ResponseBody.create(null, attempt == 2 ? new byte[] {1, 2, 3} : apk);
      } else {
        throw new AssertionError("Unexpected update request " + url);
      }
      return new Response.Builder().protocol(Protocol.HTTP_1_1).code(200).message("OK")
          .request(chain.request()).body(body).build();
    }).build();

    Activity activity = startActivitySync(new Intent(getTargetContext(), MainActivity.class)
        .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TASK));
    // Inject fixture HTTP into the production updater so Activity results still take the normal path.
    var activityField = MainActivity.class.getDeclaredField("updater");
    activityField.setAccessible(true);
    AndroidUpdater updater = (AndroidUpdater) activityField.get(activity);
    var clientField = AndroidUpdater.class.getDeclaredField("client");
    clientField.setAccessible(true);
    clientField.set(updater, client);
    runOnMainSync(() -> updater.check(true));

    awaitText("Download and install");
    capture("offer");
    click("Download and install");
    if (!activity.getPackageManager().canRequestPackageInstalls()) {
      awaitText("Open Settings");
      capture("permission");
      click("Open Settings");
      click("Allow from this source");
      long permissionDeadline = SystemClock.elapsedRealtime() + 3000;
      while (!activity.getPackageManager().canRequestPackageInstalls()
          && SystemClock.elapsedRealtime() < permissionDeadline) SystemClock.sleep(50);
      if (!activity.getPackageManager().canRequestPackageInstalls()) {
        throw new AssertionError("Android did not grant PocketAgent install permission");
      }
      getUiAutomation().performGlobalAction(android.accessibilityservice.AccessibilityService.GLOBAL_ACTION_BACK);
    }
    awaitText("Downloading update");
    if (!firstDownload.await(5, TimeUnit.SECONDS)) throw new AssertionError("Download didn't start");
    awaitText("49%", "50%");
    capture("download");
    click("Cancel");
    if (!cancelled.await(5, TimeUnit.SECONDS)) throw new AssertionError("HTTP call wasn't cancelled");
    waitForIdleSync();

    runOnMainSync(() -> updater.check(true));
    click("Download and install");
    awaitText("Could not verify the downloaded update.");
    awaitText("Open release");
    capture("failure");
    click("Retry");
    AccessibilityNodeInfo button = awaitText("Update", "Install");
    if ("dev.pocketagent".contentEquals(button.getPackageName())) {
      throw new AssertionError("Expected Android's package installer");
    }
    capture("installer");
    click("Cancel");
    if (downloads.get() != 3) throw new AssertionError("Expected three download attempts");
    long deadline = SystemClock.elapsedRealtime() + 3000;
    File directory = UpdateApkProvider.directory(activity);
    while (hasCachedApk(directory) && SystemClock.elapsedRealtime() < deadline) SystemClock.sleep(50);
    if (hasCachedApk(directory)) throw new AssertionError("Update cache was not cleaned up");
    runOnMainSync(activity::finish);
  }

  private static ResponseBody stalledDownload(byte[] apk, okhttp3.Call call,
      CountDownLatch started, CountDownLatch cancelled) {
    return ResponseBody.create(null, apk.length, Okio.buffer(new Source() {
      private final Buffer firstHalf = new Buffer().write(apk, 0, apk.length / 2);

      @Override public long read(Buffer target, long count) throws IOException {
        if (firstHalf.size() > 0) return firstHalf.read(target, count);
        started.countDown();
        long deadline = SystemClock.elapsedRealtime() + 10_000;
        while (!call.isCanceled() && SystemClock.elapsedRealtime() < deadline) SystemClock.sleep(25);
        if (call.isCanceled()) cancelled.countDown();
        throw new IOException("Fixture download stopped");
      }

      @Override public Timeout timeout() { return Timeout.NONE; }
      @Override public void close() {}
    }));
  }

  private void capture(String name) throws Exception {
    SystemClock.sleep(350); // Let the dialog entrance and exit animations finish.
    File screenshot = new File(getTargetContext().getCacheDir(), "update-" + name + ".png");
    Bitmap bitmap = getUiAutomation().takeScreenshot();
    if (bitmap == null) throw new AssertionError("Could not capture " + name);
    try (FileOutputStream output = new FileOutputStream(screenshot)) {
      bitmap.compress(Bitmap.CompressFormat.PNG, 100, output);
    } finally {
      bitmap.recycle();
    }
  }

  private static boolean hasCachedApk(File directory) {
    String[] files = directory.list((ignored, name) -> name.endsWith(".apk"));
    return files != null && files.length > 0;
  }

  private void click(String label) {
    AccessibilityNodeInfo node = awaitText(label);
    Rect bounds = new Rect();
    node.getBoundsInScreen(bounds);
    for (AccessibilityNodeInfo target = node; target != null; target = target.getParent()) {
      if (target.isClickable() && target.performAction(AccessibilityNodeInfo.ACTION_CLICK)) return;
    }
    long now = SystemClock.uptimeMillis();
    MotionEvent down = MotionEvent.obtain(now, now, MotionEvent.ACTION_DOWN, bounds.centerX(), bounds.centerY(), 0);
    MotionEvent up = MotionEvent.obtain(now, now, MotionEvent.ACTION_UP, bounds.centerX(), bounds.centerY(), 0);
    try {
      if (!getUiAutomation().injectInputEvent(down, true) || !getUiAutomation().injectInputEvent(up, true)) {
        throw new AssertionError("Could not click " + label);
      }
    } finally {
      down.recycle();
      up.recycle();
    }
  }

  private AccessibilityNodeInfo awaitText(String... labels) {
    long deadline = SystemClock.elapsedRealtime() + 12_000;
    do {
      AccessibilityNodeInfo found = findText(getUiAutomation().getRootInActiveWindow(), labels);
      if (found != null) return found;
      SystemClock.sleep(50);
    } while (SystemClock.elapsedRealtime() < deadline);
    throw new AssertionError("Missing visible text: " + String.join(" / ", labels));
  }

  private static AccessibilityNodeInfo findText(AccessibilityNodeInfo node, String[] labels) {
    if (node == null) return null;
    if (node.getText() != null && node.isVisibleToUser()) {
      for (String label : labels) {
        if (label.equalsIgnoreCase(node.getText().toString())) return node;
      }
    }
    for (int i = 0; i < node.getChildCount(); i++) {
      AccessibilityNodeInfo found = findText(node.getChild(i), labels);
      if (found != null) return found;
    }
    return null;
  }
}
