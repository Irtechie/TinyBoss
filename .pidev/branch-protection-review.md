# Branch Protection Review

Status: pending

## Scope

- Repository:
- Default branch:
- Remote:
- Reviewed by:
- Reviewed on:

## Evidence

Run from the PiDev harness:

```powershell
npm run report:branch-protection -- --live --output BRANCH_PROTECTION_REPORT.md
```

Record the relevant row here:

```text
<repo-name>: <protected|unprotected-or-inaccessible|failed|skipped>
```

## Decision

Change `Status: pending` to `Status: reviewed` only after a human confirms the repo has acceptable GitHub branch protection or an explicit external safety boundary.

Do not use this file to bypass missing protection silently. If branch protection is unavailable, document the reason and the compensating control before marking reviewed.
