using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OnCallApi.Authorization;
using OnCallApi.Data;
using OnCallApi.Models;
using OnCallApi.Services;

namespace OnCallApi.Controllers;

/// <summary>
/// Per-user permission grants for the on-call schedule (and directory). Lets an
/// administrator assign granular read/write permissions (Schedule.Read,
/// Schedule.Write, Directory.Read, Directory.Write, …) to a specific user —
/// including external principals whose Entra/Google tokens carry no app roles.
/// Grants are honored by <c>TenantClaimsMiddleware</c>.
/// </summary>
[ApiController]
[Route("api/admin/permissions")]
// Scoped (department/tenant) admins are included deliberately: they must be able to
// provision their own users. Every action below re-checks the caller's tenants via
// CanManageTenantAsync, system-wide grants still require a super admin, and
// ParseAssignablePermissionCsv makes Admin.Full/Tenant.Manage impossible to hand out —
// so a scoped admin cannot widen their own reach or escalate anyone.
[Authorize(Policy = "RequireAdminFullOrScoped")]
public class UserPermissionsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ITenantContextService _tenants;
    private readonly IAuditService _audit;

    public UserPermissionsController(AppDbContext db, ITenantContextService tenants, IAuditService audit)
    {
        _db = db;
        _tenants = tenants;
        _audit = audit;
    }

    /// <summary>List permission grants. Super admins see all; scoped admins see their tenants' only.</summary>
    [HttpGet]
    public async Task<ActionResult<List<PermissionGrantResponse>>> List([FromQuery] int? tenantId = null)
    {
        var query = _db.PermissionGrants.AsNoTracking();

        if (_tenants.IsSuperAdmin(User))
        {
            if (tenantId.HasValue)
            {
                query = query.Where(g => g.TenantId == tenantId.Value);
            }
        }
        else
        {
            var tenantIds = await _tenants.GetAuthorizedTenantIdsAsync(User);
            query = query.Where(g => !g.TenantId.HasValue || tenantIds.Contains(g.TenantId.Value));
            if (tenantId.HasValue)
            {
                query = query.Where(g => g.TenantId == tenantId.Value);
            }
        }

        var grants = await query.OrderBy(g => g.ExternalPrincipalId).ToListAsync();
        return Ok(grants.Select(ToResponse).ToList());
    }

    /// <summary>
    /// Why a principal can reach the tenants it can reach, rule by rule.
    ///
    /// Super-admin only despite the controller's wider policy — the two [Authorize]
    /// attributes are ANDed — because it reports on a named third party, and a scoped admin
    /// has no business enumerating another subscription's grants.
    /// </summary>
    [HttpGet("tenant-access")]
    [Authorize(Policy = "RequireAdminFull")]
    public async Task<ActionResult<TenantAccessExplanation>> ExplainTenantAccess([FromQuery] string principal)
    {
        if (string.IsNullOrWhiteSpace(principal))
            return BadRequest(new { error = "A principal (email or Entra object id) is required." });

        return Ok(await _tenants.ExplainTenantAccessAsync(principal));
    }

    /// <summary>Grant a permission set to a user.</summary>
    [HttpPost]
    public async Task<ActionResult<PermissionGrantResponse>> Create(CreatePermissionGrantRequest request)
    {
        var perms = Permissions.ParseAssignablePermissionCsv(request.Permissions);
        if (perms.Length == 0)
        {
            return BadRequest(new
            {
                error = "At least one valid assignable permission is required (Schedule.Read, Schedule.Write, Directory.Read, Directory.Write, CodeCall.Write)."
            });
        }

        if (string.IsNullOrWhiteSpace(request.ExternalPrincipalId))
        {
            return BadRequest(new { error = "A principal identifier (Entra object id or email) is required." });
        }

        if (ValidateGrantScope(request.TenantId, request.AllTenants) is { } scopeError)
            return scopeError;

        if (request.TenantId.HasValue && !await CanManageTenantAsync(request.TenantId.Value))
            return Forbid();
        if (!request.TenantId.HasValue && !_tenants.IsSuperAdmin(User))
            return Forbid();

        var grant = new PermissionGrant
        {
            TenantId = request.TenantId,
            PrincipalType = string.IsNullOrWhiteSpace(request.PrincipalType) ? "external" : request.PrincipalType!,
            ExternalPrincipalId = request.ExternalPrincipalId.Trim(),
            Permissions = string.Join(",", perms),
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };

        _db.PermissionGrants.Add(grant);
        await _db.SaveChangesAsync();

        AuditGrant("Granted", grant,
            $"Principal={grant.ExternalPrincipalId};Permissions={grant.Permissions}");

        return Ok(ToResponse(grant));
    }

    /// <summary>
    /// Grant the same permission set to many people at once.
    ///
    /// Takes employee ids, never principal strings. Uploaded staff have never signed in, so they
    /// do not appear in the sign-in identity list the single-record form is driven from — and
    /// keying off the directory means the server decides which address each grant lands on
    /// rather than accepting whatever a client sends.
    ///
    /// Replaces rather than accumulates: after this call each person holds exactly the selected
    /// permissions for the chosen subscription, so running it twice changes nothing the second
    /// time. That matters here because there is no unique index on the grant table, and the
    /// single-record path always inserts — so duplicates already exist in the wild and are
    /// collapsed on the way through.
    /// </summary>
    [HttpPost("bulk")]
    public async Task<ActionResult<BulkGrantResponse>> BulkGrant(BulkGrantRequest request)
    {
        var perms = Permissions.ParseAssignablePermissionCsv(request.Permissions);
        if (perms.Length == 0)
        {
            return BadRequest(new
            {
                error = "At least one valid assignable permission is required (Schedule.Read, Schedule.Write, Directory.Read, Directory.Write, CodeCall.Write)."
            });
        }

        if (request.EmployeeIds is not { Count: > 0 })
            return BadRequest(new { error = "Select at least one person." });
        if (request.EmployeeIds.Count > BulkLimits.MaxBatch)
            return BadRequest(new { error = $"A bulk grant takes at most {BulkLimits.MaxBatch} people at a time." });

        if (ValidateGrantScope(request.TenantId, request.AllTenants) is { } bulkScopeError)
            return bulkScopeError;

        // The same two guards as the single-record path, applied once for the whole request:
        // a scoped admin may only grant inside their own subscriptions, and only a super admin
        // may grant system-wide — which reaches every subscription there is.
        if (request.TenantId.HasValue && !await CanManageTenantAsync(request.TenantId.Value))
            return Forbid();
        if (!request.TenantId.HasValue && !_tenants.IsSuperAdmin(User))
            return Forbid();

        var isSuperAdmin = _tenants.IsSuperAdmin(User);
        var authorizedTenants = isSuperAdmin ? null : await _tenants.GetAuthorizedTenantIdsAsync(User);

        var ids = request.EmployeeIds.Distinct().ToList();
        var employees = await _db.Employees.Where(e => ids.Contains(e.Id)).ToListAsync();
        var byId = employees.ToDictionary(e => e.Id);

        var csv = string.Join(",", perms);
        var principalType = string.IsNullOrWhiteSpace(request.PrincipalType) ? "external" : request.PrincipalType!;
        var batchId = Guid.NewGuid();
        var results = new List<BulkGrantItemResult>(ids.Count);

        foreach (var id in ids)
        {
            if (!byId.TryGetValue(id, out var employee)
                || (!isSuperAdmin && (!employee.TenantId.HasValue || !authorizedTenants!.Contains(employee.TenantId.Value))))
            {
                // One bucket, and no address echoed back: another subscription's record must not
                // become discoverable by guessing ids.
                results.Add(new BulkGrantItemResult
                {
                    EmployeeId = id,
                    Outcome = BulkOutcomes.NotFound,
                    Message = "No such person, or they belong to a subscription you do not administer.",
                });
                continue;
            }

            if (string.Equals(employee.ContactType, ContactType.Department, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new BulkGrantItemResult
                {
                    EmployeeId = id,
                    Outcome = BulkOutcomes.SkippedNotAPerson,
                    Message = $"{employee.DisplayName} is a unit or service line, not someone who signs in.",
                });
                continue;
            }

            if (string.IsNullOrWhiteSpace(employee.Email))
            {
                // Deliberately no fallback to AzureAdObjectId: the importer synthesises
                // "csv-import-{guid}" for rows that arrive without one, and no token will ever
                // present that — the grant would look successful and confer nothing.
                results.Add(new BulkGrantItemResult
                {
                    EmployeeId = id,
                    Outcome = BulkOutcomes.SkippedNoEmail,
                    Message = "A permission grant is keyed to an email address. Add one first.",
                });
                continue;
            }

            // Granting into a subscription this person does not belong to is almost always a
            // mis-click from a list, and is how one action hands cross-tenant access to hundreds
            // of people. The single-record form remains available for the deliberate case.
            if (request.TenantId.HasValue && employee.TenantId != request.TenantId)
            {
                results.Add(new BulkGrantItemResult
                {
                    EmployeeId = id,
                    Email = employee.Email,
                    Outcome = BulkOutcomes.SkippedWrongTenant,
                    Message = "Belongs to a different subscription than the one being granted.",
                });
                continue;
            }

            var (grant, replaced) = await UpsertGrantAsync(request.TenantId, employee.Email!.Trim(), principalType, csv);

            results.Add(new BulkGrantItemResult
            {
                EmployeeId = id,
                Email = employee.Email,
                Outcome = BulkOutcomes.Succeeded,
                GrantId = grant.Id,
                Message = replaced ? "Existing permissions replaced." : null,
            });

            AuditGrant("Granted", grant,
                $"Bulk=true;BatchId={batchId};Principal={grant.ExternalPrincipalId};Permissions={grant.Permissions}");
        }

        await _db.SaveChangesAsync();

        return Ok(new BulkGrantResponse
        {
            TenantId = request.TenantId,
            Permissions = perms,
            BatchId = batchId,
            Requested = ids.Count,
            Granted = results.Count(r => r.Outcome == BulkOutcomes.Succeeded && r.Message is null),
            Replaced = results.Count(r => r.Outcome == BulkOutcomes.Succeeded && r.Message is not null),
            NotFound = results.Count(r => r.Outcome == BulkOutcomes.NotFound),
            Skipped = results.Count(r => r.Outcome is BulkOutcomes.SkippedNoEmail
                or BulkOutcomes.SkippedNotAPerson or BulkOutcomes.SkippedWrongTenant),
            Results = results,
        });
    }

    /// <summary>
    /// One grant per (subscription, principal), rewritten in place.
    ///
    /// Keyed deliberately without PrincipalType: TenantClaimsMiddleware ignores it when matching,
    /// so a stale "local" row left beside a new "external" one would keep conferring exactly what
    /// this call was meant to replace.
    ///
    /// Revoked rows are matched too. Skipping them would create a second row for someone whose
    /// access was previously withdrawn, and the Restore button in the admin UI could then bring
    /// the old permissions back.
    /// </summary>
    private async Task<(PermissionGrant Grant, bool Replaced)> UpsertGrantAsync(
        int? tenantId, string principal, string principalType, string permissionCsv)
    {
        var key = principal.ToLowerInvariant();

        // Branched explicitly: comparing a column to a captured nullable that happens to be null
        // matches no rows at all rather than the rows that are themselves null.
        var scoped = tenantId.HasValue
            ? _db.PermissionGrants.Where(g => g.TenantId == tenantId.Value)
            : _db.PermissionGrants.Where(g => g.TenantId == null);

        var existing = await scoped
            .Where(g => g.ExternalPrincipalId.ToLower() == key)
            .OrderBy(g => g.Id)
            .ToListAsync();

        if (existing.Count == 0)
        {
            var created = new PermissionGrant
            {
                TenantId = tenantId,
                PrincipalType = principalType,
                ExternalPrincipalId = principal,
                Permissions = permissionCsv,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            };
            _db.PermissionGrants.Add(created);
            await _db.SaveChangesAsync();
            return (created, false);
        }

        var keep = existing[0];
        keep.PrincipalType = principalType;
        keep.ExternalPrincipalId = principal;
        keep.Permissions = permissionCsv;
        keep.IsActive = true;
        keep.UpdatedAt = DateTime.UtcNow;

        // Duplicates the always-insert single-record path will have left behind. Switched off
        // rather than deleted, so the history of what was held survives.
        foreach (var duplicate in existing.Skip(1).Where(g => g.IsActive))
        {
            duplicate.IsActive = false;
            duplicate.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
        return (keep, true);
    }

    /// <summary>Update a grant (change permissions, revoke, re-activate).</summary>
    [HttpPut("{id}")]
    public async Task<ActionResult<PermissionGrantResponse>> Update(int id, UpdatePermissionGrantRequest request)
    {
        var grant = await _db.PermissionGrants.FindAsync(id);
        if (grant == null) return NotFound();

        if (grant.TenantId.HasValue && !await CanManageTenantAsync(grant.TenantId.Value))
            return Forbid();
        if (!grant.TenantId.HasValue && !_tenants.IsSuperAdmin(User))
            return Forbid();

        if (request.Permissions != null)
        {
            var perms = Permissions.ParseAssignablePermissionCsv(request.Permissions);
            if (perms.Length == 0)
            {
                return BadRequest(new { error = "At least one valid assignable permission is required." });
            }
            grant.Permissions = string.Join(",", perms);
        }

        // Captured before the write: this call can move a grant to a different principal, and
        // an audit entry that only names the new one hides exactly that.
        var wasPrincipal = grant.ExternalPrincipalId;
        var wasActive = grant.IsActive;

        if (request.IsActive.HasValue) grant.IsActive = request.IsActive.Value;
        if (!string.IsNullOrWhiteSpace(request.ExternalPrincipalId)) grant.ExternalPrincipalId = request.ExternalPrincipalId.Trim();
        if (!string.IsNullOrWhiteSpace(request.PrincipalType)) grant.PrincipalType = request.PrincipalType!;

        grant.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // "Revoked" when this call switched the grant off, so the trail reads as the access
        // decision it was rather than a generic edit.
        var action = wasActive && !grant.IsActive ? "Revoked" : "Updated";
        AuditGrant(action, grant,
            $"Principal={grant.ExternalPrincipalId};WasPrincipal={wasPrincipal};"
            + $"Permissions={grant.Permissions};IsActive={grant.IsActive}");

        return Ok(ToResponse(grant));
    }

    /// <summary>Revoke (hard-delete) a grant.</summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var grant = await _db.PermissionGrants.FindAsync(id);
        if (grant == null) return NotFound();
        if (grant.TenantId.HasValue && !await CanManageTenantAsync(grant.TenantId.Value))
            return Forbid();
        if (!grant.TenantId.HasValue && !_tenants.IsSuperAdmin(User))
            return Forbid();

        // Audited before the row goes, because afterwards there is nothing left to describe it
        // — and this is the one call that can erase a revoked grant, which is the evidence that
        // an address once held these permissions.
        AuditGrant("Deleted", grant,
            $"Principal={grant.ExternalPrincipalId};Permissions={grant.Permissions};WasActive={grant.IsActive}");

        _db.PermissionGrants.Remove(grant);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>
    /// Records a change to a grant.
    ///
    /// Only <see cref="Create"/> wrote one of these. <see cref="Update"/> can rewrite the
    /// permissions, flip IsActive, and even rewrite ExternalPrincipalId — moving a grant to a
    /// different person — while <see cref="Delete"/> destroys the row outright. Both did so
    /// without leaving any record of who did it or what it had been. A grant is an
    /// authorization decision; every change to one belongs in the trail.
    ///
    /// PrincipalId and IpAddress are set here because <see cref="AuditLog"/> documents both as
    /// required to attribute an action to a Google or local principal, whose identifiers are
    /// not GUIDs and so land in UserId as Guid.Empty. The original call site set neither.
    /// </summary>
    private void AuditGrant(string action, PermissionGrant grant, string details)
    {
        var objectId = PrincipalClaims.GetObjectId(User);

        _audit.Enqueue(new AuditLog
        {
            UserId = Guid.TryParse(objectId, out var parsed) ? parsed : Guid.Empty,
            PrincipalId = objectId,
            UserName = User.Identity?.Name ?? "",
            Action = action,
            ResourceType = "PermissionGrant",
            ResourceId = grant.Id.ToString(),
            Details = details,
            TenantId = grant.TenantId,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            Timestamp = DateTime.UtcNow,
        });
    }

    /// <summary>
    /// Rejects a grant whose scope was never stated.
    ///
    /// A null TenantId means "every active tenant" — <c>GetAuthorizedTenantIdsAsync</c>
    /// expands it to all of them, which is deliberately what a super admin picks for
    /// "All tenants". The hazard is that it is also what an *omitted* field looks like, so a
    /// caller that simply forgot to say which subscription silently produced the widest grant
    /// there is. That is how a tenant-scoped administrator ended up able to read every
    /// subscription's directory.
    ///
    /// System-wide therefore has to be asked for. The IsSuperAdmin check on that branch is
    /// unchanged and still applies; this only ensures the branch was chosen on purpose.
    /// </summary>
    private ActionResult? ValidateGrantScope(int? tenantId, bool? allTenants)
    {
        if (tenantId.HasValue && allTenants == true)
        {
            return BadRequest(new
            {
                error = "Specify either a subscription or allTenants, not both.",
            });
        }

        if (!tenantId.HasValue && allTenants != true)
        {
            return BadRequest(new
            {
                error = "A grant needs a scope: set tenantId to one subscription, "
                      + "or set allTenants=true to grant across every subscription.",
            });
        }

        return null;
    }

    private async Task<bool> CanManageTenantAsync(int tenantId)
    {
        if (_tenants.IsSuperAdmin(User)) return true;
        var ids = await _tenants.GetAuthorizedTenantIdsAsync(User);
        return ids.Contains(tenantId);
    }

    private static PermissionGrantResponse ToResponse(PermissionGrant g) => new()
    {
        Id = g.Id,
        TenantId = g.TenantId,
        PrincipalType = g.PrincipalType,
        ExternalPrincipalId = g.ExternalPrincipalId,
        Permissions = Permissions.ParsePermissionCsv(g.Permissions),
        IsActive = g.IsActive,
        CreatedAt = g.CreatedAt,
        UpdatedAt = g.UpdatedAt,
    };
}

// AllTenants is the explicit opt-in for a system-wide grant. Absent scope is rejected
// rather than treated as "every tenant" — see ValidateGrantScope.
public record CreatePermissionGrantRequest(int? TenantId, string? PrincipalType, string ExternalPrincipalId, string Permissions, bool? AllTenants = null);
public record UpdatePermissionGrantRequest(string? ExternalPrincipalId, string? PrincipalType, string? Permissions, bool? IsActive);

public class PermissionGrantResponse
{
    public int Id { get; set; }
    public int? TenantId { get; set; }
    public string PrincipalType { get; set; } = "external";
    public string ExternalPrincipalId { get; set; } = string.Empty;
    public string[] Permissions { get; set; } = [];
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}