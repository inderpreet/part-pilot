# WI-00-4 — Single-file publish script

- **Feature:** [F00 — Platform foundation](../../Features/F00-platform-foundation.md)
- **Status:** Done
- **Depends on:** WI-00-2
- **Estimate:** S

## Goal

One command produces a self-contained single executable per platform that runs with no other files.

## Scope

- In: publish properties, `scripts/publish.ps1` (+ `publish.sh`), smoke test, README run instructions.
- Out: installers, code signing, CI pipeline (future).

## Tasks

- [x] Publish properties in csproj (Release): `PublishSingleFile`, `SelfContained`, `IncludeNativeLibrariesForSelfExtract`,
      `EnableCompressionInSingleFile`, `DebugType=none`, `PublishTrimmed=false`, `InvariantGlobalization=false`.
- [x] Version stamping from `Directory.Build.props` (`Version`), shown in footer and `/health`.
- [x] `scripts/publish.ps1 -Runtime <rid> [-All]` → `publish/<rid>/InventoryTracker[.exe]`; prints size.
- [x] `scripts/publish.sh` + `scripts/smoke.sh` equivalent. **Written but not executed** (no Unix host or WSL distro available during development).
- [x] Smoke script: start exe with temp `--db`, poll `/health` until 200, stop, delete temp files.
- [x] Root `README.md` section: download, run, flags, run as Windows Service / systemd unit, backup/restore.

## Acceptance criteria

- [x] `publish/win-x64` contains exactly one executable (no `wwwroot`, no `.dll`, no `e_sqlite3`).
- [ ] Copying the exe alone to a clean Windows machine without .NET and running it serves the app. *Verified only as far as the smoke test copies the exe alone into an empty temp folder on a machine that has .NET installed; not yet tried on a .NET-free machine.*
- [ ] Smoke script passes for win-x64 and linux-x64. *win-x64 passes. linux-x64 (and linux-arm64, osx-arm64) publish to a single file but have not been run; `scripts/smoke.sh` is untested.*

## Notes

See architecture §10. Record actual binary size in architecture after first publish.

## Result

| RID | Size | Smoke |
|---|---|---|
| win-x64 | 51.1 MB | passed |
| linux-x64 | 52.1 MB | built only |
| linux-arm64 | 49.6 MB | built only |
| osx-arm64 | 49.1 MB | built only |

`publish.ps1` fails the build unless the output folder holds exactly one file. The smoke test uses the exe alone in a temp folder, a temp DB and a free port, and checks /health, four embedded assets, the home page and DB creation.
