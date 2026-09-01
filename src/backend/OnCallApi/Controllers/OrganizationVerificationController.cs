using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OnCallApi.Models;
using OnCallApi.Services;

namespace OnCallApi.Controllers;

/// <summary>
/// Where an organization says who it is, and where an administrator decides.
///
/// Deliberately under /api/admin so it falls inside JwtValidationMiddleware's protected
/// prefixes, and deliberately NOT behind the write policies it exists to unlock -- an
/// organization that cannot write is exactly the one that needs to submit this.
/// </summary>
[ApiController]
[Route("api/admin/verification")]
public class OrganizationVerificationController : ControllerBase
{
    private readonly OrganizationVerificationService _verification;
    private readonly ITenantContextService _tenants;
    private readonly ILogger<OrganizationVerificationController> _logger;

    public OrganizationVerificationController(
        OrganizationVerificationService verification,
        ITenantContextService tenants,
        ILogger<OrganizationVerificationController> logger)
    {
        _verification = verification;
        _tenants = tenants;
        _logger = logger;
    }

    /// <summary>What has been submitted for a subscription, and what the checks said.</summary>
    [HttpGet("{tenantId:int}")]
    [Authorize(Policy = "RequireAdminFullOrScoped")]
    public async Task<ActionResult<OrganizationVerification>> Get(int tenantId)
    {
        if (!await MayAdministerAsync(tenantId)) return NotFound();

        var verification = await _verification.GetAsync(tenantId);
        return verification == null ? NotFound() : Ok(verification);
    }

    /// <summary>
    /// Submits, or resubmits, an organization's details. Runs the checks immediately.
    ///
    /// A scoped administrator may submit for their own subscription: it is their
    /// organization being described, and requiring a super admin to type it would put a
    /// person in the middle of every signup.
    /// </summary>
    [HttpPost("{tenantId:int}")]
    [Authorize(Policy = "RequireAdminFullOrScoped")]
    public async Task<ActionResult<OrganizationVerification>> Submit(
        int tenantId, [FromBody] SubmitVerificationRequest request)
    {
        if (!await MayAdministerAsync(tenantId)) return NotFound();

        var (verification, error) = await _verification.SubmitAsync(
            tenantId, request, CurrentUserName());

        if (error != null) return BadRequest(new { error });

        // A non-healthcare organization is verified outright and has no submission to
        // return. Saying so is more useful than an empty 200.
        if (verification == null)
            return Ok(new { verified = true, message = "No verification is needed for this kind of organization." });

        return Ok(verification);
    }

    /// <summary>Everything waiting on a person's judgement.</summary>
    [HttpGet("pending")]
    [Authorize(Policy = "RequireAdminFull")]
    public async Task<ActionResult<List<OrganizationVerification>>> Pending() =>
        Ok(await _verification.GetPendingAsync());

    /// <summary>
    /// Approves a submission the automatic checks would not pass.
    ///
    /// Full admin only, and never scoped: approving your own organization is not a
    /// verification, it is a form with an extra step.
    /// </summary>
    [HttpPost("{tenantId:int}/approve")]
    [Authorize(Policy = "RequireAdminFull")]
    public async Task<IActionResult> Approve(int tenantId, [FromBody] DecideVerificationRequest? body)
    {
        var (ok, error) = await _verification.DecideAsync(
            tenantId, VerificationStatus.Verified, body?.Reason, CurrentUserName());

        if (!ok) return BadRequest(new { error });

        _logger.LogInformation("Tenant {TenantId} approved by {Who}", tenantId, CurrentUserName());
        return NoContent();
    }

    /// <summary>Rejects a submission.</summary>
    [HttpPost("{tenantId:int}/reject")]
    [Authorize(Policy = "RequireAdminFull")]
    public async Task<IActionResult> Reject(int tenantId, [FromBody] DecideVerificationRequest? body)
    {
        if (string.IsNullOrWhiteSpace(body?.Reason))
            return BadRequest(new { error = "Say why. A rejection with no reason cannot be answered." });

        var (ok, error) = await _verification.DecideAsync(
            tenantId, VerificationStatus.Rejected, body.Reason, CurrentUserName());

        if (!ok) return BadRequest(new { error });

        return NoContent();
    }

    /// <summary>
    /// Whether this caller may act for the subscription. A 404 rather than a 403 for one
    /// they may not: whether a given subscription exists is not their business either.
    /// </summary>
    private async Task<bool> MayAdministerAsync(int tenantId)
    {
        if (_tenants.IsSuperAdmin(User)) return true;

        var authorized = await _tenants.GetAuthorizedTenantIdsAsync(User);
        return authorized.Contains(tenantId);
    }

    private string? CurrentUserName() =>
        User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value
        ?? User.FindFirst("name")?.Value
        ?? User.FindFirst("preferred_username")?.Value;
}
