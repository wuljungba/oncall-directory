using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OnCallApi.Models;
using OnCallApi.Services;

namespace OnCallApi.Controllers;

/// <summary>
/// The way in for someone who has no account yet.
///
/// Submitting is anonymous — it has to be, or it could not serve its purpose — and it is
/// the only unauthenticated write in the app besides the provider webhooks. It is fenced
/// accordingly: its own rate-limit policy, a length cap on every field, and a response
/// that is identical whether the address is new, already waiting, or already a user.
///
/// Reviewing a request is admin-only, and approving one grants nothing by itself. Access
/// is still assigned by hand, scoped to a tenant, on the Permissions screen.
///
/// The admin actions deliberately hang off /api/admin/... rather than this controller's
/// own route. JwtValidationMiddleware applies its scope and tenant checks by path prefix,
/// and this prefix cannot be in that list — the submit endpoint underneath it has to stay
/// reachable without a principal. Rather than leave the admin endpoints outside those
/// checks, they are addressed under a prefix that is already inside them.
/// </summary>
[ApiController]
[Route("api/access-requests")]
public class AccessRequestsController : ControllerBase
{
    private readonly IAccessRequestService _service;
    private readonly ILogger<AccessRequestsController> _logger;

    public AccessRequestsController(IAccessRequestService service, ILogger<AccessRequestsController> logger)
    {
        _service = service;
        _logger = logger;
    }

    /// <summary>Ask for access. Anonymous by design.</summary>
    [HttpPost]
    [AllowAnonymous]
    [EnableRateLimiting("AccessRequests")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Submit([FromBody] SubmitAccessRequest request, CancellationToken ct)
    {
        if (request == null)
            return BadRequest(new { error = "A work email address is required." });

        var accepted = await _service.SubmitAsync(request, ct);

        // A malformed address is reported, because the person needs to fix it. Everything
        // else answers the same way: a stranger must not be able to use this endpoint to
        // discover who already has an account here.
        if (!accepted)
            return BadRequest(new { error = "Enter a valid work email address." });

        return Accepted(new
        {
            message = "Request received. An administrator will review it and be in touch by email.",
        });
    }

    /// <summary>The queue an admin works through.</summary>
    [HttpGet("/api/admin/access-requests")]
    [Authorize(Policy = "RequireAdminFullOrScoped")]
    public async Task<ActionResult<List<AccessRequest>>> List([FromQuery] string? status, CancellationToken ct)
    {
        try
        {
            return await _service.ListAsync(status, ct);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    /// <summary>
    /// Record that a request was approved. This does NOT grant access — it marks the
    /// request triaged so it leaves the queue. Provisioning stays a separate, deliberate
    /// act on the Permissions screen, where the tenant scope is chosen explicitly.
    /// </summary>
    [HttpPost("/api/admin/access-requests/{id}/approve")]
    [Authorize(Policy = "RequireAdminFull")]
    public Task<ActionResult<AccessRequest>> Approve(int id, [FromBody] ReviewAccessRequestBody? body, CancellationToken ct) =>
        Review(id, approved: true, body, ct);

    /// <summary>Record that a request was declined.</summary>
    [HttpPost("/api/admin/access-requests/{id}/deny")]
    [Authorize(Policy = "RequireAdminFull")]
    public Task<ActionResult<AccessRequest>> Deny(int id, [FromBody] ReviewAccessRequestBody? body, CancellationToken ct) =>
        Review(id, approved: false, body, ct);

    private async Task<ActionResult<AccessRequest>> Review(
        int id, bool approved, ReviewAccessRequestBody? body, CancellationToken ct)
    {
        try
        {
            var reviewer = User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value
                ?? User.FindFirst("name")?.Value
                ?? User.FindFirst("preferred_username")?.Value;

            return Ok(await _service.ReviewAsync(id, approved, reviewer, body?.Note, ct));
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }
}
