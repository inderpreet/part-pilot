# F11 — Requisitions

- **Status:** Done
- **Depends on:** F01, F02
- **Roles:** Any signed-in user raises and tracks their own requisitions (Viewers included). Admin, Manager and Staff see everyone's.

## Summary

People who need parts ask for them in the app instead of by email or word of mouth. A **requisition** lists catalog parts with
quantities, a reason and an optional needed-by date. Once submitted it waits for a Manager's decision ([F12](F12-requisition-approvals.md)); approved
lines can then be ordered ([F13](F13-requisition-to-po.md)). A requisition never changes stock.

## User stories

- As any signed-in user, I want to request parts with quantities so that purchasing knows what I need and why.
- As a requester, I want to save a draft and finish it later.
- As a requester, I want to withdraw a requisition I submitted by mistake and correct it.
- As a requester, I want to see where each of my requisitions stands.
- As Staff or a Manager, I want to see all requisitions so that I can spot duplicate requests.

## Functional requirements

1. FR-1 Header: Number (auto `<prefix><yyyy>-<seq 4 digits>`, e.g. `REQ-2026-0001`, through `DocumentSequence`), Requested by (the signed-in user),
   Needed by (optional date), Reason (required on submit).
2. FR-2 Lines: active catalog part (picker) and whole-number quantity ≥ 1, optional note. One line per part per requisition.
   The picker shows on hand and on order so the requester can see availability.
3. FR-3 Status machine:
   - `Draft` → `Submitted` (submit; needs at least one line and a reason)
   - `Submitted` → `Draft` (withdraw, by the requester or an Admin, only while undecided)
   - `Submitted` → `Approved` / `Rejected` (decision, F12)
   - `Submitted` | `Approved` → `Cancelled` (see F12 and F13 for who may)
   - `Draft` can be deleted; every other status is kept.
4. FR-4 Lines and header are editable only in `Draft`, only by the requester or an Admin.
5. FR-5 List: filter by status, requester, date range; search by number and part. Tab "Mine" is the default; Viewers only ever get their own.
6. FR-6 Detail page: header, lines with on hand and on order, status history (submitted, decided, by whom).
7. FR-7 Lifecycle events are published through a fan-out `IRequisitionEvents` hook, like `IProjectEvents`, for the F08 audit log.

## Business rules

- A requisition reserves nothing and does not count as on order or in draft. F06 and F10 figures do not change.
- A part archived after the requisition was submitted stays on it and is shown as archived. F13 refuses to order it.
- Quantities are whole units, as everywhere in v1 (`int`).
- Viewers are read-only everywhere else; raising requisitions is the one exception, enforced by a named policy (`CanRequest`) so the exception is visible in code.
- Ownership is a service rule: a Viewer or Staff member can only change their own `Draft`.
- Submit follows the same ownership rule as editing: the requester or an Admin. The history shows who actually submitted (`SubmittedById`).
- Adding, changing or removing a line moves the requisition's `RowVersion`, so a page showing older lines cannot submit them. The editor refreshes its hidden version after each line change.
- A part archived while the requisition is still a draft blocks submit until the line is removed.
- The list's date filter applies to the created date.

## Data model

`Requisition`, `RequisitionLine` — see architecture §5. The status check constraint carries every status from day one, so F12 and F13 do not rebuild the table (D16).

## Screens / endpoints

| Route | Purpose | Policy |
|---|---|---|
| `/Requisitions` | List (Viewers: own only) | CanRequest |
| `/Requisitions/{id}` | Detail (Viewers: own only, otherwise 404) | CanRequest |
| `/Requisitions/Edit/{id?}` | Create or edit a draft, HTMX line add and remove | CanRequest |
| `/Requisitions/{id}?handler=Submit` / `Withdraw` / `Delete` (POST) | Transitions | CanRequest + ownership in service |

## Out of scope

- Parts that are not in the catalog (free-text requests).
- Email or chat notifications; the app does not send mail (see F05).
- Budgets, cost centres, recurring requisitions.
- Issuing stock to a requester from a requisition.

## Work items

| ID | Title | Status |
|---|---|---|
| [WI-11-1](../work-items/F11-requisitions/WI-11-1-db-schema.md) | Requisition, RequisitionLine schema | Done |
| [WI-11-2](../work-items/F11-requisitions/WI-11-2-api.md) | RequisitionService, CanRequest policy | Done |
| [WI-11-3](../work-items/F11-requisitions/WI-11-3-frontend.md) | List, editor, detail | Done |

## Open questions

- ~~Should a requester be able to ask for a part that is not in the catalog yet?~~ Decided 2026-11-09: no, catalog parts only.
- Should a requisition link to a BOM or project (F10)? Assumed no for v1.
- Staff can read other people's drafts as well as submitted requisitions ("see everyone's", FR roles). Should drafts stay private to the requester until submitted? Built as readable; a one-line change in `RequisitionRules.CanSee` and `SearchAsync` if not.
