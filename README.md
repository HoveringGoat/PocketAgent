# PocketAgent

PocketAgent lets you keep an eye on a coding agent that is running on your
desktop, and direct it from a phone when you are away from the keyboard.
It is a native Windows desktop/server app paired with a native Android client.
It is not a browser wrapper and it does not move your provider account or
repository into a hosted PocketAgent service.

## Why use it?

Coding agents are most useful when they can work for a while: running tests,
exploring a repository, waiting for a build, or applying a multi-file change.
PocketAgent keeps that work on the machine that has your tools and credentials,
while giving you a compact way to monitor and guide it from another device.

Typical examples:

- Start a task on your Windows machine, then follow its progress from your
  phone while you are away from your desk.
- Send a steering prompt while the agent is already working instead of
  interrupting it or starting a competing turn.
- Respond to an approval or question request when it appears.
- Keep separate conversations for different folders, models, or machines.
- Schedule a prompt for later, or let a usage-limited prompt wait for the
  provider window to reset.
- See active work, elapsed time, usage windows, timestamps, command output,
  and the folder and machine where a conversation is running.

## How the pieces fit together

```text
Android client  <---- encrypted direct connection ---->  Windows desktop/server
                                                               |
                                                               +-- Codex app-server
                                                               +-- local repository
                                                               +-- provider credentials
```

The desktop application is both the user interface and the server. It starts
the locally installed VS Code Codex executable (or an executable path you
configure), owns the provider session, and exposes only the PocketAgent relay
API. The Android app stores its paired device credential locally and displays
the conversations made available by that desktop.

There is no central PocketAgent account, traffic relay, shared hosting service,
or peer-to-peer client path. A phone connects to the selected desktop/server
address. You choose how that address is reachable: the same Wi-Fi network, or a
manually forwarded TCP port for internet access.

## What you can do

### Conversations and work

- Open multiple conversations on each server, with a different existing local
  working directory per conversation.
- Use a temporary **New conversation** tab without naming it first; rename it
  later if useful.
- Choose an available model and reasoning level per conversation.
- Review assistant messages, user messages, command/work cards, approvals, and
  questions in one transcript.
- Collapse work cards without losing their state while the transcript updates.
- Archive and restore conversations without deleting provider history.

### Working while the agent is busy

The composer stays available while a turn is running. **Send** delivers a
steering prompt to that active turn; it does not silently cancel the work.
The server validates the turn identity so a stale steering request cannot be
applied to a different turn. Input focus and text typed after submission are
preserved.

While a conversation is actively working, the server can send a hidden
`update requested` heartbeat after five quiet minutes. A user prompt or visible
agent text resets that timer, and completion cancels it. The heartbeat consumes
provider usage like any other request, but does not add a hidden transcript
message.

### Scheduled messages and usage

Use the stopwatch control beside Send to schedule a prompt. Scheduled messages
are persisted by the desktop server, survive a restart, wait for a safe idle
conversation, and show pending/uncertain/failed states. The same delivery ID
prevents a network retry from creating a duplicate prompt.

Both apps show the provider-reported five-hour and weekly usage windows when
available. If a prompt is rejected because a usage window is exhausted, it is
kept as queued work and the app explains that it will be submitted after reset.

## Security and network model

- Provider credentials, repositories, certificates, databases, and private
  keys stay on the desktop/server and are never bundled in a release.
- Pairing is a one-time authorization step for a specific device. Device
  credentials are revocable and stored in the Android keystore.
- The connection is HTTPS with installation-specific trust/pinning. The
  pairing QR contains the connection information and one-time ticket; there
  is no second code to type. Manual entry of the complete pairing string is
  also supported.
- For internet access, forward one chosen TCP port from the router to the
  desktop listener and allow that port through Windows Firewall. Ports 80/443,
  a personal domain, Caddy changes, Let's Encrypt, and a PocketAgent relay are
  not required.
- Public exposure is an explicit setup action. The desktop's diagnostics help
  distinguish a local listener problem from a router, firewall, or external
  network problem.

Read [runtime architecture](documentation/ARCHITECTURE.md) before changing the
transport or trust model, and follow [direct internet setup](documentation/SETUP.md#direct-internet-ip-and-port)
for the supported external route.

## Quick start for packaged users

1. Install the Windows desktop/server package on the machine that has VS Code
   Codex and the repository you want to work in.
2. Start PocketAgent, choose the repository, confirm the Codex executable and
   permissions in **Settings**, then start the server.
3. In **Devices**, scan the pairing QR with the Android app, or paste the full
   pairing string. The QR and string are alternatives, not two separate steps.
4. Create or select a conversation, choose its folder/model if needed, and send
   a prompt.
5. To use the phone away from home, configure a chosen external TCP port and
   router forwarding, then test the address from cellular data.

The desktop **Menu** provides one-click access to Sessions, Devices, general
settings, update checks, release notes, and version information. On Android,
the top-right menu combines server switching with scheduling, usage, model and
reasoning controls, app settings, update checks, and version information.

The desktop can run without the phone. The phone can be offline and reconnect
later; conversations and scheduled work remain owned by the desktop server.

See [packaged app setup](documentation/SETUP.md#use-the-packaged-native-apps),
[Wi-Fi setup](documentation/SETUP.md#connect-over-wi-fi), and
[pairing](documentation/SETUP.md#pair-by-qr-or-pairing-string) for the detailed
steps and troubleshooting limits.

## Current packaged release

The latest stable releases are **desktop/server 0.3.1** and **Android 0.3.1**.
Releases contain a per-user Windows installer, a portable desktop ZIP, the
signed Android APK, and SHA-256 manifests. Desktop and Android release channels
are tracked independently and may have different version numbers.

The desktop updater can check GitHub Releases, show platform-specific notes,
download a verified update, and hand installation to a separate updater process
when the server is idle. Android checks on startup and opens the matching APK
release for manual installation. Runtime state is kept outside replaceable
package files.

Read [desktop/server changes](CHANGELOG-desktop.md) and
[Android changes](CHANGELOG-android.md) for release history.

## Build and verify from source

Normal users should install a GitHub release. Contributors can build the
native apps with the repository scripts:

```powershell
.\scripts\Build.ps1 -Target Desktop -Publish
.\scripts\Build.ps1 -Target Android
.\scripts\Start-Desktop.ps1 -Repository C:\GitHub\YourProject
```

To check release prerequisites without building:

```powershell
.\scripts\Release.ps1 -Version 0.3.1 -Target Desktop -Check
```

Add `-InstallPrerequisites` to let Windows Package Manager install missing
release-only tools such as Inno Setup 6 and GitHub CLI. To stage a release
locally, run `scripts/Release.ps1 -Version <version>`; add `-SkipInstaller`
when only portable/Android assets are needed. The script validates package
contents, writes checksums, and composes notes from the separate changelog
files. Add `-Publish` only after explicitly authenticating `gh` and deciding
to create the public GitHub release. Use `-CommitChanges` only when you want
the release script to create the version commit.

More detail is in [build prerequisites](documentation/SETUP.md#build-from-source),
[focused verification](documentation/SETUP.md#verification-commands-and-limits),
[architecture](documentation/ARCHITECTURE.md), and the
[documentation index](documentation/TABLEOFCONTENTS.md).

## Project status and boundaries

PocketAgent is an actively developed personal/work tool. Focused desktop,
server, and Android builds are verified locally. Physical-device pairing,
cellular reachability, router/firewall configuration, provider sign-in, and
real inference still require acceptance on the target machines. Full test
suites and live model turns are intentionally not run by the release script.

The project is released under the [MIT License](LICENSE). Never commit provider
credentials, device tokens, private keys, runtime databases, or `.env` files.
