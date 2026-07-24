using System.Security.Claims;
using System.Text.Json;

namespace OnCallApi.Middleware;

/// <summary>
/// JWT validation middleware for directory and schedule endpoints.
///
/// Runs after the ASP.NET Core authentication middleware has populated
/// the User principal from the JWT bearer token.  Validates that:
///
///   1. The token is present (not anonymous)
///   2. The required "access_as_user" scope is present
///   3. The user object identifier (oid) claim is present (needed for audit)
///
/// Returns structured JSON error responses on failure rather than
/// the framework's default HTML 401 page, and logs failed attempts
/// for HIPAA audit compliance.
/// </summary>
public class JwtValidationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<JwtValidationMiddleware> _logger;

    // Endpoint prefixes that require scoped JWT validation
    private static readonly HashSet<string> ProtectedPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/directory",
        "/api/schedule",
        "/api/phone-trees",
        "/api/compliance",
        "/api/settings",
        "/api/integrations",
        "/api/admin",
    };

    public JwtValidationMiddleware(RequestDelegate next, ILogger<JwtValidationMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";

        // Only enforce on protected API endpoints
        var isProtected = ProtectedPrefixes.Any(prefix =>
            path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        if (!isProtected)
        {
            await _next(context);
            return;
        }

        // ── 1. Check the user is authenticated ──
        if (context.User.Identity?.IsAuthenticated != true)
        {
            _logger.LogWarning("JWT: Unauthenticated request to {Path} from {IP}",
                path, context.Connection.RemoteIpAddress);

            await WriteAuthErrorResponse(context, StatusCodes.Status401Unauthorized,
                "Authentication required. Provide a valid JWT bearer token.");
            return;
        }

        // ── 2. Verify required scope claim ──
        var scopeClaim = context.User.FindFirstValue("scp")
                         ?? context.User.FindFirstValue("http://schemas.microsoft.com/identity/claims/scope");

        if (string.IsNullOrEmpty(scopeClaim) || !scopeClaim.Contains("access_as_user", StringComparison.OrdinalIgnoreCase))
        {
            var userId = context.User.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier") ?? "unknown";
            _logger.LogWarning("JWT: Missing access_as_user scope for user {UserId} on {Path}", userId, path);

            await WriteAuthErrorResponse(context, StatusCodes.Status403Forbidden,
                "Insufficient permissions. The token must include the access_as_user scope.");
            return;
        }

        // ── 3. Verify object identifier claim (needed for audit) ──
        var oid = context.User.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier");
        if (string.IsNullOrEmpty(oid))
        {
            _logger.LogWarning("JWT: Missing object identifier claim on {Path}", path);

            await WriteAuthErrorResponse(context, StatusCodes.Status401Unauthorized,
                "Invalid token: missing user object identifier.");
            return;
        }

        // ── 4. Verify tenant is not "common" (multi-tenant protection) ──
        var tid = context.User.FindFirstValue("http://schemas.microsoft.com/identity/claims/tenantid");
        if (string.IsNullOrEmpty(tid) || tid == "common")
        {
            _logger.LogWarning("JWT: Invalid tenant ID '{Tid}' on {Path}", tid ?? "null", path);

            await WriteAuthErrorResponse(context, StatusCodes.Status401Unauthorized,
                "Invalid token: tenant identifier is missing or invalid.");
            return;
        }

        await _next(context);
    }

    private static async Task WriteAuthErrorResponse(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";

        var response = new
        {
            error = new
            {
                code = statusCode,
                message,
                details = statusCode switch
                {
                    401 => "Provide a valid JWT bearer token in the Authorization header.",
                    403 => "Your token does not have the required permissions for this resource.",
                    _ => null as string
                }
            }
        };

        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        await context.Response.WriteAsync(json);
    }
}
