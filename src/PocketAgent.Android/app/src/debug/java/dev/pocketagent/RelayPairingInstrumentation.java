package dev.pocketagent;

import android.app.Activity;
import android.app.Instrumentation;
import android.content.Intent;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.net.Uri;
import android.os.Bundle;
import android.os.SystemClock;
import android.view.View;
import android.view.ViewGroup;
import android.view.accessibility.AccessibilityNodeInfo;
import android.widget.Button;
import java.io.File;
import java.io.FileOutputStream;
import org.json.JSONArray;
import org.json.JSONObject;

/** Uses the Android UI and real QR emitted by two isolated PocketAgent desktop instances. */
public final class RelayPairingInstrumentation extends Instrumentation {
  private static final String OFFLINE = "https://127.0.0.1:1";
  private String alternatePort;

  @Override public void onCreate(Bundle args) {
    alternatePort = args.getString("alternatePort");
    start();
  }

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
      checkDirectState(store);
      store.save(OFFLINE, "fixture-offline", "Offline fixture");
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
      Activity activity = startActivitySync(new Intent(getTargetContext(), MainActivity.class)
          .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TASK));
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
      String token = saved.getString("token");
      awaitStoredConnection(activity);
      store.select(origin, "fixture-conversation");
      store.draft(origin, "fixture-conversation", "Draft across address change");
      if (alternatePort != null) {
        String originalEndpoint = ticket.relay.endpoint;
        String movedEndpoint = "wss://" + new java.net.URI(originalEndpoint).getHost() + ":" + alternatePort + "/relay";
        String movedPayload = payload.replace(Uri.encode(originalEndpoint), Uri.encode(movedEndpoint));
        invoke(activity, "showSettings", new Class<?>[0]);
        invoke(activity, "showUpdateAddress", new Class<?>[] {String.class, String.class}, origin, movedPayload);
        capture("address");
        click("Verify and save");
        try {
          await(() -> movedEndpoint.equals(store.connection(ticket.origin).getJSONObject("relay").getString("endpoint")));
        } catch (AssertionError timeout) {
          capture("move-failed");
          throw new AssertionError("Android did not save the verified relay endpoint.", timeout);
        }
        saved = store.connection(origin);
        check(token.equals(saved.getString("token")) && "fixture-conversation".equals(store.selected(origin))
            && "Draft across address change".equals(store.draft(origin, "fixture-conversation")),
            "Updating the relay address must preserve the token and local state.");
        invoke(activity, "showUpdateAddress", new Class<?>[] {String.class, String.class}, origin,
            movedPayload.replace(Uri.encode(movedEndpoint), Uri.encode("wss://127.0.0.1:1/relay")));
        click("Verify and save");
        awaitText("Address wasn’t updated");
        check(movedEndpoint.equals(store.connection(origin).getJSONObject("relay").getString("endpoint")),
            "A failed check replaced the saved relay address.");
        click("Close");
      }
      invoke(activity, "confirmUnpair", new Class<?>[] {String.class}, OFFLINE);
      click("Unpair");
      awaitText("Forget on this phone");
      capture("forget");
      click("Forget on this phone");
      await(() -> !hasConnection(store, OFFLINE));
      check(origin.equals(store.load().getString("origin")), "Removing an offline server changed the active connection.");
      runOnMainSync(activity::finish);
      await(activity::isDestroyed);
      ApiClient restored = ApiClient.fromSaved(saved);
      try {
        check(((JSONObject) restored.request("/session", null)).getBoolean("authenticated"), "Remote desktop rejected the saved phone token.");
        check(restored.request("/conversations", null) instanceof JSONArray, "Phone could not load conversations through relay.");
        restored.close();
        check(restored.request("/scheduled", null) instanceof JSONArray, "Phone could not reconnect over relay.");
        restored.close();
        Activity reopened = startActivitySync(new Intent(getTargetContext(), MainActivity.class)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TASK));
        invoke(reopened, "showSettings", new Class<?>[0]);
        invoke(reopened, "confirmUnpair", new Class<?>[] {String.class}, origin);
        click("Unpair");
        await(() -> !hasConnection(store, ticket.origin));
        runOnMainSync(reopened::finish);
        try {
          restored.request("/conversations", null);
          throw new AssertionError("Revoked phone token kept API access.");
        } catch (ApiClient.ApiError revoked) { check(revoked.status == 401, "Unexpected revocation status."); }
      } finally { restored.close(); }
      result.putString("stream", "PASS Android relay QR, moved endpoint, preserved pairing and drafts, offline forget, and online revocation.\n");
      code = Activity.RESULT_OK;
    } catch (Throwable error) {
      result.putString("stream", "FAIL " + error.getClass().getSimpleName() + ": " + error.getMessage());
    } finally {
      try {
        if (origin != null) store.remove(origin);
        store.remove(OFFLINE);
        if (previous != null) store.activate(previous.getString("origin"));
      } catch (Exception ignored) { }
      store.checkUpdatesOnStartup(updates);
    }
    finish(code, result);
  }

  private static void checkDirectState(SessionStore store) throws Exception {
    String previous = "https://before.pocketagent.test";
    String updated = "https://after.pocketagent.test";
    String pin = String.join("", java.util.Collections.nCopies(64, "a"));
    try {
      store.save(previous, "fixture-direct", "Direct fixture", pin);
      ApiClient old = ApiClient.fromSaved(store.connection(previous));
      store.select(old.stateKey, "direct-fixture");
      store.draft(old.stateKey, "direct-fixture", "Keep this draft");
      store.uncertain(old.stateKey, "direct-fixture", true);
      ApiClient candidate = old.atUpdatedAddress(updated);
      store.updateAddress(old, candidate);
      ApiClient moved = ApiClient.fromSaved(store.load());
      check(moved.origin.equals(updated) && moved.pin.equals(pin) && moved.token.equals(old.token)
          && store.selected(moved.stateKey).equals("direct-fixture")
          && store.draft(moved.stateKey, "direct-fixture").equals("Keep this draft")
          && store.uncertain(moved.stateKey, "direct-fixture"),
          "Replacing a legacy direct endpoint must preserve its credential and local state.");
      check(!hasConnection(store, previous), "The old direct endpoint is still saved.");
      store.draft(moved.stateKey, "direct-fixture", "");
      store.uncertain(moved.stateKey, "direct-fixture", false);
    } finally {
      store.remove(previous);
      store.remove(updated);
    }
  }

  private void awaitStoredConnection(Activity activity) throws Exception {
    await(() -> {
      var api = MainActivity.class.getDeclaredField("api");
      api.setAccessible(true);
      Object[] selected = new Object[1];
      runOnMainSync(() -> {
        try { selected[0] = api.get(activity); }
        catch (IllegalAccessException exception) { throw new AssertionError(exception); }
      });
      return selected[0] instanceof ApiClient && ((ApiClient) selected[0]).relay != null;
    });
  }

  private static boolean hasConnection(SessionStore store, String origin) throws Exception {
    try { store.connection(origin); return true; }
    catch (IllegalArgumentException missing) { return false; }
  }

  private void invoke(Activity activity, String method, Class<?>[] types, Object... arguments) {
    runOnMainSync(() -> {
      try {
        var action = MainActivity.class.getDeclaredMethod(method, types);
        action.setAccessible(true);
        action.invoke(activity, arguments);
      } catch (ReflectiveOperationException exception) { throw new AssertionError(method, exception); }
    });
  }

  private interface Condition { boolean ready() throws Exception; }

  private static void await(Condition condition) throws Exception {
    long deadline = SystemClock.elapsedRealtime() + 12_000;
    while (SystemClock.elapsedRealtime() < deadline) {
      if (condition.ready()) return;
      Thread.sleep(50);
    }
    throw new AssertionError("Timed out waiting for the saved connection.");
  }

  private void click(String text) throws Exception {
    AccessibilityNodeInfo button = awaitText(text);
    check(button.performAction(AccessibilityNodeInfo.ACTION_CLICK), "Could not click " + text);
    waitForIdleSync();
  }

  private AccessibilityNodeInfo awaitText(String text) throws Exception {
    AccessibilityNodeInfo[] found = new AccessibilityNodeInfo[1];
    try {
      await(() -> {
        AccessibilityNodeInfo root = getUiAutomation().getRootInActiveWindow();
        if (root == null) return false;
        for (AccessibilityNodeInfo node : root.findAccessibilityNodeInfosByText(text)) {
          if (node.isVisibleToUser() && node.getText() != null && text.equalsIgnoreCase(node.getText().toString())) {
            found[0] = node;
            return true;
          }
        }
        return false;
      });
    } catch (AssertionError timeout) { throw new AssertionError("Missing visible control: " + text, timeout); }
    return found[0];
  }

  private void capture(String name) throws Exception {
    SystemClock.sleep(350);
    Bitmap bitmap = getUiAutomation().takeScreenshot();
    check(bitmap != null, "Could not capture " + name);
    try (FileOutputStream output = new FileOutputStream(new File(getTargetContext().getCacheDir(), "recovery-" + name + ".png"))) {
      bitmap.compress(Bitmap.CompressFormat.PNG, 100, output);
    } finally { bitmap.recycle(); }
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
