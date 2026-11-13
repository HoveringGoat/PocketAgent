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

Android's current UI uses a gear button for a full Settings page. The page lists all paired servers uniformly with per-server actions and add-server access; paired servers remain connected and poll independently. Usage and reset details are opened from the relevant server card, while application information and update controls are shown inline. Conversation tabs visually join the open pane, whose title strip contains rename, model/reasoning, archive, and archived-history actions. Scheduling is created or reviewed from the timer button beside Send; a pending count appears beside the timer when messages are scheduled, and tapping it with an empty composer opens the review/cancel dialog.

The desktop Sessions view uses the same pane model: conversation tabs are title-only, and rename, robot/model, pin, archive, and archived-history actions sit directly beside the new-conversation button. The global header shows the server machine and selected working directory together, so the pane does not need a duplicate title strip. Android's selected tab now joins the pane surface with an outlined top/side boundary; inactive tabs retain a lower divider. Its branded top bar exposes the current server selector and settings, with add-server available from the selector. Usage is shown under branding with five-hour reset time and weekly reset date. The status/model context, composer, Send, and timer controls share one full-width bottom control panel with a single subtle top separator; the conversation title is represented by its tab. Scheduling sits below the filled Send action with its pending count in the timer control. Android and desktop main Settings pages include persisted Compact (90%), Comfortable (100%), Plus (110%), and Large (120%) font/UI scale presets.

Android keeps the primary action labeled Send while an agent is running; transient warnings can be tapped away and automatically disappear after seven seconds.

Desktop keeps Sessions, Devices, Settings, and the server switch in one top-right header row. The server switch has a recessed track, thin rim, and large matte knob with a short shadow: OFF places the knob on the left with no illumination; ON places it on the right and lights the track green inside the rim. Centered ON/OFF labels use medium weight, and the knob slides in 140 ms. Starting/stopping temporarily disables the control; the server lifecycle handler restores its state after failure or cancellation. The template in MainWindow.xaml defines its appearance, and MainWindow.xaml.cs owns lifecycle state. The old Menu and separate Start/Stop buttons are gone. The server machine name appears in the global header, the working directory appears once in the selected conversation context line, and the conversation title is represented by its tab. Desktop image references support local files and data URIs, with viewed-image state, expand/restore, and external-viewer actions.

The desktop server switch measures 64 by 26 device-independent units, with a 20-unit knob and centered 11-unit labels. It aligns to the top of the header row beside the navigation buttons; Appearance scaling applies to the entire header.

Android conversation tabs are ordered by latest message, with pinned conversations first. The tab strip uses left/right chevrons to move one tab at a time when the row overflows; long-pressing the right chevron opens the complete conversation list. The server records pin state and automatically archives unpinned conversations after seven days without a user or agent message.

Current release: desktop/server 0.3.7 and Android 0.4.2. Scan the QR or paste its complete pairing string; no second code, CA installation, Let's Encrypt, ports 80/443 or Caddy changes. Releases are grouped under artifacts/releases/<version> with separate installer, portable desktop and Android outputs. Desktop/server 0.3.7 adds the compact navigation/power control, desktop image-viewer improvements, server support for Android pinning/activity, and corrected inactive-conversation archiving for legacy sessions. Android 0.4.2 adds message-timestamp tab ordering, pinned tabs, and one-tab overflow navigation. See setup for focused verification and remaining real-device acceptance.

Version 0.1.4 adds six-digit pairing, a folder and model per conversation, archive/restore, direct IP-and-port HTTPS setup/diagnostics, distinct outgoing bubbles, and fixes for Unicode decoding and incoming-output focus loss. Earlier native features include usage, scheduling, saved hosts, steering/timing and desktop permissions. See [direct internet setup](SETUP.md#direct-internet-ip-and-port). The setup guide distinguishes focused/emulator/read-only-provider checks from remaining physical-device, external-network and real-inference acceptance. Historical 0.1.0/0.1.1 scope remains in the project brief.

When adding a page, link it from both this page and [TABLEOFCONTENTS.md](TABLEOFCONTENTS.md).
