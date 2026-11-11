# Project brief

## Purpose

Enable the user to follow coding-agent work from a mobile device while the agent works on a desktop machine. A small desktop app/server will connect the mobile client to Codex or Claude and relay relevant information.

## Intended experience

The user opens the mobile client, sees their coding-agent conversations, and follows progress on ongoing work.

The desktop host remains the place where the agent accesses the working repository and performs development tasks. The mobile client presents conversations and work updates received through the desktop component.

The first milestone includes reading chats and progress, sending messages, and responding to approval requests. The user selected Codex with new conversations started through PocketAgent, and an Android phone initially connecting over the same Wi-Fi as the desktop.

These product requirements are confirmed. The user asked to continue after reviewing the milestone and technology choices. Version 0.1.0 implements the initial scope below; version 0.1.1 adds same-process provider diagnostics. Verification limits are recorded separately.

## Packaged app design requirements

These requirements extend the historical 0.1.0/0.1.1 browser milestone. Version 0.1.3 implements native Windows and Android apps, steering, desktop permissions, Wi-Fi setup, supervised internet connectors, usage, scheduling, timing and multiple saved server connections. Focused checks distinguish implementation from verification; physical phone, real inference and external-network acceptance remain open in [setup](SETUP.md).

### Distribution and local state

- Both the desktop/server app and the client must be packaged and distributable. End users should not need a source checkout or development build commands for normal use.
- Distributed artifacts contain no provider credentials, pairing secrets, device tokens, private certificate keys, runtime databases, or developer-specific paths. Configuration and private state are created or supplied on the user's devices after installation and kept outside versioned artifacts.
- Provider sign-in and repository access stay on the desktop. A paired client necessarily retains its own runtime authentication credential; this is generated during pairing, revocable, and never shared across installations or bundled in a release.
- Distribution is a self-contained Windows x64 ZIP and signed sideloadable Android APK. The Codex binary is discovered from the installed VS Code extension or an explicit override, never redistributed. Native installation/update automation and public release signing remain future work.

### Connection and pairing

The intended client onboarding is: enter the desktop server address, establish a trusted encrypted connection, enter the desktop-issued pairing code, then open conversations. Remembering the server and the device session should avoid repeating setup on every launch. Changing to a different server must not send the previous server's device credential to that server.

Networking setup belongs in the desktop experience. The app should present the usable connection address and explain configuration failures. A short-lived code authorizes a device after transport trust is established; it does not replace server authentication or encryption. Codes must be renewable from the desktop without restarting ongoing work, and paired devices must be individually revocable.

**Internet access is required for the first distributable release.** The client must be able to connect from outside the desktop's local network, including over cellular or another Wi-Fi network. A LAN-only package does not satisfy the release requirements. The existing implementation remains LAN-only; internet access is required behavior, not implemented or verified behavior.

Caddy is a candidate for a packaged reverse proxy and certificate lifecycle component, not a selected dependency. Its [automatic HTTPS documentation](https://caddyserver.com/docs/automatic-https) explains that local HTTPS requires clients to trust its generated CA. Its [reverse proxy documentation](https://caddyserver.com/docs/quick-starts/reverse-proxy) describes forwarding to a local backend. Bundling it alone does not establish phone certificate trust or provide connectivity through a home router. The remaining decision is the internet reachability and trust model: for example, an outbound tunnel/relay, a private network connection, or an explicitly configured public HTTPS endpoint. Any third-party account, domain, operating cost, or external service requirement must be part of that design rather than a hidden prerequisite.

Before implementing a new transport or proxy, specify the public address, listener ownership, origin/Host validation, proxy trust, client credential storage, certificate trust or verified enrollment, and exposure boundary in [runtime architecture](ARCHITECTURE.md). The existing same-origin cookie API cannot simply be assumed to work unchanged for a separately packaged client. Firewall, router, trust-store, and public-exposure changes require explicit setup actions rather than silent installation side effects.

### Desktop navigation and interaction

The desktop must support reviewing active PocketAgent sessions and interacting in the selected conversation, including messages, progress, supported approvals/questions, and interruption. Desktop and phone must operate on the same relay-owned conversation state and preserve existing provider permissions and stale-request protection. This requirement does not establish attachment to sessions owned by an IDE or another provider process.

Both desktop and client must support submitting additional prompts while the agent is working, with the active-turn behavior specified below.

Implemented native Windows WPF navigation is **Sessions**, **Devices**, and **Settings**, with top conversation tabs and server/provider status. Settings include saved repository, executable, permissions, listener, and certificate configuration; stopping explains the impact on active work and pending schedules.

### Minimal client and conversation identity

- The normal client screen is conversation tabs, an add-conversation action, the selected transcript, and a message composer. Progress, approval/question controls, interruption, and connection failures appear when relevant. Server setup and provider diagnostics should not occupy the normal chat view.
- The `conversation-meta` status/context/control row belongs **between the transcript and the message composer**, outside the transcript's scrolling region. It must remain visible beside the input area during long conversations, including on mobile with the keyboard open. Status and Stop work must not require scrolling to the top of the conversation. This is a required change from the current row above the transcript.
- Creating a conversation must not require entering a name. The working repository/directory should provide the default conversation identity; use a temporary **New conversation** label only when that context is not yet available.
- Users can rename a conversation later. Its execution context must remain discoverable after renaming: at least its repository and, in details, its actual working directory and host identity.
- Default labels use the repository folder name with a stable suffix for additional conversations, for example **PocketAgent** and **PocketAgent · 2**. The working directory and host remain visible for context.
- Automatic AI title generation is not required. The [official Codex app-server documentation](https://learn.chatgpt.com/docs/app-server) supports explicit `thread/name/set` and reports that new threads can have no `name`; PocketAgent should not depend on ChatGPT-style automatic titles. Provider naming support must be checked against the selected executable before integration.
- Proposed tab behavior: closing a tab does not stop or delete its conversation; saved conversations remain reopenable. Switching tabs or reconnecting must not submit a prompt or decision again. Tab restoration and history navigation still need a concrete interaction design.

The server currently fixes one repository at startup. Multiple selectable repositories would change session routing and persistence: the proposed design is a desktop-managed list of allowed repositories, a default for one-click creation, and a client choice restricted to that list. Each conversation would retain its own working directory for its lifetime. This proposal needs confirmation; arbitrary remote filesystem path entry is not implied by displaying session context.

### Prompts during active work

Submitting a prompt while the agent is working is required behavior on both desktop and client. The composer must permit typing during work and sending a steering prompt to the active turn. The UI should make the current Send/Steer action clear without introducing a separate workflow. Steering augments the ongoing work; it must not silently interrupt the agent, start a competing turn, or wait until the current turn completes before delivery.

The [official Codex app-server documentation](https://learn.chatgpt.com/docs/app-server#steer-an-active-turn) defines `turn/steer` with a required matching `expectedTurnId`. The adapter must target the turn observed by the sender and validate it again on the server. Idle conversations still use a new turn. If the expected turn completes or changes before delivery, reject the stale steering request visibly and retain the draft rather than automatically starting another turn. The installed provider executable's protocol support still needs verification.

Input should remain editable while a send is being confirmed. Acknowledgement must not erase text typed after submission. Failures retain unsent text; uncertain delivery must be shown as uncertain and never automatically retried. Provider events/history remain the authoritative record of accepted steering prompts, and reconnect must not duplicate them.

Pending approval or question requests remain explicit and independently actionable: steering must not count as an approval, clear a request, or change the provider's permission policy. Exact provider behavior while waiting for approval requires a focused check. Only one provider turn is active per conversation; that invariant must no longer be used to prohibit additional input to the same turn.

### Required release verification

- Connect a packaged client over cellular or a separate external network to the desktop using the documented address, trusted encryption, and code pairing; verify device revocation blocks further access. A same-LAN check is insufficient.
- Send a prompt during a running turn and observe the provider accept steering for that same turn. Verify turn-completion races, simultaneous desktop/phone submissions, delivery failures, preservation of newly typed text, and reconnect without automatic replay using focused deterministic checks before a separately authorized real-provider check.
- Verify that transcript scrolling leaves `conversation-meta` directly above the composer on desktop and mobile, with status and Stop work accessible. Physical keyboard/viewport behavior on Android needs real-device verification.

### Approved overnight expansion

The user approved completing and extending the native apps while away. Android must group conversation tabs under saved server connections (for example work and personal desktops), retain distinct encrypted device credentials and drafts per server, and offer add/switch/unpair actions in a compact menu. An unpaired connection opens pairing. User messages have no redundant speaker label. Message timestamps are shown in local messaging-style time with adjacent messages in the same minute grouped; unknown historical times are not invented. The UI shows elapsed active work and completed-turn duration when available, without a decorative footer. Desktop conversation navigation also moves from a sidebar to top tabs.

Both apps show provider-reported five-hour and weekly usage remaining and reset times, with explicit unavailable states when the account/executable does not expose them. Submissions blocked by an identifiable exhausted usage window are persisted for dispatch after reset and reported as queued. A durable server scheduler also supports user-selected dates/times from either app, list/cancel operations, restart recovery, and clear submitted/failed/uncertain states. A message already sent with an unknown outcome must never be automatically replayed. Scheduled messages wait for idle conversations and preserve the configured permissions and approval boundaries. Device revocation cancels that device's pending scheduled work. A stopped/sleeping desktop cannot dispatch until it runs again.

Wi-Fi setup and a documented stable internet route are both required. The default internet proposal is a user-owned named outbound tunnel, with a packaged connector and local runtime-only credentials. An external account/domain must not be silently provisioned, and public exposure remains an explicit setup action. Verification must distinguish emulator/local proxy checks from physical Android and external-network checks.

### Remaining design decisions

1. Store distribution, installer/update automation and public release signing beyond the current sideload/ZIP artifacts.
2. User provisioning of a stable named internet tunnel/domain versus a temporary Quick Tunnel trial; both paths are implemented but external-network acceptance is outstanding.
3. One configured repository or multiple desktop-approved repositories, including selection when creating a conversation.
4. Exact default naming, tab closing/restoration, desktop close/background behavior, and release/update formats.

## Component responsibilities

These responsibilities describe the implemented local architecture.

| Component | Intended responsibility |
| --- | --- |
| Desktop app/server | Connect to an agent, expose relevant conversations and progress to an authorized mobile client, and report connection status. |
| Provider integration | Translate supported Codex or Claude interactions into the relay's conversation and progress representation. The supported integration surface must be verified before implementation. |
| Mobile client | Present conversations, progress, usage and schedules through a native Android app, grouped by paired server. |

The normal information flow is agent to desktop relay to mobile client. If interactive control is included, mobile requests travel back through the relay and remain subject to the agent's permissions.

The availability of existing desktop-app chats, live session attachment, historical messages, and approval interactions must be established for the chosen provider. This document does not assume those capabilities exist.

## Selected implementation and future decisions

These selections describe the implemented relay. The [packaged app design requirements](#packaged-app-design-requirements) describe the next direction without claiming that packaging, desktop navigation, or the simplified client have landed.

- **Desktop environment:** Native Windows WPF app embedding the relay, with an optional headless command.
- **Mobile client:** Native Android platform UI distributed as a signed APK; no WebView/browser client.
- **Device access and transport:** Desktop-issued pairing code, revocable device session, and HTTPS on a configured local network address. [Local setup](SETUP.md) includes certificate setup and Android trust steps.
- **Conversation handling:** Local session registry and provider-backed history, with reconnect reconciliation as described below.
- **Implementation stack:** C# / ASP.NET Core and WPF on .NET 10, native Android Java/Gradle, and SQLite for local metadata. Native clients use snapshots; the earlier SignalR API remains compatible.

### Technology responsibilities

| Technology | Responsibility |
| --- | --- |
| C# / ASP.NET Core | Own the Codex child process, authentication, pairing, authorized conversation actions, HTTPS, and browser asset hosting. |
| WPF | Native desktop administration, conversation tabs, chat, permission selection and connector lifecycle. |
| Android platform widgets | Native multi-server conversation UI, foreground snapshot polling, local time display and encrypted device credential storage. |
| SQLite | Store paired-device hashes, PocketAgent conversation IDs, timing and durable scheduled messages. Desktop settings are runtime JSON. Codex remains the transcript source. |
| Codex adapter in C# | Translate the local app-server protocol into PocketAgent operations and events, keeping provider details out of the UI. |

The provider integration boundary is the documented JSON protocol over standard input/output. Node.js is verification/formatting tooling, not a production dependency. Runtime/build dependency versions are pinned in `global.json`, project/Gradle files, the Gradle wrapper checksum, npm lockfile and NuGet lockfiles.

## First implementation milestone

### Confirmed requirements

- Provider: Codex first.
- Session ownership: New conversations started through PocketAgent.
- Phone: Android.
- Connectivity: Same Wi-Fi initially.
- Interaction: Read conversations and progress, send messages, and answer approval requests.

### Behavior for version 0.1.0

This is implemented behavior. Real account execution and physical Android verification remain open as noted below.

1. The user starts the relay on Windows with one configured repository directory and an installed, signed-in Codex CLI. The relay launches its own Codex app-server process using local standard input/output.
2. The relay serves an Android-friendly browser UI over HTTPS on an explicitly configured LAN address. Setup explains how to trust the local certificate on Android. The initial release has no public hosting or internet access path.
3. A short-lived, single-use pairing code displayed on the desktop grants access to this user's PocketAgent conversations. Pairing attempts are rate limited; an authenticated, secure browser session can be revoked on the desktop. Provider credentials remain on the desktop.
4. The user can list PocketAgent conversations, start one in the configured repository, open its history, send a prompt when it is idle, and interrupt active work. The UI displays streaming assistant text, available tool activity, and running, awaiting-input, completed, interrupted, failed, and disconnected states.
5. Supported approval requests display the requested action and offer the applicable decisions. The relay preserves Codex's configured sandbox and approval policies and forwards an explicit decision only for the current pending request. Unsupported interactive requests produce a visible limitation and are never automatically approved.
6. Closing or disconnecting the phone does not stop desktop work. Pending approvals remain unanswered until resolved or canceled by the provider. Reconnecting refreshes history, current status, and pending requests without resending a previous prompt or decision.
7. The relay retains a local registry of the conversations it created and uses Codex's stored history for recovery. After a relay restart, saved conversations can be reopened; active execution and pending requests are not promised to survive a process restart. Lost execution must not appear to be still running.
8. Existing desktop-app conversations, simultaneous desktop-app control, Claude support, push notifications, native mobile packaging, a desktop tray installer, and access away from home are outside this milestone.

### Integration evidence and remaining verification

The [official Codex app-server documentation](https://learn.chatgpt.com/docs/app-server) documents starting and resuming threads, reading history, streaming events, interrupting turns, and answering approval requests. It also documents standard input/output as a local transport. This supports the proposed integration choice; it does not establish that the locally installed version or account has been tested successfully.

Implementation used repository-local SDK 10.0.404 and Node.js 24.22.1. The installed Codex CLI 0.153.0 generated the protocol schema used to check the adapter. A real app-server initialization and account-status probe succeeded with the normal desktop execution permissions and reported an available account. No account identifiers, credentials, or existing conversation content were printed. A real model turn, actual agent approval behavior, and a physical phone connection remain unverified.

### Acceptance checks

- Pair a real Android browser with the Windows relay and start a real Codex conversation in the configured repository.
- Send a prompt, observe live text and activity, and see the final result or an actionable failure.
- Exercise a real approval request; verify that approval or denial reaches the correct request and that reconnecting cannot submit a stale decision.
- Disconnect and reconnect the phone during work; verify that work continues and the UI recovers without duplicate prompts.
- Interrupt a turn and reopen a saved conversation after a relay restart.
- Verify that an unpaired or revoked device cannot read conversations or control the agent.

Use focused automated checks and a project-level build during implementation. Real account and Android verification must be reported separately from simulated protocol checks; do not run the full test suite unless requested.

## Hosting and environment boundaries

The default host is the user's Windows desktop. A local build produces the ASP.NET Core relay and compiled browser assets, which are distributed together and served by the relay. The phone receives browser assets and authorized conversation data; repository access and provider execution stay on the host. Development can use loopback locally; phone access uses authenticated LAN HTTPS. The desktop must remain awake and the relay running for ongoing work and connectivity.

Version 0.1.0 is the first implementation milestone. The build script produces a versioned local publish directory. Native installation, updates, CI authentication, and cloud deployment workflows are not implemented. Any future deployment must use versioned artifacts and document changes to these environment boundaries.

Provider credentials and repository access belong on the desktop host. Mobile access must be authenticated and limited to authorized sessions. Logs must not expose credentials. Any relay of agent approvals must preserve the underlying permission model.

## Implementation and verification status

Current 0.1.3: native desktop and Android apps, code pairing/revocation, VS Code binary discovery, owner-selected permissions, top conversation tabs, rename/default names, active steering, grouped timestamps, work timing, provider usage windows, persistent scheduling/usage deferral, per-server encrypted credentials and drafts, Wi-Fi setup and packaged internet connector flows. Builds, focused protocol/access/scheduler checks and a native emulator check pass; see [the current verification record](SETUP.md#verification-commands-and-limits). A read-only real-provider check confirms the selected model catalog entry and both usage windows. Physical Wi-Fi/Android, external internet and successful real inference still need acceptance checks. Historical milestone records follow.

Implemented: provider adapter, ASP.NET Core host, mobile browser UI, SQLite session registry, device pairing/authentication/revocation, SignalR updates, history, messages, supported approvals and questions, interruption, reconnect reconciliation, certificate setup, and local build/publish scripts.

Verified locally: production client build, warning-free server build, focused deterministic access-control and protocol checks, and Android-sized browser checks. Certificate generation and authenticated HTTPS passed using the generated CA without disabling certificate validation, under normal Windows execution permissions. Restricted sandbox execution could not load the Windows user key store. See [setup and verification](SETUP.md) for exact commands and the final validation record.

Version 0.1.1 adds a paired-only sign-in and model diagnostic using the same Codex process as chat requests. Focused regression and browser checks pass, and read-only real-provider checks identified different catalogs between the npm CLI and the newer VS Code bundled CLI. The user chose to retain `gpt-6.1-sol`; the existing executable override can select the IDE's newer binary without changing that model or global configuration. See [the setup guide](SETUP.md#check-codex-sign-in-and-model-compatibility).

Not verified: a successful real model response, a real agent approval round trip, physical Android trust and Wi-Fi access, or broader operating-system support. A user-reported model turn failed on the older CLI. Automatic approval review rejected the agent's live-model check because it can incur usage and operate with desktop permissions; that check requires explicit user approval. No full test suite or public deployment was run.
