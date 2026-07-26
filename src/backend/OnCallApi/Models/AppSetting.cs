namespace OnCallApi.Models;

/// <summary>
/// Key-value store for application-wide settings.
/// Settings are cached in-memory and editable by admins via the Settings UI.
/// </summary>
public class AppSetting
{
    /// <summary>The setting key, used as the lookup identifier and primary key.</summary>
    [Key]
    [Required(AllowEmptyStrings = false)]
    [MaxLength(100)]
    public string Key { get; set; } = string.Empty;

    /// <summary>The setting value, stored as a string.</summary>
    [MaxLength(2000)]
    public string Value { get; set; } = string.Empty;

    /// <summary>Optional description of what this setting controls.</summary>
    [MaxLength(500)]
    public string? Description { get; set; }

    /// <summary>Tenant-scoped settings. Null means global/app-wide setting.</summary>
    public int? TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
