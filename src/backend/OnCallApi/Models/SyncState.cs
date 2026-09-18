namespace OnCallApi.Models;

/// <summary>
/// Where a directory's delta enumeration got to, one row per directory per source.
///
/// This lived in <see cref="AppSetting"/> under "AdDeltaToken:{tenantId}", which any holder of
/// Schedule.Read could read back in full through GET /api/settings — a Graph resource link is
/// not a setting. Moving it also gives the cursor a shape of its own: a deltaLink, a scope, and
/// when it was last advanced.
/// </summary>
public class SyncState
{
    public int Id { get; set; }

    /// <summary>Null is the home directory.</summary>
    public int? TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    [Required(AllowEmptyStrings = false)]
    [MaxLength(40)]
    public string Source { get; set; } = SyncSources.AdUsers;

    /// <summary>
    /// The absolute deltaLink Graph handed back. Uncapped on purpose: these carry a token that
    /// grows with the directory, and truncating one silently restarts the enumeration.
    /// </summary>
    public string? DeltaLink { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
