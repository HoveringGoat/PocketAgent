# PocketAgent

A native Android companion and Windows desktop/server app for following and directing Codex work.

The desktop keeps provider credentials and repository execution local. Pair the Android app with a server address and one-use code, then switch between conversation tabs on saved work/personal machines. Both native apps support chat, mid-work steering, supported approvals, interruption, usage windows and scheduled messages. The browser client has been replaced.

## Run the packaged apps

Build outputs are `artifacts/PocketAgent-0.1.3-desktop.zip` (self-contained Windows x64 server) and `artifacts/PocketAgent-0.1.3-android.apk` (signed sideloadable Android client). Extract the ZIP and run `PocketAgent.Desktop.exe`; install the APK on Android. Configure a repository and Codex permissions in the desktop Settings, then start the server. The installed VS Code Codex binary is discovered automatically; it is not redistributed.

Follow [native app setup](documentation/SETUP.md) for Wi-Fi HTTPS, temporary internet trials and stable named internet tunnels. Loopback `127.0.0.1` is desktop-only. The server URL returns metadata rather than a browser chat page. No public release or internet endpoint has been published automatically.

## Build and verify

```powershell
.\scripts\Build.ps1 -Publish
.\scripts\Start-Desktop.ps1 -Repository C:\GitHub\YourProject
```

See [build prerequisites and signing](documentation/SETUP.md#build-from-source), [focused verification](documentation/SETUP.md#verification-commands-and-limits), [architecture](documentation/ARCHITECTURE.md), [project requirements](documentation/PROJECT.md), and [complete contents](documentation/TABLEOFCONTENTS.md). Contributor instructions are in [AGENTS.md](AGENTS.md).

Native emulator and focused local checks pass. Read-only real-provider diagnostics confirm `gpt-6.1-sol` catalog membership and both usage windows. Successful real inference, physical Android/Wi-Fi and external-network connectivity still require acceptance checks. Full suites and live model turns have not been run.
