package dev.pocketagent;

import android.app.Activity;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.ColorFilter;
import android.graphics.Paint;
import android.graphics.Path;
import android.graphics.PixelFormat;
import android.graphics.RectF;
import android.graphics.Typeface;
import android.graphics.drawable.Drawable;
import android.graphics.drawable.GradientDrawable;
import android.text.InputType;
import android.text.TextUtils;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.View;
import android.widget.Button;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.HorizontalScrollView;
import android.widget.ImageButton;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

/** The shared conversation surface used by the live client and debug preview. */
public final class ConversationScreenView {
  public static final int BACKGROUND = Color.rgb(17, 27, 23);
  public static final int SURFACE = Color.rgb(31, 47, 37);
  public static final int SURFACE_ALT = Color.rgb(25, 39, 31);
  public static final int TEXT = Color.rgb(235, 241, 234);
  public static final int MUTED = Color.rgb(143, 188, 143);
  public static final int SUBTLE = Color.rgb(178, 198, 181);
  public static final int ACCENT = Color.rgb(193, 218, 166);
  public static final int OUTLINE = Color.rgb(112, 145, 112);
  public static final int ACTIVE_OUTLINE = Color.rgb(167, 186, 167);
  private final Activity host;
  private final float scale;
  private LinearLayout tabRow;
  private HorizontalScrollView tabScroll;
  private View activeTab;

  public final LinearLayout root;
  public final LinearLayout tabs;
  public final LinearLayout transcript;
  public final LinearLayout metadata;
  public final LinearLayout contextSurface;
  public final LinearLayout controlPanel;
  public final TextView brand;
  public final TextView sessionLocation;
  public final TextView usageView;
  public final TextView context;
  public final TextView error;
  public final Button serverSelector;
  public final ImageButton tabBack;
  public final ImageButton newConversation;
  public final ImageButton tabOverflow;
  public final Button send;
  public final ImageButton stop;
  public final Button schedule;
  public final EditText composer;
  public final ImageButton renameAction;
  public final ImageButton pinAction;
  public final ImageButton modelAction;
  public final ImageButton archiveAction;
  public final ImageButton archivedAction;
  public final ScrollView transcriptScroll;

  public interface Actions {
    void selectServer();

    void openSettings();

    void newConversation();

    void showAllConversations();

    void renameConversation();

    void togglePin();

    void selectModel();

    void archiveConversation();

    void showArchived();

    void stopWork();

    void scheduleMessage();

    void loadOlderHistory();
  }

  public ConversationScreenView(Activity host, float scale, String serverName, Actions actions) {
    this.host = host;
    this.scale = scale;
    root = column();
    root.setBackgroundColor(BACKGROUND);
    root.setPadding(0, 0, 0, 0);

    LinearLayout header = row();
    LinearLayout brandIdentity = column();
    brand = label("PocketAgent", 16);
    brand.setTypeface(null, Typeface.BOLD);
    brand.setPadding(dp(4), dp(2), dp(4), dp(2));
    brandIdentity.addView(brand, match());
    header.addView(brandIdentity, new LinearLayout.LayoutParams(0, -2, 1));
    serverSelector = chip(serverName, v -> actions.selectServer(), "Current server");
    serverSelector.setCompoundDrawablesWithIntrinsicBounds(0, 0, R.drawable.ic_chevron_down, 0);
    serverSelector.setCompoundDrawablePadding(dp(4));
    serverSelector.setMinHeight(dp(36));
    serverSelector.setMinimumHeight(dp(36));
    header.addView(serverSelector);
    ImageButton settings = iconButton(R.drawable.ic_settings, v -> actions.openSettings(), "Settings");
    settings.setMinimumHeight(dp(36));
    settings.setMinimumWidth(dp(34));
    header.addView(settings);
    LinearLayout.LayoutParams headerLayout = match();
    headerLayout.setMargins(dp(12), 0, dp(12), 0);
    root.addView(header, headerLayout);

    tabRow = row();
    tabRow.setBackground(tabDivider());
    LinearLayout.LayoutParams tabRowLayout = match();
    tabRowLayout.setMargins(dp(12), 0, dp(12), 0);
    tabScroll = new HorizontalScrollView(host);
    tabScroll.setHorizontalScrollBarEnabled(false);
    tabScroll.setHorizontalFadingEdgeEnabled(true);
    tabScroll.setFadingEdgeLength(dp(28));
    tabScroll.setOnScrollChangeListener(
        (v, scrollX, scrollY, oldScrollX, oldScrollY) -> {
          tabRow.invalidate();
          updateTabOverflow();
        });
    tabBack = iconButton(R.drawable.ic_chevron_left, v -> shiftTabs(-1), "Earlier conversations");
    tabBack.setVisibility(View.GONE);
    tabBack.setMinimumWidth(dp(34));
    tabBack.setMinimumHeight(dp(34));
    tabRow.addView(tabBack, new LinearLayout.LayoutParams(-2, -2));
    tabs = row();
    tabs.setPadding(dp(3), dp(2), dp(3), 0);
    tabScroll.addView(tabs);
    tabRow.addView(tabScroll, new LinearLayout.LayoutParams(0, -2, 1));
    tabOverflow = iconButton(R.drawable.ic_chevron_right, v -> shiftTabs(1), "More conversations");
    tabOverflow.setOnLongClickListener(
        v -> {
          actions.showAllConversations();
          return true;
        });
    tabOverflow.setVisibility(View.GONE);
    tabOverflow.setMinimumWidth(dp(34));
    tabOverflow.setMinimumHeight(dp(34));
    LinearLayout.LayoutParams overflowLayout = new LinearLayout.LayoutParams(-2, -2);
    overflowLayout.setMargins(dp(2), 0, 0, 0);
    tabRow.addView(tabOverflow, overflowLayout);
    newConversation = iconButton(R.drawable.ic_add, v -> actions.newConversation(), "New conversation");
    newConversation.setMinimumWidth(dp(34));
    newConversation.setMinimumHeight(dp(34));
    LinearLayout.LayoutParams newConversationLayout = new LinearLayout.LayoutParams(-2, -2);
    newConversationLayout.setMargins(dp(3), 0, 0, 0);
    tabRow.addView(newConversation, newConversationLayout);
    root.addView(tabRow, tabRowLayout);

    usageView = label("5h: 80%  ·  Weekly: 20%", 11);
    usageView.setTextColor(SUBTLE);
    usageView.setContentDescription("Usage remaining");
    usageView.setText(host.getString(R.string.usage_placeholder));
    usageView.setPadding(dp(4), dp(1), dp(4), dp(1));
    usageView.setSingleLine(true);
    usageView.setEllipsize(TextUtils.TruncateAt.END);
    brandIdentity.addView(usageView, match());

    metadata = row();
    metadata.setContentDescription("Conversation location and actions");
    metadata.setPadding(dp(8), 0, dp(4), 0);
    metadata.setBackground(surface(SURFACE, 10, Color.TRANSPARENT));
    sessionLocation = label("", 11);
    sessionLocation.setTextColor(MUTED);
    sessionLocation.setPadding(dp(4), dp(1), dp(4), dp(1));
    sessionLocation.setMaxLines(1);
    sessionLocation.setEllipsize(TextUtils.TruncateAt.MIDDLE);
    metadata.addView(sessionLocation, new LinearLayout.LayoutParams(0, -2, 1));
    renameAction = iconButton(R.drawable.ic_edit, v -> actions.renameConversation(), "Rename conversation");
    pinAction = iconButton(R.drawable.ic_pin, v -> actions.togglePin(), "Pin conversation");
    modelAction = iconButton(R.drawable.ic_robot, v -> actions.selectModel(), "Model and reasoning");
    archiveAction = iconButton(R.drawable.ic_archive, v -> actions.archiveConversation(), "Archive conversation");
    archivedAction = iconButton(R.drawable.ic_unarchive, v -> actions.showArchived(), "View archived conversations");
    metadata.addView(renameAction);
    metadata.addView(pinAction);
    metadata.addView(modelAction);
    metadata.addView(archiveAction);
    metadata.addView(archivedAction);
    LinearLayout.LayoutParams metadataLayout = match();
    metadataLayout.setMargins(dp(12), 0, dp(12), 0);
    root.addView(metadata, metadataLayout);

    transcriptScroll = new ScrollView(host);
    transcriptScroll.setFillViewport(true);
    transcriptScroll.setBackgroundColor(BACKGROUND);
    transcript = column();
    transcript.setPadding(0, dp(8), 0, dp(8));
    transcriptScroll.addView(transcript);
    transcriptScroll.setOnScrollChangeListener(
        (view, scrollX, scrollY, oldScrollX, oldScrollY) -> {
          if (scrollY <= dp(24) && oldScrollY > scrollY) actions.loadOlderHistory();
        });
    LinearLayout.LayoutParams transcriptLayout = new LinearLayout.LayoutParams(-1, 0, 1);
    transcriptLayout.setMargins(dp(12), 0, dp(12), 0);
    root.addView(transcriptScroll, transcriptLayout);

    error = label("", 13);
    error.setTextColor(Color.rgb(245, 184, 165));
    error.setVisibility(View.GONE);
    LinearLayout.LayoutParams errorLayout = match();
    errorLayout.setMargins(dp(12), 0, dp(12), 0);
    root.addView(error, errorLayout);

    contextSurface = row();
    contextSurface.setContentDescription("Conversation status, model, reasoning, and permissions");
    contextSurface.setPadding(dp(12), dp(0), dp(14), dp(2));
    contextSurface.setBackgroundColor(SURFACE_ALT);
    context = label("Idle  ·  sol  xhigh", 12);
    context.setMaxLines(1);
    context.setPadding(dp(4), dp(2), dp(4), dp(2));
    context.setEllipsize(TextUtils.TruncateAt.END);
    context.setOnClickListener(v -> actions.selectModel());
    contextSurface.addView(context, new LinearLayout.LayoutParams(0, -2, 1));
    stop = iconButton(R.drawable.ic_stop, v -> actions.stopWork(), "Stop work");
    stop.setMinimumHeight(dp(32));
    stop.setPadding(dp(8), 0, dp(8), 0);
    stop.setVisibility(View.GONE);
    contextSurface.addView(stop);

    LinearLayout composeRow = row();
    composeRow.setGravity(Gravity.CENTER_VERTICAL);
    // Leave visible, symmetric breathing room around the action stack so it is
    // centered in the composer area instead of visually touching the status row.
    composeRow.setPadding(dp(12), dp(6), dp(12), dp(6));
    composer = input("Message Codex");
    composer.setContentDescription("Message Codex");
    composer.setMinLines(3);
    composer.setMaxLines(6);
    LinearLayout.LayoutParams composerLayout = new LinearLayout.LayoutParams(0, -2, 1);
    composerLayout.setMargins(0, 0, dp(6), 0);
    composeRow.addView(composer, composerLayout);
    LinearLayout sendColumn = column();
    send = primaryButton("Send", v -> {});
    send.setContentDescription("Send message");
    send.setMinHeight(dp(38));
    send.setMinimumHeight(dp(38));
    schedule = chip("⏱︎", v -> actions.scheduleMessage(), "Schedule message");
    schedule.setMinHeight(dp(38));
    schedule.setMinimumHeight(dp(38));
    LinearLayout.LayoutParams sendLayout = new LinearLayout.LayoutParams(-1, -2);
    sendLayout.setMargins(0, 0, 0, dp(3));
    sendColumn.addView(send, sendLayout);
    schedule.setText(host.getString(R.string.schedule_symbol));
    LinearLayout.LayoutParams scheduleLayout = new LinearLayout.LayoutParams(-1, -2);
    sendColumn.addView(schedule, scheduleLayout);
    FrameLayout sendHost = new FrameLayout(host);
    FrameLayout.LayoutParams sendColumnLayout =
        new FrameLayout.LayoutParams(-2, -2, Gravity.CENTER);
    sendHost.addView(sendColumn, sendColumnLayout);
    sendHost.setForegroundGravity(Gravity.CENTER);
    LinearLayout.LayoutParams sendHostLayout = new LinearLayout.LayoutParams(-2, -2);
    sendHostLayout.gravity = Gravity.CENTER_VERTICAL;
    sendHostLayout.setMargins(0, dp(2), 0, dp(2));
    composeRow.addView(sendHost, sendHostLayout);
    controlPanel = column();
    controlPanel.setContentDescription("Conversation controls");
    controlPanel.setPadding(0, dp(6), 0, dp(10));
    controlPanel.setBackgroundColor(BACKGROUND);
    View controlDivider = new View(host);
    controlDivider.setBackgroundColor(OUTLINE);
    controlPanel.addView(controlDivider, new LinearLayout.LayoutParams(-1, dp(1)));
    controlPanel.addView(contextSurface, match());
    controlPanel.addView(composeRow, match());
    root.addView(controlPanel, match());

  }

  public void addTab(String title, boolean active, View.OnClickListener click) {
    Button tab = chip(title, click, "Conversation " + title);
    tab.setTextColor(active ? TEXT : ACCENT);
    tab.setTypeface(null, active ? Typeface.BOLD : Typeface.NORMAL);
    tab.setBackground(tabSurface(active));
    tab.setMinHeight(dp(34));
    tab.setMinimumHeight(dp(34));
    tab.setPadding(dp(14), dp(4), dp(14), dp(4));
    LinearLayout.LayoutParams layout = new LinearLayout.LayoutParams(-2, -2);
    layout.setMargins(0, 0, dp(3), 0);
    tabs.addView(tab, layout);
    if (active) activeTab = tab;
    tabRow.invalidate();
    tabRow.post(() -> tabRow.invalidate());
    tabRow.post(this::updateTabOverflow);
  }

  public void clearTabs() {
    tabs.removeAllViews();
    activeTab = null;
    tabBack.setVisibility(View.GONE);
    tabOverflow.setVisibility(View.GONE);
    tabRow.invalidate();
  }

  public void updateTabOverflow() {
    if (tabScroll == null || tabBack == null || tabOverflow == null) return;
    if (tabs == null || tabs.getChildCount() == 0 || tabScroll.getWidth() <= 0) {
      tabBack.setVisibility(View.GONE);
      tabOverflow.setVisibility(View.GONE);
      return;
    }

    View lastTab = tabs.getChildAt(tabs.getChildCount() - 1);
    LinearLayout.LayoutParams lastLayout = (LinearLayout.LayoutParams) lastTab.getLayoutParams();
    int childContentWidth = lastTab.getRight() + lastLayout.rightMargin + tabs.getPaddingRight();
    int contentWidth = Math.max(tabs.getWidth(), childContentWidth);
    int scrollX = tabScroll.getScrollX();
    boolean earlier = scrollX > 0;
    boolean later = scrollX + tabScroll.getWidth() < contentWidth;
    tabBack.setVisibility(earlier ? View.VISIBLE : View.GONE);
    tabOverflow.setVisibility(later ? View.VISIBLE : View.GONE);
    tabRow.post(() -> tabRow.invalidate());
  }

  private void shiftTabs(int direction) {
    if (tabScroll == null || tabs == null || tabs.getChildCount() == 0) return;
    int current = tabScroll.getScrollX();
    View lastTab = tabs.getChildAt(tabs.getChildCount() - 1);
    LinearLayout.LayoutParams lastLayout = (LinearLayout.LayoutParams) lastTab.getLayoutParams();
    int childContentWidth = lastTab.getRight() + lastLayout.rightMargin + tabs.getPaddingRight();
    int contentWidth = Math.max(tabs.getWidth(), childContentWidth);
    int target = direction > 0 ? contentWidth : 0;
    if (direction > 0) {
      for (int i = 0; i < tabs.getChildCount(); i++) {
        View child = tabs.getChildAt(i);
        int left = Math.max(0, child.getLeft() - tabs.getPaddingLeft());
        if (left > current + 1) {
          target = left;
          break;
        }
      }
      target = Math.min(target, Math.max(0, contentWidth - tabScroll.getWidth()));
    } else {
      for (int i = tabs.getChildCount() - 1; i >= 0; i--) {
        View child = tabs.getChildAt(i);
        int left = Math.max(0, child.getLeft() - tabs.getPaddingLeft());
        if (left < current - 1) {
          target = left;
          break;
        }
      }
    }
    tabScroll.smoothScrollTo(target, 0);
  }

  private Drawable tabSurface(final boolean active) {
    return new Drawable() {
      private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);

      @Override
      public void draw(Canvas canvas) {
        RectF bounds = new RectF(getBounds());
        float radius = dp(5);
        float stroke = strokePx();
        paint.setStyle(Paint.Style.FILL);
        paint.setColor(active ? SURFACE : BACKGROUND);
        RectF fillBounds =
            new RectF(bounds.left, bounds.top, bounds.right, bounds.bottom - (active ? 0f : stroke / 2f));
        canvas.drawRoundRect(fillBounds, radius, radius, paint);
        paint.setStyle(Paint.Style.STROKE);
        paint.setStrokeWidth(stroke);
        paint.setStrokeCap(Paint.Cap.SQUARE);
        paint.setColor(active ? ACTIVE_OUTLINE : OUTLINE);
        if (active) {
          Path outline = new Path();
          float inset = stroke / 2f;
          float left = bounds.left + inset;
          float right = bounds.right - inset;
          float top = bounds.top + inset;
          float bottom = bounds.bottom;
          outline.moveTo(left, bottom);
          outline.lineTo(left, top + radius);
          outline.quadTo(left, top, left + radius, top);
          outline.lineTo(right - radius, top);
          outline.quadTo(right, top, right, top + radius);
          outline.lineTo(right, bottom);
          canvas.drawPath(outline, paint);
        }
      }

      @Override
      public void setAlpha(int alpha) {
        paint.setAlpha(alpha);
      }

      @Override
      public void setColorFilter(ColorFilter filter) {
        paint.setColorFilter(filter);
      }

      @Override
      public int getOpacity() {
        return PixelFormat.TRANSLUCENT;
      }
    };
  }

  private Drawable tabDivider() {
    return new Drawable() {
      private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);

      @Override
      public void draw(Canvas canvas) {
        paint.setColor(OUTLINE);
        float stroke = strokePx();
        paint.setStrokeWidth(stroke);
        float y = getBounds().bottom - stroke / 2f;
        if (activeTab == null || tabRow == null || tabScroll == null || activeTab.getWidth() <= 0) {
          canvas.drawLine(0, y, getBounds().right, y, paint);
          return;
        }
        int[] tabPosition = new int[2];
        int[] rowPosition = new int[2];
        activeTab.getLocationOnScreen(tabPosition);
        tabRow.getLocationOnScreen(rowPosition);
        float gapLeft = tabPosition[0] - rowPosition[0];
        float gapRight = gapLeft + activeTab.getWidth();
        gapLeft = Math.max(0, Math.min(getBounds().right, gapLeft));
        gapRight = Math.max(0, Math.min(getBounds().right, gapRight));
        paint.setStyle(Paint.Style.FILL);
        paint.setColor(SURFACE);
        canvas.drawRect(gapLeft, y - stroke / 2f, gapRight, getBounds().bottom, paint);
        paint.setStyle(Paint.Style.STROKE);
        paint.setColor(OUTLINE);
        canvas.drawLine(0, y, gapLeft, y, paint);
        canvas.drawLine(gapRight, y, getBounds().right, y, paint);
      }

      @Override
      public void setAlpha(int alpha) {
        paint.setAlpha(alpha);
      }

      @Override
      public void setColorFilter(ColorFilter filter) {
        paint.setColorFilter(filter);
      }

      @Override
      public int getOpacity() {
        return PixelFormat.TRANSLUCENT;
      }
    };
  }

  public TextView addMessage(String text, boolean outgoing, String timestamp) {
    LinearLayout row = new LinearLayout(host);
    row.setOrientation(LinearLayout.VERTICAL);
    row.setGravity(outgoing ? Gravity.END : Gravity.START);
    TextView message = label(text, 15);
    message.setTextColor(outgoing ? BACKGROUND : TEXT);
    message.setBackground(surface(outgoing ? ACCENT : SURFACE, 12, Color.TRANSPARENT));
    message.setPadding(dp(13), dp(9), dp(13), dp(9));
    LinearLayout.LayoutParams messageLayout = new LinearLayout.LayoutParams(-2, -2);
    messageLayout.setMargins(dp(4), dp(4), dp(4), 0);
    row.addView(message, messageLayout);
    if (timestamp != null && !timestamp.isEmpty()) {
      TextView time = label(timestamp, 10);
      time.setTextColor(MUTED);
      time.setPadding(dp(6), dp(2), dp(6), 0);
      row.addView(time);
    }
    transcript.addView(row, match());
    return message;
  }

  public void addWorkDuration(String text) {
    TextView duration = label(text, 10);
    duration.setTextColor(MUTED);
    duration.setGravity(Gravity.CENTER);
    duration.setPadding(dp(6), dp(10), dp(6), dp(4));
    transcript.addView(duration, match());
  }

  public void setConversationActionsVisible(boolean visible) {
    int state = visible ? View.VISIBLE : View.GONE;
    metadata.setVisibility(state);
    renameAction.setVisibility(state);
    pinAction.setVisibility(state);
    modelAction.setVisibility(state);
    archiveAction.setVisibility(state);
    archivedAction.setVisibility(state);
  }

  private TextView label(String text, int size) {
    TextView view = new TextView(host);
    view.setText(text);
    view.setTextColor(TEXT);
    view.setTextSize(TypedValue.COMPLEX_UNIT_SP, size * scale);
    view.setPadding(dp(8), dp(6), dp(8), dp(6));
    return view;
  }

  private EditText input(String hint) {
    EditText field = new EditText(host);
    field.setHint(hint);
    field.setTextColor(TEXT);
    field.setHintTextColor(Color.rgb(155, 173, 158));
    field.setTextSize(TypedValue.COMPLEX_UNIT_SP, 16 * scale);
    field.setInputType(
        InputType.TYPE_CLASS_TEXT
            | InputType.TYPE_TEXT_FLAG_MULTI_LINE
            | InputType.TYPE_TEXT_FLAG_CAP_SENTENCES);
    field.setPadding(dp(12), dp(10), dp(12), dp(10));
    field.setBackgroundColor(Color.TRANSPARENT);
    return field;
  }

  private Button chip(String text, View.OnClickListener click, String description) {
    Button button = new Button(host);
    button.setText(text);
    button.setAllCaps(false);
    button.setTextColor(ACCENT);
    button.setTextSize(TypedValue.COMPLEX_UNIT_SP, 13 * scale);
    button.setGravity(Gravity.CENTER);
    button.setOnClickListener(click);
    button.setContentDescription(description);
    button.setMinWidth(dp(44));
    button.setMinimumWidth(dp(44));
    button.setMinHeight(dp(42));
    button.setMinimumHeight(dp(42));
    button.setPadding(dp(10), 0, dp(10), 0);
    button.setBackground(surface(SURFACE_ALT, 9, OUTLINE));
    return button;
  }

  private Button iconTextButton(String text, View.OnClickListener click, String description) {
    Button button = chip(text, click, description);
    button.setTextSize(TypedValue.COMPLEX_UNIT_SP, 18 * scale);
    button.setPadding(dp(8), 0, dp(8), 0);
    return button;
  }

  private Button primaryButton(String text, View.OnClickListener click) {
    Button button = chip(text, click, text);
    button.setTextColor(BACKGROUND);
    button.setTypeface(null, Typeface.BOLD);
    button.setBackground(surface(ACCENT, 9, Color.TRANSPARENT));
    return button;
  }

  private ImageButton iconButton(int drawable, View.OnClickListener click, String description) {
    ImageButton button = new ImageButton(host);
    button.setImageResource(drawable);
    button.setContentDescription(description);
    button.setOnClickListener(click);
    button.setMinimumWidth(dp(34));
    button.setMinimumHeight(dp(34));
    button.setPadding(dp(6), dp(6), dp(6), dp(6));
    button.setScaleType(ImageButton.ScaleType.CENTER_INSIDE);
    button.setBackground(surface(Color.TRANSPARENT, 8, Color.TRANSPARENT));
    button.setColorFilter(ACCENT);
    return button;
  }

  private LinearLayout column() {
    LinearLayout result = new LinearLayout(host);
    result.setOrientation(LinearLayout.VERTICAL);
    return result;
  }

  private LinearLayout row() {
    LinearLayout result = new LinearLayout(host);
    result.setOrientation(LinearLayout.HORIZONTAL);
    result.setGravity(Gravity.CENTER_VERTICAL);
    return result;
  }

  private LinearLayout.LayoutParams match() {
    return new LinearLayout.LayoutParams(-1, -2);
  }

  private int dp(int value) {
    return Math.round(value * scale * host.getResources().getDisplayMetrics().density);
  }

  private float strokePx() {
    return Math.max(1f, scale * host.getResources().getDisplayMetrics().density * 0.5f);
  }

  private static GradientDrawable surface(int color, float radius, int stroke) {
    GradientDrawable drawable = new GradientDrawable();
    drawable.setColor(color);
    drawable.setCornerRadius(radius);
    if (stroke != Color.TRANSPARENT) drawable.setStroke(1, stroke);
    return drawable;
  }
}
