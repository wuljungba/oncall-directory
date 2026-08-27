using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OnCallApi.Authorization;

namespace OnCallApi.Controllers;

/// <summary>
/// Development-only endpoints for switching the simulated user role.
/// Only available when DevAuth is enabled. In production, roles are determined
/// by Azure AD app roles in the JWT token.
/// </summary>
[ApiController]
[Route("api/auth/dev")]
[DevAuthOnly]
[ApiExplorerSettings(IgnoreApi = true)]
public class DevAuthController : ControllerBase
{
    /// <summary>Set the simulated role for development testing.</summary>
    [HttpPost("set-role")]
    public IActionResult SetRole([FromQuery] string role)
    {
        var validRoles = new[] { "viewer", "scheduler", "admin" };
        if (!validRoles.Contains(role.ToLowerInvariant()))
            return BadRequest(new { error = $"Invalid role. Must be one of: {string.Join(", ", validRoles)}" });

        var cookieOptions = new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Expires = DateTimeOffset.UtcNow.AddHours(1),
            Secure = false,
        };
        Response.Cookies.Append("X-Dev-Role", role.ToLowerInvariant(), cookieOptions);

        var perms = role.ToLowerInvariant() switch
        {
            "viewer" => new[] { "Schedule.Read", "Directory.Read" },
            "scheduler" => new[] { "Schedule.Read", "Schedule.Write", "Directory.Read" },
            _ => new[] { "Schedule.Read", "Schedule.Write", "Directory.Read", "Directory.Write", "Admin.Full" },
        };

        return Ok(new
        {
            role = role.ToLowerInvariant(),
            permissions = perms,
            message = $"Role set to '{role.ToLowerInvariant()}'. Refresh the page to apply."
        });
    }

    /// <summary>Clear the simulated role and reset to admin (full access).</summary>
    [HttpPost("clear-role")]
    public IActionResult ClearRole()
    {
        Response.Cookies.Delete("X-Dev-Role");
        return Ok(new
        {
            role = "admin",
            permissions = new[] { "Schedule.Read", "Schedule.Write", "Directory.Read", "Directory.Write", "Admin.Full" },
            message = "Role reset to admin (full permissions). Refresh the page to apply."
        });
    }

    /// <summary>Set the simulated OID (Azure AD object ID) for development testing.</summary>
    [HttpPost("set-oid")]
    public IActionResult SetOid([FromQuery] string oid)
    {
        if (!Guid.TryParse(oid, out _))
            return BadRequest(new { error = $"Invalid OID '{oid}'. Must be a valid GUID (e.g., 00000000-0000-0000-0000-000000000099)." });

        var cookieOptions = new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Expires = DateTimeOffset.UtcNow.AddHours(1),
            Secure = false,
        };
        Response.Cookies.Append("X-Dev-Oid", oid, cookieOptions);

        return Ok(new
        {
            oid = oid,
            message = $"OID set to '{oid}'. Refresh the page to apply."
        });
    }

    /// <summary>Clear the simulated OID and reset to the seeded dev user.</summary>
    [HttpPost("clear-oid")]
    public IActionResult ClearOid()
    {
        Response.Cookies.Delete("X-Dev-Oid");
        return Ok(new
        {
            oid = "00000000-0000-0000-0000-000000000001",
            message = "OID reset to seeded dev user. Refresh the page to apply."
        });
    }
}
