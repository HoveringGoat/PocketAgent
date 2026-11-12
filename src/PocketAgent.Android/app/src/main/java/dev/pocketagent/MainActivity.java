package dev.pocketagent;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Intent;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.text.Editable;
import android.text.InputType;
import android.text.TextWatcher;
import android.view.Gravity;
import android.view.View;
import android.widget.Button;
import android.widget.EditText;
import android.widget.HorizontalScrollView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import android.net.Uri;
import java.io.BufferedReader;
import java.io.InputStreamReader;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
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
  private static final String RELEASE_LIST_ENDPOINT = "https://api.github.com/repos/HoveringGoat/PocketAgent/releases?per_page=30";
  private final Handler ui = new Handler(Looper.getMainLooper());
  private final ExecutorService network = Executors.newCachedThreadPool();
  private SessionStore store;
  private ApiClient api;
  private String selected = "", tabSignature = "", transcriptSignature = "";
  private JSONObject conversation;
  private LinearLayout root, tabs, transcript, metadata;
  private TextView connection, sessionLocation, error, context, usageView;
  private JSONObject usageState;
  private JSONArray scheduledState = new JSONArray();
  private final java.util.HashSet<String> expandedItems = new java.util.HashSet<>();
  private final java.util.HashMap<String, java.util.ArrayList<JSONObject>> optimisticItems = new java.util.HashMap<>();
  private long usageReadAt;
  private String serverName = "PocketAgent";
  private EditText composer;
  private Button send, stop, schedule;
  private ScrollView scroll;
  private boolean resumed, polling, connected, submitting, loadingDraft, connectionProblem;
  private boolean updateCheckStarted;
  private int generation;
  private final Map<String, String> answers = new HashMap<>();
  private final Runnable poll =
      new Runnable() {
        @Override
        public void run() {
          if (resumed && api != null) {
            refresh();
            ui.postDelayed(this, 1200);
          }
        }
      };

  @Override
  public void onCreate(Bundle state) {
    super.onCreate(state);
    store = new SessionStore(this);
    try {
      JSONObject session = store.load();
      if (session != null) {
        api =
            new ApiClient(
                ApiClient.normalizeOrigin(session.getString("origin")),
                session.getString("token"),
                session.optString("pin"));
        serverName = session.optString("name", api.origin);
        selected = store.selected(api.origin);
      }
    } catch (Exception ignored) {
      store.clear();
    }
    if (api == null) showPairing();
    else showChat();
    checkForUpdates();
  }

  @Override
  protected void onResume() {
    super.onResume();
    resumed = true;
    ui.removeCallbacks(poll);
    ui.post(poll);
  }

  @Override
  protected void onPause() {
    resumed = false;
    ui.removeCallbacks(poll);
    super.onPause();
  }

  @Override
  protected void onDestroy() {
    generation++;
    ui.removeCallbacksAndMessages(null);
    network.shutdownNow();
    super.onDestroy();
  }

  private int dp(int value) {
    return Math.round(value * getResources().getDisplayMetrics().density);
  }

  private void checkForUpdates() {
    if (updateCheckStarted) return;
    updateCheckStarted = true;
    network.execute(
        () -> {
          HttpURLConnection connection = null;
          try {
            connection = (HttpURLConnection) new URL(RELEASE_LIST_ENDPOINT).openConnection();
            connection.setConnectTimeout(5000);
            connection.setReadTimeout(8000);
            connection.setRequestProperty("Accept", "application/vnd.github+json");
            connection.setRequestProperty("User-Agent", "PocketAgent/" + BuildConfig.VERSION_NAME);
            if (connection.getResponseCode() < 200 || connection.getResponseCode() >= 300) return;
            StringBuilder body = new StringBuilder();
            try (BufferedReader reader = new BufferedReader(new InputStreamReader(connection.getInputStream(), StandardCharsets.UTF_8))) {
              String line;
              while ((line = reader.readLine()) != null && body.length() < 512_000) body.append(line);
            }
            JSONObject release = selectChannelRelease(new JSONArray(body.toString()), "android-v");
            if (release == null) return;
            String version = release.optString("tag_name").substring("android-v".length());
            if (!isNewerVersion(version, BuildConfig.VERSION_NAME)) return;
            boolean hasAndroidAsset = false;
            JSONArray assets = release.optJSONArray("assets");
            if (assets != null) {
              String expectedAsset = "PocketAgent-" + version + "-android.apk";
              for (int i = 0; i < assets.length(); i++) {
                JSONObject asset = assets.optJSONObject(i);
                if (asset != null && expectedAsset.equals(asset.optString("name"))) {
                  hasAndroidAsset = true;
                  break;
                }
              }
            }
            if (!hasAndroidAsset) return;
            String releasePage = release.optString("html_url", "https://github.com/HoveringGoat/PocketAgent/releases");
            String notes = channelNotes(release.optString("body", ""));
            if (notes.length() > 1200) notes = notes.substring(0, 1200) + "…";
            String message = "PocketAgent " + version + " is available.\n\n" + (notes.isEmpty() ? "Open the GitHub release page to download the APK." : notes);
            String finalReleasePage = releasePage;
            ui.post(() -> showUpdatePrompt(version, message, finalReleasePage));
          } catch (Exception ignored) {
            // Update checks are best effort and must never affect pairing or chat.
          } finally {
            if (connection != null) connection.disconnect();
          }
        });
  }

  private static JSONObject selectChannelRelease(JSONArray releases, String prefix) {
    JSONObject selected = null;
    int[] selectedVersion = new int[] {0, 0, 0};
    for (int i = 0; i < releases.length(); i++) {
      JSONObject candidate = releases.optJSONObject(i);
      if (candidate == null) continue;
      String tag = candidate.optString("tag_name");
      if (!tag.regionMatches(true, 0, prefix, 0, prefix.length())) continue;
      int[] version = parseVersion(tag.substring(prefix.length()));
      if (version == null || (selected == null && version != null) || compareVersion(version, selectedVersion) > 0) {
        if (version != null) {
          selected = candidate;
          selectedVersion = version;
        }
      }
    }
    return selected;
  }

  private static String channelNotes(String notes) {
    String marker = "## Android";
    int start = notes.indexOf(marker);
    if (start < 0) return notes.trim();
    int end = notes.indexOf("\n## ", start + marker.length());
    return notes.substring(start + marker.length(), end < 0 ? notes.length() : end).trim();
  }

  private static int[] parseVersion(String value) {
    try {
      String[] pieces = value.replaceFirst("^[vV]", "").split("\\.");
      int[] version = new int[] {0, 0, 0};
      for (int i = 0; i < Math.min(3, pieces.length); i++) version[i] = Integer.parseInt(pieces[i].replaceAll("[^0-9].*", ""));
      return version;
    } catch (Exception ignored) {
      return null;
    }
  }

  private static int compareVersion(int[] left, int[] right) {
    for (int i = 0; i < 3; i++) if (left[i] != right[i]) return Integer.compare(left[i], right[i]);
    return 0;
  }

  private void showUpdatePrompt(String version, String message, String releasePage) {
    if (isFinishing() || isDestroyed()) return;
    new AlertDialog.Builder(this)
        .setTitle("Update available: " + version)
        .setMessage(message)
        .setNegativeButton("Later", null)
        .setPositiveButton("Open release", (dialog, which) -> {
          try {
            startActivity(new Intent(Intent.ACTION_VIEW, Uri.parse(releasePage)));
          } catch (Exception ignored) {
            notice("Could not open the release page.");
          }
        })
        .show();
  }

  private static boolean isNewerVersion(String candidate, String current) {
    try {
      String[] next = candidate.split("\\.");
      String[] installed = current.split("\\.");
      for (int i = 0; i < Math.max(next.length, installed.length); i++) {
        int a = i < next.length ? Integer.parseInt(next[i].replaceAll("[^0-9].*", "")) : 0;
        int b = i < installed.length ? Integer.parseInt(installed[i].replaceAll("[^0-9].*", "")) : 0;
        if (a != b) return a > b;
      }
    } catch (Exception ignored) {
    }
    return false;
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
    view.setTextSize(size);
    view.setPadding(dp(10), dp(8), dp(10), dp(8));
    return view;
  }

  private EditText input(String hint) {
    EditText field = new EditText(this);
    field.setHint(hint);
    field.setTextColor(TEXT);
    field.setHintTextColor(Color.rgb(155, 173, 158));
    field.setTextSize(16);
    field.setPadding(dp(12), dp(10), dp(12), dp(10));
    return field;
  }

  private Button button(String text, View.OnClickListener click) {
    Button button = new Button(this);
    button.setText(text);
    button.setAllCaps(false);
    button.setTextColor(ACCENT);
    button.setOnClickListener(click);
    button.setMinWidth(dp(48));
    button.setMinimumWidth(dp(48));
    button.setMinHeight(dp(48));
    button.setMinimumHeight(dp(48));
    button.setPadding(dp(12), 0, dp(12), 0);
    button.setBackgroundTintList(android.content.res.ColorStateList.valueOf(CARD));
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
    if (error != null) {
      error.setTextColor(Color.rgb(245, 184, 165));
      error.setText(message);
      error.setVisibility(message.isEmpty() ? View.GONE : View.VISIBLE);
    }
  }

  private void notice(String message) {
    problem(message);
    if (error != null) error.setTextColor(ACCENT);
  }

  private TextView errorLabel() {
    TextView view = label("", 14);
    view.setTextColor(Color.rgb(245, 184, 165));
    view.setVisibility(View.GONE);
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
    generation++;
    base();
    root.addView(label("Connect to your desktop", 26));
    root.addView(
        label(
            "Scan its pairing QR or paste the full pairing string. It expires after ten minutes and"
                + " works once.",
            15));
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
                  ? ticket.origin
                  : serverLabel.getText().toString().trim();
          int epoch = generation;
          pair.setEnabled(false);
          notice("Verifying desktop identity and pairing…");
          network.execute(
              () -> {
                try {
                  ApiClient candidate = new ApiClient(ticket.origin, "", ticket.pin);
                  JSONObject result =
                      (JSONObject)
                          candidate.request(
                              "/devices/pair",
                              new JSONObject()
                                  .put("deviceName", device)
                                  .put("code", ticket.ticket));
                  String token = result.getString("token");
                  store.save(ticket.origin, token, label, ticket.pin);
                  ui.post(
                      () -> {
                        if (epoch != generation) return;
                        pairingInput.setText("");
                        api = new ApiClient(ticket.origin, token, ticket.pin);
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
                }
              });
        });
  }

  @Override
  protected void onActivityResult(int request, int result, android.content.Intent data) {
    super.onActivityResult(request, result, data);
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
    generation++;
    tabSignature = "";
    transcriptSignature = "";
    connected = false;
    polling = false;
    submitting = false;
    usageReadAt = 0;
    usageState = null;
    scheduledState = new JSONArray();
    base();
    LinearLayout header = row();
    connection = label("Connecting…", 15);
    connection.setTypeface(null, Typeface.BOLD);
    LinearLayout identity = column();
    identity.addView(connection, match());
    sessionLocation = label("", 11);
    sessionLocation.setTextColor(Color.rgb(143, 188, 143));
    identity.addView(sessionLocation, match());
    header.addView(identity, new LinearLayout.LayoutParams(0, -2, 1));
    Button menu = button("⋮", v -> showMenu());
    menu.setContentDescription("Connection options");
    header.addView(menu);
    root.addView(header, match());
    connection.setOnClickListener(v -> showServers());
    usageView = label("5h: unavailable · Weekly: unavailable", 11);
    usageView.setContentDescription("Usage remaining");
    usageView.setOnClickListener(v -> showUsage());
    root.addView(usageView, match());
    LinearLayout tabRow = row();
    HorizontalScrollView tabScroll = new HorizontalScrollView(this);
    tabScroll.setHorizontalScrollBarEnabled(false);
    tabs = row();
    tabScroll.addView(tabs);
    tabRow.addView(tabScroll, new LinearLayout.LayoutParams(0, -2, 1));
    Button add = button("+", v -> createConversation());
    add.setContentDescription("New conversation");
    tabRow.addView(add);
    root.addView(tabRow, match());
    scroll = new ScrollView(this);
    scroll.setFillViewport(true);
    transcript = column();
    scroll.addView(transcript);
    root.addView(scroll, new LinearLayout.LayoutParams(-1, 0, 1));
    error = errorLabel();
    root.addView(error, match());
    metadata = row();
    metadata.setContentDescription("Conversation status and context");
    metadata.setBackgroundColor(CARD);
    context = label("Select a conversation", 12);
    context.setMaxLines(1);
    context.setOnClickListener(v -> showModels());
    metadata.addView(context, new LinearLayout.LayoutParams(0, -2, 1));
    context.setOnLongClickListener(
        v -> {
          if (conversation != null)
            new AlertDialog.Builder(this)
                .setTitle("Session location")
                .setMessage(
                    conversation.optString("hostName")
                        + "\n"
                        + conversation.optString("workingDirectory"))
                .setPositiveButton("Close", null)
                .show();
          return true;
        });
    stop = button("Stop", v -> interrupt());
    stop.setContentDescription("Stop work");
    stop.setVisibility(View.GONE);
    metadata.addView(stop);
    root.addView(metadata, match());
    LinearLayout composeRow = row();
    composer = input("Message Codex");
    composer.setContentDescription("Message Codex");
    composer.setInputType(
        InputType.TYPE_CLASS_TEXT
            | InputType.TYPE_TEXT_FLAG_MULTI_LINE
            | InputType.TYPE_TEXT_FLAG_CAP_SENTENCES);
    composer.setMinLines(2);
    composer.setMaxLines(5);
    composer.setFilters(
        new android.text.InputFilter[] {new android.text.InputFilter.LengthFilter(32000)});
    composeRow.addView(composer, new LinearLayout.LayoutParams(0, -2, 1));
    send = button("Send", v -> submit());
    send.setContentDescription("Send message");
    composeRow.addView(send);
    schedule = button("\u23F1\uFE0E", v -> scheduleMessage());
    schedule.setContentDescription("Schedule message");
    composeRow.addView(schedule);
    root.addView(composeRow, match());
    watch(
        composer,
        text -> {
          if (!loadingDraft && !selected.isEmpty()) store.draft(api.origin, selected, text);
          updateSend();
        });
    loadDraft();
    updateSend();
  }

  private void loadDraft() {
    loadingDraft = true;
    composer.setText(selected.isEmpty() ? "" : store.draft(api.origin, selected));
    composer.setSelection(composer.length());
    loadingDraft = false;
  }

  private void select(String id) {
    selected = id;
    store.select(api.origin, id);
    conversation = null;
    transcriptSignature = "";
    answers.clear();
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
            JSONObject state =
                id.isEmpty()
                    ? null
                    : (JSONObject) client.request("/conversations/" + ApiClient.segment(id), null);
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
            ui.post(
                () -> {
                  if (epoch != generation || api != client) return;
                  polling = false;
                  connected = true;
                  connection.setText(R.string.connected);
                  renderTabs(sessions);
                  connection.setText(serverName);
                  connection.setContentDescription("Server " + serverName + " connected");
                  scheduledState = pendingJobs;
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
                  if (id.equals(selected) && state != null) renderConversation(state);
                  updateSend();
                });
          } catch (Exception ex) {
            ui.post(
                () -> {
                  if (epoch != generation) return;
                  polling = false;
                  connected = false;
                  connectionProblem = true;
                  connection.setText(R.string.reconnecting);
                  if (ex instanceof ApiClient.ApiError && ((ApiClient.ApiError) ex).status == 401) {
                    try {
                      store.remove(client.origin);
                    } catch (Exception ignored) {
                    }
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
    tabs.removeAllViews();
    for (int i = 0; i < sessions.length(); i++) {
      JSONObject entry = sessions.optJSONObject(i);
      String id = entry.optString("id");
      Button tab = button(entry.optString("title"), v -> select(id));
      tab.setContentDescription("Conversation " + entry.optString("title"));
      tab.setTextColor(id.equals(selected) ? Color.WHITE : ACCENT);
      tab.setTypeface(null, id.equals(selected) ? Typeface.BOLD : Typeface.NORMAL);
      tab.setOnLongClickListener(
          v -> {
            new AlertDialog.Builder(this)
                .setTitle(entry.optString("title"))
                .setItems(
                    new String[] {"Rename", "Archive"},
                    (dialog, which) -> {
                      if (which == 0) rename(id, entry.optString("title"));
                      else archive(id);
                    })
                .show();
            return true;
          });
      tabs.addView(tab);
    }
    if (sessions.length() == 0 && selected.isEmpty()) {
      transcript.removeAllViews();
      transcript.addView(label("Tap + to start a conversation.", 18));
    }
  }

  private void renderConversation(JSONObject state) {
    state = mergeOptimistic(state);
    conversation = state;
    String active = state.isNull("activeTurnId") ? "" : state.optString("activeTurnId");
    String status = state.optString("status");
    GradientDrawable metadataBackground = new GradientDrawable();
    metadataBackground.setColor(CARD);
    metadataBackground.setCornerRadius(dp(6));
    metadataBackground.setStroke(dp(1), active.isEmpty() ? Color.rgb(71, 96, 75) : Color.rgb(167, 186, 167));
    metadata.setBackground(metadataBackground);
    if (!state.isNull("activeTurnStartedAt")) {
      try {
        status +=
            " · "
                + elapsed(
                    java.time.Duration.between(
                            java.time.Instant.parse(state.optString("activeTurnStartedAt")),
                            java.time.Instant.now())
                        .toMillis());
      } catch (Exception ignored) {
      }
    } else if (!state.isNull("lastWorkDurationMs"))
      status += " · worked " + elapsed(state.optLong("lastWorkDurationMs"));
    String hostName = state.optString("hostName", "Desktop");
    String workingDirectory = state.optString("workingDirectory", "");
    context.setText(getString(R.string.session_context_v2, status)
        + (state.optBoolean("historyTruncated") ? " · older history is hidden" : ""));
    sessionLocation.setMaxLines(2);
    sessionLocation.setEllipsize(android.text.TextUtils.TruncateAt.MIDDLE);
    sessionLocation.setText(getString(R.string.session_location, hostName, workingDirectory));
    stop.setVisibility(active.isEmpty() ? View.GONE : View.VISIBLE);
    send.setText(active.isEmpty() ? "Send" : "Steer");
    JSONArray items = state.optJSONArray("items"), requests = state.optJSONArray("requests");
    String signature = items.toString() + requests.toString() + state.optString("error");
    if (!signature.equals(transcriptSignature)) {
      transcriptSignature = signature;
      boolean bottom =
          scroll.getChildCount() == 0
              || scroll.getScrollY() + scroll.getHeight() >= transcript.getHeight() - dp(80);
      int previous = scroll.getScrollY();
      transcript.removeAllViews();
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
          String summary = type + " · " + item.optString("status");
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
        transcript.addView(text, margin);
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

  private void updateSend() {
    if (send == null) return;
    String status = conversation == null ? "" : conversation.optString("status");
    send.setEnabled(
        connected
            && conversation != null
            && !conversation.optBoolean("archived")
            && !submitting
            && !composer.getText().toString().trim().isEmpty()
            && !"starting".equals(status)
            && !"uncertain".equals(status)
            && !"disconnected".equals(status));
    if (schedule != null)
      schedule.setEnabled(connected && conversation != null && !submitting && !composer.getText().toString().trim().isEmpty() && !conversation.optBoolean("archived"));
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
                  done.accept(result);
                  if (delivery) store.uncertain(client.origin, id, false);
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
      optimisticItems.computeIfAbsent(id, ignored -> new java.util.ArrayList<>()).add(optimistic);
      renderConversation(state);
    } catch (Exception ignored) {
    }
    action(
        () -> {
          JSONObject body = new JSONObject().put("text", text).put("clientMessageId", clientMessageId);
          if (!state.isNull("activeTurnId"))
            body.put("expectedTurnId", state.getString("activeTurnId"));
          return client.request("/conversations/" + ApiClient.segment(id) + "/messages", body);
        },
        receipt -> {
          if (store.draft(client.origin, id).equals(text)) store.draft(client.origin, id, "");
          if (selected.equals(id) && composer.getText().toString().equals(text))
            composer.setText("");
          if (receipt instanceof JSONObject && ((JSONObject) receipt).optBoolean("queued"))
            notice(((JSONObject) receipt).optString("message", "Queued until usage resets."));
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
        result -> newConversationDialog((JSONObject) result),
        false);
  }

  private void newConversationDialog(JSONObject options) {
    ApiClient client = api;
    LinearLayout panel = column();
    panel.setPadding(dp(16), 0, dp(16), 0);
    panel.addView(label("Working folder on the desktop", 15));
    EditText folder = input("Absolute desktop folder path");
    folder.setContentDescription("Conversation working folder");
    folder.setText(
        conversation == null
            ? options.optJSONObject("folders").optString("path")
            : conversation.optString("workingDirectory"));
    panel.addView(folder, match());
    panel.addView(
        button("Browse folders", v -> browseFolders(folder.getText().toString(), folder::setText)),
        match());
    String[] chosen = {""};
    JSONArray models = options.optJSONArray("models");
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
          JSONObject listing = (JSONObject) result;
          JSONArray roots = listing.optJSONArray("roots"),
              directories = listing.optJSONArray("directories");
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
    String[] names = new String[models.length()];
    for (int i = 0; i < models.length(); i++)
      names[i] = models.optJSONObject(i).optString("displayName");
    new AlertDialog.Builder(this)
        .setTitle("Choose model")
        .setItems(
            names,
            (dialog, which) -> {
              JSONObject choice = models.optJSONObject(which);
              selectedModel.accept(choice.optString("model"), names[which]);
            })
        .setNegativeButton("Cancel", null)
        .show();
  }

  private void showModels() {
    if (api == null || conversation == null) return;
    ApiClient client = api;
    String id = selected;
    if (!conversation.isNull("activeTurnId")) {
      problem("Finish or stop work before changing the model.");
      return;
    }
    action(
        () -> client.request("/models", null),
        result ->
            modelChoices(
                (JSONArray) result,
                (slug, name) ->
                    action(
                        () ->
                            client.request(
                                "/conversations/" + ApiClient.segment(id) + "/model",
                                new JSONObject().put("model", slug)),
                        state -> {
                          if (selected.equals(id)) renderConversation((JSONObject) state);
                          notice(
                              "Model selected for future messages, including scheduled messages.");
                          if (api == client && selected.equals(id)) showReasoning();
                        },
                        false)),
        false);
  }

  private void showReasoning() {
    if (api == null || conversation == null || conversation.isNull("model")) {
      problem("Choose a model before selecting reasoning effort.");
      return;
    }
    ApiClient client = api;
    String id = selected;
    String model = conversation.optString("model");
    String currentEffort = conversation.optString("reasoningEffort");
    action(
        () -> client.request("/models", null),
        result -> {
          if (api != client || !selected.equals(id) || conversation == null
              || !model.equals(conversation.optString("model"))) return;
          JSONArray models = (JSONArray) result;
          JSONObject selectedModel = null;
          for (int i = 0; i < models.length(); i++) {
            JSONObject candidate = models.optJSONObject(i);
            if (candidate != null && model.equals(candidate.optString("model"))) {
              selectedModel = candidate;
              break;
            }
          }
          JSONObject chosenModel = selectedModel;
          JSONArray supported = chosenModel == null ? null : chosenModel.optJSONArray("supportedReasoningEfforts");
          if (supported == null || supported.length() == 0) {
            problem("This Codex version did not advertise reasoning levels for the selected model.");
            return;
          }
          String[] labels = new String[supported.length() + 1];
          labels[0] = "Use default";
          int checked = 0;
          for (int i = 0; i < supported.length(); i++) {
            Object value = supported.opt(i);
            if (!(value instanceof String) || ((String) value).trim().isEmpty()) {
              problem("The server returned an invalid reasoning level. Refresh and try again.");
              return;
            }
            labels[i + 1] = (String) value;
            if (labels[i + 1].equalsIgnoreCase(currentEffort)) checked = i + 1;
          }
          new AlertDialog.Builder(this)
              .setTitle("Reasoning effort")
              .setSingleChoiceItems(labels, checked, (dialog, which) -> {
                String effort = which == 0 ? null : labels[which];
                action(
                    () -> client.request("/conversations/" + ApiClient.segment(id) + "/reasoning", new JSONObject().put("effort", effort == null ? JSONObject.NULL : effort)),
                    state -> { if (selected.equals(id)) renderConversation((JSONObject) state); notice("Reasoning effort selected for future messages."); dialog.dismiss(); },
                    false);
              })
              .setNegativeButton("Cancel", null)
              .show();
        },
        false);
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

  private void showMenu() {
    new AlertDialog.Builder(this)
        .setTitle(serverName)
        .setItems(
            new String[] {
              "Servers",
              "Schedule message",
              "Scheduled messages",
              "Usage and resets",
              "Refresh",
              "Choose model",
              "Choose reasoning",
              "Archive conversation",
              "Archived conversations",
              "Unpair this server"
            },
            (d, which) -> {
              if (which == 0) showServers();
              else if (which == 1) scheduleMessage();
              else if (which == 2) showScheduled();
              else if (which == 3) showUsage();
              else if (which == 4) refresh();
              else if (which == 5) showModels();
              else if (which == 6) showReasoning();
              else if (which == 7) archive(selected);
              else if (which == 8) showArchived();
              else {
                ApiClient client = api;
                action(
                    () -> client.request("/logout", new JSONObject()),
                    ignored -> {
                      try {
                        store.remove(client.origin);
                      } catch (Exception ignoredError) {
                      }
                      api = null;
                      conversation = null;
                      selected = "";
                      send = null;
                      showPairing();
                    },
                    false);
              }
            })
        .setNegativeButton("Close", null)
        .show();
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
                  api = null;
                  selected = "";
                  conversation = null;
                  send = null;
                  polling = false;
                  submitting = false;
                  showPairing();
                  return;
                }
                try {
                  JSONObject saved =
                      store.activate(servers.getJSONObject(index).getString("origin"));
                  api =
                      new ApiClient(
                          saved.getString("origin"),
                          saved.getString("token"),
                          saved.optString("pin"));
                  serverName = saved.optString("name", api.origin);
                  selected = store.selected(api.origin);
                  conversation = null;
                  answers.clear();
                  showChat();
                  ui.removeCallbacks(poll);
                  ui.post(poll);
                } catch (Exception exception) {
                  problem("This saved server could not be opened. Pair with it again.");
                }
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
    String value =
        "5h: "
            + remaining(usageState == null ? null : usageState.optJSONObject("fiveHour"))
            + " · Weekly: "
            + remaining(usageState == null ? null : usageState.optJSONObject("weekly"));
    usageView.setText(value);
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
    JSONObject five = usageState == null ? null : usageState.optJSONObject("fiveHour"),
        week = usageState == null ? null : usageState.optJSONObject("weekly");
    new AlertDialog.Builder(this)
        .setTitle("Usage remaining")
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
    if (api == null
        || conversation == null
        || !connected
        || composer.getText().toString().trim().isEmpty()) {
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
    if (api == null || conversation == null) return;
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
                    + " · "
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
                                    ignored -> notice("Scheduled message canceled."),
                                    false))
                        .show();
                  })
              .setNegativeButton("Close", null)
              .show();
        },
        false);
  }

  private String safeError(Exception exception) {
    if (exception instanceof ApiClient.ApiError || exception instanceof IllegalArgumentException)
      return exception.getMessage();
    if (exception instanceof javax.net.ssl.SSLException)
      return "Desktop identity or HTTPS verification failed. Check the phone clock and server"
                 + " address. If the desktop key changed, scan a fresh QR on that desktop. No"
                 + " connection was trusted automatically.";
    return "Could not reach the desktop. Check the IP and port, that the server is running, router"
        + " TCP forwarding, and Windows Firewall. Test over cellular; some routers cannot"
        + " access their own public IP from Wi-Fi.";
  }
}
