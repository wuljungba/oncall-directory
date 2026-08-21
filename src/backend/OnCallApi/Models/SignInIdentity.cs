using System.ComponentModel.DataAnnotations;

namespace OnCallApi.Models;

/// <summary>
/// A principal that has actually signed in, recorded so administrators can see who has
/// arrived and provision them.
///
/// Entra and Google tokens carry no app roles, so a new user lands with no access and —
/// before this existed — left no trace anywhere in the database. That made them invisible
/// in the admin UI and impossible to grant permissions to without someone typing their
/// exact email from memory. This is a directory of sign-ins, not an authorization record:
/// nothing here confers access, it only makes the person findable.
/// </summary>
public class SignInIdentity
{
    public int Id { get; set; }

    /// <summary>Which identity provider authenticated them: microsoft, google, or local.</summary>
    [Required]
    [MaxLength(20)]
    public string Provider { get; set; } = string.Empty;

    /// <summary>
    /// The stable identifier claims carry for this principal — the Entra object id, or
    /// "google-{sub}" for Google (see the OnTokenValidated handler in Program.cs). This is
    /// also the value <see cref="TenantAdmin.AzureAdObjectId"/> expects when appointing a
    /// sub-admin, which is otherwise impossible to look up.
    /// </summary>
    [Required]
    [MaxLength(200)]
    public string ExternalObjectId { get; set; } = string.Empty;

    [MaxLength(320)]
    public string? Email { get; set; }

    [MaxLength(200)]
    public string? DisplayName { get; set; }

    /// <summary>The token's home tenant ("tid"), when it carries one. Diagnostic only.</summary>
    [MaxLength(100)]
    public string? LastTenantIdClaim { get; set; }

    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;

    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
}
