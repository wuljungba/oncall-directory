namespace OnCallApi.Authorization;

/// <summary>
/// Granular permission claims for the on-call scheduling and phone directory system.
/// These sit alongside the existing Azure AD roles (OnCall.Viewer, OnCall.Scheduler, OnCall.Admin)
/// and enable more fine-grained authorization policies.
/// </summary>
public static class Permissions
{
    public const string ClaimType = "Permission";

    // ── Schedule Permissions ──
    public const string ScheduleRead = "Schedule.Read";
    public const string ScheduleWrite = "Schedule.Write";

    // ── Directory Permissions ──
    public const string DirectoryRead = "Directory.Read";
    public const string DirectoryWrite = "Directory.Write";

    // ── Code Call Permissions ──
    public const string CodeCallWrite = "CodeCall.Write";

    // ── Admin Permissions ──
    /// <summary>Full administrative access to all data across all tenants.</summary>
    public const string AdminFull = "Admin.Full";

    /// <summary>Scoped administrative access — can only manage assigned tenant(s).</summary>
    public const string AdminScoped = "Admin.Scoped";

    /// <summary>Can create and manage tenants and sub-admin assignments (super admin only).</summary>
    public const string TenantManage = "Tenant.Manage";

    /// <summary>
    /// Maps legacy Azure AD role names to their granular permission claims.
    /// Used by DevelopmentAuthenticationHandler and could be used in production
    /// to expand Azure AD roles into claims at token validation time.
    /// </summary>
    public static readonly Dictionary<string, string[]> RoleToPermissions = new()
    {
        ["OnCall.Viewer"] = [ScheduleRead, DirectoryRead],
        ["OnCall.Scheduler"] = [ScheduleRead, ScheduleWrite, DirectoryRead, CodeCallWrite],
        ["OnCall.Admin"] = [ScheduleRead, ScheduleWrite, DirectoryRead, DirectoryWrite, AdminFull, TenantManage],
    };

    /// <summary>
    /// Permissions granted to a tenant-scoped sub-admin (DepartmentAdmin role).
    /// These are issued by TenantClaimsMiddleware based on TenantAdmin records.
    /// </summary>
    public static readonly string[] ScopedAdminPermissions =
    [
        ScheduleRead, ScheduleWrite,
        DirectoryRead, DirectoryWrite,
        CodeCallWrite,
        AdminScoped,
    ];

    /// <summary>
    /// Roles granted to a configured super administrator (see SuperAdminOptions).
    /// Mirrors the dev-mode "admin" role set so real users get identical access.
    /// </summary>
    public static readonly string[] SuperAdminRoles =
    [
        "OnCall.Viewer", "OnCall.Scheduler", "OnCall.Admin",
    ];

    /// <summary>
    /// Complete permission set granted to a configured super administrator.
    /// </summary>
    public static readonly string[] SuperAdminPermissions =
    [
        ScheduleRead, ScheduleWrite,
        DirectoryRead, DirectoryWrite,
        CodeCallWrite,
        AdminScoped,
        AdminFull,
        TenantManage,
    ];

    /// <summary>
    /// Permissions an administrator can explicitly assign to a user via the
    /// admin dashboard (the per-user <c>PermissionGrant</c> model). Admin-only
    /// permissions (Admin.Full, Admin.Scoped, Tenant.Manage) are not assignable
    /// here — those come from super-admin config or tenant-admin records.
    /// </summary>
    public static readonly string[] AssignablePermissions =
    [
        ScheduleRead, ScheduleWrite,
        DirectoryRead, DirectoryWrite,
        CodeCallWrite,
    ];

    /// <summary>Whether <paramref name="permission"/> is a known permission string.</summary>
    public static bool IsKnownPermission(string permission) =>
        AssignablePermissions.Contains(permission, StringComparer.Ordinal)
        || permission == AdminFull
        || permission == AdminScoped
        || permission == TenantManage;

    /// <summary>
    /// Parses a comma-separated permission CSV, dropping blank and unknown values
    /// and de-duplicating (case-sensitive). Returns only known permission strings.
    /// </summary>
    public static string[] ParsePermissionCsv(string? csv) =>
        (csv ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsKnownPermission)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Parses a permission CSV to ONLY the permissions an administrator is allowed to
    /// assign to another user (the <see cref="AssignablePermissions"/> allow-list).
    /// Admin-only permissions (Admin.Full, Admin.Scoped, Tenant.Manage) are never
    /// returned, so a grant can never silently promote a user to super-admin.
    /// </summary>
    public static string[] ParseAssignablePermissionCsv(string? csv) =>
        ParsePermissionCsv(csv)
            .Where(AssignablePermissions.Contains)
            .ToArray();
}
