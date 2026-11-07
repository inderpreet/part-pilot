---
description: Implement a work item end to end
argument-hint: <WI-id e.g. WI-02-1>
---

Implement work item $ARGUMENTS.

1. Locate the file `Product/work-items/*/$ARGUMENTS-*.md`. Read it, its parent feature spec, and `Product/architecture.md`.
2. Verify every item in **Depends on** has `Status: Done`. If not, stop and tell me.
3. Set the work item `Status: In Progress`.
4. Implement following `AGENTS.md` conventions. Add/adjust tests. Add an EF migration if the schema changed.
5. Run `dotnet build` and `dotnet test`; fix failures.
6. Tick completed checklist items, set `Status: Done`, update the status in `Product/README.md`.
7. Note any spec deviations or new open questions in the feature doc. Summarize what changed.
