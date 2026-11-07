# CLAUDE.md

@AGENTS.md

## Claude Code specifics

- Slash commands in `.claude/commands/`:
  - `/new-feature <name>` — create a feature spec + its three work items from templates.
  - `/implement <WI-id>` — implement a work item end to end following the workflow in AGENTS.md.
  - `/sync-status` — reconcile work-item statuses with `Product/README.md`.
- Before implementing, read: the work item, its feature spec, and the relevant sections of `Product/architecture.md`.
- Prefer small, reviewable changes: one work item per change.
