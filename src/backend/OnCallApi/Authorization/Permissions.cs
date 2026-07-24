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

    // ── Admin Permission ──
    public const string AdminFull = "Admin.Full";

    /// <summary>
    /// Maps legacy Azure AD role names to their granular permission claims.
    /// Used by DevelopmentAuthenticationHandler and could be used in production
    /// to expand Azure AD roles into claims at token validation time.
    /// </summary>
    public static readonly Dictionary<string, string[]> RoleToPermissions = new()
    {
        ["OnCall.Viewer"] = [ScheduleRead, DirectoryRead],
        ["OnCall.Scheduler"] = [ScheduleRead, ScheduleWrite, DirectoryRead],
        ["OnCall.Admin"] = [ScheduleRead, ScheduleWrite, DirectoryRead, DirectoryWrite, AdminFull],
    };
}
