# WI-14-1 — Dockerfile, /data layout, trusted proxies

- **Feature:** [F14 — Deployment automation](../../Features/F14-deployment-automation.md)
- **Status:** Planned
- **Depends on:** WI-00-4
- **Estimate:** M

## Goal

The app runs correctly in a container with persistent data, and correctly behind an HTTPS proxy.

## Scope

- In: `Dockerfile`, `.dockerignore`, forwarded-header support.
- Out: scripts (WI-14-2, WI-14-3).

## Tasks

- [ ] Multi-stage `Dockerfile`: SDK stage runs the architecture §10 publish; runtime stage is `runtime-deps`; `RID` build argument, default `linux-x64`.
- [ ] Non-root user; `/data` owned by it. `ENV INVENTORY_BIND=all`, `INVENTORY_DB=/data/inventory.db`. `EXPOSE 5080`.
- [ ] Confirm logs and data-protection keys land under `/data` (they follow the DB directory).
- [ ] Confirm a build from the Windows dev laptop matches one on the Linux host (`.gitattributes` already forces LF for `Dockerfile` and `*.py`).
- [ ] `.dockerignore`: `.git`, `.data`, `.env`, `bin`, `obj`, `publish`, `artifacts`, `*.db`, `automation/logs`.
- [ ] `INVENTORY_TRUSTED_PROXIES` (CIDR list): when set, enable `UseForwardedHeaders` for `X-Forwarded-For` and `X-Forwarded-Proto` from those networks only. Unset means unchanged behaviour.
- [ ] Add it to architecture §9, `.env.example`; note in D24 that the cookie stays `SameAsRequest` and becomes `Secure` behind HTTPS.
- [ ] Tests: forwarded proto honoured from a trusted address, ignored from others and when unset.

## Acceptance criteria

- [ ] `docker build` succeeds from a clean clone. Container reaches `/health` healthy.
- [ ] Data, sign-ins and keys survive replacing the container.
- [ ] No `.env` or database file in the image.

## Notes

Non-root ownership of a bind-mounted `/data` must be set on the host; document the UID in WI-14-5. The built-in `/health` is polled from the host, so the image needs no curl.
