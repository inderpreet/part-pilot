# WI-01-2 — Auth config, policies, UserService

- **Feature:** [F01 — Users & authentication](../../Features/F01-users-auth.md)
- **Status:** Done
- **Depends on:** WI-01-1
- **Estimate:** M

## Goal

Cookie authentication, authorization policies and a user-management service enforcing the F01 business rules.

## Scope

- In: Identity options, cookie options, policies, first-run middleware, `UserService`, `ICurrentUser`, tests.
- Out: pages (WI-01-3).

## Tasks

- [x] `AddIdentityCore<ApplicationUser>().AddRoles<IdentityRole>().AddEntityFrameworkStores<AppDbContext>().AddSignInManager()`.
- [x] Password: `RequiredLength=8`, other complexity flags off. Lockout: 5 attempts, 15 min. `RequireUniqueEmail=false`.
- [x] Cookie: `LoginPath=/Account/Login`, `AccessDeniedPath=/AccessDenied`, sliding 8 h, `HttpOnly`, `SameSite=Lax`, `SecurePolicy=SameAsRequest`.
- [x] Security stamp validation interval 1 min; custom validator signs out inactive users.
- [x] Policies `RequireAdmin`, `CanEdit`, `CanView`; fallback policy = authenticated + active.
- [x] `FirstRunMiddleware`: when no users exist, redirect all non-static requests to `/Account/Setup` (cached flag after first user).
- [x] `ICurrentUser` (Id, UserName, DisplayName, roles) from `HttpContext`.
- [x] `UserService`: `CreateFirstAdminAsync`, `ListAsync(ListQuery)`, `GetAsync`, `CreateAsync`, `UpdateAsync` (role, active, display name),
      `ResetPasswordAsync`, `ChangeOwnPasswordAsync`, `RecordSignInAsync`.
- [x] Rules: last active Admin cannot be deactivated/demoted; one role per user.
- [x] Settings update method on `ISettingsService` validated (currency code 3 letters, prefix max 10 chars).
- [x] Tests: user rules, last-admin guard, policy matrix (anonymous/Viewer/Staff/Admin) via test auth handler.

## Acceptance criteria

- [x] Fresh install: any URL redirects to `/Account/Setup`; after admin creation, it redirects to login instead.
- [x] Deactivated user's existing session is rejected within 1 minute.
- [x] Demoting/deactivating the last Admin returns a validation error.
- [x] Test matrix shows Viewer gets 403 on a `CanEdit` endpoint.

## Notes

Sign-in events will be audited in F08 — expose hooks/events (`IUserEvents`) rather than writing audit rows here.

## Implementation notes

- Cookie scheme is registered with `AddIdentityCookies` so every scheme Identity signs out of on a rejected session exists. Registering only the application cookie made a deactivated user's next request return 500 (caught by the live walk-through; regression test `An_existing_session_is_rejected_cleanly_once_the_user_is_deactivated`).
- Every policy, and the fallback, requires an authenticated user carrying the `inv:active` claim. Re-validation interval is `Auth:ValidationIntervalSeconds` (default 60; tests use 0).
- Admin-affecting mutations and first-run setup are serialised by a static semaphore in `UserService`, so two concurrent requests cannot both demote the last two admins.
- Data-protection keys persist to `<db dir>/keys` so sign-in cookies and antiforgery tokens survive restarts (architecture D12).
