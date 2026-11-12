# PocketAgent desktop and server changelog

This file covers the Windows desktop application and the local PocketAgent server.

## 0.3.0 - Unreleased

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

