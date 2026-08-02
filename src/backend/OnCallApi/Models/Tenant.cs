using System.ComponentModel.DataAnnotations;

namespace OnCallApi.Models;

/// <summary>
/// Represents a business/facility within a hospital group.
/// Each tenant has its own isolated set of departments, employees, and settings.
/// A super admin can manage all tenants; sub-admins are scoped to one tenant.
/// </summary>
public class Tenant
{
    public int Id { get; set; }

    [Required(AllowEmptyStrings = false)]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty; // e.g. "Mercy Hospital — Downtown"

    [MaxLength(1000)]
    public string? Description { get; set; }

    /// <summary>Azure AD Group ID for auto-assigning sub-admins via group membership.</summary>
    [MaxLength(100)]
    public string? AzureAdGroupId { get; set; }

    /// <summary>
    /// The Entra tenant ID (GUID) whose users belong to this tenant. Acts as the
    /// approved-tenant allow-list: a user's token `tid` claim must match an active
    /// tenant's <see cref="AzureAdTenantId"/> to be scoped into this tenant.
    /// Null disables tid-based resolution (legacy group/oid-only mode).
    /// </summary>
    [MaxLength(100)]
    public string? AzureAdTenantId { get; set; }

    /// <summary>Contact email for the facility admin.</summary>
    [MaxLength(100)]
    public string? ContactEmail { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public ICollection<Department> Departments { get; set; } = new List<Department>();
    public ICollection<TenantAdmin> TenantAdmins { get; set; } = new List<TenantAdmin>();
}
