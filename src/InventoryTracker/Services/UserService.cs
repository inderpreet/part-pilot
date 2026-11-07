using System.ComponentModel.DataAnnotations;
using InventoryTracker.Auth;
using InventoryTracker.Data;
using InventoryTracker.Data.Configurations;
using InventoryTracker.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Services;

/// <summary>
/// User management and the F01 business rules: one role per user, users are never deleted, and the last active
/// Admin can never be deactivated or demoted.
/// </summary>
public interface IUserService
{
    /// <summary>Creates the Admin account of a fresh install. Fails once any user exists.</summary>
    Task<Result<UserDto>> CreateFirstAdminAsync(string userName, string displayName, string? email, string password, CancellationToken cancellationToken = default);

    Task<PagedResult<UserDto>> ListAsync(ListQuery query, CancellationToken cancellationToken = default);

    Task<UserDto?> GetAsync(string id, CancellationToken cancellationToken = default);

    Task<Result<UserDto>> CreateAsync(CreateUserInput input, string? actorUserId = null, CancellationToken cancellationToken = default);

    Task<Result<UserDto>> UpdateAsync(UpdateUserInput input, string? actorUserId = null, CancellationToken cancellationToken = default);

    /// <summary>Admin sets a new password for a user (also clears any lockout and ends their other sessions).</summary>
    Task<Result> ResetPasswordAsync(string id, string newPassword, string? actorUserId = null, CancellationToken cancellationToken = default);

    Task<Result> ChangeOwnPasswordAsync(string id, string currentPassword, string newPassword, CancellationToken cancellationToken = default);

    Task RecordSignInAsync(string id, CancellationToken cancellationToken = default);
}

public sealed class UserService(
    AppDbContext db,
    UserManager<ApplicationUser> userManager,
    IFirstRunState firstRun,
    UserEventPublisher events,
    IClock clock)
    : IUserService
{
    private const int UserNameMaxLength = 50;
    private const int EmailMaxLength = 256;

    // Serialises everything that can change who is an active Admin (and first-run setup) so two simultaneous
    // requests cannot each pass the last-admin check and leave the install with no Admin. One process owns the DB.
    private static readonly SemaphoreSlim AdminGate = new(1, 1);

    public async Task<Result<UserDto>> CreateFirstAdminAsync(string userName, string displayName, string? email, string password, CancellationToken cancellationToken = default)
    {
        await AdminGate.WaitAsync(cancellationToken);
        try
        {
            if (await db.Users.AnyAsync(cancellationToken))
            {
                firstRun.MarkUsersExist();
                return Result.Failure<UserDto>("setup.complete", "Setup has already been completed.");
            }

            var result = await CreateCoreAsync(userName, displayName, email, Roles.Admin, password, isActive: true);
            if (result.Succeeded)
            {
                firstRun.MarkUsersExist();
                await events.PublishAsync(UserEventType.FirstAdminCreated, result.Value.Id, result.Value.UserName, cancellationToken: cancellationToken);
            }

            return result;
        }
        finally
        {
            AdminGate.Release();
        }
    }

    public async Task<PagedResult<UserDto>> ListAsync(ListQuery query, CancellationToken cancellationToken = default)
    {
        var rows =
            from u in db.Users.AsNoTracking()
            join ur in db.UserRoles on u.Id equals ur.UserId into userRoles
            from ur in userRoles.DefaultIfEmpty()
            join r in db.Roles on ur.RoleId equals r.Id into roles
            from r in roles.DefaultIfEmpty()
            select new UserRow { User = u, Role = r.Name };

        if (query.SearchTerm is { } term)
        {
            var pattern = LikePattern.Contains(term);
            rows = rows.Where(x =>
                EF.Functions.Like(x.User.UserName!, pattern, LikePattern.Escape)
                || EF.Functions.Like(x.User.DisplayName, pattern, LikePattern.Escape)
                || (x.User.Email != null && EF.Functions.Like(x.User.Email, pattern, LikePattern.Escape)));
        }

        var ordered = (query.Sort?.ToLowerInvariant(), query.Desc) switch
        {
            ("name", false) => rows.OrderBy(x => x.User.DisplayName),
            ("name", true) => rows.OrderByDescending(x => x.User.DisplayName),
            ("role", false) => rows.OrderBy(x => x.Role),
            ("role", true) => rows.OrderByDescending(x => x.Role),
            ("active", false) => rows.OrderBy(x => x.User.IsActive),
            ("active", true) => rows.OrderByDescending(x => x.User.IsActive),
            ("signin", false) => rows.OrderBy(x => x.User.LastSignInAt),
            ("signin", true) => rows.OrderByDescending(x => x.User.LastSignInAt),
            (_, true) => rows.OrderByDescending(x => x.User.UserName),
            _ => rows.OrderBy(x => x.User.UserName),
        };

        var page = ordered.ThenBy(x => x.User.UserName).Select(x => new UserDto(
            x.User.Id,
            x.User.UserName!,
            x.User.DisplayName,
            x.User.Email,
            x.Role,
            x.User.IsActive,
            x.User.LastSignInAt));

        return await PagedResult<UserDto>.CreateAsync(page, query, cancellationToken);
    }

    public async Task<UserDto?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        return user is null ? null : ToDto(user, await RoleOfAsync(user.Id, cancellationToken));
    }

    public async Task<Result<UserDto>> CreateAsync(CreateUserInput input, string? actorUserId = null, CancellationToken cancellationToken = default)
    {
        await AdminGate.WaitAsync(cancellationToken);
        try
        {
            var result = await CreateCoreAsync(input.UserName, input.DisplayName, input.Email, input.Role, input.Password, input.IsActive);
            if (result.Succeeded)
            {
                await events.PublishAsync(UserEventType.UserCreated, result.Value.Id, result.Value.UserName, actorUserId, $"role {input.Role}", cancellationToken);
            }

            return result;
        }
        finally
        {
            AdminGate.Release();
        }
    }

    public async Task<Result<UserDto>> UpdateAsync(UpdateUserInput input, string? actorUserId = null, CancellationToken cancellationToken = default)
    {
        await AdminGate.WaitAsync(cancellationToken);
        try
        {
            var user = await userManager.FindByIdAsync(input.Id);
            if (user is null)
            {
                return Result.Failure<UserDto>("user.not_found", "User not found.");
            }

            var displayName = input.DisplayName?.Trim() ?? string.Empty;
            var email = NormalizeEmail(input.Email);
            var errors = ValidateProfile(displayName, email, input.Role);
            if (errors.Count > 0)
            {
                return Result.Failure<UserDto>([.. errors]);
            }

            var currentRoles = await userManager.GetRolesAsync(user);
            var currentRole = currentRoles.Count == 0 ? null : currentRoles.Contains(Roles.Admin) ? Roles.Admin : currentRoles[0];

            var isActiveAdmin = user.IsActive && currentRoles.Contains(Roles.Admin);
            var staysActiveAdmin = input.IsActive && input.Role == Roles.Admin;
            if (isActiveAdmin && !staysActiveAdmin && !await AnotherActiveAdminExistsAsync(user.Id, cancellationToken))
            {
                return Result.Failure<UserDto>(
                    "user.last_admin",
                    input.Role != Roles.Admin
                        ? "This is the last active Admin and cannot be changed to another role. Make someone else an Admin first."
                        : "This is the last active Admin and cannot be deactivated. Make someone else an Admin first.",
                    input.Role != Roles.Admin ? UserFields.Role : UserFields.IsActive);
            }

            var roleChanged = currentRoles.Count != 1 || currentRole != input.Role;
            var deactivated = user.IsActive && !input.IsActive;

            var changes = DescribeChanges(user, currentRole, displayName, email, input);

            user.DisplayName = displayName;
            user.Email = email;
            user.IsActive = input.IsActive;

            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

            if (roleChanged)
            {
                if (currentRoles.Count > 0)
                {
                    var removed = await userManager.RemoveFromRolesAsync(user, currentRoles);
                    if (!removed.Succeeded)
                    {
                        return Result.Failure<UserDto>([.. Map(removed)]);
                    }
                }

                var added = await userManager.AddToRoleAsync(user, input.Role);
                if (!added.Succeeded)
                {
                    return Result.Failure<UserDto>([.. Map(added)]);
                }
            }

            var updated = await userManager.UpdateAsync(user);
            if (!updated.Succeeded)
            {
                return Result.Failure<UserDto>([.. Map(updated)]);
            }

            // Role or active-state changes end the user's existing sessions so they pick up the new rights.
            if (roleChanged || deactivated)
            {
                var stamped = await userManager.UpdateSecurityStampAsync(user);
                if (!stamped.Succeeded)
                {
                    return Result.Failure<UserDto>([.. Map(stamped)]);
                }
            }

            await tx.CommitAsync(cancellationToken);

            await events.PublishAsync(UserEventType.UserUpdated, user.Id, user.UserName, actorUserId, changes, cancellationToken);
            return Result.Success(ToDto(user, input.Role));
        }
        finally
        {
            AdminGate.Release();
        }
    }

    public async Task<Result> ResetPasswordAsync(string id, string newPassword, string? actorUserId = null, CancellationToken cancellationToken = default)
    {
        var user = await userManager.FindByIdAsync(id);
        if (user is null)
        {
            return Result.Failure("user.not_found", "User not found.");
        }

        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        var reset = await userManager.ResetPasswordAsync(user, token, newPassword);
        if (!reset.Succeeded)
        {
            return Result.Failure([.. Map(reset, UserFields.NewPassword)]);
        }

        // A reset is the usual fix for a locked-out user.
        await userManager.SetLockoutEndDateAsync(user, null);
        await userManager.ResetAccessFailedCountAsync(user);

        await events.PublishAsync(UserEventType.PasswordReset, user.Id, user.UserName, actorUserId, cancellationToken: cancellationToken);
        return Result.Success();
    }

    public async Task<Result> ChangeOwnPasswordAsync(string id, string currentPassword, string newPassword, CancellationToken cancellationToken = default)
    {
        var user = await userManager.FindByIdAsync(id);
        if (user is null)
        {
            return Result.Failure("user.not_found", "User not found.");
        }

        var changed = await userManager.ChangePasswordAsync(user, currentPassword, newPassword);
        if (!changed.Succeeded)
        {
            return Result.Failure([.. Map(changed, UserFields.NewPassword)]);
        }

        await events.PublishAsync(UserEventType.PasswordChanged, user.Id, user.UserName, user.Id, cancellationToken: cancellationToken);
        return Result.Success();
    }

    public async Task RecordSignInAsync(string id, CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;

        // Direct UPDATE: leaves the Identity concurrency stamp alone so a sign-in never makes an admin's open edit form stale.
        await db.Users.Where(u => u.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.LastSignInAt, now), cancellationToken);
    }

    private async Task<Result<UserDto>> CreateCoreAsync(string userName, string displayName, string? email, string role, string password, bool isActive)
    {
        userName = userName?.Trim() ?? string.Empty;
        displayName = displayName?.Trim() ?? string.Empty;
        email = NormalizeEmail(email);

        var errors = ValidateProfile(displayName, email, role);
        if (userName.Length == 0)
        {
            errors.Add(new Error("user.username_required", "User name is required.", UserFields.UserName));
        }
        else if (userName.Length > UserNameMaxLength)
        {
            errors.Add(new Error("user.username_too_long", $"User name must be at most {UserNameMaxLength} characters.", UserFields.UserName));
        }

        if (errors.Count > 0)
        {
            return Result.Failure<UserDto>([.. errors]);
        }

        var user = new ApplicationUser { UserName = userName, DisplayName = displayName, Email = email, IsActive = isActive };

        // Create + role assignment are separate Identity calls; one transaction keeps a user from existing without a role.
        await using var tx = await db.Database.BeginTransactionAsync();

        var created = await userManager.CreateAsync(user, password);
        if (!created.Succeeded)
        {
            return Result.Failure<UserDto>([.. Map(created)]);
        }

        var added = await userManager.AddToRoleAsync(user, role);
        if (!added.Succeeded)
        {
            return Result.Failure<UserDto>([.. Map(added)]);
        }

        await tx.CommitAsync();
        return Result.Success(ToDto(user, role));
    }

    private Task<bool> AnotherActiveAdminExistsAsync(string excludingUserId, CancellationToken cancellationToken)
        => db.Users.AnyAsync(
            u => u.IsActive
                 && u.Id != excludingUserId
                 && db.UserRoles.Any(ur => ur.UserId == u.Id && ur.RoleId == RoleConfiguration.AdminId),
            cancellationToken);

    private Task<string?> RoleOfAsync(string userId, CancellationToken cancellationToken)
        => (from ur in db.UserRoles.AsNoTracking()
            join r in db.Roles on ur.RoleId equals r.Id
            where ur.UserId == userId
            select r.Name).FirstOrDefaultAsync(cancellationToken);

    private static List<Error> ValidateProfile(string displayName, string? email, string? role)
    {
        var errors = new List<Error>();

        if (displayName.Length == 0)
        {
            errors.Add(new Error("user.displayname_required", "Display name is required.", UserFields.DisplayName));
        }
        else if (displayName.Length > ApplicationUser.DisplayNameMaxLength)
        {
            errors.Add(new Error("user.displayname_too_long", $"Display name must be at most {ApplicationUser.DisplayNameMaxLength} characters.", UserFields.DisplayName));
        }

        if (email is not null && (email.Length > EmailMaxLength || !new EmailAddressAttribute().IsValid(email)))
        {
            errors.Add(new Error("user.email_invalid", "Enter a valid email address or leave it blank.", UserFields.Email));
        }

        if (!Roles.IsValid(role))
        {
            errors.Add(new Error("user.role_invalid", "Choose a role: Admin, Staff or Viewer.", UserFields.Role));
        }

        return errors;
    }

    private static string? NormalizeEmail(string? email) => string.IsNullOrWhiteSpace(email) ? null : email.Trim();

    private static string DescribeChanges(ApplicationUser user, string? currentRole, string displayName, string? email, UpdateUserInput input)
    {
        var changes = new List<string>();
        if (currentRole != input.Role)
        {
            changes.Add($"role {currentRole ?? "(none)"} → {input.Role}");
        }

        if (user.IsActive != input.IsActive)
        {
            changes.Add(input.IsActive ? "activated" : "deactivated");
        }

        if (user.DisplayName != displayName)
        {
            changes.Add("display name changed");
        }

        if (!string.Equals(user.Email, email, StringComparison.Ordinal))
        {
            changes.Add("email changed");
        }

        return changes.Count == 0 ? "no changes" : string.Join(", ", changes);
    }

    private static UserDto ToDto(ApplicationUser user, string? role)
        => new(user.Id, user.UserName ?? string.Empty, user.DisplayName, user.Email, role, user.IsActive, user.LastSignInAt);

    /// <summary>Maps Identity errors onto form fields so pages can show them next to the right input.</summary>
    private static IEnumerable<Error> Map(IdentityResult result, string passwordField = UserFields.Password)
        => result.Errors.Select(e => new Error(
            "identity." + e.Code,
            e.Description,
            e.Code switch
            {
                "DuplicateUserName" or "InvalidUserName" => UserFields.UserName,
                "InvalidEmail" or "DuplicateEmail" => UserFields.Email,
                "PasswordMismatch" => UserFields.CurrentPassword,
                var code when code.StartsWith("Password", StringComparison.Ordinal) => passwordField,
                _ => null,
            }));

    private sealed class UserRow
    {
        public required ApplicationUser User { get; init; }

        public string? Role { get; init; }
    }
}
