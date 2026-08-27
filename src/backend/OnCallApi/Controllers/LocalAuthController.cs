using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OnCallApi.Models;
using OnCallApi.Services;

namespace OnCallApi.Controllers;

[ApiController]
[Route("api/auth/local")]
public class LocalAuthController : ControllerBase
{
    private readonly ILocalAccountService _localAccounts;
    private readonly ILogger<LocalAuthController> _logger;

    public LocalAuthController(ILocalAccountService localAccounts, ILogger<LocalAuthController> logger)
    {
        _localAccounts = localAccounts;
        _logger = logger;
    }

    /// <summary>Register a new local account (admin-only).</summary>
    [HttpPost("register")]
    [Authorize(Policy = "RequireAdminFull")]
    public async Task<ActionResult<LocalAccountResponse>> Register([FromBody] RegisterLocalAccountRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
            return BadRequest(new { error = "Email and password are required." });

        // The login identifier must be a real email address, and this matters beyond
        // hygiene: TenantClaimsMiddleware decides whether a PermissionGrant targets an
        // email or an object id purely by whether it contains "@". A non-email username
        // that happens to contain one would be matched against the wrong set of grants.
        if (!new EmailAddressAttribute().IsValid(request.Email) || request.Email.Length > 256)
            return BadRequest(new { error = "Email must be a valid address of at most 256 characters." });

        if (request.Password.Length < 8 || request.Password.Length > 256)
            return BadRequest(new { error = "Password must be between 8 and 256 characters." });

        try
        {
            var account = await _localAccounts.RegisterAsync(
                request.Email, request.Password, request.DisplayName ?? request.Email, request.Roles, request.EmployeeId);

            return Ok(ToResponse(account));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    /// <summary>Authenticate with email and password. Returns a JWT.</summary>
    [HttpPost("login")]
    public async Task<ActionResult<LoginLocalAccountResponse>> Login([FromBody] LoginLocalAccountRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
            return BadRequest(new { error = "Email and password are required." });

        var (account, token) = await _localAccounts.AuthenticateAsync(request.Email, request.Password);

        if (account == null || token == null)
            return Unauthorized(new { error = "Invalid email or password." });

        return Ok(new LoginLocalAccountResponse
        {
            AccessToken = token,
            Id = account.Id.ToString(),
            Name = account.DisplayName,
            Email = account.Email,
        });
    }

    /// <summary>List all local accounts (admin-only).</summary>
    [HttpGet]
    [Authorize(Policy = "RequireAdminFull")]
    public async Task<ActionResult<List<LocalAccountResponse>>> GetAll([FromQuery] bool includeInactive = false)
    {
        var accounts = await _localAccounts.GetAllAsync(includeInactive);
        return Ok(accounts.Select(ToResponse).ToList());
    }

    /// <summary>Get a specific local account (admin-only).</summary>
    [HttpGet("{id}")]
    [Authorize(Policy = "RequireAdminFull")]
    public async Task<ActionResult<LocalAccountResponse>> GetById(int id)
    {
        var account = await _localAccounts.GetByIdAsync(id);
        if (account == null) return NotFound();
        return Ok(ToResponse(account));
    }

    /// <summary>Update a local account (admin-only).</summary>
    [HttpPut("{id}")]
    [Authorize(Policy = "RequireAdminFull")]
    public async Task<ActionResult<LocalAccountResponse>> Update(int id, [FromBody] UpdateLocalAccountRequest request)
    {
        try
        {
            var account = await _localAccounts.UpdateAsync(id, request.DisplayName, request.IsActive, request.Roles, request.EmployeeId);
            return Ok(ToResponse(account));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>Change password for the authenticated user.</summary>
    [HttpPost("change-password")]
    [Authorize]
    public async Task<ActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        // Extract local account ID from the "oid" claim (format: "local-{id}")
        var oid = User.FindFirst("oid")?.Value;
        if (oid == null || !oid.StartsWith("local-"))
            return BadRequest(new { error = "Not a local account." });

        if (!int.TryParse(oid["local-".Length..], out var accountId))
            return BadRequest(new { error = "Invalid account identifier." });

        var success = await _localAccounts.ChangePasswordAsync(accountId, request.CurrentPassword, request.NewPassword);
        if (!success)
            return BadRequest(new { error = "Current password is incorrect." });

        return Ok(new { message = "Password changed successfully." });
    }

    /// <summary>Reset a local account's password (admin-only).</summary>
    [HttpPost("{id}/reset-password")]
    [Authorize(Policy = "RequireAdminFull")]
    public async Task<ActionResult> ResetPassword(int id, [FromBody] ResetPasswordRequest request)
    {
        try
        {
            await _localAccounts.ResetPasswordAsync(id, request.NewPassword);
            return Ok(new { message = "Password reset successfully." });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>Delete (deactivate) a local account (admin-only).</summary>
    [HttpDelete("{id}")]
    [Authorize(Policy = "RequireAdminFull")]
    public async Task<ActionResult> Deactivate(int id)
    {
        try
        {
            await _localAccounts.DeactivateAsync(id);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    private static LocalAccountResponse ToResponse(LocalAccount account)
    {
        return new LocalAccountResponse
        {
            Id = account.Id,
            Email = account.Email,
            DisplayName = account.DisplayName,
            Roles = account.Roles,
            EmployeeId = account.EmployeeId,
            IsActive = account.IsActive,
            CreatedAt = account.CreatedAt,
            LastLoginAt = account.LastLoginAt,
        };
    }
}

// ── Request / Response types ──

public record RegisterLocalAccountRequest(string Email, string Password, string? DisplayName, string[]? Roles, Guid? EmployeeId = null);

public record LoginLocalAccountRequest(string Email, string Password);

public record LoginLocalAccountResponse
{
    public string AccessToken { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
}

public record UpdateLocalAccountRequest(string? DisplayName, bool? IsActive, string[]? Roles, Guid? EmployeeId = null);

public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public record ResetPasswordRequest(string NewPassword);

public record LocalAccountResponse
{
    public int Id { get; init; }
    public string Email { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string[] Roles { get; init; } = [];
    public Guid? EmployeeId { get; init; }
    public bool IsActive { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? LastLoginAt { get; init; }
}
