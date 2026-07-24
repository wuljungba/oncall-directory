using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace OnCallApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    /// <summary>Returns the current user's identity, roles, and granular permissions.</summary>
    [HttpGet("me")]
    [Authorize(Policy = "RequireViewer")]
    public ActionResult<CurrentUserResponse> GetCurrentUser()
    {
        var roles = User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();
        var permissions = User.FindAll("Permission").Select(c => c.Value).ToList();

        return Ok(new CurrentUserResponse
        {
            Id = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "",
            Name = User.FindFirst(ClaimTypes.Name)?.Value ?? "",
            Email = User.FindFirst(ClaimTypes.Email)?.Value ?? "",
            Roles = roles,
            Permissions = permissions,
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
}
