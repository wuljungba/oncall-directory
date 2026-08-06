using System.ComponentModel.DataAnnotations;

namespace OnCallApi.Models;

/// <summary>
/// An explicit per-user permission assignment for a tenant (or system-wide when
/// <see cref="TenantId"/> is null). Lets an administrator grant granular on-call
/// schedule (and/or directory) read-write permissions to a specific user — including
/// external principals whose Entra/Google tokens carry no application roles.
/// Honored by <c>TenantClaimsMiddleware</c>, which expands matching rows into
/// <c>Permission</c> claims.
/// </summary>
public class PermissionGrant
{
    public int Id { get; set; }

    /// <summary>Tenant this grant applies to; null applies to all tenants the user context allows.</summary>
    public int? TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    /// <summary>
    /// "external" — matched by <see cref="ExternalPrincipalId"/> (Entra object id or email).
    /// "local"    — matched by email (the same local account that signs in).
    /// </summary>
    [Required, MaxLength(20)]
    public string PrincipalType { get; set; } = "external";

    /// <summary>Entra object id or email of the principal. Used for both external and local matches.</summary>
    [MaxLength(200)]
    public string ExternalPrincipalId { get; set; } = string.Empty;

    /// <summary>Linked local account when <see cref="PrincipalType"/> == "local".</summary>
    public int? LocalUserId { get; set; }
    public LocalAccount? LocalUser { get; set; }

    /// <summary>Comma-separated permission claims, e.g. "Schedule.Read,Schedule.Write".</summary>
    [Required]
    public string Permissions { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}