package dev.pocketagent;

import android.app.Activity;
import android.os.Bundle;
import android.view.View;

/** Debug-only visual fixture for comparing the conversation surface with the approved mockup. */
public final class PreviewActivity extends Activity {
  @Override
  protected void onCreate(Bundle state) {
    super.onCreate(state);
    ConversationScreenView screen =
        new ConversationScreenView(
            this,
            1.0f,
            "Work PC",
            new ConversationScreenView.Actions() {
              public void selectServer() {}

              public void openSettings() {}

              public void newConversation() {}

              public void renameConversation() {}

              public void selectModel() {}

              public void archiveConversation() {}

              public void showArchived() {}

              public void stopWork() {}

              public void scheduleMessage() {}

              public void loadOlderHistory() {}
            });
    setContentView(screen.root);

    screen.usageView.setText(getString(R.string.preview_usage));
    screen.addTab("Fix updater", true, v -> {});
    screen.addTab("UI polish", false, v -> {});
    screen.addTab("Release prep", false, v -> {});
    screen.sessionLocation.setText(getString(R.string.preview_directory));
    screen.setConversationActionsVisible(true);
    screen.addMessage("Can you make the conversation pane match the mockup?", true, "8:14 PM");
    screen.addMessage(
        "I’m restructuring the Android surface so the server, tabs, status, and composer read as one workspace.",
        false,
        "8:15 PM");
    screen.addWorkDuration("Worked for 2m 14s");
    screen.addMessage("The preview should be close enough to review before release.", false, "8:17 PM");
    screen.context.setText(getString(R.string.preview_running));
    screen.stop.setVisibility(View.VISIBLE);
    screen.composer.setText(getString(R.string.preview_composer));
    screen.schedule.setText(getString(R.string.preview_scheduled));
    screen.schedule.setContentDescription("2 scheduled messages; schedule message");
  }
}
