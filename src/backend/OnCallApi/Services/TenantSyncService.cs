using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;
using OnCallApi.Models;

namespace OnCallApi.Services;

/// <summary>
/// Synchronizes TenantAdmin assignments from Azure AD group membership.
/// On each sync cycle, this service queries all Tenants that have AzureAdGroupId set,
/// fetches the group's members from Azure AD, and upserts TenantAdmin records.
/// </summary>
public class TenantSyncService
{
    private readonly AppDbContext _db;
    private readonly IGraphApiService _graphApi;
    private readonly ILogger<TenantSyncService> _logger;

    public TenantSyncService(
        AppDbContext db,
        IGraphApiService graphApi,
        ILogger<TenantSyncService> logger)
    {
        _db = db;
        _graphApi = graphApi;
        _logger = logger;
    }

    /// <summary>
    /// Syncs all tenant admin assignments from Azure AD groups.
    /// Returns the total number of TenantAdmin records upserted.
    /// </summary>
    public async Task<int> SyncAllAsync(CancellationToken cancellationToken = default)
    {
        var tenants = await _db.Tenants
            .Where(t => t.IsActive && t.AzureAdGroupId != null)
            .ToListAsync(cancellationToken);

        var totalChanges = 0;

        foreach (var tenant in tenants)
        {
            try
            {
                var changes = await SyncTenantAsync(tenant, cancellationToken);
                totalChanges += changes;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to sync tenant admin assignments for Tenant {TenantId} ({TenantName})",
                    tenant.Id, tenant.Name);
            }
        }

        return totalChanges;
    }

    /// <summary>
    /// Syncs admin assignments for a single tenant from its Azure AD group.
    /// </summary>
    private async Task<int> SyncTenantAsync(Tenant tenant, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(tenant.AzureAdGroupId))
            return 0;

        // Read the group from the customer's OWN directory. This asked our home directory for a
        // group id that only exists in theirs, so it came back empty every time — and the
        // empty-result guard below was the only thing standing between that and a wholesale
        // revocation of their administrators.
        var members = await _graphApi.GetDepartmentMembersAsync(
            tenant.AzureAdTenantId, tenant.AzureAdGroupId, cancellationToken);

        var memberIds = members.Members
            .Where(m => !string.IsNullOrEmpty(m.AzureAdObjectId))
            .Select(m => m.AzureAdObjectId)
            .Distinct()
            .ToList();

        if (memberIds.Count == 0)
        {
            _logger.LogWarning(
                "No members found for Azure AD group {GroupId} (Tenant: {TenantName})",
                tenant.AzureAdGroupId, tenant.Name);
            return 0;
        }

        var changes = 0;
        var now = DateTime.UtcNow;

        // Get existing auto-assigned admins for this tenant
        var existingAdmins = await _db.TenantAdmins
            .Where(a => a.TenantId == tenant.Id && a.IsAutoAssigned)
            .ToListAsync(cancellationToken);

        var existingMemberIds = existingAdmins.Select(a => a.AzureAdObjectId).ToHashSet();

        // Add new members
        foreach (var memberId in memberIds)
        {
            if (!existingMemberIds.Contains(memberId))
            {
                _db.TenantAdmins.Add(new TenantAdmin
                {
                    TenantId = tenant.Id,
                    AzureAdObjectId = memberId,
                    Role = "DepartmentAdmin",
                    IsAutoAssigned = true,
                    CreatedAt = now,
                    LastSyncedAt = now,
                });
                changes++;
            }
            else
            {
                // Update LastSyncedAt for existing
                var existing = existingAdmins.First(a => a.AzureAdObjectId == memberId);
                existing.LastSyncedAt = now;
            }
        }

        // Remove stale members (no longer in the Azure AD group).
        //
        // Absence is only evidence of departure if the whole membership was read. Group members
        // page at 100, and this deletes rows that grant administrative access — on a truncated
        // read it would revoke every admin past the first page, every cycle.
        if (members.Completed)
        {
            var staleAdmins = existingAdmins
                .Where(a => !memberIds.Contains(a.AzureAdObjectId))
                .ToList();

            if (staleAdmins.Count > 0)
            {
                _db.TenantAdmins.RemoveRange(staleAdmins);
                _logger.LogInformation(
                    "Removed {Count} stale admin(s) from Tenant {TenantId} ({TenantName})",
                    staleAdmins.Count, tenant.Id, tenant.Name);
            }
        }
        else
        {
            _logger.LogWarning(
                "Only part of the admin group for Tenant {TenantId} ({TenantName}) could be read ({Detail}); "
                + "nobody was revoked",
                tenant.Id, tenant.Name, members.FailureDetail);
        }

        await _db.SaveChangesAsync(cancellationToken);

        if (changes > 0)
        {
            _logger.LogInformation(
                "Synced {Changes} new admin(s) for Tenant {TenantId} ({TenantName})",
                changes, tenant.Id, tenant.Name);
        }

        return changes;
    }
}
