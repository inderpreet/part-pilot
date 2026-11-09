# WI-14-2 — automation/ base, pull and build scripts

- **Feature:** [F14 — Deployment automation](../../Features/F14-deployment-automation.md)
- **Status:** Planned
- **Depends on:** WI-14-1
- **Estimate:** M

## Goal

An operator can pull a defined branch and build a logged image with one command each.

## Scope

- In: `common.py`, `pull_branch.py`, `build_image.py`, config example, ignore rules, unit tests.
- Out: running containers (WI-14-3).

## Tasks

- [ ] `automation/common.py`: `deploy.env` parsing with flag overrides, console and file logging, command runner with secret redaction, log retention.
- [ ] `automation/deploy.env.example`: `DEPLOY_REPO_DIR`, `DEPLOY_BRANCH`, `DEPLOY_ALLOWED_BRANCHES`, `DEPLOY_IMAGE`, `DEPLOY_CONTAINER`, `DEPLOY_DATA_DIR`, `DEPLOY_ENV_FILE`, `DEPLOY_PORT`, `DEPLOY_HEALTH_URL`, `DEPLOY_HEALTH_TIMEOUT`, `DEPLOY_LOG_KEEP`, `DEPLOY_IMAGE_KEEP`.
- [ ] `.gitignore`: `automation/deploy.env`, `automation/logs/*` (keep `.gitkeep`).
- [ ] `pull_branch.py` per F14 FR-4: allow-list, dirty check, `--force`, fetch and hard reset, prints the commit.
- [ ] `build_image.py` per F14 FR-5: build from `HEAD`, sha tag and commit label, log file, retention of logs and images (never the running image).
- [ ] `DEPLOY_BRANCH` and `DEPLOY_ALLOWED_BRANCHES` default to `main` in the example.
- [ ] Unit tests with `unittest` and a fake command runner: branch refused, dirty tree refused, log created, retention keeps the newest, secrets redacted. They pass on Windows (dev laptop) and Linux (host); real `git` and `docker` runs happen on Linux.

## Acceptance criteria

- [ ] `pull_branch.py --branch <not allowed>` exits non-zero and changes nothing.
- [ ] A failed build exits non-zero and its log holds the full output.
- [ ] Standard library only.

## Notes

Record Python as an operator-side tool in D23 when implementing. It is not part of the shipped app.
