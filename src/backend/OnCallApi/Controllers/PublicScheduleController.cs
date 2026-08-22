using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;
using OnCallApi.Models;

namespace OnCallApi.Controllers;

/// <summary>
/// Public, unauthenticated on-call schedule coverage, exposed ONLY via a revocable
/// <see cref="PublicShare"/> token (the "permalink"). Returns coverage per
/// department — which tiers are staffed right now — with **no PHI**: no names,
/// phones, or emails. Route lives under <c>api/public/&lt;...&gt;</c> deliberately,
/// NOT <c>/api/schedule</c> or <c>/api/directory</c>, so the authenticated-only
/// <c>JwtValidationMiddleware</c> protected-prefix guard does not apply.
/// </summary>
[ApiController]
[Route("api/public")]
[EnableRateLimiting("Api")]
public class PublicScheduleController : ControllerBase
{
    private readonly AppDbContext _db;

    public PublicScheduleController(AppDbContext db) => _db = db;

    /// <summary>
    /// Current on-call coverage for a shared schedule. Coverage-only by design.
    /// </summary>
    [HttpGet("schedule/on-call/{token}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(PublicCoverageResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PublicCoverageResponse>> GetCoverage(Guid token)
    {
        var now = DateTime.UtcNow;

        var share = await _db.PublicShares
            .Include(s => s.Tenant)
            .FirstOrDefaultAsync(s => s.Token == token
                && s.IsActive
                && (s.ExpiresAt == null || s.ExpiresAt > now));

        // Deliberately the same response for unknown, disabled and expired: an anonymous
        // caller learns only that the link does not work, not whether it ever existed.
        if (share == null)
            return NotFound(new { error = "Sharing link not found, disabled, or expired." });
        var tenantId = share.TenantId;

        // On-call shifts active right now for this tenant's schedules, excluding gaps.
        var assignments = await _db.Shifts
            .Where(sh => sh.Status != "gap"
                && sh.StartTime <= now && sh.EndTime > now
                && sh.Schedule.Department != null
                && sh.Schedule.Department.TenantId == tenantId)
            // Group tier by its lowercased form so mixed-case data can't collide into a
            // duplicate dictionary key below.
            .GroupBy(sh => new { sh.Schedule.Department!.Id, sh.Schedule.Department!.Name, Tier = sh.Tier.ToLowerInvariant() })
            .Select(g => new { g.Key.Id, g.Key.Name, g.Key.Tier, Count = g.Count() })
            .ToListAsync();

        var units = assignments
            .GroupBy(a => new { a.Id, a.Name })
            .Select(g => new PublicCoveredUnit
            {
                DepartmentId = g.Key.Id,
                Department = g.Key.Name,
                Tiers = g.ToDictionary(
                    a => a.Tier,
                    a => new PublicCoveredTier { Covered = a.Count > 0, Assignments = a.Count }),
            })
            .OrderBy(u => u.Department)
            .ToList();

        return Ok(new PublicCoverageResponse
        {
            CoverageAt = now,
            Tenant = share.Tenant.Name,
            Units = units,
        });
    }
}

public class PublicCoverageResponse
{
    public DateTime CoverageAt { get; set; }
    public string Tenant { get; set; } = string.Empty;
    public List<PublicCoveredUnit> Units { get; set; } = new();
}

public class PublicCoveredUnit
{
    public long DepartmentId { get; set; }
    public string Department { get; set; } = string.Empty;
    public Dictionary<string, PublicCoveredTier> Tiers { get; set; } = new();
}

/// <summary>Coverage marker for one tier (primary/secondary/tertiary) — no identity.</summary>
public class PublicCoveredTier
{
    public bool Covered { get; set; }
    public int Assignments { get; set; }
}