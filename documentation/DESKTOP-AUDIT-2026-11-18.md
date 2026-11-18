# Rust desktop parity audit

Baseline: fixed .NET 0.3.13 (rendering fix `f91ec19`) versus the Windows Rust/Slint 0.1.1 preview. This revises the November 18 audit. Rust can open these pages and connect to Codex, but several ordinary .NET desktop workflows are missing or incomplete. The biggest replacement blockers are secure phone onboarding without a relay, archive/scheduler safeguards, and dependable conversation switching and message delivery during active work.

**Evidence:** “Capture” means visible in a linked screenshot. “Smoke” means exercised by UI automation within the limits listed below. “Source” means traced through the relevant controls and handlers. “Test” means a named focused test passed. **P0** is a replacement blocker involving connectivity or integrity, **P1** is a missing or materially weaker workflow, and **P2** is layout or usability polish. The priorities are audit judgments.

## Shared header and navigation

[.NET header and Sessions](../.local/audit-screens/dotnet-fixed-print-sessions.png) · [Rust header and Sessions](../.local/audit-screens/rust-sessions.png)

| Capability | .NET 0.3.13 | Rust 0.1.1 gap | Evidence |
| --- | --- | --- | --- |
| Server power | The header switch starts and stops the local host. The stop handler checks for active work. | **Missing, P1.** The ON/OFF pill is a readout. The direct host starts with the process. The Settings relay switch controls a different service. | Capture, source, .NET smoke |
| Navigation | Header buttons open all three pages. The fixed baseline screenshot also shows a duplicate tab strip introduced by restoring the shared WPF template. | **Available.** Rust switches pages from a single header row. The duplicate .NET strip should be treated as a baseline defect. | Capture, smoke |
| Machine, folder and usage | The title sits beside the machine/folder context. Usage includes five-hour and weekly reset information when available. | **Partial, P2.** Rust displays percentages and a separate usage line. In the captures it overlaps Devices and Settings headings. The machine, separator and folder are spread across the header. | Capture, source |

Sources: [WPF shell and page controls](../src/PocketAgent.Desktop/MainWindow.xaml) · [WPF handlers](../src/PocketAgent.Desktop/MainWindow.xaml.cs) · [Rust shell and page controls](../rust/pocketagent-desktop/ui/app.slint)

## Sessions

[.NET Sessions, stopped and empty](../.local/audit-screens/dotnet-fixed-print-sessions.png) · [Rust Sessions, no selection](../.local/audit-screens/rust-sessions.png) · [Rust with a populated thread](../.local/rust-desktop-session.png). The populated Rust screenshot establishes row separation for that transcript. The .NET capture establishes the visible controls and layout in its empty state. The behavior comparison below uses source where the captures show different states.

| Workflow | .NET 0.3.13 | Rust 0.1.1 gap | Evidence |
| --- | --- | --- | --- |
| Create in a specific folder | Plus opens a dialog with a working-folder browser and model choice. | **Partial, P1.** Plus submits immediately. The worker accepts a folder, but the corresponding desktop field is hidden. Users cannot pick a non-default folder from the creation UI. | Source |
| Switch while work is running | The window can select another conversation while the host tracks turns by conversation. | **Partial, P1.** The Rust worker services the current turn’s steering and cancellation while deferring other commands, including local selection. A tab can appear unresponsive while a turn runs. | Source |
| Rename and pin | Rename and pin are dedicated actions. Pin exposes a corresponding Unpin state. | **Partial, P2.** Rename uses an inline field beside the actions. The Pin label does not change, and the tab model carries only ID and title. Pin is persisted, but the Rust desktop list sorts by title without using pin state. | Capture, source |
| Manual archive and restore | Archived history is available separately. Archiving cancels pending schedules and has server-side eligibility checks. | **Partial, P0.** Rust has an archived view, restore, and local active-work checking. Its desktop archive path sets the flag without canceling jobs. Its scheduled prompt path checks pending status but does not check the destination’s archive flag before resuming it. Confirm the dispatch outcome with a disposable regression before claiming equivalent safeguards. | Source |
| Automatic archive | The host uses activity, pinning, and active-work/schedule exclusions. | **Partial, P0.** Rust checks age and pinning. The store routine has no pending-schedule exclusion. Its focused test covers pinned and recent sessions, leaving pending jobs and restart behavior uncovered. | Source, test |
| Missing provider thread | The .NET host can preserve local metadata and expose a disconnected error when a thread cannot load. The list and usage can still refresh. | **Partial, P1.** Rust publishes the resume error. Selection is committed on the successful path, so a failed selection does not get the same selected-thread recovery view. Retest selecting an unavailable thread, then switching, archiving and restoring. | Source |
| Model and reasoning | The robot action provides a dedicated picker and uses the provider catalog to offer supported reasoning choices. | **Partial, P1.** Rust shows an inline model dropdown, free-text reasoning, and a clipped catalog string in the captured layout. The local path stores options on creation or prompt dispatch. A change made only in the fields lacks the dedicated save workflow. | Capture, source |
| Draft, send and steer | The composer retains per-session drafts and reconciles submissions through receipts and client message IDs. Send keeps its label while steering. | **Partial, P0.** There are send and steer paths and the Send label stays stable. The Slint click handler clears the text immediately, before worker acceptance. A later rejection can leave the user reconstructing their input. The desktop flow lacks the WPF draft/receipt interaction. | Source |
| Stop and work status | The status strip keeps a fixed height and shows an icon action during work. | **Partial, P2.** Rust has elapsed/status output and an interrupt path. It uses a text button and gates its visibility on a status string. Verify that Stop stays available through tool/approval states. | Capture, source |
| Schedule and review | Timer opens local date/time scheduling. The session shows pending work and offers review and cancel. | **Partial, P1.** Rust accepts a positive delay in seconds. The worker fills a scheduled-items model and has a cancel callback, but the desktop page renders neither a job list nor a Cancel control. Invalid input is validated after the click clears the composer. | Source |
| Transcript and long history | Role-aware bubbles, timestamps, expandable tool details, bounded rendering and earlier-history loading. | **Partial, P1.** Rust renders headings, bullets, code blocks and images from flattened text. It lacks the same role-aware row presentation, timestamps, expandable tool rows and desktop scroll-to-load flow. An API history cursor does not supply a desktop scroll interaction. Rust Markdown block rendering is worth retaining. | Capture, source |
| Images | WPF supports a native viewer and an external fallback for image references. | **Partial, P1.** Rust can decode and show supported local/data images inline. The Slint image block has no click-to-open, expand or external-viewer action. Exercise spaced paths, structured image references and unsupported formats on both builds. | Source |
| Approvals and questions | The transcript shows supported requests with request-specific actions. | **Partial, P1.** Rust has a generic prompt with fixed approval buttons or a single answer entry. The UI does not use its decision-list property to build actions. Check that displayed choices match the provider’s offered choices and that a multi-answer request retains its question mapping. | Source |

Sources: [WPF session handlers](../src/PocketAgent.Desktop/MainWindow.xaml.cs) · [Rust UI wiring](../rust/pocketagent-desktop/ui/app.slint) · [Rust worker](../rust/pocketagent-desktop/src/main.rs) · [Rust registry and schedule store](../rust/pocketagent-desktop/src/store.rs) · [Rust provider and transcript mapping](../rust/pocketagent-desktop/src/codex.rs) · [.NET workflow invariants](ARCHITECTURE.md#conversations-steering-and-recovery)

## Devices

[.NET Devices, server stopped](../.local/audit-screens/dotnet-fixed-print-devices.png) · [Rust Devices](../.local/audit-screens/rust-devices.png). The .NET capture has no live QR or address because its host is stopped. The Rust capture shows a loopback endpoint. Keep enrollment material private when capturing a running host.

| Workflow | .NET 0.3.13 | Rust 0.1.1 gap | Evidence |
| --- | --- | --- | --- |
| Direct phone setup | The desktop prepares a reachable address and pinned HTTPS, with Wi-Fi and direct-internet setup in Settings. | **Missing desktop workflow, P0.** The direct Rust HTTP host defaults to loopback. Binding is configured outside the GUI. The desktop lacks the shipped HTTPS/pin setup. A successful localhost health request demonstrates local service only. Relay is a separate mode. | Capture, source, Rust smoke |
| QR and complete pairing string | Generate a fresh QR, scan it or copy its full pairing string. Copy Address is separate. | **Partial, P1.** Rust shows a code, endpoint and Renew control. It offers no QR, full-string copy or address-copy action, even though its instructions mention QR and copy. | Capture, source |
| Diagnose connection or find APK | Devices offers Connection diagnostics and Locate Android installer APK. | **Missing, P1.** Rust shows broad host status and address without equivalent tools. A failed phone setup has little actionable feedback. | Capture, source |
| Grant/revoke conversation access | The Sessions Access action opens a checklist for the current conversation. Device-wide revoke belongs in Devices. | **Partial, P1.** Rust has grant/remove and revoke callbacks. To change a grant, select the conversation, move to Devices, and use the row actions. There is no Sessions Access action. Verify with two conversations and two device identities, including revoke across conversations. | Source |
| Pairing renewal and real mobile use | The app exposes renewal, pairing and revocation workflows. | **Unverified end to end.** Rust smoke checks that Renew is present and that the host returns HTTP 200. It never invokes Renew or pairs a phone. The previous report’s claim that renewal was tested was incorrect. | Smoke script |

Sources: [WPF Devices and Settings controls](../src/PocketAgent.Desktop/MainWindow.xaml) · [WPF pairing and access handlers](../src/PocketAgent.Desktop/MainWindow.xaml.cs) · [Rust host startup and device actions](../rust/pocketagent-desktop/src/main.rs) · [Rust host and device registry](../rust/pocketagent-host/src/lib.rs)

## Settings

[.NET Settings](../.local/audit-screens/dotnet-fixed-print-settings.png) · [Rust Settings](../.local/audit-screens/rust-settings.png)

| Workflow | .NET 0.3.13 | Rust 0.1.1 gap | Evidence |
| --- | --- | --- | --- |
| Set up default folder and Codex | Browse for a folder, override the executable, inspect its resolved path and run a sign-in check. | **Partial, P1.** Rust discovers defaults and exposes editable text fields with Connect Codex and readiness/error status. It lacks the folder picker and separate sign-in check. Test a clean launch and an invalid override in both. | Capture, source, Rust negative smoke |
| Set provider permissions | The desktop offers inherited settings, ask-for-approval and full-access modes subject to managed restrictions. | **Missing, P1.** Rust Settings has no equivalent host policy selector. An approval prompt is a separate workflow. | Source |
| Startup, UI scale and progress | Startup toggles, persistent appearance presets and periodic hidden progress updates are configurable. | **Missing, P1/P2.** Rust has no corresponding controls or periodic hidden progress workflow in the reviewed paths. At larger scaling the existing header overlap adds a usability cost. | Capture, source |
| Updates and build identity | Settings shows the running version/path and provides manual or automatic update controls, backed by a separate updater. | **Missing, P1.** The Rust preview installer is distributed separately. Settings lacks equivalent build diagnostics, check/download/install controls and restart/rollback flow. Presence of .NET controls does not establish installed upgrade success. | Capture, source |
| Listener, TLS and setup guidance | Settings exposes listener address/port, HTTPS and Wi-Fi setup, and a setup guide. | **Missing, P0 for direct onboarding.** Rust shows optional relay settings and a local host readout without a direct listener setup wizard or certificate workflow. | Source |
| Optional relay | No equivalent desktop relay-node/outbound-host UI in the 0.3.13 baseline. | **Rust addition.** Local relay start/stop and identity persistence passed smoke. Outbound host enrollment is available through fields, but persistence of node identity is different from automatic reconnection of a configured outbound host. Real handset traffic through this path remains unverified. | Capture, source, smoke |

Sources: [WPF Settings controls](../src/PocketAgent.Desktop/MainWindow.xaml) · [WPF settings defaults](../src/PocketAgent.Desktop/DesktopSettings.cs) · [Rust Settings controls](../rust/pocketagent-desktop/ui/app.slint) · [Rust desktop package script](../scripts/Build-RustDesktop.ps1) · [Relay architecture](ARCHITECTURE.md#relay-mode)

## Verification

| Check | Result and limits |
| --- | --- |
| Fixed .NET 0.3.13 visual baseline | Inspected native-window captures of all three rendered pages. Earlier white-window captures are superseded. The screenshots are local files under `.local`. |
| `tests/desktop-render-smoke.ps1` and `tests/desktop-smoke.ps1`, pointed at the Release binary | Both passed on rerun. They check the window, page-specific controls and power-switch state transitions. The current scripts do not exercise send, steer, stop-work, archive, or pairing. |
| `tests/rust-desktop-smoke.ps1`, pointed at the Release executable | Passed on rerun with disposable LocalAppData, a loopback ephemeral host port and Slint software rendering. It checks page navigation, relay lifecycle/identity, presence of Renew, local host health and a failing provider override. The historical `-RequireCodex` pass reached readiness. Readiness does not establish a completed prompt. |
| `cargo test --locked --manifest-path rust/Cargo.toml -p pocketagent-desktop --bin pocketagent-desktop store::tests::auto_archive_skips_pinned_and_recent_conversations` | One focused test passed. It does not cover pending schedules, unavailable threads, or archiving during a live provider turn. |

Remaining acceptance evidence includes matched populated-session captures with the same fixture and window size, real send → steer → interrupt with a second conversation open, archive with a due job, phone enrollment and revocation, and a disposable installed upgrade. Source findings in the tables identify specific acceptance cases.

## Summary and acceptance order

1. **Protect conversation and job state.** A rejected send retains its draft. A running turn stays attached to the correct conversation while the user visits another thread. Archiving obeys the same pending-job and active-work rules across restart and both desktop and device actions.
2. **Make direct setup usable without a relay.** The desktop starts/stops the direct host from the header and produces an Android-consumable pairing QR/string through guided secure setup. Verify onboarding and revoke using a disposable device identity. Exercise the optional relay separately.
3. **Restore daily Sessions workflows.** Match folder selection, model/reasoning, session access, pending-job review/cancel, transcript structure, and image viewing. Use the same fixture in both apps for idle, streaming, tool-output and error states.
4. **Restore operational controls and finish layout.** Cover startup, provider settings, installed updates and diagnostics, then verify scale, header alignment and action icons. Record the installed-upgrade result separately from the presence of update controls.
