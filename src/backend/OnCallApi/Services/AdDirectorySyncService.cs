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
        int? tenantId, string? entraTenantId, string? deltaLink,
        string? triggeredBy = null, CancellationToken ct = default);

    Task<string?> GetStoredDeltaTokenAsync(int? tenantId, CancellationToken ct = default);

    /// <summary>
    /// Syncs every tenant that has a connected directory, plus the home directory. One
    /// tenant failing does not stop the others.
    ///
    /// <paramref name="forceFull"/> discards each stored cursor for this run, which is the
    /// operator's escape hatch when a directory looks wrong. <paramref name="triggeredBy"/>
    /// is recorded on each run, so a mass deactivation is attributable to whoever started it.
    /// </summary>
    Task<IReadOnlyList<AdSyncResult>> SyncAllAsync(
        bool forceFull = false, string? triggeredBy = null, CancellationToken ct = default);
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

    /// <summary>
    /// The cursor for one directory, from <see cref="SyncState"/>.
    ///
    /// Adopts the old AppSettings row the first time it finds one, then deletes it: left in
    /// place it stays readable through GET /api/settings by anyone holding Schedule.Read. Only
    /// a real deltaLink is carried across — a stored nextLink is a mid-enumeration cursor from
    /// the code this replaced, and replaying one returns the tail of a stale page set.
    /// </summary>
    public async Task<string?> GetStoredDeltaTokenAsync(int? tenantId, CancellationToken ct = default)
    {
        var state = await _db.SyncStates
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.Source == SyncSources.AdUsers, ct);

        if (state != null) return state.DeltaLink;

        var key = DeltaTokenKey(tenantId);
        var legacy = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (legacy == null) return null;

        var carriedOver = GraphApiService.IsUsableDeltaLink(legacy.Value) ? legacy.Value : null;

        _db.SyncStates.Add(new SyncState
        {
            TenantId = tenantId,
            Source = SyncSources.AdUsers,
            DeltaLink = carriedOver,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.AppSettings.Remove(legacy);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Moved the delta cursor for tenant {TenantId} out of AppSettings{Discarded}",
            tenantId, carriedOver == null ? " and discarded it: it was not a deltaLink" : "");

        return carriedOver;
    }

    public async Task<IReadOnlyList<AdSyncResult>> SyncAllAsync(
        bool forceFull = false, string? triggeredBy = null, CancellationToken ct = default)
    {
        var results = new List<AdSyncResult>();

        var targets = await DirectorySyncTargets.ResolveAsync(_db, _graphOptions.Value.TenantId, ct);

        foreach (var target in targets)
        {
            try
            {
                var link = forceFull ? null : await GetStoredDeltaTokenAsync(target.TenantId, ct);
                results.Add(await SyncAsync(target.TenantId, target.EntraTenantId, link, triggeredBy, ct));
            }
            catch (Exception ex)
            {
                // One customer's directory being unreachable must not stop the rest.
                _logger.LogError(ex, "Directory sync failed for {Directory}", target.Name);
                results.Add(new AdSyncResult(
                    0, 0, 0, 0, [$"Sync failed: {ex.Message}"], null,
                    target.TenantId, target.TenantId.HasValue ? target.Name : null, Succeeded: false));
            }
        }

        return results;
    }

    public async Task<AdSyncResult> SyncAsync(
        int? tenantId, string? entraTenantId, string? deltaLink,
        string? triggeredBy = null, CancellationToken ct = default)
    {
        var startedAt = DateTime.UtcNow;
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

            // Recorded like any other run. A directory nobody consented to fails on every cycle,
            // and a failure that leaves no trace is exactly how that goes unnoticed for months.
            _db.SyncRuns.Add(new SyncRun
            {
                TenantId = tenantId,
                Source = SyncSources.AdUsers,
                Mode = delta.WasFullEnumeration ? SyncModes.Full : SyncModes.Incremental,
                Outcome = SyncOutcomes.Failed,
                StartedAt = startedAt,
                CompletedAt = DateTime.UtcNow,
                PagesRead = delta.PagesRead,
                TokenWasRejected = delta.TokenWasRejected,
                TriggeredBy = triggeredBy ?? TimerActor,
                FailureDetail = Truncate(delta.FailureDetail, 2000),
            });
            await _db.SaveChangesAsync(ct);

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

        var byRemoval = SelectEmployeesToDeactivateByRemoval(activeUsers, delta.RemovedObjectIds, tenantId);

        // Most directories never delete a leaver; they disable the account. Without this, an
        // incremental run has almost no departure signal at all. Counted separately because the
        // two answer different questions: one is "Graph says they are gone", the other is
        // "Graph says they can no longer sign in".
        var disabledIds = DeactivateOnDisabledAccount()
            ? users.Where(u => !u.IsActive).Select(u => u.AzureAdObjectId).ToList()
            : [];
        var byDisabledAccount = SelectEmployeesToDeactivateByRemoval(activeUsers, disabledIds, tenantId);

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
            .Concat(byDisabledAccount)
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

        // Audit written in-band rather than through IAuditService, which is a bounded channel
        // set to DropOldest: fine for read tracing, not for the record of who retired forty
        // clinicians. Staged in the same SaveChanges as the deactivations, so the evidence
        // exists if and only if the change did. The bulk admin path settled this the same way.
        StageDeactivationAudit(toDeactivate, refused, valve.Reason, tenantId, triggeredBy, delta);

        // The cursor advances only on a complete read whose conclusions were actually applied.
        // Advancing after a refusal would turn the alarm off: the next run would be incremental,
        // see nothing unusual, and the refusal would be forgotten rather than fixed.
        var storedLink = delta.Completed && !string.IsNullOrEmpty(delta.DeltaLink) && refused == 0;
        if (storedLink)
        {
            await StageDeltaLinkAsync(delta.DeltaLink!, tenantId, ct);
        }

        var outcome = refused > 0 ? SyncOutcomes.Refused
            : !delta.Completed ? SyncOutcomes.Partial
            : SyncOutcomes.Succeeded;

        _db.SyncRuns.Add(new SyncRun
        {
            TenantId = tenantId,
            Source = SyncSources.AdUsers,
            Mode = delta.WasFullEnumeration ? SyncModes.Full : SyncModes.Incremental,
            Outcome = outcome,
            StartedAt = startedAt,
            CompletedAt = DateTime.UtcNow,
            PagesRead = delta.PagesRead,
            Fetched = users.Count,
            Created = created,
            Updated = updated,
            Skipped = skipped.Count,
            Deactivated = toDeactivate.Count,
            DeactivatedByRemoval = byRemoval.Count,
            DeactivatedByDisabledAccount = byDisabledAccount.Count,
            DeactivationsRefused = refused,
            DeltaLinkStored = storedLink,
            TokenWasRejected = delta.TokenWasRejected,
            TriggeredBy = triggeredBy ?? TimerActor,
            FailureDetail = Truncate(delta.FailureDetail, 2000),
            Notes = skipped.Count > 0 ? Truncate(string.Join(" | ", skipped), 4000) : null,
        });

        // One save for everything this cycle concluded: people, the audit of retiring them, the
        // record of the run, and the cursor. The cursor used to commit in a second save of its
        // own, which left a window where it advanced past work that had not been written.
        await _db.SaveChangesAsync(ct);
        await PruneRunHistoryAsync(tenantId, ct);

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

    /// <summary>
    /// Points this directory's cursor at the new deltaLink, WITHOUT saving: the caller commits
    /// it in the same transaction as the people it describes. Saved on its own, a cursor can
    /// advance past work that was never written.
    /// </summary>
    private async Task StageDeltaLinkAsync(string deltaLink, int? tenantId, CancellationToken ct)
    {
        var state = await _db.SyncStates
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.Source == SyncSources.AdUsers, ct);

        if (state == null)
        {
            _db.SyncStates.Add(new SyncState
            {
                TenantId = tenantId,
                Source = SyncSources.AdUsers,
                DeltaLink = deltaLink,
                UpdatedAt = DateTime.UtcNow,
            });
            return;
        }

        state.DeltaLink = deltaLink;
        state.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Who a timer-driven run is attributable to. AuditLog.PrincipalId exists for exactly this.</summary>
    private const string TimerActor = "system:ad-sync";

    /// <summary>
    /// The audit trail for retiring people, staged into the caller's transaction.
    ///
    /// Written straight to the table rather than through IAuditService, which is a bounded
    /// channel set to DropOldest — acceptable for read tracing, not for the record of who
    /// retired forty clinicians. One row per person, capped, plus a summary: an auditor asks
    /// about individuals, not totals. The bulk admin actions settled this the same way.
    /// </summary>
    private void StageDeactivationAudit(
        IReadOnlyList<Employee> deactivated, int refused, string? refusalReason,
        int? tenantId, string? triggeredBy, GraphUserDeltaResult delta)
    {
        if (deactivated.Count == 0 && refused == 0) return;

        var actor = triggeredBy ?? TimerActor;
        var mode = delta.WasFullEnumeration ? SyncModes.Full : SyncModes.Incremental;
        var cap = DeactivationAuditDetailCap();

        foreach (var person in deactivated.Take(cap))
        {
            _db.AuditLogs.Add(new AuditLog
            {
                UserId = Guid.Empty,
                PrincipalId = actor,
                UserName = "Directory sync",
                Action = "Deactivated",
                ResourceType = "Employee",
                ResourceId = person.Id.ToString(),
                TenantId = tenantId,
                IpAddress = "system",
                Details = $"Source=AdUsers;Mode={mode};Pages={delta.PagesRead};ObjectId={person.AzureAdObjectId}",
                Timestamp = DateTime.UtcNow,
            });
        }

        var truncated = deactivated.Count > cap ? $";DetailRowsCapped={cap}" : "";

        _db.AuditLogs.Add(new AuditLog
        {
            UserId = Guid.Empty,
            PrincipalId = actor,
            UserName = "Directory sync",
            Action = refused > 0 ? "DeactivationRefused" : "Deactivated",
            ResourceType = "DirectorySync",
            ResourceId = tenantId?.ToString() ?? "home",
            TenantId = tenantId,
            IpAddress = "system",
            Details = refused > 0
                ? $"Source=AdUsers;Mode={mode};Pages={delta.PagesRead};Refused={refused};Reason={refusalReason}"
                : $"Source=AdUsers;Mode={mode};Pages={delta.PagesRead};Deactivated={deactivated.Count}{truncated}",
            Timestamp = DateTime.UtcNow,
        });
    }

    private int DeactivationAuditDetailCap() =>
        _config.GetValue<int?>("Sync:DeactivationAuditDetailCap") ?? 200;

    /// <summary>
    /// Keeps the run history to a window. One row per directory per cycle is ~35k rows a year
    /// at the fifteen-minute default, and these are operational metrics — the audit rows they
    /// point at keep their own six-year retention. Done in-band rather than as another
    /// background service: it is one delete on a table this method just wrote to.
    /// </summary>
    private async Task PruneRunHistoryAsync(int? tenantId, CancellationToken ct)
    {
        var days = _config.GetValue<int?>("Sync:RunHistoryDays") ?? 90;
        if (days <= 0) return;

        var cutoff = DateTime.UtcNow.AddDays(-days);
        var stale = await _db.SyncRuns
            .Where(r => r.TenantId == tenantId && r.Source == SyncSources.AdUsers && r.StartedAt < cutoff)
            .ToListAsync(ct);

        if (stale.Count == 0) return;

        _db.SyncRuns.RemoveRange(stale);
        await _db.SaveChangesAsync(ct);
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];
}
