# WI-14-4 — Self-hosted runner workflow

- **Feature:** [F14 — Deployment automation](../../Features/F14-deployment-automation.md)
- **Status:** Planned
- **Depends on:** WI-14-3
- **Estimate:** S

## Goal

A push to the deploy branch, or a manual run, deploys through the runner on the server.

## Scope

- In: `.github/workflows/deploy.yml`.
- Out: CI for pull requests.

## Tasks

- [ ] Triggers: `push` to `main` and `workflow_dispatch` with a branch input (default `main`). No `pull_request` trigger.
- [ ] While the repo is not private: test host only, and set Actions to require approval for all outside contributors.
- [ ] `runs-on: [self-hosted, deploy]`; `concurrency` group so deployments never overlap, without cancelling one in progress.
- [ ] Runs `deploy.py` from the deploy clone with the branch; least-privilege `permissions: contents: read`.
- [ ] Always uploads the newest build and deploy logs as artifacts.
- [ ] Add a step to run `dotnet test` before deploying, or record why not.

## Acceptance criteria

- [ ] Push to the branch deploys; a failed health check fails the run.
- [ ] A dispatch with a branch outside `DEPLOY_ALLOWED_BRANCHES` fails without touching the server.

## Notes

Before a server with real data uses this runner, make the repo private (F14 business rules). Runner setup is documented in WI-14-5.
