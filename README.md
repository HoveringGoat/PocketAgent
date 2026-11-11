# PocketAgent

A native Android companion and Windows desktop/server app for following and directing Codex work.

The desktop keeps provider credentials and repository execution local. Pair the Android app with a server address and one-use code, then switch between conversation tabs on saved work/personal machines. Both native apps support chat, mid-work steering, supported approvals, interruption, usage windows and scheduled messages. The browser client has been replaced.

## Run the packaged apps

Version 0.1.9 removes remaining white scrollbar/confirmation surfaces and requests dark window title bars. Direct HTTPS can share public port 80 with an existing Caddy server through a challenge-only route. Version 0.1.8 added automatic trusted IP certificates; no phone certificate installation is required for this mode.

Version 0.1.5 adds **Detect public IP** to the direct internet setup wizard. Detection is optional and only queries the address service when clicked; chat traffic stays direct. Existing 0.1.4 phone clients remain compatible.

Build outputs are `artifacts/PocketAgent-0.1.9-desktop.zip` (self-contained Windows x64 server) and `artifacts/PocketAgent-0.1.9-android.apk` (signed sideloadable Android client). Existing phone clients are compatible. Extract the ZIP and run `PocketAgent.Desktop.exe`; use Settings > Set up direct internet for trusted HTTPS. The installed VS Code Codex binary is discovered automatically; it is not redistributed.

Follow [direct internet setup](documentation/SETUP.md#direct-internet-ip-and-port) for IP-and-port HTTPS with manually configured router forwarding/firewall rules. No domain, centralized PocketAgent host or traffic relay is required. The desktop provides certificate setup and local diagnostics; Android's Test connection over cellular checks the external route. Wi-Fi HTTPS and optional third-party tunnels remain documented alternatives. Loopback `127.0.0.1` is desktop-only. The server URL returns metadata rather than a browser chat page. No public release or internet endpoint has been published automatically.

Each conversation can select its own existing desktop folder and available model. Archive/restore hides old tabs without deleting history; archiving cancels pending scheduled messages. New pairings use six-digit codes. User messages appear in bubbles and assistant prose uses the plain transcript background. Updates preserve saved device tokens and conversation references.

## Build and verify

```powershell
.\scripts\Build.ps1 -Publish
.\scripts\Start-Desktop.ps1 -Repository C:\GitHub\YourProject
```

See [build prerequisites and signing](documentation/SETUP.md#build-from-source), [focused verification](documentation/SETUP.md#verification-commands-and-limits), [architecture](documentation/ARCHITECTURE.md), [project requirements](documentation/PROJECT.md), and [complete contents](documentation/TABLEOFCONTENTS.md). Contributor instructions are in [AGENTS.md](AGENTS.md).

Native emulator and focused local checks pass. Read-only real-provider diagnostics confirm `gpt-6.1-sol` catalog membership and both usage windows. Successful real inference, physical Android/Wi-Fi and external-network connectivity still require acceptance checks. Full suites and live model turns have not been run.
