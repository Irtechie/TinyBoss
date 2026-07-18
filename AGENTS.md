# Project Context

This repo is configured for the PiDev starter pack.

Use `pidev-plan` for planning, review, and repo orientation. Use `pidev-exec` only after the work has a clear plan, a safe branch, and a verification command.

Repo-local memory, plans, handoffs, provider routing, and local-model notes live under `.pidev/`.

Rules:

- Keep durable state in repo files, not chat memory.
- Do not put app-specific facts in global memory.
- Do not execute vague workstream bullets; create a slice plan or KB manifest first.
- Do not edit `.pi/settings.json`, `.git/`, secrets, credentials, or private key files through an agent.
- Do not run destructive Git or filesystem commands without human approval.
- Prefer repo-local test/build commands over global assumptions.
