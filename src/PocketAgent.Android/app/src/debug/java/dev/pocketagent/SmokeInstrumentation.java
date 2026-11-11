package dev.pocketagent;

import android.app.Activity;
import android.app.Instrumentation;
import android.content.Intent;
import android.graphics.Bitmap;
import android.os.Bundle;
import android.view.View;
import android.view.ViewGroup;
import android.widget.EditText;
import android.widget.TextView;
import java.io.File;
import java.io.FileOutputStream;
import java.util.concurrent.Callable;
import org.json.JSONObject;

/** Debug-only native UI check against the deterministic provider. Never shipped in release. */
@android.annotation.SuppressLint("SetTextI18n") // Fixed protocol fixture strings, not product copy.
public final class SmokeInstrumentation extends Instrumentation {
  private Bundle arguments;
  private Activity activity;
  private String checkStage = "startup";
  private String workspace;

  @Override
  public void onCreate(Bundle args) {
    super.onCreate(args);
    arguments = args;
    start();
  }

  @Override
  public void onStart() {
    Bundle result = new Bundle();
    try {
      activity =
          startActivitySync(
              new Intent(getTargetContext(), MainActivity.class)
                  .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
      if ("restore".equals(arguments.getString("phase"))) {
        await(this::connected);
        await(
            () ->
                main(
                    () ->
                        ((EditText) description("Message Codex"))
                            .getText()
                            .toString()
                            .equals("Next draft")));
        result.putString("checks", "encrypted-session restoration and draft restoration passed");
      } else {
        workspace =
            new String(
                android.util.Base64.decode(
                    arguments.getString("workspaceBase64"), android.util.Base64.DEFAULT),
                java.nio.charset.StandardCharsets.UTF_8);
        // Pairing transport/UI is covered separately by PairingInstrumentation with real pinned
        // TLS.
        // Seed this chat regression through the debug-only loopback fixture so it can focus on
        // steering, drafts, folders/models and restoration without using a physical camera.
        JSONObject paired =
            (JSONObject)
                new ApiClient(arguments.getString("origin"), "")
                    .request(
                        "/devices/pair",
                        new JSONObject()
                            .put("deviceName", "Chat fixture")
                            .put("code", arguments.getString("code")));
        new SessionStore(getTargetContext())
            .save(arguments.getString("origin"), paired.getString("token"), "Personal");
        main(
            () -> {
              activity.finish();
              return true;
            });
        activity =
            startActivitySync(
                new Intent(getTargetContext(), MainActivity.class)
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
        await(this::connected);
        main(() -> description("New conversation").performClick());
        dialogClick("Create conversation");
        await(() -> main(() -> description("Conversation PocketAgent") != null));
        boolean persistedBeforeDelivery =
            main(
                () -> {
                  ((EditText) description("Message Codex")).setText("hold");
                  description("Send message").performClick();
                  SessionStore storage = new SessionStore(getTargetContext());
                  return storage.uncertain(
                      arguments.getString("origin"),
                      storage.selected(arguments.getString("origin")));
                });
        check(persistedBeforeDelivery, "In-flight delivery was not persisted before sending.");
        await(() -> contains("Steer"));
        main(
            () -> {
              ((EditText) description("Message Codex")).setText("Focus on tests");
              description("Send message").performClick();
              ((EditText) description("Message Codex")).setText("Next draft");
              return true;
            });
        await(() -> contains("Focus on tests"));
        check(!contains("YOU"), "Redundant user speaker label is present.");
        await(() -> containsPart("5h: 75%"));
        check(
            main(
                () ->
                    find(
                            activity.getWindow().getDecorView(),
                            view ->
                                view instanceof TextView
                                    && !(view instanceof EditText)
                                    && ((TextView) view)
                                        .getText()
                                        .toString()
                                        .matches("\\d{1,2}:\\d{2}(\\s*[AaPp][Mm])?"))
                        != null),
            "Message time is missing.");
        check(
            main(
                () ->
                    ((EditText) description("Message Codex"))
                        .getText()
                        .toString()
                        .equals("Next draft")),
            "Draft typed during send was erased.");
        check(
            main(
                () -> {
                  int[] meta = new int[2], input = new int[2];
                  View status = description("Conversation status and context"),
                      composer = description("Message Codex");
                  status.getLocationOnScreen(meta);
                  composer.getLocationOnScreen(input);
                  return status.isShown()
                      && meta[1] < input[1]
                      && meta[1] + status.getHeight() <= input[1];
                }),
            "Status row is not above the composer.");
        Bitmap screenshot = getUiAutomation().takeScreenshot();
        if (screenshot != null)
          try (FileOutputStream output =
              new FileOutputStream(new File(getTargetContext().getCacheDir(), "native-chat.png"))) {
            screenshot.compress(Bitmap.CompressFormat.PNG, 100, output);
          }
        main(() -> description("Stop work").performClick());
        await(() -> containsPart("interrupted ·"));
        check(containsPart("worked"), "Completed work duration is missing.");
        if (arguments.getString("secondOrigin") != null) {
          String other = arguments.getString("secondOrigin");
          JSONObject secondary =
              (JSONObject)
                  new ApiClient(other, "")
                      .request(
                          "/devices/pair",
                          new JSONObject()
                              .put("deviceName", "Isolated second server")
                              .put("code", arguments.getString("secondCode")));
          new SessionStore(getTargetContext()).save(other, secondary.getString("token"), "Work");
          main(() -> description("Connection options").performClick());
          dialogClick("Servers");
          dialogClick("Work");
          await(this::connected);
          main(() -> description("New conversation").performClick());
          dialogClick("Create conversation");
          await(() -> main(() -> description("Conversation PocketAgent") != null));
          main(
              () -> {
                ((EditText) description("Message Codex")).setText("hold");
                description("Send message").performClick();
                return true;
              });
          await(() -> contains("Steer"));
          main(() -> description("Connection options").performClick());
          dialogClick("Servers");
          dialogClick(arguments.getString("origin"));
          await(this::connected);
          await(() -> contains("Focus on tests"));
          check(
              main(
                  () ->
                      ((EditText) description("Message Codex"))
                          .getText()
                          .toString()
                          .equals("Next draft")),
              "Draft leaked or was lost when switching servers.");
        }
        // Create a second conversation in another folder without disturbing the first draft.
        checkStage = "new folder dialog";
        main(() -> description("New conversation").performClick());
        dialogSetText("Conversation working folder", workspace);
        checkStage = "new model dialog";
        dialogClick("Model: Use Codex settings");
        dialogClick("GPT-6 Astra");
        dialogClick("Create conversation");
        checkStage = "new folder creation";
        await(() -> containsPart(workspace));
        String unicodePrompt = "unicode:focus";
        String unicodeReply = "It\u2019s ready \u2014 caf\u00e9 \u6f22\u5b57 \ud83d\ude00.";
        main(
            () -> {
              ((EditText) description("Message Codex")).setText(unicodePrompt);
              description("Send message").performClick();
              EditText input = (EditText) description("Message Codex");
              input.setText("Still typing");
              input.requestFocus();
              input.setSelection(5);
              return true;
            });
        await(() -> contains(unicodeReply));
        check(
            main(
                () -> {
                  EditText input = (EditText) description("Message Codex");
                  return input.hasFocus()
                      && input.getSelectionStart() == 5
                      && input.getSelectionEnd() == 5
                      && input.getText().toString().equals("Still typing");
                }),
            "Incoming assistant output stole input focus, cursor position, or draft text.");
        checkStage = "message appearance";
        check(
            main(
                () ->
                    text(unicodePrompt).getBackground() != null
                        && text(unicodeReply).getBackground() == null),
            "Only user messages should have chat bubbles.");
        main(() -> description("Connection options").performClick());
        checkStage = "change model";
        dialogClick("Choose model");
        dialogClick("GPT-5.6 Sol");
        await(() -> containsPart("gpt-5.6-sol"));
        checkStage = "archive";
        main(() -> description("Connection options").performClick());
        dialogClick("Archive conversation");
        dialogClick("Archive");
        await(() -> contains("Focus on tests"));
        checkStage = "restore archive";
        main(() -> description("Connection options").performClick());
        dialogClick("Archived conversations");
        dialogClick(new File(workspace).getName());
        dialogClick("Restore");
        await(() -> contains(unicodeReply));
        Bitmap unicodeScreenshot = getUiAutomation().takeScreenshot();
        if (unicodeScreenshot != null)
          try (FileOutputStream output =
              new FileOutputStream(new File(getTargetContext().getCacheDir(), "native-chat.png"))) {
            unicodeScreenshot.compress(Bitmap.CompressFormat.PNG, 100, output);
          }
        main(() -> description("Conversation PocketAgent").performClick());
        checkStage = "original conversation";
        await(() -> contains("Focus on tests"));
        org.json.JSONObject session = new SessionStore(getTargetContext()).load();
        check(
            session != null && !session.optString("token").isEmpty(),
            "Device session was not persisted.");
        String encrypted =
            getTargetContext().getSharedPreferences("connection", 0).getString("session", "");
        check(
            !encrypted.contains(session.getString("token")),
            "Device token was stored without encryption.");
        result.putString(
            "checks",
            "native pairing, nameless creation, active steering, draft preservation, bottom status,"
                + " interruption, encrypted storage passed");
      }
      finish(Activity.RESULT_OK, result);
    } catch (Exception exception) {
      // Test errors must never include the pairing code or device token.
      result.putString("stage", checkStage);
      if (exception.getStackTrace().length > 0) {
        StringBuilder frames = new StringBuilder();
        for (StackTraceElement frame : exception.getStackTrace())
          if (frame.getClassName().startsWith("dev.pocketagent"))
            frames
                .append(frame.getFileName())
                .append(':')
                .append(frame.getLineNumber())
                .append(' ');
        result.putString("location", frames.toString());
      }
      result.putString(
          "failure",
          exception.getClass().getSimpleName()
              + ": "
              + (exception instanceof AssertionErrorException
                  ? exception.getMessage()
                  : "Native UI check failed."));
      finish(Activity.RESULT_CANCELED, result);
    }
  }

  private static final class AssertionErrorException extends Exception {
    AssertionErrorException(String text) {
      super(text);
    }
  }

  private void check(boolean value, String message) throws Exception {
    if (!value) throw new AssertionErrorException(message);
  }

  private void await(Callable<Boolean> condition) throws Exception {
    long deadline = System.currentTimeMillis() + 20000;
    while (System.currentTimeMillis() < deadline) {
      if (condition.call()) return;
      Thread.sleep(150);
    }
    throw new AssertionErrorException("Timed out waiting for expected native screen.");
  }

  private <T> T main(Callable<T> action) throws Exception {
    java.util.concurrent.FutureTask<T> task = new java.util.concurrent.FutureTask<>(action);
    runOnMainSync(task);
    return task.get();
  }

  private View find(View parent, java.util.function.Predicate<View> predicate) {
    if (predicate.test(parent)) return parent;
    if (parent instanceof ViewGroup) {
      ViewGroup group = (ViewGroup) parent;
      for (int i = 0; i < group.getChildCount(); i++) {
        View found = find(group.getChildAt(i), predicate);
        if (found != null) return found;
      }
    }
    return null;
  }

  private View description(String value) {
    return find(
        activity.getWindow().getDecorView(),
        view ->
            value.contentEquals(
                view.getContentDescription() == null ? "" : view.getContentDescription()));
  }

  private View text(String value) {
    return find(
        activity.getWindow().getDecorView(),
        view -> view instanceof TextView && value.contentEquals(((TextView) view).getText()));
  }

  private boolean contains(String value) throws Exception {
    return main(() -> text(value) != null);
  }

  private void dialogClick(String text) throws Exception {
    await(
        () -> {
          android.view.accessibility.AccessibilityNodeInfo root =
              getUiAutomation().getRootInActiveWindow();
          if (root == null) return false;
          for (android.view.accessibility.AccessibilityNodeInfo node :
              root.findAccessibilityNodeInfosByText(text)) {
            android.view.accessibility.AccessibilityNodeInfo current = node;
            while (current != null) {
              if (current.isClickable()
                  && current.performAction(
                      android.view.accessibility.AccessibilityNodeInfo.ACTION_CLICK)) return true;
              current = current.getParent();
            }
          }
          return false;
        });
    Thread.sleep(150);
  }

  private android.view.accessibility.AccessibilityNodeInfo described(
      android.view.accessibility.AccessibilityNodeInfo parent, String name) {
    if (parent == null) return null;
    if (parent.getContentDescription() != null
        && name.contentEquals(parent.getContentDescription())) return parent;
    for (int i = 0; i < parent.getChildCount(); i++) {
      android.view.accessibility.AccessibilityNodeInfo found = described(parent.getChild(i), name);
      if (found != null) return found;
    }
    return null;
  }

  private void dialogSetText(String description, String value) throws Exception {
    await(
        () -> {
          android.view.accessibility.AccessibilityNodeInfo field =
              described(getUiAutomation().getRootInActiveWindow(), description);
          if (field == null) return false;
          Bundle data = new Bundle();
          data.putCharSequence(
              android.view.accessibility.AccessibilityNodeInfo
                  .ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE,
              value);
          return field.performAction(
              android.view.accessibility.AccessibilityNodeInfo.ACTION_SET_TEXT, data);
        });
  }

  private boolean connected() throws Exception {
    return main(
        () ->
            find(
                    activity.getWindow().getDecorView(),
                    view ->
                        view.getContentDescription() != null
                            && view.getContentDescription().toString().startsWith("Server ")
                            && view.getContentDescription().toString().endsWith(" connected"))
                != null);
  }

  private boolean containsPart(String value) throws Exception {
    return main(
        () ->
            find(
                    activity.getWindow().getDecorView(),
                    view ->
                        view instanceof TextView
                            && ((TextView) view).getText().toString().contains(value))
                != null);
  }
}
