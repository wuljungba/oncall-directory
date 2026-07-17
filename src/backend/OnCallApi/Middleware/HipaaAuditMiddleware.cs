using System.Security.Claims;
using OnCallApi.Data;
using OnCallApi.Models;

namespace OnCallApi.Middleware;

/// <summary>
/// HIPAA audit middleware — logs all access to PHI-containing endpoints.
/// </summary>
public class HipaaAuditMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<HipaaAuditMiddleware> _logger;

    // Endpoints that access PHI (personally identifiable health information)
    private static readonly HashSet<string> PhiPhoneEndpoints = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/employee", "/api/directory", "/api/schedule/time-off"
    };

    public HipaaAuditMiddleware(RequestDelegate next, ILogger<HipaaAuditMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, AppDbContext db)
    {
        var path = context.Request.Path.Value ?? "";

        // Check if this endpoint accesses PHI
        var isPhiAccess = PhiPhoneEndpoints.Any(prefix =>
            path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        if (isPhiAccess && context.User.Identity?.IsAuthenticated == true)
        {
            var userId = context.User.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier");
            var userName = context.User.Identity.Name ?? "unknown";

            var auditLog = new AuditLog
            {
                UserId = Guid.TryParse(userId, out var uid) ? uid : Guid.Empty,
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
                ResourceId = context.Request.RouteValues["id"]?.ToString() ??
                            context.Request.RouteValues["employeeId"]?.ToString(),
                IpAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                Timestamp = DateTime.UtcNow
            };

            db.AuditLogs.Add(auditLog);
            await db.SaveChangesAsync();

            _logger.LogInformation("HIPAA Audit: {User} {Action} {Resource} at {Time}",
                auditLog.UserName, auditLog.Action, auditLog.ResourceType, auditLog.Timestamp);
        }

        await _next(context);
    }
}
