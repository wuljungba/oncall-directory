using System.Security.Claims;
using System.Text.Json;

namespace OnCallApi.Middleware;

/// <summary>
/// JWT validation middleware for protected API endpoints.
///
/// Runs after the ASP.NET Core authentication middleware has populated
/// the User principal from the JWT bearer token. Validates that:
///
///   1. The token is present (not anonymous)
///   2. A required scope or validation claim is present:
///      - Microsoft tokens: "scp" claim containing "access_as_user"
///        (set by Microsoft.Identity.Web from the real token)
///      - Google/Local tokens: "auth_validated" claim set to "true"
///        (set by the OnTokenValidated handler)
///   3. A user identifier claim (oid or sub) is present (needed for audit)
///   4. For Microsoft tokens: tenant ID is valid
///
/// Provider-agnostic — works with Microsoft Entra ID, Google, and local JWTs.
/// Returns structured JSON error responses on failure rather than
/// the framework's default HTML 401 page, and logs failed attempts
/// for HIPAA audit compliance.
/// </summary>
public class JwtValidationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<JwtValidationMiddleware> _logger;

    // Endpoint prefixes that require scoped JWT validation.
    //
    // Kept in step with HipaaAuditMiddleware.AuditedPrefixes: the two lists had drifted, so
    // routes recognised as PHI-adjacent enough to audit — departments, tenants, escalation,
    // import, messaging, code-call locations — skipped the scope and tenant sanity checks.
    // The bearer handler still validated every token's signature before any of this, so
    // this closes an inconsistency rather than an authentication bypass.
    //
    // Anonymous routes must stay out: /api/public/* and /api/auth/* have no principal yet.
    private static readonly HashSet<string> ProtectedPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/directory",
        "/api/schedule",
        "/api/phone-trees",
        "/api/compliance",
        "/api/settings",
        "/api/integrations",
        "/api/admin",
        "/api/departments",
        "/api/tenants",
        "/api/escalation",
        "/api/import",
        "/api/messaging",
        "/api/code-call-locations",
        "/api/audit",
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

        // ── 2. Verify required scope or validation claim ──
        // Microsoft tokens get scp from Microsoft.Identity.Web (real token scope).
        // Google/Local tokens get auth_validated from their OnTokenValidated handler.
        var scopeClaim = context.User.FindFirstValue("scp")
                         ?? context.User.FindFirstValue("http://schemas.microsoft.com/identity/claims/scope");

        var hasValidScope = !string.IsNullOrEmpty(scopeClaim) &&
                            scopeClaim.Contains("access_as_user", StringComparison.OrdinalIgnoreCase);
        var hasAuthValidated = context.User.HasClaim("auth_validated", "true");

        if (!hasValidScope && !hasAuthValidated)
        {
            var userId = GetUserId(context.User);
            _logger.LogWarning("JWT: Missing required validation (scope/access_as_user or auth_validated) for user {UserId} on {Path}", userId, path);

            await WriteAuthErrorResponse(context, StatusCodes.Status403Forbidden,
                "Insufficient permissions. The token must include the required scope or validation claim.");
            return;
        }

        // ── 3. Verify user identifier claim (needed for audit) ──
        var userIdForAudit = GetUserId(context.User);
        if (string.IsNullOrEmpty(userIdForAudit))
        {
            _logger.LogWarning("JWT: Missing user identifier claim on {Path}", path);

            await WriteAuthErrorResponse(context, StatusCodes.Status401Unauthorized,
                "Invalid token: missing user identifier.");
            return;
        }

        // ── 4. For Microsoft tokens, verify tenant ID ──
        var authProvider = context.User.FindFirst("auth_provider")?.Value;
        if (authProvider == "microsoft" || authProvider == null)
        {
            var tid = context.User.FindFirstValue("http://schemas.microsoft.com/identity/claims/tenantid")
                      ?? context.User.FindFirstValue("tid");
            if (string.IsNullOrEmpty(tid) || tid == "common")
            {
                _logger.LogWarning("JWT: Invalid tenant ID '{Tid}' on {Path}", tid ?? "null", path);

                await WriteAuthErrorResponse(context, StatusCodes.Status401Unauthorized,
                    "Invalid token: tenant identifier is missing or invalid.");
                return;
            }
        }

        await _next(context);
    }

    /// <summary>
    /// Gets the user identifier from provider-agnostic claims.
    /// Tries normalized "oid" first, then Microsoft's namespace-qualified form,
    /// then Google's "sub", then NameIdentifier.
    /// </summary>
    private static string? GetUserId(ClaimsPrincipal user)
    {
        return user.FindFirstValue("oid")
               ?? user.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier")
               ?? user.FindFirstValue("sub")
               ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
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
            PropertyNamingPolicy = JsonCamelCasePolicy
        });

        await context.Response.WriteAsync(json);
    }

    /// <summary>
    /// Custom JsonNamingPolicy that always uses camelCase, avoiding
    /// reference to System.Text.Json's built-in policy.
    /// </summary>
    private static readonly JsonNamingPolicy JsonCamelCasePolicy = new CamelCasePolicy();

    private class CamelCasePolicy : JsonNamingPolicy
    {
        public override string ConvertName(string name) =>
            char.ToLowerInvariant(name[0]) + name[1..];
    }
}
