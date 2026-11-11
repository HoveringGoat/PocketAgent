# Working agreement

## Planning and implementation

- Before writing code, summarize the planned change and wait for explicit user approval unless the user is asking to fix a specific concrete error.
- Before implementation, list the exact behavior changes the planned work implies so assumptions can be checked.
- Documentation-only updates under documentation do not require prior signoff.
- Never override or revert a user-requested direction back to an earlier approach without confirming intent.
- When the user asks why or what happened, answer directly before making more edits.
- Clean up obsolete scaffolding or helper code in the same change rather than leaving dead code behind.
- This is an independent repository. Keep its implementation, tooling, and documentation here; do not modify unrelated repositories as part of this project.

## Documentation workflow

- Documentation must explain how a feature or system is intended to work as well as actual implementation details.
- Use documentation/README.md and documentation/TABLEOFCONTENTS.md as the first lookup path before digging through implementation files.
- If documentation is insufficient and behavior must be traced manually, update the relevant durable documentation in the same task.
- When behavior changes land, update the relevant documentation page in the same work unless the user explicitly says not to.
- Prefer updating an existing documentation page over creating a new one unless the topic clearly needs its own page.
- When adding a new documentation page, update both documentation/README.md and documentation/TABLEOFCONTENTS.md.
- Keep docs durable and code-adjacent: architecture, flow, invariants, failure modes, key files, and change points.
- Clearly distinguish intended behavior, open decisions, implemented behavior, and verified behavior.

## Build and test notes

- Prefer solution- or project-level build commands over low-level compiler invocations unless the user asks for something narrower.
- Do not run full test suites unless the user explicitly asks for that. Hand test verification back to the user when appropriate.
- For bug fixes, write a correct regression first, observe its failure before changing behavior, then observe it pass. Use focused tests.
- Keep linting, formatting, and warnings clean on touched files before handoff.
- Record validation commands that were run and call out environment limitations that affect verification.
- Increment the patch once per completed change set after the initial 0.1.0 milestone.

## Deployment standards

- Document the default hosting model, artifact flow, and environment boundaries for this workload.
- Use short-lived or federated CI authentication instead of long-lived credentials wherever the platform supports it.
- Use immutable, versioned artifact or image tags for deployments; do not deploy an unqualified latest tag.
- Runtime identities should have only the data-plane permissions required by the workload.
- This repository owns application code, tests, deployment artifacts, and application deployment workflows.
- If infrastructure is maintained separately, that repository owns provisioning, environment configuration, resource references, and access-control setup.
- Do not introduce an alternate deployment model without documenting the exception and its operational impact.
- Ask before destructive operations or public deployment.

## Credentials and agent access

- Never log credentials or commit .env files, access tokens, or private keys.
- Keep provider credentials on the desktop host; do not embed them in mobile client artifacts.
- Preserve the connected agent's permission and approval boundaries when relaying requests.
- Document authentication, device access, session ownership, and network exposure before implementing remote access.
