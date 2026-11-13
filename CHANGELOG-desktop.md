# PocketAgent desktop and server changelog

This file covers the Windows desktop application and the local PocketAgent server.

## Unreleased

## 0.3.9 - 2026-11-12

- Fixed image references with spaces, nested provider URL objects, and percent-encoded data payloads so the desktop viewer receives the actual image source.
- Fixed updater staging cleanup by disposing the downloaded archive stream before removing its temporary transaction directory.
- Added a mandatory disposable installed-app updater test to the desktop release workflow; it verifies replacement, restart health, and staging cleanup before packaging can publish.

## 0.3.8 - 2026-11-12

- Reduced the conversation context bar to status and elapsed work only, shrinking its footprint and hiding Stop while idle.
- Made the context bar identify the idle/no-selection state instead of rendering an empty strip.
- Replaced conversation action glyphs with the Android-style edit, robot, pin, archive, unarchive, and stop vector icons.
- Softened the Send hover color so it stays close to the primary pale-green surface.
- Fixed structured provider image URLs (`image_url.url`/`uri`) being dropped before the desktop viewer received them; desktop checks now exercise byte loading, WPF decoding, and the modal viewer path.
- Unsupported embedded formats now get an explicit desktop dialog with an external-viewer action instead of failing silently.

## 0.3.7 - 2026-11-12

- Compacted the Sessions header: conversation actions now sit beside New conversation, the selected directory appears beside the server machine, and the redundant conversation title strip is removed.
- Restored the effective model and reasoning labels to the visible conversation context; Codex-configured/default model information is loaded when the server starts.
- Added the desktop pin/unpin control for persisted conversation pin state.
- Fixed inactive-conversation maintenance incorrectly archiving recent conversations created with legacy seconds-based timestamps.
- Kept the Send action label stable during active turns and shortened the interruption action to Stop.
- Added a mandatory pre-release test gate that blocks commits, packaging, and publication when focused session, network, or desktop checks fail.

## 0.3.6 - 2026-11-12

- Replaced the separate Start/Stop server buttons with one ON/OFF switch and removed the redundant Menu button; Sessions, Devices, and Settings remain top-level destinations.
- Added a compact 64-by-26 server switch with a matte sliding knob, recessed track, centered ON/OFF labels, and a short slide animation: OFF has the knob on the left and an unlit track; ON has the knob on the right and green illumination contained inside the track.
- Unified Sessions, Devices, Settings, and the server switch into one top-right header row; removed repeated conversation titles and the redundant global server-status line to reclaim vertical space.
- Moved the server machine name into the global header and left only the working directory in the selected conversation context line.
- Removed the duplicate working-directory text from the global server status; the selected conversation header remains the single directory context.
- Hardened desktop image viewing for file URIs and encoded data URIs, preserved viewed-image labels, and added expand/restore plus external-viewer actions.
- Server now persists conversation pin state/message activity and automatically archives unpinned conversations after seven days without a user or agent message.

## 0.3.5 - 2026-11-12

- Added persisted Compact (90%), Comfortable (100%), Plus (110%), and Large (120%) font/UI scale presets.
- Moved conversation actions into the selected conversation pane title strip and replaced text-heavy controls with compact rename, robot/model, archive, and archived-history actions.
- Hardened the desktop updater replacement flow so an installed application directory is updated file-by-file without attempting to rename the live install root.

## 0.3.4 - 2026-11-11

- Added image-reference actions to transcript rows so generated workspace images can be opened and identified after viewing.
- Added Compact (90%), Comfortable (100%), Plus (110%), and Large (120%) persisted UI scale presets.
- Hide the server's automatic five-minute `update requested` heartbeat prompt while keeping its progress response visible.
- Added image-reference actions to transcript rows so generated workspace images can be opened and identified after viewing.
- Hide the server's automatic five-minute `update requested` heartbeat prompt while keeping its progress response visible.

## 0.3.3 - 2026-11-11

- Fixed desktop update checks so `desktop-v<version>` releases are compared against the running version.

## 0.3.2 - 2026-11-11

- Fixed the schedule dialog crash caused by initializing the hour selector with
  an invalid one-character date format.
- Removed the nonessential older-history notice from the conversation status.

## 0.3.1 - 2026-11-11

- Added a persisted desktop toggle for five-minute hidden progress heartbeats, enabled by default.
- Compact desktop usage summaries now show remaining percentage and the next reset in local time; hover still shows full reset timestamps.

## 0.3.0 - 2026-11-11

- Added a top-level desktop Menu for Sessions, Devices, general settings, update checks, release notes, and version information.
- Expanded the README and durable project documentation for first-time users and the direct-connection security model.

## 0.2.2 - 2026-11-11

- Fixed the desktop updater handoff so the bundled updater launches directly with the correct working directory.
- Added updater diagnostics at `%TEMP%\PocketAgent-update.log` for download, checksum, replacement, restart, and rollback failures.
- Added a desktop release-notes button when a newer version is available.

## 0.2.1 - 2026-11-11

- Fixed the desktop schedule control so it is available as soon as a message is drafted.
- Added a compact scheduled-message summary beneath the send controls.
- Kept the monotone schedule icon and made the scheduling dialog reachable without a crash.
- Published the signed desktop installer, portable ZIP, updater, and checksums alongside the Android APK.

## 0.2.0 - 2026-11-10

- Added packaged desktop distribution with an installer and portable ZIP.
- Added the separate updater process, startup/update settings, safe idle update checks, and health-file rollback protection.
- Added persistent Windows Firewall setup for the configured listener port.
- Added multi-server connections, per-conversation working directories, model/reasoning selection, scheduling, steering, usage display, and archive/restore controls.
- Added direct IP-and-port internet setup with QR or full pairing-string enrollment.

