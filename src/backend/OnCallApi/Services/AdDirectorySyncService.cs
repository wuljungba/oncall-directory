using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OnCallApi.Configuration;
using OnCallApi.Data;
using OnCallApi.Models;

namespace OnCallApi.Services;

/// <summary>
/// What one AD directory sync did. Returned rather than only logged, so the caller that
/// triggered it can be told — the manual trigger previously reported a count of users
/// fetched from Graph and nothing about what, if anything, was written.
/// </summary>
public record AdSyncResult(
    int Fetched,
    int Created,
    int Updated,
    int Deactivated,
    IReadOnlyList<string> Skipped,
    string? DeltaToken,
    int? TenantId = null,
    string? TenantName = null,
    bool Succeeded = true)
{
    public bool AnythingWritten => Created > 0 || Updated > 0 || Deactivated > 0;
}

public interface IAdDirectorySyncService
{
    /// <summary>
    /// Syncs one directory into one OnCall tenant. A null <paramref name="tenantId"/> is
    /// the home directory and the employees it owns, which is how this behaved before
    /// connected directories existed.
    /// </summary>
    Task<AdSyncResult> SyncAsync(
        int? tenantId, string? entraTenantId, string? deltaToken, CancellationToken ct = default);

    Task<string?> GetStoredDeltaTokenAsync(int? tenantId, CancellationToken ct = default);

    /// <summary>
    /// Syncs every tenant that has a connected directory, plus the home directory. One
    /// tenant failing does not stop the others.
    /// </summary>
    Task<IReadOnlyList<AdSyncResult>> SyncAllAsync(CancellationToken ct = default);
}

/// <summary>
/// Upserts the staff directory from Microsoft Entra.
///
/// This lived inside <see cref="AdSyncBackgroundService"/>, which meant the only way to run
/// it was to wait for the timer. The "sync now" endpoint called a Graph read instead and
/// reported its count as though it had synced, so it always claimed success and never wrote
/// a row. Both paths now run this.
/// </summary>
public class AdDirectorySyncService : IAdDirectorySyncService
{
    private readonly AppDbContext _db;
    private readonly IGraphApiService _graphApi;
    private readonly IOptions<GraphApiOptions> _graphOptions;
    private readonly ILogger<AdDirectorySyncService> _logger;

    public AdDirectorySyncService(
        AppDbContext db,
        IGraphApiService graphApi,
        IOptions<GraphApiOptions> graphOptions,
        ILogger<AdDirectorySyncService> logger)
    {
        _db = db;
        _graphApi = graphApi;
        _graphOptions = graphOptions;
        _logger = logger;
    }

    /// <summary>
    /// Delta state is per directory. A single shared token would hand one customer's
    /// cursor to another customer's directory, which Graph would either reject or, worse,
    /// answer with a page of changes that belong to someone else.
    /// </summary>
    private static string DeltaTokenKey(int? tenantId) =>
        tenantId.HasValue ? $"AdDeltaToken:{tenantId.Value}" : "AdDeltaToken";

    public async Task<string?> GetStoredDeltaTokenAsync(int? tenantId, CancellationToken ct = default)
    {
        var key = DeltaTokenKey(tenantId);
        var setting = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        return setting?.Value;
    }

    public async Task<IReadOnlyList<AdSyncResult>> SyncAllAsync(CancellationToken ct = default)
    {
        var results = new List<AdSyncResult>();

        // Same shape TenantSyncService already uses for AzureAdGroupId: find the tenants
        // that opted in, and work through them one at a time.
        var connected = await _db.Tenants
            .Where(t => t.IsActive && t.AzureAdTenantId != null && t.AzureAdTenantId != "")
            .Select(t => new { t.Id, t.Name, t.AzureAdTenantId })
            .ToListAsync(ct);

        // The home directory, unless a tenant has claimed it. Syncing it both ways would
        // create the same people twice — once owned by no tenant and once owned by that
        // one — and leave two sets of delta state describing one directory.
        var homeTenantId = _graphOptions.Value.TenantId;
        var homeIsClaimed = !string.IsNullOrWhiteSpace(homeTenantId)
            && connected.Any(t => string.Equals(t.AzureAdTenantId, homeTenantId, StringComparison.OrdinalIgnoreCase));

        if (!homeIsClaimed)
        {
            try
            {
                var homeToken = await GetStoredDeltaTokenAsync(null, ct);
                results.Add(await SyncAsync(null, null, homeToken, ct));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Directory sync failed for the home directory");
                results.Add(new AdSyncResult(
                    0, 0, 0, 0, [$"Sync failed: {ex.Message}"], null, null, null, Succeeded: false));
            }
        }

        foreach (var tenant in connected)
        {
            try
            {
                var token = await GetStoredDeltaTokenAsync(tenant.Id, ct);
                results.Add(await SyncAsync(tenant.Id, tenant.AzureAdTenantId, token, ct));
            }
            catch (Exception ex)
            {
                // One customer's directory being unreachable must not stop the rest.
                _logger.LogError(ex, "Directory sync failed for tenant {TenantId}", tenant.Id);
                results.Add(new AdSyncResult(
                    0, 0, 0, 0, [$"Sync failed: {ex.Message}"], null,
                    tenant.Id, tenant.Name, Succeeded: false));
            }
        }

        return results;
    }

    public async Task<AdSyncResult> SyncAsync(
        int? tenantId, string? entraTenantId, string? deltaToken, CancellationToken ct = default)
    {
        var tenantName = tenantId.HasValue
            ? (await _db.Tenants.Where(t => t.Id == tenantId).Select(t => t.Name).FirstOrDefaultAsync(ct))
            : null;

        var delta = await _graphApi.SyncUsersDeltaAsync(entraTenantId, deltaToken, ct);
        var users = delta.Users;

        // A failed read returns no users, which is indistinguishable from a directory in
        // which everyone has left. Deactivating on that basis would empty the tenant's
        // staff list because Graph was briefly unreachable, so a failed cycle changes
        // nothing at all and says so.
        if (!delta.Succeeded)
        {
            _logger.LogWarning(
                "Directory read failed for tenant {TenantId}; nothing was written and nobody was deactivated",
                tenantId);
            return new AdSyncResult(
                0, 0, 0, 0,
                ["The directory could not be read, so nothing was changed. Check the connection and try again."],
                deltaToken, tenantId, tenantName, Succeeded: false);
        }

        if (users.Count == 0 && deltaToken != null)
        {
            await StoreDeltaTokenAsync(delta.DeltaToken, tenantId, ct);
            return new AdSyncResult(0, 0, 0, 0, [], delta.DeltaToken, tenantId, tenantName);
        }

        var skipped = new List<string>();
        var created = 0;
        var updated = 0;

        foreach (var user in users)
        {
            // Employee.Email is required and uniquely indexed with no filter for blanks.
            // An Entra account with no mailbox — every cloud-only account created in the
            // portal — yields an empty address, so a batch containing two of them violated
            // the index and rolled back the ENTIRE sync, silently. One unusable record must
            // not cost every usable one, so these are skipped and named instead.
            if (string.IsNullOrWhiteSpace(user.Email))
            {
                // Say what to do about it. "No email address" is true but leaves the
                // administrator guessing, and the usual cause — a guest account, whose UPN
                // is not a real address — has a specific remedy.
                skipped.Add(
                    $"{Describe(user)} — no usable email address in Entra. Set 'mail' or "
                    + "'otherMails' on the account (guest accounts have no mailbox of their own).");
                continue;
            }

            var existing = await _db.Employees
                .FirstOrDefaultAsync(e => e.AzureAdObjectId == user.AzureAdObjectId, ct);

            if (existing != null)
            {
                existing.FirstName = user.FirstName;
                existing.LastName = user.LastName;
                existing.Title = user.Title;
                existing.Email = user.Email;
                existing.OfficePhone = user.OfficePhone;
                existing.MobilePhone = user.MobilePhone;
                existing.OfficeLocation = user.OfficeLocation;
                existing.Source = "Ad";
                existing.LastSyncedAt = DateTime.UtcNow;
                existing.TenantId ??= tenantId;
                updated++;
                continue;
            }

            // A different person may already hold this address — the unique index would
            // reject the insert and take the batch down with it. Report it as a conflict.
            var emailTaken = await _db.Employees
                .AnyAsync(e => e.Email == user.Email, ct);
            if (emailTaken)
            {
                skipped.Add($"{Describe(user)} — another directory record already uses that email address.");
                continue;
            }

            user.Source = "Ad";
            user.LastSyncedAt = DateTime.UtcNow;
            // Bind the person to the tenant whose directory they came from. Without this
            // every synced employee belonged to no tenant, so tenant-scoped queries
            // filtered them all out and a sync that reported "3 users processed" appeared
            // to have done nothing at all.
            user.TenantId = tenantId;
            _db.Employees.Add(user);
            created++;
        }

        var adObjectIds = users.Select(u => u.AzureAdObjectId).ToHashSet();

        // Only this tenant's people are candidates. Estate-wide, syncing one customer
        // deactivated every other customer's staff, because nobody else's object ids
        // appear in this directory's response.
        var activeUsers = await _db.Employees
            .Where(e => e.IsActive && e.TenantId == tenantId)
            .ToListAsync(ct);
        var toDeactivate = SelectEmployeesToDeactivate(activeUsers, adObjectIds, tenantId);

        foreach (var active in toDeactivate)
        {
            active.IsActive = false;
            active.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
        await StoreDeltaTokenAsync(delta.DeltaToken, tenantId, ct);

        if (skipped.Count > 0)
        {
            _logger.LogWarning(
                "AD sync skipped {Count} user(s) that could not be stored: {Reasons}",
                skipped.Count, string.Join(" | ", skipped));
        }

        _logger.LogInformation(
            "AD sync: {Fetched} fetched, {Created} created, {Updated} updated, {Deactivated} deactivated, {Skipped} skipped",
            users.Count, created, updated, toDeactivate.Count, skipped.Count);

        return new AdSyncResult(
            users.Count, created, updated, toDeactivate.Count, skipped, delta.DeltaToken,
            tenantId, tenantName);
    }

    /// <summary>Names a user without assuming any particular field is populated.</summary>
    private static string Describe(Employee user)
    {
        var name = $"{user.FirstName} {user.LastName}".Trim();
        return !string.IsNullOrWhiteSpace(name) ? name
            : !string.IsNullOrWhiteSpace(user.AzureAdObjectId) ? user.AzureAdObjectId
            : "an unnamed directory entry";
    }

    private async Task StoreDeltaTokenAsync(string? deltaToken, int? tenantId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(deltaToken)) return;

        var key = DeltaTokenKey(tenantId);
        var setting = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (setting != null)
        {
            setting.Value = deltaToken;
        }
        else
        {
            _db.AppSettings.Add(new AppSetting
            {
                Key = key,
                Value = deltaToken,
                Description = "Azure AD Graph API delta token for incremental user sync"
            });
        }
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Pure rule behind AD deactivation: only records tagged <see cref="Employee.Source"/>
    /// == "Ad" and no longer present in AD are selected. Local/CsvImport/unsourced records
    /// are never deactivated, regardless of their <see cref="Employee.AzureAdObjectId"/>.
    /// Kept internal/static so the invariant is unit-testable.
    /// </summary>
    internal static List<Employee> SelectEmployeesToDeactivate(
        IEnumerable<Employee> activeEmployees, HashSet<string> adObjectIds) =>
        activeEmployees
            .Where(e => e.Source == "Ad" && !adObjectIds.Contains(e.AzureAdObjectId))
            .ToList();

    /// <summary>
    /// The same rule, restricted to one tenant's people. A directory can only speak for
    /// its own tenant, so absence from it says nothing about anybody else.
    /// </summary>
    internal static List<Employee> SelectEmployeesToDeactivate(
        IEnumerable<Employee> activeEmployees, HashSet<string> adObjectIds, int? tenantId) =>
        SelectEmployeesToDeactivate(
            activeEmployees.Where(e => e.TenantId == tenantId), adObjectIds);
}
