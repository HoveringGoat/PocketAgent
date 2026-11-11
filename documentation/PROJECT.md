# Project brief

## Purpose

Enable the user to follow coding-agent work from a mobile device while the agent works on a desktop machine. A small desktop app/server will connect the mobile client to Codex or Claude and relay relevant information.

## Intended experience

The user opens the mobile client, sees their coding-agent conversations, and follows progress on ongoing work.

The desktop host remains the place where the agent accesses the working repository and performs development tasks. The mobile client presents conversations and work updates received through the desktop component.

Sending prompts, responding to approval requests, stopping work, and starting sessions are possible extensions. Their inclusion in the first milestone requires a product decision.

## Proposed responsibilities

These responsibilities express the current concept, not a finalized architecture.

| Component | Intended responsibility |
| --- | --- |
| Desktop app/server | Connect to an agent, expose relevant conversations and progress to an authorized mobile client, and report connection status. |
| Provider integration | Translate supported Codex or Claude interactions into the relay's conversation and progress representation. The supported integration surface must be verified before implementation. |
| Mobile client | Present conversations, progress, and connection state. Client technology has not been selected. |

The normal information flow is agent to desktop relay to mobile client. If interactive control is included, mobile requests travel back through the relay and remain subject to the agent's permissions.

The availability of existing desktop-app chats, live session attachment, historical messages, and approval interactions must be established for the chosen provider. This document does not assume those capabilities exist.

## Open decisions

- **Desktop environment:** Windows first or multiple operating systems; background service, tray app, or another host shape.
- **Mobile client:** Mobile browser/PWA, native app, or a cross-platform client.
- **Provider scope:** Codex first, Claude first, or both; confirm supported integration mechanisms.
- **Session ownership:** Observe existing sessions, launch sessions through the relay, or support both where possible.
- **Interaction scope:** Follow progress only, or also send messages, start work, stop work, and handle approvals.
- **Connectivity:** Same-network access, private remote connectivity, or another explicitly approved model.
- **Device access:** Pairing/authentication, authorization, and revoking a device.
- **Conversation handling:** What to retain, where to retain it, and how reconnecting clients recover missed updates.

## First implementation milestone

Before code is written, agree on one provider, one desktop target, one mobile client approach, and the connection/access model.

A candidate first milestone is to show one real agent conversation and live progress on a mobile device, with clear connected, disconnected, completed, and failed states where the provider exposes them. This is a proposal for approval.

The implementation plan must specify which sessions can be accessed, which events are available, what happens after disconnects, and whether any mobile actions can change the agent's work.

## Hosting and environment boundaries

The intended product has a user-controlled desktop host and a mobile client. Hosting, packaging, updates, CI authentication, deployment artifact flow, and network transport have not been selected or implemented. Record those decisions and their operational impact before introducing deployment workflows.

Provider credentials and repository access belong on the desktop host. Mobile access must be authenticated and limited to authorized sessions. Logs must not expose credentials. Any relay of agent approvals must preserve the underlying permission model.

## Implementation and verification status

Only the repository instructions and documentation foundation exist. No provider integration, server, mobile UI, persistence, authentication, build pipeline, or deployment is implemented.

There are no application build or test commands yet. Initial validation checks repository initialization, document links, and whitespace. Functional verification will be defined with the first implementation milestone.
