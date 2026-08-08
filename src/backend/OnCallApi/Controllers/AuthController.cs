using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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

    /// <summary>Returns the current user's identity, roles, and granular permissions.</summary>
    [HttpGet("me")]
    [Authorize(Policy = "RequireViewer")]
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
            Id = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "",
            Name = User.FindFirst(ClaimTypes.Name)?.Value ?? "",
            Email = User.FindFirst(ClaimTypes.Email)?.Value ?? "",
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
