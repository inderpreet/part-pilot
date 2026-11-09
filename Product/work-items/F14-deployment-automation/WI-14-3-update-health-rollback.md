# WI-14-3 — Update, health check, rollback, deploy.py

- **Feature:** [F14 — Deployment automation](../../Features/F14-deployment-automation.md)
- **Status:** Planned
- **Depends on:** WI-14-2
- **Estimate:** M

## Goal

One command deploys a branch, verifies it, and puts the previous version and data back if it is unhealthy.

## Scope

- In: `update_container.py`, `health_check.py`, `deploy.py`, tests.
- Out: GitHub workflow (WI-14-4).

## Tasks

- [ ] `update_container.py` per F14 FR-6: stop old, copy the database and WAL files to a timestamped `backups/` folder, start new with the same name, mount, env file and restart policy, on `127.0.0.1:<port>` by default.
- [ ] Tags: `current` and `previous` move only after a successful start. `--rollback` restores the copy and starts `previous`.
- [ ] Keep a bounded number of pre-deploy copies.
- [ ] `health_check.py` per F14 FR-7: poll, timeout, `--expect-sha`, exit codes, last response logged.
- [ ] `deploy.py` per F14 FR-8: pull, re-launch freshly pulled scripts, build, update, health, automatic rollback, one summary line per step in a deploy log.
- [ ] Handle first deployment: no existing container and no `previous` tag.
- [ ] Unit tests with a fake runner: order of steps, rollback on unhealthy, no rollback attempted on first deploy, backup precedes start.

## Acceptance criteria

- [ ] Rehearsed on a test host: a good deploy, then a deliberately unhealthy image, returns to the previous version with its data intact.
- [ ] A failed build leaves the running container untouched.

## Notes

Database migrations run at app start, so a rollback must restore the pre-deploy copy; the older image cannot be assumed to read a newer schema. Anything written between the new container starting and the rollback is lost, which is a window of seconds.
