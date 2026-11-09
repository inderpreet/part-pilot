# WI-11-2 — RequisitionService, CanRequest policy

- **Feature:** [F11 — Requisitions](../../Features/F11-requisitions.md)
- **Status:** Done
- **Depends on:** WI-11-1
- **Estimate:** M

## Goal

All requisition rules live in one service, and any signed-in user can use it within the ownership rules.

## Scope

- In: `RequisitionService`, policy, events, tests.
- Out: screens (WI-11-3), decisions (F12).

## Tasks

- [x] Policy `CanRequest`: any active user with a role. Named separately from `CanView` so the Viewer exception is explicit.
- [x] `RequisitionService`: create draft, update header, add, update and remove line, submit, withdraw, delete draft, search, get.
- [x] Number issued through `DocumentSequence` (same retry approach as projects).
- [x] Rules: active parts only, whole quantity ≥ 1, one line per part, reason required on submit, at least one line on submit.
- [x] Ownership via an explicit actor (`RequisitionActor(UserId, CanSeeAll, IsAdmin)`): only the requester or an Admin edits a draft or withdraws; Viewers can read only their own.
- [x] Status transitions enforced in the service, with `RowVersion` conflicts returned as errors.
- [x] `IRequisitionEvents` fan-out publisher (created, submitted, withdrawn), like `IProjectEvents`.
- [x] Tests: each transition and each refused transition, ownership per role, Viewer can create and submit, archived part refused, concurrent edit conflict.

## Acceptance criteria

- [x] A Viewer can raise and submit a requisition, and cannot see another user's.
- [x] A submitted requisition cannot be edited until withdrawn.
- [x] Nothing in this service touches stock or POs.

## Notes

Record in AGENTS.md → Authorization that `CanRequest` is the one write allowed to Viewers, and adjust the "Viewer cannot mutate" definition of done accordingly.
