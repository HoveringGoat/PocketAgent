# PocketAgent Android changelog

This file covers the Android companion application.

## Unreleased

- Added a persisted Appearance setting with Compact (90%), Comfortable (100%), Plus (110%), and Large (120%) font/UI scale presets.
- Added image-reference actions to transcript rows so generated workspace images can be opened and identified after viewing.
- Hide the server's automatic five-minute `update requested` heartbeat prompt while keeping its progress response visible.
- Moved usage and reset details into each server card so quotas stay scoped to the server account.
- Reworked conversation tabs into a binder-style pane with selected-conversation actions in the pane title strip.

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

