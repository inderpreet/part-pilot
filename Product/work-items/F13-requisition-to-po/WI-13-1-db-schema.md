# WI-13-1 — RequisitionLineOrder schema

- **Feature:** [F13 — Requisition to purchase order](../../Features/F13-requisition-to-po.md)
- **Status:** Done
- **Depends on:** WI-12-1, WI-05-1
- **Estimate:** S

## Goal

A requisition line can be linked to the PO line that orders it, and the link disappears by itself if that PO line goes away.

## Scope

- In: entity, configuration, migration.
- Out: conversion (WI-13-2), screens (WI-13-3).

## Tasks

- [x] `RequisitionLineOrder`: Id, RequisitionLineId (UQ, Cascade), PurchaseOrderLineId (Cascade, indexed), QuantityOrdered (check ≥ 1), ConvertedById (FK users, Restrict), ConvertedAt.
- [x] Migration `AddRequisitionLineOrders`; adding a new table leaves `PurchaseOrderLines` untouched.
- [x] Schema tests: one link per requisition line, several links may share a PO line, deleting the PO line deletes the link and keeps the requisition line.

## Acceptance criteria

- [x] Migration applies to an empty DB and to the previous schema.
- [x] Deleting a draft PO removes its links.

## Notes

The cascade is what makes "release" automatic for removed lines and deleted drafts. Checked: `DeleteDraftAsync` removes the order and the database cascades to its lines and their links; `RemoveLineAsync` deletes the tracked line and the database removes the link. Schema tests cover both.
