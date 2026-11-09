# F14 — Deployment automation

- **Status:** Planned
- **Depends on:** F00
- **Roles:** Operators only (no in-app screens)

## Summary

Deploys the app as a Docker container on one server. A GitHub Action on a **self-hosted runner** on that server runs Python scripts in a new
`automation/` folder: pull a defined branch, build an image and log the build, update the running container, and health-check it, rolling back on failure.
The repo has no Dockerfile or workflow today, so both are part of this feature. The single-executable distribution stays and is unaffected.

## User stories

- As an operator, I want a push to the deploy branch to reach the server without logging in to it.
- As an operator, I want each step runnable by hand when something goes wrong.
- As an operator, I want a build log for every deployment.
- As an operator, I want a failed deployment to put the previous version back.
- As an operator, I want to know how to get HTTPS, and whether Coolify could do this job instead.

## Functional requirements

1. FR-1 `Dockerfile` (multi-stage, Linux containers) publishing the existing single-file `linux-x64` build (architecture §10, no trimming or AOT), with `.dockerignore`. Runs as non-root. All state in `/data`: `INVENTORY_DB=/data/inventory.db`, so keys and logs follow (D12).
2. FR-2 `automation/` layout. One script per job, as asked, sharing a helper module, plus one orchestrator:

   | File | Job |
   |---|---|
   | `common.py` | Config loading, logging to file and console, running commands, log retention |
   | `pull_branch.py` | Fetch and hard-reset the deploy clone to a defined branch |
   | `build_image.py` | Build the image from the code currently pulled; log to `automation/logs/` |
   | `update_container.py` | Back up the database, replace the container, roll back on request |
   | `health_check.py` | Poll `/health` until healthy or timed out |
   | `deploy.py` | Runs the four in order; the entry point for the workflow |
   | `deploy.env.example` | Documented settings; the real `deploy.env` is git-ignored |
   | `logs/` | Git-ignored, except `.gitkeep` |

3. FR-3 Python 3.11+, **standard library only**, calling `git` and `docker` through `subprocess`. Every script supports `--help`, reads `automation/deploy.env`, accepts flag overrides, and exits non-zero on failure.
4. FR-4 `pull_branch.py`: branch comes from `--branch` or `DEPLOY_BRANCH` and must be listed in `DEPLOY_ALLOWED_BRANCHES`. Runs only in a clone marked as a deploy clone by `DEPLOY_REPO_DIR`, and refuses local changes unless `--force`, because it discards them. Prints the deployed commit.
5. FR-5 `build_image.py`: builds the commit at `HEAD`, tags `<image>:<short sha>`, records the commit as an image label, and writes the full output to `automation/logs/build-<UTC timestamp>-<short sha>.log`. Keeps the last `DEPLOY_LOG_KEEP` logs and `DEPLOY_IMAGE_KEEP` images.
6. FR-6 `update_container.py`: one container at a time, because two processes must not share the SQLite file across containers. Stop the old one, copy the database and its WAL files to `/data/backups/pre-deploy-<sha>-<timestamp>/`, start the new one with the same settings, and retag `current` and `previous`. Publishes on `127.0.0.1` by default, for use behind a reverse proxy.
7. FR-7 `health_check.py`: polls `DEPLOY_HEALTH_URL` (`/health`, anonymous, already reports database state), up to a timeout. Exit 0 healthy, 1 unhealthy. Also confirms the running container's commit label when `--expect-sha` is given.
8. FR-8 `deploy.py`: pulls, then re-launches the freshly pulled scripts for the later steps. On a failed health check it restores the pre-deploy database copy, starts the previous image, checks it and exits non-zero so the workflow fails.
9. FR-9 `.github/workflows/deploy.yml`: triggers on push to the deploy branch and `workflow_dispatch`, `runs-on: [self-hosted, deploy]`, one deployment at a time (`concurrency`), runs `deploy.py`, uploads the build log as an artifact.
10. FR-10 Proxy awareness, off by default: `INVENTORY_TRUSTED_PROXIES` (CIDR list) turns on forwarded headers so the app sees HTTPS and its `SameAsRequest` cookie becomes `Secure`. Needed for Caddy and Coolify, because the app itself only speaks HTTP.
11. FR-11 Docs: `automation/README.md` (host setup, runner install, manual runs, recovery), and new root `README.md` sections on Docker, HTTPS with Caddy and Coolify. Content below.

## Business rules

- The deploy clone is dedicated. Its local changes can be discarded, so it is never a working copy.
- The workflow never runs on `pull_request`. A self-hosted runner executes whatever a workflow gives it, so outside code must not reach it. Fork pull requests would be a route to the server if the repo is public.
- **Until the repo is private, deployment is test use only:** a throwaway host with no real data or credentials, and the repo's Actions setting requires approval for outside contributors. Make the repo private before pointing the runner at a server holding real data.
- Scripts run on the Linux deployment host, and are developed on Windows: no shell-specific commands, `pathlib` for paths, and LF line endings forced for `*.py` and `Dockerfile`, so a Windows checkout cannot break the container build or the scripts.
- Secrets stay on the host in `automation/deploy.env` and the container env file. They are never logged, committed or passed through GitHub.
- The host publishes the port on loopback when a proxy is in use, so plain HTTP is not exposed.

## README content to add (WI-14-5)

**HTTPS with Caddy.** The app speaks HTTP. Put Caddy in front; it obtains and renews certificates automatically:

```
inventory.example.com {
    reverse_proxy 127.0.0.1:5080
}
```

Set `INVENTORY_TRUSTED_PROXIES` to the proxy's address range, keep the app's port off the public interface, and for a LAN-only name use `tls internal` and trust Caddy's root certificate on the devices.

**Coolify as an alternative.** [Coolify](https://coolify.io) can replace `automation/` entirely: it builds the Dockerfile from a Git branch, deploys on push and terminates HTTPS. Point it at this repo with the Dockerfile build pack, mount persistent storage at `/data`, set the health check path to `/health` and port to `5080`, and set `INVENTORY_TRUSTED_PROXIES`. Avoid running two containers on the same database: use stop-then-start rather than a rolling update, as SQLite in `/data` must have one app process at a time. Trade-off: a bigger platform to run, in exchange for no scripts to maintain.

## Data model

None. Backups are files in `/data/backups/`.

## Screens / endpoints

| Route | Purpose | Policy |
|---|---|---|
| `/health` (existing) | Health check target | Anonymous |

## Out of scope

- Windows containers, Kubernetes, multi-host or zero-downtime deployment (SQLite is single-writer on one host).
- A container registry; images are built on the server that runs them.
- Running Caddy or Coolify from these scripts.
- Replacing the single-executable or service install routes.

## Work items

These do not follow the usual schema, API, frontend split, as there is no schema or UI.

| ID | Title | Status |
|---|---|---|
| [WI-14-1](../work-items/F14-deployment-automation/WI-14-1-docker-image.md) | Dockerfile, /data layout, trusted proxies | Planned |
| [WI-14-2](../work-items/F14-deployment-automation/WI-14-2-pull-and-build.md) | automation/ base, pull and build scripts | Planned |
| [WI-14-3](../work-items/F14-deployment-automation/WI-14-3-update-health-rollback.md) | Update, health check, rollback, deploy.py | Planned |
| [WI-14-4](../work-items/F14-deployment-automation/WI-14-4-github-workflow.md) | Self-hosted runner workflow | Planned |
| [WI-14-5](../work-items/F14-deployment-automation/WI-14-5-docs.md) | Automation README, Docker, Caddy and Coolify docs | Planned |

## Open questions

- ~~Which branch is the deploy branch?~~ Decided 2026-11-09: `main`, the default for `DEPLOY_BRANCH`, `DEPLOY_ALLOWED_BRANCHES` and the workflow.
- ~~Is the GitHub repo private?~~ Decided 2026-11-09: it goes private later; this is test use until then (see Business rules).
- ~~Server architecture?~~ Decided 2026-11-09: deployment is `linux-x64`; development is on a Windows laptop.
- Should failed deployments notify anyone beyond the failed workflow run?
