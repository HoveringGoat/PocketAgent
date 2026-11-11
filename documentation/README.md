# Documentation

[Repository](../README.md) | [Complete contents](TABLEOFCONTENTS.md)

## Start here

[Project brief](PROJECT.md) describes the intended experience, component responsibilities, open decisions, and first milestone discussion.

[Packaged app requirements](PROJECT.md#packaged-app-design-requirements) records the approved direction: native distributable apps without bundled secrets, internet connectivity, address-and-code onboarding, desktop interaction, a minimal tabbed client, directory-based identity, steering, usage, scheduling, timing and multiple saved server connections.

[Working agreement](../AGENTS.md) defines planning approval, documentation, validation, and deployment expectations.

[Runtime architecture](ARCHITECTURE.md) describes authentication, session ownership, networking, recovery, and artifact boundaries. [Local setup](SETUP.md) contains build and phone connection instructions.

[Use the native apps](SETUP.md#use-the-packaged-native-apps), [desktop permissions](SETUP.md#desktop-setup-and-permissions), [Wi-Fi](SETUP.md#connect-over-wi-fi), [temporary internet](SETUP.md#temporary-internet-trial), and [stable internet](SETUP.md#stable-internet-connection) are the setup paths. The server does not serve the former browser client.

## Documentation status

Version 0.1.3 contains native Windows/Android apps, usage and persistent scheduling, multiple saved hosts, steering/timing, desktop permissions and packaged connector setup. The setup guide distinguishes focused/emulator/read-only-provider checks from remaining physical-device, external-network and real-inference acceptance. Historical 0.1.0/0.1.1 scope remains in the project brief.

When adding a page, link it from both this page and [TABLEOFCONTENTS.md](TABLEOFCONTENTS.md).
