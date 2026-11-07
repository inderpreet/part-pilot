# WI-01-2 — Auth config, policies, UserService

- **Feature:** [F01 — Users & authentication](../../Features/F01-users-auth.md)
- **Status:** Planned
- **Depends on:** WI-01-1
- **Estimate:** M

## Goal

Cookie authentication, authorization policies and a user-management service enforcing the F01 business rules.

## Scope

- In: Identity options, cookie options, policies, first-run middleware, `UserService`, `ICurrentUser`, tests.
- Out: pages (WI-01-3).

## Tasks

- [ ] `AddIdentityCore<ApplicationUser>().AddRoles<IdentityRole>().AddEntityFrameworkStores<AppDbContext>().AddSignInManager()`.
- [ ] Password: `RequiredLength=8`, other complexity flags off. Lockout: 5 attempts, 15 min. `RequireUniqueEmail=false`.
- [ ] Cookie: `LoginPath=/Account/Login`, `AccessDeniedPath=/AccessDenied`, sliding 8 h, `HttpOnly`, `SameSite=Lax`, `SecurePolicy=SameAsRequest`.
- [ ] Security stamp validation interval 1 min; custom validator signs out inactive users.
- [ ] Policies `RequireAdmin`, `CanEdit`, `CanView`; fallback policy = authenticated + active.
- [ ] `FirstRunMiddleware`: when no users exist, redirect all non-static requests to `/Account/Setup` (cached flag after first user).
- [ ] `ICurrentUser` (Id, UserName, DisplayName, roles) from `HttpContext`.
- [ ] `UserService`: `CreateFirstAdminAsync`, `ListAsync(ListQuery)`, `GetAsync`, `CreateAsync`, `UpdateAsync` (role, active, display name),
      `ResetPasswordAsync`, `ChangeOwnPasswordAsync`, `RecordSignInAsync`.
- [ ] Rules: last active Admin cannot be deactivated/demoted; one role per user.
- [ ] Settings update method on `ISettingsService` validated (currency code 3 letters, prefix max 10 chars).
- [ ] Tests: user rules, last-admin guard, policy matrix (anonymous/Viewer/Staff/Admin) via test auth handler.

## Acceptance criteria

- [ ] Fresh install: any URL redirects to `/Account/Setup`; after admin creation, it redirects to login instead.
- [ ] Deactivated user's existing session is rejected within 1 minute.
- [ ] Demoting/deactivating the last Admin returns a validation error.
- [ ] Test matrix shows Viewer gets 403 on a `CanEdit` endpoint.

## Notes

Sign-in events will be audited in F08 — expose hooks/events (`IUserEvents`) rather than writing audit rows here.
