using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using OnCallApi.Authorization;

namespace OnCallApi.Middleware;

/// <summary>
/// Development-only authentication handler that auto-authenticates every request.
/// Supports role-switching via the X-Dev-Role cookie (set by POST /api/auth/dev/set-role).
///
/// Role cookie values: "viewer", "scheduler", "admin" (default).
/// - viewer:    OnCall.Viewer role, Schedule.Read + Directory.Read permissions
/// - scheduler: OnCall.Viewer + OnCall.Scheduler roles, + Schedule.Write
/// - admin:     All roles, all permissions (default when no cookie)
///
/// Activated when DevAuth:Enabled is true in appsettings.Development.json.
/// Provides fake claims so audit logging and downstream code don't crash on null.
/// </summary>
public class DevelopmentAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Development";

    public DevelopmentAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Read role cookie — default to admin (all permissions)
        var roleCookie = Request.Cookies["X-Dev-Role"]?.ToLowerInvariant() ?? "admin";

        var roleClaims = roleCookie switch
        {
            "viewer" => new[] { "OnCall.Viewer" },
            "scheduler" => new[] { "OnCall.Viewer", "OnCall.Scheduler" },
            _ => new[] { "OnCall.Viewer", "OnCall.Scheduler", "OnCall.Admin" },
        };

        // Build permission claims from the active roles
        var permissionClaims = roleClaims
            .SelectMany(role => Permissions.RoleToPermissions.TryGetValue(role, out var perms) ? perms : [])
            .Distinct()
            .Select(p => new Claim(Permissions.ClaimType, p));

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, "dev@local"),
            new(ClaimTypes.NameIdentifier, "dev-user-id"),
            new(ClaimTypes.Email, "dev@local"),
            // Claims the JwtValidationMiddleware and HipaaAuditMiddleware expect
            new("scp", "access_as_user"),
            new("http://schemas.microsoft.com/identity/claims/objectidentifier",
                "00000000-0000-0000-0000-000000000001"),
            new("http://schemas.microsoft.com/identity/claims/tenantid",
                "00000000-0000-0000-0000-000000000002"),
        };

        // Add role claims
        claims.AddRange(roleClaims.Select(r => new Claim(ClaimTypes.Role, r)));
        // Add permission claims
        claims.AddRange(permissionClaims);

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
