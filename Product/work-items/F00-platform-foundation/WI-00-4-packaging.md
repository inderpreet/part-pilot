# WI-00-4 — Single-file publish script

- **Feature:** [F00 — Platform foundation](../../Features/F00-platform-foundation.md)
- **Status:** Planned
- **Depends on:** WI-00-2
- **Estimate:** S

## Goal

One command produces a self-contained single executable per platform that runs with no other files.

## Scope

- In: publish properties, `scripts/publish.ps1` (+ `publish.sh`), smoke test, README run instructions.
- Out: installers, code signing, CI pipeline (future).

## Tasks

- [ ] Publish properties in csproj (Release): `PublishSingleFile`, `SelfContained`, `IncludeNativeLibrariesForSelfExtract`,
      `EnableCompressionInSingleFile`, `DebugType=none`, `PublishTrimmed=false`, `InvariantGlobalization=false`.
- [ ] Version stamping from `Directory.Build.props` (`Version`), shown in footer and `/health`.
- [ ] `scripts/publish.ps1 -Runtime <rid> [-All]` → `publish/<rid>/InventoryTracker[.exe]`; prints size.
- [ ] `scripts/publish.sh` equivalent.
- [ ] Smoke script: start exe with temp `--db`, poll `/health` until 200, stop, delete temp files.
- [ ] Root `README.md` section: download, run, flags, run as Windows Service / systemd unit, backup/restore.

## Acceptance criteria

- [ ] `publish/win-x64` contains exactly one executable (no `wwwroot`, no `.dll`, no `e_sqlite3`).
- [ ] Copying the exe alone to a clean Windows machine without .NET and running it serves the app.
- [ ] Smoke script passes for win-x64 and linux-x64.

## Notes

See architecture §10. Record actual binary size in architecture after first publish.
