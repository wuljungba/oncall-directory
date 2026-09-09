using System.ComponentModel.DataAnnotations;

namespace OnCallApi.Models;

/// <summary>
/// The shapes bulk admin actions speak in.
///
/// Every bulk action reports per record rather than succeeding or failing as a whole. That is
/// not defensive padding: an employee who has ever held a shift, taken time off, sat in a phone
/// tree or been paged cannot be hard-deleted at all (those foreign keys are Restrict), so a
/// batch that is part-blocked is the ordinary outcome, not the error case. An endpoint that
/// answered only "ok" or "failed" would be wrong most of the time it was used.
/// </summary>
public static class BulkLimits
{
    /// <summary>
    /// Records per request. Bounded by the ~2100-parameter ceiling on the SQL Server IN-lists
    /// the reference pre-flight builds, and by keeping one request's audit rows well inside the
    /// audit channel's capacity.
    /// </summary>
    public const int MaxBatch = 500;
}

/// <summary>What happened to one record in a bulk action.</summary>
public static class BulkOutcomes
{
    public const string Succeeded = "succeeded";

    /// <summary>
    /// No such record, or it belongs to a tenant the caller cannot administer — deliberately
    /// one bucket. The single-record path answers 404 rather than 403 for another tenant's
    /// employee so that existence is not confirmed; bulk must not undo that by distinguishing
    /// the two, and must not echo back a name or address for these.
    /// </summary>
    public const string NotFound = "notFound";

    /// <summary>Referenced by schedule, time-off, phone-tree or escalation history.</summary>
    public const string BlockedByHistory = "blockedByHistory";

    public const string AlreadyInState = "alreadyInState";
    public const string SkippedSelf = "skippedSelf";
    public const string SkippedNoEmail = "skippedNoEmail";
    public const string SkippedNotAPerson = "skippedNotAPerson";
    public const string SkippedWrongTenant = "skippedWrongTenant";
    public const string Failed = "failed";
}

public record BulkEmployeeActionRequest(
    [Required][MinLength(1)] List<Guid> EmployeeIds,
    [MaxLength(500)] string? Reason = null,
    /// <summary>
    /// Set once the caller has been shown that the batch contains someone holding admin rights
    /// that this action will NOT remove. Required only for permanent deletion.
    /// </summary>
    bool AcknowledgePrivileged = false);

public record BulkItemResult
{
    public Guid EmployeeId { get; init; }

    /// <summary>
    /// Null for <see cref="BulkOutcomes.NotFound"/>. Echoing a name or address back for a
    /// record the caller may not administer would turn bulk into a cross-tenant lookup for
    /// anything the caller can guess an id for.
    /// </summary>
    public string? DisplayName { get; init; }

    public string? Email { get; init; }
    public string Outcome { get; init; } = BulkOutcomes.Failed;
    public string? Message { get; init; }
    public int GrantsRevoked { get; init; }

    /// <summary>
    /// Local sign-in accounts switched off. Revoking grants alone leaves a working credential:
    /// LocalAccount has its own IsActive and no foreign key to the employee, so it survives the
    /// delete and still authenticates.
    /// </summary>
    public int SignInsDisabled { get; init; }

    /// <summary>
    /// This person holds admin rights from a source a grant revocation cannot touch — a
    /// TenantAdmin row or the configured super-admin list. Deactivating their directory record
    /// does not remove that, and saying otherwise would be false.
    /// </summary>
    public bool IsPrivilegedPrincipal { get; init; }
}

public record BulkEmployeeActionResponse
{
    public string Action { get; init; } = "";

    /// <summary>
    /// Shared by every audit row this request writes. One admin deactivating two hundred people
    /// is a single event, and per-record rows alone cannot express that.
    /// </summary>
    public Guid BatchId { get; init; }

    public int Requested { get; init; }
    public int Succeeded { get; init; }
    public int Blocked { get; init; }
    public int Skipped { get; init; }
    public int NotFound { get; init; }
    public int GrantsRevoked { get; init; }
    public int SignInsDisabled { get; init; }

    /// <summary>
    /// System-wide grants found but deliberately left alone because the caller is not a super
    /// admin. Reported so the response never claims access was removed when it was not.
    /// </summary>
    public int SystemWideGrantsLeft { get; init; }

    public int PrivilegedPrincipals { get; init; }
    public List<BulkItemResult> Results { get; init; } = [];
}

/// <summary>
/// <paramref name="AllTenants"/> is the explicit opt-in for a system-wide grant. A request
/// that names no TenantId and does not set it is rejected: an absent scope must never
/// resolve to the widest one.
/// </summary>
public record BulkGrantRequest(
    int? TenantId,
    [Required][MinLength(1)] List<Guid> EmployeeIds,
    [Required] string Permissions,
    string? PrincipalType = null,
    bool? AllTenants = null);

public record BulkGrantItemResult
{
    public Guid EmployeeId { get; init; }
    public string? Email { get; init; }
    public string Outcome { get; init; } = BulkOutcomes.Failed;
    public string? Message { get; init; }
    public int? GrantId { get; init; }
}

public record BulkGrantResponse
{
    public int? TenantId { get; init; }
    public string[] Permissions { get; init; } = [];
    public Guid BatchId { get; init; }
    public int Requested { get; init; }
    public int Granted { get; init; }
    public int Replaced { get; init; }
    public int Skipped { get; init; }
    public int NotFound { get; init; }
    public List<BulkGrantItemResult> Results { get; init; } = [];
}
