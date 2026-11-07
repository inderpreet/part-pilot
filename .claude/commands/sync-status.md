---
description: Reconcile work-item and feature statuses with Product/README.md
---

1. Read every `Product/work-items/**/WI-*.md` and collect `Status`.
2. Derive each feature status: all Done → `Done`; any In Progress/Done → `In Progress`; else `Planned`.
   Update the `Status` line in each `Product/Features/Fxx-*.md`.
3. Update both status tables in `Product/README.md` to match.
4. Report any mismatches found and any work item whose dependencies are not Done but which is In Progress/Done.
