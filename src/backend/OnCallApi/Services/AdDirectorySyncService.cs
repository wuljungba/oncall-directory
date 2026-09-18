using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OnCallApi.Configuration;
using OnCallApi.Data;
using OnCallApi.Models;
using OnCallApi.Validators;

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
    bool Succeeded = true,
    bool WasFullEnumeration = false,
    bool Completed = false,
    int PagesRead = 0,
    int DeactivatedByRemoval = 0,
    int DeactivationsRefused = 0)
{
    public bool AnythingWritten => Created > 0 || Updated > 0 || Deactivated > 0;

    /// <summary>A run that refused its own deactivation batch needs a person to look at it.</summary>
    public bool NeedsAttention => DeactivationsRefused > 0 || !Succeeded;
}

public interface IAdDirectorySyncService
{
    /// <summary>
    /// Syncs one directory into one OnCall tenant. A null <paramref name="tenantId"/> is
    /// the home directory and the employees it owns, which is how this behaved before
    /// connected directories existed.
    /// </summary>
    Task<AdSyncResult> SyncAsync(
        int? tenantId, string? entraTenantId, string? deltaLink, CancellationToken ct = default);

    Task<string?> GetStoredDeltaTokenAsync(int? tenantId, CancellationToken ct = default);

    /// <summary>
    /// Syncs every tenant that has a connected directory, plus the home directory. One
    /// tenant failing does not stop the others.
    ///
    /// <paramref name="forceFull"/> discards each stored cursor for this run, which is the
    /// operator's escape hatch when a directory looks wrong.
    /// </summary>
    Task<IReadOnlyList<AdSyncResult>> SyncAllAsync(bool forceFull = false, CancellationToken ct = default);
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
    private readonly IConfiguration _config;
    private readonly ILogger<AdDirectorySyncService> _logger;

    /// <summary>
    /// Defaults for the deactivation valve. A quarter of a directory leaving between two sync
    /// cycles is not turnover, it is a fault — and this service reading one page of a paginated
    /// directory, then deactivating everyone on the pages it never read, is exactly what that
    /// fault looked like in production.
    /// </summary>
    private const double DefaultMaxDeactivationShare = 0.25;

    /// <summary>
    /// Below this many active staff the share is meaningless: in a team of three, one departure
    /// is 33%. Small tenants are reconciled without the valve.
    /// </summary>
    private const int DefaultDeactivationGuardFloor = 10;

    public AdDirectorySyncService(
        AppDbContext db,
        IGraphApiService graphApi,
        IOptions<GraphApiOptions> graphOptions,
        IConfiguration config,
        ILogger<AdDirectorySyncService> logger)
    {
        _db = db;
        _graphApi = graphApi;
        _graphOptions = graphOptions;
        _config = config;
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

    public async Task<IReadOnlyList<AdSyncResult>> SyncAllAsync(
        bool forceFull = false, CancellationToken ct = default)
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
                var homeLink = forceFull ? null : await GetStoredDeltaTokenAsync(null, ct);
                results.Add(await SyncAsync(null, null, homeLink, ct));
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
                var link = forceFull ? null : await GetStoredDeltaTokenAsync(tenant.Id, ct);
                results.Add(await SyncAsync(tenant.Id, tenant.AzureAdTenantId, link, ct));
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
        int? tenantId, string? entraTenantId, string? deltaLink, CancellationToken ct = default)
    {
        var tenantName = tenantId.HasValue
            ? (await _db.Tenants.Where(t => t.Id == tenantId).Select(t => t.Name).FirstOrDefaultAsync(ct))
            : null;

        var delta = await _graphApi.SyncUsersDeltaAsync(entraTenantId, deltaLink, ct);
        var users = delta.Users;

        // A failed read returns no users, which is indistinguishable from a directory in
        // which everyone has left. Deactivating on that basis would empty the tenant's
        // staff list because Graph was briefly unreachable, so a failed cycle changes
        // nothing at all and says so.
        if (delta.ReadNothing)
        {
            _logger.LogWarning(
                "Directory read failed for tenant {TenantId} ({Detail}); nothing was written and nobody was deactivated",
                tenantId, delta.FailureDetail);
            return new AdSyncResult(
                0, 0, 0, 0,
                [delta.FailureDetail is { Length: > 0 } why
                    ? $"The directory could not be read, so nothing was changed: {why}"
                    : "The directory could not be read, so nothing was changed. Check the connection and try again."],
                deltaLink, tenantId, tenantName, Succeeded: false,
                WasFullEnumeration: delta.WasFullEnumeration, Completed: false, PagesRead: delta.PagesRead);
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
                ApplyGraphUserToEmployee(user, existing, tenantId);
                updated++;
                continue;
            }

            // A different person may already hold this address — the unique index would
            // reject the insert and take the batch down with it. Report it as a conflict.
            // Guarded on both sides. Email is optional now, and SQL Server would not
            // match NULL to NULL here but the in-memory provider used by the tests
            // would -- so an email-less department contact could report every mailbox-less
            // directory user as a conflict and skip them all.
            var emailTaken = !string.IsNullOrWhiteSpace(user.Email)
                && await _db.Employees.AnyAsync(
                    e => e.Email != null && e.Email == user.Email, ct);
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
            // A disabled account arrives inactive; creating it active would undo on insert what
            // the departure rules below do on update.
            _db.Employees.Add(user);
            created++;
        }

        // ── Departures ──────────────────────────────────────────────────────────────────
        //
        // A delta response contains only CHANGES. "Absent from the response" therefore means
        // "unchanged", not "gone", and reading it as departure is precisely the bug this rework
        // exists to fix: the old code read one page of a paginated directory and deactivated
        // everybody on the pages it never read.
        //
        // So absence may be read as departure ONLY for an enumeration that started from nothing
        // and reached the end (GraphUserDeltaResult.MayReconcileByAbsence). In every other run,
        // departure has to be something Graph said outright — a @removed id, or an account it
        // reported as disabled.

        // Only this tenant's people are candidates. Estate-wide, syncing one customer
        // deactivated every other customer's staff, because nobody else's object ids
        // appear in this directory's response.
        var activeUsers = await _db.Employees
            .Where(e => e.IsActive && e.TenantId == tenantId)
            .ToListAsync(ct);

        var departedIds = new List<string>(delta.RemovedObjectIds);

        if (DeactivateOnDisabledAccount())
        {
            // Most directories never delete a leaver; they disable the account. Without this,
            // an incremental run has almost no departure signal at all.
            departedIds.AddRange(users.Where(u => !u.IsActive).Select(u => u.AzureAdObjectId));
        }

        var byRemoval = SelectEmployeesToDeactivateByRemoval(activeUsers, departedIds, tenantId);

        var seenObjectIds = users
            .Where(u => u.IsActive)
            .Select(u => u.AzureAdObjectId)
            // Graph echoes back the id casing it holds, which need not match what we stored.
            // With an ordinal set, a casing difference reads as "absent" and deactivates a
            // present colleague. PresenceSyncService learned this the same way.
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var byAbsence = delta.MayReconcileByAbsence
            ? SelectEmployeesToDeactivate(activeUsers, seenObjectIds, tenantId)
            : [];

        var toDeactivate = byRemoval
            .Concat(byAbsence)
            .DistinctBy(e => e.Id)
            .ToList();

        var valve = EvaluateDeactivationBatch(
            activeUsers.Count, toDeactivate.Count, MaxDeactivationShare(tenantId), DeactivationGuardFloor());

        var refused = 0;
        if (!valve.Allowed)
        {
            refused = toDeactivate.Count;
            toDeactivate = [];
            skipped.Add(valve.Reason!);
            _logger.LogError(
                "Directory sync for tenant {TenantId} refused its deactivation batch: {Reason}",
                tenantId, valve.Reason);
        }

        foreach (var active in toDeactivate)
        {
            active.IsActive = false;
            active.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);

        // The cursor advances only on a complete read whose conclusions were actually applied.
        // Advancing after a refusal would turn the alarm off: the next run would be incremental,
        // see nothing unusual, and the refusal would be forgotten rather than fixed.
        var storedLink = false;
        if (delta.Completed && !string.IsNullOrEmpty(delta.DeltaLink) && refused == 0)
        {
            await StoreDeltaTokenAsync(delta.DeltaLink, tenantId, ct);
            storedLink = true;
        }

        if (skipped.Count > 0)
        {
            _logger.LogWarning(
                "AD sync skipped {Count} user(s) that could not be stored: {Reasons}",
                skipped.Count, string.Join(" | ", skipped));
        }

        _logger.LogInformation(
            "AD sync ({Mode}, {Pages} page(s), complete: {Complete}): {Fetched} fetched, {Created} created, "
            + "{Updated} updated, {Deactivated} deactivated ({ByRemoval} reported gone), {Refused} refused, {Skipped} skipped",
            delta.WasFullEnumeration ? "full" : "incremental", delta.PagesRead, delta.Completed,
            users.Count, created, updated, toDeactivate.Count, byRemoval.Count, refused, skipped.Count);

        return new AdSyncResult(
            users.Count, created, updated, toDeactivate.Count, skipped,
            storedLink ? delta.DeltaLink : deltaLink,
            tenantId, tenantName,
            Succeeded: delta.Completed,
            WasFullEnumeration: delta.WasFullEnumeration,
            Completed: delta.Completed,
            PagesRead: delta.PagesRead,
            DeactivatedByRemoval: byRemoval.Count,
            DeactivationsRefused: refused);
    }

    /// <summary>Names a user without assuming any particular field is populated.</summary>
    private static string Describe(Employee user)
    {
        var name = $"{user.FirstName} {user.LastName}".Trim();
        return !string.IsNullOrWhiteSpace(name) ? name
            : !string.IsNullOrWhiteSpace(user.AzureAdObjectId) ? user.AzureAdObjectId
            : "an unnamed directory entry";
    }

    private double MaxDeactivationShare(int? tenantId)
    {
        if (tenantId.HasValue)
        {
            var perTenant = _config.GetValue<double?>($"Sync:MaxDeactivationShare:{tenantId.Value}");
            if (perTenant.HasValue) return perTenant.Value;
        }

        return _config.GetValue<double?>("Sync:MaxDeactivationShare") ?? DefaultMaxDeactivationShare;
    }

    private int DeactivationGuardFloor() =>
        _config.GetValue<int?>("Sync:DeactivationGuardFloor") ?? DefaultDeactivationGuardFloor;

    private bool DeactivateOnDisabledAccount() =>
        _config.GetValue<bool?>("Sync:DeactivateOnDisabledAccount") ?? true;

    /// <summary>
    /// Whether a proposed deactivation batch is plausible turnover or evidence of a fault.
    ///
    /// Pure so the boundaries are table-testable, and deliberately blunt: a sync that would
    /// deactivate a quarter of a hospital's staff in one cycle is refused outright, because the
    /// failure it guards against — reading part of a directory and treating the rest as departed
    /// — looks exactly like a very successful sync from the inside.
    /// </summary>
    internal static (bool Allowed, string? Reason) EvaluateDeactivationBatch(
        int activeCount, int proposed, double maxShare, int guardFloor)
    {
        if (proposed <= 0) return (true, null);
        if (activeCount <= guardFloor) return (true, null);

        var share = proposed / (double)activeCount;
        if (share <= maxShare) return (true, null);

        return (false,
            $"Refused to deactivate {proposed} of {activeCount} active staff ({share:P0}, limit {maxShare:P0}). "
            + "Nobody was deactivated and the sync cursor was left alone, so this will be re-checked next run. "
            + "Confirm the directory connection before raising Sync:MaxDeactivationShare.");
    }

    /// <summary>
    /// Graph's values win where Graph has one, and are ignored where it does not.
    ///
    /// A blank from Graph used to overwrite whatever was stored, so a mobile number an
    /// administrator had typed in was erased on the next sync — and a clinician with no number
    /// is a clinician a code call cannot reach. The importer settled this same question the same
    /// way (BulkImportService.ApplyRowToEmployee); this keeps the two paths consistent.
    ///
    /// The cost is stated plainly: a title genuinely cleared in Entra stays until something
    /// replaces it. Keeping a stale title beats losing a curated phone number.
    /// </summary>
    internal static void ApplyGraphUserToEmployee(Employee incoming, Employee existing, int? tenantId)
    {
        existing.FirstName = PreferIncoming(incoming.FirstName, existing.FirstName) ?? existing.FirstName;
        existing.LastName = PreferIncoming(incoming.LastName, existing.LastName) ?? existing.LastName;
        existing.Title = PreferIncoming(incoming.Title, existing.Title);
        existing.Email = PreferIncoming(incoming.Email, existing.Email);
        existing.OfficePhone = PreferIncoming(incoming.OfficePhone, existing.OfficePhone);
        existing.MobilePhone = PreferIncoming(incoming.MobilePhone, existing.MobilePhone);
        existing.Extension = PreferIncoming(incoming.Extension, existing.Extension);
        existing.OfficeLocation = PreferIncoming(incoming.OfficeLocation, existing.OfficeLocation);

        // Unconditional: these describe the sync itself rather than the person.
        existing.Source = "Ad";
        existing.LastSyncedAt = DateTime.UtcNow;
        existing.TenantId ??= tenantId;
        existing.UpdatedAt = DateTime.UtcNow;
    }

    private static string? PreferIncoming(string? incoming, string? existing) =>
        string.IsNullOrWhiteSpace(incoming) ? existing : incoming.Trim();

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

    /// <summary>
    /// Departures Graph named outright — a <c>@removed</c> entry, or an account it reported as
    /// disabled. Unlike absence, this is a fact about a specific person, so it is safe to act on
    /// in an incremental run. The same Source and tenant guards apply: a local record that
    /// happens to carry that object id is still not the directory's to deactivate.
    /// </summary>
    internal static List<Employee> SelectEmployeesToDeactivateByRemoval(
        IEnumerable<Employee> activeEmployees, IReadOnlyCollection<string> removedObjectIds, int? tenantId)
    {
        if (removedObjectIds.Count == 0) return [];

        var removed = removedObjectIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return activeEmployees
            .Where(e => e.TenantId == tenantId
                && e.Source == "Ad"
                && !string.IsNullOrWhiteSpace(e.AzureAdObjectId)
                && removed.Contains(e.AzureAdObjectId))
            .ToList();
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
                Description = "Azure AD Graph API delta link for incremental user sync"
            });
        }
        await _db.SaveChangesAsync(ct);
    }
}
