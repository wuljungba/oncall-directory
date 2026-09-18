using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;

namespace OnCallApi.Services;

/// <summary>One directory to read, and the OnCall tenant that owns whatever comes out of it.</summary>
public sealed record DirectoryTarget(int? TenantId, string? EntraTenantId, string Name);

public static class DirectorySyncTargets
{
    /// <summary>
    /// Every directory worth syncing: each connected customer, plus our own — unless a tenant has
    /// claimed it.
    ///
    /// That last clause is the non-obvious half, and the reason this is shared rather than copied.
    /// Syncing a claimed home directory both ways creates every person twice, once owned by no
    /// tenant and once by the tenant that claimed it, and leaves two cursors describing one
    /// directory. The user sync learned that; the department sync would have had to learn it again.
    /// </summary>
    public static async Task<IReadOnlyList<DirectoryTarget>> ResolveAsync(
        AppDbContext db, string? homeEntraTenantId, CancellationToken ct = default)
    {
        var connected = await db.Tenants
            .Where(t => t.IsActive && t.AzureAdTenantId != null && t.AzureAdTenantId != "")
            .Select(t => new { t.Id, t.Name, t.AzureAdTenantId })
            .ToListAsync(ct);

        var homeIsClaimed = !string.IsNullOrWhiteSpace(homeEntraTenantId)
            && connected.Any(t =>
                string.Equals(t.AzureAdTenantId, homeEntraTenantId, StringComparison.OrdinalIgnoreCase));

        var targets = new List<DirectoryTarget>();
        if (!homeIsClaimed) targets.Add(new DirectoryTarget(null, null, "the home directory"));

        targets.AddRange(connected.Select(t => new DirectoryTarget(t.Id, t.AzureAdTenantId!, t.Name)));
        return targets;
    }
}
