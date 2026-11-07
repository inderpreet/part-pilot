# F01 — Users & authentication

- **Status:** Done
- **Depends on:** F00
- **Roles:** Admin manages users and settings; all users can sign in/out and change own password

## Summary

Secure the app with local accounts. Three fixed roles (Admin, Staff, Viewer). The first person to open a fresh
install creates the Admin account. Admins create other users; there is no self-registration.

## User stories

- As the first user of a new install, I want to create the admin account so that the app is secured immediately.
- As an Admin, I want to create users and assign roles so that staff get the right access.
- As an Admin, I want to deactivate a user so that a former employee can no longer sign in, while their history is kept.
- As any user, I want to change my password.
- As an Admin, I want to reset another user's password.
- As an Admin, I want to edit business settings (company name, currency, negative stock, PO prefix).

## Functional requirements

1. FR-1 First-run setup page shown when no users exist; creates an Admin; afterwards the page returns 404.
2. FR-2 Login with username + password; "remember me" option; lockout after 5 failures for 15 minutes.
3. FR-3 Logout (POST).
4. FR-4 User list (Admin): username, display name, role, active, last sign-in.
5. FR-5 Create/edit user: username, display name, email (optional), role, active flag; set/reset password.
6. FR-6 Change own password (requires current password).
7. FR-7 Settings page (Admin) editing `AppSetting` values from F00.
8. FR-8 Policies `RequireAdmin`, `CanEdit`, `CanView` registered, plus a fallback policy requiring an authenticated, active user.

## Business rules

- Exactly one role per user.
- Cannot deactivate or demote the last active Admin.
- Users are never deleted (deactivate only) — they are referenced by transactions and audit entries.
- Password policy: min 8 chars, no other complexity rules (NIST-style).
- Usernames unique, case-insensitive.

## Data model

Identity tables; `ApplicationUser : IdentityUser` adds `DisplayName`, `IsActive`, `LastSignInAt`.

## Screens / endpoints

| Route | Purpose | Policy |
|---|---|---|
| `/Account/Setup` | First-run admin creation | Anonymous (only when no users) |
| `/Account/Login`, `/Account/Logout` | Sign in/out | Anonymous / Anonymous (POST only, antiforgery-protected; so a deactivated user can still sign out) |
| `/Account/ChangePassword` | Own password | CanView |
| `/Admin/Users`, `/Admin/Users/Edit/{id?}` | User management | RequireAdmin |
| `/Admin/Settings` | Business settings | RequireAdmin |
| `/AccessDenied` | 403 page | Anonymous |

## Out of scope

- External logins (Google/Microsoft), 2FA, password reset by email, API tokens.

## Work items

| ID | Title | Status |
|---|---|---|
| [WI-01-1](../work-items/F01-users-auth/WI-01-1-db-schema.md) | Identity schema, role seed | Done |
| [WI-01-2](../work-items/F01-users-auth/WI-01-2-api.md) | Auth config, policies, UserService | Done |
| [WI-01-3](../work-items/F01-users-auth/WI-01-3-frontend.md) | Login, setup, user admin, settings pages | Done |

## Open questions

- Is 2FA (TOTP) wanted for Admins in v1?
- Session length: 8 h sliding OK for shared terminals?
