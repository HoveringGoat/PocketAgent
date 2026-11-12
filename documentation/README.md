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

Android's current UI uses a gear button for a full Settings page. The page lists all paired servers uniformly with per-server actions and add-server access; paired servers remain connected and poll independently. Usage and reset details are opened from the relevant server card, while application information and update controls are shown inline. Conversation tabs visually join the open pane, whose title strip contains rename, model/reasoning, archive, and archived-history actions. Scheduling is created only with the timer button beside Send. Pending schedules appear as a compact notice below Send; selecting it opens the review/cancel dialog.

The desktop Sessions view uses the same pane model: conversation tabs are title-only, and the selected pane title strip owns rename, robot/model, archive, and archived-history actions. Android's selected tab now joins the pane surface; its branded top bar exposes the current server selector, a separate add-server control, and settings, with the server machine shown beneath. The working directory remains in the pane header, status/model context stays above the composer, and the conversation title is not repeated there. Scheduling sits below the filled Send action. Android and desktop main Settings pages include persisted Compact (90%), Comfortable (100%), Plus (110%), and Large (120%) font/UI scale presets.

Current release: desktop/server 0.3.5 and Android 0.3.5. Scan the QR or paste its complete pairing string; no second code, CA installation, Let's Encrypt, ports 80/443 or Caddy changes. Releases are grouped under artifacts/releases/<version> with separate installer, portable desktop and Android outputs. The 0.3.5 release adds the branded Android server selector/conversation layout and hardens desktop updater replacement. See setup for focused verification and remaining real-device acceptance.

Version 0.1.4 adds six-digit pairing, a folder and model per conversation, archive/restore, direct IP-and-port HTTPS setup/diagnostics, distinct outgoing bubbles, and fixes for Unicode decoding and incoming-output focus loss. Earlier native features include usage, scheduling, saved hosts, steering/timing and desktop permissions. See [direct internet setup](SETUP.md#direct-internet-ip-and-port). The setup guide distinguishes focused/emulator/read-only-provider checks from remaining physical-device, external-network and real-inference acceptance. Historical 0.1.0/0.1.1 scope remains in the project brief.

When adding a page, link it from both this page and [TABLEOFCONTENTS.md](TABLEOFCONTENTS.md).
