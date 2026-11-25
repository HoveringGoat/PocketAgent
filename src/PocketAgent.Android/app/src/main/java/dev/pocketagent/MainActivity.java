package dev.pocketagent;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.SharedPreferences;
import android.content.Intent;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.LayerDrawable;
import android.graphics.drawable.StateListDrawable;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.text.Editable;
import android.text.InputType;
import android.text.TextWatcher;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.EditText;
import android.widget.HorizontalScrollView;
import android.widget.ImageButton;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.AdapterView;
import android.widget.ArrayAdapter;
import android.widget.Spinner;
import android.widget.TextView;
import android.net.Uri;
import java.net.ConnectException;
import java.net.UnknownHostException;
import java.net.NoRouteToHostException;
import java.net.SocketTimeoutException;
import java.util.HashMap;
import java.util.Map;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import org.json.JSONArray;
import org.json.JSONObject;

public final class MainActivity extends Activity {
  private static final int BG = Color.rgb(17, 27, 23),
      CARD = Color.rgb(31, 47, 37),
      TEXT = Color.rgb(235, 241, 234),
      ACCENT = Color.rgb(193, 218, 166);
  private static final int PICKER_OUTLINE = Color.rgb(71, 96, 75);
  private static final int PICKER_ACTIVE = Color.rgb(41, 67, 51);
  private final Handler ui = new Handler(Looper.getMainLooper());
  private final ExecutorService network = Executors.newCachedThreadPool();
  private final AndroidUpdater updater = new AndroidUpdater(this, network);
  private SessionStore store;
  private SharedPreferences appPreferences;
  private static final String UI_SCALE_KEY = "uiScale";
  private float uiScale = 1.0f;
  private ApiClient api;
  private final java.util.LinkedHashMap<String, ApiClient> connections = new java.util.LinkedHashMap<>();
  private final java.util.HashMap<String, String> connectionNames = new java.util.HashMap<>();
  private final java.util.concurrent.ConcurrentHashMap<String, Boolean> connectionStates = new java.util.concurrent.ConcurrentHashMap<>();
  private final java.util.concurrent.ConcurrentHashMap<String, String> connectionErrors = new java.util.concurrent.ConcurrentHashMap<>();
  private final java.util.Set<String> backgroundPolling = java.util.concurrent.ConcurrentHashMap.newKeySet();
  private String selected = "", tabSignature = "", transcriptSignature = "";
  private String historyBefore = "";
  private JSONObject conversation;
  private ConversationScreenView screen;
  private LinearLayout root, tabs, transcript, metadata, contextSurface, controlPanel;
  private Button serverSelector;
  private TextView brand, sessionLocation, error, context, usageView;
  private JSONObject usageState;
  private JSONArray scheduledState = new JSONArray();
  private final java.util.HashSet<String> expandedItems = new java.util.HashSet<>();
  private final java.util.HashSet<String> viewedImages = new java.util.HashSet<>();
  private final java.util.HashMap<String, java.util.ArrayList<JSONObject>> optimisticItems = new java.util.HashMap<>();
  private final java.util.HashMap<String, java.util.ArrayList<JSONObject>> olderHistory = new java.util.HashMap<>();
  private final java.util.HashMap<String, String> historyCursors = new java.util.HashMap<>();
  private final java.util.HashMap<String, TextView> renderedRows = new java.util.HashMap<>();
  private long usageReadAt;
  private String serverName = "PocketAgent";
  private EditText composer;
  private record DraftKey(String origin, String conversationId) {}
  private final Map<DraftKey, ComposerImage> draftImages = new HashMap<>();
  private final java.util.Set<DraftKey> receivingImages = new java.util.HashSet<>();
  private Button send, schedule;
  private ImageButton stop;
  private ImageButton renameAction, pinAction, modelAction, archiveAction, resetAction, archivedAction;
  private final java.util.ArrayList<JSONObject> orderedSessions = new java.util.ArrayList<>();
  private ScrollView scroll;
  private boolean resumed, polling, connected, submitting, loadingDraft, connectionProblem, historyLoading, settingsPage;
  private boolean transientMessage;
  private int generation;
  private final Map<String, String> answers = new HashMap<>();
  private final Runnable dismissTransientMessage =
      () -> {
        if (!transientMessage) return;
        transientMessage = false;
        if (error != null) {
          error.setText("");
          error.setVisibility(View.GONE);
        }
      };
  private final Runnable poll =
      new Runnable() {
        @Override
        public void run() {
          if (resumed && api != null) {
            if (!settingsPage) refresh();
            refreshBackgroundConnections();
            ui.postDelayed(this, 1200);
          }
        }
      };
  private final Runnable elapsedTicker =
      new Runnable() {
        @Override
        public void run() {
          if (resumed && !settingsPage) {
            updateElapsedStatus();
            ui.postDelayed(this, 1000);
          }
        }
      };

  @Override
  public void onCreate(Bundle state) {
    super.onCreate(state);
    store = new SessionStore(this);
    appPreferences = getSharedPreferences("app", MODE_PRIVATE);
    uiScale = appPreferences.getFloat(UI_SCALE_KEY, 1.0f);
    try {
      JSONObject session = store.load();
      loadSavedConnections();
      if (session != null) {
        String origin = session.getString("origin");
        api = connections.get(origin);
        if (api == null) {
          api = ApiClient.fromSaved(session);
          connections.put(origin, api);
        }
        serverName = session.optString("name", api.origin);
        connectionNames.put(api.origin, serverName);
        selected = store.selected(api.origin);
      }
    } catch (Exception ignored) {
      store.clear();
    }
    if (api == null) showPairing();
    else showChat();
    if (store.checkUpdatesOnStartup()) updater.check(false);
  }

  @Override
  protected void onResume() {
    super.onResume();
    resumed = true;
    ui.removeCallbacks(poll);
    ui.post(poll);
    ui.removeCallbacks(elapsedTicker);
    ui.post(elapsedTicker);
  }

  @Override
  protected void onPause() {
    resumed = false;
    ui.removeCallbacks(poll);
    ui.removeCallbacks(elapsedTicker);
    super.onPause();
  }

  @Override
  public void onBackPressed() {
    if (settingsPage) {
      showChat();
      return;
    }
    super.onBackPressed();
  }

  @Override
  protected void onDestroy() {
    generation++;
    ui.removeCallbacksAndMessages(null);
    updater.close();
    network.shutdownNow();
    connections.values().forEach(ApiClient::close);
    super.onDestroy();
  }

  private void loadSavedConnections() throws Exception {
    JSONArray servers = store.connections();
    for (int i = 0; i < servers.length(); i++) {
      JSONObject summary = servers.optJSONObject(i);
      if (summary == null) continue;
      JSONObject saved = store.connection(summary.optString("origin"));
      String origin = saved.getString("origin");
      connections.put(origin, ApiClient.fromSaved(saved));
      connectionNames.put(origin, saved.optString("name", origin));
      connectionStates.put(origin, false);
    }
  }

  private void refreshBackgroundConnections() {
    for (java.util.Map.Entry<String, ApiClient> entry : connections.entrySet()) {
      if (!settingsPage && api != null && api.origin.equals(entry.getKey())) continue;
      if (!backgroundPolling.add(entry.getKey())) continue;
      ApiClient client = entry.getValue();
      network.execute(
          () -> {
            try {
              client.request("/conversations", null);
              connectionStates.put(client.origin, true);
              connectionErrors.remove(client.origin);
            } catch (Exception exception) {
              connectionStates.put(client.origin, false);
              connectionErrors.put(client.origin, safeError(exception));
            } finally {
              backgroundPolling.remove(client.origin);
            }
          });
    }
  }

  private int dp(int value) {
    return Math.round(value * getResources().getDisplayMetrics().density);
  }

  private int scaledDp(int value) {
    return dp(Math.round(value * uiScale));
  }

  private LinearLayout column() {
    LinearLayout result = new LinearLayout(this);
    result.setOrientation(LinearLayout.VERTICAL);
    return result;
  }

  private LinearLayout row() {
    LinearLayout result = new LinearLayout(this);
    result.setOrientation(LinearLayout.HORIZONTAL);
    result.setGravity(Gravity.CENTER_VERTICAL);
    return result;
  }

  private TextView label(String text, int size) {
    TextView view = new TextView(this);
    view.setText(text);
    view.setTextColor(TEXT);
    view.setTextSize(TypedValue.COMPLEX_UNIT_SP, size * uiScale);
    view.setPadding(scaledDp(10), scaledDp(8), scaledDp(10), scaledDp(8));
    return view;
  }

  private EditText input(String hint) {
    EditText field = new EditText(this);
    field.setHint(hint);
    field.setTextColor(TEXT);
    field.setHintTextColor(Color.rgb(155, 173, 158));
    field.setTextSize(TypedValue.COMPLEX_UNIT_SP, 16 * uiScale);
    field.setPadding(scaledDp(12), scaledDp(10), scaledDp(12), scaledDp(10));
    return field;
  }

  private Button button(String text, View.OnClickListener click) {
    Button button = new Button(this);
    button.setText(text);
    button.setAllCaps(false);
    button.setTextColor(ACCENT);
    button.setTextSize(TypedValue.COMPLEX_UNIT_SP, 14 * uiScale);
    button.setOnClickListener(click);
    button.setMinWidth(scaledDp(48));
    button.setMinimumWidth(scaledDp(48));
    button.setMinHeight(scaledDp(48));
    button.setMinimumHeight(scaledDp(48));
    button.setPadding(scaledDp(12), 0, scaledDp(12), 0);
    button.setBackgroundTintList(android.content.res.ColorStateList.valueOf(CARD));
    return button;
  }

  private ImageButton iconButton(int drawable, String description, View.OnClickListener click) {
    ImageButton button = new ImageButton(this);
    button.setImageResource(drawable);
    button.setContentDescription(description);
    button.setOnClickListener(click);
    button.setMinimumWidth(scaledDp(44));
    button.setMinimumHeight(scaledDp(44));
    button.setPadding(scaledDp(10), scaledDp(10), scaledDp(10), scaledDp(10));
    button.setScaleType(ImageButton.ScaleType.CENTER_INSIDE);
    button.setBackgroundTintList(android.content.res.ColorStateList.valueOf(Color.TRANSPARENT));
    return button;
  }

  private LinearLayout.LayoutParams match() {
    return new LinearLayout.LayoutParams(-1, -2);
  }

  private void base() {
    root = column();
    root.setBackgroundColor(BG);
    root.setPadding(dp(10), dp(8), dp(10), dp(8));
    setContentView(root);
  }

  private void problem(String message) {
    setMessage(message, Color.rgb(245, 184, 165), false);
  }

  private void notice(String message) {
    setMessage(message, ACCENT, true);
  }

  private void setMessage(String message, int color, boolean transientWarning) {
    ui.removeCallbacks(dismissTransientMessage);
    transientMessage = transientWarning && message != null && !message.isEmpty();
    if (error == null) return;
    error.setTextColor(color);
    error.setText(message == null ? "" : message);
    error.setVisibility(message == null || message.isEmpty() ? View.GONE : View.VISIBLE);
    if (transientMessage) ui.postDelayed(dismissTransientMessage, 7000);
  }

  private void clearMessage() {
    ui.removeCallbacks(dismissTransientMessage);
    transientMessage = false;
    if (error != null) {
      error.setText("");
      error.setVisibility(View.GONE);
    }
  }

  private TextView errorLabel() {
    TextView view = label("", 14);
    view.setTextColor(Color.rgb(245, 184, 165));
    view.setVisibility(View.GONE);
    view.setOnClickListener(v -> clearMessage());
    return view;
  }

  private void watch(EditText field, java.util.function.Consumer<String> changed) {
    field.addTextChangedListener(
        new TextWatcher() {
          public void beforeTextChanged(CharSequence s, int st, int c, int a) {}

          public void onTextChanged(CharSequence s, int st, int before, int count) {
            changed.accept(s.toString());
          }

          public void afterTextChanged(Editable e) {}
        });
  }

  private EditText pairingInput;

  private void showPairing() {
    settingsPage = api != null;
    generation++;
    base();
    root.addView(label("Connect to your desktop", 26));
    root.addView(
        label(
            "Scan its pairing QR or paste the full pairing string. It expires after ten minutes and"
                + " works once.",
            15));
    if (api != null) root.addView(button("Back to settings", v -> showSettings()), match());
    root.addView(button("Saved servers", v -> showServers()), match());
    pairingInput = input("Paste pairing string");
    pairingInput.setContentDescription("Pairing string");
    pairingInput.setInputType(
        InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_VISIBLE_PASSWORD);
    pairingInput.setMaxLines(4);
    root.addView(
        button(
            "Scan pairing QR",
            v ->
                startActivityForResult(new android.content.Intent(this, QrScanActivity.class), 41)),
        match());
    root.addView(pairingInput, match());
    EditText serverLabel = input("Server name (e.g. Work or Personal)");
    serverLabel.setContentDescription("Server name");
    root.addView(serverLabel, match());
    EditText name = input("Device name");
    name.setContentDescription("Device name");
    name.setText(android.os.Build.MODEL);
    root.addView(name, match());
    error = errorLabel();
    root.addView(error);
    Button pair = button("Connect", null);
    root.addView(pair, match());
    pair.setOnClickListener(
        v -> {
          final PairingTicket ticket;
          try {
            ticket = PairingTicket.parse(pairingInput.getText().toString());
            if (name.getText().toString().trim().isEmpty())
              throw new IllegalArgumentException("Enter a device name.");
          } catch (Exception exception) {
            problem(safeError(exception));
            return;
          }
          String device = name.getText().toString().trim();
          String label =
              serverLabel.getText().toString().trim().isEmpty()
                  ? ticket.relay == null ? ticket.origin : "Relay desktop"
                  : serverLabel.getText().toString().trim();
          int epoch = generation;
          pair.setEnabled(false);
          notice("Verifying desktop identity and pairing…");
          network.execute(
              () -> {
                ApiClient candidate = ticket.client("");
                try {
                  JSONObject result =
                      (JSONObject)
                          candidate.request(
                              "/devices/pair",
                              new JSONObject()
                                  .put("deviceName", device)
                                  .put("code", ticket.ticket));
                  String token = result.getString("token");
                  store.save(ticket.origin, token, label, ticket.pin, ticket.relay);
                  ui.post(
                      () -> {
                        if (epoch != generation) return;
                        pairingInput.setText("");
                         api = ticket.client(token);
                         ApiClient old = connections.put(ticket.origin, api);
                         if (old != null) old.close();
                         connectionNames.put(ticket.origin, label);
                         connectionStates.put(ticket.origin, false);
                         connected = false;
                        serverName = label;
                        selected = store.selected(api.origin);
                        conversation = null;
                        showChat();
                        ui.removeCallbacks(poll);
                        ui.post(poll);
                      });
                } catch (Exception exception) {
                  ui.post(
                      () -> {
                        if (epoch == generation) {
                          pair.setEnabled(true);
                          problem(safeError(exception));
                        }
                      });
                } finally { candidate.close(); }
              });
        });
  }

  @Override
  protected void onActivityResult(int request, int result, android.content.Intent data) {
    super.onActivityResult(request, result, data);
    if (updater.activityResult(request, result)) return;
    if (request == 41 && result == RESULT_OK && data != null && pairingInput != null) {
      String payload = data.getStringExtra("pairing");
      try {
        PairingTicket.parse(payload == null ? "" : payload);
        pairingInput.setText(payload);
        notice("Desktop QR read. Tap Connect to pair.");
      } catch (Exception exception) {
        problem(safeError(exception));
      }
    }
  }

  private void showChat() {
    settingsPage = false;
    generation++;
    tabSignature = "";
    transcriptSignature = "";
    connected = false;
    polling = false;
    submitting = false;
    usageReadAt = 0;
    usageState = null;
    scheduledState = new JSONArray();
    screen =
        new ConversationScreenView(
            this,
            uiScale,
            serverName,
            new ConversationScreenView.Actions() {
              public void selectServer() {
                showServers();
              }

              public void openSettings() {
                showSettings();
              }

              public void newConversation() {
                createConversation();
              }

              public void showAllConversations() {
                MainActivity.this.showAllConversations();
              }

              public void renameConversation() {
                renameSelectedConversation();
              }

              public void togglePin() {
                togglePinnedConversation();
              }

              public void selectModel() {
                showModels();
              }

              public void archiveConversation() {
                archive(selected);
              }

              public void resetConversation() {
                resetSelectedConversation();
              }

              public void showArchived() {
                MainActivity.this.showArchived();
              }

              public void stopWork() {
                interrupt();
              }

              public void scheduleMessage() {
                MainActivity.this.scheduleMessage();
              }

              public void loadOlderHistory() {
                MainActivity.this.loadOlderHistory();
              }
            });
    root = screen.root;
    tabs = screen.tabs;
    transcript = screen.transcript;
    metadata = screen.metadata;
    contextSurface = screen.contextSurface;
    controlPanel = screen.controlPanel;
    brand = screen.brand;
    serverSelector = screen.serverSelector;
    sessionLocation = screen.sessionLocation;
    usageView = screen.usageView;
    context = screen.context;
    error = screen.error;
    composer = screen.composer;
    send = screen.send;
    stop = screen.stop;
    schedule = screen.schedule;
    renameAction = screen.renameAction;
    pinAction = screen.pinAction;
    modelAction = screen.modelAction;
    archiveAction = screen.archiveAction;
    resetAction = screen.resetAction;
    archivedAction = screen.archivedAction;
    scroll = screen.transcriptScroll;
    setContentView(root);
    usageView.setOnClickListener(v -> showUsage());
    send.setOnClickListener(v -> submit());
    setConversationActionsVisible(false);
    watch(
        composer,
        text -> {
          if (!loadingDraft && !selected.isEmpty()) store.draft(api.origin, selected, text);
          updateSend();
        });
    loadDraft();
    ((ComposerEditText) composer).receiveImages(this::receiveComposerImage);
    updateScheduleButton();
    updateSend();
    if (resumed) {
      ui.removeCallbacks(poll);
      ui.post(poll);
      ui.removeCallbacks(elapsedTicker);
      ui.post(elapsedTicker);
    }
  }

  private void loadDraft() {
    loadingDraft = true;
    composer.setText(selected.isEmpty() ? "" : store.draft(api.origin, selected));
    composer.setSelection(composer.length());
    loadingDraft = false;
    renderComposerImage();
  }

  private DraftKey draftKey() {
    return api == null || selected.isEmpty() ? null : new DraftKey(api.origin, selected);
  }

  private void renderComposerImage() {
    if (screen == null) return;
    DraftKey key = draftKey();
    ComposerImage image = draftImages.get(key);
    screen.showAttachment(image == null ? null : image.preview(), view -> {
      draftImages.remove(key);
      renderComposerImage();
      updateSend();
      composer.requestFocus();
    });
  }

  private void receiveComposerImage(Uri uri, Runnable release) {
    DraftKey key = draftKey();
    if (key == null || conversation == null || conversation.optBoolean("archived")) {
      release.run();
      return;
    }
    if (!conversation.optBoolean("supportsImageAttachments")) {
      release.run();
      notice("Update PocketAgent on the desktop to send images.");
      return;
    }
    if (draftImages.containsKey(key) || !receivingImages.add(key)) {
      release.run();
      notice("Remove the current image before pasting another.");
      return;
    }
    updateSend();
    network.execute(() -> {
      ComposerImage image = null;
      try {
        image = ComposerImage.read(getContentResolver(), uri);
      } catch (java.io.IOException | RuntimeException exception) {
        // The source app can revoke clipboard access, or the image may be invalid.
      } finally {
        release.run();
      }
      ComposerImage attachment = image;
      ui.post(() -> {
        receivingImages.remove(key);
        if (isDestroyed()) return;
        if (attachment != null) draftImages.put(key, attachment);
        if (!settingsPage && key.equals(draftKey())) {
          if (attachment == null) problem("The clipboard image could not be attached.");
          renderComposerImage();
          updateSend();
        }
      });
    });
  }

  private void select(String id) {
    selected = id;
    store.select(api.origin, id);
    conversation = null;
    scheduledState = new JSONArray();
    historyBefore = "";
    historyCursors.remove(id);
    olderHistory.remove(id);
    transcriptSignature = "";
    answers.clear();
    setConversationActionsVisible(false);
    loadDraft();
    transcript.removeAllViews();
    context.setText(R.string.loading);
    updateSend();
    refresh();
  }

  private void refresh() {
    if (api == null || polling || !resumed) return;
    polling = true;
    ApiClient client = api;
    String id = selected;
    int epoch = generation;
    network.execute(
        () -> {
          try {
            JSONArray sessions = (JSONArray) client.request("/conversations", null);
            JSONObject state = null;
            ApiClient.ApiError stateError = null;
            if (!id.isEmpty()) {
              try {
                state = (JSONObject) client.request("/conversations/" + ApiClient.segment(id), null);
              } catch (ApiClient.ApiError error) {
                if (error.status == 401) throw error;
                stateError = error;
              }
            }
            JSONArray jobs;
            try {
              jobs =
                  id.isEmpty()
                      ? new JSONArray()
                      : (JSONArray)
                          client.request(
                              "/conversations/" + ApiClient.segment(id) + "/scheduled", null);
            } catch (ApiClient.ApiError ignored) {
              jobs = new JSONArray();
            }
            JSONObject limits = null;
            if (System.currentTimeMillis() - usageReadAt > 15000) {
              try {
                limits = (JSONObject) client.request("/usage", null);
              } catch (ApiClient.ApiError ignored) {
                limits = new JSONObject();
              }
            }
            JSONArray pendingJobs = jobs;
            JSONObject usage = limits;
            final JSONObject selectedState = state;
            final ApiClient.ApiError selectedStateError = stateError;
            ui.post(
                () -> {
                  if (epoch != generation || api != client) return;
                  polling = false;
                  connected = true;
                  connectionStates.put(client.origin, true);
                  connectionErrors.remove(client.origin);
                  renderTabs(sessions);
                   boolean selectedVisible = id.isEmpty();
                   for (int index = 0; index < sessions.length(); index++) {
                     if (id.equals(sessions.optJSONObject(index).optString("id"))) {
                       selectedVisible = true;
                       break;
                     }
                   }
                   if (!id.isEmpty() && !selectedVisible) {
                     selected = "";
                     store.select(client.origin, "");
                     conversation = null;
                     scheduledState = new JSONArray();
                     setConversationActionsVisible(false);
                     transcript.removeAllViews();
                     context.setText(sessions.length() == 0 ? "No conversations shared with this device." : "Select a conversation.");
                     loadDraft();
                   }
                  serverSelector.setText(serverName);
                  serverSelector.setContentDescription("Server " + serverName + " connected");
                  if (id.equals(selected)) {
                    scheduledState = pendingJobs;
                    updateScheduleButton();
                    if (selectedStateError != null) {
                      conversation = null;
                      setConversationActionsVisible(false);
                      context.setText(selectedStateError.getMessage());
                      problem(selectedStateError.getMessage());
                    }
                  }
                  if (usage != null) {
                    usageState = usage;
                    usageReadAt = System.currentTimeMillis();
                    renderUsage();
                  }
                  if (connectionProblem) {
                    problem("");
                    connectionProblem = false;
                  }
                  if (selected.isEmpty() && sessions.length() > 0) {
                    select(sessions.optJSONObject(0).optString("id"));
                    return;
                  }
                  if (id.equals(selected) && selectedState != null) renderConversation(selectedState);
                  updateSend();
                });
          } catch (Exception ex) {
            ui.post(
                () -> {
                  if (epoch != generation) return;
                  polling = false;
                  connected = false;
                  connectionStates.put(client.origin, false);
                  connectionErrors.put(client.origin, safeError(ex));
                  connectionProblem = true;
                  serverSelector.setText(serverName);
                  serverSelector.setContentDescription("Server " + serverName + " reconnecting");
                  if (ex instanceof ApiClient.ApiError && ((ApiClient.ApiError) ex).status == 401) {
                    try {
                      store.remove(client.origin);
                    } catch (Exception ignored) {
                    }
                    connections.remove(client.origin);
                    client.close();
                    api = null;
                    showPairing();
                    problem(safeError(ex));
                  } else problem(safeError(ex));
                  updateSend();
                });
          }
        });
  }

  private void renderTabs(JSONArray sessions) {
    boolean found = selected.isEmpty();
    for (int i = 0; i < sessions.length(); i++)
      if (selected.equals(sessions.optJSONObject(i).optString("id"))) found = true;
    if (!found) select("");
    String signature = sessions.toString() + selected;
    if (signature.equals(tabSignature)) return;
    tabSignature = signature;
    orderedSessions.clear();
    for (int i = 0; i < sessions.length(); i++) {
      JSONObject entry = sessions.optJSONObject(i);
      if (entry != null) orderedSessions.add(entry);
    }
    orderedSessions.sort(
        (left, right) -> {
          int pinned = Boolean.compare(right.optBoolean("pinned", false), left.optBoolean("pinned", false));
          if (pinned != 0) return pinned;
          int recent = Long.compare(messageTimestamp(right), messageTimestamp(left));
          if (recent != 0) return recent;
          return left.optString("title").compareToIgnoreCase(right.optString("title"));
        });
    screen.clearTabs();
    for (JSONObject entry : orderedSessions) {
      String id = entry.optString("id");
      boolean active = id.equals(selected);
      screen.addTab(entry.optString("title"), active, v -> select(id));
    }
    screen.updateTabOverflow();
    if (selected.isEmpty()) {
      setConversationActionsVisible(false);
    }
    if (sessions.length() == 0 && selected.isEmpty()) {
      transcript.removeAllViews();
      transcript.addView(label("Tap + to start a conversation.", 18));
    }
  }

  private long messageTimestamp(JSONObject entry) {
    String value = entry.optString("lastMessageAt", "");
    try {
      return java.time.Instant.parse(value).toEpochMilli();
    } catch (Exception ignored) {
      return Long.MIN_VALUE;
    }
  }

  private void showAllConversations() {
    if (orderedSessions.isEmpty()) {
      notice("No active conversations.");
      return;
    }
    String[] choices = new String[orderedSessions.size()];
    for (int i = 0; i < orderedSessions.size(); i++) {
      JSONObject entry = orderedSessions.get(i);
      choices[i] = (entry.optBoolean("pinned", false) ? "Pinned  " : "") + entry.optString("title");
    }
    new AlertDialog.Builder(this)
        .setTitle("All conversations")
        .setItems(
            choices,
            (dialog, which) -> {
              if (which >= 0 && which < orderedSessions.size())
                select(orderedSessions.get(which).optString("id"));
            })
        .setNegativeButton("Close", null)
        .show();
  }

  private void togglePinnedConversation() {
    if (api == null || conversation == null || selected.isEmpty()) return;
    ApiClient client = api;
    String id = selected;
    boolean pinned = !conversation.optBoolean("pinned", false);
    action(
        () ->
            client.request(
                "/conversations/" + ApiClient.segment(id) + "/pin",
                new JSONObject().put("pinned", pinned)),
        state -> {
          tabSignature = "";
          if (selected.equals(id)) renderConversation((JSONObject) state);
          refresh();
          notice(pinned ? "Conversation pinned." : "Conversation unpinned.");
        },
        false);
  }

  private void renderConversation(JSONObject state) {
    state = mergeOlderHistory(state);
    state = mergeOptimistic(state);
    conversation = state;
    String active = state.isNull("activeTurnId") ? "" : state.optString("activeTurnId");
    String status = state.optString("status");
    controlPanel.setBackgroundColor(ConversationScreenView.BACKGROUND);
    if (!state.isNull("activeTurnStartedAt")) {
      try {
        status +=
            " - "
                + elapsed(
                    java.time.Duration.between(
                            java.time.Instant.parse(state.optString("activeTurnStartedAt")),
                            java.time.Instant.now())
                        .toMillis());
      } catch (Exception ignored) {
      }
    } else if (!state.isNull("lastWorkDurationMs"))
      status += " - worked " + elapsed(state.optLong("lastWorkDurationMs"));
    String workingDirectory = state.optString("workingDirectory", "");
    renderModelStatus(state, status);
    sessionLocation.setMaxLines(1);
    sessionLocation.setEllipsize(android.text.TextUtils.TruncateAt.MIDDLE);
    sessionLocation.setText(workingDirectory);
    setConversationActionsVisible(true);
    boolean pinned = state.optBoolean("pinned", false);
    pinAction.setContentDescription(pinned ? "Unpin conversation" : "Pin conversation");
    stop.setVisibility(active.isEmpty() ? View.GONE : View.VISIBLE);
    send.setText(R.string.send_label);
    JSONArray items = state.optJSONArray("items"), requests = state.optJSONArray("requests");
    String signature = items.toString() + requests.toString() + state.optString("error");
    if (!signature.equals(transcriptSignature)) {
      if (patchRenderedRows(items, requests, signature)) return;
      transcriptSignature = signature;
      boolean bottom =
          scroll.getChildCount() == 0
              || scroll.getScrollY() + scroll.getHeight() >= transcript.getHeight() - dp(80);
      int previous = scroll.getScrollY();
      transcript.removeAllViews();
      renderedRows.clear();
      String lastMinute = "";
      for (int i = 0; i < items.length(); i++) {
        JSONObject item = items.optJSONObject(i);
        String type = item.optString("type");
        TextView text;
        if ("userMessage".equals(type) || "agentMessage".equals(type)) {
          try {
            java.time.ZonedDateTime time =
                java.time.Instant.parse(item.optString("timestamp"))
                    .atZone(java.time.ZoneId.systemDefault());
            String minute = time.toLocalDate() + " " + time.getHour() + ":" + time.getMinute();
            if (!minute.equals(lastMinute)) {
              transcript.addView(
                  label(
                      time.format(
                          java.time.format.DateTimeFormatter.ofLocalizedTime(
                              java.time.format.FormatStyle.SHORT)),
                      11));
              lastMinute = minute;
            }
          } catch (Exception ignored) {
          }
          text = label(item.optString("text"), 16);
          text.setTextIsSelectable(true);
        } else {
          String summary = type + " - " + item.optString("status");
          boolean expanded = expandedItems.contains(item.optString("id"));
          text = label(expanded ? summary + "\n" + item.optString("detail") : summary, 13);
          text.setOnClickListener(
              v -> {
                String itemId = item.optString("id");
                if (expandedItems.add(itemId)) text.setText(String.format(java.util.Locale.ROOT, "%s\n%s", summary, item.optString("detail")));
                else {
                  expandedItems.remove(itemId);
                  text.setText(summary);
                }
              });
        }
        boolean user = "userMessage".equals(type);
        if (!"agentMessage".equals(type)) {
          GradientDrawable background = new GradientDrawable();
          background.setColor(user ? Color.rgb(41, 59, 44) : CARD);
          background.setCornerRadius(dp(8));
          text.setBackground(background);
        }
        LinearLayout.LayoutParams margin = user ? new LinearLayout.LayoutParams(-2, -2) : match();
        if (user) {
          margin.gravity = Gravity.END;
          text.setMaxWidth(getResources().getDisplayMetrics().widthPixels - dp(48));
        }
        margin.setMargins(0, dp(6), 0, dp(6));
        JSONArray images = item.optJSONArray("images");
        if (!user || images == null || images.length() == 0 || !item.optString("text").trim().isEmpty()) {
          transcript.addView(text, margin);
          renderedRows.put(item.optString("id"), text);
        }
        if (images != null) {
          for (int imageIndex = 0; imageIndex < images.length(); imageIndex++) {
            JSONObject image = images.optJSONObject(imageIndex);
            if (image == null) continue;
            String source = image.optString("source");
            String alt = image.optString("altText");
            if (alt.isEmpty()) alt = source.substring(Math.max(0, source.lastIndexOf('/') + 1));
            final String imageAlt = alt;
            String imageKey = item.optString("id") + ":" + source;
            String imageLabel = viewedImages.contains(imageKey) ? getString(R.string.viewed_image, imageAlt) : getString(R.string.view_image, imageAlt);
            Button imageButton = button(imageLabel, null);
            imageButton.setContentDescription(imageLabel);
            String conversationId = state.optString("id");
            imageButton.setOnClickListener(v -> viewImage(conversationId, image, imageKey, imageButton, imageAlt));
            transcript.addView(imageButton, match());
          }
        }
      }
      for (int i = 0; i < requests.length(); i++)
        renderRequest(state.optString("id"), requests.optJSONObject(i));
      if (items.length() == 0) transcript.addView(label("Send a message to start working.", 18));
      scroll.post(
          () -> {
            // fullScroll also chooses a focusable transcript child, stealing the composer's
            // cursor/IME.
            if (bottom) scroll.scrollTo(0, transcript.getHeight());
            else scroll.scrollTo(0, previous);
          });
    }
    boolean waitingForUsage = false;
    for (int i = 0; i < scheduledState.length(); i++) {
      JSONObject job = scheduledState.optJSONObject(i);
      if ("pending".equals(job.optString("status"))
          && "usage-reset".equals(job.optString("reason"))) waitingForUsage = true;
    }
    if (waitingForUsage)
      notice(
          "Usage limit reached. Your message is queued until usage becomes available; see Scheduled"
              + " messages.");
    else if (!state.isNull("error") && !state.optString("error").isEmpty())
      problem(state.optString("error"));
    updateSend();
  }

  private boolean patchRenderedRows(JSONArray items, JSONArray requests, String signature) {
    if (transcriptSignature.isEmpty() || requests.length() != 0 || renderedRows.size() != items.length()) return false;
    for (int i = 0; i < items.length(); i++) {
      JSONObject item = items.optJSONObject(i);
      if (item == null || !renderedRows.containsKey(item.optString("id"))) return false;
      if (item.optJSONArray("images") != null && item.optJSONArray("images").length() > 0) return false;
    }
    for (int i = 0; i < items.length(); i++) {
      JSONObject item = items.optJSONObject(i);
      String type = item.optString("type");
      TextView row = renderedRows.get(item.optString("id"));
      if ("userMessage".equals(type) || "agentMessage".equals(type)) row.setText(item.optString("text"));
      else {
        String summary = type + " - " + item.optString("status");
        row.setText(expandedItems.contains(item.optString("id")) ? summary + "\n" + item.optString("detail") : summary);
      }
    }
    transcriptSignature = signature;
    return true;
  }

  private void updateElapsedStatus() {
    if (conversation == null || context == null) return;
    String status = conversation.optString("status");
    if (!conversation.isNull("activeTurnStartedAt")) {
      try {
        status += " - " + elapsed(java.time.Duration.between(java.time.Instant.parse(conversation.optString("activeTurnStartedAt")), java.time.Instant.now()).toMillis());
      } catch (Exception ignored) {
      }
    } else if (!conversation.isNull("lastWorkDurationMs")) status += " - worked " + elapsed(conversation.optLong("lastWorkDurationMs"));
    renderModelStatus(conversation, status);
  }

  private void renderModelStatus(JSONObject state, String status) {
    context.setText(getString(R.string.session_context_v2, status,
        reasoningValue(state, "model", "Unavailable"),
        reasoningLabel(reasoningValue(state, "reasoningEffort", ""))));
  }

  private static String reasoningValue(JSONObject data, String field, String fallback) {
    return data.isNull(field) ? fallback : data.optString(field, fallback);
  }

  private static String reasoningLabel(String value) {
    if ("xhigh".equals(value)) return "Extra high";
    return value.isEmpty() ? "Unavailable"
        : value.substring(0, 1).toUpperCase(java.util.Locale.ROOT) + value.substring(1);
  }

  private JSONObject mergeOlderHistory(JSONObject state) {
    String id = state.optString("id");
    if (!historyCursors.containsKey(id)) historyCursors.put(id, state.optString("historyBefore"));
    historyBefore = historyCursors.getOrDefault(id, "");
    java.util.ArrayList<JSONObject> older = olderHistory.get(id);
    if (older == null || older.isEmpty()) return state;
    try {
      JSONArray current = state.optJSONArray("items"), merged = new JSONArray();
      java.util.HashSet<String> known = new java.util.HashSet<>();
      for (JSONObject item : older) {
        if (item != null && known.add(item.optString("id"))) merged.put(item);
      }
      for (int i = 0; i < current.length(); i++) {
        JSONObject item = current.optJSONObject(i);
        if (item != null && known.add(item.optString("id"))) merged.put(item);
      }
      return new JSONObject(state.toString()).put("items", merged).put("historyBefore", historyBefore).put("historyTruncated", !historyBefore.isEmpty());
    } catch (Exception ignored) {
      return state;
    }
  }

  private void loadOlderHistory() {
    if (historyLoading || api == null || selected.isEmpty() || historyBefore.isEmpty()) return;
    historyLoading = true;
    ApiClient client = api;
    String id = selected, cursor = historyBefore;
    int previousHeight = transcript.getHeight(), previousOffset = scroll.getScrollY(), epoch = generation;
    network.execute(
        () -> {
          try {
            JSONObject page = (JSONObject) client.request("/conversations/" + ApiClient.segment(id) + "/history?before=" + ApiClient.segment(cursor) + "&limit=50", null);
            JSONArray pageItems = page.optJSONArray("items");
            java.util.ArrayList<JSONObject> older = olderHistory.computeIfAbsent(id, ignored -> new java.util.ArrayList<>());
            java.util.HashSet<String> known = new java.util.HashSet<>();
            for (JSONObject item : older) if (item != null) known.add(item.optString("id"));
            java.util.ArrayList<JSONObject> prepend = new java.util.ArrayList<>();
            for (int i = 0; i < pageItems.length(); i++) {
              JSONObject item = pageItems.optJSONObject(i);
              if (item != null && known.add(item.optString("id"))) prepend.add(item);
            }
            older.addAll(0, prepend);
            String next = page.optString("before");
            historyCursors.put(id, next);
            ui.post(
                () -> {
                  if (epoch == generation && conversation != null && id.equals(selected)) {
                    transcriptSignature = "";
                    renderConversation(conversation);
                    scroll.post(() -> scroll.scrollTo(0, Math.max(0, previousOffset + transcript.getHeight() - previousHeight)));
                  }
                  historyLoading = false;
                });
          } catch (Exception exception) {
            ui.post(() -> { historyLoading = false; problem(safeError(exception)); });
          }
        });
  }

  private JSONObject mergeOptimistic(JSONObject state) {
    String id = state.optString("id");
    java.util.ArrayList<JSONObject> pending = optimisticItems.get(id);
    if (pending == null || pending.isEmpty()) return state;
    try {
      JSONArray canonical = state.optJSONArray("items");
      JSONArray merged = new JSONArray();
      for (int i = 0; i < canonical.length(); i++) merged.put(canonical.optJSONObject(i));
      java.util.HashSet<Integer> matched = new java.util.HashSet<>();
      java.util.Iterator<JSONObject> iterator = pending.iterator();
      while (iterator.hasNext()) {
        JSONObject item = iterator.next();
        boolean found = false;
        for (int i = 0; i < canonical.length(); i++) {
          if (matched.contains(i)) continue;
          JSONObject existing = canonical.optJSONObject(i);
          if (existing != null
              && ((item.optString("clientMessageId").length() > 0
                      && item.optString("clientMessageId").equals(existing.optString("clientMessageId")))
                  || ("userMessage".equals(existing.optString("type"))
                      && item.optString("text").equals(existing.optString("text"))
                      && withinMinutes(item.optString("timestamp"), existing.optString("timestamp"), 10)))) {
            found = true;
            matched.add(i);
            break;
          }
        }
        if (found) iterator.remove();
        else merged.put(item);
      }
      if (pending.isEmpty()) optimisticItems.remove(id);
      return new JSONObject(state.toString()).put("items", merged);
    } catch (Exception ignored) {
      return state;
    }
  }

  private static boolean withinMinutes(String left, String right, int minutes) {
    try {
      long difference = Math.abs(java.time.Instant.parse(left).toEpochMilli() - java.time.Instant.parse(right).toEpochMilli());
      return difference <= java.time.Duration.ofMinutes(minutes).toMillis();
    } catch (Exception ignored) {
      return false;
    }
  }

  private void updateScheduleButton() {
    if (schedule == null) return;
    int pending = 0;
    for (int i = 0; i < scheduledState.length(); i++) {
      JSONObject job = scheduledState.optJSONObject(i);
      if (job != null && "pending".equals(job.optString("status"))) pending++;
    }
    String symbol = getString(R.string.schedule_symbol);
    schedule.setText(pending == 0 ? symbol : symbol + " " + pending);
    schedule.setContentDescription(
        pending == 0
            ? "Schedule message or view scheduled messages"
            : pending + " scheduled messages; schedule message");
  }

  private void setConversationActionsVisible(boolean visible) {
    if (screen != null) {
      screen.setConversationActionsVisible(visible);
      return;
    }
    int visibility = visible ? View.VISIBLE : View.GONE;
    if (renameAction != null) renameAction.setVisibility(visibility);
    if (pinAction != null) pinAction.setVisibility(visibility);
    if (modelAction != null) modelAction.setVisibility(visibility);
    if (archiveAction != null) archiveAction.setVisibility(visibility);
    if (resetAction != null) resetAction.setVisibility(visibility);
  }

  private void renameSelectedConversation() {
    if (conversation == null || selected.isEmpty()) return;
    rename(selected, conversation.optString("title", "Conversation"));
  }

  private void updateSend() {
    if (send == null) return;
    String status = conversation == null ? "" : conversation.optString("status");
    DraftKey key = draftKey();
    boolean hasImage = draftImages.containsKey(key);
    boolean readingImage = receivingImages.contains(key);
    send.setEnabled(
        connected
            && conversation != null
            && !conversation.optBoolean("archived")
            && !submitting
            && !readingImage
            && (!hasImage || conversation.optBoolean("supportsImageAttachments"))
            && (hasImage || !composer.getText().toString().trim().isEmpty())
            && !"starting".equals(status)
            && !"uncertain".equals(status)
            && !"disconnected".equals(status));
    if (schedule != null)
      schedule.setEnabled(
          connected
              && conversation != null
              && !submitting
              && !hasImage
              && !readingImage
              && !conversation.optBoolean("archived"));
    stop.setEnabled(connected && !submitting);
  }

  private interface Work {
    Object run() throws Exception;
  }

  private interface Done {
    void accept(Object result);
  }

  private void action(Work work, Done done, boolean delivery) {
    action(work, done, delivery, null);
  }

  private void action(Work work, Done done, boolean delivery, Runnable definiteFailure) {
    if (submitting) return;
    submitting = true;
    problem("");
    updateSend();
    int epoch = generation;
    String id = selected;
    ApiClient client = api;
    if (delivery && !store.uncertain(client.origin, id, true)) {
      submitting = false;
      problem("Could not save delivery state. Your message was not sent.");
      updateSend();
      return;
    }
    network.execute(
        () -> {
          try {
            Object result = work.run();
            ui.post(
                () -> {
                  if (epoch != generation || api != client) return;
                  submitting = false;
                  if (delivery) store.uncertain(client.origin, id, false);
                  try {
                    done.accept(result);
                  } catch (RuntimeException exception) {
                    // A remote client must never be able to crash the app with a
                    // malformed or incomplete response. Keep the connection alive
                    // and surface a recoverable error instead.
                    problem("The desktop returned an invalid response. Refresh and try again.");
                  }
                  updateSend();
                               refresh();
                });
          } catch (Exception ex) {
            boolean rejected = delivery
                && ex instanceof ApiClient.ApiError
                && ((ApiClient.ApiError) ex).status < 500;
            if (rejected)
              store.uncertain(client.origin, id, false);
            if (delivery
                && (!(ex instanceof ApiClient.ApiError) || ((ApiClient.ApiError) ex).status >= 500))
              store.uncertain(client.origin, id, true);
            ui.post(
                () -> {
                  if (epoch == generation) {
                    submitting = false;
                    if (rejected && definiteFailure != null) definiteFailure.run();
                    problem(
                        safeError(ex)
                            + (store.uncertain(client.origin, id)
                                ? " Delivery is unknown. Review the conversation before sending"
                                    + " again."
                                : ""));
                    updateSend();
                  }
                });
          }
        });
  }

  private void submit() {
    if (conversation == null || api == null || !send.isEnabled()) return;
    if (reviewDelivery()) return;
    String id = selected, text = composer.getText().toString();
    DraftKey key = draftKey();
    ComposerImage image = draftImages.get(key);
    String imageBase64 = image == null ? null : android.util.Base64.encodeToString(image.png(), android.util.Base64.NO_WRAP);
    ApiClient client = api;
    JSONObject state = conversation;
    final String optimisticId = "local-" + java.util.UUID.randomUUID();
    final String clientMessageId = java.util.UUID.randomUUID().toString();
    try {
      JSONObject optimistic =
          new JSONObject()
              .put("id", optimisticId)
              .put("type", "userMessage")
              .put("text", text)
              .put("status", "pending")
              .put("clientMessageId", clientMessageId)
              .put("timestamp", java.time.Instant.now().toString());
      if (imageBase64 != null) optimistic.put("images", new JSONArray().put(new JSONObject()
          .put("source", "data:image/png;base64," + imageBase64).put("altText", "Pasted image")));
      optimisticItems.computeIfAbsent(id, ignored -> new java.util.ArrayList<>()).add(optimistic);
      renderConversation(state);
    } catch (Exception ignored) {
    }
    action(
        () -> {
          JSONObject body = new JSONObject().put("text", text).put("clientMessageId", clientMessageId);
          if (imageBase64 != null) body.put("imagePng", imageBase64);
          if (!state.isNull("activeTurnId"))
            body.put("expectedTurnId", state.getString("activeTurnId"));
          return client.request("/conversations/" + ApiClient.segment(id) + "/messages", body);
        },
        receipt -> {
          boolean accepted = true;
          if (receipt instanceof JSONObject) {
            JSONObject response = (JSONObject) receipt;
            String deliveryStatus = response.optString("deliveryStatus", "accepted");
            accepted = "accepted".equals(deliveryStatus) || "queued".equals(deliveryStatus);
            if (response.optBoolean("queued")) notice(response.optString("message", "Queued until usage resets."));
          }
          store.uncertain(client.origin, id, !accepted);
          if (!accepted) notice("This message's delivery is uncertain. Review the conversation before retrying.");
          if (accepted) {
            if (store.draft(client.origin, id).equals(text)) store.draft(client.origin, id, "");
            if (selected.equals(id) && composer.getText().toString().equals(text)) composer.setText("");
            if (image != null) draftImages.remove(key, image);
            renderComposerImage();
          }
        },
        true,
        () -> removeOptimistic(id, optimisticId));
  }

  private void removeOptimistic(String conversationId, String itemId) {
    java.util.ArrayList<JSONObject> pending = optimisticItems.get(conversationId);
    if (pending != null) {
      pending.removeIf(item -> itemId.equals(item.optString("id")));
      if (pending.isEmpty()) optimisticItems.remove(conversationId);
    }
    if (conversation != null && conversationId.equals(conversation.optString("id"))) {
      try {
        JSONArray items = conversation.optJSONArray("items");
        JSONArray remaining = new JSONArray();
        for (int i = 0; i < items.length(); i++) {
          JSONObject item = items.optJSONObject(i);
          if (item != null && !itemId.equals(item.optString("id"))) remaining.put(item);
        }
        conversation = new JSONObject(conversation.toString()).put("items", remaining);
        transcriptSignature = "";
        renderConversation(conversation);
      } catch (Exception ignored) {
      }
    }
  }

  private boolean reviewDelivery() {
    if (!store.uncertain(api.origin, selected)) return false;
    String origin = api.origin, id = selected;
    new AlertDialog.Builder(this)
        .setTitle("Review delivery first")
        .setMessage(
            "An earlier submission has an unknown outcome. Check the transcript and scheduled queue"
                + " before resending. Nothing will be sent by this dialog.")
        .setNegativeButton("Keep reviewing", null)
        .setPositiveButton("I reviewed it", (dialog, which) -> store.uncertain(origin, id, false))
        .show();
    return true;
  }

  private void createConversation() {
    if (api == null || !connected) {
      problem("Connect to the desktop first.");
      return;
    }
    ApiClient client = api;
    action(
        () ->
            new JSONObject()
                .put("folders", client.request("/directories", null))
                .put("models", client.request("/models", null)),
        result -> newConversationDialog(result instanceof JSONObject ? (JSONObject) result : null),
        false);
  }

  private void newConversationDialog(JSONObject options) {
    if (options == null) {
      problem("The desktop returned no folder or model data. Refresh and try again.");
      return;
    }
    JSONObject folders = options.optJSONObject("folders");
    JSONArray models = options.optJSONArray("models");
    if (folders == null || models == null) {
      problem("The desktop returned an invalid folder/model response. Refresh and try again.");
      return;
    }
    ApiClient client = api;
    LinearLayout panel = column();
    panel.setPadding(dp(16), 0, dp(16), 0);
    panel.addView(label("Working folder on the desktop", 15));
    EditText folder = input("Absolute desktop folder path");
    folder.setContentDescription("Conversation working folder");
    folder.setText(
        conversation == null
            ? folders.optString("path")
            : conversation.optString("workingDirectory"));
    panel.addView(folder, match());
    panel.addView(
        button("Browse folders", v -> browseFolders(folder.getText().toString(), folder::setText)),
        match());
    String[] chosen = {""};
    Button model = button("Use Codex settings", null);
    model.setContentDescription("New conversation model");
    model.setOnClickListener(
        v ->
            modelChoices(
                models,
                (slug, name) -> {
                  chosen[0] = slug;
                  model.setText(name);
                }));
    panel.addView(model, match());
    new AlertDialog.Builder(this)
        .setTitle("New conversation")
        .setView(panel)
        .setNegativeButton("Cancel", null)
        .setPositiveButton(
            "Create conversation",
            (dialog, which) ->
                action(
                    () -> {
                      JSONObject body =
                          new JSONObject().put("workingDirectory", folder.getText().toString());
                      if (!chosen[0].isEmpty()) body.put("model", chosen[0]);
                      return client.request("/conversations", body);
                    },
                    result -> select(((JSONObject) result).optString("id")),
                    false))
        .show();
  }

  private void browseFolders(String path, java.util.function.Consumer<String> chosen) {
    ApiClient client = api;
    action(
        () -> client.request("/directories?path=" + ApiClient.segment(path), null),
        result -> {
          if (!(result instanceof JSONObject)) {
            problem("The desktop returned an invalid folder response. Refresh and try again.");
            return;
          }
          JSONObject listing = (JSONObject) result;
          JSONArray roots = listing.optJSONArray("roots"),
              directories = listing.optJSONArray("directories");
          if (roots == null || directories == null || listing.optString("path").isEmpty()) {
            problem("The desktop returned an incomplete folder response. Refresh and try again.");
            return;
          }
          java.util.ArrayList<String> labels = new java.util.ArrayList<>(),
              paths = new java.util.ArrayList<>();
          if (!listing.isNull("parent")) {
            labels.add(".. (parent folder)");
            paths.add(listing.optString("parent"));
          }
          for (int i = 0; i < roots.length(); i++) {
            labels.add("Drive " + roots.optJSONObject(i).optString("name"));
            paths.add(roots.optJSONObject(i).optString("path"));
          }
          for (int i = 0; i < directories.length(); i++) {
            labels.add(directories.optJSONObject(i).optString("name"));
            paths.add(directories.optJSONObject(i).optString("path"));
          }
          new AlertDialog.Builder(this)
              .setTitle(listing.optString("path"))
              .setItems(
                  labels.toArray(new String[0]),
                  (dialog, which) -> browseFolders(paths.get(which), chosen))
              .setPositiveButton(
                  "Use this folder", (dialog, which) -> chosen.accept(listing.optString("path")))
              .setNegativeButton("Cancel", null)
              .show();
          if (listing.optBoolean("hasMore"))
            notice("Showing the first 500 folders. Enter a full path to open another folder.");
        },
        false);
  }

  private interface ModelSelection {
    void accept(String slug, String name);
  }

  private void modelChoices(JSONArray models, ModelSelection selectedModel) {
    if (models == null) {
      problem("The desktop returned no model choices. Refresh and try again.");
      return;
    }
    String[] names = new String[models.length()];
    for (int i = 0; i < models.length(); i++) {
      JSONObject candidate = models.optJSONObject(i);
      if (candidate == null || candidate.optString("model").trim().isEmpty()) {
        problem("The desktop returned an invalid model response. Refresh and try again.");
        return;
      }
      names[i] = candidate.optString("displayName", candidate.optString("model"));
    }
    new AlertDialog.Builder(this)
        .setTitle("Choose model")
        .setItems(
            names,
            (dialog, which) -> {
              JSONObject choice = models.optJSONObject(which);
              if (choice != null) selectedModel.accept(choice.optString("model"), names[which]);
            })
        .setNegativeButton("Cancel", null)
        .show();
  }

  private void showModels() {
    if (api == null || conversation == null) return;
    ApiClient client = api;
    String id = selected;
    if (!conversation.isNull("activeTurnId")) {
      notice("Finish or stop work before changing the model.");
      return;
    }
    action(
        () -> client.request("/models", null),
        result -> {
          if (api != client || !selected.equals(id) || conversation == null) return;
          if (!(result instanceof JSONArray) || ((JSONArray) result).length() == 0) {
            problem("The desktop returned no selectable models.");
            return;
          }
          showModelEditor(client, id, (JSONArray) result, conversation);
        },
        false);
  }

  private void showModelEditor(ApiClient client, String id, JSONArray catalog, JSONObject current) {
    String currentModel = reasoningValue(current, "model", "");
    String currentEffort = reasoningValue(current, "reasoningEffort", "");
    java.util.ArrayList<JSONObject> models = new java.util.ArrayList<>();
    java.util.ArrayList<String> names = new java.util.ArrayList<>();
    int initial = -1;
    for (int i = 0; i < catalog.length(); i++) {
      JSONObject model = catalog.optJSONObject(i);
      if (model == null || model.optString("model", "").trim().isEmpty()) {
        problem("The desktop returned an invalid model response.");
        return;
      }
      String slug = model.optString("model");
      if (slug.equals(currentModel)) initial = i;
      models.add(model);
      names.add(model.optString("displayName", slug));
    }
    if (initial < 0) {
      initial = names.size();
      names.add(currentModel.isEmpty() ? "Unavailable" : currentModel);
      models.add(null);
    }

    LinearLayout panel = column();
    panel.setPadding(dp(20), dp(4), dp(20), dp(8));
    Spinner modelPicker = modelPicker("Model");
    modelPicker.setAdapter(new ModelPickerAdapter(modelPicker, names));
    modelPicker.setSelection(initial);
    Spinner effortPicker = modelPicker("Reasoning");
    TextView modelLabel = label("Model", 14);
    modelLabel.setPadding(0, 0, 0, dp(6));
    panel.addView(modelLabel);
    panel.addView(modelPicker, match());
    TextView effortLabel = label("Reasoning", 14);
    effortLabel.setPadding(0, dp(14), 0, dp(6));
    panel.addView(effortLabel);
    panel.addView(effortPicker, match());

    android.widget.ScrollView scroll = new android.widget.ScrollView(this);
    scroll.addView(panel);
    AlertDialog dialog = new AlertDialog.Builder(this)
        .setTitle("Model and reasoning")
        .setView(scroll)
        .setPositiveButton("OK", null)
        .create();
    dialog.setCanceledOnTouchOutside(false);
    dialog.show();
    if (dialog.getWindow() != null) {
      dialog.getWindow().setBackgroundDrawable(new android.graphics.drawable.InsetDrawable(
          pickerSurface(BG, PICKER_OUTLINE), dp(12), dp(8), dp(12), dp(8)));
    }
    java.util.ArrayList<String> efforts = new java.util.ArrayList<>();
    Runnable refreshEfforts = () -> {
      JSONObject model = models.get(modelPicker.getSelectedItemPosition());
      efforts.clear();
      String preferred = model == null || currentModel.equals(model.optString("model"))
          ? currentEffort : "";
      String fallback = model == null ? "" : reasoningValue(model, "defaultReasoningEffort", "");
      if (preferred.isEmpty() && fallback.isEmpty()) efforts.add("");
      if (!preferred.isEmpty()) efforts.add(preferred);
      if (model != null) {
        if (!fallback.isEmpty() && !efforts.contains(fallback)) efforts.add(fallback);
        JSONArray supported = model.optJSONArray("supportedReasoningEfforts");
        for (int i = 0; supported != null && i < supported.length(); i++) {
          Object value = supported.opt(i);
          if (value instanceof String && !((String) value).isEmpty() && !efforts.contains(value))
            efforts.add((String) value);
        }
      }
      java.util.ArrayList<String> labels = new java.util.ArrayList<>();
      for (String effort : efforts) labels.add(reasoningLabel(effort));
      effortPicker.setAdapter(new ModelPickerAdapter(effortPicker, labels));
      effortPicker.setEnabled(model != null && (efforts.size() > 1 || !efforts.get(0).isEmpty()));
      dialog.getButton(AlertDialog.BUTTON_POSITIVE).setEnabled(model != null);
    };
    modelPicker.setOnItemSelectedListener(new android.widget.AdapterView.OnItemSelectedListener() {
      public void onItemSelected(android.widget.AdapterView<?> parent, View view, int position, long itemId) {
        refreshEfforts.run();
      }

      public void onNothingSelected(android.widget.AdapterView<?> parent) {}
    });
    refreshEfforts.run();
    dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(v -> {
      JSONObject model = models.get(modelPicker.getSelectedItemPosition());
      if (model == null || api != client || !selected.equals(id)) return;
      String slug = model.optString("model");
      int effortPosition = effortPicker.getSelectedItemPosition();
      if (effortPosition < 0) return;
      String effort = efforts.get(effortPosition);
      dialog.dismiss();
      if (slug.equals(currentModel) && effort.equals(currentEffort)) return;
      action(
          () -> client.request("/conversations/" + ApiClient.segment(id) + "/model-selection",
              new JSONObject().put("model", slug).put("effort", effort.isEmpty() ? JSONObject.NULL : effort)),
          state -> {
            if (api != client || !selected.equals(id)) return;
            if (state instanceof JSONObject) renderConversation((JSONObject) state);
            else refresh();
          },
          false);
    });
  }

  private Spinner modelPicker(String name) {
    Spinner picker = new Spinner(this, Spinner.MODE_DROPDOWN);
    picker.setContentDescription(name);
    StateListDrawable surface = new StateListDrawable();
    surface.addState(new int[] { -android.R.attr.state_enabled }, pickerSurface(BG, PICKER_OUTLINE));
    surface.addState(new int[] { android.R.attr.state_pressed }, pickerSurface(PICKER_ACTIVE, ACCENT));
    surface.addState(new int[] { android.R.attr.state_focused }, pickerSurface(CARD, ACCENT));
    surface.addState(new int[0], pickerSurface(CARD, PICKER_OUTLINE));
    android.graphics.drawable.Drawable chevron = getDrawable(R.drawable.ic_chevron_down).mutate();
    chevron.setTint(ACCENT);
    LayerDrawable background = new LayerDrawable(new android.graphics.drawable.Drawable[] { surface, chevron });
    background.setLayerSize(1, dp(16), dp(16));
    background.setLayerGravity(1, Gravity.END | Gravity.CENTER_VERTICAL);
    background.setLayerInsetRelative(1, 0, 0, dp(12), 0);
    picker.setBackground(background);
    picker.setPaddingRelative(dp(12), dp(3), dp(40), dp(3));
    picker.setPopupBackgroundDrawable(pickerSurface(CARD, PICKER_OUTLINE));
    picker.setDropDownHorizontalOffset(-dp(12));
    picker.addOnLayoutChangeListener((view, left, top, right, bottom, oldLeft, oldTop, oldRight, oldBottom) -> {
      int width = right - left;
      if (width > 0 && width != oldRight - oldLeft) {
        picker.setDropDownWidth(width);
        if (picker.getAdapter() instanceof ModelPickerAdapter)
          picker.post(((ModelPickerAdapter) picker.getAdapter())::notifyDataSetChanged);
      }
    });
    return picker;
  }

  private GradientDrawable pickerSurface(int fill, int stroke) {
    GradientDrawable drawable = new GradientDrawable();
    drawable.setColor(fill);
    drawable.setCornerRadius(dp(8));
    if (stroke != Color.TRANSPARENT) drawable.setStroke(dp(1), stroke);
    return drawable;
  }

  private final class ModelPickerAdapter extends ArrayAdapter<String> {
    private final Spinner picker;

    ModelPickerAdapter(Spinner picker, java.util.List<String> labels) {
      super(MainActivity.this, android.R.layout.simple_spinner_dropdown_item, labels);
      this.picker = picker;
    }

    @Override
    public View getView(int position, View recycled, ViewGroup parent) {
      return pickerRow((TextView) super.getView(position, recycled, parent), false, position);
    }

    @Override
    public View getDropDownView(int position, View recycled, ViewGroup parent) {
      LinearLayout row = recycled instanceof LinearLayout ? (LinearLayout) recycled : column();
      if (row.getChildCount() == 0) row.addView(new TextView(MainActivity.this), match());
      TextView text = (TextView) row.getChildAt(0);
      text.setText(getItem(position));
      pickerRow(text, true, position);
      int width = picker.getWidth();
      if (width > 0) text.setWidth(width - dp(2));
      return row;
    }

    private TextView pickerRow(TextView text, boolean inMenu, int position) {
      boolean selected = inMenu && position == picker.getSelectedItemPosition();
      text.setSingleLine(false);
      text.setHorizontallyScrolling(false);
      text.setEllipsize(null);
      text.setGravity(Gravity.CENTER_VERTICAL | Gravity.START);
      text.setTextSize(TypedValue.COMPLEX_UNIT_SP, 15 * uiScale);
      text.setTextColor(selected ? ACCENT : TEXT);
      text.setPaddingRelative(inMenu ? dp(10) : 0, dp(8), inMenu ? dp(10) : 0, dp(8));
      text.setMinHeight(dp(44));
      ViewGroup.LayoutParams layout = text.getLayoutParams();
      if (layout != null) layout.height = ViewGroup.LayoutParams.WRAP_CONTENT;
      int width = picker.getWidth();
      if (!inMenu) width -= picker.getPaddingStart() + picker.getPaddingEnd();
      if (width > 0) text.setMaxWidth(width);
      if (inMenu) {
        StateListDrawable row = new StateListDrawable();
        row.addState(new int[] { android.R.attr.state_pressed }, pickerSurface(PICKER_ACTIVE, ACCENT));
        row.addState(new int[0], pickerSurface(selected ? PICKER_ACTIVE : Color.TRANSPARENT, Color.TRANSPARENT));
        text.setBackground(row);
      } else {
        text.setBackground(null);
      }
      return text;
    }
  }

  private void archive(String id) {
    if (api == null || id.isEmpty()) return;
    ApiClient client = api;
    new AlertDialog.Builder(this)
        .setTitle("Archive conversation?")
        .setMessage(
            "Pending scheduled messages will be canceled. You can restore the conversation later.")
        .setNegativeButton("Cancel", null)
        .setPositiveButton(
            "Archive",
            (dialog, which) ->
                action(
                    () ->
                        client.request(
                            "/conversations/" + ApiClient.segment(id) + "/archive",
                            new JSONObject()),
                    ignored -> {
                      if (selected.equals(id)) select("");
                    },
                    false))
        .show();
  }

  private void resetSelectedConversation() {
    if (api == null || conversation == null || selected.isEmpty()) return;
    if (!conversation.isNull("activeTurnId")
        || "starting".equals(conversation.optString("status"))
        || "uncertain".equals(conversation.optString("status"))
        || "disconnected".equals(conversation.optString("status"))) {
      notice("Finish or stop active work before resetting this conversation.");
      return;
    }
    ApiClient client = api;
    String id = selected;
    new AlertDialog.Builder(this)
        .setTitle("Reset conversation?")
        .setMessage(
            "Start fresh with the same name, folder, model, and reasoning? The current conversation will be archived and remain available in Archived History.")
        .setNegativeButton("Cancel", null)
        .setPositiveButton(
            "Reset",
            (dialog, which) ->
                action(
                    () ->
                        client.request(
                            "/conversations/" + ApiClient.segment(id) + "/reset", new JSONObject()),
                    result -> {
                      if (!(result instanceof JSONObject)) {
                        problem("The desktop returned an invalid reset response. Refresh and try again.");
                        return;
                      }
                      String replacement = ((JSONObject) result).optString("id");
                      if (replacement.isEmpty()) {
                        problem("The desktop returned no replacement conversation. Refresh and try again.");
                        return;
                      }
                      select(replacement);
                      notice("Conversation reset. The previous transcript is available in Archived History.");
                    },
                    false))
        .show();
  }

  private void showArchived() {
    if (api == null) return;
    ApiClient client = api;
    action(
        () -> client.request("/conversations?archived=true", null),
        result -> {
          JSONArray archived = (JSONArray) result;
          String[] names = new String[archived.length()];
          for (int i = 0; i < archived.length(); i++)
            names[i] =
                archived.optJSONObject(i).optString("title")
                    + "\n"
                    + archived.optJSONObject(i).optString("workingDirectory");
          AlertDialog.Builder dialog =
              new AlertDialog.Builder(this)
                  .setTitle("Archived conversations")
                  .setNegativeButton("Close", null);
          if (names.length == 0) dialog.setMessage("No archived conversations.");
          else
            dialog.setItems(
                names,
                (d, which) -> {
                  String id = archived.optJSONObject(which).optString("id");
                  new AlertDialog.Builder(this)
                      .setTitle("Restore conversation?")
                      .setMessage("Canceled scheduled messages stay canceled.")
                      .setNegativeButton("Cancel", null)
                      .setPositiveButton(
                          "Restore",
                          (confirm, choice) ->
                              action(
                                  () ->
                                      client.request(
                                          "/conversations/" + ApiClient.segment(id) + "/restore",
                                          new JSONObject()),
                                  ignored -> select(id),
                                  false))
                      .show();
                });
          dialog.show();
        },
        false);
  }

  private void interrupt() {
    if (api == null || conversation == null) return;
    String id = selected;
    ApiClient client = api;
    action(
        () ->
            client.request(
                "/conversations/" + ApiClient.segment(id) + "/interrupt", new JSONObject()),
        ignored -> {},
        true);
  }

  private void rename(String id, String title) {
    EditText input = input("Conversation name");
    input.setText(title);
    input.setFilters(
        new android.text.InputFilter[] {new android.text.InputFilter.LengthFilter(100)});
    new AlertDialog.Builder(this)
        .setTitle("Rename conversation")
        .setView(input)
        .setNegativeButton("Cancel", null)
        .setPositiveButton(
            "Save",
            (d, w) -> {
              ApiClient client = api;
              action(
                  () ->
                      client.request(
                          "/conversations/" + ApiClient.segment(id) + "/rename",
                          new JSONObject().put("title", input.getText().toString())),
                  ignored -> {},
                  false);
            })
        .show();
  }

  private void renderRequest(String id, JSONObject request) {
    LinearLayout card = column();
    card.setPadding(dp(10), dp(10), dp(10), dp(10));
    card.setBackgroundColor(Color.rgb(56, 49, 30));
    boolean supported = request.optBoolean("supported");
    card.addView(
        label(
            supported
                ? "Your input is needed"
                : "Unsupported request. Stop work and complete setup on desktop.",
            16));
    JSONObject parameters = request.optJSONObject("parameters");
    String requestId = request.optString("id");
    if (parameters.has("command")) card.addView(label(parameters.optString("command"), 14));
    if (parameters.has("reason")) card.addView(label(parameters.optString("reason"), 14));
    String path =
        "/conversations/" + ApiClient.segment(id) + "/requests/" + ApiClient.segment(requestId);
    if (supported && "item/tool/requestUserInput".equals(request.optString("method"))) {
      JSONArray questions = parameters.optJSONArray("questions");
      for (int i = 0; i < questions.length(); i++) {
        JSONObject question = questions.optJSONObject(i);
        String key = id + ":" + requestId + ":" + question.optString("id");
        card.addView(label(question.optString("question"), 15));
        EditText answer = input("Your answer");
        if (question.optBoolean("isSecret"))
          answer.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_PASSWORD);
        answer.setText(answers.getOrDefault(key, ""));
        watch(answer, value -> answers.put(key, value));
        card.addView(answer, match());
        JSONArray options = question.optJSONArray("options");
        if (options != null)
          for (int j = 0; j < options.length(); j++) {
            String value = options.optJSONObject(j).optString("label");
            card.addView(button(value, v -> answer.setText(value)), match());
          }
      }
      card.addView(
          button(
              "Send answers",
              v -> {
                ApiClient client = api;
                action(
                    () -> {
                      JSONObject values = new JSONObject();
                      for (int i = 0; i < questions.length(); i++) {
                        String questionId = questions.optJSONObject(i).optString("id");
                        values.put(
                            questionId,
                            new JSONArray()
                                .put(
                                    answers.getOrDefault(
                                        id + ":" + requestId + ":" + questionId, "")));
                      }
                      return client.request(path, new JSONObject().put("answers", values));
                    },
                    ignored -> {},
                    true);
              }),
          match());
    } else if (supported) {
      JSONArray decisions = request.optJSONArray("decisions");
      for (int i = 0; i < decisions.length(); i++) {
        String decision = decisions.optString(i);
        card.addView(
            button(
                "accept".equals(decision)
                    ? "Approve once"
                    : "decline".equals(decision) ? "Deny" : "Cancel work",
                v -> {
                  ApiClient client = api;
                  action(
                      () -> client.request(path, new JSONObject().put("decision", decision)),
                      ignored -> {},
                      true);
                }),
            match());
      }
    }
    transcript.addView(card, match());
  }

  private void showSettings() {
    settingsPage = true;
    generation++;
    ui.removeCallbacks(elapsedTicker);
    base();
    LinearLayout header = row();
    header.addView(button("Back", v -> showChat()));
    TextView title = label("Settings", 24);
    title.setTypeface(null, Typeface.BOLD);
    header.addView(title, new LinearLayout.LayoutParams(0, -2, 1));
    root.addView(header, match());

    LinearLayout panel = column();
    panel.setPadding(0, dp(8), 0, dp(8));
    settingsSection(panel, "Servers");
    settingsAction(panel, "Add server", this::showPairing);
    try {
      JSONArray servers = store.connections();
      if (servers.length() == 0) panel.addView(label("No saved servers.", 15), match());
      for (int i = 0; i < servers.length(); i++) addServerCard(panel, servers.getJSONObject(i));
    } catch (Exception exception) {
      panel.addView(label("Saved servers could not be read.", 15), match());
    }

    settingsSection(panel, "Appearance");
    panel.addView(label("Font and UI size", 15), match());
    Spinner scale = new Spinner(this);
    String[] scaleLabels = {"Compact (90%)", "Comfortable (100%, default)", "Plus (110%)", "Large (120%)"};
    scale.setAdapter(new ArrayAdapter<>(this, android.R.layout.simple_spinner_dropdown_item, scaleLabels));
    scale.setMinimumHeight(scaledDp(48));
    scale.setPadding(scaledDp(12), 0, scaledDp(12), 0);
    scale.setSelection(uiScaleIndex(), false);
    final boolean[] scaleReady = {false};
    scale.post(() -> scaleReady[0] = true);
    scale.setOnItemSelectedListener(
        new AdapterView.OnItemSelectedListener() {
          @Override
          public void onItemSelected(AdapterView<?> parent, View view, int position, long id) {
            if (!scaleReady[0]) return;
            float next = position == 0 ? 0.9f : position == 1 ? 1.0f : position == 2 ? 1.1f : 1.2f;
            if (Math.abs(next - uiScale) < 0.01f) return;
            uiScale = next;
            appPreferences.edit().putFloat(UI_SCALE_KEY, uiScale).apply();
            showSettings();
          }

          @Override
          public void onNothingSelected(AdapterView<?> parent) {}
        });
    panel.addView(scale, match());

    settingsSection(panel, "Application");
    settingsAction(panel, "Check for updates", () -> updater.check(true));
    android.widget.CheckBox updates = new android.widget.CheckBox(this);
    updates.setText(R.string.check_updates_on_startup);
    updates.setTextColor(TEXT);
    updates.setTextSize(TypedValue.COMPLEX_UNIT_SP, 14 * uiScale);
    updates.setMinimumHeight(scaledDp(48));
    updates.setChecked(store.checkUpdatesOnStartup());
    updates.setOnCheckedChangeListener((button, checked) -> store.checkUpdatesOnStartup(checked));
    panel.addView(updates, match());
    TextView about =
        label(
            "PocketAgent Android "
                + BuildConfig.VERSION_NAME
                + "\nNative client for following and directing work on a PocketAgent desktop/server.",
            14);
    about.setTextColor(Color.rgb(180, 198, 181));
    panel.addView(about, match());
    error = errorLabel();
    panel.addView(error, match());

    ScrollView settingsScroll = new ScrollView(this);
    settingsScroll.addView(panel);
    root.addView(settingsScroll, new LinearLayout.LayoutParams(-1, 0, 1));
  }

  private void settingsSection(LinearLayout panel, String title) {
    TextView heading = label(title, 13);
    heading.setTextColor(ACCENT);
    heading.setTypeface(null, Typeface.BOLD);
    heading.setPadding(0, dp(12), 0, dp(2));
    panel.addView(heading, match());
  }

  private int uiScaleIndex() {
    return uiScale < 0.95f ? 0 : uiScale > 1.15f ? 3 : uiScale > 1.05f ? 2 : 1;
  }

  private void settingsAction(LinearLayout panel, String title, Runnable action) {
    Button actionButton = button(title, v -> action.run());
    actionButton.setGravity(Gravity.START | Gravity.CENTER_VERTICAL);
    panel.addView(actionButton, match());
  }

  private void addServerCard(LinearLayout panel, JSONObject server) {
    String origin = server.optString("origin");
    String name = connectionNames.getOrDefault(origin, server.optString("name", origin));
    boolean active = api != null && origin.equals(api.origin);
    boolean online = connectionStates.getOrDefault(origin, active && connected);
    String status = online ? "Connected" : "Not connected";
    if (connectionErrors.containsKey(origin)) status += "\n" + connectionErrors.get(origin);
    LinearLayout card = column();
    card.setPadding(dp(8), dp(4), dp(8), dp(4));
    card.setBackgroundColor(CARD);
    ApiClient client = connections.get(origin);
    String address = client == null ? origin : client.address();
    card.addView(label(name + "\n" + address + "\n" + status, 14), match());
    LinearLayout actions = row();
    actions.addView(button("Usage and resets", v -> showServerUsage(origin)));
    actions.addView(button("Open", v -> activateSavedServer(origin)));
    actions.addView(
        button(
            "Unpair",
            v -> {
              if (active) unpairServer();
              else unpairSavedServer(origin);
            }));
    card.addView(actions, match());
    LinearLayout.LayoutParams layout = match();
    layout.setMargins(0, dp(6), 0, 0);
    panel.addView(card, layout);
  }

  private void activateSavedServer(String origin) {
    try {
      JSONObject saved = store.activate(origin);
      api = connections.get(origin);
      if (api == null) {
        api = ApiClient.fromSaved(saved);
        connections.put(origin, api);
      }
      serverName = connectionNames.getOrDefault(origin, saved.optString("name", api.origin));
      connectionNames.put(origin, serverName);
      connected = connectionStates.getOrDefault(origin, false);
      selected = store.selected(api.origin);
      conversation = null;
      answers.clear();
      showChat();
    } catch (Exception exception) {
      problem("This saved server could not be opened. Pair with it again.");
    }
  }

  private void unpairSavedServer(String origin) {
    try {
      ApiClient client = connections.get(origin);
      if (client == null) throw new IllegalArgumentException("Saved server was not found.");
      action(
          () -> client.request("/logout", new JSONObject()),
          ignored -> {
            try {
              store.remove(origin);
              connections.remove(origin);
              client.close();
              connectionNames.remove(origin);
              connectionStates.remove(origin);
              connectionErrors.remove(origin);
              showSettings();
            } catch (Exception exception) {
              problem("The saved server could not be removed.");
            }
          },
          false);
    } catch (Exception exception) {
      problem("The saved server could not be opened. Pair with it again.");
    }
  }

  private void unpairServer() {
    ApiClient client = api;
    action(
        () -> client.request("/logout", new JSONObject()),
        ignored -> {
          try {
            store.remove(client.origin);
          } catch (Exception ignoredError) {
          }
          connections.remove(client.origin);
          client.close();
          connectionNames.remove(client.origin);
          connectionStates.remove(client.origin);
          connectionErrors.remove(client.origin);
          api = null;
          conversation = null;
          selected = "";
          send = null;
          showPairing();
        },
        false);
  }

  private void showServers() {
    try {
      JSONArray servers = store.connections();
      String[] choices = new String[servers.length() + 1];
      for (int i = 0; i < servers.length(); i++)
        choices[i] =
            servers.getJSONObject(i).optString("name")
                + "\n"
                + servers.getJSONObject(i).optString("origin");
      choices[choices.length - 1] = "+ Add server";
      new AlertDialog.Builder(this)
          .setTitle("Servers")
          .setItems(
              choices,
              (dialog, index) -> {
                if (index == servers.length()) {
                  showPairing();
                  return;
                }
                JSONObject saved = servers.optJSONObject(index);
                if (saved != null) activateSavedServer(saved.optString("origin"));
              })
          .setNegativeButton("Close", null)
          .show();
    } catch (Exception exception) {
      problem("Saved server connections could not be read.");
    }
  }

  private static String elapsed(long milliseconds) {
    long seconds = Math.max(0, milliseconds / 1000);
    return seconds >= 3600
        ? seconds / 3600 + "h " + seconds % 3600 / 60 + "m"
        : seconds >= 60 ? seconds / 60 + "m " + seconds % 60 + "s" : seconds + "s";
  }

  private String remaining(JSONObject window) {
    return window == null ? "unavailable" : window.optInt("remainingPercent") + "%";
  }

  private void renderUsage() {
    JSONObject fiveHour = usageState == null ? null : usageState.optJSONObject("fiveHour");
    JSONObject weekly = usageState == null ? null : usageState.optJSONObject("weekly");
    String detailed =
        "5hr: "
            + remaining(fiveHour)
            + "  "
            + usageReset(fiveHour, false)
            + "  ·  weekly: "
            + remaining(weekly)
            + "  "
            + usageReset(weekly, true);
    String compact =
        "5hr: " + remaining(fiveHour) + "  ·  weekly: " + remaining(weekly);
    usageView.setText(detailed);
    usageView.post(
        () -> {
          if (usageView.getWidth() <= 0) return;
          usageView.setText(
              usageView.getPaint().measureText(detailed) > usageView.getWidth() ? compact : detailed);
        });
  }

  private String usageReset(JSONObject window, boolean dateOnly) {
    if (window == null || window.isNull("resetsAt")) return "—";
    try {
      java.time.format.DateTimeFormatter format =
          java.time.format.DateTimeFormatter.ofPattern(dateOnly ? "M/d/yy" : "h:mm a");
      return java.time.Instant.parse(window.optString("resetsAt"))
          .atZone(java.time.ZoneId.systemDefault())
          .format(format);
    } catch (Exception ignored) {
      return "—";
    }
  }

  private String reset(JSONObject window) {
    if (window == null || window.isNull("resetsAt")) return "Reset time unavailable";
    return localTime(window.optString("resetsAt"));
  }

  private String localTime(String timestamp) {
    try {
      return java.time.Instant.parse(timestamp)
          .atZone(java.time.ZoneId.systemDefault())
          .format(
              java.time.format.DateTimeFormatter.ofLocalizedDateTime(
                  java.time.format.FormatStyle.MEDIUM, java.time.format.FormatStyle.SHORT));
    } catch (Exception ignored) {
      return "Reset time unavailable";
    }
  }

  private void showUsage() {
    showUsageDialog(serverName, usageState);
  }

  private void viewImage(String conversationId, JSONObject image, String imageKey, Button trigger, String alt) {
    ApiClient client = api;
    if (client == null) return;
    String source = image.optString("source");
    network.execute(
        () -> {
          try {
            byte[] bytes;
            if (source.startsWith("data:") && source.contains(",")) {
              bytes = android.util.Base64.decode(source.substring(source.indexOf(',') + 1), android.util.Base64.DEFAULT);
            } else {
              bytes = client.requestBytes("/conversations/" + ApiClient.segment(conversationId) + "/images?path=" + ApiClient.segment(source));
            }
            Bitmap bitmap = BitmapFactory.decodeByteArray(bytes, 0, bytes.length);
            if (bitmap == null) throw new IllegalArgumentException("The desktop returned an unsupported image.");
            ui.post(
                () -> {
                  viewedImages.add(imageKey);
                  trigger.setText(getString(R.string.viewed_image, alt));
                  showImageDialog(alt, bitmap);
                });
          } catch (Exception exception) {
            ui.post(() -> problem("Image could not be viewed: " + exception.getMessage()));
          }
        });
  }

  private void showImageDialog(String alt, Bitmap bitmap) {
    LinearLayout panel = column();
    panel.addView(label(getString(R.string.viewed_image, alt), 14), match());
    ImageView imageView = new ImageView(this);
    imageView.setImageBitmap(bitmap);
    imageView.setAdjustViewBounds(true);
    imageView.setScaleType(ImageView.ScaleType.FIT_CENTER);
    panel.addView(imageView, match());
    new AlertDialog.Builder(this).setTitle("Image").setView(panel).setPositiveButton("Close", null).show();
  }

  private void showServerUsage(String origin) {
    ApiClient client = connections.get(origin);
    if (client == null) {
      problem("This server is not available. Pair with it again.");
      return;
    }
    network.execute(
        () -> {
          try {
            JSONObject usage = (JSONObject) client.request("/usage", null);
            ui.post(() -> showUsageDialog(connectionNames.getOrDefault(origin, origin), usage));
          } catch (Exception exception) {
            ui.post(() -> problem("Usage could not be read for this server."));
          }
        });
  }

  private void showUsageDialog(String source, JSONObject usageState) {
    JSONObject five = usageState == null ? null : usageState.optJSONObject("fiveHour"),
        week = usageState == null ? null : usageState.optJSONObject("weekly");
    new AlertDialog.Builder(this)
        .setTitle(source + " usage remaining")
        .setMessage(
            "Five hours: "
                + remaining(five)
                + "\n"
                + reset(five)
                + "\n\nWeekly: "
                + remaining(week)
                + "\n"
                + reset(week)
                + "\n\n"
                + "Reported by this server's Codex account. Unavailable windows are not estimated.")
        .setPositiveButton("Close", null)
        .show();
  }

  private void scheduleMessage() {
    if (submitting) return;
    if (composer.getText().toString().trim().isEmpty()) {
      showScheduled();
      return;
    }
    if (api == null
        || conversation == null
        || !connected) {
      problem("Write a message in the composer, then choose Schedule message.");
      return;
    }
    ApiClient client = api;
    String id = selected, text = composer.getText().toString();
    if (reviewDelivery()) return;
    java.util.Calendar chosen = java.util.Calendar.getInstance();
    chosen.add(java.util.Calendar.MINUTE, 1);
    new android.app.DatePickerDialog(
            this,
            (date, year, month, day) -> {
              chosen.set(java.util.Calendar.YEAR, year);
              chosen.set(java.util.Calendar.MONTH, month);
              chosen.set(java.util.Calendar.DAY_OF_MONTH, day);
              new android.app.TimePickerDialog(
                      this,
                      (time, hour, minute) -> {
                        chosen.set(java.util.Calendar.HOUR_OF_DAY, hour);
                        chosen.set(java.util.Calendar.MINUTE, minute);
                        chosen.set(java.util.Calendar.SECOND, 0);
                        chosen.set(java.util.Calendar.MILLISECOND, 0);
                        if (chosen.getTimeInMillis() <= System.currentTimeMillis()) {
                          problem("Choose a future date and time.");
                          return;
                        }
                        String due =
                            java.time.Instant.ofEpochMilli(chosen.getTimeInMillis()).toString();
                        action(
                            () ->
                                client.request(
                                    "/conversations/" + ApiClient.segment(id) + "/scheduled",
                                    new JSONObject().put("text", text).put("dueAt", due).put("clientMessageId", java.util.UUID.randomUUID().toString())),
                            result -> {
                              if (store.draft(client.origin, id).equals(text))
                                store.draft(client.origin, id, "");
                              if (selected.equals(id) && composer.getText().toString().equals(text))
                                composer.setText("");
                               refresh();
                              notice(
                                  "Message scheduled. The desktop must be running at that time.");
                            },
                            true);
                      },
                      chosen.get(java.util.Calendar.HOUR_OF_DAY),
                      chosen.get(java.util.Calendar.MINUTE),
                      android.text.format.DateFormat.is24HourFormat(this))
                  .show();
            },
            chosen.get(java.util.Calendar.YEAR),
            chosen.get(java.util.Calendar.MONTH),
            chosen.get(java.util.Calendar.DAY_OF_MONTH))
        .show();
  }

  private void showScheduled() {
    if (api == null || conversation == null) {
      problem("Select a conversation to view scheduled messages.");
      return;
    }
    ApiClient client = api;
    String id = selected;
    action(
        () -> client.request("/conversations/" + ApiClient.segment(id) + "/scheduled", null),
        result -> {
          JSONArray jobs = (JSONArray) result;
          if (jobs.length() == 0) {
            notice("No scheduled messages in this conversation.");
            return;
          }
          String[] choices = new String[jobs.length()];
          for (int i = 0; i < jobs.length(); i++) {
            JSONObject job = jobs.optJSONObject(i);
            String preview = job.optString("text");
            choices[i] =
                job.optString("status")
                    + " - "
                    + localTime(job.optString("dueAt"))
                    + "\n"
                    + preview.substring(0, Math.min(preview.length(), 120));
          }
          new AlertDialog.Builder(this)
              .setTitle("Scheduled messages")
              .setItems(
                  choices,
                  (d, index) -> {
                    JSONObject job = jobs.optJSONObject(index);
                    if (!"pending".equals(job.optString("status"))) {
                      new AlertDialog.Builder(this)
                          .setMessage(
                              job.optString("error", "This message is " + job.optString("status")))
                          .setPositiveButton("Close", null)
                          .show();
                      return;
                    }
                    new AlertDialog.Builder(this)
                        .setMessage("Cancel this scheduled message?")
                        .setNegativeButton("Keep", null)
                        .setPositiveButton(
                            "Cancel message",
                            (dialog, which) ->
                                action(
                                    () ->
                                        client.request(
                                            "/scheduled/"
                                                + ApiClient.segment(job.optString("id"))
                                                + "/cancel",
                                            new JSONObject()),
                                     ignored -> {
                                       refresh();
                                       notice("Scheduled message canceled.");
                                     },
                                    false))
                        .show();
                  })
              .setNegativeButton("Close", null)
              .show();
        },
        false);
  }

  private String safeError(Exception exception) {
    if (exception instanceof ApiClient.ApiError)
      return exception.getMessage() + "\n\nNetwork diagnostic: the HTTPS server and selected port responded. This is an API or pairing response, not a blocked-port failure.";
    if (exception instanceof IllegalArgumentException || exception instanceof RelayConnection.ConnectionError)
      return exception.getMessage();
    if (exception instanceof javax.net.ssl.SSLException)
      return "Desktop identity or HTTPS verification failed. Check the phone clock and server"
                 + " address. If the desktop key changed, scan a fresh QR on that desktop. No"
                 + " connection was trusted automatically.";
    if (exception instanceof UnknownHostException)
      return "The server address could not be resolved. Check the LAN IP or hostname in the pairing string."
          + " No TCP port or firewall test was reached.";
    if (exception instanceof NoRouteToHostException)
      return "The phone has no route to the server address. Check that both devices are on the same LAN"
          + " and that Wi-Fi client isolation is disabled. No TCP port or firewall test was completed.";
    if (exception instanceof SocketTimeoutException)
      return "The TCP connection to the server timed out. The address was not reachable within the timeout;"
          + " check the selected port, router forwarding, Wi-Fi isolation, and Windows Firewall. A timeout"
          + " does not prove which one blocked it.";
    if (exception instanceof ConnectException)
      return "The server host was reached, but the TCP connection was refused. The selected port is reachable"
          + " at the host, but no listener accepted it; check that PocketAgent is running and listening on that port."
          + " Windows Firewall did not silently drop this attempt.";
    return "Could not reach the desktop. Check the IP and port, that the server is running, router"
        + " TCP forwarding, and Windows Firewall. Test over cellular; some routers cannot"
        + " access their own public IP from Wi-Fi.";
  }
}
