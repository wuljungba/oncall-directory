using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;
using OnCallApi.Models;
using OnCallApi.Services;

namespace OnCallApi.Authorization;

/// <summary>
/// Requires that the caller's organization has been verified before it may write.
///
/// Attached to the write policies only. Reading stays open throughout: an organization
/// waiting on a decision can still see its own directory, and locking that too would make
/// a pending verification indistinguishable from a broken account.
/// </summary>
public class TenantVerifiedRequirement : IAuthorizationRequirement;

/// <summary>
/// Lets a write through when every subscription the caller could be writing to is
/// verified.
///
/// Three deliberate exits, each of which would otherwise break something real:
///
/// - A super administrator always passes. They are the people who approve verifications;
///   locking them out of an unverified tenant would make the queue unworkable.
/// - A caller with no tenant scope at all passes. Plenty of this application predates
///   multi-tenancy and runs with TenantId null, and refusing those writes would disable
///   the product for every single-tenant installation.
/// - A tenant whose status is missing or unrecognised passes. The column defaults to
///   Verified for exactly this reason: every organization that existed before this check
///   did was already operating, and a deployment must not turn them all read-only.
/// </summary>
public class TenantVerifiedHandler : AuthorizationHandler<TenantVerifiedRequirement>
{
    private readonly AppDbContext _db;
    private readonly ITenantContextService _tenants;
    private readonly ILogger<TenantVerifiedHandler> _logger;

    public TenantVerifiedHandler(
        AppDbContext db, ITenantContextService tenants, ILogger<TenantVerifiedHandler> logger)
    {
        _db = db;
        _tenants = tenants;
        _logger = logger;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, TenantVerifiedRequirement requirement)
    {
        if (context.User?.Identity?.IsAuthenticated != true) return;

        if (_tenants.IsSuperAdmin(context.User))
        {
            context.Succeed(requirement);
            return;
        }

        List<int> tenantIds;
        try
        {
            tenantIds = await _tenants.GetAuthorizedTenantIdsAsync(context.User);
        }
        catch (Exception ex)
        {
            // A gate that fails closed on its own error would take the application down
            // for everybody. It fails open and says so loudly: the permission check that
            // this requirement sits alongside has already run and still applies.
            _logger.LogError(ex, "Could not resolve tenant scope for the verification gate; allowing the write");
            context.Succeed(requirement);
            return;
        }

        if (tenantIds.Count == 0)
        {
            context.Succeed(requirement);
            return;
        }

        var statuses = await _db.Tenants
            .Where(t => tenantIds.Contains(t.Id))
            .Select(t => t.VerificationStatus)
            .ToListAsync();

        // Only an explicit Pending, Unverified or Rejected blocks. Anything else -- and
        // that includes a tenant row that is somehow missing -- is treated as allowed.
        var blocked = statuses.Any(s =>
            s == VerificationStatus.Pending
            || s == VerificationStatus.Unverified
            || s == VerificationStatus.Rejected);

        if (blocked)
        {
            _logger.LogWarning(
                "Write refused: an organization in this caller's scope is not verified");
            return;
        }

        context.Succeed(requirement);
    }
}
