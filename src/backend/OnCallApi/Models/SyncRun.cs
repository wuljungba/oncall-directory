namespace OnCallApi.Models;

/// <summary>
/// What one directory sync cycle actually did, kept rather than logged.
///
/// The sync ran every fifteen minutes for months against a bug that read one page of a
/// paginated directory and deactivated everybody on the pages it never read. Nothing recorded
/// a single cycle, so the only evidence was a log line nobody reads and staff quietly
/// disappearing from the on-call directory.
///
/// <see cref="PagesRead"/> is that bug's fingerprint: a large directory reporting one page is
/// the thing to look for. Operational metrics, not audit records — deactivations write
/// <see cref="AuditLog"/> rows separately and keep their own retention.
/// </summary>
public class SyncRun
{
    public long Id { get; set; }

    /// <summary>Null is the home directory, as everywhere else here.</summary>
    public int? TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    [Required(AllowEmptyStrings = false)]
    [MaxLength(40)]
    public string Source { get; set; } = SyncSources.AdUsers;

    [MaxLength(20)]
    public string Mode { get; set; } = SyncModes.Full;

    [MaxLength(20)]
    public string Outcome { get; set; } = SyncOutcomes.Succeeded;

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    /// <summary>How much of the directory was read. One page for 500 staff is a fault.</summary>
    public int PagesRead { get; set; }

    public int Fetched { get; set; }
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }

    public int Deactivated { get; set; }

    /// <summary>Of those, the ones Graph named outright rather than ones inferred from absence.</summary>
    public int DeactivatedByRemoval { get; set; }

    /// <summary>Of those, the ones whose Entra account was reported disabled.</summary>
    public int DeactivatedByDisabledAccount { get; set; }

    /// <summary>Deactivations the safety valve declined to apply. Never silently zero.</summary>
    public int DeactivationsRefused { get; set; }

    /// <summary>Whether the cursor advanced. False after a partial read or a refusal.</summary>
    public bool DeltaLinkStored { get; set; }

    /// <summary>Whether Graph rejected the stored cursor and the directory was re-enumerated.</summary>
    public bool TokenWasRejected { get; set; }

    /// <summary>"Timer", or the principal who pressed the button.</summary>
    [MaxLength(200)]
    public string? TriggeredBy { get; set; }

    [MaxLength(2000)]
    public string? FailureDetail { get; set; }

    /// <summary>Skip reasons and anything else worth a person's attention, truncated.</summary>
    [MaxLength(4000)]
    public string? Notes { get; set; }
}

public static class SyncSources
{
    public const string AdUsers = "AdUsers";
    public const string AdGroups = "AdGroups";
}

public static class SyncModes
{
    /// <summary>Enumerated from nothing — the only mode in which absence means departure.</summary>
    public const string Full = "Full";
    public const string Incremental = "Incremental";
}

public static class SyncOutcomes
{
    public const string Succeeded = "Succeeded";

    /// <summary>Read part of the directory. Upserts applied; nobody deactivated by absence.</summary>
    public const string Partial = "Partial";

    public const string Failed = "Failed";

    /// <summary>Completed, then declined to apply a deactivation batch that looked like a fault.</summary>
    public const string Refused = "Refused";
}
