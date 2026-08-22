namespace OnCallApi.Models;

public class AuditLog
{
    public long Id { get; set; }

    /// <summary>
    /// The Entra object id when the principal has one. Google ("google-{sub}") and local
    /// ("local-{id}") principals are not GUIDs, so this is Guid.Empty for them — see
    /// <see cref="PrincipalId"/>, which always identifies the actor.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// The actor's stable identifier in whatever form their provider issues it. Required
    /// for HIPAA §164.312(b): every PHI access must be attributable to a unique user, and
    /// the GUID-only field silently attributed every Google and local user to nobody.
    /// </summary>
    [MaxLength(200)]
    public string? PrincipalId { get; set; }

    public string UserName { get; set; } = string.Empty;

    /// <summary>
    /// The HTTP status the request ultimately returned. Denied attempts (401/403) are
    /// recorded too — an attempt to reach PHI without permission is exactly the event an
    /// audit trail exists to capture.
    /// </summary>
    public int? StatusCode { get; set; }
    [Required(AllowEmptyStrings = false)]
    [MaxLength(50)]
    public string Action { get; set; } = string.Empty; // Created, Read, Updated, Deleted, Exported

    [Required(AllowEmptyStrings = false)]
    [MaxLength(50)]
    public string ResourceType { get; set; } = string.Empty; // Employee, Schedule, Shift, etc.
    public string? ResourceId { get; set; }
    public string? Details { get; set; }

    /// <summary>Tenant scope for this audit entry. Null for global operations.</summary>
    public int? TenantId { get; set; }

    public string IpAddress { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
