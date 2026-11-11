# PocketAgent

A native Android companion and Windows desktop/server app for following and directing Codex work.

The desktop keeps provider credentials and repository execution local. Scan its pairing QR or paste the same full pairing string, then switch between conversation tabs on saved work/personal machines. Both native apps support chat, mid-work steering, approvals, interruption, usage windows and scheduled messages.

## Run the packaged apps

Version 0.1.12 is the latest stable release. The 0.2.0 development line adds the stable port-based Windows Firewall rule and monochrome schedule control, alongside the other planned feature work. No additional six-digit entry, CA installation, Let's Encrypt, ports 80/443 or Caddy edits are required.

Install both apps for new enrollment. Existing saved devices/conversations are retained; re-pair to adopt pinned trust on an old connection.

Outputs are grouped under `artifacts/releases/<version>`. A release contains a per-user Windows installer, a portable desktop ZIP, the signed Android APK, and SHA-256 manifests. The installed VS Code Codex binary is discovered automatically and is not redistributed. No release is published unless the release script is called with `-Publish`.

Follow [direct internet setup](documentation/SETUP.md#direct-internet-ip-and-port) to forward one chosen TCP port to the desktop. On first start, the desktop can create one elevated port-based Windows Firewall rule that survives versioned executable updates. Start the server, then open Devices to scan/copy pairing. Pair over cellular to test the outside route. No domain, central hosting or traffic relay is required. No public release or internet endpoint has been published automatically.

Each conversation selects its existing desktop folder and available model. Archive/restore hides old tabs without deleting history; archiving cancels pending schedules. User messages use bubbles; assistant prose uses the plain transcript background.

## Build and verify

```powershell
.\scripts\Build.ps1 -Target Desktop -Publish
.\scripts\Build.ps1 -Target Android
.\scripts\Start-Desktop.ps1 -Repository C:\GitHub\YourProject
```

To check release prerequisites without building, run `scripts/Release.ps1 -Version 0.2.0 -Check`. Add `-InstallPrerequisites` to let Windows Package Manager install missing Inno Setup 6 and/or GitHub CLI. To stage a complete 0.2.0 release locally, run `scripts/Release.ps1 -Version 0.2.0`; the script performs the same preflight before compiling. Add `-SkipInstaller` when only the portable/Android assets are needed. The script validates the version, refuses to reuse an existing version directory unless `-Clean` is supplied, checks the desktop package for private files, and writes `SHA256SUMS.txt`. Add `-Publish` only after authenticating `gh` with `gh auth login`; it creates the versioned GitHub release and uploads the staged assets.

See [build prerequisites and signing](documentation/SETUP.md#build-from-source), [focused verification](documentation/SETUP.md#verification-commands-and-limits), [architecture](documentation/ARCHITECTURE.md), [project requirements](documentation/PROJECT.md), and [complete contents](documentation/TABLEOFCONTENTS.md). Contributor instructions are in [AGENTS.md](AGENTS.md).

Native emulator and focused local checks pass. Read-only real-provider diagnostics confirm `gpt-6.1-sol` catalog membership and both usage windows. Successful real inference, physical Android/Wi-Fi and external-network connectivity still require acceptance checks. Full suites and live model turns have not been run.
