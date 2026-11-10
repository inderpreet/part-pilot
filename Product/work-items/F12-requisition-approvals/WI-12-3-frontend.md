# WI-12-3 — Approval queue, decision form, role and permission UI

- **Feature:** [F12 — Requisition approvals](../../Features/F12-requisition-approvals.md)
- **Status:** Done
- **Depends on:** WI-12-2, WI-11-3
- **Estimate:** M

## Goal

Managers work through a queue and decide line by line; requesters see the outcome.

## Scope

- In: `/Requisitions/Approvals`, `/Requisitions/{id}/Decide`, detail outcome, nav badge, user edit, Settings column.
- Out: conversion (F13).

## Tasks

- [x] Queue: oldest first, requester, needed by, lines, estimated value. Own requisitions are marked and can be decided.
- [x] Decision form per F12 FR-4: approve all shortcut, quantity defaults to requested, reason required when rejecting or reducing, server errors shown per row.
- [x] Detail page: decision, approved quantity, reason and approver per line; note when the requester decided it.
- [x] Nav: "Approvals" with a count badge for `CanApprove`, like the low-stock badge.
- [x] Role choice shows Manager; Roles table in `Product/README.md` drops the planned marker.
- [x] "Data permissions" card gains a Manager column.
- [x] Page tests: queue and form smoke, Staff 403, nav hidden for Staff, full approve and partial approve end to end, Manager column saves.

## Acceptance criteria

- [x] A Manager can approve one line, reduce another and reject a third in a single submit.
- [x] Layout has no horizontal page overflow at 390, 768 and 1440 px, measured in a browser.

## Notes

The decision form is wide; at 48rem (768 px) and below each line becomes a stacked card rather than a scrolling table. Overflow measured in headless Edge on the queue, decision form (with and without errors), decided detail, cancel menu and Settings.
