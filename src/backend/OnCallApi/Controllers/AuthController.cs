using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OnCallApi.Authorization;
using OnCallApi.Services;

namespace OnCallApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly ITenantContextService _tenantContext;

    public AuthController(ITenantContextService tenantContext)
    {
        _tenantContext = tenantContext;
    }

    /// <summary>
/// Returns the current user's identity, roles, and granular permissions.
/// Any authenticated principal may call this — including Entra/Google users whose
/// tokens carry no app roles but were granted permission via a PermissionGrant.
/// (RequireViewer here rejected those users with a 403 and an empty UI.)
/// </summary>
    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<CurrentUserResponse>> GetCurrentUser()
    {
        var roles = User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();
        var permissions = User.FindAll("Permission").Select(c => c.Value).ToList();

        // Extract tenant info from claims added by TenantClaimsMiddleware
        var tenantRoles = new Dictionary<string, string>();
        foreach (var claim in User.Claims.Where(c => c.Type.StartsWith("TenantId:")))
        {
            var tenantId = claim.Type.Replace("TenantId:", "");
            tenantRoles[tenantId] = claim.Value;
        }

        var tenantIds = tenantRoles.Keys
            .Select(id => int.TryParse(id, out var n) ? n : (int?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToList();

        return Ok(new CurrentUserResponse
        {
            // Same resolver as claim expansion and tenant resolution. Reading
            // NameIdentifier directly returned the app-specific "sub" for Entra tokens,
            // so this reported a different id than the one everything else keys on.
            Id = PrincipalClaims.GetObjectId(User) ?? "",
            Name = User.FindFirst(ClaimTypes.Name)?.Value ?? "",
            Email = PrincipalClaims.GetEmail(User) ?? "",
            Roles = roles,
            Permissions = permissions,
            TenantIds = tenantIds,
            TenantRoles = tenantRoles,
            EmployeeId = await _tenantContext.GetCurrentEmployeeIdAsync(User),
        });
    }
}

public record CurrentUserResponse
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Email { get; init; } = "";
    public List<string> Roles { get; init; } = [];
    public List<string> Permissions { get; init; } = [];
    public List<int> TenantIds { get; init; } = [];
    public Dictionary<string, string> TenantRoles { get; init; } = [];

    /// <summary>The authenticated user's internal Employee.Id, when a profile is linked.</summary>
    public Guid? EmployeeId { get; init; }
}
