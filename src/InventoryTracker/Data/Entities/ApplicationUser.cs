using Microsoft.AspNetCore.Identity;

namespace InventoryTracker.Data.Entities;

/// <summary>
/// A local account. Users are never deleted (transactions and audit entries reference them): set
/// <see cref="IsActive"/> to false instead. Other tables reference users via a string <c>UserId</c> FK.
/// </summary>
public class ApplicationUser : IdentityUser
{
    public const int DisplayNameMaxLength = 100;

    public string DisplayName { get; set; } = string.Empty;

    // Initialised here rather than with HasDefaultValue(true): a CLR default of false would otherwise be
    // indistinguishable from "not set" and EF would silently store true for a deliberately inactive user.
    public bool IsActive { get; set; } = true;

    /// <summary>UTC time of the last successful sign-in; null if the user has never signed in.</summary>
    public DateTime? LastSignInAt { get; set; }
}
