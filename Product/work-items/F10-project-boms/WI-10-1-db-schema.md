# WI-10-1 — Project, BomLine, ProjectBuild schema

- **Feature:** [F10 — Projects & BOMs](../../Features/F10-project-boms.md)
- **Status:** Planned
- **Depends on:** WI-02-1, WI-01-1
- **Estimate:** S

## Goal

Tables for projects, their BOM lines and their recorded builds exist, with the constraints the service relies on. `StockTransactions` is untouched.

## Scope

- In: entities, configurations, `DbSet`s, migration `AddProjects`, schema tests.
- Out: service logic (WI-10-2), pages (WI-10-3, WI-10-4).

## Tasks

- [ ] `Project`: Id, Name (max 100, unique, case-insensitive like `Category.Name`), **Number** (max 30, required, NOCASE, unique; the project number, assigned by the service and changed only through an Admin action, like `PurchaseOrder.Number` is assigned by the service), Description (500), TargetUnits int (>= 1, default 1, CHECK), Notes (2000), IsActive (default true), RowVersion, `CreatedAt`/`UpdatedAt` (timestamp interceptor).
- [ ] `BomLine`: Id, ProjectId (FK `Cascade`; BOM lines go with a deleted project, and only a project with no builds can be deleted), ProductId (FK `Restrict`), QuantityPerUnit int (>= 1, CHECK), Note (200), timestamps. Unique `(ProjectId, ProductId)`. Index on `ProductId`.
- [ ] `ProjectBuild`: Id, ProjectId (FK `Restrict`), Units int (>= 1, CHECK), UserId (FK `AspNetUsers`, `Restrict`), Note (500), `CreatedAt`. No update path: builds are never edited (no `UpdatedAt`).
- [ ] Add `DbSet<Project>`, `DbSet<BomLine>`, `DbSet<ProjectBuild>`; configurations in `Data/Configurations/ProjectConfiguration.cs`.
- [ ] Do **not** add a column or FK to `StockTransaction` (decision D18). A build is linked to its issue rows by `Reference` text.
- [ ] Migration `AddProjects`. Check it creates only the three tables and indexes, and does not rebuild `StockTransactions` (so the D14 triggers are untouched).
- [ ] Tests: migration applies to an empty DB and to the schema as of F09; unique `(ProjectId, ProductId)`; unique project name ignoring case; two projects with the same number (any case) are rejected, and a null number is rejected; CHECKs reject `TargetUnits = 0`, `QuantityPerUnit = 0`, `Units = 0`; deleting a product that is in a BOM fails; deleting a project that has a build fails; deleting a project without builds removes its lines; the stock ledger triggers still exist (`StockSchemaTests` stays green).

## Acceptance criteria

- [ ] Migration applies cleanly to an empty database and to the previous schema.
- [ ] The constraints above are enforced by the database, not only by the service.
- [ ] `dotnet build` has zero warnings and `dotnet test` is green.

## Notes

`Project.Id` is the stable key for relationships inside the database. `Number` is the key outside systems will use (F10 does not build an API): it is unique and indexed, and only an Admin can change it (service rule, WI-10-2). Numbering uses the existing `DocumentSequence` table (name "Project"), so no schema change is needed for it.

Follows the layout of `Data/Entities/PurchaseOrder*.cs` and `PurchaseOrderConfiguration.cs`. Add the three entities to architecture section 5 and record D18 in the decisions log when this item lands.
