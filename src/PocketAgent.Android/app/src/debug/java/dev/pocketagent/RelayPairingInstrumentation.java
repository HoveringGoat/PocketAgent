package dev.pocketagent;

import android.app.Activity;
import android.app.Instrumentation;
import android.content.Intent;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.os.Bundle;
import android.os.SystemClock;
import android.view.View;
import android.view.ViewGroup;
import android.widget.Button;
import java.io.File;
import org.json.JSONArray;
import org.json.JSONObject;

/** Uses the Android UI and real QR emitted by two isolated PocketAgent desktop instances. */
public final class RelayPairingInstrumentation extends Instrumentation {
  @Override public void onCreate(Bundle args) { start(); }

  @Override public void onStart() {
    Bundle result = new Bundle();
    SessionStore store = new SessionStore(getTargetContext());
    String origin = null;
    JSONObject previous = null;
    boolean updates = store.checkUpdatesOnStartup();
    int code = Activity.RESULT_CANCELED;
    try {
      previous = store.load();
      store.checkUpdatesOnStartup(false);
      File fixture = new File(getTargetContext().getCacheDir(), "relay-pairing.png");
      Bitmap bitmap = BitmapFactory.decodeFile(fixture.getPath());
      check(bitmap != null, "Desktop QR fixture did not load.");
      int width = bitmap.getWidth(), height = bitmap.getHeight();
      int[] colors = new int[width * height];
      bitmap.getPixels(colors, 0, width, 0, 0, width, height);
      bitmap.recycle();
      byte[] pixels = new byte[colors.length];
      for (int i = 0; i < colors.length; i++) pixels[i] = (byte) android.graphics.Color.red(colors[i]);
      String payload = QrDecoder.decode(pixels, width, height);
      PairingTicket ticket = PairingTicket.parse(payload);
      origin = ticket.origin;
      Activity activity = startActivitySync(new Intent(getTargetContext(), MainActivity.class).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
      runOnMainSync(() -> {
        try {
          var pairing = MainActivity.class.getDeclaredMethod("showPairing");
          pairing.setAccessible(true);
          pairing.invoke(activity);
          ((MainActivity) activity).onActivityResult(41, Activity.RESULT_OK, new Intent().putExtra("pairing", payload));
          findConnect(activity.findViewById(android.R.id.content)).performClick();
        } catch (ReflectiveOperationException exception) { throw new IllegalStateException("Could not open Add server.", exception); }
      });
      JSONObject saved = null;
      long deadline = SystemClock.elapsedRealtime() + 15000;
      while (SystemClock.elapsedRealtime() < deadline) {
        try { saved = store.connection(origin); break; }
        catch (IllegalArgumentException pending) { Thread.sleep(100); }
      }
      check(saved != null && saved.has("relay"), "Android UI did not pair through the live relay.");
      runOnMainSync(activity::finish);
      ApiClient restored = ApiClient.fromSaved(saved);
      try {
        check(((JSONObject) restored.request("/session", null)).getBoolean("authenticated"), "Remote desktop rejected the saved phone token.");
        check(restored.request("/conversations", null) instanceof JSONArray, "Phone could not load conversations through relay.");
        restored.close();
        check(restored.request("/scheduled", null) instanceof JSONArray, "Phone could not reconnect over relay.");
        restored.request("/logout", new JSONObject());
        try {
          restored.request("/conversations", null);
          throw new AssertionError("Revoked phone token kept API access.");
        } catch (ApiClient.ApiError revoked) { check(revoked.status == 401, "Unexpected revocation status."); }
      } finally { restored.close(); }
      result.putString("stream", "PASS Android decoded the desktop relay QR, paired through Add server, restored credentials, read the authenticated API, reconnected and verified revocation.\n");
      code = Activity.RESULT_OK;
    } catch (Throwable error) {
      result.putString("stream", "FAIL " + error.getClass().getSimpleName() + ": " + error.getMessage());
    } finally {
      try {
        if (origin != null) store.remove(origin);
        if (previous != null) store.activate(previous.getString("origin"));
      } catch (Exception ignored) { }
      store.checkUpdatesOnStartup(updates);
    }
    finish(code, result);
  }

  private static Button findConnect(View view) {
    if (view instanceof Button && "Connect".contentEquals(((Button) view).getText())) return (Button) view;
    if (view instanceof ViewGroup) {
      ViewGroup group = (ViewGroup) view;
      for (int i = 0; i < group.getChildCount(); i++) {
        Button found = findConnect(group.getChildAt(i));
        if (found != null) return found;
      }
    }
    return null;
  }

  private static void check(boolean condition, String message) {
    if (!condition) throw new AssertionError(message);
  }
}
