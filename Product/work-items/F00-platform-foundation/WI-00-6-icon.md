# WI-00-6 — App icon from the logo

- **Feature:** [F00 — Platform foundation](../../Features/F00-platform-foundation.md)
- **Status:** Done
- **Depends on:** WI-00-5
- **Estimate:** S

## Goal

The Part Pilot logo (`branding/icon.jpg`, an anvil drawn with circuit traces) is the app's icon everywhere: browser tab, home-screen / touch icon, the executable, the sidebar and the sign-in page.

## Scope

- In: a repeatable script that turns the master JPG into the shipped assets, wiring them into the layouts, the executable's icon, tests, docs.
- Out: a web app manifest / installable PWA, a dark-theme logo variant, replacing the logo with a vector (SVG) version.

## Tasks

- [x] Move the master to `branding/icon.jpg` (kept in the repo; it is the one file to edit).
- [x] `scripts/make-icons.ps1` (Windows, System.Drawing; no new packages): finds the artwork, then writes
      `wwwroot/favicon.ico` (16/32/48/256 px, PNG-compressed), `wwwroot/img/apple-touch-icon.png` (180), `wwwroot/img/icon-192.png`,
      and `wwwroot/img/logo.png` (tight crop, white turned into real transparency without a halo, 160 px wide).
      Tiles are the artwork centred on white with a 10% margin, so they read on light and dark tabs.
- [x] `_Layout` and `_PrintLayout` link the icons; the sidebar brand shows `logo.png` instead of the initial-letter tile; the sign-in / setup header shows it above the company name.
- [x] `ApplicationIcon` in `InventoryTracker.csproj` points at `wwwroot/favicon.ico`, so the published executable carries the same icon.
- [x] Tests: `IconTests` (favicon is a valid multi-size ICO and served anonymously with `no-cache`, touch icons are square PNGs of the right size, the logo has an alpha channel, layouts link the icons and show the logo).

## Acceptance criteria

- [x] `/favicon.ico` returns the logo for signed-out visitors (the sign-in page needs it too).
- [x] The sidebar and sign-in page show the logo; the company name stays as text next to or under it.
- [x] The built executable's file icon is the logo (checked by extracting the associated icon).
- [x] The generated files are embedded like all other assets: the single executable still needs no loose files.
- [x] Builds with zero warnings; `dotnet test` green.

## Notes

- Regenerate with `.\scripts\make-icons.ps1` after replacing `branding/icon.jpg`; commit the regenerated files.
- Logo colours (orange, near-black) are not part of the UI palette; the rest of the interface keeps its indigo accent. Retheming is a separate decision.
