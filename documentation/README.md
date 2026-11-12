# Documentation

[Repository](../README.md) | [Complete contents](TABLEOFCONTENTS.md)

## Start here

[Project brief](PROJECT.md) describes the intended experience, component responsibilities, open decisions, and first milestone discussion.

[Packaged app requirements](PROJECT.md#packaged-app-design-requirements) records the approved direction: native distributable apps without bundled secrets, internet connectivity, address-and-code onboarding, desktop interaction, a minimal tabbed client, directory-based identity, steering, usage, scheduling, timing and multiple saved server connections.

[Working agreement](../AGENTS.md) defines planning approval, documentation, validation, and deployment expectations.

[Runtime architecture](ARCHITECTURE.md) describes authentication, session ownership, networking, recovery, and artifact boundaries. [Local setup](SETUP.md) contains build and phone connection instructions.

[Release convention](RELEASE-CONVENTION.md) is a short template for adding
platform-specific changelogs, guarded release commits, immutable tags, and
versioned artifacts to another repository.

[Code and flow audit](AUDIT.md) records the 2026-11-11 review, reproduced updater/release defects, flow boundaries, and remaining verification work. Its findings qualify earlier release verification claims.

[Desktop/server changelog](../CHANGELOG-desktop.md) and [Android changelog](../CHANGELOG-android.md) are maintained separately. Release notes are assembled from both files and shown by packaged clients when a newer GitHub release is available.

[Use the native apps](SETUP.md#use-the-packaged-native-apps), [desktop permissions](SETUP.md#desktop-setup-and-permissions), [direct internet](SETUP.md#direct-internet-ip-and-port), [Wi-Fi](SETUP.md#connect-over-wi-fi) and [scan/paste pairing](SETUP.md#pair-by-qr-or-pairing-string) are the current setup paths.

## Documentation status

Android's current UI uses a gear button for a full Settings page. The page lists all paired servers uniformly with per-server actions and add-server access; paired servers remain connected and poll independently. Application information and update controls are shown inline. Conversation actions live on each conversation tab, and scheduling is created only with the timer button beside Send. Pending schedules appear as a compact notice below Send; selecting it opens the review/cancel dialog.

Current releases: desktop/server 0.3.3 and Android 0.3.1. Scan the QR or paste its complete pairing string; no second code, CA installation, Let's Encrypt, ports 80/443 or Caddy changes. Releases are grouped under artifacts/releases/<version> with separate installer, portable desktop and Android outputs. The 0.3.3 desktop patch fixes platform release-tag parsing in update checks; the Android 0.3.1 patch restores model/reasoning context and groups overflow actions. See setup for focused verification and remaining real-device acceptance.

Version 0.1.4 adds six-digit pairing, a folder and model per conversation, archive/restore, direct IP-and-port HTTPS setup/diagnostics, distinct outgoing bubbles, and fixes for Unicode decoding and incoming-output focus loss. Earlier native features include usage, scheduling, saved hosts, steering/timing and desktop permissions. See [direct internet setup](SETUP.md#direct-internet-ip-and-port). The setup guide distinguishes focused/emulator/read-only-provider checks from remaining physical-device, external-network and real-inference acceptance. Historical 0.1.0/0.1.1 scope remains in the project brief.

When adding a page, link it from both this page and [TABLEOFCONTENTS.md](TABLEOFCONTENTS.md).
