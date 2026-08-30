using System.ComponentModel.DataAnnotations;

namespace OnCallApi.Models;

/// <summary>
/// Links a user (by Azure AD Object ID) to a Tenant with a specific admin role.
/// Supports both manual assignment (by super admin) and auto-assignment via Azure AD group sync.
/// </summary>
public class TenantAdmin
{
    public int Id { get; set; }

    public int TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    /// <summary>The Azure AD Object ID of the user being granted admin access.</summary>
    [Required(AllowEmptyStrings = false)]
    [MaxLength(100)]
    public string AzureAdObjectId { get; set; } = string.Empty;

    /// <summary>
    /// The admin role within this tenant.
    /// "TenantAdmin" — administers this tenant: its departments, employees, schedules,
    /// time off, and starting a code call.
    ///
    /// There were two values, "DepartmentAdmin" and "SuperAdmin", and nothing ever
    /// authorised on the difference — both granted exactly the same permissions. Offering
    /// the choice implied a restriction that did not exist, so the roles are now one.
    /// The old values are still accepted wherever they are read, so existing rows keep
    /// working without a migration.
    /// </summary>
    [MaxLength(50)]
    public string Role { get; set; } = OnCallApi.Authorization.TenantAdminRoles.Default;


    /// <summary>True if this assignment was created by Azure AD group sync, false if manual.</summary>
    public bool IsAutoAssigned { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastSyncedAt { get; set; }
}
