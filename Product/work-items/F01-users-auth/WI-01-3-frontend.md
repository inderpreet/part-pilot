# WI-01-3 — Login, setup, user admin, settings pages

- **Feature:** [F01 — Users & authentication](../../Features/F01-users-auth.md)
- **Status:** Planned
- **Depends on:** WI-01-2
- **Estimate:** M

## Goal

Users can set up, sign in, manage their password; Admins manage users and business settings.

## Scope

- In: Account pages, Admin/Users, Admin/Settings, nav/user menu role-awareness, AccessDenied.
- Out: audit viewer (F08).

## Tasks

- [ ] `/Account/Setup`: company name, admin username, display name, password + confirm; 404 when users exist.
- [ ] `/Account/Login`: username, password, remember me; lockout message; redirect to `ReturnUrl` (local only).
- [ ] `/Account/Logout` POST from user menu.
- [ ] `/Account/ChangePassword`.
- [ ] `/Admin/Users`: table (username, display name, role, active badge, last sign-in) with search + paging.
- [ ] `/Admin/Users/Edit/{id?}`: create (with initial password) / edit (role select, active toggle); "Reset password" modal.
- [ ] `/Admin/Settings`: company name, currency code/symbol, allow negative stock, PO number prefix.
- [ ] `/AccessDenied` page.
- [ ] Layout: user menu (display name, change password, logout); hide nav items/actions the user's role cannot use.
- [ ] Page tests: login success/failure, setup flow, Viewer cannot reach `/Admin/*`.

## Acceptance criteria

- [ ] End to end: fresh DB → setup → login → create Staff user → sign in as Staff → no Admin menu.
- [ ] Wrong password 5 times shows lockout message.
- [ ] Settings change is reflected in header company name immediately.

## Notes

Keep the login page usable on a tablet; autofocus username.
