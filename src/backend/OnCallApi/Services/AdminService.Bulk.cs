using Microsoft.EntityFrameworkCore;
using OnCallApi.Authorization;
using OnCallApi.Models;

namespace OnCallApi.Services;

/// <summary>
/// Bulk employee lifecycle: deactivate, reactivate, permanently delete.
///
/// Onboarding an uploaded organisation, or cleaning up after a mistaken import, is otherwise a
/// one-record-at-a-time job over hundreds of rows.
///
/// Two properties shape everything here:
///
/// **Partial success is the ordinary outcome.** Every history table (shifts, time-off, swaps,
/// phone-tree membership, escalations) holds a Restrict foreign key on the employee, so anyone
/// who has ever been on call cannot be hard-deleted. A batch is routinely part-blocked, and the
/// caller is told per record rather than being handed one verdict for all of them.
///
/// **Removing a person removes their access.** Grants are keyed by email or object id and hold
/// no reference to the employee, so they outlive the row and would re-attach to whoever next
/// signed in with that address. Both deactivate and delete therefore revoke them — which also
/// means reactivating does not restore them, and the caller has to be told so.
/// </summary>
public partial class AdminService
{
    private enum BulkAction { Deactivate, Reactivate, Delete }

    public Task<BulkEmployeeActionResponse> BulkDeactivateEmployeesAsync(
        IReadOnlyList<Guid> employeeIds, string? reason, bool acknowledgePrivileged, CancellationToken ct = default)
        => RunBulkAsync(BulkAction.Deactivate, employeeIds, reason, acknowledgePrivileged, ct);

    public Task<BulkEmployeeActionResponse> BulkReactivateEmployeesAsync(
        IReadOnlyList<Guid> employeeIds, string? reason, bool acknowledgePrivileged, CancellationToken ct = default)
        => RunBulkAsync(BulkAction.Reactivate, employeeIds, reason, acknowledgePrivileged, ct);

    public Task<BulkEmployeeActionResponse> BulkDeleteEmployeesAsync(
        IReadOnlyList<Guid> employeeIds, string? reason, bool acknowledgePrivileged, CancellationToken ct = default)
        => RunBulkAsync(BulkAction.Delete, employeeIds, reason, acknowledgePrivileged, ct);

    private async Task<BulkEmployeeActionResponse> RunBulkAsync(
        BulkAction action,
        IReadOnlyList<Guid> employeeIds,
        string? reason,
        bool acknowledgePrivileged,
        CancellationToken ct)
    {
        // FilterEmployeesByTenant returns the query *unfiltered* when there is no principal,
        // which is a sane default for a background read and the wrong one here. Bulk writes fail
        // closed instead.
        if (CurrentUser is null)
            throw new UnauthorizedAccessException("Bulk actions require an authenticated administrator.");

        var batchId = Guid.NewGuid();

        var ids = employeeIds.Distinct().ToList();
        if (ids.Count > BulkLimits.MaxBatch)
        {
            await AuditRefusalAsync(action, batchId, ids.Count, "OverBatchLimit", reason, ct);
            throw new ArgumentOutOfRangeException(nameof(employeeIds),
                $"A bulk action takes at most {BulkLimits.MaxBatch} records at a time.");
        }

        var isSuperAdmin = _tenantContext.IsSuperAdmin(CurrentUser);
        var callerEmployeeId = await _tenantContext.GetCurrentEmployeeIdAsync(CurrentUser);

        var scoped = await (await FilterEmployeesByTenant(
                _db.Employees.Where(e => ids.Contains(e.Id))))
            .ToListAsync(ct);

        var byId = scoped.ToDictionary(e => e.Id);

        // Only meaningful for deletion, and skipped otherwise so a deactivation does not pay for
        // nine queries it will not read.
        var blockers = action == BulkAction.Delete
            ? await FindDeleteBlockersAsync(scoped.Select(e => e.Id).ToList(), ct)
            : new Dictionary<Guid, List<string>>();

        var privileged = await FindPrivilegedPrincipalsAsync(scoped, ct);

        // The caller's own record is skipped below whatever happens, so demanding they
        // acknowledge their own admin rights asks them to confirm something that was never
        // going to be acted on.
        if (callerEmployeeId.HasValue) privileged.Remove(callerEmployeeId.Value);

        if (action == BulkAction.Delete && privileged.Count > 0 && !acknowledgePrivileged)
        {
            // Recorded before refusing. An attempt to mass-delete administrator records is
            // itself the kind of event an audit trail exists to capture, and it would otherwise
            // leave no trace at all.
            await AuditRefusalAsync(action, batchId, ids.Count,
                $"Privileged={privileged.Count};NotAcknowledged", reason, ct);

            throw new InvalidOperationException(
                $"{privileged.Count} of these records belong to administrators whose rights this "
                + "will not remove. Confirm you have seen that before deleting them.");
        }

        var results = new List<BulkItemResult>(ids.Count);
        var systemWideLeft = 0;

        foreach (var id in ids)
        {
            if (!byId.TryGetValue(id, out var employee))
            {
                // No name, no address: this bucket includes another tenant's employee, and the
                // single-record path deliberately refuses to confirm that one exists.
                results.Add(new BulkItemResult
                {
                    EmployeeId = id,
                    Outcome = BulkOutcomes.NotFound,
                    Message = "No such record, or it belongs to a subscription you do not administer.",
                });
                continue;
            }

            var isPrivileged = privileged.Contains(employee.Id);

            if (callerEmployeeId.HasValue && callerEmployeeId.Value == employee.Id)
            {
                // The realistic accident is "select all, deactivate", which locks the
                // administrator out part-way through their own batch. The single-record actions
                // are untouched, so deliberately removing yourself is still possible.
                results.Add(Describe(employee, BulkOutcomes.SkippedSelf, isPrivileged,
                    "This is your own record. Use the single-record action if you mean it."));
                continue;
            }

            var wantsActive = action == BulkAction.Reactivate;
            if (action != BulkAction.Delete && employee.IsActive == wantsActive)
            {
                results.Add(Describe(employee, BulkOutcomes.AlreadyInState, isPrivileged,
                    employee.IsActive ? "Already active." : "Already deactivated."));
                continue;
            }

            if (action == BulkAction.Delete && blockers.TryGetValue(employee.Id, out var reasons))
            {
                results.Add(Describe(employee, BulkOutcomes.BlockedByHistory, isPrivileged,
                    $"Referenced by {string.Join(", ", reasons)}. Deactivate instead."));
                continue;
            }

            try
            {
                var (revoked, leftBehind) = action == BulkAction.Reactivate
                    ? (0, 0)
                    : await StageGrantRevocationAsync(employee, isSuperAdmin, ct);
                systemWideLeft += leftBehind;

                // Revoking grants alone leaves a working credential behind. Reactivating does
                // not switch it back on, for the same reason it does not restore permissions.
                var signInsDisabled = action == BulkAction.Reactivate
                    ? 0
                    : await StageLocalAccountDisableAsync(employee, ct);

                if (action == BulkAction.Delete)
                {
                    // Per record, inside this unit of work. Detaching every direct report up
                    // front would permanently null a surviving employee's manager link when
                    // their manager's delete turned out to be blocked.
                    var reports = await _db.Employees.Where(r => r.ManagerId == employee.Id).ToListAsync(ct);
                    foreach (var report in reports)
                    {
                        report.ManagerId = null;
                        report.UpdatedAt = DateTime.UtcNow;
                    }

                    _db.Employees.Remove(employee);
                }
                else
                {
                    employee.IsActive = wantsActive;
                    employee.UpdatedAt = DateTime.UtcNow;
                }

                AddBulkAudit(action, employee, batchId, revoked, reason);

                // One SaveChanges per record is one transaction per record. The revocation, the
                // lifecycle change and the audit row commit together or not at all — revoking
                // first and failing the delete afterwards would strip a still-present employee's
                // access while reporting that nothing happened.
                await _db.SaveChangesAsync(ct);

                results.Add(Describe(employee, BulkOutcomes.Succeeded, isPrivileged,
                    BuildRevocationMessage(employee, revoked, leftBehind, signInsDisabled, action),
                    revoked, signInsDisabled));
            }
            catch (DbUpdateException ex)
            {
                // The Removed entry stays Deleted in the change tracker and would be replayed by
                // the next record's SaveChanges, taking a healthy employee down with it. Clearing
                // is lossless only because everything staged above is scoped to this one record.
                _db.ChangeTracker.Clear();
                _logger.LogWarning(ex,
                    "Bulk {Action} could not complete for employee {EmployeeId} in batch {BatchId}",
                    action, employee.Id, batchId);

                results.Add(Describe(employee,
                    action == BulkAction.Delete ? BulkOutcomes.BlockedByHistory : BulkOutcomes.Failed,
                    isPrivileged,
                    action == BulkAction.Delete
                        ? "Referenced by schedule, time-off or phone-tree history. Deactivate instead."
                        : "Could not be saved."));
            }
        }

        var response = new BulkEmployeeActionResponse
        {
            Action = action.ToString().ToLowerInvariant(),
            BatchId = batchId,
            Requested = ids.Count,
            Succeeded = results.Count(r => r.Outcome == BulkOutcomes.Succeeded),
            Blocked = results.Count(r => r.Outcome == BulkOutcomes.BlockedByHistory),
            NotFound = results.Count(r => r.Outcome == BulkOutcomes.NotFound),
            Skipped = results.Count(r => r.Outcome is BulkOutcomes.SkippedSelf or BulkOutcomes.AlreadyInState),
            GrantsRevoked = results.Sum(r => r.GrantsRevoked),
            SignInsDisabled = results.Sum(r => r.SignInsDisabled),
            SystemWideGrantsLeft = systemWideLeft,
            PrivilegedPrincipals = results.Count(r => r.IsPrivilegedPrincipal),
            Results = results,
        };

        AddBatchSummaryAudit(action, batchId, response, reason);
        await _db.SaveChangesAsync(ct);

        return response;
    }

    private static BulkItemResult Describe(
        Employee e, string outcome, bool isPrivileged, string? message,
        int revoked = 0, int signInsDisabled = 0) => new()
        {
            EmployeeId = e.Id,
            DisplayName = e.DisplayName ?? $"{e.FirstName} {e.LastName}".Trim(),
            Email = e.Email,
            Outcome = outcome,
            Message = message,
            GrantsRevoked = revoked,
            SignInsDisabled = signInsDisabled,
            IsPrivilegedPrincipal = isPrivileged,
        };

    private static string? BuildRevocationMessage(
        Employee e, int revoked, int leftBehind, int signInsDisabled, BulkAction action)
    {
        if (action == BulkAction.Reactivate)
        {
            return "Permissions are not restored by reactivating, and any sign-in account stays "
                + "switched off — grant them again if needed.";
        }

        var parts = new List<string>();

        if (revoked == 0 && string.IsNullOrWhiteSpace(e.Email) && !PrincipalClaims.IsDirectoryObjectId(e.AzureAdObjectId))
        {
            // Usually a unit or service line, which is never a sign-in identity. Reporting zero
            // without saying why reads as a failure.
            parts.Add("No email or directory id on this record, so there were no permissions to revoke.");
        }
        else if (revoked == 0)
        {
            parts.Add("No permissions were held.");
        }
        else
        {
            parts.Add($"{revoked} permission grant{(revoked == 1 ? "" : "s")} revoked.");
        }

        if (signInsDisabled > 0)
            parts.Add($"{signInsDisabled} sign-in account{(signInsDisabled == 1 ? "" : "s")} switched off.");

        if (leftBehind > 0)
        {
            parts.Add($"{leftBehind} system-wide grant{(leftBehind == 1 ? "" : "s")} left in place — "
                + "only a super admin can revoke those.");
        }

        return string.Join(" ", parts);
    }

    /// <summary>
    /// Which of these employees cannot be hard-deleted, and why, in one pass.
    ///
    /// The columns below are exactly those configured Restrict against Employee. PhoneTreeEvent
    /// .InitiatedById is SetNull and DutyHourViolation cascades, so neither blocks and neither is
    /// scanned.
    ///
    /// This is the mechanism, not an optimisation. It turns an opaque DbUpdateException into a
    /// message naming what holds the record, and the in-memory provider the tests run on does not
    /// enforce foreign keys at all — so without this scan, blocked deletion would be untestable.
    /// </summary>
    private async Task<Dictionary<Guid, List<string>>> FindDeleteBlockersAsync(
        List<Guid> ids, CancellationToken ct)
    {
        var blockers = new Dictionary<Guid, List<string>>();

        void Note(IEnumerable<Guid> found, string what)
        {
            foreach (var id in found)
            {
                if (!blockers.TryGetValue(id, out var list))
                    blockers[id] = list = [];
                if (!list.Contains(what)) list.Add(what);
            }
        }

        Note(await _db.Shifts.Where(x => ids.Contains(x.EmployeeId))
            .Select(x => x.EmployeeId).Distinct().ToListAsync(ct), "shifts");

        Note(await _db.TimeOffs.Where(x => ids.Contains(x.EmployeeId))
            .Select(x => x.EmployeeId).Distinct().ToListAsync(ct), "time-off requests");

        Note(await _db.TimeOffs.Where(x => x.ApprovedById != null && ids.Contains(x.ApprovedById.Value))
            .Select(x => x.ApprovedById!.Value).Distinct().ToListAsync(ct), "time-off approvals");

        Note(await _db.ShiftSwaps.Where(x => ids.Contains(x.RequestedById))
            .Select(x => x.RequestedById).Distinct().ToListAsync(ct), "shift swaps");

        Note(await _db.ShiftSwaps.Where(x => x.ReplacementUserId != null && ids.Contains(x.ReplacementUserId.Value))
            .Select(x => x.ReplacementUserId!.Value).Distinct().ToListAsync(ct), "shift swaps");

        Note(await _db.ShiftSwaps.Where(x => x.ApprovedById != null && ids.Contains(x.ApprovedById.Value))
            .Select(x => x.ApprovedById!.Value).Distinct().ToListAsync(ct), "shift swap approvals");

        Note(await _db.PhoneTreeNodes.Where(x => x.EmployeeId != null && ids.Contains(x.EmployeeId.Value))
            .Select(x => x.EmployeeId!.Value).Distinct().ToListAsync(ct), "phone tree membership");

        Note(await _db.PhoneTreeEventParticipants.Where(x => x.EmployeeId != null && ids.Contains(x.EmployeeId.Value))
            .Select(x => x.EmployeeId!.Value).Distinct().ToListAsync(ct), "code call participation");

        Note(await _db.EscalationEvents.Where(x => ids.Contains(x.EmployeeId))
            .Select(x => x.EmployeeId).Distinct().ToListAsync(ct), "escalation history");

        return blockers;
    }

    /// <summary>
    /// Which of these people hold admin rights that revoking grants will not touch.
    ///
    /// Privilege comes from three places and only one of them is a PermissionGrant: the
    /// configured super-admin list, TenantAdmin rows, and grants. A grant can never carry
    /// Admin.* — ParseAssignablePermissionCsv strips it — so deactivating a sub-admin and
    /// reporting "access revoked" would be false. Flagged, never removed here: dropping a
    /// TenantAdmin row as a side effect of a directory cleanup is how a subscription ends up
    /// with no administrator at all.
    /// </summary>
    private async Task<HashSet<Guid>> FindPrivilegedPrincipalsAsync(
        List<Employee> employees, CancellationToken ct)
    {
        var oids = employees
            .Where(e => PrincipalClaims.IsDirectoryObjectId(e.AzureAdObjectId))
            .Select(e => e.AzureAdObjectId)
            .ToList();

        if (oids.Count == 0) return [];

        var adminOids = await _db.TenantAdmins
            .Where(a => oids.Contains(a.AzureAdObjectId))
            .Select(a => a.AzureAdObjectId)
            .ToListAsync(ct);

        var set = adminOids.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return employees
            .Where(e => e.AzureAdObjectId != null && set.Contains(e.AzureAdObjectId))
            .Select(e => e.Id)
            .ToHashSet();
    }

    /// <summary>
    /// Marks this employee's permission grants inactive, without saving.
    ///
    /// Matches an email case-insensitively and a real directory object id exactly, mirroring
    /// TenantClaimsMiddleware — which honours both, so revoking only the email would leave
    /// working access behind while reporting it removed. A null email never matches anything:
    /// two absent addresses are not the same person. PrincipalType is deliberately not filtered,
    /// because the middleware does not filter it either, so a "local" row confers just as much.
    ///
    /// Rows are switched off rather than deleted. A grant is an access-control record, not PHI;
    /// once the employee is gone the inactive row is the only remaining evidence that this
    /// address ever held these permissions, and both resolvers filter on IsActive so it confers
    /// nothing.
    /// </summary>
    private async Task<(int Revoked, int SystemWideLeft)> StageGrantRevocationAsync(
        Employee employee, bool isSuperAdmin, CancellationToken ct)
    {
        var email = string.IsNullOrWhiteSpace(employee.Email) ? null : employee.Email.Trim().ToLowerInvariant();
        var oid = PrincipalClaims.IsDirectoryObjectId(employee.AzureAdObjectId) ? employee.AzureAdObjectId : null;

        if (email is null && oid is null) return (0, 0);

        var candidates = await _db.PermissionGrants
            .Where(g => g.IsActive)
            .Where(g => (email != null && g.ExternalPrincipalId.ToLower() == email)
                     || (oid != null && g.ExternalPrincipalId == oid))
            .ToListAsync(ct);

        var revoked = 0;
        var systemWideLeft = 0;

        foreach (var grant in candidates)
        {
            // A system-wide grant reaches every subscription. Letting an administrator of one
            // destroy it by deactivating one of their own people would remove access everywhere
            // else, so it stays unless the caller could have created it in the first place.
            if (!grant.TenantId.HasValue && !isSuperAdmin)
            {
                systemWideLeft++;
                continue;
            }

            // Another subscription's grant is not this caller's to revoke.
            if (grant.TenantId.HasValue && grant.TenantId != employee.TenantId && !isSuperAdmin)
                continue;

            grant.IsActive = false;
            grant.UpdatedAt = DateTime.UtcNow;
            revoked++;
        }

        return (revoked, systemWideLeft);
    }

    /// <summary>
    /// Audit rows are written in-band, through the DbContext, rather than enqueued.
    ///
    /// IAuditService is a bounded channel set to DropOldest, so entries are discarded under load
    /// — acceptable for read tracing, not for the record of a mass deletion. Writing them in the
    /// same SaveChanges also means the audit row exists if and only if the change committed,
    /// which an enqueue cannot promise.
    /// </summary>
    private void AddBulkAudit(BulkAction action, Employee employee, Guid batchId, int revoked, string? reason)
    {
        var objectId = PrincipalClaims.GetObjectId(CurrentUser!);

        _db.AuditLogs.Add(new AuditLog
        {
            UserId = Guid.TryParse(objectId, out var parsed) ? parsed : Guid.Empty,
            PrincipalId = objectId,
            UserName = CurrentUser!.Identity?.Name ?? "",
            Action = action switch
            {
                BulkAction.Deactivate => "Deactivated",
                BulkAction.Reactivate => "Reactivated",
                _ => "Deleted",
            },
            ResourceType = "Employee",
            ResourceId = employee.Id.ToString(),
            Details = $"Bulk=true;BatchId={batchId};Source={employee.Source};"
                + $"GrantsRevoked={revoked};Reason={reason}",
            TenantId = employee.TenantId,
            IpAddress = _httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            Timestamp = DateTime.UtcNow,
        });
    }

    /// <summary>
    /// Records a bulk action that was refused before it touched anything.
    ///
    /// The summary row at the end of a run cannot cover this: refusals throw, so the run never
    /// reaches it. An attempt to mass-delete administrator records, or a batch far over the
    /// limit, is exactly the kind of event worth being able to look back at, and without this it
    /// left no trace whatsoever.
    /// </summary>
    private async Task AuditRefusalAsync(
        BulkAction action, Guid batchId, int requested, string why, string? reason, CancellationToken ct)
    {
        var objectId = PrincipalClaims.GetObjectId(CurrentUser!);

        _db.AuditLogs.Add(new AuditLog
        {
            UserId = Guid.TryParse(objectId, out var parsed) ? parsed : Guid.Empty,
            PrincipalId = objectId,
            UserName = CurrentUser!.Identity?.Name ?? "",
            Action = $"Bulk{action}Refused",
            ResourceType = "Employee",
            ResourceId = null,
            Details = $"BatchId={batchId};Requested={requested};Refused={why};Reason={reason}",
            TenantId = null,
            IpAddress = _httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            Timestamp = DateTime.UtcNow,
        });

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Switches off any local sign-in account belonging to this person, without saving.
    ///
    /// Revoking grants alone leaves a working credential: LocalAccount carries its own IsActive
    /// and only a bare EmployeeId with no foreign key, so deleting the employee neither cascades
    /// nor blocks — the account stays enabled and still authenticates, pointing at a row that no
    /// longer exists. Matched on both the employee id and the address because there is no
    /// constraint keeping the two in step.
    /// </summary>
    private async Task<int> StageLocalAccountDisableAsync(Employee employee, CancellationToken ct)
    {
        var email = string.IsNullOrWhiteSpace(employee.Email) ? null : employee.Email.Trim().ToLowerInvariant();

        var accounts = await _db.LocalAccounts
            .Where(a => a.IsActive)
            .Where(a => a.EmployeeId == employee.Id || (email != null && a.Email.ToLower() == email))
            .ToListAsync(ct);

        foreach (var account in accounts) account.IsActive = false;

        return accounts.Count;
    }

    /// <summary>
    /// One row for the request itself, so a mass action that changed nothing — every record
    /// blocked — is still recorded as having been attempted.
    /// </summary>
    private void AddBatchSummaryAudit(
        BulkAction action, Guid batchId, BulkEmployeeActionResponse response, string? reason)
    {
        var objectId = PrincipalClaims.GetObjectId(CurrentUser!);

        _db.AuditLogs.Add(new AuditLog
        {
            UserId = Guid.TryParse(objectId, out var parsed) ? parsed : Guid.Empty,
            PrincipalId = objectId,
            UserName = CurrentUser!.Identity?.Name ?? "",
            Action = $"Bulk{action}",
            ResourceType = "Employee",
            ResourceId = null,
            Details = $"BatchId={batchId};Requested={response.Requested};Succeeded={response.Succeeded};"
                + $"Blocked={response.Blocked};Skipped={response.Skipped};NotFound={response.NotFound};"
                + $"GrantsRevoked={response.GrantsRevoked};Reason={reason}",
            TenantId = null,
            IpAddress = _httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            Timestamp = DateTime.UtcNow,
        });
    }
}
