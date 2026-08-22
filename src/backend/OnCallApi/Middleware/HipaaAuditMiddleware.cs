using OnCallApi.Authorization;
using OnCallApi.Models;
using OnCallApi.Services;

namespace OnCallApi.Middleware;

/// <summary>
/// HIPAA audit middleware — logs access to PHI-containing endpoints via an async
/// background queue to avoid blocking the request pipeline.
///
/// Runs BEFORE authorization and records the outcome afterwards, so a denied attempt to
/// reach PHI is captured rather than vanishing. Sitting after authorization meant every
/// 401/403 short-circuited past this middleware and was never audited — the opposite of
/// what an audit trail is for.
/// </summary>
public class HipaaAuditMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<HipaaAuditMiddleware> _logger;

    /// <summary>
    /// Endpoints that touch PHI or the records governing access to it. This deliberately
    /// covers more than the directory: /api/admin is the employee store, /api/import is
    /// bulk PHI ingestion, and the rest expose staff, schedules or the code-call record.
    /// </summary>
    private static readonly string[] AuditedPrefixes =
    [
        "/api/directory",
        "/api/schedule",
        "/api/phone-trees",
        "/api/settings",
        "/api/admin",
        "/api/import",
        "/api/escalation",
        "/api/compliance",
        "/api/departments",
        "/api/tenants",
        "/api/audit",
        "/api/integrations",
        "/api/public/ehr",
    ];

    public HipaaAuditMiddleware(RequestDelegate next, ILogger<HipaaAuditMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, IAuditService auditService)
    {
        var path = context.Request.Path.Value ?? "";
        var isPhiAccess = AuditedPrefixes.Any(prefix =>
            path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        if (!isPhiAccess)
        {
            await _next(context);
            return;
        }

        // Identity is captured up front — authentication has already run, and on a
        // rejection these are the credentials that were actually presented.
        var principalId = PrincipalClaims.GetObjectId(context.User);
        var userName = context.User.Identity?.Name
            ?? PrincipalClaims.GetEmail(context.User)
            ?? "anonymous";
        var wasAuthenticated = context.User.Identity?.IsAuthenticated == true;

        try
        {
            await _next(context);
        }
        finally
        {
            // Resolved after the fact: TenantClaimsMiddleware runs downstream and adds
            // tenant claims to this same principal, so they exist by the time we get here.
            var tenantId = ResolveTenantId(context.User);

            // Anonymous requests to a PHI route are worth recording too — that is an
            // access attempt, and it is exactly what a reviewer would ask about.
            var auditLog = new AuditLog
            {
                UserId = Guid.TryParse(principalId, out var uid) ? uid : Guid.Empty,
                PrincipalId = principalId,
                UserName = userName,
                Action = context.Request.Method switch
                {
                    "GET" => "Read",
                    "POST" => "Created",
                    "PUT" or "PATCH" => "Updated",
                    "DELETE" => "Deleted",
                    _ => "Unknown"
                },
                ResourceType = path.Split('/', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) ?? path,
                ResourceId = context.Request.RouteValues["id"]?.ToString()
                             ?? context.Request.RouteValues["employeeId"]?.ToString(),
                TenantId = tenantId,
                StatusCode = context.Response.StatusCode,
                IpAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                Timestamp = DateTime.UtcNow,
            };

            auditService.Enqueue(auditLog);

            if (!wasAuthenticated || context.Response.StatusCode is 401 or 403)
            {
                _logger.LogWarning(
                    "HIPAA Audit: denied {Method} {Path} for {User} ({Status})",
                    context.Request.Method, path, userName, context.Response.StatusCode);
            }
            else
            {
                _logger.LogDebug("HIPAA Audit queued: {User} {Action} {Resource}",
                    auditLog.UserName, auditLog.Action, auditLog.ResourceType);
            }
        }
    }

    /// <summary>The first tenant on the principal, so an audit row is attributable to one.</summary>
    private static int? ResolveTenantId(System.Security.Claims.ClaimsPrincipal user)
    {
        foreach (var claim in user.Claims.Where(c => c.Type.StartsWith("TenantId:")))
        {
            if (int.TryParse(claim.Type.Replace("TenantId:", ""), out var id))
                return id;
        }
        return null;
    }
}
