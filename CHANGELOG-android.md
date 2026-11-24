# PocketAgent Android changelog

This file covers the Android companion application.

## Unreleased

## 0.4.11 - 2026-11-24

- Match the model and reasoning dialog and dropdowns to the app theme.

## 0.4.10 - 2026-11-23

- Edit a conversation's model and reasoning in one dropdown dialog and show the effective choices in the conversation status. Saving both settings requires desktop/server 0.3.42 or later.

## 0.4.9 - 2026-11-22

- Preserve Android upgrade compatibility by keeping the release signing identity in a durable user-level location outside the repository and refusing to silently generate a replacement key.

## 0.4.8 - 2026-11-22

- Add a confirmed **Reset conversation** action that starts a clean Codex thread with the current conversation settings and keeps the previous transcript in Archived History.
- Fix the Android pin action icon so it renders as a push pin instead of a malformed wand shape.

## 0.4.7 - 2026-11-21

- Expand Android connection errors with DNS, route, TCP timeout/refusal, TLS, and server-response diagnostics.

## 0.4.6 - 2026-11-20

- Pair with the Codex desktop through a relay QR and use the same conversation and device controls over the encrypted relay connection.

## 0.4.5 - 2026-11-19

- Prevent model/reasoning and new-conversation folder flows from crashing when a desktop response is empty or does not match the expected JSON schema.
- Validate directory and model payloads before opening selection dialogs and refresh safely when a mutation response cannot include a snapshot.
- Guard the shared asynchronous response callback so an unexpected remote payload is shown as a recoverable error instead of terminating the app.

## 0.4.4 - 2026-11-16

- Allow archiving and restoring an idle conversation from PocketAgent metadata when its Codex thread is unavailable.
- Limit Android session lists and conversation operations to conversations granted to that paired device.

## 0.4.3 - 2026-11-15

- Keep sessions and usage visible when a selected Codex thread is unavailable; preserve the local conversation metadata and show the conversation as disconnected instead of aborting the entire refresh.
- Do not automatically recreate or resend messages for a missing thread.

## 0.4.2 - 2026-11-12

- Added message-timestamp tab ordering with pinned-first tabs, one-tab left/right overflow controls, explicit server-selector chevron, and pin/unpin actions.
- Added server-backed seven-day inactivity archiving for unpinned conversations; pinned conversations are exempt.

## 0.4.1 - 2026-11-12

- Keep the Send action label stable while steering an active agent.
- Auto-dismiss transient warnings after seven seconds, with tap-to-dismiss support.
- Fixed the live conversation screen using a stale tab renderer instead of the shared active-tab/divider treatment used by the visual preview.
- Added symmetric composer padding so the Send and scheduled-message controls stay visibly centered beside the input instead of crowding the running-status row.

## 0.4.0 - 2026-11-12

- Added a persisted Appearance setting with Compact (90%), Comfortable (100%), Plus (110%), and Large (120%) font/UI scale presets.
- Added image-reference actions to transcript rows so generated workspace images can be opened and identified after viewing.
- Hide the server's automatic five-minute `update requested` heartbeat prompt while keeping its progress response visible.
- Moved usage and reset details into each server card so quotas stay scoped to the server account.
- Reworked conversation tabs into a binder-style pane with selected-conversation actions in the pane title strip.
- Restyled the conversation pane to visually join the selected tab, added a branded top bar with server switching and selector-based add-server access, and kept only the working directory in the compact pane header.
- Moved expanded usage remaining details under branding, including the five-hour reset time and weekly reset date, and reduced the status strip height.
- Moved status/model context above the composer without repeating the conversation title.
- Moved scheduling below the filled Send action and increased the composer height for clearer primary/secondary actions.
- Tightened the branded header, changed Stop to a compact symbol, moved pending schedule counts into the timer control, and added a clear active-tab-to-pane boundary.
- Bundled usage into the branding header and matched the Send/timer column height to the composer with explicit spacing.
- Reduced tab and new-conversation control height/radius, made Settings icon-only, and removed the active-tab outline overlap.
- Unified the running status and composer into one full-width bottom control panel and removed the composer field's individual outline.
- Added a subtle lighter tone to the integrated status row and a little extra separation above the Send/timer controls.
- Removed the integrated control panel's rounded outer border, keeping only a subtle top separator so the dock reads as part of the conversation pane.
- Lowered the Send/timer column slightly and updated the model action to use a cleaner antenna-and-face robot vector matching the conversation mockup.
- Centered the Send/timer button group vertically beside the composer instead of stretching it from the status-row edge, using a dedicated full-height button host.
- Reclaimed the conversation root's extra top padding and reduced the centered action group slightly so the controls have visible breathing room around the status row.
- Integrated the New conversation `+` action into the tab strip with a compact, dark outlined treatment.
- Replaced the New conversation outline with a true icon-only plus action and replaced the settings glyph with a dedicated vector gear.
- Refined the vector controls with a slimmer bounded gear silhouette and a heavier plus stroke for better small-size readability.
- Made usage details fall back to a compact percentage-only line on narrow headers, tightened the working-directory row without shrinking its action targets, and aligned tab highlight strokes to a shared hairline divider.
- Removed duplicate per-tab bottom strokes so the conversation strip uses one continuous divider beneath the tabs.
- Opened the active tab into the conversation pane by masking the shared divider beneath it; inactive tabs retain the single consistent divider.
- Made the divider gap geometry-aware so the shared line is explicitly split around the active tab instead of relying on child overdraw.
- Added a debug-only mocked conversation preview and screenshot runner so visual changes can be reviewed before release.

## 0.3.5 - 2026-11-12

- Added persisted Compact (90%), Comfortable (100%), Plus (110%), and Large (120%) font/UI scale presets.
- Added a branded top bar with a current-server selector, separate add-server control, and settings access.
- Restyled the conversation pane so the selected tab joins the pane, the working directory stays in the pane header, and the conversation title is not repeated.
- Moved status/model context above the composer and placed the icon-only scheduling action below the filled Send button.

## 0.3.4 - 2026-11-11

- Added image-reference actions to transcript rows so generated workspace images can be opened and identified after viewing.
- Added Compact (90%), Comfortable (100%), Plus (110%), and Large (120%) persisted UI scale presets.
- Hide the server's automatic five-minute `update requested` heartbeat prompt while keeping its progress response visible.

## 0.3.3 - 2026-11-11

- Reworked Settings into a full page with current/saved server information, per-server actions, add-server access, and inline application details.
- Kept all paired servers connected and polling independently so work and personal machines can be used at the same time.

## 0.3.2 - 2026-11-11

- Removed the nonessential older-history notice from the conversation status.
- Replaced the overflow menu with a gear-based settings page and moved conversation actions onto each tab row.
- Made the timer button the only scheduling entry point and added an in-context scheduled-message summary with review/cancel access.

## 0.3.1 - 2026-11-11

- Restored the selected model and reasoning effort to the conversation status line.
- Decluttered the overflow menu into Connection, Conversation, Scheduling, and App submenus.

## 0.3.0 - 2026-11-11

- Added app settings with a persisted startup update-check preference, manual update checks, and version information.
- Added the same top-level app menu affordance for server switching, scheduling, usage, model/reasoning controls, and conversation actions.
- Replaced remaining corrupted status separators with readable ASCII separators.

## 0.2.2 - 2026-11-11

- Published the signed Android APK alongside the desktop updater fix.
- Kept startup update checks and release-note display available from GitHub release metadata.

## 0.2.1 - 2026-11-11

- Published the signed Android APK with the desktop/server patch release.
- Kept startup update checks and release-note display available from the GitHub release metadata.

## 0.2.0 - 2026-11-10

- Added saved server connections and switching between machines.
- Added per-conversation working-directory and model/reasoning selection.
- Added QR or full pairing-string enrollment, steering during active work, usage display, scheduling, and archive/restore controls.
- Added immediate outgoing-message rendering and safer polling while a response is running.

