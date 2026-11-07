namespace InventoryTracker.Services;

/// <summary>A user as shown in lists and edit forms. Never carries a password hash.</summary>
public sealed record UserDto(
    string Id,
    string UserName,
    string DisplayName,
    string? Email,
    string? Role,
    bool IsActive,
    DateTime? LastSignInAt);

public sealed record CreateUserInput(
    string UserName,
    string DisplayName,
    string? Email,
    string Role,
    string Password,
    bool IsActive = true);

/// <summary>Editable user fields. The user name is fixed after creation.</summary>
public sealed record UpdateUserInput(
    string Id,
    string DisplayName,
    string? Email,
    string Role,
    bool IsActive);

/// <summary>Form field names used in <see cref="Error.Field"/> so pages can attach messages to inputs.</summary>
public static class UserFields
{
    public const string UserName = nameof(UserName);
    public const string DisplayName = nameof(DisplayName);
    public const string Email = nameof(Email);
    public const string Role = nameof(Role);
    public const string IsActive = nameof(IsActive);
    public const string Password = nameof(Password);
    public const string CurrentPassword = nameof(CurrentPassword);
    public const string NewPassword = nameof(NewPassword);
}
