# WI-01-3 — Login, setup, user admin, settings pages

- **Feature:** [F01 — Users & authentication](../../Features/F01-users-auth.md)
- **Status:** Done
- **Depends on:** WI-01-2
- **Estimate:** M

## Goal

Users can set up, sign in, manage their password; Admins manage users and business settings.

## Scope

- In: Account pages, Admin/Users, Admin/Settings, nav/user menu role-awareness, AccessDenied.
- Out: audit viewer (F08).

## Tasks

- [x] `/Account/Setup`: company name, admin username, display name, password + confirm; 404 when users exist.
- [x] `/Account/Login`: username, password, remember me; lockout message; redirect to `ReturnUrl` (local only).
- [x] `/Account/Logout` POST from user menu.
- [x] `/Account/ChangePassword`.
- [x] `/Admin/Users`: table (username, display name, role, active badge, last sign-in) with search + paging.
- [x] `/Admin/Users/Edit/{id?}`: create (with initial password) / edit (role select, active toggle); "Reset password" modal.
- [x] `/Admin/Settings`: company name, currency code/symbol, allow negative stock, PO number prefix.
- [x] `/AccessDenied` page.
- [x] Layout: user menu (display name, change password, logout); hide nav items/actions the user's role cannot use.
- [x] Page tests: login success/failure, setup flow, Viewer cannot reach `/Admin/*`.

## Acceptance criteria

- [x] End to end: fresh DB → setup → login → create Staff user → sign in as Staff → no Admin menu.
- [x] Wrong password 5 times shows lockout message.
- [x] Settings change is reflected in header company name immediately.

## Notes

Keep the login page usable on a tablet; autofocus username.

## Implementation notes

- **Deviation:** `/Account/Logout` is `[AllowAnonymous]` (spec table said CanView). It is POST-only and antiforgery-protected; a deactivated or role-less user must still be able to sign out instead of hitting Access Denied. GET redirects to `/`.
- Nav items for sections that do not exist yet (Products, Stock, Suppliers, Purchase Orders, Reports) render as inert labels (`Available: false` in `NavItems.cs`) so they do not 404. **Flip `Available` to `true` in the work item that adds each section's first page.**
- `/Account/ChangePassword` refreshes the sign-in afterwards so the user stays signed in while other sessions end.
- Verified by xUnit page tests (`AccountPagesTests`, `AdminPagesTests`) and by a live HTTP walk-through of the whole flow.
