using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace OnCallApi.Models;

/// <summary>
/// Represents a local database account — users who authenticate with
/// email + password rather than via an external identity provider.
///
/// Local accounts are created and managed by administrators.
/// Passwords are hashed using BCrypt.
/// </summary>
public static class LocalAccountOrigin
{
    /// <summary>Created by an administrator, who has vouched for the person.</summary>
    public const string Admin = "Admin";

    /// <summary>Created by whoever filled in the signup form. Grants nothing on its own.</summary>
    public const string SelfSignup = "SelfSignup";
}

[Index(nameof(Email), IsUnique = true)]
public class LocalAccount
{
    public int Id { get; set; }

    /// <summary>Login email / username. Must be unique.</summary>
    [Required(AllowEmptyStrings = false)]
    [MaxLength(256)]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    /// <summary>BCrypt hash of the user's password.</summary>
    [Required]
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>Display name shown in the UI.</summary>
    [MaxLength(200)]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Optional link to an Employee record.
    /// When set, the local account can participate in on-call schedules
    /// and appear in the phone directory.
    /// </summary>
    public Guid? EmployeeId { get; set; }

    /// <summary>Roles assigned to this local account (JSON array of strings).</summary>
    public string RolesJson { get; set; } = "[\"OnCall.Viewer\"]";

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// "Admin" -- created by an administrator, who has vouched for the person -- or
    /// "SelfSignup", created by whoever filled in the form.
    ///
    /// The difference matters: an admin-created account is linked to a directory entry
    /// and given the staff baseline, because somebody decided it should be. A self-signed
    /// account gets neither, and an administrator provisions it afterwards.
    /// </summary>
    [MaxLength(20)]
    public string Origin { get; set; } = LocalAccountOrigin.Admin;

    /// <summary>Consecutive failed sign-ins, reset by a successful one.</summary>
    public int FailedLoginCount { get; set; }

    /// <summary>When set and in the future, sign-in is refused regardless of the password.</summary>
    public DateTime? LockedOutUntil { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastLoginAt { get; set; }

    /// <summary>Computed roles list (from RolesJson).</summary>
    public string[] Roles
    {
        get => System.Text.Json.JsonSerializer.Deserialize<string[]>(RolesJson) ?? ["OnCall.Viewer"];
        set => RolesJson = System.Text.Json.JsonSerializer.Serialize(value);
    }
}
