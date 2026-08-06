namespace OnCallApi.Models;

/// <summary>
/// A revocable public-sharing token for a tenant's on-call schedule. The token is
/// the "permalink" external viewers use to see a coverage-only view of who is on
/// call. The public endpoint deliberately returns no PHI (no names, phones, or
/// emails). Disabling/deleting a share revokes the permalink immediately.
/// </summary>
public class PublicShare
{
    public int Id { get; set; }

    public int TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    /// <summary>Unguessable token that forms the public URL path.</summary>
    public Guid Token { get; set; } = Guid.NewGuid();

    /// <summary>Short human label, e.g. "Residents board view".</summary>
    public string Label { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}