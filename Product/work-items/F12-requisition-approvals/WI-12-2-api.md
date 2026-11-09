# WI-12-2 — Manager policies, decision service

- **Feature:** [F12 — Requisition approvals](../../Features/F12-requisition-approvals.md)
- **Status:** Planned
- **Depends on:** WI-12-1, WI-11-2
- **Estimate:** M

## Goal

A Manager can do what Staff can and can decide requisitions, with the rules in the service.

## Scope

- In: policies, `CurrentUser`, data-permission handling, decision service, tests.
- Out: screens (WI-12-3).

## Tasks

- [ ] `CanEdit` and `CanView` include Manager; new policy `CanApprove` (Admin, Manager), repeating the active check.
- [ ] `ICurrentUser.CanEdit` includes Manager; add `CanApprove`.
- [ ] `DataPermissionHandler` and `SettingsService.DataPermissionSettings` handle Manager; today an unlisted role is denied.
- [ ] `DecideAsync(requisitionId, rowVersion, lineDecisions, actor)`: one transaction; requires `Submitted`, a decision for every line, approved quantity 1 to requested, reason on reject or reduce; sets `Approved` or `Rejected` and `DecidedAt`.
- [ ] Allow Managers and Admins to decide their own requisitions; `DecidedById` and `RequestedById` show when that happened.
- [ ] `CancelSubmittedAsync` with a reason. Queue query and awaiting-decision count.
- [ ] Publish decided and cancelled through `IRequisitionEvents`.
- [ ] `AppFactory`: `X-Test-Role: Manager`. Extend the policy matrix and the data-permission matrix tests.
- [ ] Tests: approve all, partial, reject all, quantity above request refused, missing reason refused, self-decision allowed for Manager and Admin, double decision refused, Staff and Viewer refused.

## Acceptance criteria

- [ ] Every existing `CanEdit` page works for a Manager.
- [ ] Staff and Viewers get 403 on approval endpoints.
- [ ] Two simultaneous decisions: one wins, the other gets a conflict.

## Notes

Update AGENTS.md → Authorization and architecture §8 for the new role and policy in this change.
