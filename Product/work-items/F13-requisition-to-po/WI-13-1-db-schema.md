# WI-13-1 — RequisitionLineOrder schema

- **Feature:** [F13 — Requisition to purchase order](../../Features/F13-requisition-to-po.md)
- **Status:** Planned
- **Depends on:** WI-12-1, WI-05-1
- **Estimate:** S

## Goal

A requisition line can be linked to the PO line that orders it, and the link disappears by itself if that PO line goes away.

## Scope

- In: entity, configuration, migration.
- Out: conversion (WI-13-2), screens (WI-13-3).

## Tasks

- [ ] `RequisitionLineOrder`: Id, RequisitionLineId (UQ, Cascade), PurchaseOrderLineId (Cascade, indexed), QuantityOrdered (check ≥ 1), ConvertedById (FK users, Restrict), ConvertedAt.
- [ ] Migration `AddRequisitionLineOrders`; adding a new table leaves `PurchaseOrderLines` untouched.
- [ ] Schema tests: one link per requisition line, several links may share a PO line, deleting the PO line deletes the link and keeps the requisition line.

## Acceptance criteria

- [ ] Migration applies to an empty DB and to the previous schema.
- [ ] Deleting a draft PO removes its links.

## Notes

The cascade is what makes "release" automatic for removed lines and deleted drafts. Check `DeleteDraftAsync` removes lines through the database or a tracked delete that honours it.
