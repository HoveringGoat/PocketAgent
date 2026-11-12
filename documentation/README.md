# Documentation

[Repository](../README.md) | [Complete contents](TABLEOFCONTENTS.md)

## Start here

[Project brief](PROJECT.md) describes the intended experience, component responsibilities, open decisions, and first milestone discussion.

[Packaged app requirements](PROJECT.md#packaged-app-design-requirements) records the approved direction: native distributable apps without bundled secrets, internet connectivity, address-and-code onboarding, desktop interaction, a minimal tabbed client, directory-based identity, steering, usage, scheduling, timing and multiple saved server connections.

[Working agreement](../AGENTS.md) defines planning approval, documentation, validation, and deployment expectations.

[Runtime architecture](ARCHITECTURE.md) describes authentication, session ownership, networking, recovery, and artifact boundaries. [Local setup](SETUP.md) contains build and phone connection instructions.

[Code and flow audit](AUDIT.md) records the 2026-11-11 review, reproduced updater/release defects, flow boundaries, and remaining verification work. Its findings qualify earlier release verification claims.

[Desktop/server changelog](../CHANGELOG-desktop.md) and [Android changelog](../CHANGELOG-android.md) are maintained separately. Release notes are assembled from both files and shown by packaged clients when a newer GitHub release is available.

[Use the native apps](SETUP.md#use-the-packaged-native-apps), [desktop permissions](SETUP.md#desktop-setup-and-permissions), [direct internet](SETUP.md#direct-internet-ip-and-port), [Wi-Fi](SETUP.md#connect-over-wi-fi) and [scan/paste pairing](SETUP.md#pair-by-qr-or-pairing-string) are the current setup paths.

## Documentation status

Current release: 0.2.2. Scan the QR or paste its complete pairing string; no second code, CA installation, Let's Encrypt, ports 80/443 or Caddy changes. Releases are grouped under artifacts/releases/<version> with separate installer, portable desktop and Android outputs. The 0.2.2 patch release includes the direct updater handoff, updater diagnostics, release-note display, stable port-based Windows Firewall rule, and the monochrome scheduling control. See setup for focused verification and remaining real-device acceptance.

Version 0.1.4 adds six-digit pairing, a folder and model per conversation, archive/restore, direct IP-and-port HTTPS setup/diagnostics, distinct outgoing bubbles, and fixes for Unicode decoding and incoming-output focus loss. Earlier native features include usage, scheduling, saved hosts, steering/timing and desktop permissions. See [direct internet setup](SETUP.md#direct-internet-ip-and-port). The setup guide distinguishes focused/emulator/read-only-provider checks from remaining physical-device, external-network and real-inference acceptance. Historical 0.1.0/0.1.1 scope remains in the project brief.

When adding a page, link it from both this page and [TABLEOFCONTENTS.md](TABLEOFCONTENTS.md).
