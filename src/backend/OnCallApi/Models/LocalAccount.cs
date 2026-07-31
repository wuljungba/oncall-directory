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

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastLoginAt { get; set; }

    /// <summary>Computed roles list (from RolesJson).</summary>
    public string[] Roles
    {
        get => System.Text.Json.JsonSerializer.Deserialize<string[]>(RolesJson) ?? ["OnCall.Viewer"];
        set => RolesJson = System.Text.Json.JsonSerializer.Serialize(value);
    }
}
