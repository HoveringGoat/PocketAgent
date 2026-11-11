# Documentation

[Repository](../README.md) | [Complete contents](TABLEOFCONTENTS.md)

## Start here

[Project brief](PROJECT.md) describes the intended experience, component responsibilities, open decisions, and first milestone discussion.

[Packaged app requirements](PROJECT.md#packaged-app-design-requirements) records the approved direction: native distributable apps without bundled secrets, internet connectivity, address-and-code onboarding, desktop interaction, a minimal tabbed client, directory-based identity, steering, usage, scheduling, timing and multiple saved server connections.

[Working agreement](../AGENTS.md) defines planning approval, documentation, validation, and deployment expectations.

[Runtime architecture](ARCHITECTURE.md) describes authentication, session ownership, networking, recovery, and artifact boundaries. [Local setup](SETUP.md) contains build and phone connection instructions.

[Use the native apps](SETUP.md#use-the-packaged-native-apps), [desktop permissions](SETUP.md#desktop-setup-and-permissions), [Wi-Fi](SETUP.md#connect-over-wi-fi), [temporary internet](SETUP.md#temporary-internet-trial), and [stable internet](SETUP.md#stable-internet-connection) are the setup paths. The server does not serve the former browser client.

## Documentation status

The current package is 0.1.9: dark scrollbar/window surfaces and application confirmation dialogs, plus instructions for sharing port 80 with an existing Caddy server. Version 0.1.8 added automatic Let's Encrypt IP certificates and renewal. Existing phone clients remain compatible. See [direct setup](SETUP.md#direct-internet-ip-and-port) for router rules, renewal requirements and verification limits.

Version 0.1.4 adds six-digit pairing, a folder and model per conversation, archive/restore, direct IP-and-port HTTPS setup/diagnostics, distinct outgoing bubbles, and fixes for Unicode decoding and incoming-output focus loss. Earlier native features include usage, scheduling, saved hosts, steering/timing and desktop permissions. See [direct internet setup](SETUP.md#direct-internet-ip-and-port). The setup guide distinguishes focused/emulator/read-only-provider checks from remaining physical-device, external-network and real-inference acceptance. Historical 0.1.0/0.1.1 scope remains in the project brief.

When adding a page, link it from both this page and [TABLEOFCONTENTS.md](TABLEOFCONTENTS.md).
