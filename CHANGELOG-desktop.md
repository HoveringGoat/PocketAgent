# PocketAgent desktop and server changelog

This file covers the Windows desktop application and the local PocketAgent server.

## Unreleased

- Server now persists conversation pin state/message activity and automatically archives unpinned conversations after seven days without a user or agent message.
- Added a persisted Appearance setting with Compact (90%), Comfortable (100%), Plus (110%), and Large (120%) font/UI scale presets.
- Moved conversation actions into the selected conversation pane title strip and replaced text-heavy controls with compact rename, robot/model, archive, and archived-history actions.

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

