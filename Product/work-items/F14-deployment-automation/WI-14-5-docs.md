# WI-14-5 — Automation README, Docker, Caddy and Coolify docs

- **Feature:** [F14 — Deployment automation](../../Features/F14-deployment-automation.md)
- **Status:** Planned
- **Depends on:** WI-14-4
- **Estimate:** S

## Goal

An operator can set up a server from the docs and knows the HTTPS and Coolify options.

## Scope

- In: `automation/README.md`, root `README.md` sections, AGENTS.md command list, architecture follow-up.
- Out: running Caddy or Coolify from these scripts.

## Tasks

- [ ] `automation/README.md`: host prerequisites, deploy clone, `/data` ownership and UID, `deploy.env`, runner install with the `deploy` label, running each script by hand, reading logs, manual rollback and recovery.
- [ ] Root `README.md`: "Running in Docker", "HTTPS with Caddy" and "Coolify as an alternative", from the text in F14.
- [ ] AGENTS.md → Commands: automation entry points.
- [ ] Architecture §14: close the HTTPS question (D24).
- [ ] Update the single-executable section to say it remains supported.

## Acceptance criteria

- [ ] Someone following only the docs brings up a server, deploys, and reaches the app over HTTPS through Caddy.
- [ ] The Coolify note states the `/data` mount, `/health`, port and the one-container limit.

## Notes

Coolify and Caddy content is drafted in the F14 spec.
