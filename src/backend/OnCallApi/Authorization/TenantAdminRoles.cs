namespace OnCallApi.Authorization;

/// <summary>
/// The single tenant-admin role, and the legacy values that mean the same thing.
///
/// "DepartmentAdmin" and "SuperAdmin" were offered as a choice but conferred identical
/// permissions -- the value was stored and displayed, and nothing authorised on it. That
/// made "Department Admin" look like a restriction it never was. New assignments record
/// <see cref="Default"/>; the old values are still recognised so existing rows are
/// unaffected.
/// </summary>
public static class TenantAdminRoles
{
    public const string Default = "TenantAdmin";

    private static readonly string[] Legacy = ["DepartmentAdmin", "SuperAdmin"];

    /// <summary>True for the current role or either value it replaced.</summary>
    public static bool IsTenantAdmin(string? role) =>
        !string.IsNullOrEmpty(role)
        && (string.Equals(role, Default, StringComparison.OrdinalIgnoreCase)
            || Legacy.Contains(role, StringComparer.OrdinalIgnoreCase));
}
